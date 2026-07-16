using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

/// <summary>
/// Builds a deterministic, immutable weekly economy archive from a validated
/// leaderboard freeze and the authoritative SQLite analytics recomputation.
/// Account-level anomaly evidence is deliberately separated into a restricted,
/// HMAC-pseudonymized artifact. Review revisions are append-only and chained to
/// the package manifest.
/// </summary>
public sealed partial class WeeklyEconomyReportArchive
{
    public const int SchemaVersion = 1;
    public const int RequiredReviewApprovals = 2;
    private const int MaximumDimensionRows = 100_000;
    private const int MaximumPopularRows = 10;
    private const int MinimumPublicCohortSize = 5;
    private const string ReviewSignatureAlgorithm = "ecdsa-p256-sha256";
    private const string NistP256CurveOid = "1.2.840.10045.3.1.7";
    private const string ShareableReportClassification = "operator-shareable-aggregate";
    private const string SmallCohortReportClassification = "restricted-small-cohort";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };
    private static readonly JsonSerializerOptions StrictJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32
    };

    private readonly string _archiveRoot;
    private readonly string? _connectionString;
    private readonly EconomyAnalyticsStore? _analytics;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _businessTimeZone;
    private readonly string _reviewTrustStoreSha256;
    private readonly IReadOnlyDictionary<string, TrustedReviewKey> _trustedReviewKeys;

    public WeeklyEconomyReportArchive(
        string dataDirectory,
        string archiveRoot,
        TimeZoneInfo businessTimeZone,
        string reviewTrustStorePath,
        string expectedReviewTrustStoreSha256,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveRoot);
        ArgumentNullException.ThrowIfNull(businessTimeZone);
        var dataRoot = Path.GetFullPath(dataDirectory);
        _archiveRoot = Path.GetFullPath(archiveRoot);
        Directory.CreateDirectory(_archiveRoot);
        RejectReparseAncestors(_archiveRoot);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataRoot, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        _businessTimeZone = businessTimeZone;
        _timeProvider = timeProvider ?? TimeProvider.System;
        (_reviewTrustStoreSha256, _trustedReviewKeys) = LoadReviewTrustStore(
            reviewTrustStorePath);
        ValidateExpectedReviewTrustStoreSha256(expectedReviewTrustStoreSha256);
        // Evidence generation must never migrate or otherwise mutate the
        // authoritative source. A missing analytics schema fails closed in
        // QueryAsync instead of being created as a side effect here.
        _analytics = EconomyAnalyticsStore.OpenReadOnly(
            dataRoot,
            businessTimeZone,
            _timeProvider);
    }

    public WeeklyEconomyReportArchive(
        string archiveRoot,
        string reviewTrustStorePath,
        string expectedReviewTrustStoreSha256,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveRoot);
        _archiveRoot = Path.GetFullPath(archiveRoot);
        RejectReparseAncestors(_archiveRoot);
        _businessTimeZone = TimeZoneInfo.Utc;
        _timeProvider = timeProvider ?? TimeProvider.System;
        (_reviewTrustStoreSha256, _trustedReviewKeys) = LoadReviewTrustStore(
            reviewTrustStorePath);
        ValidateExpectedReviewTrustStoreSha256(expectedReviewTrustStoreSha256);
    }

    public async Task<WeeklyEconomyReportArchiveResult> GenerateAsync(
        Guid seasonId,
        ReadOnlyMemory<byte> pseudonymKey,
        Guid? previousSeasonId = null,
        string? previousReviewHeadSha256 = null,
        string? expectedExistingReviewHeadSha256 = null,
        bool includeHtml = false,
        CancellationToken cancellationToken = default)
    {
        if (_connectionString is null || _analytics is null)
        {
            throw new InvalidOperationException(
                "Generation requires the data-directory constructor and an authoritative analytics source.");
        }
        ValidateSeasonId(seasonId);
        ValidatePseudonymKey(pseudonymKey.Span);
        if (previousSeasonId == Guid.Empty || previousSeasonId == seasonId)
        {
            throw new ArgumentException("The previous season identifier is invalid.", nameof(previousSeasonId));
        }
        if ((previousSeasonId is null) != (previousReviewHeadSha256 is null))
        {
            throw new ArgumentException(
                "A previous season and its externally published review head SHA-256 must be supplied together.",
                nameof(previousReviewHeadSha256));
        }
        ValidateOptionalSha256(previousReviewHeadSha256, nameof(previousReviewHeadSha256));
        ValidateOptionalSha256(expectedExistingReviewHeadSha256, nameof(expectedExistingReviewHeadSha256));
        var requestedKeyFingerprint = Sha256(pseudonymKey.Span);

        var finalDirectory = GetArchiveDirectory(seasonId);
        if (Directory.Exists(finalDirectory))
        {
            if (expectedExistingReviewHeadSha256 is null)
            {
                throw new InvalidOperationException(
                    "An existing immutable weekly archive requires its externally published review head SHA-256 for idempotent replay.");
            }
            var existing = VerifyDirectory(
                finalDirectory,
                seasonId,
                allowReviewLock: false,
                expectedExistingReviewHeadSha256);
            if (existing.Manifest.PreviousSeasonId != previousSeasonId ||
                existing.Manifest.IncludesHtml != includeHtml ||
                !string.Equals(
                    existing.Report.Privacy.PseudonymKeyFingerprint,
                    requestedKeyFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The immutable weekly archive already exists with a conflicting generation request.");
            }
            if (previousSeasonId is Guid existingPreviousSeasonId)
            {
                var existingPrevious = Verify(
                    existingPreviousSeasonId,
                    previousReviewHeadSha256!);
                if (existingPrevious.ReviewStatus.State != "approved" ||
                    !string.Equals(
                        existingPrevious.Report.Season.ServerId,
                        existing.Report.Season.ServerId,
                        StringComparison.Ordinal) ||
                    existingPrevious.Report.Season.EndsAt != existing.Report.Season.StartsAt ||
                    !string.Equals(
                        existingPrevious.Report.Privacy.PseudonymKeyFingerprint,
                        requestedKeyFingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Idempotent replay requires the externally pinned approved adjacent previous report.");
                }
            }
            return new WeeklyEconomyReportArchiveResult(
                finalDirectory,
                existing.ManifestSha256,
                existing.ReviewHeadSha256,
                Created: false,
                IdempotentReplay: true,
                existing.Report,
                existing.ReviewStatus);
        }

        var frozen = await LoadFrozenPackageAsync(seasonId, cancellationToken);
        var season = await LoadSeasonAsync(seasonId, cancellationToken);
        ValidateFrozenSeason(season, frozen.Snapshot);
        var analytics = await LoadAnalyticsAsync(season, cancellationToken);

        var authoritativePreviousSeasonId = await FindAuthoritativePreviousSeasonIdAsync(
            season,
            cancellationToken);
        if (previousSeasonId is null && authoritativePreviousSeasonId is not null)
        {
            throw new InvalidOperationException(
                "An adjacent previous authoritative season exists and must be supplied with --previous-season.");
        }
        if (previousSeasonId is Guid requestedPrevious &&
            authoritativePreviousSeasonId != requestedPrevious)
        {
            throw new InvalidOperationException(
                "The requested previous season does not match the authoritative adjacent week.");
        }

        WeeklyEconomyReportArchiveVerification? previous = null;
        if (previousSeasonId is Guid priorId)
        {
            previous = Verify(priorId, previousReviewHeadSha256!);
            ValidatePreviousSeason(previous, season);
            if (!string.Equals(
                    previous.Report.Privacy.PseudonymKeyFingerprint,
                    requestedKeyFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Adjacent weekly reports must use the same registered pseudonym key.");
            }
        }

        var report = BuildReport(
            season,
            frozen.Snapshot,
            analytics,
            pseudonymKey.Span,
            previous?.Report);
        var restricted = BuildRestrictedAccounts(
            frozen.Snapshot,
            report,
            pseudonymKey.Span);

        var stagingDirectory = Path.Combine(
            _archiveRoot,
            $".{seasonId:D}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            var manifestFiles = new List<WeeklyEconomyReportManifestFile>();
            AddArtifact(
                stagingDirectory,
                "report.json",
                report.Privacy.ReportClassification,
                CanonicalBytes(report),
                manifestFiles);
            AddArtifact(
                stagingDirectory,
                "restricted-accounts.json",
                "restricted-operators-only",
                CanonicalBytes(restricted),
                manifestFiles);
            if (includeHtml)
            {
                AddArtifact(
                    stagingDirectory,
                    "report.html",
                    report.Privacy.ReportClassification,
                    Encoding.UTF8.GetBytes(BuildSafeHtml(report)),
                    manifestFiles);
            }

            var manifest = new WeeklyEconomyReportManifest(
                SchemaVersion,
                "pal-control-weekly-economy-report",
                season.SeasonId,
                season.ServerId,
                report.Source.CombinedSourceHash,
                _reviewTrustStoreSha256,
                previousSeasonId,
                includeHtml,
                manifestFiles.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());
            var manifestBytes = CanonicalBytes(manifest);
            var manifestSha = Sha256(manifestBytes);
            WriteNewFile(Path.Combine(stagingDirectory, "manifest.json"), manifestBytes);
            WriteNewFile(
                Path.Combine(stagingDirectory, "manifest.sha256"),
                Encoding.ASCII.GetBytes($"{manifestSha}  manifest.json\n"));

            var reviews = Path.Combine(stagingDirectory, "reviews");
            Directory.CreateDirectory(reviews);
            var initialReview = new WeeklyEconomyReportReviewRevision(
                SchemaVersion,
                Sequence: 0,
                manifestSha,
                PreviousRevisionSha256: null,
                Review: null,
                new WeeklyEconomyReportReviewStatus(
                    "pending",
                    RequiredReviewApprovals,
                    DistinctApprovals: 0,
                    Rejected: false,
                    report.Season.FrozenAt));
            var initialReviewBytes = CanonicalBytes(initialReview);
            var initialReviewHeadSha256 = Sha256(initialReviewBytes);
            WriteNewFile(
                Path.Combine(reviews, ReviewFileName(0)),
                initialReviewBytes);
            ProtectImmutableFiles(stagingDirectory);

            try
            {
                Directory.Move(stagingDirectory, finalDirectory);
            }
            catch (IOException) when (Directory.Exists(finalDirectory))
            {
                DeleteDirectory(stagingDirectory);
                throw new InvalidOperationException(
                    "A concurrent weekly archive generation won the publish race. Re-run with its externally published review head SHA-256.");
            }

            var verified = VerifyDirectory(
                finalDirectory,
                seasonId,
                allowReviewLock: false,
                initialReviewHeadSha256);
            return new WeeklyEconomyReportArchiveResult(
                finalDirectory,
                verified.ManifestSha256,
                verified.ReviewHeadSha256,
                Created: true,
                IdempotentReplay: false,
                verified.Report,
                verified.ReviewStatus);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                DeleteDirectory(stagingDirectory);
            }
            throw;
        }
    }

    public WeeklyEconomyReportArchiveVerification Verify(
        Guid seasonId,
        string expectedReviewHeadSha256)
    {
        ValidateSeasonId(seasonId);
        ValidateRequiredSha256(expectedReviewHeadSha256, nameof(expectedReviewHeadSha256));
        return VerifyDirectory(
            GetArchiveDirectory(seasonId),
            seasonId,
            allowReviewLock: false,
            expectedReviewHeadSha256);
    }

    public WeeklyEconomyReportArchiveVerification AppendReview(
        Guid seasonId,
        string reviewerSubject,
        string reviewerPrivateKeyPath,
        string decision,
        string reason,
        string expectedCurrentReviewHeadSha256)
    {
        ValidateSeasonId(seasonId);
        ValidateReviewerSubject(reviewerSubject);
        ValidateText(reason, 3, 500, nameof(reason));
        ValidateRequiredSha256(
            expectedCurrentReviewHeadSha256,
            nameof(expectedCurrentReviewHeadSha256));
        var normalizedSubject = reviewerSubject.Trim().ToLowerInvariant();
        if (!_trustedReviewKeys.TryGetValue(normalizedSubject, out var trustedReviewer))
        {
            throw new InvalidOperationException(
                "The reviewer subject is not present in the trusted review policy.");
        }
        var normalizedDecision = decision.Trim().ToLowerInvariant();
        if (normalizedDecision is not ("approve" or "reject"))
        {
            throw new ArgumentException("Review decision must be approve or reject.", nameof(decision));
        }

        var directory = GetArchiveDirectory(seasonId);
        var reviewDirectory = Path.Combine(directory, "reviews");
        var lockPath = Path.Combine(reviewDirectory, ".review.lock");
        using var reviewLock = new FileStream(
            lockPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose | FileOptions.WriteThrough);
        var verified = VerifyDirectory(
            directory,
            seasonId,
            allowReviewLock: true,
            expectedCurrentReviewHeadSha256);
        if (verified.ReviewStatus.State is "approved" or "rejected")
        {
            throw new InvalidOperationException("The weekly report review is already terminal.");
        }

        var existingReviewers = LoadReviewRevisions(reviewDirectory)
            .Select(revision => revision.Review?.ReviewerSubject)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (existingReviewers.Contains(normalizedSubject))
        {
            throw new InvalidOperationException(
                "One administrator subject may review a weekly report only once.");
        }

        var sequence = checked(verified.ReviewRevision + 1);
        var previousSha = verified.ReviewHeadSha256;
        var reviewedAt = _timeProvider.GetUtcNow();
        if (reviewedAt < verified.ReviewStatus.UpdatedAt)
        {
            throw new InvalidOperationException("The review clock moved backwards.");
        }
        var approvals = verified.ReviewStatus.DistinctApprovals +
            (normalizedDecision == "approve" ? 1 : 0);
        var rejected = normalizedDecision == "reject";
        var state = rejected
            ? "rejected"
            : approvals >= RequiredReviewApprovals ? "approved" : "pending";
        var normalizedReason = reason.Trim();
        var signaturePayload = new WeeklyEconomyReportReviewSignaturePayload(
            SchemaVersion,
            sequence,
            verified.ManifestSha256,
            previousSha,
            normalizedSubject,
            trustedReviewer.KeyFingerprint,
            normalizedDecision,
            normalizedReason,
            reviewedAt);
        var signature = SignReview(
            reviewerPrivateKeyPath,
            trustedReviewer,
            CanonicalBytes(signaturePayload));
        var revision = new WeeklyEconomyReportReviewRevision(
            SchemaVersion,
            sequence,
            verified.ManifestSha256,
            previousSha,
            new WeeklyEconomyReportReview(
                normalizedSubject,
                trustedReviewer.KeyFingerprint,
                ReviewSignatureAlgorithm,
                Convert.ToBase64String(signature),
                normalizedDecision,
                normalizedReason,
                reviewedAt),
            new WeeklyEconomyReportReviewStatus(
                state,
                RequiredReviewApprovals,
                approvals,
                rejected,
                reviewedAt));
        var path = Path.Combine(reviewDirectory, ReviewFileName(sequence));
        var revisionBytes = CanonicalBytes(revision);
        var newReviewHeadSha256 = Sha256(revisionBytes);
        WriteNewFile(path, revisionBytes);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        return VerifyDirectory(
            directory,
            seasonId,
            allowReviewLock: true,
            newReviewHeadSha256);
    }

    private async Task<FrozenPackage> LoadFrozenPackageAsync(
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenReadOnly();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_id, season_id, server_id, cutoff_at,
                   rules_hash, source_hash, snapshot_hash,
                   snapshot_json, evidence_json, frozen_at
            FROM season_leaderboard_snapshots
            WHERE season_id = $seasonId;
            """;
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "A weekly report can be generated only after the leaderboard is frozen.");
        }
        var snapshot = JsonSerializer.Deserialize<SeasonLeaderboardSnapshot>(
            reader.GetString(7),
            JsonOptions) ?? throw new InvalidDataException("The frozen leaderboard snapshot is null.");
        var evidence = JsonSerializer.Deserialize<SeasonLeaderboardEvidence>(
            reader.GetString(8),
            JsonOptions) ?? throw new InvalidDataException("The frozen leaderboard evidence is null.");
        if (snapshot.SnapshotId != RequiredGuid(reader.GetString(0), "snapshot id") ||
            snapshot.SeasonId != RequiredGuid(reader.GetString(1), "snapshot season") ||
            !string.Equals(snapshot.ServerId, reader.GetString(2), StringComparison.Ordinal) ||
            snapshot.CutoffAt != RequiredTimestamp(reader.GetString(3), "snapshot cutoff") ||
            !string.Equals(snapshot.RulesHash, RequiredSha256(reader.GetString(4), "rules hash"), StringComparison.Ordinal) ||
            !string.Equals(snapshot.SourceHash, RequiredSha256(reader.GetString(5), "source hash"), StringComparison.Ordinal) ||
            !string.Equals(snapshot.SnapshotHash, RequiredSha256(reader.GetString(6), "snapshot hash"), StringComparison.Ordinal) ||
            snapshot.FrozenAt != RequiredTimestamp(reader.GetString(9), "frozen timestamp") ||
            snapshot.SeasonId != evidence.SeasonId || snapshot.CutoffAt != evidence.CutoffAt ||
            !string.Equals(snapshot.FrameworkVersion, SeasonLeaderboardPolicy.FrameworkVersion, StringComparison.Ordinal) ||
            !string.Equals(snapshot.RulesHash, SeasonLeaderboardHash.Of(snapshot.Rules), StringComparison.Ordinal) ||
            !string.Equals(snapshot.SourceHash, SeasonLeaderboardHash.Of(evidence), StringComparison.Ordinal) ||
            !string.Equals(snapshot.SnapshotHash, SeasonLeaderboardHash.Snapshot(snapshot), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The frozen leaderboard package failed column, identity, or SHA-256 validation.");
        }
        return new FrozenPackage(snapshot, evidence);
    }

    private async Task<ExtractionSeason> LoadSeasonAsync(
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        var seasons = await LoadSeasonsAsync(cancellationToken);
        return seasons.GetValueOrDefault(seasonId) ?? throw new InvalidOperationException(
            "The frozen leaderboard references a missing authoritative season.");
    }

    private async Task<IReadOnlyDictionary<Guid, ExtractionSeason>> LoadSeasonsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = OpenReadOnly();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, event_type, occurred_at, payload
            FROM extraction_events
            ORDER BY sequence;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var seasons = new Dictionary<Guid, ExtractionSeason>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var payload = reader.GetString(3);
            ExtractionEventEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<ExtractionEventEnvelope>(payload, JsonOptions)
                    ?? throw new JsonException();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("An extraction event payload is invalid.", exception);
            }
            if (envelope.SchemaVersion != 1 ||
                envelope.EventId != RequiredGuid(reader.GetString(0), "event id") ||
                !string.Equals(envelope.EventType, reader.GetString(1), StringComparison.Ordinal) ||
                envelope.At != RequiredTimestamp(reader.GetString(2), "event timestamp"))
            {
                throw new InvalidDataException("An extraction event envelope differs from its SQL columns.");
            }
            if (envelope.Season is { } eventSeason)
            {
                seasons[eventSeason.SeasonId] = eventSeason;
            }
        }
        return seasons;
    }

    private async Task<Guid?> FindAuthoritativePreviousSeasonIdAsync(
        ExtractionSeason current,
        CancellationToken cancellationToken)
    {
        var seasons = await LoadSeasonsAsync(cancellationToken);
        var candidates = seasons.Values
            .Where(season =>
                season.SeasonId != current.SeasonId &&
                string.Equals(season.ServerId, current.ServerId, StringComparison.Ordinal) &&
                season.EndsAt == current.StartsAt)
            .Select(season => season.SeasonId)
            .Distinct()
            .ToArray();
        return candidates.Length switch
        {
            0 => null,
            1 => candidates[0],
            _ => throw new InvalidDataException(
                "More than one authoritative season claims the adjacent previous window.")
        };
    }

    private async Task<EconomyAnalyticsReport> LoadAnalyticsAsync(
        ExtractionSeason season,
        CancellationToken cancellationToken)
    {
        var from = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(season.StartsAt, _businessTimeZone).DateTime);
        var endInstant = season.EndsAt.AddTicks(-1);
        var to = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(endInstant, _businessTimeZone).DateTime);
        var query = new EconomyAnalyticsQuery(
            season.ServerId,
            from,
            to,
            EconomyAnalyticsDateBasis.Business,
            season.SeasonId,
            ContentVersionId: null,
            Limit: 100,
            Offset: 0);
        var analyticsStore = _analytics ?? throw new InvalidOperationException(
            "The authoritative analytics source is unavailable.");
        var first = await analyticsStore.QueryAsync(query, cancellationToken);
        if (!first.Window.Stable)
        {
            throw new InvalidOperationException(
                "The weekly analytics window is not stable through the season end date.");
        }
        if (!first.Source.Complete)
        {
            throw new InvalidDataException(
                "The authoritative analytics source is incomplete; the weekly report failed closed.");
        }
        if (first.Alerts.Any(alert => string.Equals(alert.Severity, "critical", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Critical authoritative analytics alerts block weekly report archival.");
        }
        var total = Math.Max(first.Page.TotalProducts, first.Page.TotalZones);
        if (total > MaximumDimensionRows)
        {
            throw new InvalidDataException("Weekly analytics dimensions exceed the bounded archive limit.");
        }
        var products = new List<EconomyAnalyticsProductMetric>(first.Products);
        for (var offset = 100; offset < total; offset += 100)
        {
            var page = await analyticsStore.QueryAsync(query with { Offset = offset }, cancellationToken);
            if (!string.Equals(
                    page.Source.RecomputationHash,
                    first.Source.RecomputationHash,
                    StringComparison.Ordinal) ||
                !page.Source.Complete ||
                !page.Window.Stable ||
                page.Window != first.Window ||
                page.Alerts.Any(alert => string.Equals(
                    alert.Severity,
                    "critical",
                    StringComparison.OrdinalIgnoreCase)) ||
                page.Page.TotalProducts != first.Page.TotalProducts ||
                page.Page.TotalZones != first.Page.TotalZones)
            {
                throw new InvalidDataException("Analytics pagination changed during weekly report generation.");
            }
            products.AddRange(page.Products);
        }
        if (products.Select(product => product.Sku)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != products.Count ||
            products.Count != first.Page.TotalProducts)
        {
            throw new InvalidDataException("Weekly analytics returned duplicate product dimensions.");
        }
        return first with
        {
            Products = products.OrderBy(product => product.Sku, StringComparer.Ordinal).ToArray(),
            Page = first.Page with { Limit = products.Count, Offset = 0, NextCursor = null }
        };
    }

    private static WeeklyEconomyReport BuildReport(
        ExtractionSeason season,
        SeasonLeaderboardSnapshot snapshot,
        EconomyAnalyticsReport analytics,
        ReadOnlySpan<byte> pseudonymKey,
        WeeklyEconomyReport? previous)
    {
        var keyFingerprint = Sha256(pseudonymKey);
        var resources = snapshot.Entries
            .SelectMany(entry => entry.Items)
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SeasonLeaderboardResourceAggregate(
                group.First().ItemId,
                group.Select(item => item.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                    ? group.First().Category
                    : "mixed",
                SumChecked(group.Select(item => item.Quantity)),
                SumChecked(group.Select(item => item.Value))))
            .OrderByDescending(item => item.Value)
            .ThenByDescending(item => item.Quantity)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal)
            .Select(item => new WeeklyEconomyReportResource(
                item.ItemId,
                item.Category,
                item.Quantity,
                item.Value,
                AverageMilli(item.Value, item.Quantity)))
            .ToArray();
        var totalQuantity = SumChecked(resources.Select(item => item.Quantity));
        var totalValue = SumChecked(resources.Select(item => item.Value));
        var rules = BuildAnomalyRules(snapshot.Entries);
        var anomalyCounts = CountAnomalies(snapshot.Entries, rules);
        var minimumCohort = analytics.Privacy.MinimumCohortSize;
        if (minimumCohort != MinimumPublicCohortSize)
        {
            throw new InvalidDataException(
                "The authoritative analytics privacy threshold is incompatible with the weekly report policy.");
        }
        var frozenParticipantCohortSize = snapshot.Entries.Count;
        var reportClassification = frozenParticipantCohortSize < minimumCohort
            ? SmallCohortReportClassification
            : ShareableReportClassification;
        var anomalySummary = rules.Select(rule =>
        {
            var count = anomalyCounts.GetValueOrDefault(rule.Code);
            var suppressed = count is > 0 && count < minimumCohort;
            return new WeeklyEconomyReportAnomalySummary(
                rule.Code,
                suppressed ? null : count,
                suppressed);
        }).ToArray();
        var currencies = analytics.Currencies
            .OrderBy(currency => currency.Currency, StringComparer.Ordinal)
            .Select(currency => new WeeklyEconomyReportCurrency(
                currency.Currency,
                currency.Accounts.Value,
                currency.Inflow,
                currency.Outflow,
                currency.Net,
                currency.BalanceP50,
                currency.BalanceP95,
                currency.Suppressed))
            .ToArray();
        var products = analytics.Products
            .OrderByDescending(product => product.DeliveredQuantity ?? -1)
            .ThenBy(product => product.Sku, StringComparer.Ordinal)
            .Take(MaximumPopularRows)
            .Select(product => new WeeklyEconomyReportProduct(
                product.Sku,
                product.DeliveredBuyers.Value,
                product.DeliveredQuantity,
                product.PurchaseRate.BasisPoints,
                product.DeliveredBuyers.Suppressed || product.PurchaseRate.Suppressed))
            .ToArray();
        var priceIndex = CommonBasketIndex(resources, previous?.InflationBasket);
        var sourceHash = HashCanonical(new
        {
            schemaVersion = SchemaVersion,
            snapshot.SnapshotHash,
            snapshot.SourceHash,
            analytics.Source.RecomputationHash,
            season.SeasonId,
            season.ServerId,
            season.StartsAt,
            season.EndsAt
        });
        var provisional = new WeeklyEconomyReport(
            SchemaVersion,
            "weekly-world-resource-economy",
            new WeeklyEconomyReportSeason(
                season.SeasonId,
                season.ServerId,
                season.Code,
                season.StartsAt,
                season.EndsAt,
                snapshot.CutoffAt,
                snapshot.FrozenAt,
                snapshot.SnapshotHash),
            new WeeklyEconomyReportPrivacy(
                PublicContainsPlayerIdentifiers: false,
                RestrictedArtifact: "restricted-accounts.json",
                PseudonymAlgorithm: "HMAC-SHA256/account-guid/v1",
                keyFingerprint,
                minimumCohort,
                frozenParticipantCohortSize,
                reportClassification),
            new WeeklyEconomyReportSource(
                "frozen-leaderboard-plus-sqlite-authoritative-analytics",
                snapshot.SourceHash,
                analytics.Source.RecomputationHash,
                sourceHash,
                analytics.Source.AsOf,
                analytics.Source.Tables.OrderBy(table => table, StringComparer.Ordinal).ToArray(),
                analytics.Source.RowsRead,
                analytics.Source.Complete && analytics.Window.Stable),
            currencies,
            new WeeklyEconomyReportInflation(
                totalQuantity,
                totalValue,
                AverageMilli(totalValue, totalQuantity),
                previous?.Season.SeasonId,
                priceIndex?.IndexBasisPoints,
                priceIndex is null ? null : checked(priceIndex.Value.IndexBasisPoints - 10_000),
                priceIndex?.ItemCount ?? 0,
                "Common-item Laspeyres basket weighted by previous-week quantities; first week has no index."),
            resources.OrderBy(resource => resource.ItemId, StringComparer.Ordinal).ToArray(),
            products,
            resources.Take(MaximumPopularRows).ToArray(),
            rules,
            anomalySummary,
            BuildComparison(currencies, totalQuantity, totalValue, previous),
            analytics.Alerts.OrderBy(alert => alert.Code, StringComparer.Ordinal).ToArray());
        return provisional;
    }

    private static WeeklyEconomyRestrictedAccounts BuildRestrictedAccounts(
        SeasonLeaderboardSnapshot snapshot,
        WeeklyEconomyReport report,
        ReadOnlySpan<byte> pseudonymKey)
    {
        var pseudonymKeyBytes = pseudonymKey.ToArray();
        var rules = report.AnomalyRules.ToDictionary(rule => rule.Code, StringComparer.Ordinal);
        WeeklyEconomyRestrictedAccount[] accounts;
        try
        {
            accounts = snapshot.Entries
                .Select(entry => (Entry: entry, Codes: MatchingAnomalies(entry, rules)))
                .Where(item => item.Codes.Count > 0)
                .Select(item => new WeeklyEconomyRestrictedAccount(
                    Pseudonym(
                        pseudonymKeyBytes,
                        "pal-control-weekly-report-account-v1",
                        item.Entry.AccountId.ToString("D")),
                    item.Codes,
                    item.Entry.SettledExchanges,
                    item.Entry.ResourceQuantity,
                    item.Entry.ResourceValue,
                    item.Entry.TaskPoints,
                    item.Entry.ResourceRank,
                    item.Entry.TaskRank,
                    item.Entry.IdentityBannedAtFreeze || item.Entry.ManuallyExcludedAtFreeze))
                .OrderBy(account => account.AccountPseudonym, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pseudonymKeyBytes);
        }
        if (accounts.Select(account => account.AccountPseudonym).Distinct(StringComparer.Ordinal).Count() !=
            accounts.Length)
        {
            throw new InvalidDataException("Account pseudonyms collide in the restricted weekly report.");
        }
        return new WeeklyEconomyRestrictedAccounts(
            SchemaVersion,
            "restricted-operators-only",
            snapshot.SeasonId,
            snapshot.ServerId,
            report.Privacy.PseudonymAlgorithm,
            report.Privacy.PseudonymKeyFingerprint,
            accounts);
    }

    private static IReadOnlyList<WeeklyEconomyReportAnomalyRule> BuildAnomalyRules(
        IReadOnlyList<SeasonLeaderboardEntry> entries)
    {
        var resourceThreshold = OutlierThreshold(entries.Select(entry => entry.ResourceValue), 5_000);
        var exchangeThreshold = OutlierThreshold(entries.Select(entry => (long)entry.SettledExchanges), 20);
        var taskThreshold = OutlierThreshold(entries.Select(entry => (long)entry.TaskPoints), 500);
        return
        [
            new(
                "identity-or-manual-exclusion",
                "high",
                "Identity ban or manual exclusion was active at the immutable leaderboard freeze.",
                1),
            new(
                "resource-value-outlier",
                "medium",
                "Frozen resource value is at least max(5000, five times the season median).",
                resourceThreshold),
            new(
                "exchange-frequency-outlier",
                "medium",
                "Frozen settled-exchange count is at least max(20, five times the season median).",
                exchangeThreshold),
            new(
                "task-points-outlier",
                "medium",
                "Frozen task points are at least max(500, five times the season median).",
                taskThreshold)
        ];
    }

    private static Dictionary<string, long> CountAnomalies(
        IReadOnlyList<SeasonLeaderboardEntry> entries,
        IReadOnlyList<WeeklyEconomyReportAnomalyRule> rules)
    {
        var dictionary = rules.ToDictionary(rule => rule.Code, _ => 0L, StringComparer.Ordinal);
        var byCode = rules.ToDictionary(rule => rule.Code, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            foreach (var code in MatchingAnomalies(entry, byCode))
            {
                dictionary[code]++;
            }
        }
        return dictionary;
    }

    private static IReadOnlyList<string> MatchingAnomalies(
        SeasonLeaderboardEntry entry,
        IReadOnlyDictionary<string, WeeklyEconomyReportAnomalyRule> rules)
    {
        var result = new List<string>();
        if (entry.IdentityBannedAtFreeze || entry.ManuallyExcludedAtFreeze)
        {
            result.Add("identity-or-manual-exclusion");
        }
        if (entry.ResourceValue >= rules["resource-value-outlier"].Threshold)
        {
            result.Add("resource-value-outlier");
        }
        if (entry.SettledExchanges >= rules["exchange-frequency-outlier"].Threshold)
        {
            result.Add("exchange-frequency-outlier");
        }
        if (entry.TaskPoints >= rules["task-points-outlier"].Threshold)
        {
            result.Add("task-points-outlier");
        }
        return result.OrderBy(code => code, StringComparer.Ordinal).ToArray();
    }

    private static WeeklyEconomyReportWeekOverWeek BuildComparison(
        IReadOnlyList<WeeklyEconomyReportCurrency> currentCurrencies,
        long currentQuantity,
        long currentValue,
        WeeklyEconomyReport? previous)
    {
        if (previous is null)
        {
            return new WeeklyEconomyReportWeekOverWeek(
                null,
                Available: false,
                [],
                null,
                null,
                null,
                null);
        }
        var priorCurrencies = previous.Currencies.ToDictionary(
            currency => currency.Currency,
            StringComparer.Ordinal);
        var comparisons = currentCurrencies.Select(current =>
        {
            priorCurrencies.TryGetValue(current.Currency, out var prior);
            return new WeeklyEconomyReportCurrencyComparison(
                current.Currency,
                Difference(current.Inflow, prior?.Inflow),
                ChangeBasisPoints(current.Inflow, prior?.Inflow),
                Difference(current.Outflow, prior?.Outflow),
                ChangeBasisPoints(current.Outflow, prior?.Outflow),
                Difference(current.Net, prior?.Net));
        }).ToArray();
        return new WeeklyEconomyReportWeekOverWeek(
            previous.Season.SeasonId,
            Available: true,
            comparisons,
            checked(currentQuantity - previous.Inflation.ResourceQuantity),
            ChangeBasisPoints(currentQuantity, previous.Inflation.ResourceQuantity),
            checked(currentValue - previous.Inflation.ResourceValue),
            ChangeBasisPoints(currentValue, previous.Inflation.ResourceValue));
    }

    private static PriceIndex? CommonBasketIndex(
        IReadOnlyList<WeeklyEconomyReportResource> current,
        IReadOnlyList<WeeklyEconomyReportResource>? previous)
    {
        if (previous is null)
        {
            return null;
        }
        var currentByItem = current.ToDictionary(item => item.ItemId, StringComparer.OrdinalIgnoreCase);
        decimal currentBasket = 0;
        decimal priorBasket = 0;
        var count = 0;
        foreach (var prior in previous.Where(item => item.Quantity > 0 && item.Value > 0))
        {
            if (!currentByItem.TryGetValue(prior.ItemId, out var now) ||
                now.Quantity <= 0 || now.Value <= 0)
            {
                continue;
            }
            currentBasket += (decimal)now.Value / now.Quantity * prior.Quantity;
            priorBasket += prior.Value;
            count++;
        }
        if (count == 0 || priorBasket <= 0)
        {
            return null;
        }
        var basisPoints = checked((int)Math.Round(
            currentBasket / priorBasket * 10_000m,
            MidpointRounding.AwayFromZero));
        return new PriceIndex(basisPoints, count);
    }

    private WeeklyEconomyReportArchiveVerification VerifyDirectory(
        string directory,
        Guid expectedSeasonId,
        bool allowReviewLock,
        string expectedReviewHeadSha256)
    {
        ValidateRequiredSha256(expectedReviewHeadSha256, nameof(expectedReviewHeadSha256));
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Weekly report archive does not exist: {directory}");
        }
        RejectReparsePoint(directory);
        var manifestPath = Path.Combine(directory, "manifest.json");
        RejectReparsePoint(manifestPath);
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var manifest = ReadCanonical<WeeklyEconomyReportManifest>(manifestBytes, "manifest.json");
        var manifestSha = Sha256(manifestBytes);
        var sidecarPath = Path.Combine(directory, "manifest.sha256");
        RejectReparsePoint(sidecarPath);
        var sidecar = File.ReadAllBytes(sidecarPath);
        var expectedSidecar = Encoding.ASCII.GetBytes($"{manifestSha}  manifest.json\n");
        if (!sidecar.AsSpan().SequenceEqual(expectedSidecar))
        {
            throw new InvalidDataException("The weekly report manifest SHA-256 sidecar is invalid.");
        }
        if (manifest.SchemaVersion != SchemaVersion ||
            !string.Equals(manifest.PackageKind, "pal-control-weekly-economy-report", StringComparison.Ordinal) ||
            manifest.SeasonId != expectedSeasonId ||
            !IsSha256(manifest.CombinedSourceHash) ||
            !string.Equals(
                manifest.ReviewTrustStoreSha256,
                _reviewTrustStoreSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The weekly report manifest identity is invalid.");
        }
        if (manifest.Files.Count is < 2 or > 3 ||
            manifest.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Count)
        {
            throw new InvalidDataException("The weekly report manifest file set is invalid.");
        }
        var expectedNames = new HashSet<string>(
            ["manifest.json", "manifest.sha256", "reviews"],
            StringComparer.Ordinal);
        var verifiedArtifactBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in manifest.Files)
        {
            var expectedClassification = entry.Path == "restricted-accounts.json"
                ? "restricted-operators-only"
                : entry.Classification;
            if (entry.Path != Path.GetFileName(entry.Path) ||
                entry.Path is not ("report.json" or "restricted-accounts.json" or "report.html") ||
                (entry.Path == "restricted-accounts.json" &&
                 !string.Equals(entry.Classification, expectedClassification, StringComparison.Ordinal)) ||
                (entry.Path != "restricted-accounts.json" &&
                 entry.Classification is not (ShareableReportClassification or SmallCohortReportClassification)) ||
                !IsSha256(entry.Sha256) || entry.Bytes <= 0)
            {
                throw new InvalidDataException("A weekly report manifest file entry is unsafe.");
            }
            expectedNames.Add(entry.Path);
            var artifactPath = Path.Combine(directory, entry.Path);
            RejectReparsePoint(artifactPath);
            var bytes = File.ReadAllBytes(artifactPath);
            if (bytes.LongLength != entry.Bytes ||
                !string.Equals(Sha256(bytes), entry.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Weekly report artifact '{entry.Path}' failed SHA-256 validation.");
            }
            verifiedArtifactBytes.Add(entry.Path, bytes);
        }
        var actualNames = Directory.EnumerateFileSystemEntries(directory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (!actualNames.SetEquals(expectedNames))
        {
            throw new InvalidDataException("The weekly report archive contains an unmanifested top-level entry.");
        }
        var includesHtml = manifest.Files.Any(file => file.Path == "report.html");
        if (includesHtml != manifest.IncludesHtml ||
            !manifest.Files.Any(file => file.Path == "report.json") ||
            !manifest.Files.Any(file => file.Path == "restricted-accounts.json"))
        {
            throw new InvalidDataException("The weekly report manifest does not match its declared format.");
        }
        var report = ReadCanonical<WeeklyEconomyReport>(
            verifiedArtifactBytes["report.json"],
            "report.json");
        var restricted = ReadCanonical<WeeklyEconomyRestrictedAccounts>(
            verifiedArtifactBytes["restricted-accounts.json"],
            "restricted-accounts.json");
        ValidateArchiveDocuments(manifest, report, restricted);
        var reviews = VerifyReviews(
            Path.Combine(directory, "reviews"),
            manifestSha,
            report.Season.FrozenAt,
            allowReviewLock,
            _trustedReviewKeys);
        if (!string.Equals(
                reviews.HeadSha256,
                expectedReviewHeadSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The weekly report review head does not match the externally published SHA-256.");
        }
        VerifyDirectorySnapshotUnchanged(
            directory,
            manifestBytes,
            sidecar,
            verifiedArtifactBytes,
            expectedNames,
            reviews,
            allowReviewLock);
        return new WeeklyEconomyReportArchiveVerification(
            directory,
            manifestSha,
            reviews.HeadSha256,
            manifest,
            report,
            restricted,
            reviews.Status,
            reviews.Sequence);
    }

    private static void ValidateArchiveDocuments(
        WeeklyEconomyReportManifest manifest,
        WeeklyEconomyReport report,
        WeeklyEconomyRestrictedAccounts restricted)
    {
        var expectedReportClassification =
            report.Privacy.FrozenParticipantCohortSize < report.Privacy.PublicMinimumCohortSize
                ? SmallCohortReportClassification
                : ShareableReportClassification;
        var reportArtifacts = manifest.Files.Where(file =>
                file.Path is "report.json" or "report.html")
            .ToArray();
        if (report.SchemaVersion != SchemaVersion ||
            report.Season.SeasonId != manifest.SeasonId ||
            !string.Equals(report.Season.ServerId, manifest.ServerId, StringComparison.Ordinal) ||
            !string.Equals(report.Source.CombinedSourceHash, manifest.CombinedSourceHash, StringComparison.Ordinal) ||
            report.WeekOverWeek.PreviousSeasonId != manifest.PreviousSeasonId ||
            report.WeekOverWeek.Available != (manifest.PreviousSeasonId is not null) ||
            report.Privacy.PublicContainsPlayerIdentifiers ||
            !report.Source.Complete ||
            !IsSha256(report.Season.LeaderboardSnapshotHash) ||
            !IsSha256(report.Source.LeaderboardSourceHash) ||
            !IsSha256(report.Source.AnalyticsRecomputationHash) ||
            !IsSha256(report.Source.CombinedSourceHash) ||
            !IsSha256(report.Privacy.PseudonymKeyFingerprint) ||
            !string.Equals(report.Privacy.PseudonymAlgorithm, "HMAC-SHA256/account-guid/v1", StringComparison.Ordinal) ||
            report.Privacy.PublicMinimumCohortSize != MinimumPublicCohortSize ||
            report.Privacy.FrozenParticipantCohortSize < 0 ||
            !string.Equals(
                report.Privacy.ReportClassification,
                expectedReportClassification,
                StringComparison.Ordinal) ||
            reportArtifacts.Any(file => !string.Equals(
                file.Classification,
                expectedReportClassification,
                StringComparison.Ordinal)) ||
            report.InflationBasket.Select(item => item.ItemId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != report.InflationBasket.Count ||
            SumChecked(report.InflationBasket.Select(item => item.Quantity)) != report.Inflation.ResourceQuantity ||
            SumChecked(report.InflationBasket.Select(item => item.Value)) != report.Inflation.ResourceValue)
        {
            throw new InvalidDataException("The weekly aggregate report identity or source evidence is invalid.");
        }
        if (restricted.SchemaVersion != SchemaVersion ||
            restricted.SeasonId != manifest.SeasonId ||
            !string.Equals(restricted.ServerId, manifest.ServerId, StringComparison.Ordinal) ||
            !string.Equals(
                restricted.PseudonymKeyFingerprint,
                report.Privacy.PseudonymKeyFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(restricted.Classification, "restricted-operators-only", StringComparison.Ordinal) ||
            restricted.Accounts.Any(account => !IsSha256(account.AccountPseudonym)) ||
            restricted.Accounts.Select(account => account.AccountPseudonym)
                .Distinct(StringComparer.Ordinal).Count() != restricted.Accounts.Count)
        {
            throw new InvalidDataException("The restricted weekly account artifact is invalid.");
        }
        var expectedSourceHash = HashCanonical(new
        {
            schemaVersion = SchemaVersion,
            SnapshotHash = report.Season.LeaderboardSnapshotHash,
            SourceHash = report.Source.LeaderboardSourceHash,
            RecomputationHash = report.Source.AnalyticsRecomputationHash,
            report.Season.SeasonId,
            report.Season.ServerId,
            report.Season.StartsAt,
            report.Season.EndsAt
        });
        if (!string.Equals(expectedSourceHash, report.Source.CombinedSourceHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The combined weekly report source hash is invalid.");
        }
    }

    private static ReviewVerification VerifyReviews(
        string reviewDirectory,
        string manifestSha,
        DateTimeOffset frozenAt,
        bool allowReviewLock,
        IReadOnlyDictionary<string, TrustedReviewKey> trustedReviewKeys)
    {
        RejectReparsePoint(reviewDirectory);
        var names = Directory.EnumerateFileSystemEntries(reviewDirectory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToArray();
        if (names.Any(name =>
                !ReviewFileRegex().IsMatch(name) &&
                !(allowReviewLock && name == ".review.lock")))
        {
            throw new InvalidDataException("The weekly report review directory contains an unknown file.");
        }
        var reviewNames = names.Where(name => ReviewFileRegex().IsMatch(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (reviewNames.Length == 0)
        {
            throw new InvalidDataException("The weekly report review chain is missing.");
        }
        var reviewers = new HashSet<string>(StringComparer.Ordinal);
        var fileEvidence = new List<VerifiedFileEvidence>(reviewNames.Length);
        string? previousSha = null;
        var approvals = 0;
        var rejected = false;
        var state = "pending";
        var previousAt = frozenAt;
        WeeklyEconomyReportReviewStatus? latest = null;
        for (var index = 0; index < reviewNames.Length; index++)
        {
            var expectedName = ReviewFileName(index);
            if (!string.Equals(reviewNames[index], expectedName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The weekly report review chain has a sequence gap.");
            }
            var path = Path.Combine(reviewDirectory, reviewNames[index]);
            RejectReparsePoint(path);
            var bytes = File.ReadAllBytes(path);
            var bytesSha256 = Sha256(bytes);
            fileEvidence.Add(new VerifiedFileEvidence(path, bytes.LongLength, bytesSha256));
            var revision = ReadCanonical<WeeklyEconomyReportReviewRevision>(bytes, reviewNames[index]);
            if (revision.SchemaVersion != SchemaVersion || revision.Sequence != index ||
                !string.Equals(revision.ManifestSha256, manifestSha, StringComparison.Ordinal) ||
                !string.Equals(revision.PreviousRevisionSha256, previousSha, StringComparison.Ordinal) ||
                revision.Status.RequiredApprovals != RequiredReviewApprovals)
            {
                throw new InvalidDataException("A weekly report review revision failed chain validation.");
            }
            if (index == 0)
            {
                if (revision.Review is not null || revision.Status != new WeeklyEconomyReportReviewStatus(
                        "pending", RequiredReviewApprovals, 0, false, frozenAt))
                {
                    throw new InvalidDataException("The initial weekly report review state is invalid.");
                }
            }
            else
            {
                if (state is "approved" or "rejected" || revision.Review is null ||
                    !ReviewerSubjectPattern().IsMatch(revision.Review.ReviewerSubject) ||
                    !reviewers.Add(revision.Review.ReviewerSubject) ||
                    !trustedReviewKeys.TryGetValue(
                        revision.Review.ReviewerSubject,
                        out var trustedReviewer) ||
                    !string.Equals(
                        revision.Review.ReviewerKeyFingerprint,
                        trustedReviewer.KeyFingerprint,
                        StringComparison.Ordinal) ||
                    revision.Review.SignatureAlgorithm != ReviewSignatureAlgorithm ||
                    revision.Review.Decision is not ("approve" or "reject") ||
                    string.IsNullOrWhiteSpace(revision.Review.Reason) ||
                    revision.Review.Reason.Length is < 3 or > 500 ||
                    revision.Review.Reason != revision.Review.Reason.Trim() ||
                    revision.Review.ReviewedAt < previousAt)
                {
                    throw new InvalidDataException("A weekly report review event is invalid.");
                }
                var signaturePayload = new WeeklyEconomyReportReviewSignaturePayload(
                    SchemaVersion,
                    index,
                    manifestSha,
                    previousSha,
                    revision.Review.ReviewerSubject,
                    revision.Review.ReviewerKeyFingerprint,
                    revision.Review.Decision,
                    revision.Review.Reason,
                    revision.Review.ReviewedAt);
                VerifyReviewSignature(
                    revision.Review,
                    trustedReviewer,
                    CanonicalBytes(signaturePayload));
                if (revision.Review.Decision == "approve")
                {
                    approvals++;
                }
                else
                {
                    rejected = true;
                }
                state = rejected
                    ? "rejected"
                    : approvals >= RequiredReviewApprovals ? "approved" : "pending";
                var expectedStatus = new WeeklyEconomyReportReviewStatus(
                    state,
                    RequiredReviewApprovals,
                    approvals,
                    rejected,
                    revision.Review.ReviewedAt);
                if (revision.Status != expectedStatus)
                {
                    throw new InvalidDataException("A weekly report review status is not derived from its chain.");
                }
                previousAt = revision.Review.ReviewedAt;
            }
            latest = revision.Status;
            previousSha = bytesSha256;
        }
        return new ReviewVerification(
            latest!,
            reviewNames.Length - 1,
            previousSha!,
            fileEvidence);
    }

    private static void VerifyDirectorySnapshotUnchanged(
        string directory,
        byte[] manifestBytes,
        byte[] sidecarBytes,
        IReadOnlyDictionary<string, byte[]> artifactBytes,
        IReadOnlySet<string> expectedTopLevelNames,
        ReviewVerification reviews,
        bool allowReviewLock)
    {
        var reviewDirectory = Path.Combine(directory, "reviews");
        var expectedReviewNames = reviews.Files
            .Select(file => Path.GetFileName(file.Path))
            .ToHashSet(StringComparer.Ordinal);
        if (allowReviewLock)
        {
            expectedReviewNames.Add(".review.lock");
        }

        VerifyEntrySet(directory, expectedTopLevelNames);
        VerifyEntrySet(reviewDirectory, expectedReviewNames);
        VerifyUnchangedFile(
            Path.Combine(directory, "manifest.json"),
            manifestBytes.LongLength,
            Sha256(manifestBytes));
        VerifyUnchangedFile(
            Path.Combine(directory, "manifest.sha256"),
            sidecarBytes.LongLength,
            Sha256(sidecarBytes));
        foreach (var artifact in artifactBytes)
        {
            VerifyUnchangedFile(
                Path.Combine(directory, artifact.Key),
                artifact.Value.LongLength,
                Sha256(artifact.Value));
        }
        foreach (var review in reviews.Files)
        {
            VerifyUnchangedFile(review.Path, review.Bytes, review.Sha256);
        }
        VerifyEntrySet(directory, expectedTopLevelNames);
        VerifyEntrySet(reviewDirectory, expectedReviewNames);
    }

    private static void VerifyEntrySet(string directory, IReadOnlySet<string> expectedNames)
    {
        RejectReparsePoint(directory);
        var actualNames = Directory.EnumerateFileSystemEntries(directory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (!actualNames.SetEquals(expectedNames))
        {
            throw new InvalidDataException(
                "The weekly report archive changed during verification.");
        }
    }

    private static void VerifyUnchangedFile(
        string path,
        long expectedBytes,
        string expectedSha256)
    {
        RejectReparsePoint(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != expectedBytes ||
            !string.Equals(Sha256(bytes), expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A weekly report archive file changed during verification.");
        }
        RejectReparsePoint(path);
    }

    private static IReadOnlyList<WeeklyEconomyReportReviewRevision> LoadReviewRevisions(
        string reviewDirectory) =>
        Directory.EnumerateFiles(reviewDirectory, "*-review-state.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => ReadCanonical<WeeklyEconomyReportReviewRevision>(
                File.ReadAllBytes(path),
                Path.GetFileName(path)))
            .ToArray();

    private static void ValidatePreviousSeason(
        WeeklyEconomyReportArchiveVerification previous,
        ExtractionSeason current)
    {
        if (!string.Equals(previous.Report.Season.ServerId, current.ServerId, StringComparison.Ordinal) ||
            previous.ReviewStatus.State != "approved" ||
            previous.Report.Season.EndsAt != current.StartsAt)
        {
            throw new InvalidOperationException(
                "Week-over-week comparison requires an approved, non-overlapping, adjacent report from the same server.");
        }
    }

    private static void ValidateFrozenSeason(
        ExtractionSeason season,
        SeasonLeaderboardSnapshot snapshot)
    {
        if (season.State is not (ExtractionSeasonState.Closed or ExtractionSeasonState.Archived) ||
            season.SeasonId != snapshot.SeasonId ||
            !string.Equals(season.ServerId, snapshot.ServerId, StringComparison.Ordinal) ||
            !string.Equals(season.Code, snapshot.SeasonCode, StringComparison.Ordinal) ||
            season.EndsAt > snapshot.CutoffAt ||
            snapshot.FrozenAt < snapshot.CutoffAt)
        {
            throw new InvalidDataException(
                "The weekly report source is not one coherent closed and frozen season.");
        }
    }

    private static void AddArtifact(
        string directory,
        string name,
        string classification,
        byte[] bytes,
        ICollection<WeeklyEconomyReportManifestFile> manifest)
    {
        WriteNewFile(Path.Combine(directory, name), bytes);
        manifest.Add(new WeeklyEconomyReportManifestFile(
            name,
            classification,
            bytes.LongLength,
            Sha256(bytes)));
    }

    private static void ProtectImmutableFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
        var initialReview = Path.Combine(directory, "reviews", ReviewFileName(0));
        File.SetAttributes(initialReview, File.GetAttributes(initialReview) | FileAttributes.ReadOnly);
    }

    private static void WriteNewFile(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static string BuildSafeHtml(WeeklyEconomyReport report)
    {
        Func<string, string> encode = value => HtmlEncoder.Default.Encode(value);
        var builder = new StringBuilder();
        builder.Append("<!doctype html>\n<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<title>周档经济报告 ").Append(encode(report.Season.SeasonCode)).Append("</title>")
            .Append("<style>body{font-family:system-ui,sans-serif;max-width:960px;margin:2rem auto;padding:0 1rem;color:#17202a}")
            .Append("table{border-collapse:collapse;width:100%;margin:1rem 0}th,td{border:1px solid #ccd1d1;padding:.5rem;text-align:left}")
            .Append("code{overflow-wrap:anywhere}</style></head><body>")
            .Append("<h1>周档经济报告：").Append(encode(report.Season.SeasonCode)).Append("</h1>")
            .Append("<p>服务器：").Append(encode(report.Season.ServerId)).Append("；周档：<code>")
            .Append(report.Season.SeasonId.ToString("D")).Append("</code></p>")
            .Append("<p>Classification: <code>")
            .Append(encode(report.Privacy.ReportClassification))
            .Append("</code>; frozen participant cohort: ")
            .Append(report.Privacy.FrozenParticipantCohortSize.ToString(CultureInfo.InvariantCulture))
            .Append("</p>")
            .Append("<p>来源哈希：<code>").Append(report.Source.CombinedSourceHash).Append("</code></p>")
            .Append("<h2>双币产销</h2><table><thead><tr><th>货币</th><th>流入</th><th>流出</th><th>净额</th></tr></thead><tbody>");
        foreach (var currency in report.Currencies)
        {
            builder.Append("<tr><td>").Append(encode(currency.Currency)).Append("</td><td>")
                .Append(FormatNullable(currency.Inflow, currency.Suppressed)).Append("</td><td>")
                .Append(FormatNullable(currency.Outflow, currency.Suppressed)).Append("</td><td>")
                .Append(FormatNullable(currency.Net, currency.Suppressed)).Append("</td></tr>");
        }
        builder.Append("</tbody></table><h2>热门商品</h2><table><thead><tr><th>SKU</th><th>送达数量</th></tr></thead><tbody>");
        foreach (var product in report.PopularProducts)
        {
            builder.Append("<tr><td>").Append(encode(product.Sku)).Append("</td><td>")
                .Append(FormatNullable(product.DeliveredQuantity, product.Suppressed)).Append("</td></tr>");
        }
        builder.Append("</tbody></table><h2>热门资源</h2><table><thead><tr><th>ItemID</th><th>类别</th><th>数量</th><th>价值</th></tr></thead><tbody>");
        foreach (var resource in report.PopularResources)
        {
            builder.Append("<tr><td>").Append(encode(resource.ItemId)).Append("</td><td>")
                .Append(encode(resource.Category)).Append("</td><td>")
                .Append(resource.Quantity.ToString(CultureInfo.InvariantCulture)).Append("</td><td>")
                .Append(resource.Value.ToString(CultureInfo.InvariantCulture)).Append("</td></tr>");
        }
        builder.Append("</tbody></table><p>账户级异常证据仅位于受限伪匿名文件，HTML 不包含账户明细。</p></body></html>\n");
        return builder.ToString();
    }

    private static string FormatNullable(long? value, bool suppressed) =>
        suppressed ? "已抑制" : value?.ToString(CultureInfo.InvariantCulture) ?? "不可用";

    private SqliteConnection OpenReadOnly()
    {
        if (_connectionString is null)
        {
            throw new InvalidOperationException("The authoritative SQLite source is unavailable.");
        }
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only=ON; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private string GetArchiveDirectory(Guid seasonId) =>
        Path.Combine(_archiveRoot, seasonId.ToString("D"));

    private static long OutlierThreshold(IEnumerable<long> values, long minimum)
    {
        var ordered = values.Where(value => value >= 0).OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return minimum;
        }
        var median = ordered[(ordered.Length - 1) / 2];
        var scaled = median > long.MaxValue / 5 ? long.MaxValue : median * 5;
        return Math.Max(minimum, scaled);
    }

    private static long? AverageMilli(long value, long quantity) =>
        quantity <= 0 ? null : checked((long)Math.Round(
            (decimal)value * 1_000m / quantity,
            MidpointRounding.AwayFromZero));

    private static long? Difference(long? current, long? previous) =>
        current is long now && previous is long prior ? checked(now - prior) : null;

    private static int? ChangeBasisPoints(long? current, long? previous) =>
        current is long now && previous is > 0
            ? checked((int)Math.Round(
                (decimal)(now - previous.Value) / previous.Value * 10_000m,
                MidpointRounding.AwayFromZero))
            : null;

    private static int? ChangeBasisPoints(long current, long previous) =>
        previous > 0
            ? checked((int)Math.Round(
                (decimal)(current - previous) / previous * 10_000m,
                MidpointRounding.AwayFromZero))
            : null;

    private static long SumChecked(IEnumerable<long> values)
    {
        long total = 0;
        foreach (var value in values)
        {
            total = checked(total + value);
        }
        return total;
    }

    private static string Pseudonym(
        ReadOnlySpan<byte> key,
        string domain,
        string value)
    {
        var material = Encoding.UTF8.GetBytes($"{domain}\n{value}");
        try
        {
            return Convert.ToHexStringLower(HMACSHA256.HashData(key, material));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static byte[] CanonicalBytes<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
        return CanonicalBytes(document.RootElement);
    }

    private static byte[] CanonicalBytes(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, element);
        }
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().ToArray();
                if (properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != properties.Length)
                {
                    throw new InvalidDataException("Canonical JSON contains a duplicate property.");
                }
                foreach (var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal))
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
                throw new InvalidDataException("Canonical JSON contains an unsupported token.");
        }
        writer.Flush();
    }

    private static void RejectDuplicateJsonProperties(JsonElement element, string name)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToArray();
                if (properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != properties.Length)
                {
                    throw new InvalidDataException(
                        $"The {name} contains a duplicate JSON property.");
                }
                foreach (var property in properties)
                {
                    RejectDuplicateJsonProperties(property.Value, name);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    RejectDuplicateJsonProperties(item, name);
                }
                break;
        }
    }

    private static T ReadCanonical<T>(byte[] bytes, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var canonical = CanonicalBytes(document.RootElement);
            if (!bytes.AsSpan().SequenceEqual(canonical))
            {
                throw new InvalidDataException($"Weekly report file '{name}' is not canonical JSON.");
            }
            return JsonSerializer.Deserialize<T>(bytes, StrictJsonOptions)
                ?? throw new InvalidDataException($"Weekly report file '{name}' is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Weekly report file '{name}' is invalid JSON.", exception);
        }
    }

    private static string HashCanonical<T>(T value) => Sha256(CanonicalBytes(value));
    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static Guid RequiredGuid(string value, string field) =>
        Guid.TryParse(value, out var result) && result != Guid.Empty
            ? result
            : throw new InvalidDataException($"The {field} is invalid.");

    private static DateTimeOffset RequiredTimestamp(string value, string field) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var result)
            ? result
            : throw new InvalidDataException($"The {field} is invalid.");

    private static string RequiredSha256(string value, string field) =>
        IsSha256(value) ? value : throw new InvalidDataException($"The {field} is invalid.");

    private static void ValidateSeasonId(Guid seasonId)
    {
        if (seasonId == Guid.Empty)
        {
            throw new ArgumentException("Season id cannot be empty.", nameof(seasonId));
        }
    }

    private static void ValidatePseudonymKey(ReadOnlySpan<byte> key)
    {
        if (key.Length < 32)
        {
            throw new ArgumentException("The weekly report pseudonym key must contain at least 32 bytes.");
        }
    }

    private static void ValidateRequiredSha256(string value, string parameter)
    {
        if (!IsSha256(value))
        {
            throw new ArgumentException(
                $"{parameter} must contain a 64-character lowercase SHA-256.",
                parameter);
        }
    }

    private static void ValidateOptionalSha256(string? value, string parameter)
    {
        if (value is not null)
        {
            ValidateRequiredSha256(value, parameter);
        }
    }

    private static bool IsNistP256Key(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        return key.KeySize == 256 &&
            string.Equals(
                parameters.Curve.Oid.Value,
                NistP256CurveOid,
                StringComparison.Ordinal);
    }

    private static (
        string Sha256,
        IReadOnlyDictionary<string, TrustedReviewKey> Keys) LoadReviewTrustStore(
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        RejectReparseAncestors(fullPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is < 1 or > 1024 * 1024)
        {
            throw new FileNotFoundException(
                "The weekly review trust store is missing or outside its size limit.",
                fullPath);
        }
        var bytes = File.ReadAllBytes(fullPath);
        WeeklyEconomyReportReviewTrustStore trust;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = StrictJsonOptions.MaxDepth
            });
            RejectDuplicateJsonProperties(document.RootElement, "weekly review trust store");
            trust = JsonSerializer.Deserialize<WeeklyEconomyReportReviewTrustStore>(
                        bytes,
                        StrictJsonOptions)
                    ?? throw new InvalidDataException("The weekly review trust store is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The weekly review trust store is invalid strict JSON.",
                exception);
        }
        if (trust.SchemaVersion != SchemaVersion ||
            string.IsNullOrWhiteSpace(trust.PolicyId) ||
            trust.PolicyId.Trim().Length is < 3 or > 128 ||
            trust.Keys is null || trust.Keys.Count is < RequiredReviewApprovals or > 32)
        {
            throw new InvalidDataException(
                "The weekly review trust store identity or key count is invalid.");
        }

        var keys = new Dictionary<string, TrustedReviewKey>(StringComparer.Ordinal);
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in trust.Keys)
        {
            ValidateReviewerSubject(item.Subject);
            var subject = item.Subject.Trim().ToLowerInvariant();
            if (item.Subject != subject ||
                item.Algorithm != ReviewSignatureAlgorithm ||
                string.IsNullOrWhiteSpace(item.PublicKeyPem))
            {
                throw new InvalidDataException(
                    "The weekly review trust store contains an invalid subject or algorithm.");
            }
            try
            {
                using var key = ECDsa.Create();
                key.ImportFromPem(item.PublicKeyPem);
                var publicKey = key.ExportSubjectPublicKeyInfo();
                var fingerprint = Sha256(publicKey);
                if (!IsNistP256Key(key) ||
                    !fingerprints.Add(fingerprint) ||
                    !keys.TryAdd(
                        subject,
                        new TrustedReviewKey(
                            key.ExportSubjectPublicKeyInfoPem(),
                            fingerprint)))
                {
                    throw new InvalidDataException(
                        "Weekly reviewers must have distinct subjects and ECDSA P-256 keys.");
                }
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException(
                    "The weekly review trust store contains an invalid ECDSA public key.",
                    exception);
            }
        }
        return (Sha256(bytes), keys);
    }

    private void ValidateExpectedReviewTrustStoreSha256(string expected)
    {
        if (!IsSha256(expected) ||
            !string.Equals(expected, _reviewTrustStoreSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The weekly review trust store does not match the externally approved SHA-256.");
        }
    }

    private static byte[] SignReview(
        string privateKeyPath,
        TrustedReviewKey trustedReviewer,
        ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPath);
        var fullPath = Path.GetFullPath(privateKeyPath);
        RejectReparseAncestors(fullPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is < 1 or > 64 * 1024)
        {
            throw new FileNotFoundException(
                "The reviewer private key is missing or outside its size limit.",
                fullPath);
        }
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(fullPath, Encoding.UTF8));
            var fingerprint = Sha256(key.ExportSubjectPublicKeyInfo());
            if (!IsNistP256Key(key) ||
                !string.Equals(
                    fingerprint,
                    trustedReviewer.KeyFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The reviewer private key does not match the trusted subject.");
            }
            return key.SignData(payload, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                "The reviewer private key is not a valid ECDSA P-256 key.",
                exception);
        }
    }

    private static void VerifyReviewSignature(
        WeeklyEconomyReportReview review,
        TrustedReviewKey trustedReviewer,
        ReadOnlySpan<byte> payload)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(review.SignatureBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "A weekly review signature is not valid base64.",
                exception);
        }
        if (signature.Length is < 64 or > 80)
        {
            throw new InvalidDataException("A weekly review signature has an invalid length.");
        }
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(trustedReviewer.PublicKeyPem);
            if (!IsNistP256Key(key) ||
                !key.VerifyData(payload, signature, HashAlgorithmName.SHA256))
            {
                throw new InvalidDataException("A weekly review signature is invalid.");
            }
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                "A weekly review signature could not be verified.",
                exception);
        }
    }

    private static void ValidateReviewerSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject) ||
            !ReviewerSubjectPattern().IsMatch(subject.Trim().ToLowerInvariant()))
        {
            throw new ArgumentException(
                "Reviewer subjects must be keyed pseudonyms in the form subj:hmac-sha256:<64 lowercase hex>.",
                nameof(subject));
        }
    }

    private static void ValidateText(string value, int minimum, int maximum, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < minimum ||
            value.Trim().Length > maximum || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{parameter} must contain {minimum} to {maximum} printable characters.",
                parameter);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException("A weekly report archive entry is missing.", path);
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Weekly report archives cannot contain reparse points.");
        }
    }

    private static void RejectReparseAncestors(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Weekly report paths cannot contain reparse points.");
        }
        var startingDirectory = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath)
            : file.Directory;
        for (DirectoryInfo? current = startingDirectory;
             current is not null;
             current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Weekly report paths cannot resolve through reparse points.");
            }
        }
    }

    private static void DeleteDirectory(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        Directory.Delete(directory, recursive: true);
    }

    private static string ReviewFileName(int sequence) =>
        $"{sequence:D4}-review-state.json";

    [GeneratedRegex("^[0-9]{4}-review-state\\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex ReviewFileRegex();

    [GeneratedRegex("^subj:hmac-sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReviewerSubjectPattern();

    private sealed record FrozenPackage(
        SeasonLeaderboardSnapshot Snapshot,
        SeasonLeaderboardEvidence Evidence);

    private sealed record ExtractionEventEnvelope
    {
        public int SchemaVersion { get; init; }
        public Guid EventId { get; init; }
        public string EventType { get; init; } = string.Empty;
        public DateTimeOffset At { get; init; }
        public ExtractionSeason? Season { get; init; }
    }

    private readonly record struct PriceIndex(int IndexBasisPoints, int ItemCount);
    private sealed record ReviewVerification(
        WeeklyEconomyReportReviewStatus Status,
        int Sequence,
        string HeadSha256,
        IReadOnlyList<VerifiedFileEvidence> Files);

    private sealed record VerifiedFileEvidence(
        string Path,
        long Bytes,
        string Sha256);

    private sealed record TrustedReviewKey(
        string PublicKeyPem,
        string KeyFingerprint);
}
