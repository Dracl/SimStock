using SimStock.Models;
using SqlSugar;

namespace SimStock;

public static class AdminService
{
    private static SqlSugarScope Db => Entry.Db!;

    /// <summary>检查用户是否是指定群的插件管理员。未配置管理员时任何人都不通过。</summary>
    public static async Task<bool> IsAdminAsync(long groupId, long qq)
    {
        return await Db.Queryable<GroupAdmin>()
            .AnyAsync(a => a.GroupId == groupId && a.QQ == qq);
    }

    /// <summary>检查指定群是否已配置了至少一位管理员</summary>
    public static async Task<bool> HasAnyAdminAsync(long groupId)
    {
        return await Db.Queryable<GroupAdmin>()
            .AnyAsync(a => a.GroupId == groupId);
    }

    /// <summary>获取指定群的所有插件管理员</summary>
    public static async Task<List<GroupAdmin>> GetAdminsAsync(long groupId)
    {
        return await Db.Queryable<GroupAdmin>()
            .Where(a => a.GroupId == groupId)
            .ToListAsync();
    }

    /// <summary>添加插件管理员</summary>
    public static async Task<(bool success, string? error)> AddAdminAsync(long groupId, long qq)
    {
        var exists = await Db.Queryable<GroupAdmin>()
            .AnyAsync(a => a.GroupId == groupId && a.QQ == qq);
        if (exists)
        {
            return (false, "该用户已是本群插件管理员");
        }

        await Db.Insertable(new GroupAdmin { GroupId = groupId, QQ = qq }).ExecuteCommandAsync();
        return (true, null);
    }

    /// <summary>移除插件管理员。允许移除最后一位，之后该群回到开放模式。</summary>
    public static async Task<(bool success, string? error)> RemoveAdminAsync(long groupId, long qq)
    {
        var admin = await Db.Queryable<GroupAdmin>()
            .FirstAsync(a => a.GroupId == groupId && a.QQ == qq);
        if (admin == null)
        {
            return (false, "该用户不是本群插件管理员");
        }

        await Db.Deleteable(admin).ExecuteCommandAsync();
        return (true, null);
    }
}
