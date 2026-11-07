using Hdlss.Core.IO;

namespace Hdlss.Core.Transactions;

/// <summary>Generic uninstall for any component, driven only by its <see cref="ComponentRecord"/>.</summary>
public static class RemovalPlanner
{
    public static ChangePlan Plan(Storage.ContentStore store, GameState state, string? stateStamp, string gameName, ComponentRecord record, bool force = false)
    {
        var plan = new ChangePlan
        {
            GameId = state.GameId, GameName = gameName, Folder = state.Folder, ComponentId = record.Id,
            ComponentName = record.Name, Version = record.Version, IsRemoval = true, StateStamp = stateStamp,
            Roots = new(record.Roots),
        };
        foreach (var f in record.Files)
        {
            var full = Locations.Resolve(state.Folder, record.Roots, f.Path);
            var current = FileHash.OfFileOrNull(full);
            if (FileHash.Equal(current, f.Before)) continue; // already back to original
            if (!FileHash.Equal(current, f.After) && !OnlyEditedByOthers(state, record.Id, f, full))
            {
                if (f.KeepIfModified) { plan.Notes.Add($"{f.Path}: modificato dall'utente, conservato."); continue; }
                if (current != null && record.Meta.ContainsKey(PlanBuilder.AdoptGameChangesMeta))
                {
                    // The game's own DLL, replaced by a game update: that is already the game's file.
                    plan.Notes.Add($"{f.Path}: sostituito dal gioco dopo l'ultima modifica, lasciato così com'è.");
                    continue;
                }
                if (!force)
                    throw new ModifiedByOthersException($"{f.Path} è stato modificato da un altro programma dopo l'installazione.");
                plan.Notes.Add($"{f.Path}: modificato da altro software, lasciato invariato.");
                continue;
            }
            plan.Files.Add(new FileOp { Path = f.Path, NewHash = f.Before, Expected = current, Attributes = f.Attributes, Release = true });
        }

        foreach (var group in record.IniEdits.Where(e => e.Applied).GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase))
        {
            var full = Locations.Resolve(state.Folder, record.Roots, group.Key);
            if (!File.Exists(full)) continue;
            var original = FileHash.OfFile(full);
            var doc = IniDocument.Load(full);
            foreach (var e in group.Reverse())
            {
                var current = doc.Get(e.Section, e.Key);
                switch (e.Kind)
                {
                    case IniEditKind.ListAdd:
                        doc.ListRemove(e.Section, e.Key, e.Value);
                        break;
                    case IniEditKind.Set or IniEditKind.SetIfMissing when current == e.Value:
                        if (e.Previous == null || e.Kind == IniEditKind.SetIfMissing) doc.Remove(e.Section, e.Key);
                        else doc.Set(e.Section, e.Key, e.Previous);
                        break;
                    default:
                        plan.Notes.Add($"{e.File} [{e.Section}] {e.Key}: valore cambiato dall'utente, conservato.");
                        continue;
                }
                plan.IniEdits.Add(e);
            }
            var bytes = doc.ToBytes();
            if (FileHash.Equal(FileHash.OfBytes(bytes), original)) continue;
            plan.Files.Add(new FileOp { Path = PathGuard.Normalize(group.Key), NewHash = store.Put(bytes), Expected = original, IsConfig = true });
        }
        return plan;
    }

    /// <summary>
    /// A config this component owns may carry recorded edits from another managed component
    /// (ReShade sets [Plugins] LoadReshade in OptiScaler.ini). Undoing those edits on a copy and
    /// getting back our own version means nobody else touched it: the file can go.
    /// </summary>
    private static bool OnlyEditedByOthers(GameState state, string self, FileRecord f, string full)
    {
        if (!File.Exists(full) || !f.Path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) return false;
        var edits = state.Components.Values.Where(c => c.Id != self)
            .SelectMany(c => c.IniEdits).Where(e => e.Applied && string.Equals(PathGuard.Normalize(e.File), PathGuard.Normalize(f.Path), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (edits.Count == 0) return false;
        var doc = IniDocument.Load(full);
        foreach (var e in Enumerable.Reverse(edits))
        {
            if (e.Kind == IniEditKind.ListAdd) doc.ListRemove(e.Section, e.Key, e.Value);
            else if (e.Previous == null || e.Kind == IniEditKind.SetIfMissing) doc.Remove(e.Section, e.Key);
            else doc.Set(e.Section, e.Key, e.Previous);
        }
        return FileHash.Equal(FileHash.OfBytes(doc.ToBytes()), f.After);
    }
}
