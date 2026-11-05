using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Hdlss.Core.Hardware;

public sealed record GpuInfo(string Name, string Driver, int Series)
{
    public static readonly Version MinimumDriver = new(616, 56);
    public static GpuInfo Unknown { get; } = new("GPU NVIDIA RTX non rilevata", "", 0);

    public bool SupportsNr => Series is 40 or 50;
    public bool DriverTooOld => Version.TryParse(Driver, out var v) && v < MinimumDriver;

    public override string ToString() => Series == 0 ? Name : $"{Name} · driver {Driver}";

    public static GpuInfo Detect()
    {
        var smi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (!File.Exists(smi)) return Unknown;
        try
        {
            var psi = new ProcessStartInfo(smi, "--query-gpu=name,driver_version --format=csv,noheader")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { p.Kill(); return Unknown; }
            return Parse(output.Result);
        }
        catch (System.ComponentModel.Win32Exception) { return Unknown; }
    }

    public static GpuInfo Parse(string csv)
    {
        foreach (var line in csv.Split('\n'))
        {
            var m = Regex.Match(line, @"^(.+RTX\s+(20|30|40|50)\d\d[^,]*),\s*([0-9.]+)");
            if (m.Success) return new GpuInfo(m.Groups[1].Value.Trim(), m.Groups[3].Value, int.Parse(m.Groups[2].Value));
        }
        return Unknown;
    }
}
