using System.Security.Cryptography;
using System.Text;

namespace PalControl.ControlApi.Content;

public static class EconomyDynamicEconomyRuntime
{
    public static EconomyDynamicEconomyEvidence? Create(
        EconomyContentVersion version,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(timeZone);
        var policy = version.Definition.DynamicEconomyPolicy;
        if (policy is null)
        {
            return null;
        }

        var rotation = EconomyContentRotation.Create(version);
        var orderedZones = StableOrder(
            policy.ZonePool,
            zone => zone.ZoneId,
            StablePolicySeed(version, policy.PolicyVersion, "zones"));
        var openZoneIds = CircularSlice(
                orderedZones.Select(zone => zone.ZoneId).ToArray(),
                version.BusinessDate.DayNumber,
                policy.DailyOpenZoneCount)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hotspotZoneIds = CircularSlice(
                orderedZones.Where(zone => openZoneIds.Contains(zone.ZoneId))
                    .Select(zone => zone.ZoneId).ToArray(),
                version.BusinessDate.DayNumber,
                policy.TimedHotspotCount)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var openWindow = Window(
            version,
            timeZone,
            policy.DynamicOpenStartsAtMinute,
            policy.DynamicOpenDurationMinutes,
            policy.DynamicOpenGraceSeconds);
        var hotspotWindow = Window(
            version,
            timeZone,
            policy.TimedHotspotStartsAtMinute,
            policy.TimedHotspotDurationMinutes,
            policy.TimedHotspotGraceSeconds);
        var zones = orderedZones.Select(zone => new EconomyDynamicZoneEvidence(
                zone.ZoneId,
                zone.RiskLevel,
                openZoneIds.Contains(zone.ZoneId),
                openWindow,
                hotspotZoneIds.Contains(zone.ZoneId),
                hotspotZoneIds.Contains(zone.ZoneId) ? hotspotWindow : null))
            .OrderBy(zone => zone.ZoneId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var activeEvents = policy.WorldEvents.Where(worldEvent => worldEvent.Active).ToArray();
        var orderedEvents = StableOrder(
            activeEvents,
            worldEvent => worldEvent.EventKey,
            StablePolicySeed(version, policy.PolicyVersion, "events"));
        var selectedEvents = CircularSlice(
            orderedEvents,
            version.BusinessDate.DayNumber,
            policy.DailyWorldEventCount);
        var events = selectedEvents.Select(worldEvent =>
        {
            var seed = Hash($"{rotation.Seed}|world-event|{worldEvent.EventKey}");
            return new EconomyWorldEventEvidence(
                Hash($"{seed}|event-id")[..32],
                worldEvent.EventKey,
                worldEvent.DisplayName,
                worldEvent.Kind,
                seed,
                Window(
                    version,
                    timeZone,
                    worldEvent.StartsAtMinuteOfBusinessDay,
                    worldEvent.DurationMinutes,
                    worldEvent.GraceSeconds),
                worldEvent.ZoneYieldMultiplierBasisPoints,
                worldEvent.ProductPriceMultiplierBasisPoints);
        }).OrderBy(worldEvent => worldEvent.EventKey, StringComparer.OrdinalIgnoreCase).ToArray();

        return new EconomyDynamicEconomyEvidence(
            policy.PolicyVersion,
            rotation.Seed,
            version.BusinessDate,
            zones,
            events);
    }

    public static bool Contains(
        EconomyEventWindowEvidence window,
        DateTimeOffset now,
        bool includeGrace = false) =>
        now >= window.StartsAt && now < (includeGrace ? window.GraceEndsAt : window.EndsAt);

    public static bool IsZoneOpen(
        EconomyDynamicEconomyEvidence evidence,
        string zoneId,
        DateTimeOffset now,
        bool includeGrace = false) =>
        evidence.Zones.FirstOrDefault(zone => string.Equals(
            zone.ZoneId,
            zoneId,
            StringComparison.OrdinalIgnoreCase)) is { SelectedOpen: true } zone &&
        Contains(zone.OpenWindow, now, includeGrace);

    public static bool IsHotspot(
        EconomyDynamicEconomyEvidence evidence,
        string zoneId,
        DateTimeOffset now,
        bool includeGrace = false) =>
        evidence.Zones.FirstOrDefault(zone => string.Equals(
            zone.ZoneId,
            zoneId,
            StringComparison.OrdinalIgnoreCase)) is
            { SelectedHotspot: true, HotspotWindow: not null } zone &&
        Contains(zone.HotspotWindow, now, includeGrace);

    public static IReadOnlyList<EconomyWorldEventEvidence> ActiveEvents(
        EconomyDynamicEconomyEvidence evidence,
        DateTimeOffset now,
        bool includeGrace = false) =>
        evidence.WorldEvents.Where(worldEvent => Contains(worldEvent.Window, now, includeGrace)).ToArray();

    public static int CombineMultipliersBasisPoints(IEnumerable<int> multipliers)
    {
        decimal result = 10_000m;
        foreach (var multiplier in multipliers)
        {
            result = Math.Ceiling(result * multiplier / 10_000m);
            if (result > int.MaxValue)
            {
                // Validation rejects an unrepresentable authored combination.
                // Saturation keeps every downstream safety analysis conservative
                // instead of allowing a hostile draft to crash validation.
                return int.MaxValue;
            }
        }
        return (int)result;
    }

    public static bool MultipliersFitInt32(IEnumerable<int> multipliers)
    {
        decimal result = 10_000m;
        foreach (var multiplier in multipliers)
        {
            result = Math.Ceiling(result * multiplier / 10_000m);
            if (result > int.MaxValue)
            {
                return false;
            }
        }
        return result >= 0m;
    }

    public static int MaximumPossibleEventYieldMultiplierBasisPoints(
        ContentDynamicEconomyPolicy? policy)
    {
        if (policy is null)
        {
            return 10_000;
        }
        return ExtremeSelectedMultiplier(
            policy.WorldEvents.Where(worldEvent => worldEvent.Active)
                .Select(worldEvent => worldEvent.ZoneYieldMultiplierBasisPoints),
            policy.DailyWorldEventCount,
            maximum: true);
    }

    public static int MinimumPossibleEventPriceMultiplierBasisPoints(
        ContentDynamicEconomyPolicy? policy)
    {
        if (policy is null)
        {
            return 10_000;
        }
        return ExtremeSelectedMultiplier(
            policy.WorldEvents.Where(worldEvent => worldEvent.Active)
                .Select(worldEvent => worldEvent.ProductPriceMultiplierBasisPoints),
            policy.DailyWorldEventCount,
            maximum: false);
    }

    public static DateTimeOffset? NextZoneOpen(
        EconomyContentVersion currentVersion,
        ContentExchangeZoneDefinition zone,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        if (currentVersion.Definition.DynamicEconomyPolicy is null)
        {
            return EconomyContentSchedule.NextOpen(zone, now, timeZone);
        }
        // A valid policy can contain up to 100 zones and select only one per
        // day. Scan a complete rotation so a closed zone never loses its
        // authoritative nextOpensAt merely because it is outside one week.
        var horizon = Math.Max(8, currentVersion.Definition.DynamicEconomyPolicy.ZonePool.Count + 1);
        for (var offset = 0; offset <= horizon; offset++)
        {
            var date = currentVersion.BusinessDate.AddDays(offset);
            var evidence = Create(currentVersion with { BusinessDate = date }, timeZone);
            var dynamicZone = evidence?.Zones.FirstOrDefault(candidate =>
                string.Equals(candidate.ZoneId, zone.ZoneId, StringComparison.OrdinalIgnoreCase));
            if (dynamicZone is not { SelectedOpen: true })
            {
                continue;
            }
            var candidate = now > dynamicZone.OpenWindow.StartsAt
                ? now
                : dynamicZone.OpenWindow.StartsAt;
            if (candidate >= dynamicZone.OpenWindow.EndsAt)
            {
                continue;
            }
            if (EconomyContentSchedule.IsOpen(zone, candidate, timeZone))
            {
                return candidate;
            }
            var staticNext = EconomyContentSchedule.NextOpen(zone, candidate, timeZone);
            if (staticNext is not null && staticNext < dynamicZone.OpenWindow.EndsAt)
            {
                return staticNext;
            }
        }
        return null;
    }

    public static TimeZoneInfo ResolveTimeZone(EconomyContentDefinition definition)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(definition.TimeZoneId);
        }
        catch (TimeZoneNotFoundException) when (string.Equals(
                   definition.TimeZoneId,
                   "Asia/Shanghai",
                   StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
    }

    private static int ExtremeSelectedMultiplier(
        IEnumerable<int> values,
        int selectedCount,
        bool maximum)
    {
        var ordered = maximum ? values.OrderDescending() : values.Order();
        return CombineMultipliersBasisPoints(ordered.Take(Math.Max(0, selectedCount)));
    }

    private static EconomyEventWindowEvidence Window(
        EconomyContentVersion version,
        TimeZoneInfo timeZone,
        int startsAtMinute,
        int durationMinutes,
        int graceSeconds)
    {
        var businessStartLocal = version.BusinessDate.ToDateTime(
            new TimeOnly(version.Definition.DailyRefreshHour, 0),
            DateTimeKind.Unspecified);
        var startsLocal = businessStartLocal.AddMinutes(startsAtMinute);
        var endsLocal = startsLocal.AddMinutes(durationMinutes);
        var startsAt = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(startsLocal, timeZone),
            TimeSpan.Zero);
        var endsAt = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(endsLocal, timeZone),
            TimeSpan.Zero);
        return new EconomyEventWindowEvidence(startsAt, endsAt, endsAt.AddSeconds(graceSeconds));
    }

    private static T[] StableOrder<T>(
        IEnumerable<T> values,
        Func<T, string> keySelector,
        string seed) => values
        .Select(value => new
        {
            Value = value,
            Key = Hash($"{seed}|{keySelector(value).ToLowerInvariant()}")
        })
        .OrderBy(value => value.Key, StringComparer.Ordinal)
        .ThenBy(value => keySelector(value.Value), StringComparer.OrdinalIgnoreCase)
        .Select(value => value.Value)
        .ToArray();

    private static T[] CircularSlice<T>(IReadOnlyList<T> values, int dayNumber, int count)
    {
        if (values.Count == 0 || count <= 0)
        {
            return [];
        }
        var bounded = Math.Clamp(count, 0, values.Count);
        var start = Math.Abs(dayNumber % values.Count);
        return Enumerable.Range(0, bounded)
            .Select(offset => values[(start + offset) % values.Count])
            .ToArray();
    }

    private static string StablePolicySeed(
        EconomyContentVersion version,
        string policyVersion,
        string purpose) => Hash(string.Join('|',
        version.ServerId,
        version.RulesVersion,
        version.ContentHash,
        policyVersion,
        purpose));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
