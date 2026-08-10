using Another_Mirai_Native.Abstractions;
using Another_Mirai_Native.Abstractions.Attributes;
using Another_Mirai_Native.Abstractions.Services;
using SqlSugar;
using SimStock.Models;

namespace SimStock;

[PluginInfo(
    appId: "me.cqp.luohuaming.SimStock",
    name: "水银韭菜机",
    version: "1.18.0",
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
            typeof(Models.GroupAdmin),
            typeof(Models.UserGroup),
            typeof(Models.CreditRecord),
            typeof(Models.TomorrowOrder));

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

        // 启动开盘清仓定时任务
        StartTomorrowClearScheduler(ct);

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

    /// <summary>启动开盘清仓定时任务：下一个交易日 9:31 执行待执行的清仓订单</summary>
    private static void StartTomorrowClearScheduler(CancellationToken ct)
    {
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.Now;
                    var nextTime = CalculateNextExecutionTime(now);

                    if (nextTime.HasValue)
                    {
                        var delay = nextTime.Value - now;
                        Api.Logger.Info("水银韭菜机", $"开盘清仓定时任务下次执行时间: {nextTime:yyyy-MM-dd HH:mm:ss}，等待 {delay.TotalMinutes:F0} 分钟");
                        await Task.Delay(delay, ct);
                    }

                    await ExecuteTomorrowClearOrders();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Api.Logger.Error("水银韭菜机", $"开盘清仓定时任务异常: {ex.Message}");
                }
            }
        }, ct);
    }

    /// <summary>计算下次执行时间：下一个交易日 9:31</summary>
    private static DateTime? CalculateNextExecutionTime(DateTime now)
    {
        var candidate = now.Date.AddHours(9).AddMinutes(31);

        // 如果还没到今天的 9:31，就用今天
        if (candidate > now)
        {
            return candidate;
        }

        // 否则用明天，跳过周末
        candidate = now.Date.AddDays(1).AddHours(9).AddMinutes(31);
        while (candidate.DayOfWeek == DayOfWeek.Saturday || candidate.DayOfWeek == DayOfWeek.Sunday)
        {
            candidate = candidate.AddDays(1);
        }

        return candidate;
    }

    /// <summary>执行待执行的开盘清仓订单</summary>
    private static async Task ExecuteTomorrowClearOrders()
    {
        try
        {
            var pendingOrders = await Db!.Queryable<TomorrowOrder>()
                .Where(o => o.Status == 0)
                .ToListAsync();

            if (pendingOrders.Count == 0)
            {
                return;
            }

            Api.Logger.Info("水银韭菜机", $"开盘清仓：发现 {pendingOrders.Count} 个待执行订单，开始执行");

            var client = await ConnMgr!.EnsureConnectedAsync();
            if (client == null)
            {
                Api.Logger.Warn("水银韭菜机", "开盘清仓：无法连接行情源，跳过本次执行");
                return;
            }

            // 检查当前是否在交易时段（9:30 之后）
            if (!TradingHoursChecker.IsInTradingSession())
            {
                Api.Logger.Info("水银韭菜机", "开盘清仓：当前非交易时段，跳过执行");
                return;
            }

            var failedCount = 0;

            foreach (var order in pendingOrders)
            {
                try
                {
                    // 获取账户
                    var account = await Db.Queryable<Account>()
                        .FirstAsync(a => a.QQ == order.QQ);
                    if (account == null)
                    {
                        await MarkOrderFailedAsync(order, "账户不存在");
                        failedCount++;
                        continue;
                    }

                    // 检查持仓
                    var positions = await AccountService.GetPositionsAsync(account.Id);
                    var pos = positions.FirstOrDefault(x => x.StockCode == order.StockCode);
                    if (pos == null || pos.Quantity <= 0)
                    {
                        await MarkOrderFailedAsync(order, "持仓不足");
                        failedCount++;
                        continue;
                    }

                    // 获取行情
                    var parsed = StockCodeParser.ParseNormalized(order.StockCode);
                    if (!parsed.HasValue)
                    {
                        await MarkOrderFailedAsync(order, "股票代码格式错误");
                        failedCount++;
                        continue;
                    }

                    var (market, code) = parsed.Value;
                    var quote = await Quotes!.GetQuoteAsync(market, code);
                    if (quote == null || quote.Bid1 <= 0)
                    {
                        await MarkOrderFailedAsync(order, "行情获取失败或无买盘");
                        failedCount++;
                        continue;
                    }

                    // 执行卖出
                    var (sellOrder, sellErr, fee) = await TradingService.MarketSellAsync(
                        order.QQ, order.StockCode, pos.Quantity, order.GroupId);

                    if (sellErr != null)
                    {
                        await MarkOrderFailedAsync(order, sellErr);
                        failedCount++;
                    }
                    else
                    {
                        // 执行成功：更新订单状态
                        order.Status = 1;
                        order.UpdatedAt = DateTime.Now;
                        await Db.Updateable(order).ExecuteCommandAsync();

                        var stockName = await StockNames.GetNameAsync(order.StockCode);
                        Api.Logger.Info("水银韭菜机", $"开盘清仓成功：{order.QQ} {order.StockCode}（{stockName}）卖出 {pos.Quantity} 股");

                        // 在群里发送成功通知
                        await SendGroupMessageAsync(order.GroupId, $"✅ {order.StockCode}（{stockName}）开盘清仓成功！卖出 {pos.Quantity} 股");
                    }
                }
                catch (Exception ex)
                {
                    await MarkOrderFailedAsync(order, $"执行异常：{ex.Message}");
                    failedCount++;
                }
            }

            if (failedCount > 0)
            {
                Api.Logger.Warn("水银韭菜机", $"开盘清仓：{failedCount} 个订单执行失败");
            }
        }
        catch (Exception ex)
        {
            Api.Logger.Error("水银韭菜机", $"开盘清仓执行异常：{ex.Message}");
        }
    }

    /// <summary>标记订单为失败</summary>
    private static async Task MarkOrderFailedAsync(TomorrowOrder order, string reason)
    {
        order.Status = 3;
        order.FailureReason = reason;
        order.UpdatedAt = DateTime.Now;
        await Db.Updateable(order).ExecuteCommandAsync();

        // 在群里发送失败通知
        await SendGroupMessageAsync(order.GroupId, $"⚠️ {order.QQ} 的开盘清仓 {order.StockCode} 因 {reason} 执行失败，请手动处理");
    }

    /// <summary>发送群消息</summary>
    private static async Task SendGroupMessageAsync(long groupId, string message)
    {
        try
        {
            await Api.MessageApi.SendGroupMessageAsync(groupId, message);
        }
        catch (Exception ex)
        {
            Api.Logger.Warn("水银韭菜机", $"发送群消息失败：{ex.Message}");
        }
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
                        API.Logger.Info("水银韭菜机", $"启动结算：订单 {order.Id} {order.StockCode} 已成交");
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
            API.Logger.Error("水银韭菜机", $"清理遗留挂单异常：{ex.Message}");
        }
    }
}
