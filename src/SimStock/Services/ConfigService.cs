using SimStock.Models;
using SqlSugar;

namespace SimStock;

public class ConfigService
{
    public int MaxPendingOrdersPerUser { get; set; } = 5;

    public int QuotePollingIntervalSec { get; set; } = 3;

    public HashSet<long> GroupWhitelist { get; set; } = [];

    public HashSet<long> UserBlacklist { get; set; } = [];

    public async Task LoadAsync(SqlSugarScope db)
    {
        var settings = await db.Queryable<Setting>().ToListAsync();
        var dict = settings.ToDictionary(s => s.Key, s => s.Value);

        if (dict.TryGetValue("MaxPendingOrdersPerUser", out var maxOrders) && int.TryParse(maxOrders, out var v1))
        {
            MaxPendingOrdersPerUser = v1;
        }

        if (dict.TryGetValue("QuotePollingIntervalSec", out var interval) && int.TryParse(interval, out var v2) && v2 >= 1)
        {
            QuotePollingIntervalSec = v2;
        }

        if (dict.TryGetValue("GroupWhitelist", out var wl) && !string.IsNullOrWhiteSpace(wl))
        {
            GroupWhitelist = wl.Split(',').Select(s => long.TryParse(s.Trim(), out var id) ? id : 0).Where(id => id > 0).ToHashSet();
        }

        if (dict.TryGetValue("UserBlacklist", out var bl) && !string.IsNullOrWhiteSpace(bl))
        {
            UserBlacklist = bl.Split(',').Select(s => long.TryParse(s.Trim(), out var id) ? id : 0).Where(id => id > 0).ToHashSet();
        }
    }

    public async Task SetAsync(SqlSugarScope db, string key, string value)
    {
        var setting = await db.Queryable<Setting>().FirstAsync(s => s.Key == key);
        if (setting == null)
        {
            await db.Insertable(new Setting { Key = key, Value = value }).ExecuteCommandAsync();
        }
        else
        {
            setting.Value = value;
            await db.Updateable(setting).ExecuteCommandAsync();
        }

        // 即时更新内存缓存
        await LoadAsync(db);
    }
}