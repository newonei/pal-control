namespace PalControl.ControlApi.Infrastructure;

public sealed record WeeklyEconomyReportSeason(
    Guid SeasonId,
    string ServerId,
    string SeasonCode,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset CutoffAt,
    DateTimeOffset FrozenAt,
    string LeaderboardSnapshotHash);

public sealed record WeeklyEconomyReportPrivacy(
    bool PublicContainsPlayerIdentifiers,
    string RestrictedArtifact,
    string PseudonymAlgorithm,
    string PseudonymKeyFingerprint,
    int PublicMinimumCohortSize,
    int FrozenParticipantCohortSize,
    string ReportClassification);

public sealed record WeeklyEconomyReportSource(
    string Kind,
    string LeaderboardSourceHash,
    string AnalyticsRecomputationHash,
    string CombinedSourceHash,
    DateTimeOffset AnalyticsAsOf,
    IReadOnlyList<string> Tables,
    long RowsRead,
    bool Complete);

public sealed record WeeklyEconomyReportCurrency(
    string Currency,
    long? Accounts,
    long? Inflow,
    long? Outflow,
    long? Net,
    long? BalanceP50,
    long? BalanceP95,
    bool Suppressed);

public sealed record WeeklyEconomyReportInflation(
    long ResourceQuantity,
    long ResourceValue,
    long? AverageResourceValuePerUnitMilli,
    Guid? PreviousSeasonId,
    int? CommonBasketPriceIndexBasisPoints,
    int? InflationBasisPoints,
    int CommonBasketItemCount,
    string Method);

public sealed record WeeklyEconomyReportProduct(
    string Sku,
    long? DeliveredBuyers,
    long? DeliveredQuantity,
    int? PurchaseRateBasisPoints,
    bool Suppressed);

public sealed record WeeklyEconomyReportResource(
    string ItemId,
    string Category,
    long Quantity,
    long Value,
    long? AverageUnitValueMilli);

public sealed record WeeklyEconomyReportAnomalyRule(
    string Code,
    string Severity,
    string Description,
    long Threshold);

public sealed record WeeklyEconomyReportAnomalySummary(
    string Code,
    long? Accounts,
    bool Suppressed);

public sealed record WeeklyEconomyReportCurrencyComparison(
    string Currency,
    long? InflowChange,
    int? InflowChangeBasisPoints,
    long? OutflowChange,
    int? OutflowChangeBasisPoints,
    long? NetChange);

public sealed record WeeklyEconomyReportWeekOverWeek(
    Guid? PreviousSeasonId,
    bool Available,
    IReadOnlyList<WeeklyEconomyReportCurrencyComparison> Currencies,
    long? ResourceQuantityChange,
    int? ResourceQuantityChangeBasisPoints,
    long? ResourceValueChange,
    int? ResourceValueChangeBasisPoints);

public sealed record WeeklyEconomyReport(
    int SchemaVersion,
    string ReportKind,
    WeeklyEconomyReportSeason Season,
    WeeklyEconomyReportPrivacy Privacy,
    WeeklyEconomyReportSource Source,
    IReadOnlyList<WeeklyEconomyReportCurrency> Currencies,
    WeeklyEconomyReportInflation Inflation,
    IReadOnlyList<WeeklyEconomyReportResource> InflationBasket,
    IReadOnlyList<WeeklyEconomyReportProduct> PopularProducts,
    IReadOnlyList<WeeklyEconomyReportResource> PopularResources,
    IReadOnlyList<WeeklyEconomyReportAnomalyRule> AnomalyRules,
    IReadOnlyList<WeeklyEconomyReportAnomalySummary> Anomalies,
    WeeklyEconomyReportWeekOverWeek WeekOverWeek,
    IReadOnlyList<EconomyAnalyticsAlert> AnalyticsAlerts);

public sealed record WeeklyEconomyRestrictedAccount(
    string AccountPseudonym,
    IReadOnlyList<string> RuleCodes,
    int SettledExchanges,
    long ResourceQuantity,
    long ResourceValue,
    int TaskPoints,
    int? ResourceRank,
    int? TaskRank,
    bool ExcludedAtFreeze);

public sealed record WeeklyEconomyRestrictedAccounts(
    int SchemaVersion,
    string Classification,
    Guid SeasonId,
    string ServerId,
    string PseudonymAlgorithm,
    string PseudonymKeyFingerprint,
    IReadOnlyList<WeeklyEconomyRestrictedAccount> Accounts);

public sealed record WeeklyEconomyReportManifestFile(
    string Path,
    string Classification,
    long Bytes,
    string Sha256);

public sealed record WeeklyEconomyReportManifest(
    int SchemaVersion,
    string PackageKind,
    Guid SeasonId,
    string ServerId,
    string CombinedSourceHash,
    string ReviewTrustStoreSha256,
    Guid? PreviousSeasonId,
    bool IncludesHtml,
    IReadOnlyList<WeeklyEconomyReportManifestFile> Files);

public sealed record WeeklyEconomyReportReview(
    string ReviewerSubject,
    string ReviewerKeyFingerprint,
    string SignatureAlgorithm,
    string SignatureBase64,
    string Decision,
    string Reason,
    DateTimeOffset ReviewedAt);

public sealed record WeeklyEconomyReportReviewSignaturePayload(
    int SchemaVersion,
    int Sequence,
    string ManifestSha256,
    string? PreviousRevisionSha256,
    string ReviewerSubject,
    string ReviewerKeyFingerprint,
    string Decision,
    string Reason,
    DateTimeOffset ReviewedAt);

public sealed record WeeklyEconomyReportReviewTrustStore(
    int SchemaVersion,
    string PolicyId,
    IReadOnlyList<WeeklyEconomyReportReviewTrustKey> Keys);

public sealed record WeeklyEconomyReportReviewTrustKey(
    string Subject,
    string Algorithm,
    string PublicKeyPem);

public sealed record WeeklyEconomyReportReviewStatus(
    string State,
    int RequiredApprovals,
    int DistinctApprovals,
    bool Rejected,
    DateTimeOffset UpdatedAt);

public sealed record WeeklyEconomyReportReviewRevision(
    int SchemaVersion,
    int Sequence,
    string ManifestSha256,
    string? PreviousRevisionSha256,
    WeeklyEconomyReportReview? Review,
    WeeklyEconomyReportReviewStatus Status);

public sealed record WeeklyEconomyReportArchiveResult(
    string ArchiveDirectory,
    string ManifestSha256,
    string ReviewHeadSha256,
    bool Created,
    bool IdempotentReplay,
    WeeklyEconomyReport Report,
    WeeklyEconomyReportReviewStatus ReviewStatus);

public sealed record WeeklyEconomyReportArchiveVerification(
    string ArchiveDirectory,
    string ManifestSha256,
    string ReviewHeadSha256,
    WeeklyEconomyReportManifest Manifest,
    WeeklyEconomyReport Report,
    WeeklyEconomyRestrictedAccounts RestrictedAccounts,
    WeeklyEconomyReportReviewStatus ReviewStatus,
    int ReviewRevision);
