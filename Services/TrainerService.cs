using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public class TrainerService : ITrainerService
{
    private readonly IHttpClientProvider _httpClientProvider;

    public TrainerService(IHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
    }

    public async Task<List<TrainerInfo>> GetHotTrainersAsync(int count = 10)
    {
        var result = new List<TrainerInfo>();
        var html = await FetchHomepageAsync();

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var items = doc.DocumentNode.SelectNodes("//div[contains(@class,'popular-posts')]//ul[contains(@class,'wpp-list')]/li");
        if (items == null) return result;

        foreach (var li in items.Take(count))
        {
            var trainer = ParseWppListItem(li);
            if (trainer != null) result.Add(trainer);
        }

        return result;
    }

    public async Task<List<TrainerInfo>> GetNewReleasesAsync(int count = 10)
    {
        var result = new List<TrainerInfo>();
        var html = await FetchHomepageAsync();

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var items = doc.DocumentNode.SelectNodes("//div[@id='rpwe_widget-4']//ul[contains(@class,'rpwe-ul')]/li");
        if (items == null) return result;

        foreach (var item in items.Take(count))
        {
            var titleLink = item.SelectSingleNode(".//h3[contains(@class,'rpwe-title')]/a");
            var imgNode = item.SelectSingleNode(".//a[contains(@class,'rpwe-img')]//img");

            var name = Decode(titleLink?.InnerText.Trim() ?? "");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var pageUrl = titleLink?.GetAttributeValue("href", "") ?? "";
            var coverUrl = imgNode?.GetAttributeValue("src", "") ?? "";

            result.Add(new TrainerInfo
            {
                GameName = StripTrainerSuffix(name),
                CoverUrl = coverUrl,
                PageUrl = pageUrl
            });
        }

        return result;
    }

    public async Task<List<TrainerInfo>> SearchTrainersAsync(string query)
    {
        var result = new List<TrainerInfo>();

        if (string.IsNullOrWhiteSpace(query)) return result;

        var url = $"https://flingtrainer.com/?s={Uri.EscapeDataString(query)}";
        var html = await _httpClientProvider.SendWithProxyRetryAsync(
            "trainer-search",
            TimeSpan.FromSeconds(15),
            client => client.GetStringAsync(url));

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var articles = doc.DocumentNode.SelectNodes("//article[contains(@class,'post-standard')]");
        if (articles == null) return result;

        foreach (var article in articles)
        {
            var titleNode = article.SelectSingleNode(".//h2[contains(@class,'post-title')]/a");
            var imgNode = article.SelectSingleNode(".//img[contains(@class,'wp-post-image')]");
            var dayNode = article.SelectSingleNode(".//div[contains(@class,'post-details-day')]");
            var monthNode = article.SelectSingleNode(".//div[contains(@class,'post-details-month')]");
            var yearNode = article.SelectSingleNode(".//div[contains(@class,'post-details-year')]");

            var name = Decode(titleNode?.InnerText.Trim() ?? "Unknown");
            var pageUrl = titleNode?.GetAttributeValue("href", "") ?? "";
            var coverUrl = imgNode?.GetAttributeValue("src", "") ?? "";

            var dateStr = "";
            if (dayNode != null && monthNode != null && yearNode != null)
                dateStr = $"{yearNode.InnerText.Trim()}.{GetMonthNumber(monthNode.InnerText.Trim())}.{dayNode.InnerText.Trim()}";

            result.Add(new TrainerInfo
            {
                GameName = StripTrainerSuffix(name),
                CoverUrl = coverUrl,
                PageUrl = pageUrl,
                UpdateDate = dateStr
            });

            if (result.Count >= 10) break;
        }

        return result;
    }

    /// <summary>智能搜索：支持中英文与中文简写。中文先经 Steam 商店解析出候选游戏，再取英文名去风灵月影官网搜索。</summary>
    public async Task<List<TrainerInfo>> SearchTrainersSmartAsync(string query)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return new List<TrainerInfo>();

        var hasChinese = trimmed.Any(c => c > 0x2E80);
        if (!hasChinese)
        {
            var direct = await SearchTrainersAsync(trimmed);
            return direct.Count > 0 ? direct : await SearchTrainersAsync(ShortenEnglish(trimmed));
        }

        var candidates = await ResolveChineseCandidatesAsync(trimmed);
        var result = new List<TrainerInfo>();
        foreach (var (appId, _) in candidates)
        {
            var en = await ResolveEnglishNameAsync(appId);
            if (string.IsNullOrWhiteSpace(en)) continue;

            var list = await SearchTrainersAsync(en);
            if (list.Count == 0)
                list = await SearchTrainersAsync(ShortenEnglish(en));

            foreach (var trainer in list)
                if (result.All(r => r.PageUrl != trainer.PageUrl))
                    result.Add(trainer);
            if (result.Count >= 10) break;
        }

        if (result.Count == 0)
            result = await SearchTrainersAsync(trimmed);

        return result;
    }

    /// <summary>中文简写 → Steam 商店候选（AppID + 中文名），支持“黑神话”“致命”这类模糊词。</summary>
    private async Task<List<(int AppId, string ZhName)>> ResolveChineseCandidatesAsync(string query)
    {
        var result = new List<(int AppId, string ZhName)>();
        var attempts = new List<string> { query };
        var normalized = NormalizeChinese(query);
        if (normalized != query) attempts.Add(normalized);

        foreach (var term in attempts)
        {
            try
            {
                var url = "https://store.steampowered.com/api/storesearch/?term=" +
                          Uri.EscapeDataString(term) + "&l=schinese&cc=cn";
                var json = await _httpClientProvider.SendWithProxyRetryAsync(
                    "trainer-steam-search",
                    TimeSpan.FromSeconds(15),
                    client => client.GetStringAsync(url));
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray().Take(6))
                    {
                        var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var id = item.TryGetProperty("id", out var i) && i.TryGetInt32(out var ai) ? ai : 0;
                        if (!string.IsNullOrWhiteSpace(name) && id > 0 && result.All(c => c.AppId != id))
                            result.Add((id, name));
                    }
                }
                if (result.Count > 0) break;
            }
            catch { }
        }
        return result;
    }

    /// <summary>AppID → 英文名：优先商店 API，失败再解析商店页面标题。</summary>
    private async Task<string?> ResolveEnglishNameAsync(int appId)
    {
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english";
            var json = await _httpClientProvider.SendWithProxyRetryAsync(
                "trainer-en-name",
                TimeSpan.FromSeconds(15),
                client => client.GetStringAsync(url));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(appId.ToString(), out var node) &&
                node.TryGetProperty("success", out var ok) && ok.GetBoolean() &&
                node.TryGetProperty("data", out var data) &&
                data.TryGetProperty("name", out var name))
            {
                var en = name.GetString();
                if (!string.IsNullOrWhiteSpace(en)) return en;
            }
        }
        catch { }

        try
        {
            var html = await _httpClientProvider.SendWithProxyRetryAsync(
                "trainer-en-title",
                TimeSpan.FromSeconds(15),
                client => client.GetStringAsync($"https://store.steampowered.com/app/{appId}/?l=english&cc=cn"));
            var m = Regex.Match(html, "<title>(.*?)</title>", RegexOptions.Singleline);
            if (m.Success)
            {
                var title = WebUtility.HtmlDecode(m.Groups[1].Value);
                title = Regex.Replace(title, "\\s+on Steam\\s*$", "", RegexOptions.IgnoreCase);
                title = Regex.Replace(title, "^Save \\d+% on ", "", RegexOptions.IgnoreCase);
                if (!string.IsNullOrWhiteSpace(title) &&
                    !title.Contains("Welcome to Steam", StringComparison.OrdinalIgnoreCase))
                    return title;
            }
        }
        catch { }
        return null;
    }

    private static string NormalizeChinese(string s) =>
        new(s.Where(c => !char.IsWhiteSpace(c) && c != '：' && c != ':' && c != '《' && c != '》' && c != '“' && c != '”').ToArray());

    private static string ShortenEnglish(string s)
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 2 ? string.Join(' ', words.Take(2)) : s;
    }
    public async Task<string?> GetDownloadUrlAsync(string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return null;

        try
        {
            var html = await _httpClientProvider.SendWithProxyRetryAsync(
                "trainer-page",
                TimeSpan.FromSeconds(15),
                client => client.GetStringAsync(pageUrl));

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var linkNode = doc.DocumentNode.SelectSingleNode("//a[contains(@class,'attachment-link')]");
            if (linkNode == null) return null;

            var href = linkNode.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(href)) return null;

            return href;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> FetchHomepageAsync()
    {
        return await _httpClientProvider.SendWithProxyRetryAsync(
            "trainer-home",
            TimeSpan.FromSeconds(15),
            client => client.GetStringAsync("https://flingtrainer.com/"));
    }

    private static TrainerInfo? ParseWppListItem(HtmlNode li)
    {
        var titleLink = li.SelectSingleNode(".//a[contains(@class,'wpp-post-title')]");
        var imgNode = li.SelectSingleNode(".//img[contains(@class,'wpp-thumbnail')]");

        var name = Decode(titleLink?.InnerText.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var pageUrl = titleLink?.GetAttributeValue("href", "") ?? "";
        var coverUrl = imgNode?.GetAttributeValue("src", "") ?? "";

        return new TrainerInfo
        {
            GameName = StripTrainerSuffix(name),
            CoverUrl = coverUrl,
            PageUrl = pageUrl
        };
    }

    private static string Decode(string html) => WebUtility.HtmlDecode(html).Replace('\u2019', '\'').Replace('\u2018', '\'');

    private static string StripTrainerSuffix(string name)
    {
        var suffix = " Trainer";
        return name.EndsWith(suffix) ? name[..^suffix.Length] : name;
    }

    private static string GetMonthNumber(string month)
    {
        return month.ToLower() switch
        {
            "jan" => "01", "feb" => "02", "mar" => "03", "apr" => "04",
            "may" => "05", "jun" => "06", "jul" => "07", "aug" => "08",
            "sep" => "09", "oct" => "10", "nov" => "11", "dec" => "12",
            _ => month
        };
    }
}
