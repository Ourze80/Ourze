namespace Hdlss.Core.Games;

public enum GameStore { Steam, Xbox, Manual }

public enum GraphicsApi { Unknown, DX11, DX12, Vulkan }

public enum EngineKind { Unknown, Unreal4, Unreal5, UnrealUnverified }

public sealed class Game
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Root { get; init; }
    public GameStore Store { get; init; }
    public string Exe { get; set; } = "";
    public EngineKind Engine { get; set; }
    /// <summary>Unreal project name (folder above Binaries), used to find Saved/Config.</summary>
    public string? Project { get; set; }
    /// <summary>Xbox: "PackageFamilyName!AppId" for shell:AppsFolder launching.</summary>
    public string? LaunchId { get; set; }
    public string? Cover { get; set; }
    public string? Hero { get; set; }
    /// <summary>Set when the install folder is protected (e.g. WindowsApps): nothing can be modified.</summary>
    public string? ProtectedReason { get; set; }
    public List<string> Warnings { get; } = [];

    /// <summary>
    /// Folder where the game loads proxy DLLs from, when it is not the executable's own folder
    /// (e.g. platform\steam\Game.exe loading from Project\Binaries\Win64). Full path or null.
    /// </summary>
    public string? ModFolder { get; set; }
    /// <summary>True when <see cref="ModFolder"/> was found automatically rather than chosen.</summary>
    public bool ModFolderAuto { get; set; }

    public string ExeFolder => string.IsNullOrEmpty(Exe) ? Root : Path.GetDirectoryName(Path.GetFullPath(Exe))!;
    /// <summary>Folder that receives proxy DLLs and add-ons: the mod folder, else the executable's directory.</summary>
    public string Folder => ModFolder ?? ExeFolder;
    public bool HasExe => !string.IsNullOrEmpty(Exe) && File.Exists(Exe);
    public string StoreLabel => Store switch { GameStore.Steam => "STEAM", GameStore.Xbox => "XBOX", _ => "PC" };

    public override string ToString() => Name;
}

/// <summary>User choices stored per game.</summary>
public sealed class GameProfile
{
    public string? Target { get; set; }               // exe relative to Root
    /// <summary>Mod folder relative to Root, when the game loads DLLs from somewhere else than the exe folder.</summary>
    public string? ModFolder { get; set; }
    public GraphicsApi? ApiOverride { get; set; }
    /// <summary>Unreal Saved/Config folder chosen by the user when auto-detection is ambiguous.</summary>
    public string? ConfigFolder { get; set; }
}
