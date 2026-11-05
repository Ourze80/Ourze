namespace Hdlss.Core.Transactions;

/// <summary>One file change inside a game folder. Payloads are referenced by store hash.</summary>
public sealed class FileOp
{
    public required string Path { get; init; }            // relative, forward slashes
    public string? NewHash { get; init; }                  // null = delete
    public string? Expected { get; init; }                 // hash the file must have right now (null = must not exist)
    public FileAttributes? Attributes { get; init; }
    /// <summary>Configuration file edited in place: tracked through <see cref="IniEdit"/>, not owned.</summary>
    public bool IsConfig { get; init; }
    /// <summary>On removal, leave the file if the user changed it (presets, user-tunable ini).</summary>
    public bool KeepIfModified { get; init; }
    /// <summary>Returns the file to its pre-HDLSS content; it is no longer owned afterwards.</summary>
    public bool Release { get; init; }
    public bool IsDelete => NewHash == null;

    public override string ToString() => IsDelete ? $"Rimuovi  {Path}"
        : IsConfig ? $"Modifica {Path}"
        : Expected == null ? $"Aggiungi {Path}" : $"Sostituisci {Path} (backup)";
}

public enum IniEditKind { Set, SetIfMissing, ListAdd }

/// <summary>
/// A semantic INI change. Removal reverts exactly this edit, so settings the user changed
/// afterwards (or that other components added) survive an uninstall.
/// </summary>
public sealed class IniEdit
{
    public required string File { get; init; }
    public required string Section { get; init; }
    public required string Key { get; init; }
    public required IniEditKind Kind { get; init; }
    public required string Value { get; init; }
    public string? Previous { get; set; }
    /// <summary>False when the value was already present: nothing to revert.</summary>
    public bool Applied { get; set; }

    public bool SameTarget(IniEdit o) =>
        string.Equals(File, o.File, StringComparison.OrdinalIgnoreCase) && string.Equals(Section, o.Section, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Key, o.Key, StringComparison.OrdinalIgnoreCase) && Kind == o.Kind
        && (Kind != IniEditKind.ListAdd || string.Equals(Value, o.Value, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Kind switch
    {
        IniEditKind.ListAdd => $"{File} [{Section}] {Key} += {Value}",
        _ => $"{File} [{Section}] {Key} = {Value}",
    };
}

public sealed class ChangePlan
{
    public required string GameId { get; init; }
    public required string GameName { get; init; }
    public required string Folder { get; init; }
    public required string ComponentId { get; init; }
    public required string ComponentName { get; init; }
    public string Version { get; init; } = "";
    public bool IsRemoval { get; init; }
    public List<FileOp> Files { get; } = [];
    public List<IniEdit> IniEdits { get; } = [];
    public List<string> Notes { get; } = [];
    public Dictionary<string, string> Meta { get; } = [];
    /// <summary>Hash of the state file when the plan was built: detects concurrent changes.</summary>
    public string? StateStamp { get; init; }
    /// <summary>Extra roots outside the game folder, addressed as "@alias/relative" (e.g. Unreal config).</summary>
    public Dictionary<string, string> Roots { get; init; } = [];
    /// <summary>When set, this record is stored as-is (adoption of a 1.x install).</summary>
    public ComponentRecord? Adopt { get; init; }

    public bool IsEmpty => Files.Count == 0;

    public string Describe()
    {
        var lines = new List<string> { $"{GameName} · {ComponentName}{(Version.Length > 0 ? " · " + Version : "")}", Folder, "" };
        lines.AddRange(Files.Select(f => f.ToString()));
        if (IniEdits.Count > 0) { lines.Add(""); lines.AddRange(IniEdits.Where(e => e.Applied).Select(e => (IsRemoval ? "Annulla  " : "Imposta  ") + e)); }
        if (Notes.Count > 0) { lines.Add(""); lines.AddRange(Notes); }
        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class FileRecord
{
    public required string Path { get; init; }
    public string? Before { get; init; }
    public required string After { get; set; }
    public FileAttributes Attributes { get; init; } = FileAttributes.Normal;
    public bool KeepIfModified { get; init; }
}

public sealed class ComponentRecord
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime InstalledAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<FileRecord> Files { get; set; } = [];
    public List<IniEdit> IniEdits { get; set; } = [];
    public Dictionary<string, string> Meta { get; set; } = [];
    public Dictionary<string, string> Roots { get; set; } = [];

    public FileRecord? FindFile(string path) =>
        Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Everything HDLSS installed in one game folder.</summary>
public sealed class GameState
{
    public int Schema { get; set; } = 2;
    public string GameId { get; set; } = "";
    public string Folder { get; set; } = "";
    public Dictionary<string, ComponentRecord> Components { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ComponentRecord? Get(string id) => Components.TryGetValue(id, out var r) ? r : null;

    public string? OwnerOf(string path) =>
        Components.Values.FirstOrDefault(c => c.FindFile(path) != null)?.Id;
}

public enum JournalStatus { Pending, Completed, RolledBack, NeedsRecovery, Undone }

public sealed class JournalEntry
{
    public required string Path { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
    public FileAttributes Attributes { get; init; }
}

public sealed class Journal
{
    public required string Id { get; init; }
    public required string GameId { get; init; }
    public required string GameName { get; init; }
    public required string Folder { get; init; }
    public required string ComponentId { get; init; }
    public string Description { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;
    public JournalStatus Status { get; set; }
    public List<JournalEntry> Entries { get; init; } = [];
    public string? Error { get; set; }
    /// <summary>Component state before the operation, restored on undo.</summary>
    public GameState? StateBefore { get; set; }
    public Dictionary<string, string> Roots { get; init; } = [];
}
