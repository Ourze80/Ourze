using Hdlss.Core.Diagnostics;
using Hdlss.Core.IO;
using Hdlss.Core.Storage;

namespace Hdlss.Core.Transactions;

/// <summary>
/// The single place where game files are written. Every operation:
/// 1. re-checks that each file still has the hash seen in the preview;
/// 2. snapshots the current files into the content store (deduplicated) and writes a journal;
/// 3. writes atomically (temp + replace) and verifies the final hash;
/// 4. rolls everything back on any error, then records the new component state.
/// </summary>
public sealed class TransactionEngine(ContentStore store, StateStore states, string journalRoot, Log log)
{
    public ContentStore Store => store;
    public StateStore States => states;

    public Journal Apply(ChangePlan plan, IProgress<OperationProgress>? progress = null)
    {
        if (plan.StateStamp != states.Stamp(plan.Folder))
            throw new HdlssException("Lo stato del gioco è cambiato dopo l'anteprima: ripeti l'operazione.");
        var duplicate = plan.Files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) throw new HdlssException($"Percorso duplicato nel piano: {duplicate.Key}");

        log.Info($"{(plan.IsRemoval ? "RIMOZIONE" : "INSTALLAZIONE")} · {plan.GameName} · {plan.ComponentName} {plan.Version}");
        progress?.Report(new("Verifica dei file…", 0));

        var resolved = plan.Files.Select(f => (Op: f, Full: Locations.Resolve(plan.Folder, plan.Roots, f.Path))).ToList();
        foreach (var (op, full) in resolved)
        {
            if (!FileHash.Equal(FileHash.OfFileOrNull(full), op.Expected))
                throw new HdlssException($"File cambiato dopo l'anteprima: {op.Path}. Ripeti l'operazione.");
            if (op.NewHash != null) store.Open(op.NewHash);
        }

        var journal = new Journal
        {
            Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
            GameId = plan.GameId, GameName = plan.GameName, Folder = plan.Folder, ComponentId = plan.ComponentId,
            Description = $"{(plan.IsRemoval ? "Rimozione" : "Installazione")} {plan.ComponentName} {plan.Version}".Trim(),
            Status = JournalStatus.Pending,
            StateBefore = states.Load(plan.GameId, plan.Folder),
            Roots = new(plan.Roots),
        };
        foreach (var (op, full) in resolved)
        {
            string? before = File.Exists(full) ? store.Put(full) : null;
            journal.Entries.Add(new JournalEntry
            {
                Path = op.Path, Before = before, After = op.NewHash,
                Attributes = File.Exists(full) ? File.GetAttributes(full) : FileAttributes.Normal,
            });
        }
        SaveJournal(journal);

        var touched = new List<JournalEntry>();
        try
        {
            for (int i = 0; i < resolved.Count; i++)
            {
                var (op, full) = resolved[i];
                var entry = journal.Entries[i];
                progress?.Report(new($"{(op.IsDelete ? "Rimozione" : "Scrittura")} {op.Path}", 100.0 * i / Math.Max(1, resolved.Count)));
                touched.Add(entry);
                if (op.IsDelete) DeleteFile(full, Locations.RootOf(plan.Folder, plan.Roots, op.Path));
                else WriteFromStore(full, op.NewHash!, op.Attributes ?? (entry.Before != null ? entry.Attributes : FileAttributes.Normal));
                if (!FileHash.Equal(FileHash.OfFileOrNull(full), op.NewHash))
                    throw new IOException($"Verifica fallita dopo la scrittura: {op.Path}");
                log.Debug($"{(op.IsDelete ? "rimosso" : "scritto")} {op.Path}");
            }
            UpdateState(plan);
            journal.Status = JournalStatus.Completed;
            SaveJournal(journal);
            progress?.Report(new("Operazione completata", 100));
            log.Info($"Completato · journal {journal.Id}");
            return journal;
        }
        catch (Exception ex)
        {
            log.Error($"Errore durante l'applicazione, annullo {touched.Count} modifiche", ex);
            var errors = Rollback(plan.Folder, plan.Roots, touched);
            journal.Status = errors.Count == 0 ? JournalStatus.RolledBack : JournalStatus.NeedsRecovery;
            journal.Error = ex.Message + (errors.Count > 0 ? " | " + string.Join("; ", errors) : "");
            SaveJournal(journal);
            throw new HdlssException(errors.Count == 0
                ? $"{ex.Message}\nNessun file è stato lasciato modificato."
                : $"{ex.Message}\nRipristino incompleto ({string.Join("; ", errors)}). Usa 'Cronologia' per recuperare dal journal {journal.Id}.", ex);
        }
    }

    /// <summary>Reverts a completed journal (undo). Fails if files changed since.</summary>
    public void Undo(Journal journal)
    {
        if (journal.Status != JournalStatus.Completed && journal.Status != JournalStatus.NeedsRecovery)
            throw new HdlssException("Questa operazione non può essere annullata.");
        foreach (var e in journal.Entries)
        {
            var full = Locations.Resolve(journal.Folder, journal.Roots, e.Path);
            var current = FileHash.OfFileOrNull(full);
            if (!FileHash.Equal(current, e.After) && !FileHash.Equal(current, e.Before))
                throw new HdlssException($"{e.Path} è stato modificato dopo l'operazione: annullamento fermato.");
            if (e.Before != null) store.Open(e.Before);
        }
        var errors = Rollback(journal.Folder, journal.Roots, journal.Entries);
        if (errors.Count > 0) throw new HdlssException("Annullamento incompleto: " + string.Join("; ", errors));
        if (journal.StateBefore != null) states.Save(journal.StateBefore);
        journal.Status = JournalStatus.Undone;
        SaveJournal(journal);
        log.Info($"Annullata operazione {journal.Id} ({journal.Description})");
    }

    public IEnumerable<Journal> Journals()
    {
        if (!Directory.Exists(journalRoot)) yield break;
        foreach (var file in Directory.EnumerateFiles(journalRoot, "*.json").OrderByDescending(f => f))
        {
            Journal? j = null;
            try { j = JsonFile.Read<Journal>(file); }
            catch (Exception ex) { log.Warn($"Journal illeggibile {Path.GetFileName(file)}", ex); }
            if (j != null) yield return j;
        }
    }

    public void DeleteJournal(string id) => File.Delete(Path.Combine(journalRoot, id + ".json"));

    private List<string> Rollback(string folder, IReadOnlyDictionary<string, string> roots, IEnumerable<JournalEntry> entries)
    {
        var errors = new List<string>();
        foreach (var e in entries.Reverse())
        {
            try
            {
                var full = Locations.Resolve(folder, roots, e.Path);
                if (e.Before == null) DeleteFile(full, Locations.RootOf(folder, roots, e.Path));
                else WriteFromStore(full, e.Before, e.Attributes);
            }
            catch (Exception ex) { errors.Add($"{e.Path}: {ex.Message}"); }
        }
        return errors;
    }

    private void UpdateState(ChangePlan plan)
    {
        var state = states.Load(plan.GameId, plan.Folder);
        if (plan.Adopt != null)
        {
            state.Components[plan.Adopt.Id] = plan.Adopt;
        }
        else if (plan.IsRemoval)
        {
            state.Components.Remove(plan.ComponentId);
        }
        else
        {
            var old = state.Get(plan.ComponentId);
            var record = new ComponentRecord
            {
                Id = plan.ComponentId, Name = plan.ComponentName, Version = plan.Version,
                InstalledAt = old?.InstalledAt ?? DateTime.Now, UpdatedAt = DateTime.Now, Meta = new(plan.Meta),
                Roots = new((old?.Roots ?? []).Concat(plan.Roots).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value)),
            };
            // Keep the very first "Before" of each file so removal returns to the pre-HDLSS state.
            foreach (var op in plan.Files.Where(f => !f.IsConfig && !f.IsDelete && !f.Release))
            {
                var prior = old?.FindFile(op.Path);
                record.Files.Add(new FileRecord
                {
                    Path = op.Path, Before = prior != null && !op.NewOriginal ? prior.Before : op.Expected, After = op.NewHash!,
                    Attributes = prior?.Attributes ?? FileAttributes.Normal, KeepIfModified = op.KeepIfModified,
                });
            }
            // Files the previous version owned but this plan did not touch stay owned.
            if (old != null)
                foreach (var f in old.Files)
                    if (record.FindFile(f.Path) == null && !plan.Files.Any(p => string.Equals(p.Path, f.Path, StringComparison.OrdinalIgnoreCase)))
                        record.Files.Add(f);
            // Merge INI edits: an edit applied on first install must still be reverted later.
            var edits = new List<IniEdit>(old?.IniEdits ?? []);
            foreach (var e in plan.IniEdits)
            {
                var existing = edits.FirstOrDefault(x => x.SameTarget(e));
                if (existing == null) edits.Add(e);
                else if (!existing.Applied && e.Applied) { edits.Remove(existing); edits.Add(e); }
            }
            record.IniEdits = edits.Where(e => e.Applied).ToList();
            state.Components[plan.ComponentId] = record;
        }
        states.Save(state);
    }

    private void WriteFromStore(string full, string hash, FileAttributes attributes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var source = store.Open(hash);
        var temp = full + ".hdlss-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.Copy(source, temp, overwrite: true);
            File.SetAttributes(temp, FileAttributes.Normal);
            if (File.Exists(full))
            {
                File.SetAttributes(full, FileAttributes.Normal);
                File.Replace(temp, full, null, ignoreMetadataErrors: true);
            }
            else File.Move(temp, full);
            File.SetLastWriteTimeUtc(full, DateTime.UtcNow);
            File.SetAttributes(full, attributes == 0 ? FileAttributes.Normal : attributes);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            FileHash.Invalidate(full);
        }
    }

    /// <summary>Deletes a file, then any parent folders left empty, never above <paramref name="root"/>.</summary>
    private static void DeleteFile(string full, string root)
    {
        FileHash.Invalidate(full);
        if (File.Exists(full))
        {
            File.SetAttributes(full, FileAttributes.Normal);
            File.Delete(full);
        }
        var stop = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        for (var dir = Path.GetDirectoryName(full); dir != null && dir.Length > stop.Length
             && PathGuard.IsUnder(dir, stop); dir = Path.GetDirectoryName(dir))
        {
            if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) break;
            Directory.Delete(dir);
        }
    }

    private void SaveJournal(Journal journal) => JsonFile.Write(Path.Combine(journalRoot, journal.Id + ".json"), journal);
}
