using System.Text.Json;

namespace SteamLuaManager.Services;

/// <summary>远程版本信息。</summary>
public sealed record UpdateInfo(string LatestVersion, string Notes, string ReleaseUrl, string? DownloadUrl);

public interface IUpdateService
{
    Task<UpdateInfo?> CheckLatestAsync(string url, CancellationToken cancellationToken = default);
}

public class UpdateService : IUpdateService
{
    private readonly IHttpClientProvider _httpClientProvider;

    public UpdateService(IHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
    }

    /// <summary>解析版本号，去掉常见的 v 前缀；无法解析返回 null。</summary>
    public static Version? TryParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var text = version.Trim().TrimStart('v', 'V');
        return Version.TryParse(text, out var parsed) ? parsed : null;
    }

    /// <summary>判断远端版本是否比本地版本新。</summary>
    public static bool IsNewer(string? latest, string current)
    {
        var remote = TryParseVersion(latest);
        if (remote == null) return false;
        var local = TryParseVersion(current);
        return local == null || remote > local;
    }

    public async Task<UpdateInfo?> CheckLatestAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var client = _httpClientProvider.GetClient("update-check", TimeSpan.FromSeconds(15));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XiJiaYi-Updater/1.0");
        var json = await client.GetStringAsync(url, cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // GitHub Releases API 格式（https://api.github.com/repos/owner/repo/releases/latest）
        if (root.TryGetProperty("tag_name", out var tagElement))
        {
            var version = tagElement.GetString();
            var releaseUrl = GetString(root, "html_url") ?? url;
            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var candidate = GetString(asset, "browser_download_url");
                    if (!string.IsNullOrEmpty(candidate))
                    {
                        downloadUrl = candidate;
                        break;
                    }
                }
            }
            return new UpdateInfo(
                NormalizeTagVersion(version),
                GetString(root, "body") ?? string.Empty,
                releaseUrl,
                downloadUrl);
        }

        // 通用 JSON 格式：{ "version": "1.5.0", "notes": "...", "url": "https://...", "downloadUrl": "..." }
        var ver = GetString(root, "version") ?? GetString(root, "latestVersion") ?? GetString(root, "latest");
        if (string.IsNullOrWhiteSpace(ver)) return null;

        return new UpdateInfo(
            NormalizeTagVersion(ver),
            GetString(root, "notes") ?? GetString(root, "body") ?? string.Empty,
            GetString(root, "url") ?? url,
            GetString(root, "downloadUrl"));
    }

    private static string NormalizeTagVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return string.Empty;
        return version.Trim().TrimStart('v', 'V');
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return null;
    }
}
