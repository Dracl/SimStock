using Another_Mirai_Native.Abstractions;
using Another_Mirai_Native.Abstractions.Attributes;
using Another_Mirai_Native.Abstractions.Services;
using SqlSugar;

namespace SimStock;

[PluginInfo(
    appId: "me.cqp.luohuaming.SimStock",
    name: "水银韭菜机",
    version: "1.5.0",
    description: "群聊模拟炒股插件",
    author: "落花茗"
)]
public class Entry : PluginBase
{
    public static SqlSugarScope? Db { get; private set; }

    public static ConfigService Config { get; private set; } = null!;

    public static MatchingEngine? Matcher { get; private set; }

    public static ConnectionManager? ConnMgr { get; private set; }

    public static QuoteService Quotes { get; private set; } = null!;

    public static StockNameService StockNames { get; private set; } = null!;

    public static IPluginApi Api { get; private set; } = null!;

    public override async Task OnEnableAsync(CancellationToken ct)
    {
        Api = API;
        var appDir = API.AppApi.GetAppDirectory();
        API.Logger.Info("水银韭菜机", $"插件目录: {appDir}");

        // 初始化数据库
        var dbPath = Path.Combine(appDir, "core.db");
        var connStr = $"Data Source={dbPath};Pooling=true;";

        Db = new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = connStr,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute
        });

        // 建库建表
        Db.DbMaintenance.CreateDatabase();
        Db.CodeFirst.InitTables(
            typeof(Models.Account),
            typeof(Models.Position),
            typeof(Models.Order),
            typeof(Models.TradeRecord),
            typeof(Models.Setting),
            typeof(Models.GroupAdmin));

        // 加载配置
        Config = new ConfigService();
        await Config.LoadAsync(Db);

        // 初始化行情服务
        Quotes = new QuoteService();
        ConnMgr = new ConnectionManager(appDir);
        ConnMgr.SetHolidayCacheDirectory(appDir);
        StockNames = new StockNameService(appDir, ConnMgr);

        // 启动时清理遗留挂单
        await CleanupPendingOrdersOnStartup();

        // 启动撮合引擎
        Matcher = new MatchingEngine(ConnMgr);
        Matcher.Start(ct);

        API.Logger.Info("水银韭菜机", "插件已启用");
    }

    public override async Task OnDisableAsync(CancellationToken ct)
    {
        API.Logger.Info("水银韭菜机", "正在停止...");

        if (Matcher != null)
        {
            await Matcher.StopAsync();
        }

        ConnMgr?.Disconnect();
        ConnMgr?.Dispose();

        Db?.Close();

        API.Logger.Info("水银韭菜机", "插件已禁用");
    }

    /// <summary>启动时清理遗留挂单：交易时段尝试结算，非交易时段直接撤销</summary>
    private async Task CleanupPendingOrdersOnStartup()
    {
        try
        {
            var pending = await Db!.Queryable<Models.Order>()
                .Where(o => o.Status == 0)
                .ToListAsync();
            if (pending.Count == 0)
            {
                return;
            }

            var inSession = TradingHoursChecker.IsInTradingSession();

            if (inSession)
            {
                API.Logger.Info("水银韭菜机", $"启动时发现 {pending.Count} 个遗留挂单，当前为交易时段，尝试结算");

                var client = await ConnMgr!.EnsureConnectedAsync();
                if (client == null)
                {
                    API.Logger.Warn("水银韭菜机", "无法连接行情源，遗留挂单保留待撮合引擎处理");
                    return;
                }

                // 收集唯一股票代码
                var uniqueStocks = pending
                    .Select(o => o.StockCode).Distinct()
                    .Select(StockCodeParser.ParseNormalized)
                    .Where(p => p.HasValue)
                    .Select(p => p!.Value)
                    .ToList();

                var quotes = await Quotes!.GetQuotesBatchAsync(uniqueStocks);
                if (quotes == null)
                {
                    API.Logger.Warn("水银韭菜机", "获取行情失败，遗留挂单保留待撮合引擎处理");
                    return;
                }

                foreach (var order in pending)
                {
                    if (!quotes.TryGetValue(order.StockCode, out var quote))
                    {
                        continue;
                    }

                    bool shouldExecute = order.OrderType == 1
                        ? (quote.Ask1 > 0 && order.Price >= (decimal)quote.Ask1)
                        : order.OrderType == 3
                            ? (quote.Bid1 > 0 && order.Price <= (decimal)quote.Bid1)
                            : false;

                    if (shouldExecute)
                    {
                        var execPrice = order.OrderType == 1 ? (decimal)quote.Ask1 : (decimal)quote.Bid1;
                        await TradingService.ExecuteOrderAsync(order, execPrice);
                        API.Logger.Info("水银韭菜机", $"启动结算: 订单 {order.Id} {order.StockCode} 已成交");
                    }
                }
            }
            else
            {
                // 非交易时段：全部撤销
                API.Logger.Info("水银韭菜机", $"启动时发现 {pending.Count} 个遗留挂单，非交易时段，全部撤销");
                foreach (var order in pending)
                {
                    order.Status = 3;
                    order.UpdatedAt = DateTime.Now;
                    await Db.Updateable(order).ExecuteCommandAsync();
                }
            }
        }
        catch (Exception ex)
        {
            API.Logger.Error("水银韭菜机", $"清理遗留挂单异常: {ex.Message}");
        }
    }
}