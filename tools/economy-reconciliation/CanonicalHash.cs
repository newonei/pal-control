using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PalControl.EconomyReconciliation;

internal static class CanonicalHash
{
    public const string Version = "pal-control-economy-canonical-v1";

    public static string Json(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        });
        WriteElement(writer, element);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string Domain(string category, string canonical) =>
        Sha256($"{Version}\n{category}\n{canonical}");

    public static string Key(string category, string canonicalKey) =>
        Sha256($"{Version}\nkey\n{category}\n{canonicalKey}");

    public static string Aggregate(
        string category,
        IEnumerable<AuditRowHash> rows)
    {
        var canonical = string.Join(
            '\n',
            rows.OrderBy(row => row.KeyFingerprint, StringComparer.Ordinal)
                .ThenBy(row => row.CanonicalHash, StringComparer.Ordinal)
                .Select(row => $"{row.KeyFingerprint}:{row.CanonicalHash}"));
        return Domain($"aggregate:{category}", canonical);
    }

    public static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    public static JsonElement ToElement<T>(T value, JsonSerializerOptions options) =>
        JsonSerializer.SerializeToElement(value, options);

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                writer.WriteStartObject();
                var properties = element.EnumerateObject().ToArray();
                if (properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != properties.Length)
                {
                    throw new InvalidDataException(
                        "Canonical JSON cannot contain duplicate object property names.");
                }
                foreach (var property in properties.OrderBy(
                             property => property.Name,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            }
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
                WriteNumber(writer, element);
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
                throw new InvalidDataException(
                    $"Unsupported JSON token in canonical input: {element.ValueKind}.");
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.TryGetInt64(out var integer))
        {
            writer.WriteNumberValue(integer);
            return;
        }
        if (element.TryGetDecimal(out var decimalValue))
        {
            writer.WriteRawValue(
                decimalValue.ToString("G29", CultureInfo.InvariantCulture),
                skipInputValidation: false);
            return;
        }
        if (!element.TryGetDouble(out var doubleValue) ||
            double.IsNaN(doubleValue) ||
            double.IsInfinity(doubleValue))
        {
            throw new InvalidDataException("Canonical JSON contains an invalid number.");
        }
        writer.WriteRawValue(
            doubleValue.ToString("R", CultureInfo.InvariantCulture),
            skipInputValidation: false);
    }
}
