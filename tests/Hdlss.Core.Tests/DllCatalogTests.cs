using System.Text.Json;
using Hdlss.Core.Components;
using Xunit;

namespace Hdlss.Core.Tests;

public class DllCatalogTests
{
    [Fact]
    public void Dlss_catalog_lists_signed_release_builds_newest_first()
    {
        var json = """
        { "dlss": [
            {"version":"310.9.1.0","is_dev_file":false,"is_signature_valid":true,"download_url":"https://dlss-swapper-downloads.beeradmoore.com/dlss/a.zip","zip_md5_hash":"A","md5_hash":"B"},
            {"version":"310.9.16.0","is_dev_file":false,"is_signature_valid":true,"download_url":"https://dlss-swapper-downloads.beeradmoore.com/dlss/b.zip","zip_md5_hash":"C","md5_hash":"D"},
            {"version":"310.10.0.0","is_dev_file":true,"is_signature_valid":true,"download_url":"https://dlss-swapper-downloads.beeradmoore.com/dlss/dev.zip","zip_md5_hash":"E","md5_hash":"F"},
            {"version":"999.0.0.0","is_dev_file":false,"is_signature_valid":true,"download_url":"https://evil.example.com/dlss/x.zip","zip_md5_hash":"G","md5_hash":"H"}
          ],
          "dlss_g": [
            {"version":"310.9.16.0","is_dev_file":false,"is_signature_valid":false,"download_url":"https://dlss-swapper-downloads.beeradmoore.com/dlss_g/u.zip","zip_md5_hash":"I","md5_hash":"J"}
          ] }
        """;
        using var doc = JsonDocument.Parse(json);
        var all = DlssCatalog.ParseAll(doc.RootElement);
        var sr = all["nvngx_dlss.dll"];
        Assert.Equal(["310.9.16.0", "310.9.1.0"], sr.Select(b => b.Version.ToString()));   // dev build and foreign host excluded
        Assert.False(all.ContainsKey("nvngx_dlssg.dll"));                                    // unsigned build excluded
        Assert.Equal("310.9.16.0", DlssCatalog.Parse(doc.RootElement)["nvngx_dlss.dll"].Version.ToString());
    }

    [Fact]
    public void Streamline_in_the_game_root_gets_a_valid_path()
    {
        // Regression: DLLs next to the exe in the root produced "@root/./sl.interposer.dll" (refused).
        var root = new StreamlineComponent.FolderState("", null, []);
        Assert.Equal("@root/sl.interposer.dll", root.PathOf("sl.interposer.dll"));
        var sub = new StreamlineComponent.FolderState("Bin/Win64", null, []);
        Assert.Equal("@root/Bin/Win64/sl.interposer.dll", sub.PathOf("sl.interposer.dll"));
    }
}
