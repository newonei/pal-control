using System.Text.Json.Serialization;

namespace PalControl.ControlApi.Content;

[JsonConverter(typeof(JsonStringEnumConverter<ContentZoneRiskLevel>))]
public enum ContentZoneRiskLevel
{
    Guarded,
    Elevated,
    Severe
}

/// <summary>
/// Purely economic server-side world events. They may change authoritative
/// sell multipliers or projected shop prices, but never synthesize kills,
/// drops, inventory, or client-reported gameplay facts.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContentWorldEventKind>))]
public enum ContentWorldEventKind
{
    ResourceSurge,
    SupplyRelief
}

public sealed record ContentDynamicZoneRule(
    string ZoneId,
    ContentZoneRiskLevel RiskLevel);

public sealed record ContentWorldEventDefinition(
    string EventKey,
    string DisplayName,
    ContentWorldEventKind Kind,
    int StartsAtMinuteOfBusinessDay,
    int DurationMinutes,
    int GraceSeconds,
    int ZoneYieldMultiplierBasisPoints,
    int ProductPriceMultiplierBasisPoints,
    bool Active = true);

/// <summary>
/// Optional modern Scheme A policy. Omission preserves legacy JSON/hash and
/// its static schedule. Presence makes dynamic-zone/event evidence mandatory.
/// All minute offsets are relative to the configured business-day refresh.
/// </summary>
public sealed record ContentDynamicEconomyPolicy(
    string PolicyVersion,
    IReadOnlyList<ContentDynamicZoneRule> ZonePool,
    int DailyOpenZoneCount,
    int DynamicOpenStartsAtMinute,
    int DynamicOpenDurationMinutes,
    int DynamicOpenGraceSeconds,
    int TimedHotspotCount,
    int TimedHotspotStartsAtMinute,
    int TimedHotspotDurationMinutes,
    int TimedHotspotGraceSeconds,
    IReadOnlyList<ContentWorldEventDefinition> WorldEvents,
    int DailyWorldEventCount = 1);

public sealed record EconomyEventWindowEvidence(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset GraceEndsAt);

public sealed record EconomyWorldEventEvidence(
    string EventId,
    string EventKey,
    string DisplayName,
    ContentWorldEventKind Kind,
    string Seed,
    EconomyEventWindowEvidence Window,
    int ZoneYieldMultiplierBasisPoints,
    int ProductPriceMultiplierBasisPoints);

public sealed record EconomyDynamicZoneEvidence(
    string ZoneId,
    ContentZoneRiskLevel RiskLevel,
    bool SelectedOpen,
    EconomyEventWindowEvidence OpenWindow,
    bool SelectedHotspot,
    EconomyEventWindowEvidence? HotspotWindow);

public sealed record EconomyDynamicEconomyEvidence(
    string PolicyVersion,
    string Seed,
    DateOnly BusinessDate,
    IReadOnlyList<EconomyDynamicZoneEvidence> Zones,
    IReadOnlyList<EconomyWorldEventEvidence> WorldEvents);

/// <summary>
/// Frozen on a resource quote/run so a replay never re-rolls a zone, event,
/// risk level, window, multiplier, or seed.
/// </summary>
public sealed record EconomyDynamicQuoteEvidence(
    string PolicyVersion,
    string Seed,
    ContentZoneRiskLevel RiskLevel,
    EconomyEventWindowEvidence OpenWindow,
    EconomyEventWindowEvidence? HotspotWindow,
    IReadOnlyList<EconomyWorldEventEvidence> WorldEvents);
