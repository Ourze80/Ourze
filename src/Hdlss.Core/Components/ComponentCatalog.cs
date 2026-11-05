namespace Hdlss.Core.Components;

public static class ComponentCatalog
{
    public static readonly IReadOnlyList<IComponent> All =
    [
        // Neural Rendering
        new ReShadeComponent(),
        new RenoDxNrComponent(),
        new FeederComponent(),
        new OptiScalerComponent(),
        new NrModelComponent(),
        // Game DLSS
        new DlssUpdateComponent(),
        new StreamlineComponent(),
        // HDR
        new RenoDxHdrComponent(),
        new UnrealHdrComponent(),
        // Frame generation
        new MfgUnlockComponent(),
        // Shaders
        ShaderPackComponent.Lilium,
        ShaderPackComponent.Cas,
        // Mod loaders
        new Ue4ssComponent(),
        new AsiLoaderComponent(),
        new ReFrameworkComponent(),
    ];

    public static string CategoryName(ComponentCategory c) => c switch
    {
        ComponentCategory.NeuralRendering => "Neural Rendering",
        ComponentCategory.Upscaling => "DLL del gioco (DLSS e Streamline)",
        ComponentCategory.Hdr => "HDR",
        ComponentCategory.FrameGeneration => "Generazione fotogrammi",
        ComponentCategory.Shaders => "Shader ReShade",
        ComponentCategory.Loaders => "Loader per mod",
        _ => c.ToString(),
    };

    public static IComponent Get(string id) =>
        All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new HdlssException($"Componente sconosciuto: {id}");

    public static string NameOf(string id) =>
        All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;

    /// <summary>Installed components that depend on <paramref name="id"/>.</summary>
    public static IEnumerable<IComponent> Dependents(GameContext ctx, string id) =>
        All.Where(c => (c.Requires.Contains(id, StringComparer.OrdinalIgnoreCase)
                        || (id == ReShadeComponent.ComponentId && c is ComponentBase { NeedsReShade: true }))
                       && ctx.Installed(c.Id));
}
