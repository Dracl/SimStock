using SqlSugar;

namespace SimStock.Models;

[SugarTable("TradeRecords")]
public class TradeRecord
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnDataType = "INTEGER")]
    public long Id { get; set; }

    public long AccountId { get; set; }

    public long OrderId { get; set; }

    public string StockCode { get; set; } = "";

    /// <summary>0=买入 1=卖出</summary>
    public int TradeType { get; set; }

    public int Quantity { get; set; }

    public decimal Price { get; set; }

    public decimal Amount { get; set; }

    public DateTime TradedAt { get; set; }
}