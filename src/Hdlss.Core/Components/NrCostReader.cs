using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Hdlss.Core.Components;

/// <summary>GPU cost of the DLSS 5 NR add-on, as measured by the add-on itself.</summary>
/// <param name="GpuMs">Milliseconds of GPU time added per frame.</param>
/// <param name="FpsWithout">Estimated FPS without NR (0 when the log has no estimate).</param>
/// <param name="FpsWith">Estimated FPS with NR (0 when the log has no estimate).</param>
public sealed record NrCost(double GpuMs, int FpsWithout, int FpsWith)
{
    public bool HasFps => FpsWithout > 0 && FpsWith > 0;
}

/// <summary>Reads the last "NR cost:" line from ReShade.log.</summary>
public static partial class NrCostReader
{
    private const int TailBytes = 256 * 1024;

    [GeneratedRegex(@"gpu_ms=(?<ms>\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex GpuMsRx();

    [GeneratedRegex(@"estimate:\s*(?<a>\d+)\s*fps without NR,\s*(?<b>\d+)\s*fps with it", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FpsRx();

    public static NrCost? Read(string reshadeLogPath)
    {
        try
        {
            if (!File.Exists(reshadeLogPath)) return null;
            using var fs = new FileStream(reshadeLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, fs.Length - TailBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[fs.Length - start];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = fs.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return Parse(Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Finds the last parseable "NR cost:" line in <paramref name="text"/>.</summary>
    public static NrCost? Parse(string text)
    {
        var lines = text.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("NR cost:", StringComparison.Ordinal)) continue;
            var ms = GpuMsRx().Match(line);
            if (!ms.Success || !double.TryParse(ms.Groups["ms"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var gpu)) continue;
            var fps = FpsRx().Match(line);
            int a = 0, b = 0;
            if (fps.Success)
            {
                int.TryParse(fps.Groups["a"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out a);
                int.TryParse(fps.Groups["b"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out b);
            }
            return new NrCost(gpu, a, b);
        }
        return null;
    }
}
