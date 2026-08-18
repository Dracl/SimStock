using System.Text;
using TdxProtocol;
using TdxProtocol.Commands;

Console.OutputEncoding = Encoding.UTF8;
// TDX 股票简称使用 GBK；.NET 5+ 必须显式启用传统代码页。
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var endpoints = args.Length > 0
    ? args
    : ["117.34.114.14:7709", "220.178.55.71:7709"];

var snapshots = new List<ServerSnapshot>();
foreach (var endpoint in endpoints)
{
    var (ip, port) = ParseEndpoint(endpoint);
    snapshots.Add(ProbeServer(ip, port));
}

if (snapshots.Count >= 2)
{
    Console.WriteLine("\n=== 与首个服务器的证券列表差异 ===");
    var baseline = snapshots[0];
    foreach (var other in snapshots.Skip(1))
    {
        foreach (var market in new[] { TdxConstants.MarketSZ, TdxConstants.MarketSH })
        {
            if (!baseline.Markets.TryGetValue(market, out var baselineMarket)
                || !other.Markets.TryGetValue(market, out var otherMarket))
            {
                Console.WriteLine($"{MarketName(market)}: 至少一台服务器未完成读取，无法比较。");
                continue;
            }
            var missing = baselineMarket.Codes.Except(otherMarket.Codes).Order().ToArray();
            var extra = otherMarket.Codes.Except(baselineMarket.Codes).Order().ToArray();
            Console.WriteLine($"{MarketName(market)}: {other.Endpoint} 相比 {baseline.Endpoint} 缺少 {missing.Length} 条，多出 {extra.Length} 条");
            PrintSamples("  缺少", missing);
            PrintSamples("  多出", extra);
        }
    }
}

return;

static ServerSnapshot ProbeServer(string ip, int port)
{
    const int batchSize = 1000;
    var endpoint = $"{ip}:{port}";
    var markets = new Dictionary<byte, MarketSnapshot>();

    Console.WriteLine($"\n=== {endpoint} ===");
    using var client = new TdxClient();
    try
    {
        client.Connect(ip, port);
        Console.WriteLine("握手成功");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"连接或握手失败: {ex.GetType().Name}: {ex.Message}");
        return new ServerSnapshot(endpoint, markets);
    }

    foreach (var market in new[] { TdxConstants.MarketSZ, TdxConstants.MarketSH })
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var codeNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var pageSignatures = new HashSet<string>(StringComparer.Ordinal);
        var pageIssues = new List<string>();
        var total = 0;
        var received = 0;

        try
        {
            var countCmd = new GetSecurityCountCmd();
            countCmd.SetParams(market);
            total = countCmd.ParseResponse(client.SendPacket(countCmd.BuildRequest()));
            Console.WriteLine($"{MarketName(market)}: 服务端声明 {total} 条证券");

            for (var start = 0; start < total; start += batchSize)
            {
                var listCmd = new GetSecurityListCmd();
                listCmd.SetParams(market, checked((ushort)start));
                var page = listCmd.ParseResponse(client.SendPacket(listCmd.BuildRequest()));
                received += page.Length;

                var signature = string.Join('|', page.Select(x => x.Code));
                if (!pageSignatures.Add(signature))
                    pageIssues.Add($"start={start} 返回了与此前完全相同的一页");
                if (page.Length == 0)
                    pageIssues.Add($"start={start} 返回空页");
                else if (start + page.Length < total && page.Length != batchSize)
                    pageIssues.Add($"start={start} 仅返回 {page.Length} 条（期望 {batchSize}）");

                foreach (var stock in page)
                {
                    codes.Add($"{market}:{stock.Code}");
                    codeNames[stock.Code] = stock.Name;
                }

                var first = page.FirstOrDefault()?.Code ?? "-";
                var last = page.LastOrDefault()?.Code ?? "-";
                Console.WriteLine($"  start={start,5}: 返回 {page.Length,4}，范围 {first}..{last}");
            }
        }
        catch (Exception ex)
        {
            pageIssues.Add($"读取时异常: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine($"{MarketName(market)} 汇总: 接收 {received}，唯一代码 {codes.Count}，声明 {total}");
        foreach (var issue in pageIssues)
            Console.WriteLine($"  异常: {issue}");
        if (market == TdxConstants.MarketSH)
        {
            var target = codeNames.TryGetValue("688825", out var targetName)
                ? $"命中 688825 / {targetName}"
                : "未找到 688825";
            var changxin = codeNames
                .Where(x => x.Value.Contains("长鑫", StringComparison.Ordinal))
                .Select(x => $"{x.Key} / {x.Value}")
                .ToArray();
            Console.WriteLine($"目标检查: {target}");
            Console.WriteLine(changxin.Length == 0
                ? "名称检查: 未找到包含“长鑫”的证券"
                : $"名称检查: {string.Join(", ", changxin)}");

            try
            {
                var quoteCmd = new GetSecurityQuotesCmd();
                quoteCmd.SetParams([(TdxConstants.MarketSH, "688825")]);
                var quotes = quoteCmd.ParseResponse(client.SendPacket(quoteCmd.BuildRequest()));
                Console.WriteLine(quotes.Length == 0
                    ? "行情检查: 688825 返回空结果"
                    : $"行情检查: 命中 {quotes[0].Code}，现价 {quotes[0].Price:F2}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"行情检查: {ex.GetType().Name}: {ex.Message}");
            }
        }
        markets[market] = new MarketSnapshot(total, received, codes, codeNames, pageIssues);
    }

    return new ServerSnapshot(endpoint, markets);
}

static (string ip, int port) ParseEndpoint(string endpoint)
{
    var parts = endpoint.Split(':', 2);
    if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
        throw new ArgumentException($"服务器地址必须是 IP:端口，实际为: {endpoint}");
    return (parts[0], port);
}

static string MarketName(byte market) => market == TdxConstants.MarketSZ ? "深市" : "沪市";

static void PrintSamples(string label, string[] codes)
{
    if (codes.Length > 0)
        Console.WriteLine($"{label}: {string.Join(", ", codes.Take(20))}{(codes.Length > 20 ? " ..." : "")}");
}

record ServerSnapshot(string Endpoint, Dictionary<byte, MarketSnapshot> Markets);
record MarketSnapshot(
    int DeclaredCount,
    int ReceivedCount,
    HashSet<string> Codes,
    Dictionary<string, string> CodeNames,
    List<string> Issues);
