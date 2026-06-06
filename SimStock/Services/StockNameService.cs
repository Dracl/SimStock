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
    private const int CacheHours = 24;
    private const int BatchSize = 1000;

    private readonly ConcurrentDictionary<string, string> _names = new();
    private readonly ConcurrentDictionary<string, string> _nameToCode = new(); // 中文名 → 标准化代码
    private readonly string _cachePath;
    private readonly ConnectionManager _connMgr;
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
        _nameToCode[name] = code;
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

        // 1. 尝试从本地文件缓存加载
        if (TryLoadFromFile())
        {
            return;
        }

        // 2. 从 TDX 服务端拉取
        await FetchAllAsync();
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
            if (cache is null || (DateTime.Now - cache.FetchedAt).TotalHours >= CacheHours)
            {
                return false;
            }

            foreach (var entry in cache.Entries)
            {
                AddToIndex(entry.Code, entry.Name);
            }

            _lastFetch = cache.FetchedAt;
            _initialized = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task FetchAllAsync()
    {
        var client = await _connMgr.EnsureConnectedAsync();
        if (client is null)
        {
            // 无网络时尝试加载过期缓存
            TryLoadStaleCache();
            _initialized = true;
            return;
        }

        var entries = new List<StockNameEntry>(10000);

        foreach (var market in new byte[] { TdxConstants.MarketSZ, TdxConstants.MarketSH })
        {
            try
            {
                // 先获取总数
                var countCmd = new GetSecurityCountCmd();
                countCmd.SetParams(market);
                int total = countCmd.ParseResponse(client.SendPacket(countCmd.BuildRequest()));

                for (ushort start = 0; start < total; start += BatchSize)
                {
                    var cmd = new GetSecurityListCmd();
                    cmd.SetParams(market, start);
                    var stocks = cmd.ParseResponse(client.SendPacket(cmd.BuildRequest()));

                    foreach (var s in stocks)
                    {
                        var normalized = StockCodeParser.NormalizeCode(market, s.Code);
                        entries.Add(new StockNameEntry(normalized, s.Name));
                    }
                }
            }
            catch
            {
                // 某个市场拉取失败，继续尝试下一个
            }
        }

        if (entries.Count > 0)
        {
            foreach (var entry in entries)
            {
                AddToIndex(entry.Code, entry.Name);
            }

            _lastFetch = DateTime.Now;
            SaveToFile(entries);
        }
        else
        {
            // 拉取完全失败，尝试过期缓存
            TryLoadStaleCache();
        }

        _initialized = true;
    }

    private void TryLoadStaleCache()
    {
        if (!File.Exists(_cachePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_cachePath);
            var cache = JsonSerializer.Deserialize<StockNameCache>(json);
            if (cache is null)
            {
                return;
            }

            foreach (var entry in cache.Entries)
            {
                _names.TryAdd(entry.Code, entry.Name);
                _nameToCode.TryAdd(entry.Name, entry.Code);
            }
        }
        catch
        {
            // 缓存损坏，放弃
        }
    }

    private void SaveToFile(List<StockNameEntry> entries)
    {
        try
        {
            var cache = new StockNameCache(DateTime.Now, entries);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(cache));
        }
        catch
        {
            // 忽略写缓存失败
        }
    }
}

internal record StockNameEntry(string Code, string Name);

internal record StockNameCache(DateTime FetchedAt, List<StockNameEntry> Entries);
