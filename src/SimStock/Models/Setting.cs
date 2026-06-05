using SqlSugar;

namespace SimStock.Models;

[SugarTable("Settings")]
public class Setting
{
    [SugarColumn(IsPrimaryKey = true)]
    public string Key { get; set; } = "";

    public string Value { get; set; } = "";
}