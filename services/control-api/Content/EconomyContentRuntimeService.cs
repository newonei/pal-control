using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

namespace PalControl.ControlApi.Content;

public sealed record EconomyRuntimeContent(
    EconomyContentVersion Version,
    EconomyContentRotationIdentity Rotation,
    IReadOnlyDictionary<string, ContentProductDefinition> Products,
    IReadOnlyDictionary<string, ContentResourceDefinition> Resources,
    IReadOnlyList<ContentExchangeZoneDefinition> ExchangeZones,
    IReadOnlySet<string> HotspotZoneIds,
    EconomyDynamicEconomyEvidence? DynamicEconomy = null);

/// <summary>
/// Bridges immutable content versions into the existing economy projections.
/// A new business day receives a new offer version even when the underlying
/// definition is unchanged, so a catalog captured before refresh is rejected
/// with OFFER_NOT_AVAILABLE instead of silently buying at a different price.
/// </summary>
public sealed class EconomyContentRuntimeService
{
    public const string SupportedRulesVersion = "weekly-economy-v1";

    internal static readonly long[] DailyPriceMultipliers = [9_000, 9_500, 10_000, 10_500, 11_000];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IEconomyContentStore _store;
    private readonly PalworldResourceCatalogService _catalog;
    private readonly ExtractionCommerceService _commerce;
    private readonly ExtractionModeOptions _options;
    private readonly EconomySafetyOptions _safety;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _timeZone;
    private readonly ILogger<EconomyContentRuntimeService> _logger;
    private Guid? _verifiedProjectionVersionId;

    public EconomyContentRuntimeService(
        IEconomyContentStore store,
        PalworldResourceCatalogService catalog,
        ExtractionCommerceService commerce,
        IOptions<ExtractionModeOptions> options,
        IOptions<EconomySafetyOptions> safety,
        TimeProvider timeProvider,
        ILogger<EconomyContentRuntimeService> logger)
    {
        _store = store;
        _catalog = catalog;
        _commerce = commerce;
        _options = options.Value;
        _safety = safety.Value;
        _timeProvider = timeProvider;
        _timeZone = _options.ResolveTimeZone();
        _logger = logger;
    }

    public DateOnly GetBusinessDate(DateTimeOffset now)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, _timeZone);
        return DateOnly.FromDateTime(localNow.AddHours(-_options.DailyRefreshHour).DateTime);
    }

    public DateOnly GetCurrentBusinessDate() => GetBusinessDate(_timeProvider.GetUtcNow());

    public async Task<EconomyContentValidationContext> BuildValidationContextAsync(
        CancellationToken cancellationToken)
    {
        var catalog = await _catalog.GetAsync(cancellationToken);
        return new EconomyContentValidationContext(
            catalog.Items.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>([SupportedRulesVersion], StringComparer.Ordinal),
            catalog.Revision,
            _safety.ApprovedGameVersion,
            _safety.ApprovedPalDefenderVersion);
    }

    public async Task<EconomyRuntimeContent> EnsureCurrentForBusinessDateAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var businessDate = GetCurrentBusinessDate();
            var pointer = await _store.GetCurrentAsync(_options.ServerId, cancellationToken);
            EconomyContentVersion version;
            if (pointer is null)
            {
                version = await PublishBootstrapAsync(businessDate, cancellationToken);
            }
            else
            {
                version = await _store.GetVersionAsync(pointer.VersionId, cancellationToken)
                    ?? throw new InvalidDataException(
                        "The current economy-content pointer references a missing version.");
                if (version.BusinessDate > businessDate)
                {
                    throw new ContentStoreException(
                        "CONTENT_BUSINESS_DATE_IN_FUTURE",
                        "The published economy content is ahead of the current business date; writes remain closed.");
                }
                if (version.BusinessDate < businessDate)
                {
                    version = await PublishDailyVersionAsync(version, businessDate, cancellationToken);
                }
                else
                {
                    // Startup verifies the immutable pointer/product
                    // projection once per process and version. High-frequency
                    // player reads must not repeatedly acquire SQLite's writer
                    // lock for an unchanged business-day projection.
                    if (_verifiedProjectionVersionId != version.VersionId)
                    {
                        _ = await ActivateProductProjectionAsync(
                            version,
                            version.VersionId,
                            "publish",
                            $"content-version:{version.VersionNumber}",
                            cancellationToken);
                    }
                }
            }
            return CreateRuntime(version);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EconomyRuntimeContent> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var pointer = await _store.GetCurrentAsync(_options.ServerId, cancellationToken)
            ?? throw new ContentStoreException(
                "CONTENT_NOT_PUBLISHED",
                "No economy content version is currently published.");
        var version = await _store.GetVersionAsync(pointer.VersionId, cancellationToken)
            ?? throw new InvalidDataException(
                "The current economy-content pointer references a missing version.");
        return CreateRuntime(version);
    }

    public Task<ContentProductDefinition> ResolveCurrentProductAsync(
        Guid offeredVersionId,
        string offeredContentHash,
        string sku,
        CancellationToken cancellationToken) =>
        _store.ResolveCurrentProductAsync(
            _options.ServerId,
            offeredVersionId,
            offeredContentHash,
            sku,
            cancellationToken);

    public async Task ActivatePublishedVersionAsync(
        EconomyContentVersion version,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (!string.Equals(version.ServerId, _options.ServerId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentStoreException(
                "CONTENT_SERVER_MISMATCH",
                "The content version belongs to another server.");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _ = await ActivateProductProjectionAsync(
                version,
                version.VersionId,
                "publish",
                $"content-version:{version.VersionNumber}",
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContentProductProjectionActivationResult> ActivatePreparedVersionAsync(
        EconomyContentVersion version,
        Guid? expectedCurrentVersionId,
        string action,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (!string.Equals(version.ServerId, _options.ServerId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentStoreException(
                "CONTENT_SERVER_MISMATCH",
                "The content version belongs to another server.");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ActivateProductProjectionAsync(
                version,
                expectedCurrentVersionId,
                action,
                actor,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public long EffectiveUnitPrice(EconomyContentVersion version, ContentProductDefinition product)
        => CalculateEffectiveUnitPrice(version, product);

    public static long CalculateEffectiveUnitPrice(
        EconomyContentVersion version,
        ContentProductDefinition product)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{version.BusinessDate:yyyy-MM-dd}|{product.Sku}|{version.RulesVersion}|{version.ContentHash}"));
        var dailyMultiplier = DailyPriceMultipliers[digest[0] % DailyPriceMultipliers.Length];
        var dynamic = EconomyDynamicEconomyRuntime.Create(
            version,
            EconomyDynamicEconomyRuntime.ResolveTimeZone(version.Definition));
        var eventMultiplier = dynamic is null
            ? 10_000
            : EconomyDynamicEconomyRuntime.CombineMultipliersBasisPoints(
                dynamic.WorldEvents.Select(worldEvent => worldEvent.ProductPriceMultiplierBasisPoints));
        var multiplier = EconomyDynamicEconomyRuntime.CombineMultipliersBasisPoints(
            [(int)dailyMultiplier, eventMultiplier]);
        return ApplyPriceMultiplier(product.UnitPrice, multiplier);
    }

    public static long CalculateMinimumPossibleEffectiveUnitPrice(
        EconomyContentDefinition definition,
        ContentProductDefinition product)
    {
        var eventMultiplier = EconomyDynamicEconomyRuntime
            .MinimumPossibleEventPriceMultiplierBasisPoints(definition.DynamicEconomyPolicy);
        var multiplier = EconomyDynamicEconomyRuntime.CombineMultipliersBasisPoints(
            [(int)DailyPriceMultipliers.Min(), eventMultiplier]);
        return ApplyPriceMultiplier(product.UnitPrice, multiplier);
    }

    public static long CalculateMaximumPossibleEffectiveUnitPrice(
        EconomyContentDefinition definition,
        ContentProductDefinition product)
    {
        var policy = definition.DynamicEconomyPolicy;
        var eventMultiplier = policy is null
            ? 10_000
            : EconomyDynamicEconomyRuntime.CombineMultipliersBasisPoints(
                policy.WorldEvents.Where(worldEvent => worldEvent.Active)
                    .Select(worldEvent => worldEvent.ProductPriceMultiplierBasisPoints)
                    .OrderDescending()
                    .Take(Math.Max(0, policy.DailyWorldEventCount)));
        var multiplier = EconomyDynamicEconomyRuntime.CombineMultipliersBasisPoints(
            [(int)DailyPriceMultipliers.Max(), eventMultiplier]);
        return ApplyPriceMultiplier(product.UnitPrice, multiplier);
    }

    private static long ApplyPriceMultiplier(long unitPrice, int multiplierBasisPoints)
    {
        var adjusted = Math.Floor(
            ((decimal)unitPrice * multiplierBasisPoints + 5_000m) / 10_000m);
        if (adjusted <= 1m)
        {
            return 1;
        }
        return adjusted >= long.MaxValue ? long.MaxValue : (long)adjusted;
    }

    private async Task<EconomyContentVersion> PublishBootstrapAsync(
        DateOnly businessDate,
        CancellationToken cancellationToken)
    {
        var catalog = await _catalog.GetAsync(cancellationToken);
        var definition = EconomyContentDefaults.Create(
            _options,
            _safety,
            catalog);
        var draft = await _store.CreateDraftAsync(
            _options.ServerId,
            "scheme-a-bootstrap-v1",
            null,
            definition,
            "system-content-bootstrap",
            cancellationToken);
        var context = await BuildValidationContextAsync(cancellationToken);
        var prepared = await _store.PreparePublishAsync(
            draft.DraftId,
            draft.Revision,
            businessDate,
            $"bootstrap-{_options.ServerId}-{businessDate:yyyyMMdd}-{draft.DraftId:N}",
            "system-content-bootstrap",
            context,
            cancellationToken);
        _ = await ActivateProductProjectionAsync(
            prepared.Version,
            prepared.ExpectedCurrentVersionId,
            "publish",
            "system-content-bootstrap",
            cancellationToken);
        _logger.LogInformation(
            "Published bootstrap economy content {VersionNumber} for business date {BusinessDate}.",
            prepared.Version.VersionNumber,
            businessDate);
        return prepared.Version;
    }

    private async Task<EconomyContentVersion> PublishDailyVersionAsync(
        EconomyContentVersion previous,
        DateOnly businessDate,
        CancellationToken cancellationToken)
    {
        var draft = await _store.CreateDraftAsync(
            _options.ServerId,
            $"automatic-business-date-{businessDate:yyyy-MM-dd}",
            previous.VersionId,
            previous.Definition,
            "system-daily-content",
            cancellationToken);
        var context = await BuildValidationContextAsync(cancellationToken);
        var prepared = await _store.PreparePublishAsync(
            draft.DraftId,
            draft.Revision,
            businessDate,
            $"daily-{_options.ServerId}-{businessDate:yyyyMMdd}-{draft.DraftId:N}",
            "system-daily-content",
            context,
            cancellationToken);
        _ = await ActivateProductProjectionAsync(
            prepared.Version,
            prepared.ExpectedCurrentVersionId,
            "publish",
            "system-daily-content",
            cancellationToken);
        _logger.LogInformation(
            "Activated economy content version {VersionNumber} for business date {BusinessDate}.",
            prepared.Version.VersionNumber,
            businessDate);
        return prepared.Version;
    }

    private async Task<ContentProductProjectionActivationResult> ActivateProductProjectionAsync(
        EconomyContentVersion version,
        Guid? expectedCurrentVersionId,
        string action,
        string actor,
        CancellationToken cancellationToken)
    {
        var products = EconomyContentProductProjection.Create(version);
        var result = await _commerce.ActivateContentProductProjectionAsync(
            new ContentProductProjectionActivation(
                version.ServerId,
                version.VersionId,
                version.VersionNumber,
                version.BusinessDate,
                version.RulesVersion,
                version.ContentHash,
                expectedCurrentVersionId,
                action,
                actor,
                products),
            cancellationToken);
        _verifiedProjectionVersionId = version.VersionId;
        return result;
    }

    private EconomyRuntimeContent CreateRuntime(EconomyContentVersion version)
    {
        var rotation = EconomyContentRotation.Create(version);
        var dynamicEconomy = EconomyDynamicEconomyRuntime.Create(version, _timeZone);
        var hotspots = dynamicEconomy is null
            ? SelectDailyHotspots(version)
            : dynamicEconomy.Zones.Where(zone => zone.SelectedHotspot)
                .Select(zone => zone.ZoneId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new EconomyRuntimeContent(
            version,
            rotation,
            version.Definition.Products.ToDictionary(
                product => product.Sku,
                StringComparer.OrdinalIgnoreCase),
            version.Definition.Resources.Where(resource => resource.Active).ToDictionary(
                resource => resource.ItemId,
                StringComparer.OrdinalIgnoreCase),
            version.Definition.ExchangeZones.Where(zone => zone.Active).ToArray(),
            hotspots,
            dynamicEconomy);
    }

    /// <summary>
    /// Selects a deterministic circular slice from a content-stable zone order.
    /// For a two-zone pool with one daily hotspot this guarantees that adjacent
    /// business dates select different zones, while a replay of the same date
    /// always selects the same set.
    /// </summary>
    public static IReadOnlySet<string> SelectDailyHotspots(EconomyContentVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var policy = version.Definition.Rotation;
        var stableOrderSeed = string.Join('|',
            version.ServerId,
            policy.RulesVersion,
            policy.AlgorithmVersion,
            policy.SeedNamespace,
            version.ContentHash);
        var orderedPool = policy.HotspotZonePool
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => new
            {
                Value = value,
                Key = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes($"{stableOrderSeed}|{value}")))
            })
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Value)
            .ToArray();
        if (orderedPool.Length == 0 || policy.DailyHotspotCount <= 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var count = Math.Clamp(policy.DailyHotspotCount, 0, orderedPool.Length);
        var start = version.BusinessDate.DayNumber % orderedPool.Length;
        return Enumerable.Range(0, count)
            .Select(offset => orderedPool[(start + offset) % orderedPool.Length])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static int CalculateZoneYieldMultiplierBasisPoints(
        ContentRotationPolicy policy,
        ContentExchangeZoneDefinition zone,
        bool hotspot)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(zone);
        if (!hotspot)
        {
            return zone.YieldMultiplierBasisPoints;
        }
        var hotspotMultiplier = policy.HotspotYieldMultiplierBasisPoints > 0
            ? policy.HotspotYieldMultiplierBasisPoints
            : 12_000;
        var effectiveMultiplier = Math.Ceiling(
            (decimal)zone.YieldMultiplierBasisPoints * hotspotMultiplier / 10_000m);
        return (int)Math.Clamp(effectiveMultiplier, int.MinValue, int.MaxValue);
    }

    public static long CalculateEffectiveResourceUnitValue(
        long unitValue,
        int zoneYieldMultiplierBasisPoints)
    {
        var value = Math.Ceiling(
            (decimal)unitValue * zoneYieldMultiplierBasisPoints / 10_000m);
        if (value <= 1m)
        {
            return 1;
        }
        return value >= long.MaxValue ? long.MaxValue : (long)value;
    }

    public static int CalculateRuntimeZoneYieldMultiplierBasisPoints(
        EconomyContentVersion version,
        ContentExchangeZoneDefinition zone,
        bool hotspot,
        IReadOnlyList<EconomyWorldEventEvidence>? activeEvents = null)
    {
        var hotspotAdjusted = CalculateZoneYieldMultiplierBasisPoints(
            version.Definition.Rotation,
            zone,
            hotspot);
        var eventMultiplier = EconomyDynamicEconomyRuntime.CombineMultipliersBasisPoints(
            (activeEvents ?? []).Select(worldEvent => worldEvent.ZoneYieldMultiplierBasisPoints));
        return EconomyDynamicEconomyRuntime.CombineMultipliersBasisPoints(
            [hotspotAdjusted, eventMultiplier]);
    }

    public static int CalculateMaximumPossibleZoneYieldMultiplierBasisPoints(
        EconomyContentDefinition definition,
        ContentExchangeZoneDefinition zone)
    {
        var hotspot = definition.DynamicEconomyPolicy is null
            ? definition.Rotation.DailyHotspotCount > 0 &&
              definition.Rotation.HotspotZonePool.Contains(zone.ZoneId, StringComparer.OrdinalIgnoreCase)
            : definition.DynamicEconomyPolicy.TimedHotspotCount > 0 &&
              definition.DynamicEconomyPolicy.ZonePool.Any(rule => string.Equals(
                  rule.ZoneId,
                  zone.ZoneId,
                  StringComparison.OrdinalIgnoreCase));
        var hotspotAdjusted = CalculateZoneYieldMultiplierBasisPoints(
            definition.Rotation,
            zone,
            hotspot);
        var eventMultiplier = EconomyDynamicEconomyRuntime
            .MaximumPossibleEventYieldMultiplierBasisPoints(definition.DynamicEconomyPolicy);
        return EconomyDynamicEconomyRuntime.CombineMultipliersBasisPoints(
            [hotspotAdjusted, eventMultiplier]);
    }
}

public static class EconomyContentSchedule
{
    public static bool IsOpen(
        ContentExchangeZoneDefinition zone,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        bool includeGrace = false)
    {
        var local = TimeZoneInfo.ConvertTime(now, timeZone);
        return zone.OpenWindows.Any(window => Contains(window, local, includeGrace));
    }

    public static DateTimeOffset? NextOpen(
        ContentExchangeZoneDefinition zone,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(now, timeZone);
        DateTimeOffset? next = null;
        for (var dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            var date = DateOnly.FromDateTime(local.DateTime).AddDays(dayOffset);
            foreach (var window in zone.OpenWindows.Where(window =>
                         window.DayOfWeek == date.DayOfWeek))
            {
                var candidate = date.ToDateTime(window.OpensAt);
                if (candidate > local.DateTime)
                {
                    var utc = TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified),
                        timeZone);
                    var candidateOffset = new DateTimeOffset(utc, TimeSpan.Zero);
                    if (next is null || candidateOffset < next)
                    {
                        next = candidateOffset;
                    }
                }
            }
        }
        return next;
    }

    public static DateTimeOffset? NextOpen(
        IEnumerable<ContentExchangeZoneDefinition> zones,
        DateTimeOffset now,
        TimeZoneInfo timeZone) =>
        zones.Select(zone => NextOpen(zone, now, timeZone))
            .Where(value => value is not null)
            .Min();

    private static bool Contains(
        ContentExchangeWindow window,
        DateTimeOffset local,
        bool includeGrace)
    {
        var date = DateOnly.FromDateTime(local.DateTime);
        var today = date.DayOfWeek;
        var time = TimeOnly.FromDateTime(local.DateTime);
        var grace = includeGrace ? TimeSpan.FromSeconds(window.GraceSeconds) : TimeSpan.Zero;
        if (window.OpensAt < window.ClosesAt)
        {
            return today == window.DayOfWeek &&
                   time >= window.OpensAt &&
                   BeforeClose(local.DateTime, date.ToDateTime(window.ClosesAt), grace);
        }

        if (today == window.DayOfWeek && time >= window.OpensAt)
        {
            return true;
        }
        var previous = date.AddDays(-1);
        return previous.DayOfWeek == window.DayOfWeek &&
               BeforeClose(local.DateTime, date.ToDateTime(window.ClosesAt), grace);
    }

    private static bool BeforeClose(DateTime local, DateTime closesAt, TimeSpan grace) =>
        grace > TimeSpan.Zero ? local <= closesAt.Add(grace) : local < closesAt;
}
