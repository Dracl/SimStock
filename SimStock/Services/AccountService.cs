using SimStock.Models;
using SqlSugar;

namespace SimStock;

/// <summary>
/// 账户管理服务。负责账户注册、查询、入金/出金、重置、排行。
/// </summary>
public static class AccountService
{
    private static SqlSugarScope Db => Entry.Db!;

    public const decimal DefaultInitialCapital = 1_000_000m;

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
            Balance = DefaultInitialCapital,
            TotalAsset = DefaultInitialCapital,
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
            return (false, "请先使用 /股票注册 创建账户");
        }

        account.Balance += amount;
        account.TotalAsset += amount;
        account.UpdatedAt = DateTime.Now;
        await Db.Updateable(account).ExecuteCommandAsync();
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
            return (false, "请先使用 /股票注册 创建账户");
        }

        if (amount > account.Balance)
        {
            return (false, $"出金失败，可用余额 {account.Balance:F2} 元，不足 {amount:F2} 元");
        }

        account.Balance -= amount;
        account.TotalAsset -= amount;
        account.UpdatedAt = DateTime.Now;
        await Db.Updateable(account).ExecuteCommandAsync();
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
        return await Db.Queryable<Account>()
            .Where(a => a.GroupId == groupId)
            .OrderBy(a => a.TotalAsset, OrderByType.Desc)
            .Take(top)
            .ToListAsync();
    }

    public static async Task<List<Position>> GetPositionsAsync(long accountId)
    {
        return await Db.Queryable<Position>()
            .Where(p => p.AccountId == accountId && p.Quantity > 0)
            .ToListAsync();
    }

    public static async Task UpdateTotalAssetAsync(long accountId)
    {
        // 简单版：仅更新总资产 = 余额（市值计算比较复杂，需要行情数据）
        // 排名显示时只按 TotalAsset 排序，每次交易后自动更新
        var account = await Db.Queryable<Account>().FirstAsync(a => a.Id == accountId);
        if (account != null)
        {
            // 计算持仓市值
            var positions = await GetPositionsAsync(accountId);
            decimal marketValue = 0;
            foreach (var pos in positions)
            {
                try
                {
                    var parsed = StockCodeParser.ParseNormalized(pos.StockCode);
                    if (parsed.HasValue)
                    {
                        var quote = await Entry.Quotes!.GetQuoteAsync(parsed.Value.market, parsed.Value.code);
                        if (quote != null && quote.Price > 0)
                        {
                            marketValue += (decimal)quote.Price * pos.Quantity;
                        }
                    }
                }
                catch { /* 某只股票行情获取失败，跳过 */ }
            }

            account.TotalAsset = account.Balance + marketValue;
            account.UpdatedAt = DateTime.Now;
            await Db.Updateable(account).ExecuteCommandAsync();
        }
    }
}