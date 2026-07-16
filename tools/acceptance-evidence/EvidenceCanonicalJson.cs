using System.Text.Json;

namespace PalControl.AcceptanceEvidence;

internal static class EvidenceCanonicalJson
{
    public static byte[] CreateVersionCombinationPayload(VersionCombination combination)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        }))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "$schema",
                "https://github.com/newonei/pal-control/schemas/version-combination/v1");
            writer.WriteString("caddyVersion", combination.CaddyVersion);
            writer.WriteString("configurationSha256", combination.ConfigurationSha256);
            writer.WriteString("controlApiCommit", combination.ControlApiCommit);
            writer.WriteString("deploymentPackageSha256", combination.DeploymentPackageSha256);
            writer.WriteString("nativeBridgeVersion", combination.NativeBridgeVersion);
            writer.WriteString("nativeCapability", combination.NativeCapability);
            writer.WriteString("palDefenderVersion", combination.PalDefenderVersion);
            writer.WriteString("palworldVersion", combination.PalworldVersion);
            writer.WriteString("steamBuild", combination.SteamBuild);
            writer.WriteString("ue4ssVersion", combination.Ue4ssVersion);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static byte[] CreateSignaturePayload(
        AcceptanceManifest manifest,
        string trustStoreSha256,
        string executorKeyId,
        string reviewerKeyId)
    {
        var envelope = JsonSerializer.SerializeToElement(
            manifest,
            EvidenceVerifier.SerializerOptions);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", EvidenceConstants.SignaturePayloadSchemaId);
            writer.WritePropertyName("evidenceEnvelope");
            WriteObjectWithoutProperty(writer, envelope, "signatures");
            writer.WriteString("executorAlgorithm", EvidenceConstants.SignatureAlgorithm);
            writer.WriteString("executorKeyId", executorKeyId);
            writer.WriteString("reviewerAlgorithm", EvidenceConstants.SignatureAlgorithm);
            writer.WriteString("reviewerKeyId", reviewerKeyId);
            writer.WriteString("trustStoreSha256", trustStoreSha256);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static byte[] SerializeElement(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        }))
        {
            WriteElement(writer, element);
        }
        return buffer.ToArray();
    }

    private static void WriteObjectWithoutProperty(
        Utf8JsonWriter writer,
        JsonElement element,
        string omittedProperty)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The evidence envelope must be a JSON object.");
        }
        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject()
                     .Where(property => !string.Equals(
                         property.Name,
                         omittedProperty,
                         StringComparison.Ordinal))
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            writer.WritePropertyName(property.Name);
            WriteElement(writer, property.Value);
        }
        writer.WriteEndObject();
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
                throw new JsonException("Unsupported JSON value in the evidence signature payload.");
        }
    }
}
