using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hdlss.Core.Storage;

public static class JsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? Read<T>(string path) where T : class =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Options) : null;

    /// <summary>Atomic write: temp file + replace, keeping one ".bak" copy.</summary>
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
        try
        {
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(temp, path, overwrite: true);
        }
        catch (IOException) when (File.Exists(temp))
        {
            // Another writer created or replaced the file in between: last write wins.
            File.Move(temp, path, overwrite: true);
        }
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
}
