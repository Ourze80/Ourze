using Hdlss.Core.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.IO;
using Hdlss.Core.Storage;

namespace Hdlss.Core.Net;

/// <summary>
/// HTTP access with: a single shared <see cref="HttpClient"/>, ETag-cached GitHub API calls
/// (conditional requests don't count against the 60/h anonymous rate limit), offline
/// fallback to the cached copy, bounded and resumable-safe downloads verified by SHA-256.
/// </summary>
public sealed class HttpService : IDisposable
{
    private readonly HttpClient _http;
    private readonly AppPaths _paths;
    private readonly Log _log;

    public HttpService(AppPaths paths, Log log, HttpMessageHandler? handler = null)
    {
        _paths = paths;
        _log = log;
        _http = handler == null ? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
                                : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HDLSS5", "2.0"));
    }

    /// <summary>The cache is only an optimisation: failing to write it must not fail the request.</summary>
    private void SaveCache(string file, CachedResponse value)
    {
        try { JsonFile.Write(file, value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Debug($"Cache HTTP non scritta: {ex.Message}"); }
    }

    private sealed class CachedResponse
    {
        public string Url { get; set; } = "";
        public string? ETag { get; set; }
        public DateTime Fetched { get; set; }
        public string Body { get; set; } = "";
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> _inFlight = new();

    /// <summary>GET text with ETag caching. Falls back to the cached body when offline or rate-limited.</summary>
    public Task<string> GetTextAsync(string url, CancellationToken cancel = default, TimeSpan? maxAge = null)
    {
        if (maxAge == null) return FetchTextAsync(url, cancel, null);
        // Cacheable reads (catalogs shown on every card) share one request while it is running:
        // several components checking at once would otherwise download and write the same file.
        var task = _inFlight.GetOrAdd(url, u =>
        {
            var t = FetchTextAsync(u, CancellationToken.None, maxAge);
            t.ContinueWith(_ => _inFlight.TryRemove(new KeyValuePair<string, Task<string>>(u, t)), TaskScheduler.Default);
            return t;
        });
        // A cache hit completes synchronously, before GetOrAdd stored it: don't keep it around.
        if (task.IsCompleted) _inFlight.TryRemove(new KeyValuePair<string, Task<string>>(url, task));
        return task.WaitAsync(cancel);
    }

    private async Task<string> FetchTextAsync(string url, CancellationToken cancel, TimeSpan? maxAge)
    {
        var cacheFile = Path.Combine(_paths.HttpCache, FileHash.OfBytes(System.Text.Encoding.UTF8.GetBytes(url))[..24] + ".json");
        var cached = JsonFile.Read<CachedResponse>(cacheFile);
        if (cached != null && maxAge != null && DateTime.Now - cached.Fetched < maxAge) return cached.Body;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (url.StartsWith("https://api.github.com/", StringComparison.Ordinal))
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        }
        if (cached?.ETag != null) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response.StatusCode == HttpStatusCode.NotModified && cached != null)
            {
                cached.Fetched = DateTime.Now;
                SaveCache(cacheFile, cached);
                return cached.Body;
            }
            if ((response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429) && url.Contains("api.github.com"))
            {
                if (cached != null) { _log.Warn($"GitHub: limite richieste raggiunto, uso la copia locale di {url}"); return cached.Body; }
                throw new HdlssException(Tx.Get("http.rateLimit"));
            }
            response.EnsureSuccessStatusCode();
            var body = await ReadBoundedAsync(response, 32L << 20, cancel);
            SaveCache(cacheFile, new CachedResponse { Url = url, ETag = response.Headers.ETag?.ToString(), Fetched = DateTime.Now, Body = body });
            _log.Debug($"GET {url} · {(int)response.StatusCode}");
            return body;
        }
        catch (HttpRequestException ex) when (cached != null)
        {
            _log.Warn($"Rete non disponibile per {new Uri(url).Host}, uso la copia locale", ex);
            return cached.Body;
        }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested && cached != null)
        {
            _log.Warn($"Timeout per {new Uri(url).Host}, uso la copia locale", ex);
            return cached.Body;
        }
    }

    public async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancel = default, TimeSpan? maxAge = null) =>
        JsonDocument.Parse(await GetTextAsync(url, cancel, maxAge));

    /// <summary>
    /// Downloads to the cache and returns the local file. When <paramref name="sha256"/> is
    /// known, a cached copy is reused and the download is rejected if it does not match.
    /// </summary>
    public async Task<string> DownloadAsync(string url, string fileName, string? sha256, long maxBytes,
        IProgress<OperationProgress>? progress, CancellationToken cancel = default)
    {
        fileName = PathGuard.Normalize(fileName).Replace('/', '_');
        string dir = sha256 != null && FileHash.IsSha256(sha256)
            ? Path.Combine(_paths.Downloads, sha256.ToLowerInvariant()[..16])
            : Path.Combine(_paths.Downloads, "unverified", FileHash.OfBytes(System.Text.Encoding.UTF8.GetBytes(url))[..16]);
        var target = Path.Combine(dir, fileName);
        if (sha256 != null && File.Exists(target) && FileHash.Equal(FileHash.OfFile(target), sha256))
        {
            File.SetLastAccessTime(target, DateTime.Now);
            progress?.Report(new(Tx.Get("http.localVerified", fileName), 100));
            return target;
        }
        Directory.CreateDirectory(dir);
        var temp = target + ".partial";
        _log.Info($"Download {url}");
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            long? length = response.Content.Headers.ContentLength;
            if (length > maxBytes) throw new HdlssException(Tx.Get("http.sizeOver", fileName, length / 1048576));
            await using (var input = await response.Content.ReadAsStreamAsync(cancel))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 17];
                long received = 0; int read; int lastPercent = -1;
                while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    received += read;
                    if (received > maxBytes) throw new HdlssException(Tx.Get("http.downloadOver", fileName));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    if (length > 0)
                    {
                        int percent = (int)(received * 100 / length.Value);
                        if (percent != lastPercent) { lastPercent = percent; progress?.Report(new(Tx.Get("http.downloading", fileName), percent)); }
                    }
                }
            }
            if (sha256 != null && !FileHash.Equal(FileHash.OfFile(temp), sha256))
                throw new HdlssException(Tx.Get("http.shaMismatch", fileName));
            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch (HttpRequestException ex)
        {
            throw new HdlssException(Tx.Get("http.downloadFailed", new Uri(url).Host, ex.Message), ex);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Least-recently-used trimming of the download cache.</summary>
    public void TrimDownloads(long limitBytes)
    {
        if (!Directory.Exists(_paths.Downloads)) return;
        var files = new DirectoryInfo(_paths.Downloads).EnumerateFiles("*", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastAccessTimeUtc).ToList();
        long total = 0;
        foreach (var f in files)
        {
            total += f.Length;
            if (total > limitBytes) { f.Delete(); _log.Debug($"Cache: rimosso {f.Name}"); }
        }
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, long max, CancellationToken cancel)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > max) throw new HdlssException(Tx.Get("http.responseTooBig"));
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    public void Dispose() => _http.Dispose();
}
