using System.Text;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.Storage;
using Hdlss.Core.Transactions;

namespace Hdlss.Core.Tests;

/// <summary>Isolated game folder + data folder per test.</summary>
public sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hdlss-tests", Guid.NewGuid().ToString("N"));
    public string Game => Path.Combine(Root, "game");
    public string Payload => Path.Combine(Root, "payload");
    public AppPaths Paths { get; }
    public ContentStore Store { get; }
    public StateStore States { get; }
    public TransactionEngine Engine { get; }

    public Fixture()
    {
        Directory.CreateDirectory(Game);
        Directory.CreateDirectory(Payload);
        Paths = new AppPaths(Root, Path.Combine(Root, "data"));
        Paths.EnsureCreated();
        Store = new ContentStore(Paths.Store);
        States = new StateStore(Paths.State);
        Engine = new TransactionEngine(Store, States, Paths.Journal, Log.Null);
    }

    public string GameFile(string rel, string? content = null)
    {
        var path = Path.Combine(Game, rel.Replace('/', '\\'));
        if (content != null) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); }
        return path;
    }

    public string PayloadFile(string name, string content)
    {
        var path = Path.Combine(Payload, name);
        File.WriteAllText(path, content);
        return path;
    }

    public string Read(string rel) => File.ReadAllText(GameFile(rel));

    public GameState State() => States.Load("g1", Game);

    public PlanBuilder Builder(string component = "comp", string version = "1") =>
        new(Store, State(), States.Stamp(Game), "Test Game", component, component, version);

    public ChangePlan Removal(string component, bool force = false) =>
        RemovalPlanner.Plan(Store, State(), States.Stamp(Game), "Test Game", State().Get(component)!, force);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch (IOException) { }
    }
}
