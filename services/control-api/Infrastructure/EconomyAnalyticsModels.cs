namespace PalControl.ControlApi.Infrastructure;

public enum EconomyAnalyticsDateBasis
{
    Utc,
    Business
}

public enum EconomyAnalyticsFactType
{
    PortalSession,
    CatalogView
}

public sealed record EconomyAnalyticsQuery(
    string ServerId,
    DateOnly From,
    DateOnly To,
    EconomyAnalyticsDateBasis DateBasis,
    Guid? SeasonId,
    Guid? ContentVersionId,
    int Limit,
    int Offset);

public sealed record EconomyAnalyticsWindow(
    DateOnly From,
    DateOnly To,
    string DateBasis,
    bool Stable,
    DateOnly StableThrough,
    string TimeZoneId);

public sealed record EconomyAnalyticsPrivacy(
    int MinimumCohortSize,
    string SuppressionRule,
    bool ContainsPlayerIdentifiers);

public sealed record EconomyAnalyticsSource(
    string Kind,
    int SchemaVersion,
    DateTimeOffset AsOf,
    string RecomputationHash,
    IReadOnlyList<string> Tables,
    long RowsRead,
    bool Complete);

public sealed record EconomyAnalyticsCount(
    long? Value,
    bool Suppressed);

public sealed record EconomyAnalyticsRate(
    long? Numerator,
    long? Denominator,
    int? BasisPoints,
    bool Suppressed,
    bool DenominatorComplete);

public sealed record EconomyAnalyticsFunnelStage(
    string Key,
    string Label,
    EconomyAnalyticsCount Accounts,
    long? Facts,
    bool SuccessOnly,
    string Source);

public sealed record EconomyAnalyticsProductMetric(
    string Sku,
    EconomyAnalyticsCount CatalogViewers,
    EconomyAnalyticsCount DeliveredBuyers,
    long? DeliveredQuantity,
    EconomyAnalyticsRate PurchaseRate);

public sealed record EconomyAnalyticsExchangeSummary(
    EconomyAnalyticsCount QuotingAccounts,
    EconomyAnalyticsCount SettledAccounts,
    long? QuotedRuns,
    long? SettledRuns,
    long? UncertainRuns,
    long? SettledValue,
    EconomyAnalyticsRate ConversionRate);

public sealed record EconomyAnalyticsZoneMetric(
    string ZoneId,
    EconomyAnalyticsCount Accounts,
    long? QuotedRuns,
    long? SettledRuns,
    long? UncertainRuns,
    long? SettledValue,
    bool Suppressed);

public sealed record EconomyAnalyticsCurrencyHealth(
    string Currency,
    EconomyAnalyticsCount Accounts,
    long? Inflow,
    long? Outflow,
    long? Net,
    long? BalanceP50,
    long? BalanceP95,
    long? MinimumBalance,
    long? MaximumBalance,
    bool Suppressed);

public sealed record EconomyAnalyticsUncertainHealth(
    long? Orders,
    long? Deliveries,
    long? ResourceSettlements,
    bool Suppressed);

public sealed record EconomyAnalyticsAlert(
    string Code,
    string Severity,
    string Message);

public sealed record EconomyAnalyticsPage(
    int Limit,
    int Offset,
    int TotalProducts,
    int TotalZones,
    string? NextCursor);

public sealed record EconomyAnalyticsReport(
    int SchemaVersion,
    string ServerId,
    Guid? SeasonId,
    Guid? ContentVersionId,
    EconomyAnalyticsWindow Window,
    EconomyAnalyticsPrivacy Privacy,
    EconomyAnalyticsSource Source,
    IReadOnlyList<EconomyAnalyticsFunnelStage> Funnel,
    IReadOnlyList<EconomyAnalyticsProductMetric> Products,
    EconomyAnalyticsExchangeSummary ResourceExchange,
    IReadOnlyList<EconomyAnalyticsZoneMetric> Zones,
    IReadOnlyList<EconomyAnalyticsCurrencyHealth> Currencies,
    EconomyAnalyticsUncertainHealth Uncertain,
    IReadOnlyList<EconomyAnalyticsAlert> Alerts,
    EconomyAnalyticsPage Page);

public sealed class EconomyAnalyticsException : Exception
{
    public EconomyAnalyticsException(string code, string message, int statusCode = 409)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public int StatusCode { get; }
}
