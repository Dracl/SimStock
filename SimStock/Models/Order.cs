using SqlSugar;

namespace SimStock.Models;

[SugarTable("Orders")]
public class Order
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnDataType = "INTEGER")]
    public long Id { get; set; }

    public long AccountId { get; set; }

    public string StockCode { get; set; } = "";

    /// <summary>订单来源群号，null 表示来自私聊</summary>
    [SugarColumn(IsNullable = true)]
    public long? SourceGroupId { get; set; }

    /// <summary>原始消息ID，用于撮合成功后引用回复</summary>
    [SugarColumn(IsNullable = true)]
    public int? SourceMessageId { get; set; }

    /// <summary>0=市价买 1=限价买 2=市价卖 3=限价卖</summary>
    public int OrderType { get; set; }

    public int Quantity { get; set; }

    public decimal Price { get; set; }

    public int FilledQuantity { get; set; }

    /// <summary>0=挂单中 1=部分成交 2=已成交 3=已撤销</summary>
    public int Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}