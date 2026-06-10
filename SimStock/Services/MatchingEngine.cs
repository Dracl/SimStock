using Another_Mirai_Native.Abstractions.Models;

namespace SimStock;

/// <summary>
/// 后台撮合引擎。交易时段内轮询所有待成交限价单，自动撮合。
/// 无挂单时断连休眠；非交易时段断连休眠。
/// </summary>
public class MatchingEngine : IDisposable
{
    private readonly ConnectionManager _connMgr;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public MatchingEngine(ConnectionManager connMgr)
    {
        _connMgr = connMgr;
    }

    public void Start(CancellationToken externalCt)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }

        if (_loopTask != null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var wasInSession = false;

        // 日志去重信号：每个状态只打印一次，所有检查通过后重置
        bool loggedAuction = false;
        bool loggedOffHours = false;
        bool loggedHoliday = false;
        bool loggedNoOrders = false;
        bool loggedConnFail = false;
        bool loggedQuoteFail = false;

        void ResetLogSignals()
        {
            loggedAuction = loggedOffHours = loggedHoliday = false;
            loggedNoOrders = loggedConnFail = loggedQuoteFail = false;
        }

        void LogOnce(ref bool flag, string msg)
        {
            if (!flag)
            {
                Entry.Api.Logger.Info("撮合引擎", msg);
                flag = true;
            }
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 竞价时段跳过
                if (TradingHoursChecker.IsInAuctionPeriod())
                {
                    wasInSession = false;
                    LogOnce(ref loggedAuction, "当前是竞价时段，不处理挂单");
                    _connMgr.Disconnect();
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }

                // 非交易时段
                if (!TradingHoursChecker.IsInTradingSession())
                {
                    if (wasInSession)
                    {
                        wasInSession = false;
                        await CancelAllPendingOrdersAtCloseAsync();
                    }

                    LogOnce(ref loggedOffHours, "当前是非交易时段，不处理挂单");
                    _connMgr.Disconnect();
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }

                // 检查是否交易日
                if (!await _connMgr.IsInTradingSessionAsync())
                {
                    if (wasInSession)
                    {
                        wasInSession = false;
                        await CancelAllPendingOrdersAtCloseAsync();
                    }

                    LogOnce(ref loggedHoliday, "当前是节假日，不处理挂单");
                    _connMgr.Disconnect();
                    await Task.Delay(TimeSpan.FromMinutes(5), ct);
                    continue;
                }

                wasInSession = true;

                // 获取待成交限价单
                var pendingOrders = await TradingService.GetPendingLimitOrdersAsync();
                if (pendingOrders.Count == 0)
                {
                    LogOnce(ref loggedNoOrders, "当前无待成交挂单");
                    _connMgr.Disconnect();
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }

                // 收集唯一股票代码
                var uniqueStocks = pendingOrders
                    .Select(o => o.StockCode)
                    .Distinct()
                    .Select(code =>
                    {
                        var parsed = StockCodeParser.ParseNormalized(code);
                        return parsed.HasValue ? (parsed.Value.market, parsed.Value.code) : ((byte)0, "");
                    })
                    .Where(s => s.Item2.Length > 0)
                    .ToList();

                if (uniqueStocks.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    continue;
                }

                // 确保连接
                var client = await _connMgr.EnsureConnectedAsync(ct);
                if (client == null)
                {
                    LogOnce(ref loggedConnFail, "无法连接行情服务器");
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    continue;
                }

                // 批量获取行情
                var quotesDict = await Entry.Quotes!.GetQuotesBatchAsync(uniqueStocks);
                if (quotesDict == null || quotesDict.Count == 0)
                {
                    LogOnce(ref loggedQuoteFail, "获取行情数据失败");
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }

                // 所有检查通过，重置日志信号
                ResetLogSignals();

                // 逐单撮合
                foreach (var order in pendingOrders)
                {
                    if (!quotesDict.TryGetValue(order.StockCode, out var quote))
                    {
                        continue;
                    }

                    bool shouldExecute = false;

                    if (order.OrderType == 1) // 限价买
                    {
                        if (quote.Ask1 > 0 && order.Price >= (decimal)quote.Ask1)
                        {
                            shouldExecute = true;
                        }
                    }
                    else if (order.OrderType == 3) // 限价卖
                    {
                        if (quote.Bid1 > 0 && order.Price <= (decimal)quote.Bid1)
                        {
                            shouldExecute = true;
                        }
                    }

                    if (shouldExecute)
                    {
                        var execPrice = order.OrderType == 1 ? (decimal)quote.Ask1 : (decimal)quote.Bid1;
                        await TradingService.ExecuteOrderAsync(order, execPrice);

                        // 发送成交通知：群聊来源发群，私聊来源发私聊
                        try
                        {
                            var account = await Entry.Db!.Queryable<Models.Account>()
                                .FirstAsync(a => a.Id == order.AccountId);
                            if (account != null)
                            {
                                var fee = SafetyChecker.CalcFee(execPrice * order.Quantity);
                                var dir = order.OrderType == 1 ? "🔴买入" : "🟢卖出";
                                var msg = $"🎯 [限价单成交通知]\n" +
                                          $"📋 股票: {StockCodeParser.ToDisplayCode(order.StockCode)}\n" +
                                          $"📌 方向: {dir}\n" +
                                          $"📦 数量: {order.Quantity} 股\n" +
                                          $"💲 成交价: {execPrice:F2} 元\n" +
                                          $"🧾 手续费: {fee:F2} 元\n" +
                                          $"💰 金额: {execPrice * order.Quantity:F2} 元\n" +
                                          $"⏰ 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

                                if (order.SourceGroupId.HasValue)
                                {
                                    await SendGroupNotificationAsync(order, msg, account.QQ);
                                }
                                else
                                {
                                    await Entry.Api.MessageApi.SendPrivateMessageAsync(account.QQ, msg);
                                }
                            }
                        }
                        catch { /* 通知失败不影响撮合 */ }
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(Entry.Config.QuotePollingIntervalSec), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                _connMgr.Disconnect();
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    /// <summary>收盘后自动撤销所有未成交挂单，并在对应群内发送汇总通知</summary>
    private async Task CancelAllPendingOrdersAtCloseAsync()
    {
        try
        {
            var pendingOrders = await TradingService.GetPendingLimitOrdersAsync();
            if (pendingOrders.Count == 0)
            {
                return;
            }

            // 收集订单信息并按来源分组
            var accountIds = pendingOrders.Select(o => o.AccountId).Distinct().ToList();
            var accounts = await Entry.Db!.Queryable<Models.Account>()
                .Where(a => accountIds.Contains(a.Id))
                .ToListAsync();
            var accountDict = accounts.ToDictionary(a => a.Id);

            // groupId → orders,    0 = 私聊来源
            var groupOrders = new Dictionary<long, List<(Models.Order Order, long QQ)>>();
            var privateOrders = new List<(Models.Order Order, long QQ)>();

            foreach (var order in pendingOrders)
            {
                order.Status = 3;
                order.UpdatedAt = DateTime.Now;
                await Entry.Db!.Updateable(order).ExecuteCommandAsync();

                if (!accountDict.TryGetValue(order.AccountId, out var acc))
                {
                    continue;
                }

                if (order.SourceGroupId.HasValue)
                {
                    var gid = order.SourceGroupId.Value;
                    if (!groupOrders.ContainsKey(gid))
                    {
                        groupOrders[gid] = [];
                    }

                    groupOrders[gid].Add((order, acc.QQ));
                }
                else
                {
                    privateOrders.Add((order, acc.QQ));
                }
            }

            // 群聊来源：按群发汇总
            foreach (var (sourceGroupId, orders) in groupOrders)
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("🌙 本日已休市，未成交挂单自动取消：");

                    // 获取昵称后发群
                    var nameCache = new Dictionary<long, string>();
                    foreach (var (_, qq) in orders)
                    {
                        if (!nameCache.ContainsKey(qq))
                        {
                            try
                            {
                                var member = Entry.Api.GroupApi.GetGroupMemberInfo(sourceGroupId, qq);
                                nameCache[qq] = member != null
                                    ? (!string.IsNullOrEmpty(member.Card) ? member.Card
                                        : !string.IsNullOrEmpty(member.Nick) ? member.Nick
                                        : qq.ToString())
                                    : qq.ToString();
                            }
                            catch { nameCache[qq] = qq.ToString(); }
                        }
                    }

                    foreach (var (order, qq) in orders)
                    {
                        var name = nameCache.TryGetValue(qq, out var n) ? n : qq.ToString();
                        var dir = order.OrderType switch { 1 => "买入", 3 => "卖出", _ => "?" };
                        sb.AppendLine($"  · {name}");
                        sb.AppendLine($"    📋 {StockCodeParser.ToDisplayCode(order.StockCode)}");
                        sb.AppendLine($"    📌 {dir} {order.Quantity} 股");
                        sb.AppendLine($"    💲 委托价: {order.Price:F2}");
                    }

                    await Entry.Api.MessageApi.SendGroupMessageAsync(sourceGroupId, sb.ToString());
                }
                catch { /* 发送失败不影响 */ }
            }

            // 私聊来源：逐人发私聊
            foreach (var (order, qq) in privateOrders)
            {
                try
                {
                    var dir = order.OrderType switch { 1 => "买入", 3 => "卖出", _ => "?" };
                    var msg = $"🌙 本日已休市，挂单自动取消：\n" +
                              $"📋 {StockCodeParser.ToDisplayCode(order.StockCode)}\n" +
                              $"📌 {dir} {order.Quantity} 股\n" +
                              $"💲 委托价: {order.Price:F2}";
                    await Entry.Api.MessageApi.SendPrivateMessageAsync(qq, msg);
                }
                catch { /* 发送失败不影响 */ }
            }
        }
        catch { /* 撤单失败不影响主循环 */ }
    }

    /// <summary>
    /// 群聊成交通知：优先引用原始挂单消息回复，找不到时 @用户
    /// </summary>
    private static async Task SendGroupNotificationAsync(Models.Order order, string msg, long qq)
    {
        try
        {
            if (order.SourceMessageId.HasValue)
            {
                var mb = new MessageBuilder();
                mb.Items.Add(new Another_Mirai_Native.Abstractions.Models.MessageItem.Reply(order.SourceMessageId.Value));
                mb.Text(msg);
                await Entry.Api.MessageApi.SendGroupMessageAsync(order.SourceGroupId!.Value, mb.Build());
            }
            else
            {
                // 没有原始消息ID，@用户
                var mb = new MessageBuilder();
                mb.At(qq);
                mb.Text(msg);
                await Entry.Api.MessageApi.SendGroupMessageAsync(order.SourceGroupId!.Value, mb.Build());
            }
        }
        catch
        {
            // 引用回复失败（消息可能被删除），回退到 @用户
            try
            {
                var mb = new MessageBuilder();
                mb.At(qq);
                mb.Text(msg);
                await Entry.Api.MessageApi.SendGroupMessageAsync(order.SourceGroupId!.Value, mb.Build());
            }
            catch { /* 最终失败放弃 */ }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _loopTask?.Dispose();
    }
}