using Hdlss.Core.Games;
using Hdlss.Core.Storage;

namespace Hdlss.Core.Settings;

public sealed class ManualGameEntry
{
    public string Exe { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class AppSettings
{
    public string Language { get; set; } = "it";
    public List<string> ExtraSteamLibraries { get; set; } = [];
    public List<string> ExtraXboxLibraries { get; set; } = [];
    public List<ManualGameEntry> ManualGames { get; set; } = [];
    public List<string> HiddenGames { get; set; } = [];
    public Dictionary<string, GameProfile> Profiles { get; set; } = [];
    /// <summary>Folder of an HDLSS5 1.x installation (contains <c>data</c> and <c>nr-packages</c>).</summary>
    public string? LegacyAppDir { get; set; }
    public string? LastGameId { get; set; }
    /// <summary>Shows paths, options, every component, 1.x details and history.</summary>
    public bool AdvancedMode { get; set; }
    public int KeepJournalsPerGame { get; set; } = 10;
    public long DownloadCacheLimitMb { get; set; } = 2048;

    public GameProfile Profile(string gameId)
    {
        if (!Profiles.TryGetValue(gameId, out var p)) Profiles[gameId] = p = new GameProfile();
        return p;
    }

    public static AppSettings Load(AppPaths paths) => JsonFile.Read<AppSettings>(paths.SettingsFile) ?? new AppSettings();
    public void Save(AppPaths paths) => JsonFile.Write(paths.SettingsFile, this);
}
