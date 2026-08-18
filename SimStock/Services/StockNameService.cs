using System.Collections.Concurrent;
using System.Text.Json;
using TdxProtocol;
using TdxProtocol.Commands;

namespace SimStock;

/// <summary>
/// 股票名称查询服务。从 TDX 获取全量股票列表并缓存 code→name 映射。
/// 参考 Python mootdx/quotes.py stocks() / stock_all()。
/// </summary>
public class StockNameService
{
    private const string CacheFile = "stocknames.json";
    private const int CacheHours = 24 * 5;
    private const int BatchSize = 1000;
    private const int CacheVersion = 2;
    private const int MaxFetchRetries = 3;
    private const int FetchRetryDelayMs = 300;

    private readonly ConcurrentDictionary<string, string> _names = new();
    private readonly ConcurrentDictionary<string, string> _nameToCode = new(); // 中文名 → 标准化代码
    private readonly string _cachePath;
    private readonly ConnectionManager _connMgr;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private DateTime _lastFetch;
    private bool _initialized;

    public StockNameService(string appDir, ConnectionManager connMgr)
    {
        _cachePath = Path.Combine(appDir, CacheFile);
        _connMgr = connMgr;
    }

    /// <summary>
    /// 根据标准化代码获取股票名称。未命中时返回代码本身。
    /// </summary>
    public async Task<string> GetNameAsync(string normalizedCode)
    {
        await EnsureInitializedAsync();

        return _names.TryGetValue(normalizedCode, out var name) ? name : normalizedCode;
    }

    /// <summary>
    /// 批量获取股票名称。
    /// </summary>
    public async Task<Dictionary<string, string>> GetNamesAsync(IEnumerable<string> normalizedCodes)
    {
        await EnsureInitializedAsync();

        var result = new Dictionary<string, string>();
        foreach (var code in normalizedCodes)
        {
            result[code] = _names.TryGetValue(code, out var name) ? name : code;
        }

        return result;
    }

    /// <summary>
    /// 根据中文名称搜索股票。精确匹配返回单个结果，模糊匹配返回最多 5 个候选。
    /// </summary>
    /// <returns>(exactMatch: normalizedCode|null, candidates: [(code, name), ...])</returns>
    public async Task<(string? exactMatch, List<(string code, string name)> candidates)> SearchByNameAsync(string keyword)
    {
        await EnsureInitializedAsync();
        keyword = keyword.Trim();

        // 精确匹配
        if (_nameToCode.TryGetValue(keyword, out var exactCode))
            return (exactCode, []);

        // 模糊匹配：名称包含关键词，最多返回5个
        var candidates = _nameToCode
            .Where(kv => kv.Key.Contains(keyword))
            .Take(5)
            .Select(kv => (kv.Value, kv.Key))
            .ToList();

        return (null, candidates);
    }

    private void AddToIndex(string code, string name)
    {
        _names[code] = name;

        // 同名冲突时A股优先：债券/基金等不应覆盖A股
        if (_nameToCode.TryGetValue(name, out var existingCode))
        {
            var existingIsAStock = IsAStock(existingCode);
            var newIsAStock = IsAStock(code);
            if (existingIsAStock && !newIsAStock)
                return; // 保留已有的A股
        }

        _nameToCode[name] = code;
    }

    private static bool IsAStock(string normalizedCode)
    {
        var parsed = StockCodeParser.ParseNormalized(normalizedCode);
        if (!parsed.HasValue) return false;
        var (market, code) = parsed.Value;
        var prefix = code.Length >= 2 ? code[..2] : code;
        return market switch
        {
            TdxProtocol.TdxConstants.MarketSH => prefix is "60" or "68",
            TdxProtocol.TdxConstants.MarketSZ => prefix is "00" or "30",
            TdxProtocol.TdxConstants.MarketBJ => prefix is "83" or "87" or "43" or "92",
            _ => false
        };
    }

    /// <summary>
    /// 判断缓存是否已加载（不触发网络请求）。
    /// </summary>
    public bool IsLoaded => _initialized;

    private async Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _initializeLock.WaitAsync();
        try
        {
            // 其他并发请求可能已在等待期间完成初始化。
            if (_initialized)
            {
                return;
            }

            // 1. 尝试从本地文件缓存加载
            if (TryLoadFromFile())
            {
                _initialized = true;
                return;
            }

            // 2. 仅在沪深列表都完整时才视为初始化成功。
            _initialized = await FetchAllAsync();
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private bool TryLoadFromFile()
    {
        if (!File.Exists(_cachePath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(_cachePath);
            var cache = JsonSerializer.Deserialize<StockNameCache>(json);
            if (cache is null
                || cache.Version != CacheVersion
                || (DateTime.Now - cache.FetchedAt).TotalHours >= CacheHours
                || !IsCompleteCache(cache))
            {
                return false;
            }

            foreach (var entry in cache.Entries)
            {
                AddToIndex(entry.Code, entry.Name);
            }

            _lastFetch = cache.FetchedAt;
            return true;
        }
        catch (Exception ex)
        {
            LogWarning($"读取股票名称缓存失败: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> FetchAllAsync()
    {
        var client = await _connMgr.EnsureConnectedAsync();
        if (client is null)
        {
            // 无网络时尝试加载过期缓存
            return TryLoadStaleCache();
        }

        var entries = new List<StockNameEntry>(10000);
        var marketCounts = new Dictionary<byte, int>();

        foreach (var market in new byte[] { TdxConstants.MarketSZ, TdxConstants.MarketSH })
        {
            try
            {
                // 先获取总数
                int total = await SendFetchRequestWithRetryAsync(activeClient =>
                {
                    var countCmd = new GetSecurityCountCmd();
                    countCmd.SetParams(market);
                    return countCmd.ParseResponse(activeClient.SendPacket(countCmd.BuildRequest()));
                });
                if (total <= 0 || total > ushort.MaxValue)
                {
                    throw new InvalidDataException($"服务器返回了无效证券数量: {total}");
                }

                var marketEntries = new List<StockNameEntry>(total);
                var marketCodes = new HashSet<string>(StringComparer.Ordinal);

                for (var start = 0; start < total; start += BatchSize)
                {
                    var stocks = await SendFetchRequestWithRetryAsync(activeClient =>
                    {
                        var cmd = new GetSecurityListCmd();
                        cmd.SetParams(market, checked((ushort)start));
                        return cmd.ParseResponse(activeClient.SendPacket(cmd.BuildRequest()));
                    });
                    var expectedCount = Math.Min(BatchSize, total - start);
                    if (stocks.Length != expectedCount)
                    {
                        throw new InvalidDataException(
                            $"证券列表分页不完整: start={start}, expected={expectedCount}, actual={stocks.Length}");
                    }

                    foreach (var s in stocks)
                    {
                        var normalized = StockCodeParser.NormalizeCode(market, s.Code);
                        if (!marketCodes.Add(normalized))
                        {
                            throw new InvalidDataException($"证券列表包含重复代码: {normalized}");
                        }
                        marketEntries.Add(new StockNameEntry(normalized, s.Name));
                    }
                }

                if (marketEntries.Count != total || marketCodes.Count != total)
                {
                    throw new InvalidDataException(
                        $"证券列表总数不一致: expected={total}, received={marketEntries.Count}, unique={marketCodes.Count}");
                }

                entries.AddRange(marketEntries);
                marketCounts[market] = total;
            }
            catch (Exception ex)
            {
                // 任一市场不完整时，不得用部分数据覆盖现有缓存。
                LogWarning($"拉取{GetMarketName(market)}证券列表失败，保留现有缓存: {ex.Message}");
                return TryLoadStaleCache();
            }
        }

        if (marketCounts.Count == 2)
        {
            foreach (var entry in entries)
            {
                AddToIndex(entry.Code, entry.Name);
            }

            _lastFetch = DateTime.Now;
            SaveToFile(entries, marketCounts);
            return true;
        }

        return TryLoadStaleCache();
    }

    private bool TryLoadStaleCache()
    {
        if (!File.Exists(_cachePath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(_cachePath);
            var cache = JsonSerializer.Deserialize<StockNameCache>(json);
            if (cache is null || cache.Version != CacheVersion || !IsCompleteCache(cache))
            {
                return false;
            }

            foreach (var entry in cache.Entries)
            {
                AddToIndex(entry.Code, entry.Name);
            }

            _lastFetch = cache.FetchedAt;
            return true;
        }
        catch (Exception ex)
        {
            LogWarning($"读取过期股票名称缓存失败: {ex.Message}");
            return false;
        }
    }

    private static bool IsCompleteCache(StockNameCache cache)
    {
        if (cache.MarketCounts is null
            || !cache.MarketCounts.TryGetValue(TdxConstants.MarketSZ, out var szCount)
            || !cache.MarketCounts.TryGetValue(TdxConstants.MarketSH, out var shCount)
            || szCount <= 0 || shCount <= 0)
        {
            return false;
        }

        var uniqueCodes = cache.Entries.Select(x => x.Code).ToHashSet(StringComparer.Ordinal);
        var cachedSzCount = uniqueCodes.Count(x => x.StartsWith("sz", StringComparison.Ordinal));
        var cachedShCount = uniqueCodes.Count(x => x.StartsWith("sh", StringComparison.Ordinal));
        return cachedSzCount == szCount && cachedShCount == shCount;
    }

    /// <summary>
    /// 单个 TDX 拉取请求的静默重试。首次请求失败后最多再试 3 次；
    /// 若失败使连接断开，每次重试均会重新通过 ConnectionManager 获取连接。
    /// </summary>
    private async Task<T> SendFetchRequestWithRetryAsync<T>(Func<TdxClient, T> request)
    {
        Exception? lastException = null;

        for (var retry = 0; retry <= MaxFetchRetries; retry++)
        {
            try
            {
                var activeClient = await _connMgr.EnsureConnectedAsync();
                if (activeClient is null)
                {
                    throw new IOException("无法连接 TDX 行情服务器");
                }

                return request(activeClient);
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (retry == MaxFetchRetries)
                {
                    break;
                }

                // 中间重试不记录日志，避免短暂网络抖动污染插件日志。
                await Task.Delay(FetchRetryDelayMs);
            }
        }

        throw new InvalidOperationException(
            $"TDX 请求连续失败，已重试 {MaxFetchRetries} 次", lastException);
    }

    private void SaveToFile(List<StockNameEntry> entries, Dictionary<byte, int> marketCounts)
    {
        string? tempPath = null;
        try
        {
            var cache = new StockNameCache(DateTime.Now, entries, CacheVersion, marketCounts);
            tempPath = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(cache));
            File.Move(tempPath, _cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            LogWarning($"保存股票名称缓存失败: {ex.Message}");
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static string GetMarketName(byte market) => market == TdxConstants.MarketSZ ? "深市" : "沪市";

    private static void LogWarning(string message)
    {
        try { Entry.Api?.Logger.Warn("股票名称", message); } catch { }
    }
}

internal record StockNameEntry(string Code, string Name);

internal record StockNameCache(
    DateTime FetchedAt,
    List<StockNameEntry> Entries,
    int Version = 0,
    Dictionary<byte, int>? MarketCounts = null);
