using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface ITrainerService
{
    Task<List<TrainerInfo>> GetHotTrainersAsync(int count = 10);
    Task<List<TrainerInfo>> GetNewReleasesAsync(int count = 10);
    Task<List<TrainerInfo>> SearchTrainersAsync(string query);
    Task<List<TrainerInfo>> SearchTrainersSmartAsync(string query);
    Task<string?> GetDownloadUrlAsync(string pageUrl);
    Task<string> DownloadTrainerFileAsync(string pageUrl, string downloadUrl, string targetDir, IProgress<double>? progress = null, CancellationToken ct = default);
}
