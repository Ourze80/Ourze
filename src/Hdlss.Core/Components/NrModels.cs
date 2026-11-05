using Hdlss.Core.Hardware;
using Hdlss.Core.IO;

namespace Hdlss.Core.Components;

public sealed record NrModel(int GpuSeries, string Label, string Sha256, string Folder);

/// <summary>The bundled NR 310.9.16.0 models, picked by GPU series and verified before use.</summary>
public static class NrModels
{
    public static readonly NrModel Rtx40 = new(40, "310.9.16.0 · RTX 4000 · Compat FP16", "cdfa6c1ec784867182164c7836f324f087b5c9f374d0c9fe46f54999feabf3ed", "rtx40");
    public static readonly NrModel Rtx50 = new(50, "310.9.16.0 · RTX 5000 · Originale", "2bc19bc09a7b89dd44f73e2ac0108fcc586e2154bdd0830f242f4f040e28789f", "rtx50");
    public static readonly IReadOnlyList<NrModel> All = [Rtx40, Rtx50];

    public static NrModel ForGpu(GpuInfo gpu) => gpu.Series switch
    {
        40 => Rtx40,
        50 => Rtx50,
        _ => throw new BlockedException("Serve una GPU NVIDIA RTX 4000 o 5000 per scegliere il modello NR."),
    };

    public static NrModel? Identify(string file)
    {
        var hash = FileHash.OfFileOrNull(file);
        return All.FirstOrDefault(m => FileHash.Equal(m.Sha256, hash));
    }

    /// <summary>Finds and verifies the model file (hash, PE x64, product name and version).</summary>
    public static string Locate(NrModel model, IEnumerable<string> packageFolders)
    {
        foreach (var root in packageFolders)
        {
            var file = Path.Combine(root, model.Folder, "nvngx_dlssnr.dll");
            if (!File.Exists(file)) continue;
            var version = PeFile.FileVersion(file);
            if (!PeFile.IsPe64(file) || !FileHash.Equal(FileHash.OfFile(file), model.Sha256) || version != new Version(310, 9, 16, 0)
                || !PeFile.Identity(file).Contains("DLSSNR", StringComparison.OrdinalIgnoreCase))
                throw new HdlssException($"Il modello in {file} non corrisponde a {model.Label} (hash/versione).");
            return file;
        }
        throw new HdlssException("Cartella nr-packages non trovata. Copiala accanto a HDLSS5.exe oppure indica la cartella di HDLSS5 1.x nelle impostazioni.");
    }
}
