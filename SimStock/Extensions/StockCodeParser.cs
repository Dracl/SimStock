using TdxProtocol;

namespace SimStock;

/// <summary>
/// 股票代码解析。支持 sz/sh/bj 前缀，无前缀时智能推断交易所。
/// </summary>
public static class StockCodeParser
{
    /// <summary>
    /// 带前缀解析: "sz000001" → (MarketSZ, "000001")
    /// 返回 null 表示格式不正确
    /// </summary>
    public static (byte market, string code)? TryParseWithPrefix(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        input = input.Trim().ToLowerInvariant();

        // 匹配: 可选2字母前缀 + 数字代码（至少4位，最多6位）
        var match = System.Text.RegularExpressions.Regex.Match(input, @"^(sz|sh|bj)?(\d{4,6})$");
        if (!match.Success)
        {
            return null;
        }

        var prefix = match.Groups[1].Value;
        var code = match.Groups[2].Value.PadLeft(6, '0');

        byte market;
        if (prefix == "sh")
        {
            market = TdxConstants.MarketSH;
        }
        else if (prefix == "bj")
        {
            market = TdxConstants.MarketBJ;
        }
        else
        {
            market = TdxConstants.MarketSZ; // 默认深市（sz或省略）
        }

        return (market, code);
    }

    /// <summary>
    /// 无前缀时根据代码段推断交易所。
    /// 返回 null 表示无法推断（需要在两个市场都尝试）。
    /// </summary>
    public static (byte market, string code)? TryInferMarket(string code)
    {
        if (code.Length < 4)
        {
            return null;
        }

        var prefix = code[..2];

        // 60xxxx, 68xxxx → 沪市A股
        // 00xxxx, 30xxxx → 深市A股
        // 8xxxxx → 北交所
        // 其他: 无法确定
        if (prefix == "60" || prefix == "68")
        {
            return (TdxConstants.MarketSH, code.PadLeft(6, '0'));
        }

        if (prefix == "00" || prefix == "30")
        {
            return (TdxConstants.MarketSZ, code.PadLeft(6, '0'));
        }

        if (prefix == "83" || prefix == "87" || prefix == "43" || prefix == "92")
        {
            return (TdxConstants.MarketBJ, code.PadLeft(6, '0'));
        }

        return null;
    }

    /// <summary>
    /// 标准化代码: (MarketSZ, "1") → "sz000001"
    /// </summary>
    public static string NormalizeCode(byte market, string code)
    {
        var prefix = market switch
        {
            TdxConstants.MarketSH => "sh",
            TdxConstants.MarketBJ => "bj",
            _ => "sz"
        };
        return prefix + code.PadLeft(6, '0');
    }

    /// <summary>
    /// 反向解析: "sz000001" → (MarketSZ, "000001")
    /// </summary>
    public static (byte market, string code)? ParseNormalized(string normalizedCode)
    {
        return TryParseWithPrefix(normalizedCode);
    }
}