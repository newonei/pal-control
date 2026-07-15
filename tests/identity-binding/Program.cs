using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Extraction;

var cancellationToken = CancellationToken.None;
var directory = Path.Combine(
    Path.GetTempPath(),
    $"pal-control-identity-binding-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);

try
{
    await VerifyIdentityBindingLifecycleAsync(directory, cancellationToken);
    Console.WriteLine(
        "PASS: platform isolation, bidirectional uniqueness, weekly rebinding, display-name independence, history, and restart persistence.");
}
finally
{
    try
    {
        Directory.Delete(directory, recursive: true);
    }
    catch (IOException)
    {
        // A failed cleanup must not hide the binding invariant result.
    }
}

static async Task VerifyIdentityBindingLifecycleAsync(
    string directory,
    CancellationToken cancellationToken)
{
    var worldOne = Guid.NewGuid().ToString("N").ToUpperInvariant();
    var worldTwo = Guid.NewGuid().ToString("N").ToUpperInvariant();
    var playerUidAWeekOne = Guid.NewGuid().ToString("D");
    var playerUidAWeekTwo = Guid.NewGuid().ToString("N");
    var playerUidB = Guid.NewGuid().ToString("N");
    Guid accountAId;
    Guid seasonTwoId;
    Guid bindingTwoId;

    using (var repository = new SqliteExtractionRepository(directory))
    {
        var now = DateTimeOffset.UtcNow;
        var seasonOne = await repository.UpsertSeasonAsync(
            null,
            new ExtractionSeasonDefinition(
                "local",
                "identity-week-1",
                "Identity week 1",
                worldOne,
                now.AddHours(-1),
                now.AddDays(1),
                ExtractionSeasonState.Active),
            null,
            cancellationToken);
        var accountA = await repository.GetOrCreateAccountAsync(
            "steam",
            "steam_76561198000000001",
            "Alice",
            cancellationToken);
        var accountB = await repository.GetOrCreateAccountAsync(
            "steam",
            "steam_76561198000000002",
            "Bob",
            cancellationToken);
        accountAId = accountA.AccountId;

        Assert(accountA.AccountId != accountB.AccountId,
            "Steam A and Steam B unexpectedly shared an account.");
        var creditA = await repository.AdjustWalletAsync(
            new WalletAdjustmentRequest(
                accountA.AccountId,
                seasonOne.SeasonId,
                ExtractionCurrency.SeasonVoucher,
                25,
                "identity isolation seed",
                "identity-test",
                "steam-a",
                "identity-harness",
                "identity-wallet-a"),
            cancellationToken);
        Assert(creditA.Created, "Steam A wallet seed failed.");
        var walletA = await repository.GetWalletAsync(
            accountA.AccountId,
            seasonOne.SeasonId,
            cancellationToken);
        var walletB = await repository.GetWalletAsync(
            accountB.AccountId,
            seasonOne.SeasonId,
            cancellationToken);
        Assert(walletA.SeasonVoucher.Balance == 25 && walletB.SeasonVoucher.Balance == 0,
            "Steam A wallet state leaked into Steam B.");

        var forgedSubject = await repository.BindOrVerifyPlayerIdentityAsync(
            new PlayerIdentityBindingRequest(
                accountB.ExternalUserId,
                seasonOne.SeasonId,
                worldOne,
                playerUidAWeekOne,
                accountA.AccountId),
            cancellationToken);
        Assert(!forgedSubject.Verified &&
               forgedSubject.ErrorCode == "PLAYER_BINDING_SUBJECT_MISMATCH",
            "A forged platform UserId was accepted for another account.");
        var forgedNickname = await repository.BindOrVerifyPlayerIdentityAsync(
            new PlayerIdentityBindingRequest(
                accountA.DisplayName,
                seasonOne.SeasonId,
                worldOne,
                playerUidAWeekOne,
                accountA.AccountId),
            cancellationToken);
        Assert(!forgedNickname.Verified &&
               forgedNickname.ErrorCode == "PLAYER_BINDING_SUBJECT_MISMATCH",
            "A display name was accepted as an authorization subject.");
        await AssertThrowsAsync<ArgumentException>(
            () => repository.BindOrVerifyPlayerIdentityAsync(
                BindingRequest(accountA, seasonOne, worldOne, "1234abcd"),
                cancellationToken),
            "A short PlayerUID was accepted as a complete current-world identity.");

        var first = await repository.BindOrVerifyPlayerIdentityAsync(
            BindingRequest(accountA, seasonOne, worldOne, playerUidAWeekOne),
            cancellationToken);
        Assert(first.Created && first.Verified && first.Binding is not null,
            "The first server-observed identity was not bound.");
        var firstBinding = first.Binding ?? throw new InvalidOperationException(
            "The first binding result did not contain its binding.");
        Assert(firstBinding.PlayerUid == Guid.Parse(playerUidAWeekOne).ToString("N"),
            "PlayerUID was not stored in canonical complete form.");

        var bySubject = await repository.FindActivePlayerIdentityBindingAsync(
            "local",
            accountA.ExternalUserId,
            cancellationToken);
        var byUid = await repository.FindActivePlayerIdentityBindingAsync(
            "local",
            playerUidAWeekOne,
            cancellationToken);
        Assert(bySubject?.BindingId == firstBinding.BindingId &&
               byUid?.BindingId == firstBinding.BindingId,
            "Active binding lookup did not resolve the same platform/PlayerUID subject.");
        var bCannotReadA = await repository.GetPlayerIdentityBindingAsync(
            accountB.AccountId,
            seasonOne.SeasonId,
            worldOne,
            cancellationToken);
        Assert(bCannotReadA is null, "Steam B resolved Steam A's current-world binding.");

        var sameUidOtherAccount = await repository.BindOrVerifyPlayerIdentityAsync(
            BindingRequest(accountB, seasonOne, worldOne, playerUidAWeekOne),
            cancellationToken);
        Assert(!sameUidOtherAccount.Verified &&
               sameUidOtherAccount.ErrorCode == "PLAYER_UID_ALREADY_BOUND",
            "The same PlayerUID was accepted for two accounts.");

        var sameAccountOtherUid = await repository.BindOrVerifyPlayerIdentityAsync(
            BindingRequest(accountA, seasonOne, worldOne, playerUidB),
            cancellationToken);
        Assert(!sameAccountOtherUid.Verified &&
               sameAccountOtherUid.ErrorCode == "PLAYER_ACCOUNT_ALREADY_BOUND",
            "An account changed PlayerUID inside one weekly world.");

        await AssertDatabaseRejectsBidirectionalDuplicatesAsync(
            directory,
            firstBinding,
            accountB.AccountId,
            playerUidB,
            cancellationToken);

        var renamedA = await repository.GetOrCreateAccountAsync(
            "steam",
            accountA.ExternalUserId,
            "Alice Renamed",
            cancellationToken);
        Assert(renamedA.AccountId == accountA.AccountId,
            "A display-name change created a new authorization subject.");
        var afterRename = await repository.BindOrVerifyPlayerIdentityAsync(
            BindingRequest(renamedA, seasonOne, worldOne, playerUidAWeekOne),
            cancellationToken);
        Assert(!afterRename.Created && afterRename.Verified &&
               afterRename.Binding?.BindingId == firstBinding.BindingId,
            "A display-name change replaced the identity binding.");

        var weekOneHistory = await repository.ListPlayerIdentityBindingHistoryAsync(
            accountA.AccountId,
            seasonOne.SeasonId,
            100,
            cancellationToken);
        Assert(weekOneHistory.Count == 2 &&
               weekOneHistory.Any(entry => entry.Action == "bound") &&
               weekOneHistory.Any(entry => entry.Action == "verified"),
            "Binding creation and verification history was not persisted.");

        var closedSeasonOne = await repository.UpsertSeasonAsync(
            seasonOne.SeasonId,
            new ExtractionSeasonDefinition(
                seasonOne.ServerId,
                seasonOne.Code,
                seasonOne.DisplayName,
                seasonOne.WorldId,
                seasonOne.StartsAt,
                seasonOne.EndsAt,
                ExtractionSeasonState.Closed),
            seasonOne.Revision,
            cancellationToken);
        Assert(closedSeasonOne.State == ExtractionSeasonState.Closed,
            "Week one did not close before rollover.");

        var seasonTwo = await repository.UpsertSeasonAsync(
            null,
            new ExtractionSeasonDefinition(
                "local",
                "identity-week-2",
                "Identity week 2",
                worldTwo,
                now.AddDays(1),
                now.AddDays(8),
                ExtractionSeasonState.Active),
            null,
            cancellationToken);
        seasonTwoId = seasonTwo.SeasonId;
        var weekTwo = await repository.BindOrVerifyPlayerIdentityAsync(
            BindingRequest(renamedA, seasonTwo, worldTwo, playerUidAWeekTwo),
            cancellationToken);
        Assert(weekTwo.Created && weekTwo.Verified && weekTwo.Binding is not null,
            "The account could not bind a new complete PlayerUID in the new week.");
        bindingTwoId = (weekTwo.Binding ?? throw new InvalidOperationException(
            "The week-two binding result did not contain its binding.")).BindingId;

        var staleUidIsNotCurrent = await repository.FindActivePlayerIdentityBindingAsync(
            "local",
            playerUidAWeekOne,
            cancellationToken);
        var newUidIsCurrent = await repository.FindActivePlayerIdentityBindingAsync(
            "local",
            playerUidAWeekTwo,
            cancellationToken);
        Assert(staleUidIsNotCurrent is null && newUidIsCurrent?.BindingId == bindingTwoId,
            "The old-world PlayerUID remained valid after weekly rollover.");
    }

    using var reopened = new SqliteExtractionRepository(directory);
    var persisted = await reopened.GetPlayerIdentityBindingAsync(
        accountAId,
        seasonTwoId,
        worldTwo,
        cancellationToken);
    Assert(persisted?.BindingId == bindingTwoId &&
           persisted.PlayerUid == Guid.Parse(playerUidAWeekTwo).ToString("N"),
        "The current weekly binding did not survive a repository restart.");
    var historyAfterRestart = await reopened.ListPlayerIdentityBindingHistoryAsync(
        accountAId,
        null,
        100,
        cancellationToken);
    Assert(historyAfterRestart.Count == 3,
        "Binding history did not survive a repository restart.");
}

static PlayerIdentityBindingRequest BindingRequest(
    ExtractionAccount account,
    ExtractionSeason season,
    string worldId,
    string playerUid) => new(
    account.ExternalUserId,
    season.SeasonId,
    worldId,
    playerUid,
    account.AccountId);

static async Task AssertDatabaseRejectsBidirectionalDuplicatesAsync(
    string directory,
    PlayerIdentityBinding original,
    Guid otherAccountId,
    string otherPlayerUid,
    CancellationToken cancellationToken)
{
    var databasePath = Path.Combine(directory, "extraction-commerce.db");
    await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
    await connection.OpenAsync(cancellationToken);

    await AssertUniqueConstraintAsync(
        connection,
        original with
        {
            BindingId = Guid.NewGuid(),
            PlatformSubject = "steam_database_account_duplicate",
            PlayerUid = Guid.Parse(otherPlayerUid).ToString("N")
        },
        "The SQLite account-to-PlayerUID unique constraint is missing.",
        cancellationToken);
    await AssertUniqueConstraintAsync(
        connection,
        original with
        {
            BindingId = Guid.NewGuid(),
            PlatformSubject = "steam_database_uid_duplicate",
            AccountId = otherAccountId
        },
        "The SQLite PlayerUID-to-account unique constraint is missing.",
        cancellationToken);
}

static async Task AssertUniqueConstraintAsync(
    SqliteConnection connection,
    PlayerIdentityBinding binding,
    string failureMessage,
    CancellationToken cancellationToken)
{
    await using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO player_identity_bindings (
            binding_id, platform_subject, season_id, world_id, player_uid,
            account_id, first_bound_at, last_verified_at)
        VALUES (
            $bindingId, $platformSubject, $seasonId, $worldId, $playerUid,
            $accountId, $firstBoundAt, $lastVerifiedAt);
        """;
    command.Parameters.AddWithValue("$bindingId", binding.BindingId.ToString("D"));
    command.Parameters.AddWithValue("$platformSubject", binding.PlatformSubject);
    command.Parameters.AddWithValue("$seasonId", binding.SeasonId.ToString("D"));
    command.Parameters.AddWithValue("$worldId", binding.WorldId);
    command.Parameters.AddWithValue("$playerUid", binding.PlayerUid);
    command.Parameters.AddWithValue("$accountId", binding.AccountId.ToString("D"));
    command.Parameters.AddWithValue("$firstBoundAt", binding.FirstBoundAt.ToString("O"));
    command.Parameters.AddWithValue("$lastVerifiedAt", binding.LastVerifiedAt.ToString("O"));
    try
    {
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
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

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}
