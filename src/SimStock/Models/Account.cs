using SqlSugar;

namespace SimStock.Models;

[SugarTable("Accounts")]
public class Account
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnDataType = "INTEGER")]
    public long Id { get; set; }

    [SugarColumn(IndexGroupNameList = ["IX_QQ_Group"])]
    public long QQ { get; set; }

    [SugarColumn(IndexGroupNameList = ["IX_QQ_Group"])]
    public long GroupId { get; set; }

    public decimal Balance { get; set; }

    public decimal TotalAsset { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    [Navigate(NavigateType.OneToMany, nameof(Position.AccountId))]
    public List<Position> Positions { get; set; } = [];

    [Navigate(NavigateType.OneToMany, nameof(Order.AccountId))]
    public List<Order> Orders { get; set; } = [];
}