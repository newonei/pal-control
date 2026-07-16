using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PalControl.ControlApi.Infrastructure;

public sealed record GameCatalogEntry(
    string Id,
    string Name,
    string Category,
    string? Dex = null,
    string? EnglishName = null);

public sealed record PalTemplateCatalogEntry(
    string FileName,
    string? PalId,
    string? Nickname,
    int? Level,
    bool? Shiny,
    int PassiveCount,
    int ActiveSkillCount,
    long SizeBytes,
    DateTimeOffset LastModifiedAt,
    string Sha256,
    bool Parseable,
    bool Selectable,
    string RiskLevel,
    string Summary);

public sealed record GameCatalogSource(
    string Name,
    string Note,
    string ItemsUrl,
    string PalsUrl,
    string TechnologiesUrl,
    string? PalNamesUrl = null,
    string? PalNamesLicense = null);

public sealed record GameCatalogCoverage(
    string Items,
    string Pals,
    string Eggs,
    string Technologies,
    string Templates);

public sealed record GameResourceCatalog(
    string SchemaVersion,
    string Revision,
    DateTimeOffset GeneratedAt,
    GameCatalogSource Source,
    GameCatalogCoverage Coverage,
    IReadOnlyList<GameCatalogEntry> Items,
    IReadOnlyList<GameCatalogEntry> Pals,
    IReadOnlyList<GameCatalogEntry> Eggs,
    IReadOnlyList<GameCatalogEntry> Technologies,
    IReadOnlyList<PalTemplateCatalogEntry> Templates);

internal sealed record StoredGameResourceCatalog(
    string SchemaVersion,
    string Revision,
    DateTimeOffset GeneratedAt,
    GameCatalogSource Source,
    GameCatalogCoverage Coverage,
    IReadOnlyList<GameCatalogEntry> Items,
    IReadOnlyList<GameCatalogEntry> Pals,
    IReadOnlyList<GameCatalogEntry> Eggs,
    IReadOnlyList<GameCatalogEntry> Technologies);

public sealed class PalworldResourceCatalogService
{
    private const int MaximumTemplateBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ExtremeNumericFields =
    [
        "HP", "SP", "MP", "Shield", "Hunger", "MaxHunger", "SAN", "Support", "CraftSpeed",
        "PartnerSkillLevel", "CondensedPals"
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _catalogPath;
    private readonly string _templateDirectory;
    private readonly ILogger<PalworldResourceCatalogService> _logger;
    private StoredGameResourceCatalog? _catalog;
    private DateTime _catalogLastWriteUtc;

    public PalworldResourceCatalogService(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<PalworldResourceCatalogService> logger)
    {
        _logger = logger;
        var configuredCatalogPath = configuration["Palworld:ResourceCatalogPath"];
        _catalogPath = string.IsNullOrWhiteSpace(configuredCatalogPath)
            ? Path.Combine(
                environment.ContentRootPath,
                "Resources",
                "palworld-resource-catalog.json")
            : Path.GetFullPath(
                Path.IsPathRooted(configuredCatalogPath)
                    ? configuredCatalogPath
                    : Path.Combine(environment.ContentRootPath, configuredCatalogPath));

        var installRoot = configuration["Palworld:InstallRoot"] ?? "../../../PalServer";
        var resolvedInstallRoot = Path.GetFullPath(
            Path.IsPathRooted(installRoot)
                ? installRoot
                : Path.Combine(environment.ContentRootPath, installRoot));
        _templateDirectory = Path.Combine(
            resolvedInstallRoot,
            "Pal",
            "Binaries",
            "Win64",
            "PalDefender",
            "Pals",
            "Templates");
    }

    public async Task<GameResourceCatalog> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureCatalogLoadedAsync(cancellationToken);
            var templates = await ReadTemplatesAsync(cancellationToken);
            var catalog = _catalog
                ?? throw new InvalidOperationException("The game resource catalog is not loaded.");
            var revision = BuildRevision(catalog.Revision, templates);
            return new GameResourceCatalog(
                catalog.SchemaVersion,
                revision,
                catalog.GeneratedAt,
                catalog.Source,
                catalog.Coverage,
                catalog.Items,
                catalog.Pals,
                catalog.Eggs,
                catalog.Technologies,
                templates);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureCatalogLoadedAsync(CancellationToken cancellationToken)
    {
        var file = new FileInfo(_catalogPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException(
                "The bundled Palworld resource catalog is unavailable.",
                _catalogPath);
        }
        if (_catalog is not null && file.LastWriteTimeUtc == _catalogLastWriteUtc)
        {
            return;
        }

        await using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var loaded = await JsonSerializer.DeserializeAsync<StoredGameResourceCatalog>(
            stream,
            JsonOptions,
            cancellationToken);
        if (loaded is null || loaded.Items.Count == 0 || loaded.Pals.Count == 0)
        {
            throw new InvalidDataException("The bundled Palworld resource catalog is empty or invalid.");
        }
        _catalog = loaded;
        _catalogLastWriteUtc = file.LastWriteTimeUtc;
    }

    private async Task<IReadOnlyList<PalTemplateCatalogEntry>> ReadTemplatesAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_templateDirectory))
        {
            return [];
        }

        var root = Path.GetFullPath(_templateDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var templates = new List<PalTemplateCatalogEntry>();
        foreach (var path in Directory.EnumerateFiles(
                     _templateDirectory,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var file = new FileInfo(fullPath);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            templates.Add(await ReadTemplateAsync(file, cancellationToken));
        }
        return templates
            .OrderBy(template => template.Selectable ? 0 : 1)
            .ThenBy(template => template.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<PalTemplateCatalogEntry> ReadTemplateAsync(
        FileInfo file,
        CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            if (file.Length > MaximumTemplateBytes)
            {
                return InvalidTemplate(file, "high", "模板文件超过 256 KiB，已禁止选择。");
            }
            bytes = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogSafeWarning(
                exception,
                "A PalDefender template could not be read ({TemplateFingerprint}).",
                ControlPlaneLog.Fingerprint(file.Name));
            return InvalidTemplate(file, "invalid", "模板文件无法读取。");
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return InvalidTemplate(file, "invalid", "模板根节点不是 JSON 对象。", sha256);
            }

            var palId = GetString(root, "PalID");
            var nickname = GetString(root, "Nickname");
            var level = GetInt32(root, "Level");
            var shiny = GetBoolean(root, "Shiny");
            var passiveCount = GetArrayCount(root, "Passives");
            var activeSkillCount = GetArrayCount(root, "ActiveSkills");
            var extremeValue = ExtremeNumericFields.Any(field =>
                GetDouble(root, field) is > 100_000);
            var extremeSouls = HasObjectNumberAbove(root, "PalSouls", 10);
            var extremeIvs = HasObjectNumberAbove(root, "IVs", 100);

            var riskLevel = level is > 65 || passiveCount > 4 || extremeValue || extremeSouls || extremeIvs
                ? "high"
                : level is > 50 || shiny == true
                    ? "elevated"
                    : "standard";
            var selectable = !string.IsNullOrWhiteSpace(palId) && riskLevel != "high";
            var summary = riskLevel switch
            {
                "high" => "检测到超等级、超量词条或异常属性，已禁止直接发放。",
                "elevated" => "高等级或闪光模板，发放前需要重点核对。",
                _ => "模板结构可解析，可在确认摘要后发放。"
            };
            if (string.IsNullOrWhiteSpace(palId))
            {
                selectable = false;
                riskLevel = "invalid";
                summary = "模板缺少 PalID，无法发放。";
            }
            return new PalTemplateCatalogEntry(
                file.Name,
                palId,
                nickname,
                level,
                shiny,
                passiveCount,
                activeSkillCount,
                file.Length,
                file.LastWriteTimeUtc,
                sha256,
                Parseable: true,
                Selectable: selectable,
                RiskLevel: riskLevel,
                Summary: summary);
        }
        catch (JsonException exception)
        {
            _logger.LogSafeWarning(
                exception,
                "A PalDefender template is invalid JSON ({TemplateFingerprint}).",
                ControlPlaneLog.Fingerprint(file.Name));
            return InvalidTemplate(file, "invalid", "模板 JSON 无法解析。", sha256);
        }
    }

    private static PalTemplateCatalogEntry InvalidTemplate(
        FileInfo file,
        string riskLevel,
        string summary,
        string sha256 = "") => new(
        file.Name,
        PalId: null,
        Nickname: null,
        Level: null,
        Shiny: null,
        PassiveCount: 0,
        ActiveSkillCount: 0,
        file.Length,
        file.LastWriteTimeUtc,
        sha256,
        Parseable: false,
        Selectable: false,
        RiskLevel: riskLevel,
        Summary: summary);

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? GetDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
            ? number
            : null;

    private static bool? GetBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int GetArrayCount(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static bool HasObjectNumberAbove(JsonElement root, string name, double threshold)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        return value.EnumerateObject().Any(property =>
            property.Value.TryGetDouble(out var number) && number > threshold);
    }

    private static string BuildRevision(
        string catalogRevision,
        IReadOnlyList<PalTemplateCatalogEntry> templates)
    {
        var text = new StringBuilder(catalogRevision);
        foreach (var template in templates.OrderBy(
                     template => template.FileName,
                     StringComparer.OrdinalIgnoreCase))
        {
            text.Append('|')
                .Append(template.FileName)
                .Append(':').Append(template.Sha256)
                .Append(':').Append(template.SizeBytes)
                .Append(':').Append(template.LastModifiedAt.ToUniversalTime().Ticks)
                .Append(':').Append(template.RiskLevel)
                .Append(':').Append(template.Selectable);
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))
            .ToLowerInvariant();
        return $"{catalogRevision}-{hash[..16]}";
    }
}
