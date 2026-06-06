using SqlSugar;

namespace SimStock.Models;

[SugarTable("GroupAdmins")]
public class GroupAdmin
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnDataType = "INTEGER")]
    public long Id { get; set; }

    [SugarColumn(IndexGroupNameList = ["IX_GroupId_QQ"])]
    public long GroupId { get; set; }

    [SugarColumn(IndexGroupNameList = ["IX_GroupId_QQ"])]
    public long QQ { get; set; }
}
