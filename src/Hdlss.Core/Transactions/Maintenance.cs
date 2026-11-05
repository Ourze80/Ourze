using Hdlss.Core.Diagnostics;
using Hdlss.Core.Storage;

namespace Hdlss.Core.Transactions;

public sealed record MaintenanceReport(int JournalsRemoved, int BlobsRemoved, long BytesFreed, long StoreBytes)
{
    public override string ToString() =>
        $"Journal rimossi: {JournalsRemoved} · copie liberate: {BlobsRemoved} ({BytesFreed / 1048576.0:N0} MB) · archivio: {StoreBytes / 1048576.0:N0} MB";
}

/// <summary>
/// Bounded disk usage (the 1.x <c>dlss-recovery</c> folder grew to ~20 GB):
/// keeps the last N operations per game plus anything not completed, then deletes
/// every stored blob that no state or retained journal references.
/// </summary>
public sealed class Maintenance(TransactionEngine engine, Log log)
{
    public MaintenanceReport Run(int keepPerGame = 10, int keepDays = 30)
    {
        int removed = 0;
        var cutoff = DateTime.Now.AddDays(-keepDays);
        foreach (var group in engine.Journals().GroupBy(j => j.Folder, StringComparer.OrdinalIgnoreCase))
        {
            int kept = 0;
            foreach (var j in group.OrderByDescending(j => j.Time))
            {
                bool unfinished = j.Status is JournalStatus.Pending or JournalStatus.NeedsRecovery;
                if (unfinished || kept < keepPerGame || j.Time > cutoff) { kept++; continue; }
                engine.DeleteJournal(j.Id);
                removed++;
            }
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in engine.States.All())
            foreach (var c in state.Components.Values)
                foreach (var f in c.Files)
                {
                    if (f.Before != null) keep.Add(f.Before);
                    keep.Add(f.After);
                }
        foreach (var j in engine.Journals())
        {
            foreach (var e in j.Entries)
            {
                if (e.Before != null) keep.Add(e.Before);
                if (e.After != null) keep.Add(e.After);
            }
            foreach (var f in j.StateBefore?.Components.Values.SelectMany(c => c.Files) ?? [])
            {
                if (f.Before != null) keep.Add(f.Before);
                keep.Add(f.After);
            }
        }
        var (blobs, bytes) = engine.Store.Collect(keep);
        var report = new MaintenanceReport(removed, blobs, bytes, engine.Store.SizeBytes());
        log.Info("Manutenzione · " + report);
        return report;
    }
}
