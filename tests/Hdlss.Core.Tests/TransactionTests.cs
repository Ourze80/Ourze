using Hdlss.Core.IO;
using Hdlss.Core.Transactions;
using Xunit;

namespace Hdlss.Core.Tests;

public class TransactionTests : IDisposable
{
    private readonly Fixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact]
    public void Install_then_remove_restores_original_files()
    {
        _f.GameFile("dxgi.dll", "original");
        var b = _f.Builder();
        b.AddFile("dxgi.dll", _f.PayloadFile("a", "new-dxgi"));
        b.AddFile("sub/new.bin", _f.PayloadFile("b", "brand-new"));
        _f.Engine.Apply(b.Build());

        Assert.Equal("new-dxgi", _f.Read("dxgi.dll"));
        Assert.Equal("brand-new", _f.Read("sub/new.bin"));
        Assert.NotNull(_f.State().Get("comp"));

        _f.Engine.Apply(_f.Removal("comp"));
        Assert.Equal("original", _f.Read("dxgi.dll"));
        Assert.False(File.Exists(_f.GameFile("sub/new.bin")));
        Assert.False(Directory.Exists(Path.Combine(_f.Game, "sub")));   // emptied folders are pruned
        Assert.True(Directory.Exists(_f.Game));                          // never the game folder itself
        Assert.Null(_f.State().Get("comp"));
    }

    [Fact]
    public void Original_put_back_by_another_tool_can_be_updated_again()
    {
        _f.GameFile("sl.interposer.dll", "original");
        var b = _f.Builder(); b.AddFile("sl.interposer.dll", _f.PayloadFile("a", "v1")); _f.Engine.Apply(b.Build());
        _f.GameFile("sl.interposer.dll", "original");               // Steam "verify files"

        var again = _f.Builder(); again.AddFile("sl.interposer.dll", _f.PayloadFile("b", "v2")); _f.Engine.Apply(again.Build());
        Assert.Equal("v2", _f.Read("sl.interposer.dll"));
        _f.Engine.Apply(_f.Removal("comp"));
        Assert.Equal("original", _f.Read("sl.interposer.dll"));
    }

    [Fact]
    public void Game_update_becomes_the_new_original_for_game_dlls_only()
    {
        _f.GameFile("nvngx_dlss.dll", "original");
        var b = _f.Builder(); b.AddFile("nvngx_dlss.dll", _f.PayloadFile("a", "v1")); _f.Engine.Apply(b.Build());
        _f.GameFile("nvngx_dlss.dll", "patched-by-game");          // game update

        var strict = _f.Builder();
        Assert.Throws<BlockedException>(() => strict.AddFile("nvngx_dlss.dll", _f.PayloadFile("b", "v2")));

        var adopt = _f.Builder(); adopt.AdoptGameChanges = true;
        adopt.AddFile("nvngx_dlss.dll", _f.PayloadFile("b", "v2")); _f.Engine.Apply(adopt.Build());
        Assert.Equal("v2", _f.Read("nvngx_dlss.dll"));
        _f.Engine.Apply(_f.Removal("comp"));
        Assert.Equal("patched-by-game", _f.Read("nvngx_dlss.dll"));
    }

    [Fact]
    public void File_changed_after_preview_aborts_without_touching_anything()
    {
        _f.GameFile("dxgi.dll", "original");
        var b = _f.Builder();
        b.AddFile("dxgi.dll", _f.PayloadFile("a", "new"));
        b.AddFile("other.dll", _f.PayloadFile("b", "other"));
        var plan = b.Build();
        _f.GameFile("dxgi.dll", "changed-by-someone-else");

        Assert.Throws<HdlssException>(() => _f.Engine.Apply(plan));
        Assert.Equal("changed-by-someone-else", _f.Read("dxgi.dll"));
        Assert.False(File.Exists(_f.GameFile("other.dll")));
    }

    [Fact]
    public void Write_failure_rolls_back_previous_writes()
    {
        _f.GameFile("a.dll", "A0");
        var b = _f.Builder();
        b.AddFile("a.dll", _f.PayloadFile("a", "A1"));
        b.AddFile("b.dll", _f.PayloadFile("b", "B1"));
        var plan = b.Build();
        // A directory where a file must be written makes the second write fail.
        Directory.CreateDirectory(_f.GameFile("b.dll"));

        var ex = Assert.Throws<HdlssException>(() => _f.Engine.Apply(plan));
        Assert.Contains("Nessun file", ex.Message);
        Assert.Equal("A0", _f.Read("a.dll"));
        Assert.Null(_f.State().Get("comp"));
        Assert.Contains(_f.Engine.Journals(), j => j.Status == JournalStatus.RolledBack);
    }

    [Fact]
    public void Removal_refuses_files_modified_by_other_software_unless_forced()
    {
        var b = _f.Builder();
        b.AddFile("x.dll", _f.PayloadFile("a", "ours"));
        b.AddFile("y.dll", _f.PayloadFile("b", "ours-y"));
        _f.Engine.Apply(b.Build());
        _f.GameFile("x.dll", "theirs");

        var ex = Assert.Throws<ModifiedByOthersException>(() => _f.Removal("comp"));
        Assert.Contains("x.dll", ex.Message);
        Assert.DoesNotContain("«", ex.Message);                                   // names no button
        _f.Engine.Apply(_f.Removal("comp", force: true));
        Assert.Equal("theirs", _f.Read("x.dll"));
        Assert.False(File.Exists(_f.GameFile("y.dll")));
    }

    [Fact]
    public void Update_keeps_first_backup_and_releases_dropped_files()
    {
        _f.GameFile("main.dll", "game-original");
        var v1 = _f.Builder(version: "1");
        v1.AddFile("main.dll", _f.PayloadFile("m1", "v1"));
        v1.AddFile("extra.dll", _f.PayloadFile("e1", "extra"));
        _f.Engine.Apply(v1.Build());

        var v2 = _f.Builder(version: "2");
        v2.AddFile("main.dll", _f.PayloadFile("m2", "v2"));
        _f.Engine.Apply(v2.Build());
        Assert.Equal("v2", _f.Read("main.dll"));
        Assert.False(File.Exists(_f.GameFile("extra.dll")));
        Assert.Null(_f.State().Get("comp")!.FindFile("extra.dll"));

        _f.Engine.Apply(_f.Removal("comp"));
        Assert.Equal("game-original", _f.Read("main.dll"));
    }

    [Fact]
    public void Reinstall_of_identical_payload_is_a_no_op()
    {
        var payload = _f.PayloadFile("a", "same");
        var b1 = _f.Builder(); b1.AddFile("a.dll", payload); _f.Engine.Apply(b1.Build());
        var b2 = _f.Builder(); b2.AddFile("a.dll", payload);
        Assert.True(b2.Build().IsEmpty);
    }

    [Fact]
    public void Files_owned_by_another_component_are_protected()
    {
        var b = _f.Builder("first"); b.AddFile("shared.dll", _f.PayloadFile("a", "1")); _f.Engine.Apply(b.Build());
        var other = _f.Builder("second");
        Assert.Throws<BlockedException>(() => other.AddFile("shared.dll", _f.PayloadFile("b", "2")));
    }

    [Fact]
    public void Ini_edits_are_reverted_semantically_preserving_user_changes()
    {
        _f.GameFile("ReShade.ini", "[GENERAL]\r\nPresetPath=.\\mine.ini\r\n\r\n[ADDON]\r\nLoadFromDllMain=existing.addon64\r\n");
        var b = _f.Builder();
        b.IniListAdd("ReShade.ini", "ADDON", "LoadFromDllMain", "renodx-dlss5.addon64");
        b.IniSet("ReShade.ini", "GENERAL", "PresetPath", ".\\HDLSS-Feeder.ini");
        b.IniSetIfMissing("ReShade.ini", "ADDON", "AddonPath", ".\\");
        _f.Engine.Apply(b.Build());

        var doc = IniDocument.Load(_f.GameFile("ReShade.ini"));
        Assert.Equal("existing.addon64,renodx-dlss5.addon64", doc.Get("ADDON", "LoadFromDllMain"));
        Assert.Equal(".\\HDLSS-Feeder.ini", doc.Get("GENERAL", "PresetPath"));

        // The user adds a setting of their own afterwards.
        doc.Set("INPUT", "KeyOverlay", "36,0,0,0");
        File.WriteAllBytes(_f.GameFile("ReShade.ini"), doc.ToBytes());

        _f.Engine.Apply(_f.Removal("comp"));
        var after = IniDocument.Load(_f.GameFile("ReShade.ini"));
        Assert.Equal("existing.addon64", after.Get("ADDON", "LoadFromDllMain"));
        Assert.Equal(".\\mine.ini", after.Get("GENERAL", "PresetPath"));
        Assert.Null(after.Get("ADDON", "AddonPath"));
        Assert.Equal("36,0,0,0", after.Get("INPUT", "KeyOverlay"));
    }

    [Fact]
    public void Ini_value_changed_by_user_is_not_reverted()
    {
        _f.GameFile("OptiScaler.ini", "[DlssNr]\nEnabled=false\n");
        var b = _f.Builder();
        b.IniSet("OptiScaler.ini", "DlssNr", "Enabled", "true");
        _f.Engine.Apply(b.Build());
        _f.GameFile("OptiScaler.ini", "[DlssNr]\nEnabled=auto\n");

        var removal = _f.Removal("comp");
        Assert.Contains(removal.Notes, n => n.Contains("Enabled"));
        _f.Engine.Apply(removal);
        Assert.Equal("auto", IniDocument.Load(_f.GameFile("OptiScaler.ini")).Get("DlssNr", "Enabled"));
    }

    [Fact]
    public void Store_deduplicates_and_maintenance_collects_unreferenced_blobs()
    {
        var payload = _f.PayloadFile("big", new string('x', 100_000));
        for (int i = 0; i < 3; i++)
        {
            var b = _f.Builder("c" + i);
            b.AddFile($"copy{i}.dll", payload);
            _f.Engine.Apply(b.Build());
        }
        Assert.Single(_f.Store.AllHashes());

        foreach (var c in new[] { "c0", "c1", "c2" }) _f.Engine.Apply(_f.Removal(c));
        foreach (var j in _f.Engine.Journals().ToList()) _f.Engine.DeleteJournal(j.Id);
        var report = new Maintenance(_f.Engine, Diagnostics.Log.Null).Run(keepPerGame: 0, keepDays: -1);
        Assert.Equal(1, report.BlobsRemoved);
        Assert.Empty(_f.Store.AllHashes());
    }

    [Fact]
    public void Undo_restores_files_and_component_state()
    {
        _f.GameFile("dxgi.dll", "original");
        var b = _f.Builder();
        b.AddFile("dxgi.dll", _f.PayloadFile("a", "new"));
        var journal = _f.Engine.Apply(b.Build());

        _f.Engine.Undo(journal);
        Assert.Equal("original", _f.Read("dxgi.dll"));
        Assert.Null(_f.State().Get("comp"));
    }

    [Fact]
    public void Stale_plan_is_rejected_after_another_operation()
    {
        var first = _f.Builder("a"); first.AddFile("a.dll", _f.PayloadFile("a", "1"));
        var second = _f.Builder("b"); second.AddFile("b.dll", _f.PayloadFile("b", "2"));
        var p1 = first.Build(); var p2 = second.Build();
        _f.Engine.Apply(p1);
        Assert.Throws<HdlssException>(() => _f.Engine.Apply(p2));
    }
}
