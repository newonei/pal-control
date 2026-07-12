using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalControl.ControlApi.Infrastructure;

public sealed class ServerConfigurationService
{
    private const int SchemaVersion = 2;
    private const int MaxChanges = 256;
    private const int MaxStringLength = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.Ordinal)
    {
        "AdminPassword",
        "ServerPassword"
    };
    private static readonly Dictionary<string, string[]> EnumValues = new(StringComparer.Ordinal)
    {
        ["Difficulty"] = ["None"],
        ["DeathPenalty"] = ["None", "Item", "ItemAndEquipment", "All"],
        ["LogFormatType"] = ["Text", "Json"],
        ["RandomizerType"] = ["None", "Region", "All"]
    };
    private static readonly string[] CrossplayPlatforms = ["Steam", "Xbox", "PS5", "Mac"];

    private readonly string _path;
    private readonly string _defaultPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ServerConfigurationService(IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredRoot = configuration["Palworld:InstallRoot"] ?? "../../../PalServer";
        var installRoot = Path.GetFullPath(Path.IsPathRooted(configuredRoot)
            ? configuredRoot
            : Path.Combine(environment.ContentRootPath, configuredRoot));
        _path = Path.Combine(
            installRoot,
            "Pal/Saved/Config/WindowsServer/PalWorldSettings.ini");
        _defaultPath = Path.Combine(installRoot, "DefaultPalWorldSettings.ini");
    }

    public async Task<ServerConfigurationDocument> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadRequiredAsync(_path, cancellationToken);
            var defaults = await LoadOptionalAsync(_defaultPath, cancellationToken);
            return BuildDocument(current, defaults);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServerConfigurationDocument> UpdateAsync(
        ServerConfigurationUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (string.IsNullOrWhiteSpace(update.Revision))
        {
            throw new ArgumentException("revision 不能为空。", nameof(update));
        }
        if (update.Changes is null)
        {
            throw new ArgumentException("changes 不能为空。", nameof(update));
        }
        if (update.Changes.Count > MaxChanges)
        {
            throw new ArgumentException($"单次最多修改 {MaxChanges} 个配置项。", nameof(update));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadRequiredAsync(_path, cancellationToken);
            if (!string.Equals(update.Revision, current.Revision, StringComparison.Ordinal))
            {
                throw new ConfigurationRevisionConflictException(update.Revision, current.Revision);
            }

            var defaults = await LoadOptionalAsync(_defaultPath, cancellationToken);
            var currentByKey = current.Settings.ByKey;
            var defaultByKey = defaults?.Settings.ByKey
                ?? new Dictionary<string, SettingEntry>(StringComparer.Ordinal);
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (key, value) in update.Changes)
            {
                currentByKey.TryGetValue(key, out var currentEntry);
                defaultByKey.TryGetValue(key, out var defaultEntry);
                if (currentEntry is null && defaultEntry is null)
                {
                    throw new ArgumentException($"配置项 '{key}' 不存在于当前或默认配置文件中。", nameof(update));
                }

                var sample = defaultEntry?.RawValue ?? currentEntry?.RawValue ?? string.Empty;
                var rule = GetRule(key, sample);
                replacements[key] = SerializeChange(key, value, rule);
            }

            if (replacements.Count == 0)
            {
                return BuildDocument(current, defaults);
            }

            var outputText = current.Settings.Render(replacements);
            _ = ParseSettings(outputText);
            var outputBytes = EncodeUtf8(outputText, current.HasUtf8Bom);
            await WriteAtomicallyAsync(outputBytes, current.Revision, cancellationToken);

            var written = await LoadRequiredAsync(_path, cancellationToken);
            return BuildDocument(written, defaults);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ServerConfigurationDocument BuildDocument(LoadedSettings current, LoadedSettings? defaults)
    {
        var currentByKey = current.Settings.ByKey;
        var defaultByKey = defaults?.Settings.ByKey
            ?? new Dictionary<string, SettingEntry>(StringComparer.Ordinal);
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (defaults is not null)
        {
            foreach (var entry in defaults.Settings.Entries)
            {
                if (seen.Add(entry.Key))
                {
                    keys.Add(entry.Key);
                }
            }
        }
        foreach (var entry in current.Settings.Entries)
        {
            if (seen.Add(entry.Key))
            {
                keys.Add(entry.Key);
            }
        }

        var options = new List<ServerConfigurationOption>(keys.Count);
        foreach (var key in keys)
        {
            currentByKey.TryGetValue(key, out var currentEntry);
            defaultByKey.TryGetValue(key, out var defaultEntry);
            var sample = defaultEntry?.RawValue ?? currentEntry?.RawValue ?? string.Empty;
            var rule = GetRule(key, sample);
            var sensitive = rule.Sensitive;
            var hasCurrent = currentEntry is not null;
            var currentRaw = currentEntry?.RawValue ?? defaultEntry?.RawValue ?? string.Empty;
            var hasValue = sensitive
                ? hasCurrent && DecodeString(currentEntry!.RawValue).Length > 0
                : hasCurrent;
            object? value = sensitive ? null : ParseValue(key, currentRaw, rule);
            object? defaultValue = sensitive || defaultEntry is null
                ? null
                : ParseValue(key, defaultEntry.RawValue, rule);
            var customized = hasCurrent
                && (defaultEntry is null || !ValuesEquivalent(key, currentEntry!.RawValue, defaultEntry.RawValue, rule));

            options.Add(new ServerConfigurationOption(
                key,
                rule.Kind,
                value,
                defaultValue,
                sensitive,
                hasValue,
                rule.AllowedValues,
                rule.Minimum,
                rule.Maximum,
                rule.Step,
                customized));
        }

        return new ServerConfigurationDocument(
            SchemaVersion,
            current.Revision,
            options,
            _path,
            _defaultPath,
            File.GetLastWriteTimeUtc(_path));
    }

    private async Task WriteAtomicallyAsync(
        byte[] output,
        string expectedRevision,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("配置文件目录无效。");
        var nonce = Guid.NewGuid().ToString("N");
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.pal-control.{nonce}.tmp");
        var backupPath = _path
            + ".pal-control."
            + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
            + "."
            + nonce
            + ".bak";

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(output, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            var latestBytes = await File.ReadAllBytesAsync(_path, cancellationToken);
            var latestRevision = ComputeRevision(latestBytes);
            if (!string.Equals(expectedRevision, latestRevision, StringComparison.Ordinal))
            {
                throw new ConfigurationRevisionConflictException(expectedRevision, latestRevision);
            }

            File.Replace(tempPath, _path, backupPath, ignoreMetadataErrors: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string SerializeChange(string key, JsonElement value, SettingRule rule)
    {
        if (rule.Sensitive && value.ValueKind == JsonValueKind.Null)
        {
            return Quote(string.Empty);
        }

        return rule.Kind switch
        {
            "boolean" => SerializeBoolean(value),
            "integer" => SerializeInteger(key, value, rule),
            "number" => SerializeNumber(key, value, rule),
            "enum" => SerializeEnum(key, value, rule),
            "string-list" => SerializeStringList(key, value, rule),
            "string" => Quote(ValidateString(key, value, rule.Sensitive)),
            _ => throw new InvalidDataException($"配置项 '{key}' 的类型 '{rule.Kind}' 不受支持。")
        };
    }

    private static string SerializeBoolean(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => throw new ArgumentException("布尔配置项必须为 true 或 false。")
        };
    }

    private static string SerializeInteger(string key, JsonElement value, SettingRule rule)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw new ArgumentException($"配置项 '{key}' 必须为整数。");
        }
        ValidateRange(key, number, rule);
        return number.ToString(CultureInfo.InvariantCulture);
    }

    private static string SerializeNumber(string key, JsonElement value, SettingRule rule)
    {
        if (value.ValueKind != JsonValueKind.Number
            || !decimal.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            throw new ArgumentException($"配置项 '{key}' 必须为有限数字。");
        }
        ValidateRange(key, number, rule);
        return number.ToString("0.############################", CultureInfo.InvariantCulture);
    }

    private static string SerializeEnum(string key, JsonElement value, SettingRule rule)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"配置项 '{key}' 必须为枚举字符串。");
        }
        var text = value.GetString() ?? string.Empty;
        if (rule.AllowedValues is null || !rule.AllowedValues.Contains(text, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"配置项 '{key}' 只允许：{string.Join("、", rule.AllowedValues ?? [])}。");
        }
        return text;
    }

    private static string SerializeStringList(string key, JsonElement value, SettingRule rule)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"配置项 '{key}' 必须为字符串数组。");
        }

        var items = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException($"配置项 '{key}' 的每个元素都必须是字符串。");
            }
            var item = (element.GetString() ?? string.Empty).Trim();
            if (item.Length == 0 || item.Length > 120 || item.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':' or '/')))
            {
                throw new ArgumentException($"配置项 '{key}' 包含无效列表元素。");
            }
            if (!unique.Add(item))
            {
                throw new ArgumentException($"配置项 '{key}' 不能包含重复元素 '{item}'。");
            }
            if (rule.AllowedValues is not null && !rule.AllowedValues.Contains(item, StringComparer.Ordinal))
            {
                throw new ArgumentException($"配置项 '{key}' 不允许列表元素 '{item}'。");
            }
            items.Add(item);
        }
        if (items.Count > 256)
        {
            throw new ArgumentException($"配置项 '{key}' 的列表元素不能超过 256 个。");
        }
        if (items.Count == 0 && string.Equals(key, "CrossplayPlatforms", StringComparison.Ordinal))
        {
            throw new ArgumentException("CrossplayPlatforms 至少要保留一个平台。");
        }

        if (items.Count == 0 && string.Equals(key, "DenyTechnologyList", StringComparison.Ordinal))
        {
            return string.Empty;
        }
        var serializedItems = string.Equals(key, "DenyTechnologyList", StringComparison.Ordinal)
            ? items.Select(Quote)
            : items;
        return "(" + string.Join(',', serializedItems) + ")";
    }

    private static string ValidateString(string key, JsonElement value, bool sensitive)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"配置项 '{key}' 必须为字符串；敏感字段可用 null 清空。");
        }
        var text = value.GetString() ?? string.Empty;
        var limit = key switch
        {
            "ServerName" => 100,
            "ServerDescription" => 500,
            "AdminPassword" or "ServerPassword" => 256,
            "BanListURL" => 2048,
            _ => MaxStringLength
        };
        if (text.Length > limit)
        {
            throw new ArgumentException($"配置项 '{key}' 不能超过 {limit} 个字符。");
        }
        if (text.Any(ch => ch is '\r' or '\n' or '\0' || (char.IsControl(ch) && ch != '\t')))
        {
            throw new ArgumentException($"配置项 '{key}' 包含不允许的控制字符。");
        }
        if (!sensitive && string.Equals(key, "ServerName", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("ServerName 不能为空。");
        }
        if (string.Equals(key, "BanListURL", StringComparison.Ordinal)
            && text.Length > 0
            && (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new ArgumentException("BanListURL 必须是 HTTP 或 HTTPS 绝对地址。");
        }
        return text;
    }

    private static void ValidateRange(string key, decimal value, SettingRule rule)
    {
        if (rule.Minimum is decimal minimum && value < minimum)
        {
            throw new ArgumentException($"配置项 '{key}' 不能小于 {minimum}。");
        }
        if (rule.Maximum is decimal maximum && value > maximum)
        {
            throw new ArgumentException($"配置项 '{key}' 不能大于 {maximum}。");
        }
    }

    private static object ParseValue(string key, string raw, SettingRule rule)
    {
        return rule.Kind switch
        {
            "boolean" when bool.TryParse(raw.Trim(), out var boolean) => boolean,
            "integer" when long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer,
            "number" when decimal.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            "enum" => raw.Trim(),
            "string-list" => ParseStringList(raw),
            "string" => DecodeString(raw),
            _ => throw new InvalidDataException($"配置项 '{key}' 的值 '{raw}' 与类型 {rule.Kind} 不匹配。")
        };
    }

    private static string[] ParseStringList(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0 || value == "()")
        {
            return [];
        }
        if (value.Length >= 2 && value[0] == '(' && value[^1] == ')')
        {
            value = value[1..^1];
        }
        else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = DecodeString(value);
        }
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(DecodeString)
            .ToArray();
    }

    private static bool ValuesEquivalent(string key, string left, string right, SettingRule rule)
    {
        if (rule.Sensitive)
        {
            return string.Equals(DecodeString(left), DecodeString(right), StringComparison.Ordinal);
        }
        var a = ParseValue(key, left, rule);
        var b = ParseValue(key, right, rule);
        if (a is string[] aa && b is string[] bb)
        {
            return aa.SequenceEqual(bb, StringComparer.Ordinal);
        }
        return Equals(a, b);
    }

    private static SettingRule GetRule(string key, string sampleRaw)
    {
        if (SensitiveKeys.Contains(key))
        {
            return new SettingRule("string", true, null, null, null, null);
        }
        if (string.Equals(key, "CrossplayPlatforms", StringComparison.Ordinal))
        {
            return new SettingRule("string-list", false, CrossplayPlatforms, null, null, null);
        }
        if (string.Equals(key, "DenyTechnologyList", StringComparison.Ordinal))
        {
            return new SettingRule("string-list", false, null, null, null, null);
        }
        if (EnumValues.TryGetValue(key, out var allowedValues))
        {
            return new SettingRule("enum", false, allowedValues, null, null, null);
        }

        var kind = InferKind(sampleRaw);
        var (minimum, maximum, step) = GetNumericMetadata(key, kind);
        return new SettingRule(kind, false, null, minimum, maximum, step);
    }

    private static string InferKind(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return "string";
        }
        if (bool.TryParse(value, out _))
        {
            return "boolean";
        }
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return "integer";
        }
        if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return "number";
        }
        return "string";
    }

    private static (decimal? Minimum, decimal? Maximum, decimal? Step) GetNumericMetadata(
        string key,
        string kind)
    {
        return key switch
        {
            "PublicPort" or "RESTAPIPort" or "RCONPort" => (1, 65535, 1),
            "ServerPlayerMaxNum" => (1, 128, 1),
            "CoopPlayerMaxNum" => (1, 32, 1),
            "BaseCampWorkerMaxNum" => (1, 50, 1),
            "BaseCampMaxNumInGuild" => (1, 10, 1),
            "ServerReplicatePawnCullDistance" => (5000, 15000, 1),
            "ExpRate" or "PalCaptureRate" or "PalSpawnNumRate" or "WorkSpeedRate" => (0.1m, 20, 0.1m),
            "DayTimeSpeedRate" or "NightTimeSpeedRate" => (0.1m, 20, 0.1m),
            "PalEggDefaultHatchingTime" => (0, 240, 0.1m),
            "AutoSaveSpan" => (1, 3600, 1),
            "PhysicsActiveDropItemMaxNum" => (-1, null, 1),
            _ when kind == "number" && (key.EndsWith("Rate", StringComparison.Ordinal)
                || key.EndsWith("Multiplier", StringComparison.Ordinal)) => (0, 100, 0.1m),
            _ when kind == "number" => (null, null, 0.1m),
            _ when kind == "integer" => (null, null, 1),
            _ => (null, null, null)
        };
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string DecodeString(string raw)
    {
        var value = raw.Trim();
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        var output = new StringBuilder(value.Length - 2);
        for (var index = 1; index < value.Length - 1; index++)
        {
            var character = value[index];
            if (character == '\\' && index + 1 < value.Length - 1
                && value[index + 1] is '\\' or '"')
            {
                output.Append(value[++index]);
            }
            else
            {
                output.Append(character);
            }
        }
        return output.ToString();
    }

    private static async Task<LoadedSettings?> LoadOptionalAsync(
        string path,
        CancellationToken cancellationToken)
    {
        return File.Exists(path) ? await LoadAsync(path, cancellationToken) : null;
    }

    private static async Task<LoadedSettings> LoadRequiredAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("PalWorldSettings.ini was not found.", path);
        }
        return await LoadAsync(path, cancellationToken);
    }

    private static async Task<LoadedSettings> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var hasBom = bytes.AsSpan().StartsWith(Utf8Preamble);
        var textBytes = hasBom ? bytes.AsSpan(Utf8Preamble.Length) : bytes.AsSpan();
        string text;
        try
        {
            text = StrictUtf8.GetString(textBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"配置文件 '{path}' 不是有效的 UTF-8。", exception);
        }
        return new LoadedSettings(
            ParseSettings(text),
            ComputeRevision(bytes),
            hasBom);
    }

    private static byte[] EncodeUtf8(string text, bool includeBom)
    {
        var body = StrictUtf8.GetBytes(text);
        if (!includeBom)
        {
            return body;
        }
        var output = new byte[Utf8Preamble.Length + body.Length];
        Utf8Preamble.CopyTo(output, 0);
        body.CopyTo(output, Utf8Preamble.Length);
        return output;
    }

    private static string ComputeRevision(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static ParsedSettings ParseSettings(string text)
    {
        const string marker = "OptionSettings";
        var markerIndex = FindOptionSettingsIndex(text, marker);
        if (markerIndex < 0)
        {
            throw new InvalidDataException("OptionSettings entry is missing.");
        }

        var cursor = markerIndex + marker.Length;
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }
        if (cursor >= text.Length || text[cursor] != '=')
        {
            throw new InvalidDataException("OptionSettings entry is invalid.");
        }
        cursor++;
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }
        if (cursor >= text.Length || text[cursor] != '(')
        {
            throw new InvalidDataException("OptionSettings opening parenthesis is missing.");
        }

        var contentStart = cursor + 1;
        var depth = 1;
        var quoted = false;
        var escaped = false;
        var closeIndex = -1;
        for (var index = contentStart; index < text.Length; index++)
        {
            var character = text[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (quoted && character == '\\')
            {
                escaped = true;
                continue;
            }
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (quoted)
            {
                continue;
            }
            if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && --depth == 0)
            {
                closeIndex = index;
                break;
            }
        }
        if (closeIndex < 0 || quoted || escaped)
        {
            throw new InvalidDataException("OptionSettings contains unbalanced quotes or parentheses.");
        }

        var content = text.Substring(contentStart, closeIndex - contentStart);
        var entries = Tokenize(content);
        return new ParsedSettings(text, contentStart, content.Length, content, entries);
    }

    private static int FindOptionSettingsIndex(string text, string marker)
    {
        var found = -1;
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            var cursor = lineStart;
            while (cursor < lineEnd && text[cursor] is ' ' or '\t')
            {
                cursor++;
            }

            if (cursor + marker.Length <= lineEnd
                && text.AsSpan(cursor, marker.Length).SequenceEqual(marker)
                && IsOptionSettingsAssignment(text, cursor + marker.Length, lineEnd))
            {
                if (found >= 0)
                {
                    throw new InvalidDataException("Configuration contains more than one OptionSettings entry.");
                }
                found = cursor;
            }

            if (lineEnd == text.Length)
            {
                break;
            }
            lineStart = lineEnd + 1;
        }
        return found;
    }

    private static bool IsOptionSettingsAssignment(string text, int cursor, int lineEnd)
    {
        while (cursor < lineEnd && text[cursor] is ' ' or '\t')
        {
            cursor++;
        }
        return cursor < lineEnd && text[cursor] == '=';
    }

    private static List<SettingEntry> Tokenize(string content)
    {
        var entries = new List<SettingEntry>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var tokenStart = 0;
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var quoted = false;
        var escaped = false;

        for (var index = 0; index <= content.Length; index++)
        {
            var atEnd = index == content.Length;
            var character = atEnd ? '\0' : content[index];
            if (!atEnd)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (quoted && character == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (character == '"')
                {
                    quoted = !quoted;
                    continue;
                }
                if (quoted)
                {
                    continue;
                }
                switch (character)
                {
                    case '(':
                        parenthesisDepth++;
                        break;
                    case ')':
                        parenthesisDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        braceDepth--;
                        break;
                }
                if (parenthesisDepth < 0 || bracketDepth < 0 || braceDepth < 0)
                {
                    throw new InvalidDataException("OptionSettings contains unbalanced nested values.");
                }
            }

            if (!atEnd && (character != ',' || parenthesisDepth != 0 || bracketDepth != 0 || braceDepth != 0))
            {
                continue;
            }

            AddEntry(content, tokenStart, index, entries, keys);
            tokenStart = index + 1;
        }

        if (quoted || escaped || parenthesisDepth != 0 || bracketDepth != 0 || braceDepth != 0)
        {
            throw new InvalidDataException("OptionSettings contains unbalanced nested values.");
        }
        return entries;
    }

    private static void AddEntry(
        string content,
        int tokenStart,
        int tokenEnd,
        List<SettingEntry> entries,
        HashSet<string> keys)
    {
        var start = tokenStart;
        var end = tokenEnd;
        while (start < end && char.IsWhiteSpace(content[start]))
        {
            start++;
        }
        while (end > start && char.IsWhiteSpace(content[end - 1]))
        {
            end--;
        }
        if (start == end)
        {
            if (entries.Count == 0 && content.AsSpan(tokenStart, tokenEnd - tokenStart).Trim().IsEmpty)
            {
                return;
            }
            throw new InvalidDataException("OptionSettings contains an empty configuration entry.");
        }

        var equalsIndex = content.IndexOf('=', start, end - start);
        if (equalsIndex < 0)
        {
            throw new InvalidDataException($"OptionSettings entry '{content[start..end]}' has no equals sign.");
        }
        var key = content[start..equalsIndex].Trim();
        if (key.Length == 0 || key.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-' or ':')))
        {
            throw new InvalidDataException($"OptionSettings contains invalid key '{key}'.");
        }
        if (!keys.Add(key))
        {
            throw new InvalidDataException($"OptionSettings contains duplicate key '{key}'.");
        }

        var valueStart = equalsIndex + 1;
        while (valueStart < end && char.IsWhiteSpace(content[valueStart]))
        {
            valueStart++;
        }
        var valueEnd = end;
        while (valueEnd > valueStart && char.IsWhiteSpace(content[valueEnd - 1]))
        {
            valueEnd--;
        }
        entries.Add(new SettingEntry(
            key,
            content[valueStart..valueEnd],
            valueStart,
            valueEnd - valueStart));
    }

    private sealed record SettingRule(
        string Kind,
        bool Sensitive,
        IReadOnlyList<string>? AllowedValues,
        decimal? Minimum,
        decimal? Maximum,
        decimal? Step);

    private sealed record SettingEntry(
        string Key,
        string RawValue,
        int ValueStart,
        int ValueLength);

    private sealed class ParsedSettings
    {
        public ParsedSettings(
            string text,
            int contentStart,
            int contentLength,
            string content,
            List<SettingEntry> entries)
        {
            Text = text;
            ContentStart = contentStart;
            ContentLength = contentLength;
            Content = content;
            Entries = entries;
            ByKey = entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        }

        public string Text { get; }
        public int ContentStart { get; }
        public int ContentLength { get; }
        public string Content { get; }
        public IReadOnlyList<SettingEntry> Entries { get; }
        public IReadOnlyDictionary<string, SettingEntry> ByKey { get; }

        public string Render(IReadOnlyDictionary<string, string> replacements)
        {
            var body = new StringBuilder(Content.Length + replacements.Count * 32);
            var cursor = 0;
            foreach (var entry in Entries)
            {
                body.Append(Content, cursor, entry.ValueStart - cursor);
                body.Append(replacements.TryGetValue(entry.Key, out var replacement)
                    ? replacement
                    : entry.RawValue);
                cursor = entry.ValueStart + entry.ValueLength;
            }
            body.Append(Content, cursor, Content.Length - cursor);

            var missing = replacements.Where(change => !ByKey.ContainsKey(change.Key)).ToArray();
            if (missing.Length > 0)
            {
                var trailingWhitespace = body.Length;
                while (trailingWhitespace > 0 && char.IsWhiteSpace(body[trailingWhitespace - 1]))
                {
                    trailingWhitespace--;
                }
                var addition = new StringBuilder();
                var hasExisting = trailingWhitespace > 0;
                foreach (var (key, value) in missing)
                {
                    if (hasExisting || addition.Length > 0)
                    {
                        addition.Append(',');
                    }
                    addition.Append(key).Append('=').Append(value);
                }
                body.Insert(trailingWhitespace, addition.ToString());
            }

            return Text[..ContentStart] + body + Text[(ContentStart + ContentLength)..];
        }
    }

    private sealed record LoadedSettings(
        ParsedSettings Settings,
        string Revision,
        bool HasUtf8Bom);
}

public sealed record ServerConfigurationDocument(
    int SchemaVersion,
    string Revision,
    IReadOnlyList<ServerConfigurationOption> Options,
    string FilePath,
    string DefaultFilePath,
    DateTime LastModifiedAt);

public sealed record ServerConfigurationOption(
    string Key,
    string Kind,
    object? Value,
    object? DefaultValue,
    bool Sensitive,
    bool HasValue,
    IReadOnlyList<string>? AllowedValues,
    decimal? Minimum,
    decimal? Maximum,
    decimal? Step,
    bool Customized);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServerConfigurationUpdate(
    string Revision,
    Dictionary<string, JsonElement> Changes);

public sealed class ConfigurationRevisionConflictException(
    string expectedRevision,
    string currentRevision)
    : Exception("配置文件已被其他操作修改，请重新读取后再保存。")
{
    public string ExpectedRevision { get; } = expectedRevision;
    public string CurrentRevision { get; } = currentRevision;
}
