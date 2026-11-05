using System.Text.Json;

namespace Hdlss.Core.Net;

public sealed record ReleaseAsset(string Name, string Url, string? Sha256, long Size);

public sealed record GitHubRelease(string Tag, bool Draft, bool Prerelease, DateTime Published, IReadOnlyList<ReleaseAsset> Assets)
{
    public static List<GitHubRelease> ParseList(JsonElement array) => array.EnumerateArray().Select(Parse).ToList();

    public static GitHubRelease Parse(JsonElement r)
    {
        var assets = new List<ReleaseAsset>();
        if (r.TryGetProperty("assets", out var list))
            foreach (var a in list.EnumerateArray())
            {
                string? digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                assets.Add(new ReleaseAsset(a.GetProperty("name").GetString() ?? "", a.GetProperty("browser_download_url").GetString() ?? "",
                    IO.FileHash.IsSha256(sha) ? sha : null, a.TryGetProperty("size", out var s) ? s.GetInt64() : 0));
            }
        DateTime.TryParse(r.TryGetProperty("published_at", out var p) ? p.GetString() : null, out var published);
        return new GitHubRelease(r.GetProperty("tag_name").GetString() ?? "",
            r.TryGetProperty("draft", out var dr) && dr.GetBoolean(),
            r.TryGetProperty("prerelease", out var pr) && pr.GetBoolean(), published, assets);
    }

    /// <summary>The asset must be served from this repository's release downloads.</summary>
    public static bool FromRepo(ReleaseAsset asset, string repo, string tag) =>
        asset.Url.StartsWith($"https://github.com/{repo}/releases/download/{Uri.EscapeDataString(tag)}/", StringComparison.Ordinal)
        || asset.Url.StartsWith($"https://github.com/{repo}/releases/download/{tag}/", StringComparison.Ordinal);
}

public static class GitHub
{
    public static string ReleasesUrl(string repo, int perPage = 15) => $"https://api.github.com/repos/{repo}/releases?per_page={perPage}";
    public static string LatestUrl(string repo) => $"https://api.github.com/repos/{repo}/releases/latest";
}
