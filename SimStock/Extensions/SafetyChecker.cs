using SimStock.Models;
using SqlSugar;
using TdxProtocol;

namespace SimStock;

/// <summary>
/// 10项安全互锁检查。
/// </summary>
public static class SafetyChecker
{
    // 1. 群组白名单（空白名单=允许所有）
    public static (bool passed, string? error) CheckGroupWhitelist(long groupId)
    {
        var wl = Entry.Config.GroupWhitelist;
        if (wl.Count > 0 && !wl.Contains(groupId))
        {
            return (false, "本群未在交易白名单中");
        }

        return (true, null);
    }

    // 2. 用户黑名单
    public static (bool passed, string? error) CheckUserBlacklist(long qq)
    {
        if (Entry.Config!.UserBlacklist.Contains(qq))
        {
            return (false, "您已被禁止使用股票模拟交易");
        }

        return (true, null);
    }

    // 3. 交易时段检查（仅下单时需要）
    public static (bool passed, string? error) CheckTradingHours()
    {
        if (!TradingHoursChecker.IsInTradingSession())
        {
            return (false, $"当前{TradingHoursChecker.GetStatusDescription()}，无法交易");
        }

        return (true, null);
    }

    // 4. 需要已有账户
    public static async Task<(Account? account, string? error)> RequireAccountAsync(SqlSugarScope db, long qq)
    {
        var account = await db.Queryable<Account>()
            .FirstAsync(a => a.QQ == qq);
        if (account == null)
        {
            return (null, $"请先使用 {Entry.Config.GetTrigger("Register")} 创建交易账户");
        }

        return (account, null);
    }

    // 5. 仅允许A股交易（排除指数、基金、债券）
    public static (bool passed, string? error) CheckAStock(byte market, string code)
    {
        var type = TdxConstants.GetSecurityType(market, code);
        if (!type.EndsWith("_A_STOCK"))
        {
            return (false, $"仅支持A股交易，代码 {code} 类型为 {type}");
        }

        return (true, null);
    }

    // 6. 参数合法性：数量≥100且为100的倍数，价格>0
    public static (bool passed, string? error) CheckOrderParams(int qty, decimal? price = null)
    {
        if (qty < 100 || qty % 100 != 0)
        {
            return (false, "交易数量必须为100的整数倍（最少100股）");
        }

        if (price.HasValue && price.Value <= 0)
        {
            return (false, "价格必须大于0");
        }

        return (true, null);
    }

    // 7. 资金充足
    public static (bool passed, string? error) CheckFunds(Account account, decimal requiredAmount)
    {
        if (account.Balance < requiredAmount)
        {
            return (false, $"可用资金不足，需要 {requiredAmount:F2} 元，当前余额 {account.Balance:F2} 元");
        }

        return (true, null);
    }

    // 8. 持仓充足
    public static async Task<(bool passed, string? error)> CheckHoldingsAsync(SqlSugarScope db, long accountId, string stockCode, int sellQty)
    {
        var totalHolding = await db.Queryable<Position>()
            .Where(p => p.AccountId == accountId && p.StockCode == stockCode)
            .SumAsync(p => p.Quantity);
        if (totalHolding < sellQty)
        {
            return (false, $"持仓不足，需要 {sellQty} 股，当前持有 {totalHolding} 股");
        }

        return (true, null);
    }

    // 9. 最大挂单数
    public static async Task<(bool passed, string? error)> CheckPendingOrderLimitAsync(SqlSugarScope db, long accountId)
    {
        var pendingCount = await db.Queryable<Order>()
            .CountAsync(o => o.AccountId == accountId && o.Status == 0);
        if (pendingCount >= Entry.Config!.MaxPendingOrdersPerUser)
        {
            return (false, $"挂单数量已达上限（{Entry.Config.MaxPendingOrdersPerUser}单），请先撤单");
        }

        return (true, null);
    }

    // 10. 订单归属检查（撤单用）
    public static async Task<(Order? order, string? error)> RequireOwnOrderAsync(SqlSugarScope db, long orderId, long accountId)
    {
        var order = await db.Queryable<Order>().FirstAsync(o => o.Id == orderId);
        if (order == null)
        {
            return (null, "订单不存在");
        }

        if (order.AccountId != accountId)
        {
            return (null, "无权操作此订单");
        }

        if (order.Status != 0)
        {
            return (null, "该订单已成交或已撤销，无法撤单");
        }

        return (order, null);
    }

    // 11. T+1检查：当日买入的股票当日不可卖出
    public static async Task<(bool passed, string? error)> CheckT1RuleAsync(SqlSugarScope db, long accountId, string stockCode)
    {
        var today = DateTime.Now.Date;
        var boughtToday = await db.Queryable<TradeRecord>()
            .AnyAsync(t => t.AccountId == accountId && t.StockCode == stockCode
                        && t.TradeType == 0 && t.TradedAt >= today);
        if (boughtToday)
        {
            return (false, "T+1制度限制：当日买入的股票需下一个交易日方可卖出");
        }

        return (true, null);
    }

    // 12. 停牌检查
    public static (bool passed, string? error) CheckSuspension(double bid1, double ask1)
    {
        if (bid1 <= 0 && ask1 <= 0)
        {
            return (false, "该股票已停牌或暂无流动性，无法交易");
        }

        return (true, null);
    }

    /// <summary>计算手续费：交易金额的0.03%，最低5元</summary>
    public static decimal CalcFee(decimal amount)
    {
        var fee = amount * 0.0003m;
        return fee < 5m ? 5m : Math.Round(fee, 2);
    }

    /// <summary>计算授信待还利息：本金 × 日利率 × 天数</summary>
    public static decimal CalculateInterest(Account account, ConfigService config)
    {
        if (account.DebtBalance <= 0 || config.CreditInterestRate <= 0)
        {
            return 0m;
        }

        var days = (DateTime.Now - account.LastInterestCalculated).TotalDays;
        if (days <= 0) return 0m;

        return Math.Round(account.DebtBalance * (decimal)days * config.CreditInterestRate, 2);
    }
}
