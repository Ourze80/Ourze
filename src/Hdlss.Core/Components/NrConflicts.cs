using System.Text.RegularExpressions;
using Hdlss.Core.IO;
using Hdlss.Core.Text;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Components;

/// <summary>
/// Finds and fixes DLSS 5 NR add-on conflicts: more than one NR add-on loaded at once, a Feeder / Lumenite
/// next to DLSS5 NR 12, and ReShade early-load entries for add-ons that are gone.
///
/// Where ReShade looks for add-ons: its own folder, <c>[ADDON] AddonPath</c>, and the <c>_storage_</c>
/// mirror. <c>_storage_</c> is made by REFramework (RE Engine games, e.g. Dragon's Dogma 2): at every start
/// it copies the loaded DLLs and add-ons there and loads the copies. It never deletes, so after removing an
/// add-on from the game folder an old copy stays in <c>_storage_</c> and is still loaded.
/// </summary>
public static class NrConflicts
{
    public const string MirrorFolder = "_storage_";
    public const string CleanupId = "compat-nr";

    private static readonly Regex NrName = new(@"^(renodx-dlss5[^\\/]*|renodx-dlss|dlss5-nr[^\\/]*)\.addon64$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FeederName = new(@"^(dlss5-feed[^\\/]*|[^\\/]*lumenite[^\\/]*)\.addon64$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record LoadFolder(string Prefix, string Dir, bool Mirror, string? Config);

    private sealed record Found(string Path, string Full, string Name, LoadFolder Folder, bool IsNr, bool IsFeeder, bool Is12, Version Version, string? Owner)
    {
        public string Shown => Folder.Mirror ? Tx.Get("compat.nrCopy", Name) : Folder.Prefix.Length == 0 ? Name : Path;
        public string ShownVersion => Is12 ? Shown + " (" + NrAddonInfo.Format(Version) + ")" : Shown;
    }

    private sealed record Stale(LoadFolder Folder, string Item, bool Missing);

    private sealed class Analysis
    {
        public List<LoadFolder> Folders { get; } = [];
        public Dictionary<string, string> Roots { get; } = [];
        public List<Found> Nr { get; } = [];
        public List<Found> Feeders { get; } = [];
        public Found? Keep { get; set; }
        public List<Found> Remove { get; } = [];
        /// <summary>To remove but owned by another HDLSS component: left alone, the user removes them from that card.</summary>
        public List<Found> Blocked { get; } = [];
        public List<Stale> StaleEntries { get; } = [];
        public bool Has12 => Nr.Any(n => n.Is12);
    }

    public static List<CompatIssue> Check(GameContext ctx)
    {
        var a = Analyze(ctx);
        var issues = new List<CompatIssue>();
        var nrExtra = a.Remove.Where(f => f.IsNr).ToList();
        if (nrExtra.Count > 0 && a.Keep != null)
        {
            var list = string.Join(", ", nrExtra.Select(f => f.Shown));
            issues.Add(new(Tx.Get(a.Nr.Count == 2 ? "compat.nrTwice" : "compat.nrMany", list, a.Keep.ShownVersion), true, CompatKind.NrAddons));
        }
        var feeders = a.Remove.Where(f => f.IsFeeder).ToList();
        if (feeders.Count > 0)
            issues.Add(new(Tx.Get("compat.nrFeeder", string.Join(", ", feeders.Select(f => f.Shown))), true, CompatKind.NrAddons));
        if (a.Blocked.Any(IsHdlssFeeder))
            issues.Add(new(Tx.Get("compat.nrFeederOwned"), true, CompatKind.NrAddons));
        foreach (var g in a.Blocked.Where(b => !IsHdlssFeeder(b)).GroupBy(b => b.Owner!))
            issues.Add(new(Tx.Get("compat.nrOwned", string.Join(", ", g.Select(f => f.Shown)), ComponentCatalog.NameOf(g.Key)), false, CompatKind.NrAddons));
        // Entries of add-ons being removed are cleaned together with them; only really missing files are an issue of their own.
        if (a.StaleEntries.Any(s => s.Missing))
            issues.Add(new(Tx.Get("compat.nrStale", string.Join(", ", a.StaleEntries.Where(s => s.Missing).Select(s => s.Item).Distinct(StringComparer.OrdinalIgnoreCase))), true, CompatKind.NrAddons));
        return issues;
    }

    /// <summary>
    /// One plan that keeps a single NR add-on (the newest DLSS5 NR 12, else the one HDLSS manages, else the newest),
    /// removes the other NR add-ons and the Feeder / Lumenite leftovers, and cleans ReShade's early-load list.
    /// Nothing but NR / Feeder add-ons and that list is touched. Null when there is nothing to fix.
    /// </summary>
    public static ChangePlan? PlanFix(GameContext ctx)
    {
        var a = Analyze(ctx);
        if (a.Remove.Count == 0 && a.StaleEntries.Count == 0) return null;
        var store = ctx.Services.Store;
        var plan = new ChangePlan
        {
            GameId = ctx.Game.Id, GameName = ctx.Game.Name, Folder = ctx.Folder, ComponentId = CleanupId,
            ComponentName = Tx.Get("compat.nrFixName"), StateStamp = ctx.StateStamp, Cleanup = true,
            Roots = new(a.Roots),
        };
        foreach (var root in a.Roots.Values) Locations.ValidateRoot(root, ctx.Game.Root);

        var removedNames = new Dictionary<LoadFolder, HashSet<string>>();
        foreach (var f in a.Remove)
        {
            var current = FileHash.OfFileOrNull(f.Full);
            if (current == null) continue;
            // Always deleted, never "restored": a put-back original would be another NR add-on and a second round.
            plan.Files.Add(new FileOp { Path = f.Path, NewHash = null, Expected = current, Release = f.Owner != null });
            if (f.Owner != null) plan.Disown.Add(f.Path);
            if (!removedNames.TryGetValue(f.Folder, out var set)) removedNames[f.Folder] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(f.Name);
        }

        // Early-load list: drop the removed add-ons and the NR / Feeder ones that do not exist any more.
        foreach (var folder in a.Folders.Where(f => f.Config != null))
        {
            var cfgPath = folder.Prefix + folder.Config;
            var full = Locations.Resolve(ctx.Folder, a.Roots, cfgPath);
            if (!File.Exists(full)) continue;
            var doc = IniDocument.Load(full);
            var original = FileHash.OfFile(full);
            bool changed = false;
            foreach (var item in IniDocument.SplitList(doc.Get("ADDON", "LoadFromDllMain")))
                if (IsManagedName(item) && (a.Remove.Any(r => r.Folder == folder && r.Name.Equals(item, StringComparison.OrdinalIgnoreCase))
                                            || a.StaleEntries.Any(s => s.Folder == folder && s.Item.Equals(item, StringComparison.OrdinalIgnoreCase))))
                {
                    changed |= doc.ListRemove("ADDON", "LoadFromDllMain", item);
                    plan.Disown.Add("ini:" + PathGuard.Normalize(cfgPath) + "|" + item);
                }
            if (!changed) continue;
            plan.Files.Add(new FileOp { Path = PathGuard.Normalize(cfgPath), NewHash = store.Put(doc.ToBytes()), Expected = original, IsConfig = true });
        }

        if (plan.Files.Count == 0) return null;
        foreach (var f in a.Remove) plan.Notes.Add(Tx.Get("compat.nrNoteRemove", f.Shown));
        if (a.Keep != null) plan.Notes.Add(Tx.Get("compat.nrNoteKeep", a.Keep.ShownVersion));
        plan.Notes.Add(Tx.Get("compat.nrNoteUndo"));
        return plan;
    }

    private static bool IsManagedName(string name) =>
        name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase) && (NrName.IsMatch(name) || FeederName.IsMatch(name));

    private static Analysis Analyze(GameContext ctx)
    {
        var a = new Analysis();
        bool addonPathUnknown = false;
        a.Folders.Add(new("", ctx.Folder, false, ctx.Scan.ReShadeConfig));

        // [ADDON] AddonPath
        var scan = ctx.Scan;
        if (scan.AddonFolder == "?")
        {
            var abs = AddonPathAbsolute(ctx);
            if (abs != null && Directory.Exists(abs) && TryRoot(ctx, a, "addons", abs)) a.Folders.Add(new("@addons/", abs, false, null));
            else addonPathUnknown = true; // outside the game: cannot be judged, so the early-load list is left alone
        }
        else if (scan.AddonFolder.Length > 0)
        {
            try { a.Folders.Add(new(scan.AddonFolder.TrimEnd('/') + "/", PathGuard.Resolve(ctx.Folder, scan.AddonFolder), false, null)); }
            catch (UnsafePathException) { addonPathUnknown = true; }
        }

        // Mirror made by REFramework (and similar tools): loaded too when it holds add-ons or a ReShade config.
        AddMirror(ctx, a, "", ctx.Folder, null);
        if (!SameDir(ctx.Folder, ctx.Game.Root)) AddMirror(ctx, a, "storage", ctx.Game.Root, "@storage/");

        foreach (var folder in a.Folders.Where(f => Directory.Exists(f.Dir)))
            foreach (var file in Directory.EnumerateFiles(folder.Dir, "*.addon64").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var name = System.IO.Path.GetFileName(file);
                if (!name.EndsWith(".addon64", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = folder.Prefix + name;
                var read = NrAddonInfo.Read(file);
                bool isNr = read != null || NrName.IsMatch(name), isFeeder = !isNr && FeederName.IsMatch(name);
                if (!isNr && !isFeeder) continue;
                var version = read ?? PeFile.FileVersion(file) ?? new Version(0, 0);
                var owner = ctx.State.OwnerOf(rel);
                var found = new Found(rel, file, name, folder, isNr, isFeeder, read != null, version, owner);
                (isNr ? a.Nr : a.Feeders).Add(found);
            }

        // REFramework keeps an identical copy of every loaded add-on in _storage_ and recreates it at each start:
        // same name, size and version as an original-folder add-on means "the same add-on", not a conflict.
        a.Nr.RemoveAll(m => m.Folder.Mirror && a.Nr.Any(o => !o.Folder.Mirror && SameAddon(o, m)));

        // NR: keep one. The original folders win over the mirror copies, which are only derived from them.
        if (a.Nr.Count > 0)
        {
            var pool = a.Nr.Any(n => !n.Folder.Mirror) ? a.Nr.Where(n => !n.Folder.Mirror).ToList() : a.Nr;
            bool Managed(Found f) => f.Owner == RenoDxNrComponent.ComponentId;
            if (pool.Any(n => n.Is12))
                a.Keep = pool.Where(n => n.Is12).OrderByDescending(n => n.Version).ThenByDescending(Managed).ThenBy(n => n.Folder.Prefix.Length).First();
            else
                a.Keep = pool.FirstOrDefault(Managed)
                         ?? pool.OrderByDescending(n => n.Version).ThenBy(n => n.Folder.Prefix.Length).First();
            foreach (var n in a.Nr.Where(n => n != a.Keep)) AddRemoval(a, n);
        }
        if (a.Has12)
            foreach (var f in a.Feeders) AddRemoval(a, f);

        // Early-load list entries for NR / Feeder add-ons that are not there (any more).
        var gone = a.Remove.Select(f => f.Full).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in a.Folders.Where(f => f.Config != null))
        {
            if (folder.Prefix.Length == 0 && (addonPathUnknown || scan.ReShadeLoaders.Count == 0)) continue;
            string full;
            try { full = Locations.Resolve(ctx.Folder, a.Roots, folder.Prefix + folder.Config); }
            catch (Exception ex) when (ex is HdlssException or ArgumentException) { continue; }
            if (!File.Exists(full)) continue;
            var places = folder.Mirror ? [folder] : a.Folders.Where(f => !f.Mirror).ToList();
            foreach (var item in IniDocument.SplitList(IniDocument.Load(full).Get("ADDON", "LoadFromDllMain")))
            {
                if (!IsManagedName(item)) continue;
                bool exists = places.Any(p => File.Exists(System.IO.Path.Combine(p.Dir, item)) && !gone.Contains(System.IO.Path.Combine(p.Dir, item)));
                if (!exists) a.StaleEntries.Add(new(folder, item, !places.Any(p => File.Exists(System.IO.Path.Combine(p.Dir, item)))));
            }
        }
        return a;
    }

    private static bool SameAddon(Found x, Found y) =>
        x.Name.Equals(y.Name, StringComparison.OrdinalIgnoreCase)
        && new FileInfo(x.Full).Length == new FileInfo(y.Full).Length
        && x.Version == y.Version;

    /// <summary>True when a Feeder HDLSS5 installed sits next to DLSS5 NR 12: the app removes it with the Feeder card's own removal flow.</summary>
    public static bool NeedsFeederRemoval(GameContext ctx) => Analyze(ctx).Blocked.Any(IsHdlssFeeder);

    private static bool IsHdlssFeeder(Found f) => f.IsFeeder && f.Owner == FeederComponent.ComponentId;

    private static void AddRemoval(Analysis a, Found f)
    {
        // A file another HDLSS component owns as its own (Feeder, ...) is not ours to delete. RenoDX NR's
        // own older add-on is: it is released like a normal update does.
        if (f.Owner != null && f.Owner != RenoDxNrComponent.ComponentId) a.Blocked.Add(f);
        else a.Remove.Add(f);
    }

    private static void AddMirror(GameContext ctx, Analysis a, string alias, string baseDir, string? prefix)
    {
        try
        {
            string dir;
            if (alias.Length == 0) dir = PathGuard.Resolve(ctx.Folder, MirrorFolder);
            else
            {
                dir = System.IO.Path.Combine(baseDir, MirrorFolder);
                if (!Directory.Exists(dir) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return;
            }
            if (!Directory.Exists(dir)) return;
            bool hasAddons = Directory.EnumerateFiles(dir, "*.addon64").Any();
            bool hasIni = File.Exists(System.IO.Path.Combine(dir, "ReShade.ini"));
            if (!hasAddons && !hasIni) return;
            if (alias.Length > 0 && !TryRoot(ctx, a, alias, dir)) return;
            a.Folders.Add(new(prefix ?? MirrorFolder + "/", dir, true, hasIni ? "ReShade.ini" : null));
        }
        catch (Exception ex) when (ex is UnsafePathException or IOException or UnauthorizedAccessException) { }
    }

    private static bool TryRoot(GameContext ctx, Analysis a, string alias, string absolute)
    {
        try { Locations.ValidateRoot(absolute, ctx.Game.Root); }
        catch (HdlssException) { return false; }
        a.Roots[alias] = System.IO.Path.GetFullPath(absolute);
        return true;
    }

    private static string? AddonPathAbsolute(GameContext ctx)
    {
        var value = ctx.Scan.AddonPathValue;
        if (value.Length == 0) return null;
        try { return System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(value) ? value : System.IO.Path.Combine(ctx.Folder, value)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }

    private static bool SameDir(string x, string y) =>
        string.Equals(System.IO.Path.GetFullPath(x).TrimEnd('\\', '/'), System.IO.Path.GetFullPath(y).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
