using Hdlss.Core.Text;
using System.Diagnostics;
using System.Text.Json;
using Hdlss.Core.Games;
using Hdlss.Core.IO;

namespace Hdlss.Core.Components;

/// <summary>
/// Per-game Vulkan layers. Layers are never registered machine-wide: the game is started
/// by HDLSS with VK_LAYER_PATH/VK_INSTANCE_LAYERS set only for that child process.
/// </summary>
public static class Vulkan
{
    public const string Dir = "hdlss-vulkan";
    public const string ReShadeDll = Dir + "/ReShade64.dll";
    public const string ReShadeJson = Dir + "/ReShade64.json";
    public const string FeedDll = Dir + "/VkLayer_feed_vk.dll";
    public const string FeedJson = Dir + "/VkLayer_feed_vk.json";
    public const string ReShadeLayer = "VK_LAYER_reshade";
    public const string FeedLayer = "VK_LAYER_feed_vk";

    public static byte[] LayerManifest(string name, string library, bool reshade)
    {
        var layer = new Dictionary<string, object>
        {
            ["name"] = name, ["type"] = "GLOBAL", ["library_path"] = ".\\" + library,
            ["api_version"] = "1.3.268", ["implementation_version"] = "1",
            ["description"] = reshade ? "ReShade - HDLSS per-game Vulkan" : "DLSS5 Feeder - HDLSS per-game Vulkan",
        };
        if (reshade)
            layer["device_extensions"] = new[] { new Dictionary<string, object> { ["name"] = "VK_EXT_tooling_info", ["spec_version"] = "1", ["entrypoints"] = new[] { "vkGetPhysicalDeviceToolPropertiesEXT" } } };
        else
            layer["functions"] = new Dictionary<string, string> { ["vkNegotiateLoaderLayerInterfaceVersion"] = "vkNegotiateLoaderLayerInterfaceVersion" };
        return JsonSerializer.SerializeToUtf8Bytes(new { file_format_version = "1.2.0", layer }, new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool UsesLayers(GameContext ctx) => ctx.Installed(ReShadeComponent.ComponentId) && ctx.State.Get(ReShadeComponent.ComponentId)!.FindFile(ReShadeDll) != null;

    public static ProcessStartInfo StartInfo(GameContext ctx)
    {
        if (!UsesLayers(ctx)) throw new BlockedException(Tx.Get("vk.installFirst"));
        foreach (var record in ctx.State.Components.Values)
            foreach (var f in record.Files.Where(f => f.Path.StartsWith(Dir + "/", StringComparison.OrdinalIgnoreCase)))
                if (!FileHash.Equal(FileHash.OfFileOrNull(PathGuard.Resolve(ctx.Folder, f.Path)), f.After))
                    throw new HdlssException(Tx.Get("vk.layerModified", f.Path));
        var layers = ReShadeLayer;
        if (ctx.State.Components.Values.Any(c => c.FindFile(FeedJson) != null)) layers += ";" + FeedLayer;
        var psi = new ProcessStartInfo(ctx.Game.Exe) { WorkingDirectory = ctx.Game.ExeFolder, UseShellExecute = false };
        Prepend(psi, "VK_LAYER_PATH", Path.Combine(ctx.Folder, Dir));
        Prepend(psi, "VK_INSTANCE_LAYERS", layers);
        return psi;
    }

    private static void Prepend(ProcessStartInfo psi, string name, string value)
    {
        var prior = psi.Environment.TryGetValue(name, out var v) ? v : null;
        psi.Environment[name] = string.IsNullOrEmpty(prior) ? value : value + ";" + prior;
    }
}

public static class GameLauncher
{
    public static void Launch(GameContext ctx)
    {
        if (ctx.Api == GraphicsApi.Vulkan && Vulkan.UsesLayers(ctx)) { Process.Start(Vulkan.StartInfo(ctx)); return; }
        Launch(ctx.Game, ctx.Services.Settings);
    }

    /// <summary>Launch through the game's own store (Steam/Xbox) or the executable.</summary>
    public static void Launch(Game game, Settings.AppSettings settings)
    {
        if (game.Store == GameStore.Xbox)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", XboxLibrary.LaunchArgument(game)) { UseShellExecute = true });
            return;
        }
        if (game.Store == GameStore.Steam && System.Text.RegularExpressions.Regex.IsMatch(game.Id, "^[0-9]+$"))
        {
            var steam = Path.Combine(GameLibrary.SteamRoot(), "steam.exe");
            if (!File.Exists(steam)) throw new HdlssException(Tx.Get("vk.noSteam"));
            var args = "-applaunch " + game.Id;
            var api = settings.Profile(game.Id).ApiOverride;
            if (api != null && game.Engine is EngineKind.Unreal5 or EngineKind.Unreal4 or EngineKind.UnrealUnverified)
                args += api switch { GraphicsApi.DX11 => " -dx11", GraphicsApi.DX12 => " -dx12", GraphicsApi.Vulkan => " -vulkan", _ => "" };
            Process.Start(new ProcessStartInfo(steam, args) { UseShellExecute = true });
            return;
        }
        if (!game.HasExe) throw new HdlssException(Tx.Get("vk.noExe"));
        Process.Start(new ProcessStartInfo(game.Exe) { WorkingDirectory = game.ExeFolder, UseShellExecute = true });
    }

    /// <summary>Throws if a process with the game's executable name is running.</summary>
    public static void EnsureClosed(Game game)
    {
        var name = Path.GetFileNameWithoutExtension(game.Exe);
        if (string.IsNullOrEmpty(name)) return;
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                if (p.Id != Environment.ProcessId) throw new BlockedException(Tx.Get("vk.close", game.Name));
            }
        }
    }
}
