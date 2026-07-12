using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PalControl.ControlApi.Domain;

namespace PalControl.ControlApi.Infrastructure;

public sealed class InGameNotificationCapabilityService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NativeBridgeState _bridgeState;
    private readonly NativeBridgeClient _bridge;

    public InGameNotificationCapabilityService(
        NativeBridgeState bridgeState,
        NativeBridgeClient bridge)
    {
        _bridgeState = bridgeState;
        _bridge = bridge;
    }

    public async Task<InGameNotificationProbeOutcome> ProbeAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        var snapshot = _bridgeState.GetSnapshot();
        if (!snapshot.Connected ||
            !snapshot.Capabilities.Contains("ui.notifications.probe") ||
            !snapshot.Capabilities.Contains("ui.notifications.write"))
        {
            return new InGameNotificationProbeOutcome(
                null,
                new ApiError(
                    "NATIVE_NOTIFICATION_PRESET_PROBE_UNAVAILABLE",
                    $"Native in-game notification presets are unavailable for server '{serverId}'."));
        }

        NativeBridgeResult result;
        try
        {
            result = await _bridge.SendCommandAsync(
                serverId,
                "ui.notifications.probe",
                new { },
                "read-only server-native notification preset capability probe",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return new InGameNotificationProbeOutcome(
                null,
                new ApiError(
                    "NATIVE_NOTIFICATION_PRESET_PROBE_UNAVAILABLE",
                    exception is TimeoutException
                        ? "Native in-game notification preset probe timed out."
                        : "Native in-game notification preset probe connection ended."));
        }

        if (!string.Equals(result.State, "succeeded", StringComparison.Ordinal) ||
            result.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return new InGameNotificationProbeOutcome(
                null,
                new ApiError(
                    result.Error?.Code ?? "NATIVE_NOTIFICATION_PRESET_PROBE_FAILED",
                    result.Error?.Message ?? "Native in-game notification preset probe failed."));
        }

        InGameNotificationCapabilityProbe? probe;
        try
        {
            probe = data.Deserialize<InGameNotificationCapabilityProbe>(JsonOptions);
        }
        catch (JsonException)
        {
            probe = null;
        }

        var structureError = ValidateProbeStructure(probe);
        return structureError is null
            ? new InGameNotificationProbeOutcome(probe, null)
            : new InGameNotificationProbeOutcome(null, structureError);
    }

    private static ApiError? ValidateProbeStructure(InGameNotificationCapabilityProbe? probe)
    {
        if (probe is null ||
            !probe.Ready ||
            probe.Dispatched ||
            !string.Equals(probe.Mode, "server-native-presets", StringComparison.Ordinal) ||
            probe.SchemaVersions.Count == 0 ||
            probe.SchemaVersions.Any(string.IsNullOrWhiteSpace) ||
            probe.SchemaVersions.Distinct(StringComparer.Ordinal).Count() != probe.SchemaVersions.Count ||
            probe.SupportedAudiences.Count == 0 ||
            probe.SupportedAudiences.Any(string.IsNullOrWhiteSpace) ||
            probe.SupportedAudiences.Distinct(StringComparer.Ordinal).Count() != probe.SupportedAudiences.Count ||
            probe.SupportedPresets.Count == 0 ||
            probe.SupportedPresets.Any(preset =>
                string.IsNullOrWhiteSpace(preset.Name) ||
                string.IsNullOrWhiteSpace(preset.DisplayName) ||
                string.IsNullOrWhiteSpace(preset.Description) ||
                string.IsNullOrWhiteSpace(preset.Function) ||
                preset.FunctionFlags <= 0 ||
                preset.PropertiesSize < 0 ||
                preset.PositionPolicy is null ||
                preset.DurationPolicy is null ||
                !string.Equals(preset.PositionPolicy.Mode, "game-defined", StringComparison.Ordinal) ||
                !string.Equals(preset.DurationPolicy.Mode, "game-defined", StringComparison.Ordinal) ||
                preset.PositionPolicy.Configurable ||
                preset.DurationPolicy.Configurable ||
                string.IsNullOrWhiteSpace(preset.PositionPolicy.Note) ||
                string.IsNullOrWhiteSpace(preset.DurationPolicy.Note)) ||
            probe.SupportedPresets.Select(preset => preset.Name)
                .Distinct(StringComparer.Ordinal).Count() != probe.SupportedPresets.Count)
        {
            return new ApiError(
                "NATIVE_NOTIFICATION_PRESET_PROBE_INVALID",
                "Native notification preset capabilities did not match the fail-closed server-native schema.");
        }

        foreach (var preset in probe.SupportedPresets)
        {
            if (preset.Parameters.Any(parameter =>
                    string.IsNullOrWhiteSpace(parameter.Name) ||
                    string.IsNullOrWhiteSpace(parameter.Type)) ||
                preset.Parameters.Select(parameter => parameter.Name)
                    .Distinct(StringComparer.Ordinal).Count() != preset.Parameters.Count)
            {
                return new ApiError(
                    "NATIVE_NOTIFICATION_PRESET_PROBE_INVALID",
                    $"Native notification preset '{preset.Name}' has invalid parameter capabilities.");
            }
        }

        return null;
    }
}

public static class InGameNotificationContract
{
    private static readonly HashSet<string> SupportedParameterTypes =
        new(["string", "integer", "number", "boolean", "string-array"], StringComparer.Ordinal);

    public static ApiError? ValidateShape(InGameNotificationInput input)
    {
        if (HasAdditionalProperties(input.AdditionalProperties) ||
            input.Template is null ||
            HasAdditionalProperties(input.Template.AdditionalProperties) ||
            input.Audience is null ||
            HasAdditionalProperties(input.Audience.AdditionalProperties))
        {
            return new ApiError(
                "UNSUPPORTED_NOTIFICATION_OVERRIDE",
                "In-game notifications accept only server-native preset fields; custom style or unknown overrides are not supported.");
        }
        if (!string.Equals(input.SchemaVersion?.Trim(), "1", StringComparison.Ordinal))
        {
            return new ApiError(
                "UNSUPPORTED_NOTIFICATION_SCHEMA",
                "schemaVersion must be '1'.");
        }
        if (string.IsNullOrWhiteSpace(input.Template.Preset) ||
            input.Template.Preset.Trim().Length > 100)
        {
            return new ApiError(
                "INVALID_NOTIFICATION_PRESET",
                "A server-native notification preset name with at most 100 characters is required.");
        }
        if (input.Template.Parameters.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(input.Template.Parameters.GetRawText()) > 16_384)
        {
            return new ApiError(
                "INVALID_NOTIFICATION_PARAMETERS",
                "Notification preset parameters must be a JSON object no larger than 16384 bytes.");
        }
        if (string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length is < 3 or > 500)
        {
            return new ApiError(
                "INVALID_NOTIFICATION_REASON",
                "A human-readable reason between 3 and 500 characters is required.");
        }

        var audienceType = input.Audience.Type?.Trim().ToLowerInvariant();
        var audienceIds = input.Audience.Ids?
            .Select(id => id?.Trim())
            .ToArray();
        if (audienceType == "global")
        {
            if (input.Audience.Ids is { Count: > 0 })
            {
                return new ApiError(
                    "INVALID_NOTIFICATION_AUDIENCE",
                    "The global notification audience cannot contain target IDs.");
            }
        }
        else if (audienceType == "players")
        {
            if (audienceIds is not { Length: > 0 and <= 100 } ||
                audienceIds.Any(id => string.IsNullOrEmpty(id) || id.Length > 128) ||
                audienceIds.Distinct(StringComparer.Ordinal).Count() != audienceIds.Length)
            {
                return new ApiError(
                    "INVALID_NOTIFICATION_AUDIENCE",
                    "The players audience requires 1 to 100 unique player IDs of at most 128 characters.");
            }
        }
        else
        {
            return new ApiError(
                "INVALID_NOTIFICATION_AUDIENCE",
                "Notification audience type must be global or players.");
        }

        if (input.ExpiresAt is { } expiresAt &&
            expiresAt <= (input.DisplayAt ?? DateTimeOffset.UtcNow))
        {
            return new ApiError(
                "INVALID_NOTIFICATION_EXPIRY",
                "expiresAt must be later than displayAt.");
        }

        return null;
    }

    public static ApiError? ValidateAgainstProbe(
        InGameNotificationInput input,
        InGameNotificationCapabilityProbe probe)
    {
        if (!probe.SchemaVersions.Contains(input.SchemaVersion.Trim(), StringComparer.Ordinal))
        {
            return new ApiError(
                "UNSUPPORTED_NOTIFICATION_SCHEMA",
                $"Native notification presets do not support schemaVersion '{input.SchemaVersion}'.");
        }

        var audienceType = input.Audience.Type.Trim().ToLowerInvariant();
        if (!probe.SupportedAudiences.Contains(audienceType, StringComparer.Ordinal))
        {
            return new ApiError(
                "UNSUPPORTED_NOTIFICATION_AUDIENCE",
                $"Native notification presets do not support audience '{audienceType}'.");
        }

        var presetName = input.Template.Preset.Trim().ToLowerInvariant();
        var preset = probe.SupportedPresets.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, presetName, StringComparison.Ordinal));
        if (preset is null)
        {
            return new ApiError(
                "UNSUPPORTED_NOTIFICATION_PRESET",
                $"Native notification preset '{presetName}' is not available in the loaded game build.");
        }

        var capabilities = preset.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        var supplied = input.Template.Parameters.EnumerateObject().ToArray();
        var unknown = supplied
            .Where(property => !capabilities.ContainsKey(property.Name))
            .Select(property => property.Name)
            .ToArray();
        if (unknown.Length > 0)
        {
            return new ApiError(
                "UNSUPPORTED_NOTIFICATION_PARAMETER",
                $"Preset '{presetName}' does not support parameters: {string.Join(", ", unknown)}.");
        }

        foreach (var capability in preset.Parameters)
        {
            var property = supplied.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, capability.Name, StringComparison.Ordinal));
            if (property.Name is null)
            {
                if (capability.Required)
                {
                    return new ApiError(
                        "NOTIFICATION_PARAMETER_REQUIRED",
                        $"Preset '{presetName}' requires parameter '{capability.Name}'.");
                }
                continue;
            }

            var validation = ValidateParameter(presetName, capability, property.Value);
            if (validation is not null)
            {
                return validation;
            }
        }

        return null;
    }

    public static InGameNotificationInput Normalize(InGameNotificationInput input) => new()
    {
        SchemaVersion = input.SchemaVersion.Trim(),
        Template = new InGameNotificationTemplate
        {
            Preset = input.Template.Preset.Trim().ToLowerInvariant(),
            Parameters = input.Template.Parameters.Clone()
        },
        Audience = new InGameNotificationAudience
        {
            Type = input.Audience.Type.Trim().ToLowerInvariant(),
            Ids = input.Audience.Ids?
                .Select(id => id?.Trim())
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray()
        },
        DisplayAt = input.DisplayAt?.ToUniversalTime(),
        ExpiresAt = input.ExpiresAt?.ToUniversalTime(),
        Reason = input.Reason.Trim()
    };

    public static string HashInput(string serverId, InGameNotificationInput input)
    {
        var normalized = Normalize(input);
        var canonical = new StringBuilder();
        canonical.Append("in-game-notification.create.v1\n")
            .Append(serverId).Append('\n')
            .Append(normalized.SchemaVersion).Append('\n')
            .Append(normalized.Template.Preset).Append('\n')
            .Append(CanonicalJson(normalized.Template.Parameters)).Append('\n')
            .Append(normalized.Audience.Type).Append('\n')
            .Append(string.Join("\n", normalized.Audience.Ids ?? [])).Append('\n')
            .Append(normalized.DisplayAt?.ToString("O") ?? string.Empty).Append('\n')
            .Append(normalized.ExpiresAt?.ToString("O") ?? string.Empty).Append('\n')
            .Append(normalized.Reason);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    public static string HashDispatch(string serverId, InGameNotification notification)
    {
        var canonical = string.Join('\n',
            "in-game-notification.dispatch.v1",
            serverId,
            notification.NotificationId.ToString("D"),
            notification.SchemaVersion,
            notification.Template.Preset,
            CanonicalJson(notification.Template.Parameters),
            notification.Audience.Type,
            string.Join("\n", notification.Audience.Ids ?? []),
            notification.DisplayAt?.ToString("O") ?? string.Empty,
            notification.ExpiresAt?.ToString("O") ?? string.Empty,
            notification.Reason);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static InGameNotificationInput ToInput(InGameNotification notification) => new()
    {
        SchemaVersion = notification.SchemaVersion,
        Template = notification.Template,
        Audience = notification.Audience,
        DisplayAt = notification.DisplayAt,
        ExpiresAt = notification.ExpiresAt,
        Reason = notification.Reason
    };

    private static ApiError? ValidateParameter(
        string presetName,
        InGameNotificationParameterCapability capability,
        JsonElement value)
    {
        if (!SupportedParameterTypes.Contains(capability.Type))
        {
            return new ApiError(
                "NATIVE_NOTIFICATION_PRESET_PROBE_INVALID",
                $"Preset '{presetName}' advertised unsupported parameter type '{capability.Type}'.");
        }

        bool typeValid;
        double? numeric = null;
        int? length = null;
        switch (capability.Type)
        {
            case "string":
                typeValid = value.ValueKind == JsonValueKind.String;
                length = typeValid ? value.GetString()!.Length : null;
                break;
            case "integer":
                long integer = 0;
                typeValid = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out integer);
                numeric = typeValid ? integer : null;
                break;
            case "number":
                double number = 0;
                typeValid = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number);
                numeric = typeValid ? number : null;
                break;
            case "boolean":
                typeValid = value.ValueKind is JsonValueKind.True or JsonValueKind.False;
                break;
            case "string-array":
                typeValid = value.ValueKind == JsonValueKind.Array &&
                    value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String);
                length = typeValid ? value.GetArrayLength() : null;
                break;
            default:
                typeValid = false;
                break;
        }

        if (!typeValid)
        {
            return new ApiError(
                "INVALID_NOTIFICATION_PARAMETER",
                $"Preset '{presetName}' parameter '{capability.Name}' must be {capability.Type}.");
        }
        if (length < capability.MinLength || length > capability.MaxLength ||
            numeric < capability.Minimum || numeric > capability.Maximum)
        {
            return new ApiError(
                "INVALID_NOTIFICATION_PARAMETER",
                $"Preset '{presetName}' parameter '{capability.Name}' is outside its advertised bounds.");
        }
        if (capability.AllowedValues is { ValueKind: JsonValueKind.Array } allowed &&
            !allowed.EnumerateArray().Any(candidate =>
                string.Equals(CanonicalJson(candidate), CanonicalJson(value), StringComparison.Ordinal)))
        {
            return new ApiError(
                "INVALID_NOTIFICATION_PARAMETER",
                $"Preset '{presetName}' parameter '{capability.Name}' is not one of its advertised values.");
        }
        if (capability.Pattern is { Length: > 0 } pattern &&
            value.ValueKind == JsonValueKind.String)
        {
            try
            {
                if (!Regex.IsMatch(
                        value.GetString()!,
                        pattern,
                        RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100)))
                {
                    return new ApiError(
                        "INVALID_NOTIFICATION_PARAMETER",
                        $"Preset '{presetName}' parameter '{capability.Name}' does not match its advertised format.");
                }
            }
            catch (ArgumentException)
            {
                return new ApiError(
                    "NATIVE_NOTIFICATION_PRESET_PROBE_INVALID",
                    $"Preset '{presetName}' advertised an invalid pattern for parameter '{capability.Name}'.");
            }
        }

        return null;
    }

    private static bool HasAdditionalProperties(Dictionary<string, JsonElement>? properties) =>
        properties is { Count: > 0 };

    private static string CanonicalJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, element);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
