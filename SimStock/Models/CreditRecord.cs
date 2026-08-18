using SqlSugar;

namespace SimStock.Models;

[SugarTable("CreditRecords")]
public class CreditRecord
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, ColumnDataType = "INTEGER")]
    public long Id { get; set; }

    public long AccountId { get; set; }

    /// <summary>1=借入, 2=偿还</summary>
    public int Type { get; set; }

    public decimal Amount { get; set; }

    public decimal Interest { get; set; }

    public DateTime CreatedAt { get; set; }

    public long? SourceMessageId { get; set; }
}