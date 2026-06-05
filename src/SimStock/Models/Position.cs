using SqlSugar;

namespace SimStock.Models;

[SugarTable("Positions")]
public class Position
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnDataType = "INTEGER")]
    public long Id { get; set; }

    public long AccountId { get; set; }

    public string StockCode { get; set; } = "";

    public int Quantity { get; set; }

    public decimal AvgCost { get; set; }

    public DateTime UpdatedAt { get; set; }
}