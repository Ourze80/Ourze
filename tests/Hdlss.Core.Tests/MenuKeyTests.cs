using Hdlss.Core;
using Hdlss.Core.Components;
using Hdlss.Core.IO;
using Xunit;

namespace Hdlss.Core.Tests;

public class MenuKeyTests : IDisposable
{
    private readonly Fixture _f = new();
    public void Dispose() => _f.Dispose();

    private string Key() => IniDocument.Load(_f.GameFile("ReShade.ini")).Get("INPUT", "KeyOverlay") ?? "(none)";

    private void Install(string option)
    {
        var b = _f.Builder();
        MenuKeys.Apply(b, _f.State().Get("comp"), "ReShade.ini", "INPUT", "KeyOverlay", option, MenuKeys.ReShadeDefault, MenuKeys.ReShadeFormat);
        b.Meta(MenuKeys.OptionKey, option);
        _f.Engine.Apply(b.Build());
    }

    [Fact]
    public void Option_lists_default_first_without_repeating_it()
    {
        var o = MenuKeys.Option(MenuKeys.OptiScalerDefault);
        Assert.Equal(MenuKeys.Default, o.Default);
        Assert.Equal("Predefinito (Ins)", o.Choices[0].Label);
        Assert.DoesNotContain(o.Choices, c => c.Value == "0x2D");
        Assert.Equal(0x77, MenuKeys.Parse("0x77"));
        Assert.Null(MenuKeys.Parse(MenuKeys.Default));
    }

    [Fact]
    public void Chosen_key_is_written_changed_and_put_back_by_default()
    {
        _f.GameFile("ReShade.ini", "[INPUT]\r\nKeyOverlay=113,0,0,0\r\n");   // the user's own F2
        Install(MenuKeys.Default);
        Assert.Equal("113,0,0,0", Key());                                     // default: untouched

        Install("0x77");
        Assert.Equal("119,0,0,0", Key());                                     // F8
        Install("0x23");
        Assert.Equal("35,0,0,0", Key());                                      // End

        Install(MenuKeys.Default);
        Assert.Equal("113,0,0,0", Key());                                     // back to the user's key
    }

    [Fact]
    public void Removal_restores_the_key_from_before()
    {
        _f.GameFile("ReShade.ini", "[INPUT]\r\nKeyOverlay=113,0,0,0\r\n");
        Install("0x77");
        _f.Engine.Apply(_f.Removal("comp"));
        Assert.Equal("113,0,0,0", Key());
    }

    [Fact]
    public void Config_built_on_the_current_file_replaces_a_user_modified_copy()
    {
        var b = _f.Builder(); b.AddBytes("OptiScaler.ini", "[Menu]\r\nShortcutKey=auto\r\n"u8.ToArray(), keepIfModified: true); _f.Engine.Apply(b.Build());
        _f.GameFile("OptiScaler.ini", "[Menu]\r\nShortcutKey=auto\r\nScale=1.5\r\n");   // the user's change

        var kept = _f.Builder(); kept.AddBytes("OptiScaler.ini", "[Menu]\r\nShortcutKey=0x77\r\n"u8.ToArray(), keepIfModified: true); _f.Engine.Apply(kept.Build());
        Assert.Contains("Scale=1.5", _f.Read("OptiScaler.ini"));
        Assert.DoesNotContain("0x77", _f.Read("OptiScaler.ini"));

        var merged = _f.Builder();
        merged.AddBytes("OptiScaler.ini", "[Menu]\r\nShortcutKey=0x77\r\nScale=1.5\r\n"u8.ToArray(), keepIfModified: true, builtOnCurrent: true);
        _f.Engine.Apply(merged.Build());
        Assert.Contains("ShortcutKey=0x77", _f.Read("OptiScaler.ini"));
        Assert.Contains("Scale=1.5", _f.Read("OptiScaler.ini"));
    }

    [Fact]
    public void File_replaced_by_another_build_of_the_same_component_is_updated()
    {
        int n = 0; string Source(string text) => _f.PayloadFile($"refw{++n}.dll", text);
        var b = _f.Builder(); b.AddFile("dinput8.dll", Source("REFW build 1")); _f.Engine.Apply(b.Build());
        _f.GameFile("dinput8.dll", "REFW build 2 (mod manager)");

        var blocked = _f.Builder();
        Assert.Throws<BlockedException>(() => blocked.AddFile("dinput8.dll", Source("REFW build 3")));

        var update = _f.Builder();
        update.AddFile("dinput8.dll", Source("REFW build 3"), sameKind: path => File.ReadAllText(path).StartsWith("REFW"));
        _f.Engine.Apply(update.Build());
        Assert.Equal("REFW build 3", _f.Read("dinput8.dll"));

        // Another tool already put the newest build in place: recorded, not "already up to date".
        _f.GameFile("dinput8.dll", "REFW build 4");
        var same = _f.Builder(version: "4");
        same.AddFile("dinput8.dll", Source("REFW build 4"), sameKind: path => File.ReadAllText(path).StartsWith("REFW"));
        var plan = same.Build();
        Assert.Single(plan.Files);
        _f.Engine.Apply(plan);
        Assert.Equal("4", _f.State().Get("comp")!.Version);

        _f.Engine.Apply(_f.Removal("comp"));        // still the component's file: removal deletes it
        Assert.False(File.Exists(Path.Combine(_f.Game, "dinput8.dll")));
    }
}
