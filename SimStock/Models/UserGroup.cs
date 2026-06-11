using SqlSugar;

namespace SimStock.Models;

[SugarTable("UserGroups")]
public class UserGroup
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnDataType = "INTEGER")]
    public long Id { get; set; }

    [SugarColumn(UniqueGroupNameList = ["UQ_QQ_GroupId"])]
    public long QQ { get; set; }

    [SugarColumn(UniqueGroupNameList = ["UQ_QQ_GroupId"])]
    public long GroupId { get; set; }
}
