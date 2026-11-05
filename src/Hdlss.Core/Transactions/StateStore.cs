using Hdlss.Core.IO;
using Hdlss.Core.Storage;

namespace Hdlss.Core.Transactions;

/// <summary>Persists one <see cref="GameState"/> per game folder under <c>data/state</c>.</summary>
public sealed class StateStore(string root)
{
    public string FileFor(string folder) =>
        Path.Combine(root, FileHash.OfBytes(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToUpperInvariant()))[..20] + ".json");

    public GameState Load(string gameId, string folder)
    {
        var state = JsonFile.Read<GameState>(FileFor(folder));
        if (state == null) return new GameState { GameId = gameId, Folder = Path.GetFullPath(folder) };
        if (state.Schema != 2 || !string.Equals(Path.GetFullPath(state.Folder), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
            throw new HdlssException("Registro dello stato non valido per questa cartella.");
        state.Components = new Dictionary<string, ComponentRecord>(state.Components, StringComparer.OrdinalIgnoreCase);
        return state;
    }

    public string? Stamp(string folder) => FileHash.OfFileOrNull(FileFor(folder));

    public void Save(GameState state)
    {
        var path = FileFor(state.Folder);
        if (state.Components.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        JsonFile.Write(path, state);
    }

    public IEnumerable<GameState> All()
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            var state = JsonFile.Read<GameState>(file);
            if (state != null) yield return state;
        }
    }
}
