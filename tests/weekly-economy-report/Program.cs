using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var root = Path.Combine(Path.GetTempPath(), $"pal-control-weekly-report-{Guid.NewGuid():N}");
var data = Path.Combine(root, "data");
var archives = Path.Combine(root, "archives");
Directory.CreateDirectory(data);
var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero));
var key = RandomNumberGenerator.GetBytes(32);
try
{
    var reviewerA = CreateReviewer(root, "reviewer-a");
    var reviewerB = CreateReviewer(root, "reviewer-b");
    var reviewTrustStore = Path.Combine(root, "weekly-review-trust.json");
    File.WriteAllText(
        reviewTrustStore,
        JsonSerializer.Serialize(
            new WeeklyEconomyReportReviewTrustStore(
                1,
                "weekly-report-reviewers-2026",
                [
                    new WeeklyEconomyReportReviewTrustKey(
                        reviewerA.Subject,
                        "ecdsa-p256-sha256",
                        reviewerA.PublicKeyPem),
                    new WeeklyEconomyReportReviewTrustKey(
                        reviewerB.Subject,
                        "ecdsa-p256-sha256",
                        reviewerB.PublicKeyPem)
                ]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        new UTF8Encoding(false));
    var reviewTrustHash = HashFile(reviewTrustStore);
    var duplicatePropertyTrustStore = Path.Combine(root, "duplicate-property-review-trust.json");
    var validTrustJson = File.ReadAllText(reviewTrustStore);
    File.WriteAllText(
        duplicatePropertyTrustStore,
        validTrustJson[..^1] + ",\"keys\":[]}",
        new UTF8Encoding(false));
    await AssertThrowsAsync(
        () => Task.FromResult(new WeeklyEconomyReportArchive(
            root,
            duplicatePropertyTrustStore,
            HashFile(duplicatePropertyTrustStore),
            clock)),
        "duplicate JSON property");

    var duplicatePublicKeyTrustStore = Path.Combine(root, "duplicate-public-key-review-trust.json");
    File.WriteAllText(
        duplicatePublicKeyTrustStore,
        JsonSerializer.Serialize(
            new WeeklyEconomyReportReviewTrustStore(
                1,
                "weekly-report-duplicate-key-policy",
                [
                    new WeeklyEconomyReportReviewTrustKey(
                        reviewerA.Subject,
                        "ecdsa-p256-sha256",
                        reviewerA.PublicKeyPem),
                    new WeeklyEconomyReportReviewTrustKey(
                        reviewerB.Subject,
                        "ecdsa-p256-sha256",
                        reviewerA.PublicKeyPem)
                ]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        new UTF8Encoding(false));
    await AssertThrowsAsync(
        () => Task.FromResult(new WeeklyEconomyReportArchive(
            root,
            duplicatePublicKeyTrustStore,
            HashFile(duplicatePublicKeyTrustStore),
            clock)),
        "distinct subjects and ECDSA P-256 keys");

    var nonP256TrustStore = Path.Combine(root, "non-p256-review-trust.json");
    using (var nonP256 = ECDsa.Create(ECCurve.NamedCurves.nistP384))
    {
        File.WriteAllText(
            nonP256TrustStore,
            JsonSerializer.Serialize(
                new WeeklyEconomyReportReviewTrustStore(
                    1,
                    "weekly-report-non-p256-policy",
                    [
                        new WeeklyEconomyReportReviewTrustKey(
                            reviewerA.Subject,
                            "ecdsa-p256-sha256",
                            reviewerA.PublicKeyPem),
                        new WeeklyEconomyReportReviewTrustKey(
                            reviewerB.Subject,
                            "ecdsa-p256-sha256",
                            nonP256.ExportSubjectPublicKeyInfoPem())
                    ]),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new UTF8Encoding(false));
    }
    await AssertThrowsAsync(
        () => Task.FromResult(new WeeklyEconomyReportArchive(
            root,
            nonP256TrustStore,
            HashFile(nonP256TrustStore),
            clock)),
        "distinct subjects and ECDSA P-256 keys");

    Guid seasonOne;
    Guid seasonTwo;
    Guid outlierAccount;
    string outlierExternalId;
    await using (var repository = new SqliteExtractionRepository(data, clock))
    {
        var analytics = new EconomyAnalyticsStore(data, TimeZoneInfo.Utc, clock);
        var accounts = new List<ExtractionAccount>();
        for (var index = 0; index < 6; index++)
        {
            var externalId = $"steam-weekly-private-{index:D2}";
            var account = await repository.GetOrCreateAccountAsync(
                "steam",
                externalId,
                $"Private Player {index:D2}",
                CancellationToken.None);
            accounts.Add(account);
            var funded = await repository.AdjustWalletAsync(
                new WalletAdjustmentRequest(
                    account.AccountId,
                    null,
                    ExtractionCurrency.MarketCoin,
                    10_000,
                    "pre-season fixture funding",
                    "harness_seed",
                    $"fund:{account.AccountId:N}",
                    "harness",
                    $"fund-{account.AccountId:N}"),
                CancellationToken.None);
            Assert(funded.Created, "Pre-season MarketCoin fixture was not created.");
        }
        outlierAccount = accounts[^1].AccountId;
        outlierExternalId = accounts[^1].ExternalUserId;

        var weekOne = await SeedWeekAsync(
            repository,
            analytics,
            data,
            accounts,
            clock,
            weekNumber: 1,
            startsAt: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            endsAt: new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero),
            businessDate: new DateOnly(2026, 7, 4),
            resourceUnitValue: 10,
            deliveredQuantity: 1,
            CancellationToken.None);
        seasonOne = weekOne.SeasonId;
        var weekTwo = await SeedWeekAsync(
            repository,
            analytics,
            data,
            accounts,
            clock,
            weekNumber: 2,
            startsAt: weekOne.EndsAt,
            endsAt: new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
            businessDate: new DateOnly(2026, 7, 11),
            resourceUnitValue: 12,
            deliveredQuantity: 2,
            CancellationToken.None);
        seasonTwo = weekTwo.SeasonId;

        clock.SetUtcNow(new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        var gate = new ExtractionOperationGateState(
            Maintenance: true,
            Reason: "weekly report harness",
            Actor: "harness",
            UpdatedAt: clock.GetUtcNow());
        var identity = new PlayerIdentitySecurityStore(data);
        var leaderboardStore = new SeasonLeaderboardStore(data, clock);
        var jobStore = new SeasonSettlementJobStore(data, clock);
        var jobs = new SeasonSettlementJobService(repository, jobStore, clock);
        var leaderboard = new SeasonLeaderboardService(
            repository,
            identity,
            leaderboardStore,
            jobs,
            clock);
        var frozenOne = await leaderboard.FreezeAsync(
            seasonOne,
            "harness-freezer",
            Guid.NewGuid().ToString("D"),
            gate,
            activeOperations: 0,
            CancellationToken.None);
        var frozenTwo = await leaderboard.FreezeAsync(
            seasonTwo,
            "harness-freezer",
            Guid.NewGuid().ToString("D"),
            gate,
            activeOperations: 0,
            CancellationToken.None);
        Assert(frozenOne.Snapshot.Entries.Count == 6 && frozenTwo.Snapshot.Entries.Count == 6,
            "Both synthetic weeks were not frozen from all authoritative accounts.");
    }

    // Keep the authoritative database open with write sharing denied for the
    // entire report campaign. The generator must use genuine SQLite read-only
    // connections and must not perform an idempotent migration on startup.
    using var sourceWriteGuard = new FileStream(
        Path.Combine(data, "extraction-commerce.db"),
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read | FileShare.Delete);
    var generator = new WeeklyEconomyReportArchive(
        data,
        archives,
        TimeZoneInfo.Utc,
        reviewTrustStore,
        reviewTrustHash,
        clock);
    var first = await generator.GenerateAsync(
        seasonOne,
        key,
        previousSeasonId: null,
        includeHtml: true);
    Assert(first.Created && !first.IdempotentReplay && first.ReviewStatus.State == "pending",
        "The first immutable weekly report was not created pending review.");
    var initialReviewPath = Path.Combine(
        first.ArchiveDirectory,
        "reviews",
        "0000-review-state.json");
    Assert(first.ReviewHeadSha256 == HashFile(initialReviewPath),
        "The generated archive did not publish its initial review head SHA-256.");
    await AssertThrowsAsync(
        () => generator.GenerateAsync(
            seasonOne,
            key,
            previousSeasonId: null,
            includeHtml: true),
        "requires its externally published review head");
    var replay = await generator.GenerateAsync(
        seasonOne,
        key,
        previousSeasonId: null,
        expectedExistingReviewHeadSha256: first.ReviewHeadSha256,
        includeHtml: true);
    Assert(!replay.Created && replay.IdempotentReplay &&
           replay.ManifestSha256 == first.ManifestSha256 &&
           replay.ReviewHeadSha256 == first.ReviewHeadSha256,
        "Pinned idempotent generation changed the first weekly archive identity.");
    var wrongKey = RandomNumberGenerator.GetBytes(32);
    try
    {
        await AssertThrowsAsync(
            () => generator.GenerateAsync(
                seasonOne,
                wrongKey,
                previousSeasonId: null,
                expectedExistingReviewHeadSha256: first.ReviewHeadSha256,
                includeHtml: true),
            "conflicting generation request");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(wrongKey);
    }

    var offline = new WeeklyEconomyReportArchive(
        archives,
        reviewTrustStore,
        reviewTrustHash,
        clock);
    await AssertThrowsAsync(
        () => Task.FromResult(offline.AppendReview(
            seasonOne,
            reviewerA.Subject,
            reviewerB.PrivateKeyPath,
            "approve",
            "A reviewer cannot borrow another trusted subject key.",
            first.ReviewHeadSha256)),
        "does not match the trusted subject");
    await AssertThrowsAsync(
        () => Task.FromResult(offline.AppendReview(
            seasonOne,
            reviewerA.Subject,
            reviewerA.PrivateKeyPath,
            "approve",
            "A review cannot append against an unrecognized head.",
            Hash("wrong-review-head"))),
        "does not match the externally published SHA-256");
    var oneApproval = offline.AppendReview(
        seasonOne,
        reviewerA.Subject,
        reviewerA.PrivateKeyPath,
        "approve",
        "Independent source and privacy review passed.",
        first.ReviewHeadSha256);
    Assert(oneApproval.ReviewStatus is { State: "pending", DistinctApprovals: 1 } &&
           oneApproval.ReviewHeadSha256 == HashFile(Path.Combine(
               oneApproval.ArchiveDirectory,
               "reviews",
               "0001-review-state.json")) &&
           oneApproval.ReviewHeadSha256 != first.ReviewHeadSha256,
        "One administrator incorrectly completed a two-person review.");
    var signedReviewPath = Path.Combine(
        oneApproval.ArchiveDirectory,
        "reviews",
        "0001-review-state.json");
    var signedReviewBytes = File.ReadAllBytes(signedReviewPath);
    var signedReviewText = Encoding.UTF8.GetString(signedReviewBytes);
    const string signatureMarker = "\"signatureBase64\":\"";
    var signatureOffset = signedReviewText.IndexOf(
        signatureMarker,
        StringComparison.Ordinal) + signatureMarker.Length;
    Assert(signatureOffset >= signatureMarker.Length,
        "The signed review did not contain its ECDSA signature.");
    var tamperedReview = signedReviewText.ToCharArray();
    tamperedReview[signatureOffset] = tamperedReview[signatureOffset] == 'A' ? 'B' : 'A';
    File.SetAttributes(signedReviewPath, FileAttributes.Normal);
    File.WriteAllText(signedReviewPath, new string(tamperedReview), new UTF8Encoding(false));
    await AssertThrowsAsync(
        () => Task.FromResult(offline.Verify(seasonOne, oneApproval.ReviewHeadSha256)),
        "signature");
    File.WriteAllBytes(signedReviewPath, signedReviewBytes);
    File.SetAttributes(signedReviewPath, FileAttributes.ReadOnly);
    _ = offline.Verify(seasonOne, oneApproval.ReviewHeadSha256);
    File.SetAttributes(signedReviewPath, FileAttributes.Normal);
    File.WriteAllText(
        signedReviewPath,
        signedReviewText[..^1] + ",\"zzUnknown\":\"unsigned-data\"}",
        new UTF8Encoding(false));
    await AssertThrowsAsync(
        () => Task.FromResult(offline.Verify(seasonOne, oneApproval.ReviewHeadSha256)),
        "invalid JSON");
    File.WriteAllBytes(signedReviewPath, signedReviewBytes);
    File.SetAttributes(signedReviewPath, FileAttributes.ReadOnly);
    var nestedReviewDirectory = Path.Combine(oneApproval.ArchiveDirectory, "reviews", "hidden");
    Directory.CreateDirectory(nestedReviewDirectory);
    await AssertThrowsAsync(
        () => Task.FromResult(offline.Verify(seasonOne, oneApproval.ReviewHeadSha256)),
        "unknown file");
    Directory.Delete(nestedReviewDirectory);
    _ = offline.Verify(seasonOne, oneApproval.ReviewHeadSha256);
    await AssertThrowsAsync(
        () => Task.FromResult(offline.AppendReview(
            seasonOne,
            reviewerA.Subject,
            reviewerA.PrivateKeyPath,
            "approve",
            "Duplicate reviewer must not count twice.",
            oneApproval.ReviewHeadSha256)),
        "One administrator subject may review");
    var approved = offline.AppendReview(
        seasonOne,
        reviewerB.Subject,
        reviewerB.PrivateKeyPath,
        "approve",
        "Second independent review passed.",
        oneApproval.ReviewHeadSha256);
    Assert(approved.ReviewStatus is { State: "approved", DistinctApprovals: 2 } &&
           approved.ReviewHeadSha256 != oneApproval.ReviewHeadSha256,
        "Two distinct administrator subjects did not approve the first archive.");

    await AssertThrowsAsync(
        () => Task.FromResult(offline.Verify(seasonOne, first.ReviewHeadSha256)),
        "does not match the externally published SHA-256");
    await AssertThrowsAsync(
        () => Task.FromResult(offline.Verify(seasonOne, Hash("wrong-approved-review-head"))),
        "does not match the externally published SHA-256");
    await AssertThrowsAsync(
        () => generator.GenerateAsync(
            seasonOne,
            key,
            previousSeasonId: null,
            expectedExistingReviewHeadSha256: oneApproval.ReviewHeadSha256,
            includeHtml: true),
        "does not match the externally published SHA-256");
    var approvedReplay = await generator.GenerateAsync(
        seasonOne,
        key,
        previousSeasonId: null,
        expectedExistingReviewHeadSha256: approved.ReviewHeadSha256,
        includeHtml: true);
    Assert(approvedReplay.IdempotentReplay &&
           approvedReplay.ReviewHeadSha256 == approved.ReviewHeadSha256,
        "An idempotent replay did not preserve the externally pinned approved review head.");

    var approvedReviewPath = Path.Combine(
        approved.ArchiveDirectory,
        "reviews",
        "0002-review-state.json");
    var approvedReviewBytes = File.ReadAllBytes(approvedReviewPath);
    File.SetAttributes(approvedReviewPath, FileAttributes.Normal);
    File.Delete(approvedReviewPath);
    await AssertThrowsAsync(
        () => Task.FromResult(offline.Verify(seasonOne, approved.ReviewHeadSha256)),
        "does not match the externally published SHA-256");
    await AssertThrowsAsync(
        () => Task.FromResult(offline.AppendReview(
            seasonOne,
            reviewerB.Subject,
            reviewerB.PrivateKeyPath,
            "approve",
            "A truncated chain cannot be rewritten from a published head.",
            approved.ReviewHeadSha256)),
        "does not match the externally published SHA-256");
    File.WriteAllBytes(approvedReviewPath, approvedReviewBytes);
    File.SetAttributes(approvedReviewPath, FileAttributes.ReadOnly);
    _ = offline.Verify(seasonOne, approved.ReviewHeadSha256);
    var alternateTrustStore = Path.Combine(root, "alternate-weekly-review-trust.json");
    File.WriteAllText(
        alternateTrustStore,
        File.ReadAllText(reviewTrustStore).Replace(
            "weekly-report-reviewers-2026",
            "weekly-report-reviewers-other",
            StringComparison.Ordinal),
        new UTF8Encoding(false));
    await AssertThrowsAsync(
        () => Task.FromResult(new WeeklyEconomyReportArchive(
            archives,
            alternateTrustStore,
            reviewTrustHash,
            clock).Verify(seasonOne, approved.ReviewHeadSha256)),
        "externally approved SHA-256");

    await AssertThrowsAsync(
        () => generator.GenerateAsync(
            seasonTwo,
            key,
            previousSeasonId: null,
            includeHtml: false),
        "must be supplied with --previous-season");

    await AssertThrowsAsync(
        () => generator.GenerateAsync(
            seasonTwo,
            key,
            previousSeasonId: seasonOne,
            previousReviewHeadSha256: oneApproval.ReviewHeadSha256,
            includeHtml: false),
        "does not match the externally published SHA-256");

    var second = await generator.GenerateAsync(
        seasonTwo,
        key,
        previousSeasonId: seasonOne,
        previousReviewHeadSha256: approved.ReviewHeadSha256,
        includeHtml: false);
    Assert(second.Created && second.Report.WeekOverWeek.Available &&
           second.Report.WeekOverWeek.PreviousSeasonId == seasonOne,
        "The second week did not bind its approved adjacent comparison baseline.");
    Assert(second.Report.Inflation is
    { CommonBasketPriceIndexBasisPoints: 12_000, InflationBasisPoints: 2_000 },
        "The common-resource weekly inflation comparison is incorrect.");
    Assert(second.Report.Currencies.Select(currency => currency.Currency)
        .SequenceEqual(["merchantCoin", "weeklyTicket"], StringComparer.Ordinal) &&
           second.Report.Currencies.All(currency => currency.Inflow is not null && currency.Outflow is not null),
        "The weekly report omitted dual-currency production or consumption.");
    Assert(second.Report.PopularProducts.Count == 10 &&
           second.Report.PopularProducts[0].Sku == "WEEKLY-KIT" &&
           second.Report.PopularProducts[0].DeliveredQuantity == 12,
        "Popular products were not completely paged and ordered from authoritative delivered quantities.");
    Assert(second.Report.PopularResources.Single().AverageUnitValueMilli == 12_000,
        "Popular resources did not preserve the frozen value snapshot.");

    var secondVerified = offline.Verify(seasonTwo, second.ReviewHeadSha256);
    var publicJson = File.ReadAllText(Path.Combine(secondVerified.ArchiveDirectory, "report.json"));
    var restrictedJson = File.ReadAllText(
        Path.Combine(secondVerified.ArchiveDirectory, "restricted-accounts.json"));
    Assert(!publicJson.Contains(outlierAccount.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
           !publicJson.Contains(outlierExternalId, StringComparison.Ordinal) &&
           !publicJson.Contains("Private Player", StringComparison.Ordinal) &&
           !restrictedJson.Contains(outlierAccount.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
           !restrictedJson.Contains(outlierExternalId, StringComparison.Ordinal) &&
           secondVerified.RestrictedAccounts.Accounts.Count == 1 &&
           secondVerified.RestrictedAccounts.Accounts[0].AccountPseudonym.Length == 64,
        "Public or restricted weekly artifacts leaked a raw account identity.");
    var firstPseudonym = approved.RestrictedAccounts.Accounts.Single().AccountPseudonym;
    Assert(firstPseudonym == secondVerified.RestrictedAccounts.Accounts.Single().AccountPseudonym,
        "The restricted account pseudonym is not stable across consecutive reports.");

    var reportPath = Path.Combine(secondVerified.ArchiveDirectory, "report.json");
    var originalReport = File.ReadAllBytes(reportPath);
    File.SetAttributes(reportPath, FileAttributes.Normal);
    File.WriteAllBytes(reportPath, [.. originalReport, (byte)' ']);
    await AssertThrowsAsync(
        () => Task.FromResult(offline.Verify(seasonTwo, second.ReviewHeadSha256)),
        "SHA-256 validation");
    File.WriteAllBytes(reportPath, originalReport);
    File.SetAttributes(reportPath, FileAttributes.ReadOnly);
    _ = offline.Verify(seasonTwo, second.ReviewHeadSha256);

    sourceWriteGuard.Dispose();
    var corruptData = Path.Combine(root, "corrupt-data");
    Directory.CreateDirectory(corruptData);
    BackupDatabase(
        Path.Combine(data, "extraction-commerce.db"),
        Path.Combine(corruptData, "extraction-commerce.db"));
    Execute(
        Path.Combine(corruptData, "extraction-commerce.db"),
        "UPDATE season_leaderboard_snapshots SET snapshot_json = json_set(snapshot_json, '$.seasonCode', 'TAMPERED') " +
        $"WHERE season_id = '{seasonTwo:D}';");
    var corruptGenerator = new WeeklyEconomyReportArchive(
        corruptData,
        Path.Combine(root, "corrupt-archives"),
        TimeZoneInfo.Utc,
        reviewTrustStore,
        reviewTrustHash,
        clock);
    await AssertThrowsAsync(
        () => corruptGenerator.GenerateAsync(seasonTwo, key),
        "failed column, identity, or SHA-256 validation");

    await AssertCohortClassificationAsync(
        root,
        reviewTrustStore,
        reviewTrustHash,
        key,
        participantCount: 1,
        expectedClassification: "restricted-small-cohort",
        rejectionReviewer: reviewerA);
    await AssertCohortClassificationAsync(
        root,
        reviewTrustStore,
        reviewTrustHash,
        key,
        participantCount: 4,
        expectedClassification: "restricted-small-cohort");
    await AssertCohortClassificationAsync(
        root,
        reviewTrustStore,
        reviewTrustHash,
        key,
        participantCount: 5,
        expectedClassification: "operator-shareable-aggregate");

    Console.WriteLine(
        "PASS: read-only source, two frozen synthetic weeks, canonical immutable reports, dual currency/products/inflation, " +
        "approved comparison, externally pinned review heads with truncation rejection, 1/4/5 privacy boundaries, " +
        "idempotent replay, restricted pseudonyms, trusted signed two-person review, and source/archive tamper fail-closed.");
    return 0;
}
finally
{
    CryptographicOperations.ZeroMemory(key);
    if (Directory.Exists(root))
    {
        NormalizeAttributes(root);
        Directory.Delete(root, recursive: true);
    }
}

static async Task AssertCohortClassificationAsync(
    string root,
    string reviewTrustStore,
    string reviewTrustHash,
    ReadOnlyMemory<byte> pseudonymKey,
    int participantCount,
    string expectedClassification,
    TestReviewer? rejectionReviewer = null)
{
    var cohortRoot = Path.Combine(root, $"cohort-{participantCount}");
    var data = Path.Combine(cohortRoot, "data");
    var archives = Path.Combine(cohortRoot, "archives");
    Directory.CreateDirectory(data);
    var startsAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    var endsAt = startsAt.AddDays(7);
    var clock = new MutableTimeProvider(startsAt);
    Guid seasonId;

    await using (var repository = new SqliteExtractionRepository(data, clock))
    {
        var analytics = new EconomyAnalyticsStore(data, TimeZoneInfo.Utc, clock);
        var accounts = new List<ExtractionAccount>();
        for (var index = 0; index < participantCount; index++)
        {
            var account = await repository.GetOrCreateAccountAsync(
                "steam",
                $"cohort-{participantCount}-private-{index:D2}",
                $"Cohort private player {index:D2}",
                CancellationToken.None);
            accounts.Add(account);
            var funded = await repository.AdjustWalletAsync(
                new WalletAdjustmentRequest(
                    account.AccountId,
                    null,
                    ExtractionCurrency.MarketCoin,
                    10_000,
                    "cohort boundary fixture funding",
                    "harness_seed",
                    $"cohort-fund:{participantCount}:{account.AccountId:N}",
                    "harness",
                    $"cohort-fund-{participantCount}-{account.AccountId:N}"),
                CancellationToken.None);
            Assert(funded.Created, "Cohort boundary MarketCoin fixture was not created.");
        }

        var season = await SeedWeekAsync(
            repository,
            analytics,
            data,
            accounts,
            clock,
            weekNumber: 10 + participantCount,
            startsAt,
            endsAt,
            businessDate: new DateOnly(2026, 8, 4),
            resourceUnitValue: 10,
            deliveredQuantity: 1,
            CancellationToken.None);
        seasonId = season.SeasonId;
        clock.SetUtcNow(endsAt.AddDays(1));
        var gate = new ExtractionOperationGateState(
            Maintenance: true,
            Reason: "weekly report cohort boundary harness",
            Actor: "harness",
            UpdatedAt: clock.GetUtcNow());
        var identity = new PlayerIdentitySecurityStore(data);
        var leaderboardStore = new SeasonLeaderboardStore(data, clock);
        var jobStore = new SeasonSettlementJobStore(data, clock);
        var jobs = new SeasonSettlementJobService(repository, jobStore, clock);
        var leaderboard = new SeasonLeaderboardService(
            repository,
            identity,
            leaderboardStore,
            jobs,
            clock);
        var frozen = await leaderboard.FreezeAsync(
            seasonId,
            "cohort-boundary-freezer",
            Guid.NewGuid().ToString("D"),
            gate,
            activeOperations: 0,
            CancellationToken.None);
        Assert(frozen.Snapshot.Entries.Count == participantCount,
            $"The {participantCount}-participant privacy fixture did not freeze the exact cohort.");
    }

    var generator = new WeeklyEconomyReportArchive(
        data,
        archives,
        TimeZoneInfo.Utc,
        reviewTrustStore,
        reviewTrustHash,
        clock);
    var generated = await generator.GenerateAsync(
        seasonId,
        pseudonymKey,
        includeHtml: true);
    var offline = new WeeklyEconomyReportArchive(
        archives,
        reviewTrustStore,
        reviewTrustHash,
        clock);
    var verified = offline.Verify(seasonId, generated.ReviewHeadSha256);
    var reportArtifacts = verified.Manifest.Files
        .Where(file => file.Path is "report.json" or "report.html")
        .ToArray();
    Assert(verified.Report.Privacy.PublicMinimumCohortSize == 5 &&
           verified.Report.Privacy.FrozenParticipantCohortSize == participantCount &&
           verified.Report.Privacy.ReportClassification == expectedClassification &&
           reportArtifacts.Length == 2 &&
           reportArtifacts.All(file => file.Classification == expectedClassification),
        $"The {participantCount}-participant report did not apply classification '{expectedClassification}'.");
    Assert(verified.Report.PopularResources.Count == 1,
        "A restricted small-cohort report unexpectedly discarded its internal exact resource evidence.");

    if (rejectionReviewer is not null)
    {
        var rejected = offline.AppendReview(
            seasonId,
            rejectionReviewer.Subject,
            rejectionReviewer.PrivateKeyPath,
            "reject",
            "The independent review rejected this fixture.",
            generated.ReviewHeadSha256);
        Assert(rejected.ReviewStatus is { State: "rejected", Rejected: true } &&
               rejected.ReviewHeadSha256 != generated.ReviewHeadSha256,
            "A signed rejection did not publish a terminal review head.");
        var rejectionPath = Path.Combine(
            rejected.ArchiveDirectory,
            "reviews",
            "0001-review-state.json");
        var rejectionBytes = File.ReadAllBytes(rejectionPath);
        File.SetAttributes(rejectionPath, FileAttributes.Normal);
        File.Delete(rejectionPath);
        await AssertThrowsAsync(
            () => Task.FromResult(offline.Verify(seasonId, rejected.ReviewHeadSha256)),
            "does not match the externally published SHA-256");
        await AssertThrowsAsync(
            () => Task.FromResult(offline.AppendReview(
                seasonId,
                rejectionReviewer.Subject,
                rejectionReviewer.PrivateKeyPath,
                "approve",
                "A deleted terminal rejection cannot be rewritten.",
                rejected.ReviewHeadSha256)),
            "does not match the externally published SHA-256");
        File.WriteAllBytes(rejectionPath, rejectionBytes);
        File.SetAttributes(rejectionPath, FileAttributes.ReadOnly);
        _ = offline.Verify(seasonId, rejected.ReviewHeadSha256);
    }
}

static TestReviewer CreateReviewer(string root, string name)
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var privateKeyPath = Path.Combine(root, $"{name}.private.pem");
    File.WriteAllText(
        privateKeyPath,
        key.ExportPkcs8PrivateKeyPem(),
        new UTF8Encoding(false));
    var subjectDigest = SHA256.HashData(Encoding.UTF8.GetBytes(name));
    return new TestReviewer(
        "subj:hmac-sha256:" + Convert.ToHexStringLower(subjectDigest),
        privateKeyPath,
        key.ExportSubjectPublicKeyInfoPem());
}

static async Task<ExtractionSeason> SeedWeekAsync(
    SqliteExtractionRepository repository,
    EconomyAnalyticsStore analytics,
    string dataDirectory,
    IReadOnlyList<ExtractionAccount> accounts,
    MutableTimeProvider clock,
    int weekNumber,
    DateTimeOffset startsAt,
    DateTimeOffset endsAt,
    DateOnly businessDate,
    long resourceUnitValue,
    int deliveredQuantity,
    CancellationToken cancellationToken)
{
    clock.SetUtcNow(startsAt.AddDays(3).AddHours(12));
    var seasonId = Guid.NewGuid();
    var season = await repository.UpsertSeasonAsync(
        seasonId,
        new ExtractionSeasonDefinition(
            "weekly-report-server",
            $"WEEK-{weekNumber:D2}",
            $"Synthetic week {weekNumber}",
            Guid.NewGuid().ToString("N"),
            startsAt,
            endsAt,
            ExtractionSeasonState.Active),
        null,
        cancellationToken);
    var contentVersionId = Guid.NewGuid();
    var contentHash = Hash($"weekly-content-{weekNumber}");
    CreateContentVersion(
        Path.Combine(dataDirectory, "extraction-commerce.db"),
        contentVersionId,
        weekNumber,
        businessDate,
        contentHash,
        clock.GetUtcNow());
    _ = await repository.UpsertProductAsync(
        new ShopProductDefinition(
            "WEEKLY-KIT",
            "Weekly Kit",
            "Weekly report fixture",
            ExtractionCurrency.MarketCoin,
            20,
            [new ShopItemGrant("Stone", 1)],
            null,
            true,
            null,
            null,
            ContentVersionId: contentVersionId,
            ContentHash: contentHash),
        null,
        "harness",
        cancellationToken);
    if (weekNumber == 2)
    {
        for (var productIndex = 0; productIndex < 104; productIndex++)
        {
            _ = await repository.UpsertProductAsync(
                new ShopProductDefinition(
                    $"UNSOLD-{productIndex:D3}",
                    $"Unpurchased product {productIndex:D3}",
                    "Pagination completeness fixture",
                    ExtractionCurrency.MarketCoin,
                    100 + productIndex,
                    [new ShopItemGrant("Stone", 1)],
                    null,
                    true,
                    null,
                    null,
                    ContentVersionId: contentVersionId,
                    ContentHash: contentHash),
                null,
                "harness",
                cancellationToken);
        }
    }

    var runs = new List<ExtractionSettlementRun>();
    for (var index = 0; index < accounts.Count; index++)
    {
        var account = accounts[index];
        await analytics.RecordPortalSessionAsync(
            account.AccountId,
            season.ServerId,
            season.SeasonId,
            contentVersionId,
            businessDate,
            cancellationToken);
        await analytics.RecordCatalogViewAsync(
            account.AccountId,
            season.ServerId,
            season.SeasonId,
            contentVersionId,
            businessDate,
            cancellationToken);
        var vouchers = await repository.AdjustWalletAsync(
            new WalletAdjustmentRequest(
                account.AccountId,
                season.SeasonId,
                ExtractionCurrency.SeasonVoucher,
                100,
                "weekly production fixture",
                "fixture",
                $"voucher:{season.SeasonId:N}:{index}",
                "harness",
                $"voucher-{season.SeasonId:N}-{index}"),
            cancellationToken);
        Assert(vouchers.Created, "Weekly voucher production was not created.");
        var voucherSpend = await repository.AdjustWalletAsync(
            new WalletAdjustmentRequest(
                account.AccountId,
                season.SeasonId,
                ExtractionCurrency.SeasonVoucher,
                -10,
                "weekly consumption fixture",
                "fixture",
                $"voucher-spend:{season.SeasonId:N}:{index}",
                "harness",
                $"voucher-spend-{season.SeasonId:N}-{index}"),
            cancellationToken);
        Assert(voucherSpend.Created, "Weekly voucher consumption was not created.");

        var delivered = await PurchaseAsync(
            repository,
            account,
            season,
            contentVersionId,
            contentHash,
            deliveredQuantity,
            $"delivered-{weekNumber}-{index}",
            deliver: true,
            cancellationToken);
        Assert(delivered.State == ShopOrderState.Delivered,
            "Delivered product fixture did not reach its authoritative terminal state.");
        var refunded = await PurchaseAsync(
            repository,
            account,
            season,
            contentVersionId,
            contentHash,
            1,
            $"refund-{weekNumber}-{index}",
            deliver: false,
            cancellationToken);
        Assert(refunded.State == ShopOrderState.Refunded,
            "Refund fixture did not produce authoritative MarketCoin inflow.");

        var quantity = index == accounts.Count - 1 ? 600 : 10;
        var now = clock.GetUtcNow().AddMinutes(index);
        runs.Add(new ExtractionSettlementRun(
            Guid.NewGuid(),
            account.AccountId,
            season.SeasonId,
            $"private-user-{index}",
            "zone-a",
            "Zone A",
            ExtractionSettlementState.Settled,
            [new ExtractionLootLine(
                "Stone",
                "Stone",
                quantity,
                resourceUnitValue,
                checked(quantity * resourceUnitValue))],
            quantity,
            checked(quantity * resourceUnitValue),
            Hash($"quote-{weekNumber}-{index}"),
            null,
            $"settle-{weekNumber}-{index}",
            null,
            null,
            now,
            now.AddMinutes(5),
            now,
            now)
        {
            Revision = 2,
            StateChangedAt = now,
            ContentVersionId = contentVersionId,
            ContentHash = contentHash,
            ContentBusinessDate = businessDate,
            ContentRulesVersion = $"weekly-v{weekNumber}",
            ZoneYieldMultiplierBasisPoints = 10_000
        });
    }
    await repository.PersistSettlementRunWritesAsync(
        runs.Select(ExtractionSettlementRunWrite.Insert).ToArray(),
        cancellationToken);
    return await repository.UpsertSeasonAsync(
        season.SeasonId,
        new ExtractionSeasonDefinition(
            season.ServerId,
            season.Code,
            season.DisplayName,
            season.WorldId,
            season.StartsAt,
            season.EndsAt,
            ExtractionSeasonState.Closed),
        season.Revision,
        cancellationToken);
}

static async Task<ShopOrder> PurchaseAsync(
    SqliteExtractionRepository repository,
    ExtractionAccount account,
    ExtractionSeason season,
    Guid contentVersionId,
    string contentHash,
    int quantity,
    string key,
    bool deliver,
    CancellationToken cancellationToken)
{
    var purchase = await repository.PurchaseAsync(
        new ShopPurchaseRequest(
            account.AccountId,
            season.SeasonId,
            season.ServerId,
            $"private-player-{account.AccountId:N}",
            [new ShopPurchaseLineInput("WEEKLY-KIT", quantity)],
            key,
            "harness",
            "weekly report fixture",
            ExpectedContentVersionId: contentVersionId,
            ExpectedContentHash: contentHash),
        cancellationToken);
    Assert(purchase.Created && purchase.Delivery is not null && purchase.Order is not null,
        $"Shop purchase fixture was not created: {purchase.ErrorCode} {purchase.ErrorMessage}");
    _ = await repository.MarkDeliveryDispatchedAsync(
        purchase.Delivery!.DeliveryId,
        Guid.NewGuid(),
        cancellationToken);
    if (deliver)
    {
        var outcome = await repository.MarkDeliveryOutcomeAsync(
            purchase.Delivery.DeliveryId,
            ShopDeliveryState.Delivered,
            null,
            null,
            cancellationToken);
        return outcome.Order!;
    }
    _ = await repository.MarkDeliveryOutcomeAsync(
        purchase.Delivery.DeliveryId,
        ShopDeliveryState.Failed,
        "FIXTURE_FAILED",
        "Known no-mutation fixture failure",
        cancellationToken);
    var refund = await repository.RefundFailedOrderAsync(
        purchase.Order!.OrderId,
        $"refund-{key}",
        "harness",
        "verified no mutation",
        cancellationToken);
    return refund.Order!;
}

static void CreateContentVersion(
    string database,
    Guid versionId,
    int versionNumber,
    DateOnly businessDate,
    string contentHash,
    DateTimeOffset publishedAt)
{
    using var connection = Open(database);
    using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS content_versions (
            version_id TEXT PRIMARY KEY,
            server_id TEXT NOT NULL,
            version_number INTEGER NOT NULL,
            business_date TEXT NOT NULL,
            rules_version TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            document_json TEXT NOT NULL,
            source_draft_id TEXT NOT NULL,
            published_by TEXT NOT NULL,
            published_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS content_current (
            server_id TEXT PRIMARY KEY,
            version_id TEXT NOT NULL,
            updated_at TEXT NOT NULL);
        INSERT INTO content_versions (
            version_id, server_id, version_number, business_date, rules_version,
            content_hash, document_json, source_draft_id, published_by, published_at)
        VALUES (
            $versionId, 'weekly-report-server', $versionNumber, $businessDate, $rulesVersion,
            $contentHash, $document, $draftId, 'harness', $publishedAt);
        INSERT INTO content_current (server_id, version_id, updated_at)
        VALUES ('weekly-report-server', $versionId, $publishedAt)
        ON CONFLICT(server_id) DO UPDATE SET
            version_id = excluded.version_id,
            updated_at = excluded.updated_at;
        """;
    command.Parameters.AddWithValue("$versionId", versionId.ToString("D"));
    command.Parameters.AddWithValue("$versionNumber", versionNumber);
    command.Parameters.AddWithValue("$businessDate", businessDate.ToString("yyyy-MM-dd"));
    command.Parameters.AddWithValue("$rulesVersion", $"weekly-v{versionNumber}");
    command.Parameters.AddWithValue("$contentHash", contentHash);
    command.Parameters.AddWithValue(
        "$document",
        "{\"resources\":[{\"itemId\":\"Stone\",\"category\":\"material\"}]}");
    command.Parameters.AddWithValue("$draftId", Guid.NewGuid().ToString("D"));
    command.Parameters.AddWithValue("$publishedAt", publishedAt.ToString("O"));
    command.ExecuteNonQuery();
}

static void BackupDatabase(string source, string target)
{
    using var sourceConnection = Open(source);
    using var targetConnection = Open(target);
    sourceConnection.BackupDatabase(targetConnection);
}

static void Execute(string database, string sql)
{
    using var connection = Open(database);
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static SqliteConnection Open(string database)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = database,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false
    }.ToString());
    connection.Open();
    return connection;
}

static async Task AssertThrowsAsync(Func<Task> action, string messageFragment)
{
    try
    {
        await action();
    }
    catch (Exception exception) when (
        exception.Message.Contains(messageFragment, StringComparison.OrdinalIgnoreCase))
    {
        return;
    }
    throw new InvalidOperationException($"Expected failure containing '{messageFragment}'.");
}

static string Hash(string value) => Convert.ToHexStringLower(
    SHA256.HashData(Encoding.UTF8.GetBytes(value)));

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void NormalizeAttributes(string directory)
{
    foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
    {
        File.SetAttributes(path, FileAttributes.Normal);
    }
}

sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
}

sealed record TestReviewer(
    string Subject,
    string PrivateKeyPath,
    string PublicKeyPem);
