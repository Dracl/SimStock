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
        var digitsOnly = System.Text.RegularExpressions.Regex.Match(input.Trim(), @"\d+").Value;
        if (digitsOnly.Length > 0)
        {
            var codeOnly = digitsOnly.PadLeft(6, '0');
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

            var markets = new[] { TdxConstants.MarketSZ, TdxConstants.MarketSH, TdxConstants.MarketBJ };
            byte? foundMarket = null;

            foreach (var mkt in markets)
            {
                try
                {
                    var type = TdxConstants.GetSecurityType(mkt, codeOnly);
                    if (!type.EndsWith("_A_STOCK")) continue;

                    var cmd = new GetSecurityQuotesCmd();
                    cmd.SetParams([(mkt, codeOnly)]);
                    var results = cmd.ParseResponse(client.SendPacket(cmd.BuildRequest()));
                    if (results.Length > 0 && results[0].Price > 0)
                    {
                        foundMarket = mkt;
                        break;
                    }

                    if (results.Length > 0)
                        foundMarket ??= mkt;
                }
                catch (Exception ex) { Entry.Api.Logger.Warn("行情服务", $"查询市场{mkt}股票{codeOnly}失败: {ex.Message}"); }
            }

            if (foundMarket.HasValue)
                return (foundMarket.Value, codeOnly, StockCodeParser.NormalizeCode(foundMarket.Value, codeOnly), null);

            // 4. 仅用 GetSecurityType 快速判断
            foreach (var mkt in markets)
            {
                try
                {
                    var type = TdxConstants.GetSecurityType(mkt, codeOnly);
                    if (type.EndsWith("_A_STOCK"))
                        return (mkt, codeOnly, StockCodeParser.NormalizeCode(mkt, codeOnly), null);
                }
                catch (Exception ex) { Entry.Api.Logger.Warn("行情服务", $"判断股票类型失败 {mkt}/{codeOnly}: {ex.Message}"); }
            }
        }

        // 5. 尝试用中文名称搜索
        var (exactMatch, candidates) = await Entry.StockNames.SearchByNameAsync(input);
        if (exactMatch != null)
        {
            var p = StockCodeParser.ParseNormalized(exactMatch);
            if (p.HasValue)
                return (p.Value.market, p.Value.code, exactMatch, null);
        }

        if (candidates.Count > 0)
        {
            var hints = string.Join("\n", candidates.Select(c => $"  {c.code}  {c.name}"));
            return (0, "", "", $"未找到与「{input}」匹配的股票，您是否要找:\n{hints}");
        }

        return (0, "", "", $"代码 {input} 无法识别，请使用 sz/sh/bj 前缀指定交易所，或输入股票中文名称");
    }

    /// <summary>
    /// 获取单只股票实时报价。返回 null 表示获取失败。
    /// </summary>
    public async Task<QuoteResult?> GetQuoteAsync(byte market, string code)
    {
        var client = await Entry.ConnMgr!.EnsureConnectedAsync();
        if (client == null)
        {
            Entry.Api.Logger.Warn("行情服务", "无法连接行情源，GetQuoteAsync 返回 null");
            return null;
        }

        try
        {
            var cmd = new GetSecurityQuotesCmd();
            cmd.SetParams([(market, code)]);
            var results = cmd.ParseResponse(client.SendPacket(cmd.BuildRequest()));
            if (results.Length == 0)
            {
                Entry.Api.Logger.Warn("行情服务", $"服务器返回空结果 (market={market}, code={code})");
            }
            return results.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("行情服务", $"获取单股行情失败: {ex.Message}");
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
            Entry.Api.Logger.Warn("行情服务", "无法连接行情源，GetQuotesBatchAsync 返回 null");
            return null;
        }

        try
        {
            var cmd = new GetSecurityQuotesCmd();
            cmd.SetParams(stocks.Select(s => (s.market, s.code)).ToArray());
            var results = cmd.ParseResponse(client.SendPacket(cmd.BuildRequest()));

            if (results.Length == 0)
            {
                Entry.Api.Logger.Warn("行情服务", $"服务器返回空结果，请求了 {stocks.Count} 只股票");
            }

            var dict = new Dictionary<string, QuoteResult>();
            foreach (var r in results)
            {
                dict[StockCodeParser.NormalizeCode((byte)r.Market, r.Code)] = r;
            }

            return dict;
        }
        catch (Exception ex)
        {
            Entry.Api.Logger.Warn("行情服务", $"批量获取行情失败: {ex.Message}");
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