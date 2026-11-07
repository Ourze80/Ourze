using Hdlss.Core.Text;
using Hdlss.Core.IO;
using Hdlss.Core.Storage;

namespace Hdlss.Core.Transactions;

/// <summary>
/// Builds install/update plans for one component. Components only declare *what* they want
/// in the folder; ownership checks, backups, update diffs and INI bookkeeping live here,
/// once, instead of being re-implemented by every component as in 1.x.
/// </summary>
public sealed class PlanBuilder
{
    private readonly ContentStore _store;
    private readonly GameState _state;
    private readonly ChangePlan _plan;
    private readonly ComponentRecord? _existing;
    private readonly Dictionary<string, (IniDocument Doc, string? OriginalHash)> _ini = new(StringComparer.OrdinalIgnoreCase);

    public string Folder => _plan.Folder;
    public ChangePlan Plan => _plan;

    public PlanBuilder(ContentStore store, GameState state, string? stateStamp, string gameName,
        string componentId, string componentName, string version)
    {
        _store = store;
        _state = state;
        _existing = state.Get(componentId);
        _plan = new ChangePlan
        {
            GameId = state.GameId, GameName = gameName, Folder = state.Folder,
            ComponentId = componentId, ComponentName = componentName, Version = version, StateStamp = stateStamp,
        };
        foreach (var (alias, root) in _existing?.Roots ?? []) _plan.Roots[alias] = root;
    }

    public ComponentRecord? Existing => _existing;

    /// <summary>
    /// For the game's own files (DLSS, Streamline): when the game replaced a managed file, accept
    /// it as the new original instead of blocking.
    /// </summary>
    public bool AdoptGameChanges
    {
        get => _plan.Meta.ContainsKey(AdoptGameChangesMeta);
        set { if (value) _plan.Meta[AdoptGameChangesMeta] = "true"; else _plan.Meta.Remove(AdoptGameChangesMeta); }
    }

    /// <summary>Stored on the record so removal knows the same rule.</summary>
    public const string AdoptGameChangesMeta = "adoptGameChanges";

    public string Full(string relative) => Locations.Resolve(Folder, _plan.Roots, relative);

    /// <summary>Registers a folder outside the game directory, addressed as "@alias/…".</summary>
    public void AddRoot(string alias, string absolute, string gameRoot)
    {
        Locations.ValidateRoot(absolute, gameRoot);
        _plan.Roots[alias] = Path.GetFullPath(absolute);
    }

    public void Note(string text) => _plan.Notes.Add(text);
    public void Meta(string key, string value) => _plan.Meta[key] = value;

    /// <param name="sameKind">
    /// Recognizes a file that another tool replaced with another build of the same component
    /// (e.g. a mod manager updating REFramework): it is updated instead of blocking.
    /// </param>
    public void AddFile(string relative, string sourceFile, bool keepIfModified = false, Func<string, bool>? sameKind = null) =>
        Add(PathGuard.Normalize(relative), _store.Put(sourceFile), keepIfModified, sameKind: sameKind);

    /// <param name="builtOnCurrent">
    /// The data was built from the file as it is now (user changes included): write it even when the
    /// user modified the file, instead of keeping it untouched.
    /// </param>
    public void AddBytes(string relative, byte[] data, bool keepIfModified = false, bool builtOnCurrent = false) =>
        Add(PathGuard.Normalize(relative), _store.Put(data), keepIfModified, builtOnCurrent);

    private void Add(string relative, string hash, bool keepIfModified, bool builtOnCurrent = false, Func<string, bool>? sameKind = null)
    {
        if (_ini.ContainsKey(relative)) throw new InvalidOperationException($"{relative} è già modificato come configurazione.");
        Want(relative);
        var full = Full(relative);
        var current = FileHash.OfFileOrNull(full);
        var owner = _state.OwnerOf(relative);
        if (owner != null && !string.Equals(owner, _plan.ComponentId, StringComparison.OrdinalIgnoreCase))
            throw new BlockedException(Tx.Get("plan.ownedBy", relative, owner));
        var prior = _existing?.FindFile(relative);
        bool newOriginal = false, adopted = false;
        if (prior != null && !FileHash.Equal(current, prior.After))
        {
            if (FileHash.Equal(current, prior.Before))
            {
                // Original put back by someone else (Steam "verify files", another tool): just reapply.
            }
            else if (AdoptGameChanges && current != null)
            {
                newOriginal = true;
                Note(Tx.Get("plan.replacedByGame", relative));
            }
            else if (keepIfModified && builtOnCurrent) { Note(Tx.Get("plan.userChangesKept", relative)); }
            else if (keepIfModified) { Note(Tx.Get("plan.userModifiedKept", relative)); return; }
            else if (current != null && sameKind != null && sameKind(full))
            {
                // Recorded again even when it already is the new build, so the installed version is right.
                adopted = true;
                Note(FileHash.Equal(current, hash)
                    ? Tx.Get("plan.adoptedSame", relative)
                    : Tx.Get("plan.adoptedOther", relative));
            }
            else throw new BlockedException(Tx.Get("plan.modifiedByOtherApp", relative));
        }
        if (FileHash.Equal(current, hash) && prior != null && !newOriginal && !adopted) return; // already up to date
        _plan.Files.Add(new FileOp { Path = relative, NewHash = hash, Expected = current, KeepIfModified = keepIfModified, NewOriginal = newOriginal });
    }

    /// <summary>Opens (once) an INI file of the game folder for semantic edits.</summary>
    public IniDocument Ini(string relative)
    {
        relative = PathGuard.Normalize(relative);
        if (_ini.TryGetValue(relative, out var entry)) return entry.Doc;
        var full = Full(relative);
        var doc = IniDocument.Load(full);
        _ini[relative] = (doc, FileHash.OfFileOrNull(full));
        return doc;
    }

    public void IniSet(string file, string section, string key, string value) => Edit(file, section, key, IniEditKind.Set, value);
    public void IniSetIfMissing(string file, string section, string key, string value) => Edit(file, section, key, IniEditKind.SetIfMissing, value);
    public void IniListAdd(string file, string section, string key, string item) => Edit(file, section, key, IniEditKind.ListAdd, item);

    private void Edit(string file, string section, string key, IniEditKind kind, string value)
    {
        var doc = Ini(file);
        var previous = doc.Get(section, key);
        var edit = new IniEdit { File = PathGuard.Normalize(file), Section = section, Key = key, Kind = kind, Value = value, Previous = previous };
        switch (kind)
        {
            case IniEditKind.Set:
                edit.Applied = previous != value;
                if (edit.Applied) doc.Set(section, key, value);
                break;
            case IniEditKind.SetIfMissing:
                edit.Applied = previous == null;
                if (edit.Applied) doc.Set(section, key, value);
                break;
            case IniEditKind.ListAdd:
                edit.Applied = doc.ListAdd(section, key, value);
                break;
        }
        // A Set over a value we set ourselves earlier must keep the original "Previous".
        var earlier = _existing?.IniEdits.FirstOrDefault(e => e.SameTarget(edit));
        if (earlier != null && kind != IniEditKind.ListAdd)
        {
            edit.Previous = earlier.Previous;
            edit.Applied = earlier.Applied || edit.Applied;
        }
        _plan.IniEdits.Add(edit);
    }

    public ChangePlan Build()
    {
        foreach (var (relative, (doc, original)) in _ini)
        {
            var bytes = doc.ToBytes();
            var hash = FileHash.OfBytes(bytes);
            if (FileHash.Equal(hash, original)) continue;
            _plan.Files.Add(new FileOp { Path = relative, NewHash = _store.Put(bytes), Expected = original, IsConfig = true });
        }
        // An update that no longer ships a file returns it to its original state.
        if (_existing != null)
        {
            foreach (var f in _existing.Files)
            {
                if (_plan.Files.Any(p => string.Equals(p.Path, f.Path, StringComparison.OrdinalIgnoreCase)) || WantsFile(f.Path)) continue;
                var current = FileHash.OfFileOrNull(Full(f.Path));
                if (!FileHash.Equal(current, f.After)) continue;
                _plan.Files.Add(new FileOp { Path = f.Path, NewHash = f.Before, Expected = current, Release = true, Attributes = f.Attributes });
            }
        }
        return _plan;
    }

    private readonly HashSet<string> _wanted = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Marks a file as still shipped even if it needs no change (already up to date).</summary>
    private bool WantsFile(string path) => _wanted.Contains(path);

    // Called by Add for every requested file, including no-op ones.
    /// <summary>Keeps an already-managed file owned even though this plan does not rewrite it.</summary>
    public void Want(string path) => _wanted.Add(path);
}
