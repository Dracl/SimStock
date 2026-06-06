using SimStock.Models;
using SqlSugar;

namespace SimStock;

public class ConfigService
{
    public int MaxPendingOrdersPerUser { get; set; } = 5;

    public int QuotePollingIntervalSec { get; set; } = 3;

    public decimal InitialCapital { get; set; } = 1_000_000m;

    public string CustomHelpText { get; set; } = "";

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

        if (dict.TryGetValue("InitialCapital", out var capital) && decimal.TryParse(capital, out var v3) && v3 > 0)
        {
            InitialCapital = v3;
        }

        if (dict.TryGetValue("CustomHelpText", out var help) && !string.IsNullOrWhiteSpace(help))
        {
            CustomHelpText = help;
        }
        else
        {
            CustomHelpText = "";
        }

        if (dict.TryGetValue("GroupWhitelist", out var wl) && !string.IsNullOrWhiteSpace(wl))
        {
            GroupWhitelist = ParseIdList(wl);
        }

        if (dict.TryGetValue("UserBlacklist", out var bl) && !string.IsNullOrWhiteSpace(bl))
        {
            UserBlacklist = ParseIdList(bl);
        }
    }

    /// <summary>解析逗号分隔的ID列表，同时支持英文逗号和中文逗号，无效条目静默跳过</summary>
    public static HashSet<long> ParseIdList(string raw)
    {
        return raw.Split(',', '，')
            .Select(s => long.TryParse(s.Trim(), out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();
    }

    /// <summary>将ID集合规范化为逗号分隔的存储字符串</summary>
    public static string FormatIdList(HashSet<long> ids) => string.Join(",", ids);

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