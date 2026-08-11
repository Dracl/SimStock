using SqlSugar;

namespace SimStock.Models;

[SugarTable("TomorrowOrders")]
public class TomorrowOrder
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnDataType = "INTEGER")]
    public long Id { get; set; }

    /// <summary>QQ 号</summary>
    public long QQ { get; set; }

    /// <summary>群号</summary>
    public long GroupId { get; set; }

    /// <summary>股票代码，"ALL" 表示全仓清仓</summary>
    public string StockCode { get; set; } = "";

    /// <summary>订单类型：0=开盘清仓 1=开盘梭哈买入</summary>
    public int OrderType { get; set; }

    /// <summary>订单状态：0=待执行 1=已执行 2=已取消 3=失败</summary>
    public int Status { get; set; }

    /// <summary>失败原因</summary>
    [SugarColumn(IsNullable = true)]
    public string? FailureReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
