using TdxProtocol;
using TdxProtocol.Commands;
using TdxProtocol.Models;

namespace SimStock;

/// <summary>
/// 行情查询服务。封装 TdxProtocol 的实时报价获取、代码解析、股票类型判定。
/// </summary>
public class QuoteService
{
    /// <summary>
    /// 解析用户输入的股票代码。支持 sz/sh/bj 前缀，无前缀时尝试在市场间查询。
    /// 返回: (market, code, normalizedCode, error) - error 为 null 表示成功
    /// </summary>
    public async Task<(byte market, string code, string normalizedCode, string? error)> ResolveCodeAsync(string input)
    {
        // 1. 带前缀解析: sz000001, sh600000 等
        var parsed = StockCodeParser.TryParseWithPrefix(input);
        if (parsed.HasValue)
        {
            var (market, code) = parsed.Value;
            return (market, code, StockCodeParser.NormalizeCode(market, code), null);
        }

        // 2. 纯数字代码，根据代码段推断
        var codeOnly = input.Trim().PadLeft(6, '0');
        var inferred = StockCodeParser.TryInferMarket(codeOnly);
        if (inferred.HasValue)
        {
            var (market, code) = inferred.Value;
            return (market, code, StockCodeParser.NormalizeCode(market, code), null);
        }

        // 3. 无法推断，连接行情源实际查询
        var client = await Entry.ConnMgr!.EnsureConnectedAsync();
        if (client == null)
            return (0, "", "", "行情服务暂不可用，请稍后重试");

        var candidates = new[] { TdxConstants.MarketSZ, TdxConstants.MarketSH, TdxConstants.MarketBJ };
        byte? foundMarket = null;

        foreach (var mkt in candidates)
        {
            try
            {
                var type = TdxConstants.GetSecurityType(mkt, codeOnly);
                if (!type.EndsWith("_A_STOCK")) continue;

                // 尝试拉取实时行情确认该代码在此市场真实存在
                var cmd = new GetSecurityQuotesCmd();
                cmd.SetParams([(mkt, codeOnly)]);
                var results = cmd.ParseResponse(client.SendPacket(cmd.BuildRequest()));
                if (results.Length > 0 && results[0].Price > 0)
                {
                    foundMarket = mkt;
                    break;
                }

                // 即使价格为0也可能是有效代码（停牌等），记下但不 break
                if (results.Length > 0)
                    foundMarket ??= mkt;
            }
            catch { /* 该市场查询失败，试下一个 */ }
        }

        if (foundMarket.HasValue)
            return (foundMarket.Value, codeOnly, StockCodeParser.NormalizeCode(foundMarket.Value, codeOnly), null);

        // 4. 仅用 GetSecurityType 快速判断（无需行情连接）
        foreach (var mkt in candidates)
        {
            try
            {
                var type = TdxConstants.GetSecurityType(mkt, codeOnly);
                if (type.EndsWith("_A_STOCK"))
                    return (mkt, codeOnly, StockCodeParser.NormalizeCode(mkt, codeOnly), null);
            }
            catch { }
        }

        return (0, "", "", $"代码 {input} 无法识别，请使用 sz/sh/bj 前缀指定交易所。如: sz{codeOnly}");
    }

    /// <summary>
    /// 获取单只股票实时报价。返回 null 表示获取失败。
    /// </summary>
    public async Task<QuoteResult?> GetQuoteAsync(byte market, string code)
    {
        var client = await Entry.ConnMgr!.EnsureConnectedAsync();
        if (client == null)
        {
            return null;
        }

        try
        {
            var cmd = new GetSecurityQuotesCmd();
            cmd.SetParams([(market, code)]);
            var results = cmd.ParseResponse(client.SendPacket(cmd.BuildRequest()));
            return results.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 批量获取多只股票实时报价。返回 Dictionary<normalizedCode, QuoteResult>。
    /// </summary>
    public async Task<Dictionary<string, QuoteResult>?> GetQuotesBatchAsync(List<(byte market, string code)> stocks)
    {
        var client = await Entry.ConnMgr!.EnsureConnectedAsync();
        if (client == null)
        {
            return null;
        }

        try
        {
            var cmd = new GetSecurityQuotesCmd();
            cmd.SetParams(stocks.Select(s => (s.market, s.code)).ToArray());
            var results = cmd.ParseResponse(client.SendPacket(cmd.BuildRequest()));

            var dict = new Dictionary<string, QuoteResult>();
            foreach (var r in results)
            {
                dict[StockCodeParser.NormalizeCode((byte)r.Market, r.Code)] = r;
            }

            return dict;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 判断股票代码是否为A股（非指数/基金/债券）。
    /// </summary>
    public static bool IsAStock(byte market, string code)
    {
        var type = TdxConstants.GetSecurityType(market, code);
        return type.EndsWith("_A_STOCK");
    }
}