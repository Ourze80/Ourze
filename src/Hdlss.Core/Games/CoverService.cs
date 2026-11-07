using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.IO;
using Hdlss.Core.Net;
using Hdlss.Core.Storage;
using Hdlss.Core.Text;

namespace Hdlss.Core.Games;

/// <summary>
/// Game covers. Priority: custom cover chosen by the user, the store's own artwork
/// (<see cref="Game.Cover"/>), the automatic cover found on the Steam store, else nothing (the UI shows the exe icon).
/// Files live in the data folder (<see cref="AppPaths.Covers"/>); nothing is ever written to a game folder.
/// </summary>
public sealed class CoverService
{
    internal const long MaxImageBytes = 5L << 20;
    internal static readonly TimeSpan NegativeTtl = TimeSpan.FromDays(7);

    private readonly AppPaths _paths;
    private readonly HttpService _http;
    private readonly Log _log;
    private readonly object _gate = new();

    public CoverService(AppPaths paths, HttpService http, Log log)
    {
        _paths = paths;
        _http = http;
        _log = log;
    }

    // ---- lookup ----

    /// <summary>Custom file, else the store's artwork, else the cached automatic cover, else null. No network.</summary>
    public string? Current(Game game)
    {
        try
        {
            return CustomFile(game) ?? StoreArt(game) ?? AutoFile(game);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Wide image for the game page banner: custom cover, else the store's hero, else the cached automatic hero, else null.</summary>
    public string? CurrentHero(Game game)
    {
        try
        {
            if (CustomFile(game) is { } custom) return custom;
            if (!string.IsNullOrEmpty(game.Hero) && File.Exists(game.Hero)) return game.Hero;
            return FindFile("auto-hero-" + SafeId(game.Id) + ".*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool HasCustom(Game game)
    {
        try { return CustomFile(game) != null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? StoreArt(Game game) =>
        !string.IsNullOrEmpty(game.Cover) && File.Exists(game.Cover) ? game.Cover : null;

    private static string CustomPattern(Game game) => "custom-" + SafeId(game.Id) + ".*";
    private static string AutoPattern(Game game) => "auto-" + SafeId(game.Id) + ".*";

    private string? CustomFile(Game game) => FindFile(CustomPattern(game));
    private string? AutoFile(Game game) => FindFile(AutoPattern(game));

    private string? FindFile(string pattern)
    {
        if (!Directory.Exists(_paths.Covers)) return null;
        return Directory.EnumerateFiles(_paths.Covers, pattern)
            .Where(f => ImageCheck.Extension(f) != null)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    /// <summary>Game ids can hold characters that are not valid in file names: keep letters and digits, add a hash.</summary>
    internal static string SafeId(string id)
    {
        var clean = new string(id.Where(char.IsAsciiLetterOrDigit).ToArray());
        if (clean.Length > 40) clean = clean[..40];
        var hash = FileHash.OfBytes(Encoding.UTF8.GetBytes(id))[..8];
        return clean.Length == 0 ? hash : $"{clean}-{hash}";
    }

    // ---- manual cover ----

    /// <summary>Validates <paramref name="imageFile"/> (PNG or JPEG, size cap) and stores a copy. Returns the stored path.</summary>
    public string SetCustom(Game game, string imageFile)
    {
        try
        {
            if (!File.Exists(imageFile)) throw new HdlssException(Tx.Get("cover.err.missing"));
            var limit = MaxImageBytes * 2;
            if (new FileInfo(imageFile).Length > limit) throw new HdlssException(Tx.Get("cover.err.size", limit >> 20));
            var bytes = File.ReadAllBytes(imageFile);
            var ext = ImageCheck.Validate(bytes);
            if (ext == null) throw new HdlssException(Tx.Get("cover.err.format"));
            return Store("custom-", game, bytes, ext);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HdlssException(Tx.Get("cover.err.io", ex.Message), ex);
        }
    }

    public void ClearCustom(Game game)
    {
        try
        {
            lock (_gate)
            {
                if (!Directory.Exists(_paths.Covers)) return;
                foreach (var f in Directory.EnumerateFiles(_paths.Covers, CustomPattern(game))) File.Delete(f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HdlssException(Tx.Get("cover.err.io", ex.Message), ex);
        }
    }

    private string Store(string prefix, Game game, byte[] bytes, string ext)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_paths.Covers);
            var stem = prefix + SafeId(game.Id);
            var target = Path.Combine(_paths.Covers, stem + ext);
            var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllBytes(temp, bytes);
            foreach (var old in Directory.EnumerateFiles(_paths.Covers, stem + ".*").Where(f => ImageCheck.Extension(f) != null && !f.Equals(target, StringComparison.OrdinalIgnoreCase)))
                File.Delete(old);
            MoveWithRetry(temp, target);
            return target;
        }
    }

    /// <summary>The target may be open in the UI for a moment: retry briefly before giving up.</summary>
    private static void MoveWithRetry(string temp, string target)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { File.Move(temp, target, overwrite: true); return; }
            catch (IOException) when (attempt < 3) { Thread.Sleep(100); }
            catch (Exception)
            {
                try { File.Delete(temp); } catch (IOException) { }
                throw;
            }
        }
    }

    // ---- automatic cover ----

    private sealed class Apps { public Dictionary<string, AppEntry> Items { get; set; } = []; }
    private sealed class AppEntry { public int AppId { get; set; } public bool HeroTried { get; set; } }

    private string AppsFile => Path.Combine(_paths.Covers, "apps.json");

    private AppEntry? LoadApp(Game game)
    {
        lock (_gate)
        {
            try { return JsonFile.Read<Apps>(AppsFile)?.Items.GetValueOrDefault(game.Id); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
        }
    }

    private void SaveApp(Game game, AppEntry entry)
    {
        lock (_gate)
        {
            try
            {
                Apps apps;
                try { apps = JsonFile.Read<Apps>(AppsFile) ?? new(); }
                catch (Exception ex) when (ex is IOException or JsonException) { apps = new(); }
                apps.Items[game.Id] = entry;
                JsonFile.Write(AppsFile, apps);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Debug($"Copertine: appid non scritto: {ex.Message}"); }
        }
    }

    private sealed class Misses { public Dictionary<string, Miss> Items { get; set; } = []; }
    private sealed class Miss { public DateTime When { get; set; } public string Key { get; set; } = ""; }

    private string MissFile => Path.Combine(_paths.Covers, "misses.json");

    private Misses LoadMisses()
    {
        try { return JsonFile.Read<Misses>(MissFile) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    private void SaveMisses(Misses misses)
    {
        try { JsonFile.Write(MissFile, misses); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Debug($"Copertine: cache negativa non scritta: {ex.Message}"); }
    }

    /// <summary>True when a lookup for these names failed within the last 7 days.</summary>
    internal bool IsNegativeCached(Game game, string key, DateTime now)
    {
        lock (_gate)
        {
            return LoadMisses().Items.TryGetValue(game.Id, out var miss) && miss.Key == key && now - miss.When < NegativeTtl;
        }
    }

    internal void RememberMiss(Game game, string key, DateTime when)
    {
        lock (_gate)
        {
            var misses = LoadMisses();
            misses.Items[game.Id] = new Miss { When = when, Key = key };
            SaveMisses(misses);
        }
    }

    /// <summary>
    /// Looks the cover up on the Steam store, honouring the caches. Never throws: network trouble returns null
    /// and is retried at the next start. No-op (returns <see cref="Current"/>) when custom or store art exists.
    /// </summary>
    public async Task<string?> ResolveAsync(Game game, CancellationToken cancel)
    {
        try
        {
            if (CustomFile(game) != null || StoreArt(game) != null) return Current(game);
            var existing = AutoFile(game);
            if (existing != null)
            {
                await EnsureHeroAsync(game, cancel);
                return existing;
            }

            var names = CoverMatcher.Candidates(game, ProductName(game.Exe));
            if (names.Count == 0) return null;
            var key = string.Join("|", names);
            if (IsNegativeCached(game, key, DateTime.Now)) return null;

            foreach (var name in names)
            {
                cancel.ThrowIfCancellationRequested();
                var appId = await SearchAsync(name, cancel);
                if (appId == null) continue;
                // Matched but no usable image: don't cache a miss, try again next start.
                var cover = await DownloadCoverAsync(game, appId.Value, cancel);
                if (cover != null)
                {
                    SaveApp(game, new AppEntry { AppId = appId.Value });
                    await EnsureHeroAsync(game, cancel);
                }
                return cover;
            }

            RememberMiss(game, key, DateTime.Now);
            _log.Debug($"Copertina: nessuna corrispondenza per {game.Name}");
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.Warn($"Copertina automatica non disponibile per {game.Name}", ex);
            return null;
        }
    }

    /// <summary>Downloads library_hero.jpg once per game (a missing hero is fine). Re-searches the app only if its id was not stored.</summary>
    private async Task EnsureHeroAsync(Game game, CancellationToken cancel)
    {
        try
        {
            if (FindFile("auto-hero-" + SafeId(game.Id) + ".*") != null) return;
            var entry = LoadApp(game);
            if (entry is { HeroTried: true }) return;
            if (entry == null)
            {
                int? appId = null;
                foreach (var name in CoverMatcher.Candidates(game, ProductName(game.Exe)))
                    if ((appId = await SearchAsync(name, cancel)) != null) break;
                if (appId == null) return;
                entry = new AppEntry { AppId = appId.Value };
            }
            var image = await DownloadImageAsync($"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{entry.AppId}/library_hero.jpg",
                $"{entry.AppId}-library_hero.jpg", cancel);
            if (image != null) Store("auto-hero-", game, image.Value.data, image.Value.ext);
            entry.HeroTried = true;
            SaveApp(game, entry);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Debug($"Hero non scaricato per {game.Name}: {ex.Message}");   // network trouble: retried next start
        }
    }

    private async Task<(byte[] data, string ext)?> DownloadImageAsync(string url, string fileName, CancellationToken cancel)
    {
        string? downloaded = null;
        try
        {
            downloaded = await _http.DownloadAsync(url, fileName, null, MaxImageBytes, null, cancel);
            var bytes = await File.ReadAllBytesAsync(downloaded, cancel);
            var ext = ImageCheck.Validate(bytes);
            return ext == null || bytes.Length > MaxImageBytes ? null : (bytes, ext);
        }
        catch (HdlssException ex)
        {
            _log.Debug($"Immagine non scaricata ({fileName}): {ex.Message}");
            return null;
        }
        finally
        {
            try { if (downloaded != null) File.Delete(downloaded); } catch (IOException) { }
        }
    }

    private static string? ProductName(string exe)
    {
        try
        {
            return !string.IsNullOrEmpty(exe) && File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductName : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>First Steam store result whose title matches <paramref name="name"/> well, else null. Network errors propagate.</summary>
    private async Task<int?> SearchAsync(string name, CancellationToken cancel)
    {
        var url = "https://store.steampowered.com/api/storesearch/?term=" + Uri.EscapeDataString(name) + "&l=english&cc=US";
        var json = await _http.GetTextAsync(url, cancel, TimeSpan.FromDays(1));
        return CoverMatcher.PickApp(json, name);
    }

    private async Task<string?> DownloadCoverAsync(Game game, int appId, CancellationToken cancel)
    {
        var baseUrl = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/";
        foreach (var file in new[] { "library_600x900.jpg", "header.jpg" })
        {
            cancel.ThrowIfCancellationRequested();
            string? downloaded = null;
            try
            {
                downloaded = await _http.DownloadAsync(baseUrl + file, $"{appId}-{file}", null, MaxImageBytes, null, cancel);
                var bytes = await File.ReadAllBytesAsync(downloaded, cancel);
                var ext = ImageCheck.Validate(bytes);
                if (ext == null || bytes.Length > MaxImageBytes) continue;
                return CustomFile(game) ?? Store("auto-", game, bytes, ext);   // the user may have picked one meanwhile
            }
            catch (HdlssException ex)
            {
                _log.Debug($"Copertina {file} non scaricata: {ex.Message}");
            }
            finally
            {
                try { if (downloaded != null) File.Delete(downloaded); } catch (IOException) { }
            }
        }
        return null;
    }
}

/// <summary>Name cleaning and matching between a local game and Steam store titles.</summary>
internal static partial class CoverMatcher
{
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "launcher", "game", "games", "start", "starter", "play", "run", "main", "app", "application", "client", "bootstrap",
        "setup", "install", "installer", "unrealgame", "ue4game", "ue5game", "unreal engine", "unrealengine", "shipping",
        "win64", "win32", "x64", "x86", "release", "binaries", "bin", "retail", "dx11", "dx12", "vulkan", "executable", "exe",
        "game launcher", "gamelauncher", "start game", "play game", "ue4", "ue5", "unity", "godot",
    };

    // Words that distinguish editions or packaging but not the game itself (apostrophes are removed before matching).
    private static readonly string[] EditionWords =
    [
        "game of the year", "directors cut", "edition", "definitive", "remastered", "remaster", "goty", "complete", "deluxe",
        "ultimate", "digital", "standard", "gold", "enhanced", "anniversary", "legendary", "premium", "collectors", "collector",
        "special", "hd", "the",
    ];

    [GeneratedRegex(@"[-_ ]*(win64|win32|wingdk|x64)?[-_ ]*shipping$", RegexOptions.IgnoreCase)]
    private static partial Regex ShippingSuffix();
    [GeneratedRegex(@"[-_ ]+(win64|win32|x64|x86)$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchSuffix();
    [GeneratedRegex(@"\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex ExeSuffix();

    /// <summary>Strips exe/shipping suffixes and surrounding junk.</summary>
    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = ExeSuffix().Replace(name.Trim(), "");
        s = ShippingSuffix().Replace(s, "");
        s = ArchSuffix().Replace(s, "");
        return s.Trim(' ', '-', '_', '.');
    }

    public static bool IsGeneric(string? name)
    {
        var clean = Clean(name);
        if (clean.Length < 3) return true;
        var n = Normalize(clean, dropEditions: false);
        return n.Length < 3 || Generic.Contains(n) || Generic.Contains(n.Replace(" ", ""));
    }

    /// <summary>Search names in order of trust: exe ProductName, game name, install folder name. Generic ones are skipped.</summary>
    public static List<string> Candidates(Game game, string? productName)
    {
        var result = new List<string>();
        var seen = new HashSet<string>();
        var folder = string.IsNullOrEmpty(game.Root) ? null : Path.GetFileName(game.Root.TrimEnd('\\', '/'));
        foreach (var raw in new[] { productName, game.Name, folder })
        {
            var clean = Clean(raw);
            if (IsGeneric(clean)) continue;
            if (seen.Add(Normalize(clean))) result.Add(clean);
        }
        return result;
    }

    /// <summary>Lower case, no accents or ™®©, punctuation to spaces, edition words dropped.</summary>
    public static string Normalize(string? text, bool dropEditions = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var decomposed = text.Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is '™' or '®' or '©' or '\'' or '’') continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        var joined = " " + string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)) + " ";
        if (dropEditions)
            foreach (var word in EditionWords)
            {
                var pattern = " " + word + " ";
                while (joined.Contains(pattern)) joined = joined.Replace(pattern, " ");
            }
        return string.Join(' ', joined.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Exact match after normalisation, or a very close one that keeps every number identical (so "2" never matches "3").</summary>
    public static bool IsMatch(string query, string title)
    {
        var a = Normalize(query);
        var b = Normalize(title);
        if (a.Length < 2 || b.Length < 2) return false;
        if (a == b || a.Replace(" ", "") == b.Replace(" ", "")) return true;
        if (!Numbers(a).SequenceEqual(Numbers(b))) return false;
        var max = Math.Max(a.Length, b.Length);
        if (max < 8) return false;
        var distance = Levenshtein(a, b);
        return distance <= 1 || 1.0 - (double)distance / max >= 0.92;
    }

    private static IEnumerable<string> Numbers(string s) => s.Split(' ').Where(w => w.Length > 0 && w.All(char.IsDigit));

    /// <summary>The app id of an exact title match in a Steam storesearch response, else of the first very close one, else null.</summary>
    public static int? PickApp(string json, string query)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
            int? close = null;
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) && type.GetString() is { } t && t != "app") continue;
                if (!item.TryGetProperty("name", out var name) || !item.TryGetProperty("id", out var id) || !id.TryGetInt32(out var appId)) continue;
                var title = name.GetString() ?? "";
                if (Normalize(title) == Normalize(query)) return appId;
                if (close == null && IsMatch(query, title)) close = appId;
            }
            return close;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

/// <summary>Image signature checks (PNG / JPEG only).</summary>
internal static class ImageCheck
{
    /// <summary>".png" or ".jpg" from the file's magic bytes, else null.</summary>
    public static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return ".png";
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        return null;
    }

    /// <summary>Like <see cref="Sniff"/> but also requires a readable header with width/height above zero and a proper end (JPEG EOI, PNG IEND).</summary>
    public static string? Validate(ReadOnlySpan<byte> b)
    {
        var ext = Sniff(b);
        return ext == ".png" ? (ValidPng(b) ? ext : null) : ext == ".jpg" ? (ValidJpeg(b) ? ext : null) : null;
    }

    private static bool ValidPng(ReadOnlySpan<byte> b)
    {
        // signature(8) + IHDR chunk (25 bytes) ... final IEND chunk (12 bytes).
        if (b.Length < 8 + 25 + 12) return false;
        if (!b.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        uint w = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b[16..]);
        uint h = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b[20..]);
        if (w == 0 || h == 0 || w > 65535 || h > 65535) return false;
        return b.Slice(b.Length - 8, 4).SequenceEqual("IEND"u8);
    }

    private static bool ValidJpeg(ReadOnlySpan<byte> b)
    {
        int end = b.Length;
        while (end > 0 && b[end - 1] == 0) end--;                     // some encoders pad with zeros
        if (end < 4 || b[end - 2] != 0xFF || b[end - 1] != 0xD9) return false;
        int i = 2;
        while (i + 4 <= end)
        {
            if (b[i] != 0xFF) return false;
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }                     // fill byte
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            int length = (b[i + 2] << 8) | b[i + 3];
            if (length < 2) return false;
            bool sof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (sof)
            {
                if (i + 9 > end) return false;
                int h = (b[i + 5] << 8) | b[i + 6];
                int w = (b[i + 7] << 8) | b[i + 8];
                return w > 0 && h > 0;
            }
            if (marker == 0xDA) return false;                          // scan reached without a frame header
            i += 2 + length;
        }
        return false;
    }

    public static string? Extension(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext is ".png" or ".jpg" ? ext : null;
    }
}
