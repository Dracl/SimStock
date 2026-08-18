using System.Text.Json;
using TdxProtocol;

namespace SimStock;

/// <summary>
/// TDX 连接管理。负责最佳服务器选择（每天一次缓存）、连接建立/断开。
/// </summary>
public class ConnectionManager : IDisposable
{
    private TdxClient? _client;
    private string? _bestIp;
    private int _bestPort;
    private DateTime _lastBestIpCheck;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _bestIpCachePath;
    private readonly HolidayClient _holidayClient;
    private string _holidayCacheDir;

    public ConnectionManager(string appDir)
    {
        _bestIpCachePath = Path.Combine(appDir, "bestip.json");
        _holidayCacheDir = appDir;
        _holidayClient = new HolidayClient { CacheDirectory = appDir };
    }

    public void SetHolidayCacheDirectory(string dir)
    {
        _holidayCacheDir = dir;
        _holidayClient.CacheDirectory = dir;
    }

    public bool IsConnected => _client?.IsConnected ?? false;

    /// <summary>
    /// 确保 TDX 已连接。如果断连会自动重连。
    /// 每天只检查一次最佳服务器。
    /// </summary>
    public async Task<TdxClient?> EnsureConnectedAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_client?.IsConnected == true)
            {
                return _client;
            }

            // 每天检查一次最佳服务器
            await RefreshBestIpIfNeededAsync();

            if (_bestIp == null)
            {
                Entry.Api.Logger.Warn("连接管理", "无法获取最佳服务器IP，行情服务不可用");
                return null;
            }

            // 重新连接
            _client?.Dispose();
            _client = new TdxClient();
            TdxClient.Logger = msg => Entry.Api.Logger.Debug("TDX", msg);
            _client.Connect(_bestIp, _bestPort);
            Entry.Api.Logger.Info("连接管理", $"已连接 TDX {_bestIp}:{_bestPort}");
            return _client;
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("连接管理", $"TDX连接失败: {ex.Message}");
            _client?.Dispose();
            _client = null;
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 断开 TDX 连接。
    /// </summary>
    public void Disconnect()
    {
        _lock.Wait();
        try
        {
            _client?.Dispose();
            _client = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> IsInTradingSessionAsync()
    {
        if (!TradingHoursChecker.IsInTradingSession())
        {
            return false;
        }

        return await _holidayClient.IsTradingDayAsync(DateTime.Now);
    }

    /// <summary>判断今天是否为交易日（不考虑具体时段，节假日返回 false）</summary>
    public async Task<bool> IsTradingDayAsync()
    {
        return await _holidayClient.IsTradingDayAsync(DateTime.Now);
    }

    public async Task RefreshBestIpAsync()
    {
        try
        {
            TdxClient.Logger = msg => Entry.Api.Logger.Debug("TDX", msg);
            BestIpFinder.Log = msg => Entry.Api.Logger.Info("寻找最佳服务器", msg);

            var results = await BestIpFinder.BestIpAsync(top: 1, savePath: _bestIpCachePath);
            if (results.Length > 0)
            {
                _bestIp = results[0].Server.Ip;
                _bestPort = results[0].Server.Port;
                _lastBestIpCheck = DateTime.Now;
            }
            else
            {
                Entry.Api.Logger.Warn("连接管理", "最佳服务器发现完成，但未找到可用服务器");
            }
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("连接管理", $"最佳服务器发现失败: {ex.Message}");
        }
    }

    private async Task RefreshBestIpIfNeededAsync()
    {
        // 先尝试从缓存文件加载
        if (_bestIp == null && File.Exists(_bestIpCachePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<BestIpConfig>(File.ReadAllText(_bestIpCachePath));
                if (cached != null)
                {
                    _bestIp = cached.BestIp.Ip;
                    _bestPort = cached.BestIp.Port;
                    _lastBestIpCheck = cached.UpdatedAt;
                }
            }
            catch (Exception ex)
            {
                Entry.Api.Logger.Warn("连接管理", $"最佳服务器缓存读取失败: {ex.Message}");
            }
        }

        // 检查是否需要刷新（每天一次，或者还没有IP）
        if (_bestIp == null || (DateTime.Now - _lastBestIpCheck).TotalHours >= 24)
        {
            await RefreshBestIpAsync();
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _lock.Dispose();
    }
}