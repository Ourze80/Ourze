using System.Text.Json;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.IO;
using Hdlss.Core.Storage;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Legacy;

/// <summary>An installation recorded by HDLSS5 1.x (marker json in the game folder).</summary>
public sealed class LegacyInstall
{
    public required string Marker { get; init; }
    public required string Folder { get; init; }
    public string Build { get; init; } = "";
    public string Release { get; init; } = "";
    public string PreviousOwner { get; init; } = "";
    public required List<LegacyFile> Files { get; init; }

    /// <summary>The 2.0 component that can take over this install, or null if it can only be restored.</summary>
    public string? AdoptableAs => Marker switch
    {
        "hdr-unlock-reshade.json" => "reshade",
        "hdlss-renodx-nr.json" => "renodx-nr",
        "hdlss-nr-feeder.json" => "nr-feeder",
        "hdr-unlock-dlss.json" => "optiscaler",
        "hdr-unlock-nr-model.json" => "nr-model",
        "hdr-unlock-renodx.json" => "renodx-hdr",
        "hdlss-mfg-unlock.json" => "mfg-unlock",
        "hdlss-asi-loader.json" => "asi-loader",
        "hdlss-ue4ss.json" => "ue4ss",
        "hdlss-reframework.json" => "reframework",
        "hdr-unlock-effects-autohdr.json" => "autohdr",
        _ when Marker.StartsWith("hdr-unlock-effects-", StringComparison.OrdinalIgnoreCase)
               && Marker[19..^5] is "lilium" or "cas" => "fx-" + Marker[19..^5],
        _ => null,
    };

    public string Title => Marker switch
    {
        "hdr-unlock-dlss.json" => "OptiScaler (1.x)",
        "hdlss-nr-feeder.json" => "Feeder NR (1.x)",
        "hdlss-renodx-nr.json" => "RenoDX NR (1.x)",
        "hdr-unlock-nr-model.json" => "Modello NR (1.x)",
        "hdlss-mfg-unlock.json" => "MFG Unlock (1.x)",
        "hdlss-ue4ss.json" => "UE4SS (1.x)",
        "hdlss-asi-loader.json" => "ASI Loader (1.x)",
        "hdlss-streamline.json" => "Streamline / DLSS-G (1.x)",
        "hdr-unlock-dlls.json" => "DLL DLSS sostituite (1.x)",
        "hdlss-reframework.json" => "REFramework (1.x)",
        "hdr-unlock-reshade-key.json" => "Tasto menu ReShade (1.x)",
        "hdlss-renodx-engine.json" => "HDR Engine.ini RenoDX (1.x)",
        "hdr-unlock-renodx.json" => "RenoDX HDR (1.x)",
        "hdr-unlock-reshade.json" => "ReShade (1.x)",
        _ when Marker.StartsWith("hdr-unlock-effects-", StringComparison.OrdinalIgnoreCase) => $"Shader ReShade «{Marker[19..^5]}» (1.x)",
        _ when Marker.Contains("reshade", StringComparison.OrdinalIgnoreCase) || Marker.Contains("renodx", StringComparison.OrdinalIgnoreCase) => "ReShade/RenoDX (1.x)",
        _ => $"Componente 1.x ({Marker})",
    } + (Release.Length is > 0 and <= 40 && !System.Text.RegularExpressions.Regex.IsMatch(Release, "^[0-9a-f]{40}$") ? $" · {Release}" : "");
}

public sealed class LegacyFile
{
    public string Name { get; set; } = "";
    public string? Before { get; set; }
    public string? After { get; set; }
    public string? Backup { get; set; }
    public int Attributes { get; set; }
}

/// <summary>
/// Reads 1.x manifests so games modified by the old app stay restorable, using the
/// backups in the old <c>data</c> folder. Nothing in the old folder is modified.
/// </summary>
public static class LegacyInstalls
{
    private sealed class OldManifest
    {
        public int Schema { get; set; }
        public string? Folder { get; set; }
        public string? Build { get; set; }
        public string? Release { get; set; }
        public string? PreviousOwner { get; set; }
        public List<LegacyFile>? Files { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string? FindDataFolder(string? legacyAppDir, string appDir)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(legacyAppDir)) candidates.Add(Path.Combine(legacyAppDir, "data"));
        candidates.Add(Path.Combine(appDir, "data"));
        var parent = Directory.GetParent(appDir)?.FullName;
        if (parent != null) candidates.Add(Path.Combine(parent, "data"));
        return candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "preferences.json")) || Directory.Exists(Path.Combine(d, "dlss-backups")));
    }

    public static List<LegacyInstall> Find(string folder, Log log)
    {
        var result = new List<LegacyInstall>();
        if (!Directory.Exists(folder)) return result;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            var name = Path.GetFileName(file);
            if (!(name.StartsWith("hdr-unlock", StringComparison.OrdinalIgnoreCase) || name.StartsWith("hdlss-", StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var m = JsonSerializer.Deserialize<OldManifest>(File.ReadAllText(file), Options);
                if (m?.Schema != 1 || m.Files == null || m.Folder == null) continue;
                if (!string.Equals(Path.GetFullPath(m.Folder), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new LegacyInstall
                {
                    Marker = name, Folder = Path.GetFullPath(folder), Build = m.Build ?? "", Release = m.Release ?? "",
                    PreviousOwner = m.PreviousOwner ?? "", Files = m.Files,
                });
            }
            catch (JsonException ex) { log.Warn($"Registro 1.x illeggibile {name}", ex); }
        }
        return result;
    }

    /// <summary>
    /// Takes a 1.x install over without touching game files: original backups move into the
    /// 2.0 store, files become owned by the matching component, and the old marker is removed.
    /// INI files are left as they are (their 1.x edits are not tracked semantically).
    /// </summary>
    public static ChangePlan PlanAdopt(LegacyInstall install, string componentId, string componentName, string legacyData,
        ContentStore store, GameState state, string? stamp, string gameName)
    {
        if (state.Get(componentId) != null) throw new BlockedException("Il componente è già gestito da HDLSS5 2.0 in questo gioco.");
        var record = new ComponentRecord
        {
            Id = componentId, Name = componentName, Version = install.Release.Length > 0 ? install.Release + " (da 1.x)" : "da 1.x",
            Meta = new() { ["adopted"] = install.Marker },
        };
        var notes = new List<string>();
        foreach (var f in install.Files)
        {
            var full = PathGuard.Resolve(install.Folder, f.Name);
            var current = FileHash.OfFileOrNull(full);
            if (f.Name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) { notes.Add($"{f.Name}: configurazione lasciata com'è."); continue; }
            if (current == null || !FileHash.Equal(current, f.After)) { notes.Add($"{f.Name}: cambiato dopo la 1.x, non adottato."); continue; }
            var owner = state.OwnerOf(PathGuard.Normalize(f.Name));
            if (owner != null) throw new BlockedException($"{f.Name} è già gestito da «{owner}».");
            string? before = null;
            if (f.Before != null)
            {
                var backup = f.Backup == null ? null : PathGuard.Resolve(legacyData, f.Backup);
                if (backup == null || !File.Exists(backup) || !FileHash.Equal(FileHash.OfFile(backup), f.Before))
                    throw new HdlssException($"Backup 1.x mancante o modificato per {f.Name}: usa «Ripristina comunque» o verifica i file del gioco.");
                before = store.Put(backup);
            }
            record.Files.Add(new FileRecord { Path = PathGuard.Normalize(f.Name), Before = before, After = current, Attributes = (FileAttributes)f.Attributes });
        }
        if (record.Files.Count == 0) throw new HdlssException("Nessun file della 1.x è ancora intatto: niente da adottare.");
        var plan = new ChangePlan
        {
            GameId = state.GameId, GameName = gameName, Folder = install.Folder, ComponentId = componentId,
            ComponentName = componentName + " · adozione 1.x", Version = record.Version, StateStamp = stamp, Adopt = record,
        };
        var marker = PathGuard.Resolve(install.Folder, install.Marker);
        plan.Files.Add(new FileOp { Path = install.Marker, NewHash = null, Expected = FileHash.OfFile(marker), Release = true });
        plan.Notes.Add($"{record.Files.Count} file passano sotto la gestione di HDLSS5 2.0; nessun file del gioco viene modificato.");
        plan.Notes.AddRange(notes);
        return plan;
    }

    /// <summary>True when no non-INI file is still as 1.x installed it: the record only describes the past.</summary>
    public static bool IsStale(LegacyInstall install)
    {
        var files = install.Files.Where(f => !f.Name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) return false;
        foreach (var f in files)
        {
            try
            {
                var current = FileHash.OfFileOrNull(PathGuard.Resolve(install.Folder, f.Name));
                if (FileHash.Equal(current, f.After)) return false;
            }
            catch (UnsafePathException) { }
        }
        return true;
    }

    /// <summary>Archives an outdated 1.x record: only the marker is removed (undoable from history).</summary>
    public static ChangePlan PlanForget(LegacyInstall install, GameState state, string? stamp, string gameName)
    {
        var plan = new ChangePlan
        {
            GameId = state.GameId, GameName = gameName, Folder = install.Folder, ComponentId = "legacy:" + install.Marker,
            ComponentName = install.Title + " · registro superato", IsRemoval = true, StateStamp = stamp,
        };
        plan.Files.Add(new FileOp { Path = install.Marker, NewHash = null, Expected = FileHash.OfFile(PathGuard.Resolve(install.Folder, install.Marker)), Release = true });
        plan.Notes.Add("I file sono stati cambiati da altro software dopo la 1.x: il registro non descrive più il gioco e viene archiviato. Nessun file del gioco cambia.");
        return plan;
    }

    /// <summary>Plan that returns every file of a 1.x install to its pre-HDLSS content and drops the marker.</summary>
    public static ChangePlan PlanRestore(LegacyInstall install, string legacyData, ContentStore store, GameState state, string? stamp, string gameName, bool force)
    {
        var plan = new ChangePlan
        {
            GameId = state.GameId, GameName = gameName, Folder = install.Folder, ComponentId = "legacy:" + install.Marker,
            ComponentName = install.Title, IsRemoval = true, StateStamp = stamp,
        };
        foreach (var f in install.Files)
        {
            var full = PathGuard.Resolve(install.Folder, f.Name);
            var current = FileHash.OfFileOrNull(full);
            if (FileHash.Equal(current, f.Before)) continue;
            if (!FileHash.Equal(current, f.After))
            {
                if (f.Name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) { plan.Notes.Add($"{f.Name}: configurazione cambiata dopo l'installazione, conservata."); continue; }
                if (!force) throw new BlockedException($"{f.Name} è cambiato dopo l'installazione 1.x. Usa «Ripristina comunque» per lasciarlo invariato.");
                plan.Notes.Add($"{f.Name}: cambiato da altro software, lasciato invariato.");
                continue;
            }
            string? before = null;
            if (f.Before != null)
            {
                if (f.Backup == null) throw new HdlssException($"Backup 1.x non indicato per {f.Name}.");
                var backup = PathGuard.Resolve(legacyData, f.Backup);
                if (!File.Exists(backup) || !FileHash.Equal(FileHash.OfFile(backup), f.Before))
                    throw new HdlssException($"Backup 1.x mancante o modificato: {f.Name}. Verifica l'integrità del gioco dal launcher.");
                before = store.Put(backup);
            }
            plan.Files.Add(new FileOp { Path = PathGuard.Normalize(f.Name), NewHash = before, Expected = current, Attributes = (FileAttributes)f.Attributes, Release = true });
        }
        var marker = PathGuard.Resolve(install.Folder, install.Marker);
        plan.Files.Add(new FileOp { Path = install.Marker, NewHash = null, Expected = FileHash.OfFile(marker), Release = true });
        plan.Notes.Add($"Stato ripristinato: {install.PreviousOwner}. I file di HDLSS 1.x in data/ non vengono toccati.");
        return plan;
    }
}
