using System.Security.Cryptography;
using System.Text.Json;

namespace PalControl.Soak;

public static class CanonicalJson
{
    public const string CanonicalizationId = "pal-control-soak-canonical-json-v1";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static byte[] Serialize<T>(T value)
    {
        var source = JsonSerializer.SerializeToElement(value, SerializerOptions);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        }))
        {
            WriteElement(writer, source);
        }
        return buffer.ToArray();
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static void WriteReport(string outputDirectory, SoakReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var fullOutput = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(fullOutput) || File.Exists(fullOutput))
        {
            throw new IOException("The immutable soak evidence directory already exists.");
        }
        var parent = Path.GetDirectoryName(fullOutput)
            ?? throw new IOException("The soak evidence directory has no parent.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(
            parent,
            $".{Path.GetFileName(fullOutput)}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var reportPath = Path.Combine(staging, "report.json");
        var hashPath = Path.Combine(staging, "report.json.sha256");
        var bytes = Serialize(report);
        var hash = Sha256Hex(bytes);
        WriteAtomic(reportPath, bytes);
        WriteAtomic(hashPath, System.Text.Encoding.ASCII.GetBytes(hash + "\n"));
        Directory.Move(staging, fullOutput);
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("Unsupported value in canonical soak report.");
        }
    }

    private static void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Evidence is append-only at the directory level. A repeated run
            // must use a new directory instead of overwriting a prior result.
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
