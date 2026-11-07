using Hdlss.Core.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hdlss.Core.Games;
using Hdlss.Core.IO;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

/// <summary>A component installed by another tool, recognised file by file.</summary>
public sealed record ForeignInstall(string ComponentId, string ComponentName, string Source, IReadOnlyList<ForeignFile> Files)
{
    public string Summary => $"{ComponentName} · {string.Join(", ", Files.Select(f => f.Path).Take(3))}{(Files.Count > 3 ? $" (+{Files.Count - 3})" : "")}";
    public int WithBackup => Files.Count(f => f.Backup != null);
}

/// <summary>One file of a foreign install; <see cref="Backup"/> is the original found next to it, if any.</summary>
public sealed record ForeignFile(string Path, string? Backup, bool KeepIfModified);

/// <summary>Something injected into the game that HDLSS5 recognises but cannot manage.</summary>
public sealed record UnknownInjector(string Path, string Description);

/// <summary>
/// Recognises installs made by other tools (DLSS 5 Autopilot, ReShade's installer, manual
/// OptiScaler/RenoDX copies…) and imports them: HDLSS5 records the files as its own without
/// touching the game, so they can then be verified, updated and removed from here.
/// </summary>
public static partial class ForeignInstalls
{
    // Files that belong to the game itself: never imported, so a removal can never delete them.
    [GeneratedRegex(@"(^|/)(nvngx_dlss[dg]?\.dll|sl\.[a-z0-9_.]+\.dll|nvngx\.dll)$", RegexOptions.IgnoreCase)] private static partial Regex GameOwned();
    [GeneratedRegex(@"^renodx-(?!dlss|mfg).*\.addon64$", RegexOptions.IgnoreCase)] private static partial Regex RenoDxHdrAddon();
    [GeneratedRegex(@"^renodx-dlss5.*\.addon64$", RegexOptions.IgnoreCase)] private static partial Regex RenoDxNrAddon();
    [GeneratedRegex(@"^(amd_fidelityfx_.*|libxess.*|libxell|fakenvapi|dlssg_to_fsr3_amd_is_better)\.dll$", RegexOptions.IgnoreCase)] private static partial Regex OptiScalerSidecar();

    private static readonly string[] BackupSuffixes = [".dlss5-autopilot-backup", ".dlss5kur-yedek", ".dlss5-installer-backup"];
    private static readonly string[] ProbeOrder =
    [
        ReShadeComponent.ComponentId, FeederComponent.ComponentId, RenoDxNrComponent.ComponentId, OptiScalerComponent.ComponentId,
        NrModelComponent.ComponentId, MfgUnlockComponent.ComponentId, RenoDxHdrComponent.ComponentId, AsiLoaderComponent.ComponentId,
        ReFrameworkComponent.ComponentId, Ue4ssComponent.ComponentId,
    ];

    /// <summary>Foreign installs that can be imported, skipping components already managed or adoptable from 1.x.</summary>
    public static List<ForeignInstall> Detect(GameContext ctx, IReadOnlySet<string> skipComponents)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ctx.State.Components.Values) foreach (var f in c.Files) claimed.Add(f.Path);
        var autopilot = AutopilotFiles(ctx);
        var result = new List<ForeignInstall>();
        foreach (var id in ProbeOrder)
        {
            if (ctx.Installed(id) || skipComponents.Contains(id)) continue;
            var files = Candidates(ctx, id)
                .Select(PathGuard.Normalize).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => !claimed.Contains(p) && !GameOwned().IsMatch(p) && File.Exists(PathGuard.Resolve(ctx.Folder, p)))
                .ToList();
            if (files.Count == 0) continue;
            foreach (var f in files) claimed.Add(f);
            var source = files.Any(autopilot.Contains) ? Tx.Get("imp.src.autopilot")
                : Directory.Exists(Path.Combine(ctx.Game.Root, "_DLSS5_Backup")) && id != ReShadeComponent.ComponentId ? Tx.Get("imp.src.installer")
                : id == ReShadeComponent.ComponentId ? Tx.Get("imp.src.reshade")
                : Tx.Get("imp.src.other");
            result.Add(new ForeignInstall(id, ComponentCatalog.NameOf(id), source,
                files.Select(f => new ForeignFile(f, FindBackup(ctx, f), f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))).ToList()));
        }
        return result;
    }

    /// <summary>Injected files nobody (HDLSS5 or a known component) accounts for: possible conflicts.</summary>
    public static List<UnknownInjector> Unknown(GameContext ctx, IEnumerable<ForeignInstall> known, IEnumerable<string> legacyFiles)
    {
        var accounted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ctx.State.Components.Values) foreach (var f in c.Files) accounted.Add(f.Path);
        foreach (var k in known) foreach (var f in k.Files) accounted.Add(f.Path);
        foreach (var f in legacyFiles) accounted.Add(PathGuard.Normalize(f));
        var list = new List<UnknownInjector>();
        foreach (var addon in ctx.Scan.Addons.Where(a => !accounted.Contains(a)))
            list.Add(new UnknownInjector(addon, Tx.Get("imp.unk.addon")));
        foreach (var name in FolderScan.ProxyNames.Concat(["dinput8.dll", "d3d11.dll", "d3d9.dll", "opengl32.dll", "dwmapi.dll", "nvngx.dll"]).Distinct())
        {
            if (accounted.Contains(name)) continue;
            var full = PathGuard.Resolve(ctx.Folder, name);
            if (!File.Exists(full)) continue;
            var id = PeFile.Identity(full);
            if (id.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) && !id.Contains("ReShade", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new UnknownInjector(name, id.Length > 0 ? Tx.Get("imp.unk.proxyId", id) : Tx.Get("imp.unk.proxy")));
        }
        foreach (var file in ExecutableDetector.SafeFiles(ctx.Folder, "dlss-enabler*.dll"))
            list.Add(new UnknownInjector(Path.GetFileName(file), "DLSS Enabler"));
        return list;
    }

    /// <summary>Import = record ownership. No game file is written; the plan only stores the record.</summary>
    public static ChangePlan PlanImport(GameContext ctx, ForeignInstall install)
    {
        var record = new ComponentRecord
        {
            Id = install.ComponentId, Name = install.ComponentName,
            Version = Version(ctx, install) is { } v ? $"{v} (importato)" : "importato",
            Meta = new() { ["imported"] = install.Source },
        };
        var notes = new List<string>();
        foreach (var f in install.Files)
        {
            var full = PathGuard.Resolve(ctx.Folder, f.Path);
            string? before = null;
            if (f.Backup != null && File.Exists(f.Backup) && new FileInfo(f.Backup).Length > 0) before = ctx.Services.Store.Put(f.Backup);
            record.Files.Add(new FileRecord { Path = f.Path, Before = before, After = FileHash.OfFile(full), KeepIfModified = f.KeepIfModified });
            notes.Add(before != null ? Tx.Get("imp.noteBackup", f.Path, install.Source)
                : f.KeepIfModified ? Tx.Get("imp.noteConfig", f.Path)
                : Tx.Get("imp.noteAdded", f.Path, install.Source));
        }
        var plan = new ChangePlan
        {
            GameId = ctx.Game.Id, GameName = ctx.Game.Name, Folder = ctx.Folder, ComponentId = install.ComponentId,
            ComponentName = install.ComponentName + " · importazione", Version = record.Version, StateStamp = ctx.StateStamp, Adopt = record,
        };
        plan.Notes.Add(Tx.Get("imp.noteMain", install.Source, install.Files.Count));
        plan.Notes.AddRange(notes);
        return plan;
    }

    private static IEnumerable<string> Candidates(GameContext ctx, string id)
    {
        var s = ctx.Scan;
        bool feed = s.Addons.Any(a => Path.GetFileName(a).Equals(FeederComponent.FeedAddon, StringComparison.OrdinalIgnoreCase));
        bool renoNr = s.Addons.Any(a => RenoDxNrAddon().IsMatch(Path.GetFileName(a)));
        switch (id)
        {
            case ReShadeComponent.ComponentId when s.ReShadeLoaders.Count == 1:
                yield return s.ReShadeLoaders[0];
                if (s.ReShadeLoaders[0] == Vulkan.ReShadeDll) yield return Vulkan.ReShadeJson;
                break;
            case FeederComponent.ComponentId when feed:
                foreach (var a in s.Addons.Where(a => Path.GetFileName(a).Equals(FeederComponent.FeedAddon, StringComparison.OrdinalIgnoreCase) || RenoDxNrAddon().IsMatch(Path.GetFileName(a))))
                    yield return a;
                yield return "nvngx_dlssnr.dll";
                yield return Vulkan.FeedDll;
                yield return Vulkan.FeedJson;
                break;
            case RenoDxNrComponent.ComponentId when !feed && renoNr:
                foreach (var a in s.Addons.Where(a => RenoDxNrAddon().IsMatch(Path.GetFileName(a)))) yield return a;
                yield return "nvngx_dlssnr.dll";
                break;
            case OptiScalerComponent.ComponentId when s.OptiScalerProxies.Count == 1:
                yield return s.OptiScalerProxy!;
                yield return "OptiScaler.ini";
                foreach (var f in ExecutableDetector.SafeFiles(ctx.Folder, "*.dll").Where(f => OptiScalerSidecar().IsMatch(Path.GetFileName(f))))
                    yield return Path.GetFileName(f);
                var sub = Path.Combine(ctx.Folder, "OptiScaler");
                if (Directory.Exists(sub))
                    foreach (var f in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories)) yield return PathGuard.RelativeTo(ctx.Folder, f);
                if (!feed && !renoNr) yield return "nvngx_dlssnr.dll";
                break;
            case NrModelComponent.ComponentId:
                yield return "nvngx_dlssnr.dll";
                break;
            case MfgUnlockComponent.ComponentId:
                foreach (var a in s.Addons.Where(a => Path.GetFileName(a).Equals(MfgUnlockComponent.AddonName, StringComparison.OrdinalIgnoreCase))) yield return a;
                break;
            case RenoDxHdrComponent.ComponentId:
                foreach (var a in s.Addons.Where(a => RenoDxHdrAddon().IsMatch(Path.GetFileName(a)))) yield return a;
                break;
            case AsiLoaderComponent.ComponentId:
                var asi = AsiLoaderComponent.Present(ctx);
                if (asi.Count == 1) yield return asi[0];
                break;
            case ReFrameworkComponent.ComponentId:
                var dinput = PathGuard.Resolve(ctx.Folder, "dinput8.dll");
                if (File.Exists(dinput) && ReFrameworkComponent.Recognized(dinput)) yield return "dinput8.dll";
                break;
            case Ue4ssComponent.ComponentId:
                foreach (var f in new[] { "UE4SS.dll", "ue4ss/UE4SS.dll" })
                    if (File.Exists(PathGuard.Resolve(ctx.Folder, f))) { yield return f; yield return "dwmapi.dll"; }
                break;
        }
    }

    /// <summary>Original file saved by the other tool: "&lt;file&gt;.&lt;suffix&gt;" or _DLSS5_Backup/originals/*/&lt;path&gt;.</summary>
    internal static string? FindBackup(GameContext ctx, string relative)
    {
        var full = PathGuard.Resolve(ctx.Folder, relative);
        foreach (var suffix in BackupSuffixes)
            if (File.Exists(full + suffix) && new FileInfo(full + suffix).Length > 0) return full + suffix;
        var originals = Path.Combine(ctx.Game.Root, "_DLSS5_Backup", "originals");
        if (!Directory.Exists(originals)) return null;
        var fromRoot = Path.GetRelativePath(ctx.Game.Root, full);
        return ExecutableDetector.SafeDirs(originals)
            .Select(d => Path.Combine(d, fromRoot))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
    }

    /// <summary>Files listed by DLSS 5 Autopilot's own record, when present.</summary>
    private static HashSet<string> AutopilotFiles(GameContext ctx)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var marker = Path.Combine(ctx.Folder, "dlss5-autopilot.json");
        if (!File.Exists(marker)) return set;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                foreach (var f in files.EnumerateArray())
                    if (f.GetString() is { } name)
                        try { set.Add(PathGuard.Normalize(name)); } catch (UnsafePathException) { }
        }
        catch (JsonException) { }
        return set;
    }

    private static string? Version(GameContext ctx, ForeignInstall install)
    {
        var dll = install.Files.Select(f => f.Path).FirstOrDefault(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase));
        if (dll == null) return null;
        var v = PeFile.FileVersion(PathGuard.Resolve(ctx.Folder, dll));
        return v == null || v == new System.Version(0, 0, 0, 0) ? null : v.ToString();
    }
}
