using SimStock.Models;
using SqlSugar;

namespace SimStock;

/// <summary>
/// 账户管理服务。负责账户注册、查询、入金/出金、重置、排行。
/// </summary>
public static class AccountService
{
    private static SqlSugarScope Db => Entry.Db!;

    public static async Task<Account?> GetAccountAsync(long qq, long groupId)
    {
        return await Db.Queryable<Account>()
            .FirstAsync(a => a.QQ == qq && a.GroupId == groupId);
    }

    public static async Task<(Account? account, string? error)> CreateAccountAsync(long qq, long groupId)
    {
        var existing = await GetAccountAsync(qq, groupId);
        if (existing != null)
        {
            return (existing, "您已注册过交易账户，请使用 /股票账户 查看");
        }

        var account = new Account
        {
            QQ = qq,
            GroupId = groupId,
            Balance = Entry.Config.InitialCapital,
            TotalAsset = Entry.Config.InitialCapital,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        var id = await Db.Insertable(account).ExecuteReturnBigIdentityAsync();
        account.Id = id;
        return (account, null);
    }

    public static async Task<(bool success, string? error)> DepositAsync(long qq, long groupId, decimal amount)
    {
        if (amount <= 0)
        {
            return (false, "入金金额必须大于0");
        }

        var account = await GetAccountAsync(qq, groupId);
        if (account == null)
        {
            return (false, $"请先使用 {Entry.Config.GetTrigger("Register")} 创建账户");
        }

        account.Balance += amount;
        account.UpdatedAt = DateTime.Now;
        await Db.Updateable(account).ExecuteCommandAsync();
        await UpdateTotalAssetAsync(account.Id);
        return (true, null);
    }

    public static async Task<(bool success, string? error)> WithdrawAsync(long qq, long groupId, decimal amount)
    {
        if (amount <= 0)
        {
            return (false, "出金金额必须大于0");
        }

        var account = await GetAccountAsync(qq, groupId);
        if (account == null)
        {
            return (false, $"请先使用 {Entry.Config.GetTrigger("Register")} 创建账户");
        }

        if (amount > account.Balance)
        {
            return (false, $"出金失败，可用余额 {account.Balance:N2} 元，不足 {amount:N2} 元");
        }

        account.Balance -= amount;
        account.UpdatedAt = DateTime.Now;
        await Db.Updateable(account).ExecuteCommandAsync();
        await UpdateTotalAssetAsync(account.Id);
        return (true, null);
    }

    public static async Task ResetAccountAsync(long qq, long groupId)
    {
        var account = await GetAccountAsync(qq, groupId);
        if (account == null)
        {
            return;
        }

        await Db.UseTranAsync(async () =>
        {
            await Db.Deleteable<Order>().Where(o => o.AccountId == account.Id).ExecuteCommandAsync();
            await Db.Deleteable<TradeRecord>().Where(t => t.AccountId == account.Id).ExecuteCommandAsync();
            await Db.Deleteable<Position>().Where(p => p.AccountId == account.Id).ExecuteCommandAsync();
            await Db.Deleteable<Account>().Where(a => a.Id == account.Id).ExecuteCommandAsync();
        });
    }

    public static async Task<List<Account>> GetLeaderboardAsync(long groupId, int top = 20)
    {
        var accounts = await Db.Queryable<Account>()
            .Where(a => a.GroupId == groupId)
            .OrderBy(a => a.TotalAsset, OrderByType.Desc)
            .Take(top)
            .ToListAsync();
        await RefreshTotalAssetsAsync(accounts);
        accounts.Sort((a, b) => b.TotalAsset.CompareTo(a.TotalAsset));
        return accounts;
    }

    public static async Task<List<Account>> GetGlobalLeaderboardAsync(int top = 20)
    {
        var accounts = await Db.Queryable<Account>()
            .OrderBy(a => a.TotalAsset, OrderByType.Desc)
            .Take(top)
            .ToListAsync();
        await RefreshTotalAssetsAsync(accounts);
        accounts.Sort((a, b) => b.TotalAsset.CompareTo(a.TotalAsset));
        return accounts;
    }

    /// <summary>批量刷新账户总资产：收集所有持仓的唯�股票，一次批量获取行情后重算</summary>
    private static async Task RefreshTotalAssetsAsync(List<Account> accounts)
    {
        if (accounts.Count == 0) return;

        // 收集所有账户的持仓
        var accountIds = accounts.Select(a => a.Id).ToList();
        var allPositions = await Db.Queryable<Position>()
            .Where(p => accountIds.Contains(p.AccountId) && p.Quantity > 0)
            .ToListAsync();

        if (allPositions.Count == 0) return;

        // 收集唯一股票代码并批量获取行情
        var uniqueStocks = allPositions
            .Select(p => StockCodeParser.ParseNormalized(p.StockCode))
            .Where(p => p.HasValue)
            .Select(p => (p.Value.market, p.Value.code))
            .Distinct()
            .ToList();

        Dictionary<string, TdxProtocol.Models.QuoteResult>? quotes = null;
        try { quotes = await Entry.Quotes!.GetQuotesBatchAsync(uniqueStocks); }
        catch { /* 行情不可用时刷新失败，使用缓存值 */ }
        if (quotes == null) return;

        // 按账户分组计算市值
        var marketValues = new Dictionary<long, decimal>();
        foreach (var pos in allPositions)
        {
            if (quotes.TryGetValue(pos.StockCode, out var quote) && quote.Price > 0)
            {
                marketValues.TryGetValue(pos.AccountId, out var current);
                marketValues[pos.AccountId] = current + (decimal)quote.Price * pos.Quantity;
            }
        }

        // 更新 TotalAsset
        foreach (var account in accounts)
        {
            var mv = marketValues.GetValueOrDefault(account.Id);
            if (account.TotalAsset != account.Balance + mv)
            {
                account.TotalAsset = account.Balance + mv;
                account.UpdatedAt = DateTime.Now;
                await Db.Updateable(account).ExecuteCommandAsync();
            }
        }
    }

    public static async Task<List<Position>> GetPositionsAsync(long accountId)
    {
        return await Db.Queryable<Position>()
            .Where(p => p.AccountId == accountId && p.Quantity > 0)
            .ToListAsync();
    }

    /// <summary>更新账户总资产 = 现金余额 + 持仓市值（实时行情）</summary>
    public static async Task UpdateTotalAssetAsync(long accountId)
    {
        var account = await Db.Queryable<Account>().FirstAsync(a => a.Id == accountId);
        if (account == null) return;

        var positions = await GetPositionsAsync(accountId);
        if (positions.Count == 0)
        {
            account.TotalAsset = account.Balance;
            account.UpdatedAt = DateTime.Now;
            await Db.Updateable(account).ExecuteCommandAsync();
            return;
        }

        // 批量获取行情
        var stocks = positions
            .Select(p => StockCodeParser.ParseNormalized(p.StockCode))
            .Where(p => p.HasValue)
            .Select(p => (p.Value.market, p.Value.code))
            .ToList();

        var quotes = await Entry.Quotes!.GetQuotesBatchAsync(stocks);
        decimal marketValue = 0;
        foreach (var pos in positions)
        {
            var normalized = pos.StockCode;
            if (quotes != null && quotes.TryGetValue(normalized, out var quote) && quote.Price > 0)
                marketValue += (decimal)quote.Price * pos.Quantity;
        }

        account.TotalAsset = account.Balance + marketValue;
        account.UpdatedAt = DateTime.Now;
        await Db.Updateable(account).ExecuteCommandAsync();
    }
}