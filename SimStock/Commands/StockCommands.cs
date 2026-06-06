using Another_Mirai_Native.Abstractions;
using Another_Mirai_Native.Abstractions.Attributes;
using Another_Mirai_Native.Abstractions.Context;
using Another_Mirai_Native.Abstractions.Enums;
using Another_Mirai_Native.Abstractions.Models;

namespace SimStock;

/// <summary>
/// 所有用户命令的处理器。继承 CommandHandlerBase 自动获得消息路由能力。
/// </summary>
public class StockCommands : CommandHandlerBase
{
    // ==================== 账户管理 ====================

    [Command(MatchMode.FullMatch, "/股票注册")]
    public async Task<EventHandleResult> CmdRegister(GroupMessageContext e)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w)
        {
            return EventHandleResult.Block;
        }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b)
        {
            return EventHandleResult.Block;
        }

        var (account, err3) = await AccountService.CreateAccountAsync(e.FromQQ.Id, e.FromGroup.Id);
        if (err3 != null)
        {
            await e.SendMessageAsync(err3);
            return EventHandleResult.Block;
        }

        await e.SendMessageAsync(new MessageBuilder()
            .At(e.FromQQ.Id)
            .Text($" 🎉 账户注册成功！初始资金: {Entry.Config.InitialCapital:N0} 元\n输入 /股票帮助 查看完整命令列表")
            .Build());
        return EventHandleResult.Block;
    }

    [Command(MatchMode.FullMatch, "/股票账户")]
    public async Task<EventHandleResult> CmdAccount(GroupMessageContext e)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w)
        {
            return EventHandleResult.Block;
        }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b)
        {
            return EventHandleResult.Block;
        }

        var account = await AccountService.GetAccountAsync(e.FromQQ.Id, e.FromGroup.Id);
        if (account == null)
        {
            await e.SendMessageAsync("⚠️ 您还没有交易账户，请使用 /股票注册 创建");
            return EventHandleResult.Block;
        }

        var positions = await AccountService.GetPositionsAsync(account.Id);
        var pendingOrders = await Entry.Db!.Queryable<Models.Order>()
            .CountAsync(o => o.AccountId == account.Id && o.Status == 0);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"💰 账户信息 - QQ: {e.FromQQ.Id}");
        sb.AppendLine($"💵 可用余额: {account.Balance:N2} 元");
        sb.AppendLine($"📊 总资产: {account.TotalAsset:N2} 元");
        sb.AppendLine($"📅 注册时间: {account.CreatedAt:yyyy-MM-dd HH:mm}");

        if (positions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("📦 --- 持仓 ---");
            foreach (var pos in positions)
            {
                sb.AppendLine($"  {pos.StockCode}  数量:{pos.Quantity}  均价:{pos.AvgCost:F2}");
            }
        }
        else
        {
            sb.AppendLine("📭 当前无持仓");
        }

        if (pendingOrders > 0)
        {
            sb.AppendLine($"\n📋 当前挂单: {pendingOrders} 单");
        }

        await e.SendMessageAsync(sb.ToString());
        return EventHandleResult.Block;
    }

    [Command(MatchMode.Regex, @"^/股票入金\s+(?<amount>\d+(\.\d+)?)$")]
    public async Task<EventHandleResult> CmdDeposit(GroupMessageContext e, decimal amount)
    {
        amount = Math.Round(amount, 2);
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w)
        {
            return EventHandleResult.Block;
        }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b)
        {
            return EventHandleResult.Block;
        }

        var (success, err3) = await AccountService.DepositAsync(e.FromQQ.Id, e.FromGroup.Id, amount);
        if (!success)
        {
            await e.SendMessageAsync(err3!);
            return EventHandleResult.Block;
        }

        var account = await AccountService.GetAccountAsync(e.FromQQ.Id, e.FromGroup.Id);
        await e.SendMessageAsync(new MessageBuilder()
            .At(e.FromQQ.Id)
            .Text($" 💵 入金 {amount:N2} 元成功！当前余额: {account!.Balance:N2} 元")
            .Build());
        return EventHandleResult.Block;
    }

    [Command(MatchMode.Regex, @"^/股票出金\s+(?<amount>\d+(\.\d+)?)$")]
    public async Task<EventHandleResult> CmdWithdraw(GroupMessageContext e, decimal amount)
    {
        amount = Math.Round(amount, 2);
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w)
        {
            return EventHandleResult.Block;
        }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b)
        {
            return EventHandleResult.Block;
        }

        var (success, err3) = await AccountService.WithdrawAsync(e.FromQQ.Id, e.FromGroup.Id, amount);
        if (!success)
        {
            await e.SendMessageAsync(err3!);
            return EventHandleResult.Block;
        }

        var account = await AccountService.GetAccountAsync(e.FromQQ.Id, e.FromGroup.Id);
        await e.SendMessageAsync(new MessageBuilder()
            .At(e.FromQQ.Id)
            .Text($" 💸 出金 {amount:N2} 元成功！当前余额: {account!.Balance:N2} 元")
            .Build());
        return EventHandleResult.Block;
    }

    [Command(MatchMode.FullMatch, "/股票重置")]
    public async Task<EventHandleResult> CmdReset(GroupMessageContext e)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w)
        {
            return EventHandleResult.Block;
        }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b)
        {
            return EventHandleResult.Block;
        }

        var account = await AccountService.GetAccountAsync(e.FromQQ.Id, e.FromGroup.Id);
        if (account == null)
        {
            await e.SendMessageAsync("您还没有交易账户");
            return EventHandleResult.Block;
        }

        await AccountService.ResetAccountAsync(e.FromQQ.Id, e.FromGroup.Id);
        await e.SendMessageAsync(new MessageBuilder()
            .At(e.FromQQ.Id)
            .Text(" 🔄 账户已重置，所有数据已清空。使用 /股票注册 重新开始")
            .Build());
        return EventHandleResult.Block;
    }

    // ==================== 管理员管理 ====================

    [Command(MatchMode.Regex, @"^/股票管理\s+添加\s+(?<qq>\d{5,12})$")]
    public async Task<EventHandleResult> CmdAdminAdd(GroupMessageContext e, long qq)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w) { return EventHandleResult.Block; }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b) { return EventHandleResult.Block; }

        if (!await AdminService.IsAdminAsync(e.FromGroup.Id, e.FromQQ.Id))
        {
            await e.SendMessageAsync("仅本群插件管理员可使用此命令");
            return EventHandleResult.Block;
        }

        var (success, err3) = await AdminService.AddAdminAsync(e.FromGroup.Id, qq);
        if (!success)
        {
            await e.SendMessageAsync(err3!);
            return EventHandleResult.Block;
        }

        await e.SendMessageAsync(new MessageBuilder()
            .At(e.FromQQ.Id)
            .Text($" 已将 QQ({qq}) 设为本群插件管理员")
            .Build());
        return EventHandleResult.Block;
    }

    [Command(MatchMode.Regex, @"^/股票管理\s+移除\s+(?<qq>\d{5,12})$")]
    public async Task<EventHandleResult> CmdAdminRemove(GroupMessageContext e, long qq)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w) { return EventHandleResult.Block; }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b) { return EventHandleResult.Block; }

        if (!await AdminService.IsAdminAsync(e.FromGroup.Id, e.FromQQ.Id))
        {
            await e.SendMessageAsync("仅本群插件管理员可使用此命令");
            return EventHandleResult.Block;
        }

        var (success, err3) = await AdminService.RemoveAdminAsync(e.FromGroup.Id, qq);
        if (!success)
        {
            await e.SendMessageAsync(err3!);
            return EventHandleResult.Block;
        }

        await e.SendMessageAsync(new MessageBuilder()
            .At(e.FromQQ.Id)
            .Text($" 已移除 QQ({qq}) 的本群插件管理员权限")
            .Build());
        return EventHandleResult.Block;
    }

    [Command(MatchMode.FullMatch, "/股票管理 列表")]
    public async Task<EventHandleResult> CmdAdminList(GroupMessageContext e)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w) { return EventHandleResult.Block; }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b) { return EventHandleResult.Block; }

        var admins = await AdminService.GetAdminsAsync(e.FromGroup.Id);
        if (admins.Count == 0)
        {
            await e.SendMessageAsync("本群尚未配置插件管理员。请在管理面板中设定。");
            return EventHandleResult.Block;
        }

        var names = new List<string>();
        foreach (var admin in admins)
        {
            try
            {
                var member = Entry.Api.GroupApi.GetGroupMemberInfo(e.FromGroup.Id, admin.QQ);
                var name = member != null
                    ? (!string.IsNullOrEmpty(member.Card) ? member.Card
                        : !string.IsNullOrEmpty(member.Nick) ? member.Nick
                        : admin.QQ.ToString())
                    : admin.QQ.ToString();
                names.Add($"  {name} (QQ:{admin.QQ})");
            }
            catch { names.Add($"  QQ:{admin.QQ}"); }
        }

        await e.SendMessageAsync($"本群插件管理员:\n{string.Join("\n", names)}");
        return EventHandleResult.Block;
    }

    // ==================== 行情查询 ====================

    [Command(MatchMode.Regex, @"^/股价\s+(?<code>\w{2,8})$")]
    public async Task<EventHandleResult> CmdPrice(GroupMessageContext e, string code)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w)
        {
            return EventHandleResult.Block;
        }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b)
        {
            return EventHandleResult.Block;
        }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0)
        {
            await e.SendMessageAsync(resolveErr);
            return EventHandleResult.Block;
        }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        if (quote == null || quote.Price <= 0)
        {
            await e.SendMessageAsync($"⚠️ 未获取到 {normalized} 的行情数据，可能不在交易时段");
            return EventHandleResult.Block;
        }

        var type = TdxProtocol.TdxConstants.GetSecurityTypeName(market, resolvedCode);

        var changePct = quote.LastClose > 0 ? (quote.Price - quote.LastClose) / quote.LastClose * 100 : 0;
        var changeSign = changePct >= 0 ? "+" : "";
        var changeEmoji = changePct > 0 ? "🔴" : changePct < 0 ? "🟢" : "⚪";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📈 {normalized} ({type}) 实时行情");
        sb.AppendLine($"💹 现价: {quote.Price:F2}  昨收: {quote.LastClose:F2}  {changeEmoji} {changeSign}{changePct:F2}%");
        sb.AppendLine($"🔴 买一: {quote.Bid1:F2}  🟢 卖一: {quote.Ask1:F2}");
        sb.AppendLine($"📊 最高: {quote.High:F2}  最低: {quote.Low:F2}");
        sb.AppendLine($"📦 成交量: {quote.Vol:F0}  成交额: {quote.Amount:F0}");

        await e.SendMessageAsync(sb.ToString());
        return EventHandleResult.Block;
    }

    // ==================== 交易操作 ====================

    [Command(MatchMode.Regex, @"^/买入\s+(?<code>\w{2,8})\s+(?<qty>\d+)$")]
    public async Task<EventHandleResult> CmdBuy(GroupMessageContext e, string code, int qty)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w)
        {
            return EventHandleResult.Block;
        }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b)
        {
            return EventHandleResult.Block;
        }

        var (th, err3) = SafetyChecker.CheckTradingHours();
        if (!th) { await e.SendMessageAsync(err3!); return EventHandleResult.Block; }

        var (account, err4) = await SafetyChecker.RequireAccountAsync(Entry.Db!, e.FromQQ.Id, e.FromGroup.Id);
        if (account == null) { await e.SendMessageAsync(err4!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0)
        {
            await e.SendMessageAsync(resolveErr);
            return EventHandleResult.Block;
        }

        var (order, err5, fee) = await TradingService.MarketBuyAsync(e.FromQQ.Id, e.FromGroup.Id, normalized, qty, e.FromGroup.Id);
        if (err5 != null) { await e.SendMessageAsync(err5); return EventHandleResult.Block; }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        var price = quote != null ? (decimal)quote.Ask1 : 0;

        await e.SendMessageAsync(new MessageBuilder()
            .At(e.FromQQ.Id)
            .Text($" ✅ 市价买入成功！\n股票: {normalized}\n数量: {qty} 股\n成交价: {price:F2} 元\n金额: {price * qty:F2} 元\n手续费: {fee:F2} 元")
            .Build());
        return EventHandleResult.Block;
    }

    [Command(MatchMode.Regex, @"^/限价买入\s+(?<code>\w{2,8})\s+(?<qty>\d+)\s+(?<price>\d+(\.\d+)?)$")]
    public async Task<EventHandleResult> CmdLimitBuy(GroupMessageContext e, string code, int qty, decimal price)
    {
        price = Math.Round(price, 2);
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w)
        {
            return EventHandleResult.Block;
        }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b)
        {
            return EventHandleResult.Block;
        }

        var (th, err3) = SafetyChecker.CheckTradingHours();
        if (!th) { await e.SendMessageAsync(err3!); return EventHandleResult.Block; }

        var (account, err4) = await SafetyChecker.RequireAccountAsync(Entry.Db!, e.FromQQ.Id, e.FromGroup.Id);
        if (account == null) { await e.SendMessageAsync(err4!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0)
        {
            await e.SendMessageAsync(resolveErr);
            return EventHandleResult.Block;
        }

        var (order, err5, fee, pendingId) = await TradingService.LimitBuyAsync(e.FromQQ.Id, e.FromGroup.Id, normalized, qty, price, e.FromGroup.Id);
        if (err5 != null) { await e.SendMessageAsync(err5); return EventHandleResult.Block; }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        var currentAsk = quote?.Ask1 ?? 0;

        if (fee.HasValue)
        {
            await e.SendMessageAsync(new MessageBuilder()
                .At(e.FromQQ.Id)
                .Text($" 🎯 限价买入已立即成交！\n股票: {normalized}\n数量: {qty} 股\n成交价: {currentAsk:F2} 元\n手续费: {fee:F2} 元")
                .Build());
        }
        else
        {
            await e.SendMessageAsync(new MessageBuilder()
                .At(e.FromQQ.Id)
                .Text($" 📝 限价买单已挂出！\n订单号: {pendingId ?? 0}\n股票: {normalized}\n数量: {qty} 股\n委托价: {price:F2} 元\n当前卖一: {currentAsk:F2} 元\n⏳ 当卖一价 ≤ {price:F2} 时自动成交")
                .Build());
        }
        return EventHandleResult.Block;
    }

    [Command(MatchMode.Regex, @"^/卖出\s+(?<code>\w{2,8})\s+(?<qty>\d+)$")]
    public async Task<EventHandleResult> CmdSell(GroupMessageContext e, string code, int qty)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w)
        {
            return EventHandleResult.Block;
        }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b)
        {
            return EventHandleResult.Block;
        }

        var (th, err3) = SafetyChecker.CheckTradingHours();
        if (!th) { await e.SendMessageAsync(err3!); return EventHandleResult.Block; }

        var (account, err4) = await SafetyChecker.RequireAccountAsync(Entry.Db!, e.FromQQ.Id, e.FromGroup.Id);
        if (account == null) { await e.SendMessageAsync(err4!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0)
        {
            await e.SendMessageAsync(resolveErr);
            return EventHandleResult.Block;
        }

        var (order, err5, fee) = await TradingService.MarketSellAsync(e.FromQQ.Id, e.FromGroup.Id, normalized, qty, e.FromGroup.Id);
        if (err5 != null) { await e.SendMessageAsync(err5); return EventHandleResult.Block; }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        var price = quote != null ? (decimal)quote.Bid1 : 0;

        await e.SendMessageAsync(new MessageBuilder()
            .At(e.FromQQ.Id)
            .Text($" ✅ 市价卖出成功！\n股票: {normalized}\n数量: {qty} 股\n成交价: {price:F2} 元\n金额: {price * qty:F2} 元\n手续费: {fee:F2} 元")
            .Build());
        return EventHandleResult.Block;
    }

    [Command(MatchMode.Regex, @"^/限价卖出\s+(?<code>\w{2,8})\s+(?<qty>\d+)\s+(?<price>\d+(\.\d+)?)$")]
    public async Task<EventHandleResult> CmdLimitSell(GroupMessageContext e, string code, int qty, decimal price)
    {
        price = Math.Round(price, 2);
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w)
        {
            return EventHandleResult.Block;
        }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b)
        {
            return EventHandleResult.Block;
        }

        var (th, err3) = SafetyChecker.CheckTradingHours();
        if (!th) { await e.SendMessageAsync(err3!); return EventHandleResult.Block; }

        var (account, err4) = await SafetyChecker.RequireAccountAsync(Entry.Db!, e.FromQQ.Id, e.FromGroup.Id);
        if (account == null) { await e.SendMessageAsync(err4!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0)
        {
            await e.SendMessageAsync(resolveErr);
            return EventHandleResult.Block;
        }

        var (order, err5, fee, pendingId) = await TradingService.LimitSellAsync(e.FromQQ.Id, e.FromGroup.Id, normalized, qty, price, e.FromGroup.Id);
        if (err5 != null) { await e.SendMessageAsync(err5); return EventHandleResult.Block; }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        var currentBid = quote?.Bid1 ?? 0;

        if (fee.HasValue)
        {
            await e.SendMessageAsync(new MessageBuilder()
                .At(e.FromQQ.Id)
                .Text($" 🎯 限价卖出已立即成交！\n股票: {normalized}\n数量: {qty} 股\n成交价: {currentBid:F2} 元\n手续费: {fee:F2} 元")
                .Build());
        }
        else
        {
            await e.SendMessageAsync(new MessageBuilder()
                .At(e.FromQQ.Id)
                .Text($" 📝 限价卖单已挂出！\n订单号: {pendingId ?? 0}\n股票: {normalized}\n数量: {qty} 股\n委托价: {price:F2} 元\n当前买一: {currentBid:F2} 元\n⏳ 当买一价 ≥ {price:F2} 时自动成交")
                .Build());
        }
        return EventHandleResult.Block;
    }

    [Command(MatchMode.Regex, @"^/股票撤单\s+(?<orderId>\d+)$")]
    public async Task<EventHandleResult> CmdCancel(GroupMessageContext e, long orderId)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w)
        {
            return EventHandleResult.Block;
        }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b)
        {
            return EventHandleResult.Block;
        }

        var (success, err3) = await TradingService.CancelOrderAsync(e.FromQQ.Id, e.FromGroup.Id, orderId);
        if (!success)
        {
            await e.SendMessageAsync(err3!);
            return EventHandleResult.Block;
        }

        await e.SendMessageAsync(new MessageBuilder()
            .At(e.FromQQ.Id)
            .Text($" ❌ 订单 {orderId} 已撤销")
            .Build());
        return EventHandleResult.Block;
    }

    // ==================== 信息查询 ====================

    [Command(MatchMode.FullMatch, "/股票排行")]
    public async Task<EventHandleResult> CmdRank(GroupMessageContext e)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w)
        {
            return EventHandleResult.Block;
        }
        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b)
        {
            return EventHandleResult.Block;
        }

        var leaderboard = await AccountService.GetLeaderboardAsync(e.FromGroup.Id, 20);
        if (leaderboard.Count == 0)
        {
            await e.SendMessageAsync("🏆 本群还没有人注册交易账户");
            return EventHandleResult.Block;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("🏆 === 交易排行榜 TOP 20 ===");
        sb.AppendLine($"{"🥇排名",-6} {"QQ",-14} {"💰总资产",14}");
        for (int i = 0; i < leaderboard.Count; i++)
        {
            var a = leaderboard[i];
            var medal = i == 0 ? "🥇" : i == 1 ? "🥈" : i == 2 ? "🥉" : $"{i + 1}.";
            sb.AppendLine($"{medal,-4}  {a.QQ,-12} {a.TotalAsset,14:N2}");
        }

        await e.SendMessageAsync(sb.ToString());
        return EventHandleResult.Block;
    }

    [Command(MatchMode.FullMatch, "/历史订单")]
    public async Task<EventHandleResult> CmdHistory(GroupMessageContext e)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w)
        {
            return EventHandleResult.Block;
        }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b)
        {
            return EventHandleResult.Block;
        }

        var account = await AccountService.GetAccountAsync(e.FromQQ.Id, e.FromGroup.Id);
        if (account == null)
        {
            await e.SendMessageAsync("⚠️ 请先使用 /股票注册 创建账户");
            return EventHandleResult.Block;
        }

        var trades = await TradingService.GetTradeHistoryAsync(account.Id, 20);
        if (trades.Count == 0)
        {
            await e.SendMessageAsync("📭 暂无交易记录");
            return EventHandleResult.Block;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("📜 === 最近交易记录 ===");
        sb.AppendLine($"{"⏰时间",-20} {"📌方向",6} {"📋代码",-10} {"📦数量",8} {"💲价格",10} {"💰金额",12}");

        foreach (var t in trades)
        {
            var dir = t.TradeType == 0 ? "🔴买" : "🟢卖";
            sb.AppendLine($"{t.TradedAt:yyyy-MM-dd HH:mm}  {dir}  {t.StockCode,-8} {t.Quantity,6} {t.Price,8:F2} {t.Amount,10:F2}");
        }

        await e.SendMessageAsync(sb.ToString());
        return EventHandleResult.Block;
    }

    [Command(MatchMode.FullMatch, "/股票帮助")]
    public async Task<EventHandleResult> CmdHelp(GroupMessageContext e)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w)
        {
            return EventHandleResult.Block;
        }

        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b)
        {
            return EventHandleResult.Block;
        }

        var custom = Entry.Config.CustomHelpText;
        var helpText = !string.IsNullOrWhiteSpace(custom)
            ? custom
            : """
            🌿 === 水银韭菜机 帮助 ===

            💰 【账户管理】
            /股票注册          创建账户，获得初始资金
            /股票账户          查看余额、持仓、挂单
            /股票入金 金额     增加账户资金
            /股票出金 金额     取出账户资金
            /股票重置          清空所有数据重新开始

            🔧 【管理员命令】
            /股票管理 添加 QQ  添加插件管理员
            /股票管理 移除 QQ  移除插件管理员
            /股票管理 列表     查看本群管理员

            📈 【行情查询】
            /股价 代码         查询实时股价
              示例: /股价 sz000001
              前缀: sz深市 sh沪市 bj北交所

            💹 【交易操作】
            /买入 代码 数量    市价买入
            /卖出 代码 数量    市价卖出
            /限价买入 代码 数量 价格  挂限价买单
            /限价卖出 代码 数量 价格  挂限价卖单
            /股票撤单 订单号   撤销挂单

            🔍 【信息查询】
            /股票排行          本群交易排行榜
            /历史订单          个人交易历史
            /股票帮助          显示本帮助

            ⚠️ 【交易规则】
            - T+1制度: 当日买入的股票次日方可卖出
            - 涨跌停限制: ±10%，涨停只能卖、跌停只能买
            - 停牌股票无法交易
            - 手续费: 成交金额的0.03%，最低5元
            - 仅支持A股交易（不含指数/基金/债券）
            - 交易单位: 100股（1手）的整数倍
            - 交易时段: 工作日 9:30-11:30 13:00-15:00
            - 限价单在满足条件时自动成交
            - 不明确交易所时请加前缀，如 /买入 sz000001 100
            """;

        await e.SendMessageAsync(helpText);
        return EventHandleResult.Block;
    }
}