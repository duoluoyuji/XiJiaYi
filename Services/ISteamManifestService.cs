namespace SteamLuaManager.Services;

public interface ISteamManifestService
{
    Dictionary<int, string> ParseMountedDepots(string acfPath);
    Task<string?> FetchLatestManifestIdAsync(int appId, int depotId);
    Task<(bool success, int count, string message)> EnsureManifestsCachedAsync(int appId, IProgress<string>? progress = null, CancellationToken ct = default);
}
