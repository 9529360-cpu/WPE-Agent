using System.Net;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace 币安量化机器人.Services.Agent;

public sealed record NewsResearchResult(IReadOnlyList<NewsEvidence> Items,IReadOnlyList<string> MissingSources,int SuccessfulSources,int FullTextDocuments);

public sealed partial class NewsResearchService
{
    private sealed record NewsFeedSource(string Source,string Url,string Reliability,string TrustedDomain);
    private const int MaximumRedirects=3;
    private const int MaximumFeedBytes=2_000_000;
    private const int MaximumArticleBytes=3_000_000;

    public static WpeAgent.FinancialEvidence.AuthorizedFixtureProjectionV1<NewsEvidence> CollectAuthorizedLocalFixtures(
        IReadOnlyList<WpeAgent.FinancialEvidence.FinancialEvidenceRecordV1>? fixtures,
        WpeAgent.FinancialEvidence.FinancialEvidenceRetrievalRequestV1 request)
    {
        var corpus = new WpeAgent.FinancialEvidence.AuthorizedLocalCorpusV1().Collect(fixtures, request);
        if (!corpus.Accepted) return new(false, [], corpus.ReasonCodes);
        try
        {
            var items = corpus.Records.Select(record =>
            {
                using var payload = System.Text.Json.JsonDocument.Parse(record.Draft.Payload);
                var root = payload.RootElement;
                var title = root.GetProperty("title").GetString();
                var summary = root.GetProperty("bodySummary").GetString();
                var reliability = root.GetProperty("reliability").GetString();
                var assets = root.GetProperty("affectedAssets").EnumerateArray().Select(x => x.GetString()).ToArray();
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary) || string.IsNullOrWhiteSpace(reliability) || assets.Any(string.IsNullOrWhiteSpace)) throw new System.Text.Json.JsonException();
                return new NewsEvidence(record.Draft.SourceProvider, title, record.Draft.SourceUriOrDatasetId, record.ObservedAt.UtcDateTime, record.Draft.RecordedAt.UtcDateTime, reliability, record.ContentHash, assets.Select(x => x!).Order(StringComparer.Ordinal).ToArray(), summary);
            }).OrderBy(x => x.Title, StringComparer.Ordinal).ToArray();
            return new(true, items, []);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(false, [], ["evidence.payload-malformed"]);
        }
    }

    private static readonly NewsFeedSource[] Feeds=
    [
        new("SEC","https://www.sec.gov/news/pressreleases.rss","official","sec.gov"),
        new("CFTC","https://www.cftc.gov/RSS/RSSENF.xml","official","cftc.gov"),
        new("Federal Reserve","https://www.federalreserve.gov/feeds/press_all.xml","official","federalreserve.gov"),
        new("ECB","https://www.ecb.europa.eu/rss/press.html","official","ecb.europa.eu"),
        new("CoinDesk","https://www.coindesk.com/arc/outboundfeeds/rss/","mainstream","coindesk.com"),
        new("Cointelegraph","https://cointelegraph.com/rss","mainstream","cointelegraph.com"),
        new("Google News","https://news.google.com/rss/search?q=Bitcoin%20OR%20Ethereum%20OR%20crypto%20regulation&hl=en-US&gl=US&ceid=US:en","aggregator","news.google.com")
    ];
    private readonly HttpClient _http;
    private readonly Dictionary<string, DateTime> _feedCooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    public NewsResearchService()
    {
        _http=new HttpClient(new HttpClientHandler{AutomaticDecompression=DecompressionMethods.All,AllowAutoRedirect=false,UseCookies=false}){Timeout=TimeSpan.FromSeconds(10)};
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WPE-Agent/2.0");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("(local-research-client)");
    }
    public async Task<NewsResearchResult> CollectAsync(IEnumerable<string> symbols,CancellationToken ct)
    {
        var raw=new List<NewsEvidence>();var missing=new List<string>();var successful=0;
        foreach(var feed in Feeds)
        {
            if (_feedCooldownUntil.TryGetValue(feed.Source, out var cooldown) && cooldown > DateTime.UtcNow)
            {
                missing.Add(feed.Source);
                continue;
            }
            try
            {
                await using var stream=await GetStreamWithRetryAsync(feed.Url,feed.TrustedDomain,ct);
                var readerSettings=new XmlReaderSettings{Async=true,DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaximumFeedBytes};
                using var reader=XmlReader.Create(stream,readerSettings);
                var doc=await XDocument.LoadAsync(reader,LoadOptions.None,ct);
                foreach(var item in doc.Descendants().Where(x=>x.Name.LocalName is "item" or "entry").Take(25))
                {
                    string Value(string name)=>item.Elements().FirstOrDefault(x=>x.Name.LocalName==name)?.Value?.Trim()??string.Empty;
                    var title=WebUtility.HtmlDecode(Value("title"));if(string.IsNullOrWhiteSpace(title))continue;
                    var link=Value("link");if(string.IsNullOrWhiteSpace(link))link=item.Elements().FirstOrDefault(x=>x.Name.LocalName=="link")?.Attribute("href")?.Value??string.Empty;
                    if(!string.IsNullOrWhiteSpace(link)&&!IsTrustedContentUri(link,feed.TrustedDomain))link=string.Empty;
                    DateTime? published=DateTimeOffset.TryParse(Value("pubDate")+Value("published")+Value("updated"),out var date)?date.UtcDateTime:null;
                    var summary=CleanHtml(Value("description")+" "+Value("summary")+" "+Value("content"),900);
                    raw.Add(new(feed.Source,title,link,published,DateTime.UtcNow,feed.Reliability,Hash(title),Assets(title+" "+summary,symbols),summary));
                }
                successful++;_feedCooldownUntil.Remove(feed.Source);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch
            {
                missing.Add(feed.Source);
                _feedCooldownUntil[feed.Source] = DateTime.UtcNow.AddMinutes(5);
            }
        }
        var candidates=raw.OrderByDescending(x=>x.PublishedAt).Take(35).ToArray();using var gate=new SemaphoreSlim(4);
        var enriched=await Task.WhenAll(candidates.Select(async item=>
        {
            if(string.IsNullOrWhiteSpace(item.Url)||item.Source=="Google News")return item;
            var source=Feeds.FirstOrDefault(x=>string.Equals(x.Source,item.Source,StringComparison.OrdinalIgnoreCase));
            if(source is null||!IsTrustedContentUri(item.Url,source.TrustedDomain))return item;
            await gate.WaitAsync(ct);
            try
            {
                var html=await GetTrustedStringAsync(item.Url,source.TrustedDomain,MaximumArticleBytes,ct);
                var text=ExtractArticle(html);
                return text.Length>item.BodySummary.Length?item with{BodySummary=text}:item;
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch{return item;}
            finally{gate.Release();}
        }));
        var clusters=Cluster(enriched);var output=new List<NewsEvidence>();foreach(var cluster in clusters)
        {
            var representative=cluster.OrderByDescending(x=>Reliability(x.Reliability)).ThenByDescending(x=>x.BodySummary.Length).First();var sources=cluster.Select(x=>x.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count();var ageHours=(DateTime.UtcNow-(representative.PublishedAt??representative.CollectedAt)).TotalHours;var confidence=Math.Clamp(.30+Reliability(representative.Reliability)*.35+Math.Min(3,sources-1)*.12+(representative.BodySummary.Length>200?.10:0)-(ageHours>48?.15:0),0,1);var combined=string.Join(" ",cluster.Select(x=>x.Title+" "+x.BodySummary));var type=EventType(combined);var sentiment=Sentiment(combined);var breaking=ageHours<=2&&(sources>=2||representative.Reliability=="official");output.Add(representative with{DuplicateGroup=Hash(string.Join('|',cluster.Select(x=>x.Title).Order())),Confidence=confidence,CorroboratingSources=sources,EventType=type,IsBreaking=breaking,Sentiment=sentiment});
        }
        return new(output.OrderByDescending(x=>x.IsBreaking).ThenByDescending(x=>x.Confidence).ThenByDescending(x=>x.PublishedAt).Take(50).ToArray(),missing,successful,output.Count(x=>x.BodySummary.Length>200));
    }

    internal static bool IsTrustedContentUri(string? candidate,string trustedDomain)
    {
        if(string.IsNullOrWhiteSpace(candidate)||string.IsNullOrWhiteSpace(trustedDomain)||!Uri.TryCreate(candidate,UriKind.Absolute,out var uri))return false;
        var host=uri.IdnHost.TrimEnd('.');var domain=trustedDomain.Trim().TrimEnd('.');
        return uri.Scheme==Uri.UriSchemeHttps&&uri.IsDefaultPort&&string.IsNullOrEmpty(uri.UserInfo)&&
            (string.Equals(host,domain,StringComparison.OrdinalIgnoreCase)||host.EndsWith("."+domain,StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Stream> GetStreamWithRetryAsync(string url,string trustedDomain,CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try{return new MemoryStream(await GetTrustedBytesAsync(url,trustedDomain,MaximumFeedBytes,ct),writable:false);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
                if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), ct);
            }
        }
        throw last ?? new HttpRequestException("Unable to fetch trusted news feed.");
    }

    private async Task<string> GetTrustedStringAsync(string url,string trustedDomain,int maximumBytes,CancellationToken ct)=>
        Encoding.UTF8.GetString(await GetTrustedBytesAsync(url,trustedDomain,maximumBytes,ct));

    private async Task<byte[]> GetTrustedBytesAsync(string url,string trustedDomain,int maximumBytes,CancellationToken ct)
    {
        if(!IsTrustedContentUri(url,trustedDomain))throw new HttpRequestException("News URI denied by source policy.");
        var current=new Uri(url,UriKind.Absolute);
        for(var redirects=0;redirects<=MaximumRedirects;redirects++)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,current);
            using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
            if((int)response.StatusCode is >=300 and <400)
            {
                if(redirects==MaximumRedirects||response.Headers.Location is null)throw new HttpRequestException("News redirect denied by source policy.");
                var next=response.Headers.Location.IsAbsoluteUri?response.Headers.Location:new Uri(current,response.Headers.Location);
                if(!IsTrustedContentUri(next.AbsoluteUri,trustedDomain))throw new HttpRequestException("News redirect left the trusted source domain.");
                current=next;continue;
            }
            response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength is long declared&&declared>maximumBytes)throw new HttpRequestException("News response exceeds the source byte limit.");
            await using var stream=await response.Content.ReadAsStreamAsync(ct);using var memory=new MemoryStream();var buffer=new byte[8192];
            while(true)
            {
                var read=await stream.ReadAsync(buffer,ct);if(read==0)break;
                if(memory.Length+read>maximumBytes)throw new HttpRequestException("News response exceeds the source byte limit.");
                memory.Write(buffer,0,read);
            }
            return memory.ToArray();
        }
        throw new HttpRequestException("News redirect limit exceeded.");
    }

    private static IReadOnlyList<List<NewsEvidence>> Cluster(IEnumerable<NewsEvidence> items)
    {
        var clusters=new List<List<NewsEvidence>>();foreach(var item in items){var target=clusters.FirstOrDefault(c=>Similarity(c[0].Title,item.Title)>=.32);if(target is null)clusters.Add([item]);else target.Add(item);}return clusters;
    }
    private static double Similarity(string left,string right){var a=Tokens(left);var b=Tokens(right);if(a.Count==0||b.Count==0)return 0;return a.Intersect(b).Count()/(double)a.Union(b).Count();}
    private static HashSet<string> Tokens(string value)=>WordRegex().Matches(value.ToLowerInvariant()).Select(x=>x.Value).Where(x=>x.Length>2&&!StopWords.Contains(x)).ToHashSet();
    private static readonly HashSet<string> StopWords=["the","and","for","with","from","that","this","will","has","have","are","was","crypto","bitcoin","ethereum"];
    private static IReadOnlyList<string> Assets(string text,IEnumerable<string> symbols){var result=new List<string>();foreach(var symbol in symbols){var asset=symbol.EndsWith("USDT",StringComparison.OrdinalIgnoreCase)?symbol[..^4]:symbol;if(text.Contains(asset,StringComparison.OrdinalIgnoreCase)||(asset=="BTC"&&text.Contains("bitcoin",StringComparison.OrdinalIgnoreCase))||(asset=="ETH"&&text.Contains("ethereum",StringComparison.OrdinalIgnoreCase)))result.Add(asset);}return result.Distinct().ToArray();}
    private static string EventType(string text){text=text.ToLowerInvariant();if(text.Contains("hack")||text.Contains("exploit")||text.Contains("breach"))return "SECURITY";if(text.Contains("sec ")||text.Contains("cftc")||text.Contains("regulat")||text.Contains("lawsuit"))return "REGULATION";if(text.Contains("etf"))return "ETF";if(text.Contains("federal reserve")||text.Contains("inflation")||text.Contains("interest rate")||text.Contains("cpi"))return "MACRO";if(text.Contains("exchange")||text.Contains("binance")||text.Contains("coinbase"))return "EXCHANGE";return "MARKET";}
    private static double Sentiment(string text){text=text.ToLowerInvariant();var positive=new[]{"approval","approved","surge","gain","record high","adoption","inflow","rally"};var negative=new[]{"hack","exploit","ban","lawsuit","outflow","crash","liquidation","fraud"};var score=positive.Count(text.Contains)-negative.Count(text.Contains);return Math.Clamp(score/3d,-1,1);}
    private static double Reliability(string value)=>value switch{"official"=>1,"mainstream"=>.75,"aggregator"=>.45,_=>.35};
    private static string ExtractArticle(string html){var meta=MetaDescriptionRegex().Match(html);var description=meta.Success?WebUtility.HtmlDecode(meta.Groups[1].Value):string.Empty;var paragraphs=string.Join(' ',ParagraphRegex().Matches(html).Select(x=>CleanHtml(x.Groups[1].Value,600)).Where(x=>x.Length>40).Take(8));return CleanHtml(description+" "+paragraphs,2400);}
    private static string CleanHtml(string value,int limit){value=ScriptRegex().Replace(value," ");value=TagRegex().Replace(value," ");value=WebUtility.HtmlDecode(value);value=WhitespaceRegex().Replace(value," ").Trim();return value.Length<=limit?value:value[..limit];}
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..20];
    [GeneratedRegex(@"[\p{L}\p{N}]+",RegexOptions.CultureInvariant)]private static partial Regex WordRegex();[GeneratedRegex("""<meta[^>]+(?:name|property)=["'](?:description|og:description)["'][^>]+content=["']([^"']+)""",RegexOptions.IgnoreCase|RegexOptions.Singleline)]private static partial Regex MetaDescriptionRegex();[GeneratedRegex(@"<p\b[^>]*>(.*?)</p>",RegexOptions.IgnoreCase|RegexOptions.Singleline)]private static partial Regex ParagraphRegex();[GeneratedRegex(@"<(script|style|noscript)\b[^>]*>.*?</\1>",RegexOptions.IgnoreCase|RegexOptions.Singleline)]private static partial Regex ScriptRegex();[GeneratedRegex(@"<[^>]+>",RegexOptions.Singleline)]private static partial Regex TagRegex();[GeneratedRegex(@"\s+")]private static partial Regex WhitespaceRegex();
}