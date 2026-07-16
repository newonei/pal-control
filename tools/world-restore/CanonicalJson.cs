using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PalControl.WorldRestore;

internal static class CanonicalJson
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public static byte[] Serialize<T>(T value)
    {
        using var source = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions));
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        }))
        {
            WriteElement(writer, source.RootElement);
        }
        return output.ToArray();
    }

    public static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string WriteEvidence<T>(string directory, string fileName, T value)
    {
        PathSafety.EnsureSafeLeafName(fileName, "evidence file name");
        PathSafety.EnsureDirectory(directory, create: true);
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        PathSafety.EnsureStrictChild(directory, path, "evidence output");
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new InvalidOperationException($"Evidence output already exists: {path}");
        }

        var bytes = Serialize(value);
        WriteNewDurable(path, bytes);
        var hash = Sha256(bytes);
        var sidecar = path + ".sha256";
        var sidecarText = $"{hash}  {Path.GetFileName(path)}\n";
        WriteNewDurable(sidecar, Encoding.UTF8.GetBytes(sidecarText));
        return hash;
    }

    public static T ReadCanonical<T>(string path, long maximumBytes)
    {
        PathSafety.EnsureRegularFile(path, "canonical JSON input");
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > maximumBytes)
        {
            throw new InvalidDataException("Canonical JSON input has an invalid size.");
        }
        var bytes = File.ReadAllBytes(path);
        T value;
        try
        {
            value = JsonSerializer.Deserialize<T>(bytes, SerializerOptions)
                ?? throw new InvalidDataException("Canonical JSON input is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Canonical JSON input is invalid.", exception);
        }
        if (!bytes.AsSpan().SequenceEqual(Serialize(value)))
        {
            throw new InvalidDataException("JSON input is not the exact canonical representation.");
        }
        return value;
    }

    public static void VerifyHashSidecar(string path, string expectedHash)
    {
        var sidecar = path + ".sha256";
        PathSafety.EnsureRegularFile(sidecar, "SHA-256 sidecar");
        var expected = $"{expectedHash}  {Path.GetFileName(path)}\n";
        var actual = File.ReadAllText(sidecar, Encoding.UTF8);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Canonical report SHA-256 sidecar does not match the report.");
        }
    }

    public static void WriteNewDurable(string path, byte[] bytes)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidDataException("Output path has no parent directory.");
        PathSafety.EnsureDirectory(parent, create: false);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    public static string WriteCanonicalDurableReplace<T>(string path, T value)
    {
        var full = PathSafety.FullPath(path, "durable canonical JSON output");
        var parent = Path.GetDirectoryName(full)
            ?? throw new InvalidDataException("Durable output path has no parent directory.");
        PathSafety.EnsureDirectory(parent, create: false);
        PathSafety.EnsureAncestorChainHasNoReparsePoint(full);
        if (Directory.Exists(full))
        {
            throw new InvalidDataException("Durable canonical JSON output is a directory.");
        }
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Durable canonical JSON output is a reparse point.");
        }

        var bytes = Serialize(value);
        var temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.next");
        // A failed publication intentionally leaves the uniquely named .next
        // file for forensic inspection. The restore tool never deletes data.
        WriteNewDurable(temporary, bytes);
        File.Move(temporary, full, overwrite: true);
        using var committed = new FileStream(
            full,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        committed.Flush(flushToDisk: true);
        return Sha256(bytes);
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(item => item.Name, StringComparer.Ordinal))
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
                throw new InvalidDataException("Unsupported JSON value in canonical document.");
        }
    }
}
