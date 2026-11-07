using Hdlss.Core.Text;
using System.Text.RegularExpressions;
using Hdlss.Core.IO;

namespace Hdlss.Core.Components;

public enum VerifyLevel { NotRun, Working, Partial, Failed }

public sealed record VerifyResult(VerifyLevel Level, string Title, IReadOnlyList<string> Details);

/// <summary>
/// "Did it work?": reads the logs the game wrote after the last install and says in plain
/// words whether the Neural Rendering pass actually ran, and what to try if it did not.
/// </summary>
public static class Verifier
{
    public static VerifyResult Check(GameContext ctx)
    {
        var path = NrAdvisor.Installed(ctx);
        if (path == null) return new(VerifyLevel.NotRun, Tx.Get("ver.notInstalled"), []);
        var record = ctx.State.Get(NrAdvisor.Components[path.Value].Last())!;
        var since = record.UpdatedAt.AddMinutes(-1);
        var details = new List<string>();

        // A crash report written after the install outranks everything else.
        var crash = ExecutableDetectorFiles(ctx.Folder, "*crash*.log").Select(f => new FileInfo(f))
            .Where(f => f.LastWriteTime >= since).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        if (crash != null)
            return new(VerifyLevel.Failed, Tx.Get("ver.crash"),
                [Tx.Get("ver.crashReport", crash.Name, crash.LastWriteTime),
                 path == NrPath.Native ? Tx.Get("ver.crashNative") : Tx.Get("ver.crashOther")]);

        var logs = new List<(string Name, string[] Lines)>();
        foreach (var name in LogNames(ctx, path.Value))
        {
            var file = PathGuard.Resolve(ctx.Folder, name);
            if (!File.Exists(file) || File.GetLastWriteTime(file) < since) continue;
            logs.Add((name, ReadShared(file)));
        }
        if (logs.Count == 0)
            return new(VerifyLevel.NotRun, Tx.Get("ver.notPlayed"),
                [Tx.Get("ver.notPlayedHint")]);

        bool loaded = false, ran = false, failed = false;
        foreach (var (name, lines) in logs)
        {
            foreach (var line in lines)
            {
                if (Regex.IsMatch(line, @"(Loaded|Registered).*(renodx-dlss5|dlss5-feed|Neural Rendering|DLSS ?5)", RegexOptions.IgnoreCase)) loaded = true;
                if (Regex.IsMatch(line, @"feature ready|frame delivered|ENGAGED|DlssNr.*(created|init(ialized)?\b.*(ok|success))|Neural Rendering (active|enabled|ready)", RegexOptions.IgnoreCase)) ran = true;
                if (Regex.IsMatch(line, @"(failed to (load|create|initialize)).*(dlss|ngx|neural|add-?on)|NVSDK_NGX_Result_Fail|access violation", RegexOptions.IgnoreCase))
                {
                    failed = true;
                    if (details.Count < 4) details.Add($"{name}: {line.Trim()[..Math.Min(160, line.Trim().Length)]}");
                }
            }
        }

        if (ran && !failed) return new(VerifyLevel.Working, Tx.Get("ver.ok"), [Tx.Get("ver.okHint")]);
        if (failed)
        {
            details.Insert(0, Tx.Get("ver.logError"));
            details.Add(path == NrPath.Native
                ? Tx.Get("ver.errNative")
                : Tx.Get("ver.errOther"));
            return new(VerifyLevel.Failed, Tx.Get("ver.loadedErr"), details);
        }
        if (loaded) return new(VerifyLevel.Partial, Tx.Get("ver.partial"),
            [Tx.Get("ver.partialHint1"),
             path == NrPath.Feeder ? Tx.Get("ver.partialFeeder") : Tx.Get("ver.partialNative")]);
        return new(VerifyLevel.Failed, Tx.Get("ver.notLoaded"),
            [Tx.Get("ver.notLoadedHint1"),
             Tx.Get("ver.notLoadedHint2")]);
    }

    private static string[] ExecutableDetectorFiles(string dir, string pattern) => Games.ExecutableDetector.SafeFiles(dir, pattern);

    private static IEnumerable<string> LogNames(GameContext ctx, NrPath path)
    {
        yield return "ReShade.log";
        foreach (var loader in ctx.Scan.ReShadeLoaders.Where(l => !l.Contains('/')))
            yield return Path.ChangeExtension(loader, ".log");
        if (path == NrPath.Feeder) yield return "dlss5-feed.log";
        if (path == NrPath.OptiScaler) yield return "OptiScaler.log";
    }

    private static string[] ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(Math.Max(0, stream.Length - 400_000), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n');
    }
}
