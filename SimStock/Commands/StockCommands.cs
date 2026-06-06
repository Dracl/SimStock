using Another_Mirai_Native.Abstractions;
using Another_Mirai_Native.Abstractions.Attributes;
using Another_Mirai_Native.Abstractions.Context;
using Another_Mirai_Native.Abstractions.Enums;
using Another_Mirai_Native.Abstractions.Models;
using SimStock.Models;

namespace SimStock;

public class StockCommands : CommandHandlerBase
{
    private const long PrivateChatGroupId = 0L;

    public string AccountCmd => Entry.Config.GetCommandTemplate("Account");

    public string AdminAddCmd => Entry.Config.GetCommandTemplate("AdminAdd");

    public string AdminListCmd => Entry.Config.GetCommandTemplate("AdminList");

    public string AdminRemoveCmd => Entry.Config.GetCommandTemplate("AdminRemove");

    public string BuyCmd => Entry.Config.GetCommandTemplate("Buy");

    public string CancelCmd => Entry.Config.GetCommandTemplate("Cancel");

    public string DepositCmd => Entry.Config.GetCommandTemplate("Deposit");

    public string GlobalRankCmd => Entry.Config.GetCommandTemplate("GlobalRank");

    public string HelpCmd => Entry.Config.GetCommandTemplate("Help");

    public string HistoryCmd => Entry.Config.GetCommandTemplate("History");

    public string LimitBuyCmd => Entry.Config.GetCommandTemplate("LimitBuy");

    public string LimitSellCmd => Entry.Config.GetCommandTemplate("LimitSell");

    public string PriceCmd => Entry.Config.GetCommandTemplate("Price");

    public string RankCmd => Entry.Config.GetCommandTemplate("Rank");

    public string RegisterCmd => Entry.Config.GetCommandTemplate("Register");

    public string ResetCmd => Entry.Config.GetCommandTemplate("Reset");

    public string SellCmd => Entry.Config.GetCommandTemplate("Sell");

    public string WithdrawCmd => Entry.Config.GetCommandTemplate("Withdraw");

    [DynamicCommand(nameof(AccountCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdAccount(GroupMessageContext? g, PrivateMessageContext? p)
    {
        var qq = GetQQ(g, p);
        var (groupId, _, isPrivate) = ResolveCtx(g, p);

        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var account = await AccountService.GetAccountAsync(qq, groupId);
        if (account == null)
        {
            await SendAsync(g, p, $"⚠️ 您还没有交易账户，请使用 {Entry.Config.GetTrigger("Register")} 创建");
            return EventHandleResult.Block;
        }

        var positions = await AccountService.GetPositionsAsync(account.Id);
        var pendingOrders = await Entry.Db!.Queryable<Models.Order>().CountAsync(o => o.AccountId == account.Id && o.Status == 0);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(isPrivate ? "💰 账户信息" : $"💰 账户信息 - QQ: {qq}");
        sb.AppendLine($"💵 可用余额: {account.Balance:N2} 元");
        sb.AppendLine($"📊 总资产: {account.TotalAsset:N2} 元");
        sb.AppendLine($"📅 注册时间: {account.CreatedAt:yyyy-MM-dd HH:mm}");
        if (positions.Count > 0)
        {
            sb.AppendLine(); sb.AppendLine("📦 --- 持仓 ---"); foreach (var pos in positions)
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

        await SendAsync(g, p, sb.ToString());
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(AdminAddCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdAdminAdd(GroupMessageContext e, long qq)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w) { return EventHandleResult.Block; }
        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b) { return EventHandleResult.Block; }
        if (!await AdminService.IsAdminAsync(e.FromGroup.Id, e.FromQQ.Id)) { await e.SendMessageAsync("仅本群插件管理员可使用此命令"); return EventHandleResult.Block; }

        var (success, err3) = await AdminService.AddAdminAsync(e.FromGroup.Id, qq);
        if (!success) { await e.SendMessageAsync(err3!); return EventHandleResult.Block; }

        await e.SendMessageAsync(new MessageBuilder().At(e.FromQQ.Id).Text($" 已将 QQ({qq}) 设为本群插件管理员").Build());
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(AdminListCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdAdminList(GroupMessageContext e)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w) { return EventHandleResult.Block; }
        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b) { return EventHandleResult.Block; }

        var admins = await AdminService.GetAdminsAsync(e.FromGroup.Id);
        if (admins.Count == 0) { await e.SendMessageAsync("本群尚未配置插件管理员。请在管理面板中设定。"); return EventHandleResult.Block; }

        var names = new List<string>();
        foreach (var admin in admins)
        {
            try
            {
                var member = Entry.Api.GroupApi.GetGroupMemberInfo(e.FromGroup.Id, admin.QQ);
                var name = member != null ? (!string.IsNullOrEmpty(member.Card) ? member.Card : !string.IsNullOrEmpty(member.Nick) ? member.Nick : admin.QQ.ToString()) : admin.QQ.ToString();
                names.Add($"  {name} (QQ:{admin.QQ})");
            }
            catch { names.Add($"  QQ:{admin.QQ}"); }
        }
        await e.SendMessageAsync($"本群插件管理员:\n{string.Join("\n", names)}");
        return EventHandleResult.Block;
    }

    // ==================== 管理员管理（仅群聊） ====================
    [DynamicCommand(nameof(AdminRemoveCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdAdminRemove(GroupMessageContext e, long qq)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w) { return EventHandleResult.Block; }
        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b) { return EventHandleResult.Block; }
        if (!await AdminService.IsAdminAsync(e.FromGroup.Id, e.FromQQ.Id)) { await e.SendMessageAsync("仅本群插件管理员可使用此命令"); return EventHandleResult.Block; }

        var (success, err3) = await AdminService.RemoveAdminAsync(e.FromGroup.Id, qq);
        if (!success) { await e.SendMessageAsync(err3!); return EventHandleResult.Block; }

        await e.SendMessageAsync(new MessageBuilder().At(e.FromQQ.Id).Text($" 已移除 QQ({qq}) 的本群插件管理员权限").Build());
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(BuyCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdBuy(GroupMessageContext? g, PrivateMessageContext? p, string code, int qty)
    {
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (th, err) = SafetyChecker.CheckTradingHours();
        if (!th) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        var (account, err2) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq, groupId);
        if (account == null) { await SendAsync(g, p, err2!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }

        var (order, err3, fee) = await TradingService.MarketBuyAsync(qq, groupId, normalized, qty, sourceGroupId);
        if (err3 != null) { await SendAsync(g, p, err3); return EventHandleResult.Block; }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        var price = quote != null ? (decimal)quote.Ask1 : 0;
        await SendAsync(g, p, $" ✅ 市价买入成功！\n股票: {normalized}\n数量: {qty} 股\n成交价: {price:F2} 元\n金额: {price * qty:F2} 元\n手续费: {fee:F2} 元");
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(CancelCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdCancel(GroupMessageContext? g, PrivateMessageContext? p, long orderId)
    {
        var qq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (success, err3) = await TradingService.CancelOrderAsync(qq, groupId, orderId);
        if (!success) { await SendAsync(g, p, err3!); return EventHandleResult.Block; }

        await SendAsync(g, p, $" ❌ 订单 {orderId} 已撤销");
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(DepositCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdDeposit(GroupMessageContext? g, PrivateMessageContext? p, long qq, decimal amount)
    {
        amount = Math.Round(amount, 2);
        var callerQq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);

        if (!await CheckAccess(g, p, callerQq))
        {
            return EventHandleResult.Block;
        }

        if (!await AdminService.IsAdminAsync(groupId, callerQq)) { await SendAsync(g, p, "仅本群插件管理员可执行此操作"); return EventHandleResult.Block; }

        var (success, err3) = await AccountService.DepositAsync(qq, groupId, amount);
        if (!success) { await SendAsync(g, p, err3!); return EventHandleResult.Block; }

        var account = await AccountService.GetAccountAsync(qq, groupId);
        await SendAsync(g, p, $" 💵 已向 QQ({qq}) 入金 {amount:N2} 元，当前余额: {account!.Balance:N2} 元");
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(GlobalRankCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdGlobalRank(GroupMessageContext? g, PrivateMessageContext? p)
    {
        var qq = GetQQ(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var leaderboard = await AccountService.GetGlobalLeaderboardAsync(20);
        if (leaderboard.Count == 0) { await SendAsync(g, p, "还没有人注册交易账户"); return EventHandleResult.Block; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("🌍 === 全局排行 TOP 20 ===");
        if (g != null)
        {
            await BuildLeaderboardAsync(sb, leaderboard, g.FromGroup.Id);
        }
        else
        {
            AppendRankRowsForPrivate(sb, leaderboard);
        }

        await SendAsync(g, p, sb.ToString());
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(HelpCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdHelp(GroupMessageContext? g, PrivateMessageContext? p)
    {
        var qq = GetQQ(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var custom = Entry.Config.CustomHelpText;
        await SendAsync(g, p, !string.IsNullOrWhiteSpace(custom) ? custom : BuildDefaultHelpText());
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(HistoryCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdHistory(GroupMessageContext? g, PrivateMessageContext? p)
    {
        var qq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var account = await AccountService.GetAccountAsync(qq, groupId);
        if (account == null) { await SendAsync(g, p, $"⚠️ 请先使用 {Entry.Config.GetTrigger("Register")} 创建账户"); return EventHandleResult.Block; }

        var trades = await TradingService.GetTradeHistoryAsync(account.Id, 20);
        if (trades.Count == 0) { await SendAsync(g, p, "📭 暂无交易记录"); return EventHandleResult.Block; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("📜 === 最近交易记录 ===");
        sb.AppendLine($"{"⏰时间",-20} {"📌方向",6} {"📋代码",-10} {"📦数量",8} {"💲价格",10} {"💰金额",12}");
        foreach (var t in trades)
        {
            var dir = t.TradeType == 0 ? "🔴买" : "🟢卖";
            sb.AppendLine($"{t.TradedAt:yyyy-MM-dd HH:mm}  {dir}  {t.StockCode,-8} {t.Quantity,6} {t.Price,8:F2} {t.Amount,10:F2}");
        }
        await SendAsync(g, p, sb.ToString());
        return EventHandleResult.Block;
    }

    // ==================== 交易操作 ====================
    [DynamicCommand(nameof(LimitBuyCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdLimitBuy(GroupMessageContext? g, PrivateMessageContext? p, string code, int qty, decimal price)
    {
        price = Math.Round(price, 2);
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (th, err) = SafetyChecker.CheckTradingHours();
        if (!th) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        var (account, err2) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq, groupId);
        if (account == null) { await SendAsync(g, p, err2!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }

        var (order, err3, fee, pendingId) = await TradingService.LimitBuyAsync(qq, groupId, normalized, qty, price, sourceGroupId);
        if (err3 != null) { await SendAsync(g, p, err3); return EventHandleResult.Block; }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        var currentAsk = quote?.Ask1 ?? 0;

        if (fee.HasValue)
        {
            await SendAsync(g, p, $" 🎯 限价买入已立即成交！\n股票: {normalized}\n数量: {qty} 股\n成交价: {currentAsk:F2} 元\n手续费: {fee:F2} 元");
        }
        else
        {
            await SendAsync(g, p, $" 📝 限价买单已挂出！\n订单号: {pendingId ?? 0}\n股票: {normalized}\n数量: {qty} 股\n委托价: {price:F2} 元\n当前卖一: {currentAsk:F2} 元\n⏳ 当卖一价 ≤ {price:F2} 时自动成交");
        }

        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(LimitSellCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdLimitSell(GroupMessageContext? g, PrivateMessageContext? p, string code, int qty, decimal price)
    {
        price = Math.Round(price, 2);
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (th, err) = SafetyChecker.CheckTradingHours();
        if (!th) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        var (account, err2) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq, groupId);
        if (account == null) { await SendAsync(g, p, err2!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }

        var (order, err3, fee, pendingId) = await TradingService.LimitSellAsync(qq, groupId, normalized, qty, price, sourceGroupId);
        if (err3 != null) { await SendAsync(g, p, err3); return EventHandleResult.Block; }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        var currentBid = quote?.Bid1 ?? 0;

        if (fee.HasValue)
        {
            await SendAsync(g, p, $" 🎯 限价卖出已立即成交！\n股票: {normalized}\n数量: {qty} 股\n成交价: {currentBid:F2} 元\n手续费: {fee:F2} 元");
        }
        else
        {
            await SendAsync(g, p, $" 📝 限价卖单已挂出！\n订单号: {pendingId ?? 0}\n股票: {normalized}\n数量: {qty} 股\n委托价: {price:F2} 元\n当前买一: {currentBid:F2} 元\n⏳ 当买一价 ≥ {price:F2} 时自动成交");
        }

        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(PriceCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdPrice(GroupMessageContext? g, PrivateMessageContext? p, string code)
    {
        var qq = GetQQ(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        if (quote == null || quote.Price <= 0) { await SendAsync(g, p, $"⚠️ 未获取到 {normalized} 的行情数据，可能不在交易时段"); return EventHandleResult.Block; }

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
        await SendAsync(g, p, sb.ToString());
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(RankCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdRank(GroupMessageContext e)
    {
        var (w, err) = SafetyChecker.CheckGroupWhitelist(e.FromGroup.Id);
        if (!w) { return EventHandleResult.Block; }
        var (b, err2) = SafetyChecker.CheckUserBlacklist(e.FromQQ.Id);
        if (!b) { return EventHandleResult.Block; }

        var leaderboard = await AccountService.GetLeaderboardAsync(e.FromGroup.Id, 20);
        if (leaderboard.Count == 0) { await e.SendMessageAsync("本群还没有人注册交易账户"); return EventHandleResult.Block; }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("🏆 === 本群排行 TOP 20 ===");
        await BuildLeaderboardAsync(sb, leaderboard, e.FromGroup.Id);
        await e.SendMessageAsync(sb.ToString());
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(RegisterCmd), MatchMode.FullMatch)]
    public async Task<EventHandleResult> CmdRegister(GroupMessageContext? g, PrivateMessageContext? p)
    {
        var qq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);

        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (account, err3) = await AccountService.CreateAccountAsync(qq, groupId);
        if (err3 != null) { await SendAsync(g, p, err3); return EventHandleResult.Block; }

        var msg = groupId != PrivateChatGroupId
            ? $"🎉 账户注册成功！初始资金: {Entry.Config.InitialCapital:N0} 元\n输入 {Entry.Config.GetTrigger("Help")} 查看完整命令列表"
            : $"🎉 账户注册成功！初始资金: {Entry.Config.InitialCapital:N0} 元";
        await SendAsync(g, p, msg);
        return EventHandleResult.Block;
    }

    [DynamicCommand(nameof(ResetCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdReset(GroupMessageContext? g, PrivateMessageContext? p, long qq)
    {
        var callerQq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);

        if (!await CheckAccess(g, p, callerQq))
        {
            return EventHandleResult.Block;
        }

        if (!await AdminService.IsAdminAsync(groupId, callerQq)) { await SendAsync(g, p, "仅本群插件管理员可执行此操作"); return EventHandleResult.Block; }

        var account = await AccountService.GetAccountAsync(qq, groupId);
        if (account == null) { await SendAsync(g, p, $"QQ({qq}) 在本群没有交易账户"); return EventHandleResult.Block; }

        await AccountService.ResetAccountAsync(qq, groupId);
        await SendAsync(g, p, $" 🔄 QQ({qq}) 的账户已重置，所有数据已清空");
        return EventHandleResult.Block;
    }

    // ==================== 行情查询 ====================
    [DynamicCommand(nameof(SellCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdSell(GroupMessageContext? g, PrivateMessageContext? p, string code, int qty)
    {
        var qq = GetQQ(g, p);
        var (groupId, sourceGroupId, _) = ResolveCtx(g, p);
        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (th, err) = SafetyChecker.CheckTradingHours();
        if (!th) { await SendAsync(g, p, err!); return EventHandleResult.Block; }

        var (account, err2) = await SafetyChecker.RequireAccountAsync(Entry.Db!, qq, groupId);
        if (account == null) { await SendAsync(g, p, err2!); return EventHandleResult.Block; }

        var (market, resolvedCode, normalized, resolveErr) = await Entry.Quotes!.ResolveCodeAsync(code);
        if (resolveErr != null && market == 0) { await SendAsync(g, p, resolveErr); return EventHandleResult.Block; }

        var (order, err3, fee) = await TradingService.MarketSellAsync(qq, groupId, normalized, qty, sourceGroupId);
        if (err3 != null) { await SendAsync(g, p, err3); return EventHandleResult.Block; }

        var quote = await Entry.Quotes!.GetQuoteAsync(market, resolvedCode);
        var price = quote != null ? (decimal)quote.Bid1 : 0;
        await SendAsync(g, p, $" ✅ 市价卖出成功！\n股票: {normalized}\n数量: {qty} 股\n成交价: {price:F2} 元\n金额: {price * qty:F2} 元\n手续费: {fee:F2} 元");
        return EventHandleResult.Block;
    }

    // ==================== 账户管理 ====================
    [DynamicCommand(nameof(WithdrawCmd), MatchMode.Regex)]
    public async Task<EventHandleResult> CmdWithdraw(GroupMessageContext? g, PrivateMessageContext? p, decimal amount)
    {
        amount = Math.Round(amount, 2);
        var qq = GetQQ(g, p);
        var (groupId, _, _) = ResolveCtx(g, p);

        if (!await CheckAccess(g, p, qq))
        {
            return EventHandleResult.Block;
        }

        var (success, err3) = await AccountService.WithdrawAsync(qq, groupId, amount);
        if (!success) { await SendAsync(g, p, err3!); return EventHandleResult.Block; }

        var account = await AccountService.GetAccountAsync(qq, groupId);
        await SendAsync(g, p, $" 💸 出金 {amount:N2} 元成功！当前余额: {account!.Balance:N2} 元");
        return EventHandleResult.Block;
    }

    private static void AppendRankRows(System.Text.StringBuilder sb, List<Account> accounts, Dictionary<long, string> nameCache)
    {
        sb.AppendLine($"{"排名",-4} {"昵称",-16} {"💰总资产",14}");
        for (int i = 0; i < accounts.Count; i++)
        {
            var a = accounts[i];
            var medal = i == 0 ? "🥇" : i == 1 ? "🥈" : i == 2 ? "🥉" : $"{i + 1}.";
            var name = nameCache.TryGetValue(a.QQ, out var n) ? n : a.QQ.ToString();
            sb.AppendLine($"{medal,-4} {name,-14} {a.TotalAsset,14:N2}");
        }
    }

    private static void AppendRankRowsForPrivate(System.Text.StringBuilder sb, List<Account> accounts)
    {
        var nameCache = accounts.ToDictionary(a => a.QQ, a => a.QQ.ToString());
        AppendRankRows(sb, accounts, nameCache);
    }

    private static string BuildDefaultHelpText()
    {
        var t = (string name) => Entry.Config.GetTrigger(name);
        return $"""
            🌿 === 水银韭菜机 帮助 ===

            💰 【账户管理】
            {t("Register")}          创建账户，获得初始资金
            {t("Account")}          查看余额、持仓、挂单
            {t("Withdraw")} 金额     取出账户资金

            🔧 【管理员命令】
            {t("Deposit")} QQ 金额  为指定用户增加资金
            {t("Reset")} QQ       重置指定用户的账户
            {t("AdminAdd")} QQ  添加插件管理员
            {t("AdminRemove")} QQ  移除插件管理员
            {t("AdminList")}     查看本群管理员

            📈 【行情查询】
            {t("Price")} 代码     查询实时股价
              示例: {t("Price")} sz000001
              前缀: sz深市 sh沪市 bj北交所

            💹 【交易操作】
            {t("Buy")} 代码 数量 市价买入
            {t("Sell")} 代码 数量 市价卖出
            {t("LimitBuy")} 代码 数量 价格  挂限价买单
            {t("LimitSell")} 代码 数量 价格  挂限价卖单
            {t("Cancel")} 订单号   撤销挂单

            🔍 【信息查询】
            {t("Rank")}          本群交易排行榜
            {t("GlobalRank")}          全局交易排行榜
            {t("History")}          个人交易历史
            {t("Help")}          显示本帮助

            ⚠️ 【交易规则】
            - T+1制度: 当日买入的股票次日方可卖出
            - 涨跌停限制: ±10%，涨停只能卖、跌停只能买
            - 停牌股票无法交易
            - 手续费: 成交金额的0.03%，最低5元
            - 仅支持A股交易（不含指数/基金/债券）
            - 交易单位: 100股（1手）的整数倍
            - 交易时段: 工作日 9:30-11:30 13:00-15:00
            - 限价单在满足条件时自动成交
            - 不明确交易所时请加前缀，如 {t("Buy")} sz000001 100
            """;
    }

    // ==================== 信息查询 ====================
    private static async Task BuildLeaderboardAsync(System.Text.StringBuilder sb, List<Account> accounts, long groupId)
    {
        var nameCache = new Dictionary<long, string>();
        foreach (var a in accounts)
        {
            if (nameCache.ContainsKey(a.QQ))
            {
                continue;
            }

            try
            {
                var member = Entry.Api.GroupApi.GetGroupMemberInfo(groupId, a.QQ);
                nameCache[a.QQ] = member != null
                    ? (!string.IsNullOrEmpty(member.Card) ? member.Card : !string.IsNullOrEmpty(member.Nick) ? member.Nick : a.QQ.ToString())
                    : a.QQ.ToString();
            }
            catch { nameCache[a.QQ] = a.QQ.ToString(); }
        }
        AppendRankRows(sb, accounts, nameCache);
    }

    /// <summary>
    /// 访问检查：私聊跳过白名单，群聊检查白名单；统一检查黑名单
    /// </summary>
    private static async Task<bool> CheckAccess(GroupMessageContext? g, PrivateMessageContext? p, long qq)
    {
        if (g != null)
        {
            var (w, _) = SafetyChecker.CheckGroupWhitelist(g.FromGroup.Id);
            if (!w)
            {
                return false;
            }
        }
        var (b, _) = SafetyChecker.CheckUserBlacklist(qq);
        return b;
    }

    private static long GetQQ(GroupMessageContext? g, PrivateMessageContext? p)
    {
        return (g ?? (object?)p) switch
        {
            GroupMessageContext gc => gc.FromQQ.Id,
            PrivateMessageContext pc => pc.FromQQ.Id,
            _ => 0
        };
    }

    /// <summary>
    /// 从上下文解析来源：群聊返回 (groupId, sourceGroupId)，私聊返回 (0, null)
    /// </summary>
    private static (long groupId, long? sourceGroupId, bool isPrivate) ResolveCtx(GroupMessageContext? g, PrivateMessageContext? p)
    {
        if (g != null)
        {
            return (g.FromGroup.Id, g.FromGroup.Id, false);
        }

        return (PrivateChatGroupId, null, true);
    }

    /// <summary>
    /// 向来源发送消息：群聊时 @发送者，私聊时直接发送
    /// </summary>
    private static Task SendAsync(GroupMessageContext? g, PrivateMessageContext? p, string msg)
    {
        if (g != null)
        {
            return g.SendMessageAsync(new MessageBuilder().At(g.FromQQ.Id).Text(msg).Build());
        }

        return p!.SendMessageAsync(msg);
    }

    // ==================== 辅助方法 ====================
}