using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Extraction;

var directory = Path.Combine(
    Path.GetTempPath(),
    $"pal-control-new-player-activity-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
try
{
    await VerifyLifecycleClaimAndRestartAsync(directory, CancellationToken.None);
    Console.WriteLine(
        "PASS: version immutability, RBAC-facing lifecycle data, exact identity/world isolation, atomic dual-wallet claim, replay conflict, uniqueness, and restart persistence.");
    return 0;
}
finally
{
    for (var attempt = 0; attempt < 5 && Directory.Exists(directory); attempt++)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException) when (attempt < 4)
        {
            await Task.Delay(50);
        }
        catch (UnauthorizedAccessException) when (attempt < 4)
        {
            await Task.Delay(50);
        }
    }
}

static async Task VerifyLifecycleClaimAndRestartAsync(
    string directory,
    CancellationToken cancellationToken)
{
    var worldId = Guid.NewGuid().ToString("N").ToUpperInvariant();
    var playerUid = Guid.NewGuid().ToString("N").ToLowerInvariant();
    Guid seasonId;
    Guid accountId;
    NewPlayerActivity published;
    NewPlayerActivityClaimRequest claimRequest;

    using (var repository = new SqliteExtractionRepository(directory))
    {
        var now = DateTimeOffset.UtcNow;
        var season = await repository.UpsertSeasonAsync(
            null,
            new ExtractionSeasonDefinition(
                "local",
                "WEEK-ACTIVITY",
                "Activity test week",
                worldId,
                now.AddHours(-1),
                now.AddHours(1),
                ExtractionSeasonState.Active),
            null,
            cancellationToken);
        var account = await repository.GetOrCreateAccountAsync(
            "steam",
            "steam_activity_user",
            "Activity User",
            cancellationToken);
        seasonId = season.SeasonId;
        accountId = account.AccountId;
        var binding = await repository.BindOrVerifyPlayerIdentityAsync(
            new PlayerIdentityBindingRequest(
                account.ExternalUserId,
                season.SeasonId,
                worldId,
                playerUid,
                account.AccountId),
            cancellationToken);
        Assert(binding.Verified && binding.Binding is not null,
            "The exact weekly-world identity fixture was not persisted.");

        var draft = await repository.CreateNewPlayerActivityDraftAsync(
            "welcome-pack",
            new NewPlayerActivityDefinition(
                "Welcome pack",
                "A versioned first-login economy grant.",
                MarketCoin: 100,
                SeasonVoucher: 25),
            "economy-admin:test",
            cancellationToken);
        Assert(draft.Version == 1 &&
               draft.State == NewPlayerActivityState.Draft &&
               draft.Revision == 0,
            "The first activity version was not created as draft v1.");

        var updated = await repository.UpdateNewPlayerActivityDraftAsync(
            draft.ActivityKey,
            draft.Version,
            new NewPlayerActivityDefinition(
                "Welcome pack v1",
                "The immutable published first-login economy grant.",
                MarketCoin: 120,
                SeasonVoucher: 30),
            expectedRevision: 0,
            cancellationToken);
        Assert(updated.Revision == 1 && updated.MarketCoin == 120,
            "The draft update did not honor optimistic revision control.");

        published = await repository.PublishNewPlayerActivityAsync(
            updated.ActivityKey,
            updated.Version,
            season,
            "economy-admin:test",
            cancellationToken);
        Assert(published.State == NewPlayerActivityState.Published &&
               published.PublishedSeasonId == season.SeasonId &&
               string.Equals(published.PublishedWorldId, worldId, StringComparison.OrdinalIgnoreCase),
            "Publication did not freeze the active season and world GUID.");

        await AssertActivityErrorAsync(
            () => repository.UpdateNewPlayerActivityDraftAsync(
                published.ActivityKey,
                published.Version,
                new NewPlayerActivityDefinition("Changed", "Forbidden mutation", 1, 1),
                published.Revision,
                cancellationToken),
            "NEW_PLAYER_ACTIVITY_IMMUTABLE",
            "A published activity version was editable through the repository.");

        using (var connection = OpenDatabase(directory))
        {
            var immutableAtDatabase = false;
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE new_player_activities
                    SET title = 'bypassed'
                    WHERE activity_id = $activityId;
                    """;
                command.Parameters.AddWithValue("$activityId", published.ActivityId.ToString("D"));
                command.ExecuteNonQuery();
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                immutableAtDatabase = true;
            }
            Assert(immutableAtDatabase,
                "SQLite did not reject a direct mutation of a published version.");
        }

        var secondDraft = await repository.CreateNewPlayerActivityDraftAsync(
            published.ActivityKey,
            new NewPlayerActivityDefinition("Welcome pack v2", "Next immutable version", 200, 50),
            "economy-admin:test",
            cancellationToken);
        Assert(secondDraft.Version == 2,
            "Creating a replacement draft did not advance the activity version.");
        await AssertActivityErrorAsync(
            () => repository.PublishNewPlayerActivityAsync(
                secondDraft.ActivityKey,
                secondDraft.Version,
                season,
                "economy-admin:test",
                cancellationToken),
            "NEW_PLAYER_ACTIVITY_VERSION_STILL_PUBLISHED",
            "A second version was published without explicitly closing the first.");

        claimRequest = new NewPlayerActivityClaimRequest(
            published.ActivityKey,
            published.Version,
            account.AccountId,
            season.SeasonId,
            worldId,
            playerUid,
            account.ExternalUserId,
            "activity-claim-0001",
            $"player:steam:{account.ExternalUserId}");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            repository.ClaimNewPlayerActivityAsync(claimRequest, cancellationToken)));
        Assert(concurrent.Count(result => result.Created) == 1 &&
               concurrent.All(result =>
                   result.ErrorCode is null &&
                   !result.IdempotencyConflict &&
                   result.Grant?.ActivityId == published.ActivityId),
            "Concurrent exact claims did not collapse to one immutable grant.");
        Assert(concurrent.Select(result => result.Grant?.GrantId).Distinct().Count() == 1,
            "Concurrent claim replays returned different grant identities.");

        var wallet = await repository.GetWalletAsync(
            account.AccountId,
            season.SeasonId,
            cancellationToken);
        var ledger = await repository.GetLedgerAsync(
            account.AccountId,
            season.SeasonId,
            100,
            cancellationToken);
        Assert(wallet.MarketCoin.Balance == 120 && wallet.SeasonVoucher.Balance == 30,
            "The dual-wallet grant was not applied exactly once.");
        Assert(ledger.Count == 2 &&
               ledger.Count(entry => entry.ReferenceType == "new_player_activity") == 2 &&
               ledger.Sum(entry => entry.Delta) == 150,
            "The dual-wallet transaction did not persist exactly two linked ledger entries.");

        var alternateKeyReplay = await repository.ClaimNewPlayerActivityAsync(
            claimRequest with { IdempotencyKey = "activity-claim-0002" },
            cancellationToken);
        Assert(!alternateKeyReplay.Created &&
               alternateKeyReplay.ErrorCode == "NEW_PLAYER_ACTIVITY_ALREADY_CLAIMED",
            "A different key bypassed the account+activity-version uniqueness boundary.");

        var conflict = await repository.ClaimNewPlayerActivityAsync(
            claimRequest with { ActivityVersion = secondDraft.Version },
            cancellationToken);
        Assert(conflict.IdempotencyConflict && conflict.ErrorCode == "IDEMPOTENCY_CONFLICT",
            "Reusing a claim key with a different activity version was not rejected.");

        var wrongRole = await repository.ClaimNewPlayerActivityAsync(
            claimRequest with
            {
                PlayerUid = Guid.NewGuid().ToString("N"),
                IdempotencyKey = "activity-wrong-role"
            },
            cancellationToken);
        Assert(wrongRole.ErrorCode == "NEW_PLAYER_ACTIVITY_IDENTITY_BINDING_REQUIRED",
            "A different PlayerUID was able to reuse the account's activity claim boundary.");

        var wrongWorld = await repository.ClaimNewPlayerActivityAsync(
            claimRequest with
            {
                WorldId = Guid.NewGuid().ToString("N"),
                IdempotencyKey = "activity-wrong-world"
            },
            cancellationToken);
        Assert(wrongWorld.ErrorCode == "NEW_PLAYER_ACTIVITY_WORLD_MISMATCH",
            "A different weekly world was able to reuse the account's activity claim boundary.");

        var available = await repository.ListAvailableNewPlayerActivitiesAsync(
            account.AccountId,
            season.SeasonId,
            worldId,
            cancellationToken);
        Assert(available.Count == 1 &&
               available[0].Grant?.GrantId == concurrent[0].Grant?.GrantId,
            "The player projection did not return the current-world claim state.");

        using var database = OpenDatabase(directory);
        Assert(Scalar(database, "SELECT COUNT(*) FROM new_player_activity_grants;") == 1,
            "SQLite persisted more than one account+activity-version grant.");
        Assert(Scalar(
                   database,
                   "SELECT COUNT(*) FROM extraction_events WHERE event_type = 'new_player_activity.claimed';") == 1,
            "The wallet event and activity grant did not share one exactly-once transaction boundary.");
        var grantImmutable = false;
        try
        {
            using var mutateGrant = database.CreateCommand();
            mutateGrant.CommandText = """
                UPDATE new_player_activity_grants
                SET market_coin = market_coin + 1;
                """;
            mutateGrant.ExecuteNonQuery();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            grantImmutable = true;
        }
        Assert(grantImmutable,
            "SQLite allowed an immutable activity grant to be edited after issuance.");
    }

    using (var reopened = new SqliteExtractionRepository(directory))
    {
        var replay = await reopened.ClaimNewPlayerActivityAsync(claimRequest, cancellationToken);
        Assert(!replay.Created && replay.IdempotentReplay &&
               replay.Grant is not null && replay.Activity?.ActivityId == published.ActivityId,
            "Exact claim replay did not survive a repository restart.");
        var wallet = await reopened.GetWalletAsync(accountId, seasonId, cancellationToken);
        var ledger = await reopened.GetLedgerAsync(accountId, seasonId, 100, cancellationToken);
        Assert(wallet.MarketCoin.Balance == 120 &&
               wallet.SeasonVoucher.Balance == 30 &&
               ledger.Count == 2,
            "Restart replay changed one side of the dual-wallet grant.");
    }
}

static SqliteConnection OpenDatabase(string directory)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(directory, "extraction-commerce.db"),
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ToString());
    connection.Open();
    return connection;
}

static long Scalar(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(command.ExecuteScalar());
}

static async Task AssertActivityErrorAsync(
    Func<Task> action,
    string expectedCode,
    string failureMessage)
{
    try
    {
        await action();
    }
    catch (NewPlayerActivityException exception) when (exception.Code == expectedCode)
    {
        return;
    }
    throw new InvalidOperationException(failureMessage);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
