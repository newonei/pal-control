using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PalControl.ControlApi.Extraction;

public sealed partial class SqliteExtractionRepository
{
    private const string NewPlayerActivityColumns = """
        activity_id, activity_key, version, state, title, description,
        market_coin, season_voucher, created_by, created_at, revision,
        published_season_id, published_world_id, published_by, published_at,
        closed_by, closed_at
        """;

    private const string NewPlayerActivityGrantColumns = """
        grant_id, activity_id, activity_key, activity_version, account_id,
        season_id, world_id, player_uid, platform_subject, market_coin,
        season_voucher, market_coin_ledger_entry_id,
        season_voucher_ledger_entry_id, market_coin_balance_after,
        season_voucher_balance_after, claimed_at
        """;

    public async Task<IReadOnlyList<NewPlayerActivity>> ListNewPlayerActivitiesAsync(
        string? activityKey,
        CancellationToken cancellationToken)
    {
        var normalizedKey = string.IsNullOrWhiteSpace(activityKey)
            ? null
            : NormalizeNewPlayerActivityKey(activityKey);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {NewPlayerActivityColumns}
                FROM new_player_activities
                WHERE ($activityKey IS NULL OR activity_key = $activityKey COLLATE NOCASE)
                ORDER BY activity_key COLLATE NOCASE, version DESC;
                """;
            command.Parameters.AddWithValue(
                "$activityKey",
                normalizedKey is null ? DBNull.Value : normalizedKey);
            var results = new List<NewPlayerActivity>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(ReadNewPlayerActivity(reader));
            }
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NewPlayerActivity?> GetNewPlayerActivityAsync(
        string activityKey,
        int version,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeNewPlayerActivityKey(activityKey);
        ValidateNewPlayerActivityVersion(version);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            return await ReadNewPlayerActivityAsync(
                connection,
                transaction: null,
                normalizedKey,
                version,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NewPlayerActivity> CreateNewPlayerActivityDraftAsync(
        string activityKey,
        NewPlayerActivityDefinition definition,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeNewPlayerActivityKey(activityKey);
        var normalizedDefinition = NormalizeNewPlayerActivityDefinition(definition);
        var normalizedActor = NormalizeRequired(actor, 256, nameof(actor));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var version = 1;
            await using (var versionCommand = connection.CreateCommand())
            {
                versionCommand.Transaction = (SqliteTransaction)transaction;
                versionCommand.CommandText = """
                    SELECT COALESCE(MAX(version), 0) + 1
                    FROM new_player_activities
                    WHERE activity_key = $activityKey COLLATE NOCASE;
                    """;
                versionCommand.Parameters.AddWithValue("$activityKey", normalizedKey);
                version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken));
            }

            var now = UtcNow();
            var activity = new NewPlayerActivity(
                Guid.NewGuid(),
                normalizedKey,
                version,
                NewPlayerActivityState.Draft,
                normalizedDefinition.Title,
                normalizedDefinition.Description,
                normalizedDefinition.MarketCoin,
                normalizedDefinition.SeasonVoucher,
                normalizedActor,
                now,
                Revision: 0,
                PublishedSeasonId: null,
                PublishedWorldId: null,
                PublishedBy: null,
                PublishedAt: null,
                ClosedBy: null,
                ClosedAt: null);
            await InsertNewPlayerActivityAsync(
                connection,
                (SqliteTransaction)transaction,
                activity,
                cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
            EnsureAuthoritativeMarker();
            return activity;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NewPlayerActivity> UpdateNewPlayerActivityDraftAsync(
        string activityKey,
        int version,
        NewPlayerActivityDefinition definition,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeNewPlayerActivityKey(activityKey);
        ValidateNewPlayerActivityVersion(version);
        var normalizedDefinition = NormalizeNewPlayerActivityDefinition(definition);
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var activity = await RequireNewPlayerActivityAsync(
                connection,
                (SqliteTransaction)transaction,
                normalizedKey,
                version,
                cancellationToken);
            if (activity.State != NewPlayerActivityState.Draft)
            {
                throw ActivityConflict(
                    "NEW_PLAYER_ACTIVITY_IMMUTABLE",
                    "Published or closed activity versions are immutable; create a new version instead.");
            }
            if (activity.Revision != expectedRevision)
            {
                throw ActivityConflict(
                    "NEW_PLAYER_ACTIVITY_REVISION_CONFLICT",
                    $"Activity revision changed (expected {expectedRevision}, actual {activity.Revision}).");
            }

            var updated = activity with
            {
                Title = normalizedDefinition.Title,
                Description = normalizedDefinition.Description,
                MarketCoin = normalizedDefinition.MarketCoin,
                SeasonVoucher = normalizedDefinition.SeasonVoucher,
                Revision = checked(activity.Revision + 1)
            };
            await UpdateNewPlayerActivityRowAsync(
                connection,
                (SqliteTransaction)transaction,
                updated,
                cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
            EnsureAuthoritativeMarker();
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NewPlayerActivity> PublishNewPlayerActivityAsync(
        string activityKey,
        int version,
        ExtractionSeason season,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(season);
        var normalizedKey = NormalizeNewPlayerActivityKey(activityKey);
        ValidateNewPlayerActivityVersion(version);
        var normalizedActor = NormalizeRequired(actor, 256, nameof(actor));
        if (season.State != ExtractionSeasonState.Active || season.SeasonId == Guid.Empty)
        {
            throw ActivityConflict(
                "NEW_PLAYER_ACTIVITY_ACTIVE_SEASON_REQUIRED",
                "An activity can be published only into the active weekly season.");
        }
        var worldId = NormalizeBindingWorldId(season.WorldId ?? string.Empty);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            RequireSeason(season.SeasonId);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var activity = await RequireNewPlayerActivityAsync(
                connection,
                (SqliteTransaction)transaction,
                normalizedKey,
                version,
                cancellationToken);
            if (activity.State == NewPlayerActivityState.Published &&
                activity.PublishedSeasonId == season.SeasonId &&
                string.Equals(activity.PublishedWorldId, worldId, StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(cancellationToken);
                return activity;
            }
            if (activity.State != NewPlayerActivityState.Draft)
            {
                throw ActivityConflict(
                    "NEW_PLAYER_ACTIVITY_IMMUTABLE",
                    "Only a draft version can be published.");
            }
            if (await HasPublishedNewPlayerActivityVersionAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    normalizedKey,
                    activity.ActivityId,
                    cancellationToken))
            {
                throw ActivityConflict(
                    "NEW_PLAYER_ACTIVITY_VERSION_STILL_PUBLISHED",
                    "Close the currently published version before publishing another version.");
            }

            var now = UtcNow();
            var published = activity with
            {
                State = NewPlayerActivityState.Published,
                PublishedSeasonId = season.SeasonId,
                PublishedWorldId = worldId,
                PublishedBy = normalizedActor,
                PublishedAt = now,
                Revision = checked(activity.Revision + 1)
            };
            await UpdateNewPlayerActivityRowAsync(
                connection,
                (SqliteTransaction)transaction,
                published,
                cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
            EnsureAuthoritativeMarker();
            return published;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NewPlayerActivity> CloseNewPlayerActivityAsync(
        string activityKey,
        int version,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeNewPlayerActivityKey(activityKey);
        ValidateNewPlayerActivityVersion(version);
        var normalizedActor = NormalizeRequired(actor, 256, nameof(actor));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var activity = await RequireNewPlayerActivityAsync(
                connection,
                (SqliteTransaction)transaction,
                normalizedKey,
                version,
                cancellationToken);
            if (activity.State == NewPlayerActivityState.Closed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return activity;
            }
            if (activity.State != NewPlayerActivityState.Published)
            {
                throw ActivityConflict(
                    "NEW_PLAYER_ACTIVITY_NOT_PUBLISHED",
                    "Only a published activity version can be closed.");
            }

            var closed = activity with
            {
                State = NewPlayerActivityState.Closed,
                ClosedBy = normalizedActor,
                ClosedAt = UtcNow(),
                Revision = checked(activity.Revision + 1)
            };
            await UpdateNewPlayerActivityRowAsync(
                connection,
                (SqliteTransaction)transaction,
                closed,
                cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
            EnsureAuthoritativeMarker();
            return closed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<NewPlayerActivityAvailability>>
        ListAvailableNewPlayerActivitiesAsync(
            Guid accountId,
            Guid seasonId,
            string worldId,
            CancellationToken cancellationToken)
    {
        var normalizedWorldId = NormalizeBindingWorldId(worldId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            RequireAccount(accountId);
            RequireSeason(seasonId);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {NewPlayerActivityColumns}
                FROM new_player_activities
                WHERE state = 'published'
                  AND published_season_id = $seasonId
                  AND published_world_id = $worldId COLLATE NOCASE
                ORDER BY activity_key COLLATE NOCASE, version DESC;
                """;
            command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
            command.Parameters.AddWithValue("$worldId", normalizedWorldId);
            var activities = new List<NewPlayerActivity>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    activities.Add(ReadNewPlayerActivity(reader));
                }
            }

            var results = new List<NewPlayerActivityAvailability>(activities.Count);
            foreach (var activity in activities)
            {
                var grant = await ReadNewPlayerActivityGrantByActivityAsync(
                    connection,
                    transaction: null,
                    accountId,
                    activity.ActivityId,
                    cancellationToken);
                results.Add(new NewPlayerActivityAvailability(activity, grant));
            }
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NewPlayerActivityClaimResult> ClaimNewPlayerActivityAsync(
        NewPlayerActivityClaimRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = NormalizeNewPlayerActivityClaimRequest(request);
        var requestHash = HashNewPlayerActivityClaim(normalized);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_accounts.TryGetValue(normalized.AccountId, out var account) ||
                !string.Equals(
                    account.ExternalUserId,
                    normalized.PlatformSubject,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ActivityClaimFailure(
                    "NEW_PLAYER_ACTIVITY_ACCOUNT_MISMATCH",
                    "The authenticated platform subject does not own this economy account.");
            }
            if (!_seasons.TryGetValue(normalized.SeasonId, out var season) ||
                season.State != ExtractionSeasonState.Active ||
                !string.Equals(
                    NormalizeBindingWorldId(season.WorldId ?? string.Empty),
                    normalized.WorldId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ActivityClaimFailure(
                    "NEW_PLAYER_ACTIVITY_WORLD_MISMATCH",
                    "The claim is not bound to the active weekly world.");
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (!await HasExactNewPlayerActivityBindingAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    normalized,
                    cancellationToken))
            {
                return await RollbackClaimFailureAsync(
                    transaction,
                    "NEW_PLAYER_ACTIVITY_IDENTITY_BINDING_REQUIRED",
                    "The claim requires the authenticated account's exact current-world PlayerUID binding.",
                    cancellationToken);
            }

            var idempotent = await ReadNewPlayerActivityGrantByIdempotencyAsync(
                connection,
                (SqliteTransaction)transaction,
                normalized.AccountId,
                normalized.IdempotencyKey,
                cancellationToken);
            if (idempotent is not null)
            {
                if (!string.Equals(idempotent.Value.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return ActivityClaimFailure(
                        "IDEMPOTENCY_CONFLICT",
                        "The idempotency key was already used for a different new-player activity claim.",
                        idempotencyConflict: true);
                }
                var replayActivity = await RequireNewPlayerActivityByIdAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    idempotent.Value.Grant.ActivityId,
                    cancellationToken);
                await transaction.RollbackAsync(cancellationToken);
                return new NewPlayerActivityClaimResult(
                    replayActivity,
                    idempotent.Value.Grant,
                    CreateWalletSnapshot(normalized.AccountId, normalized.SeasonId),
                    Created: false,
                    IdempotentReplay: true,
                    IdempotencyConflict: false,
                    ErrorCode: null,
                    ErrorMessage: null);
            }

            var activity = await ReadNewPlayerActivityAsync(
                connection,
                (SqliteTransaction)transaction,
                normalized.ActivityKey,
                normalized.ActivityVersion,
                cancellationToken);
            if (activity is null)
            {
                return await RollbackClaimFailureAsync(
                    transaction,
                    "NEW_PLAYER_ACTIVITY_NOT_FOUND",
                    "The requested new-player activity version does not exist.",
                    cancellationToken);
            }

            var existingGrant = await ReadNewPlayerActivityGrantByActivityAsync(
                connection,
                (SqliteTransaction)transaction,
                normalized.AccountId,
                activity.ActivityId,
                cancellationToken);
            if (existingGrant is not null)
            {
                return await RollbackClaimFailureAsync(
                    transaction,
                    "NEW_PLAYER_ACTIVITY_ALREADY_CLAIMED",
                    "This account already claimed the requested activity version; replay it with the original idempotency key.",
                    cancellationToken);
            }

            if (activity.State != NewPlayerActivityState.Published ||
                activity.PublishedSeasonId != normalized.SeasonId ||
                !string.Equals(
                    activity.PublishedWorldId,
                    normalized.WorldId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return await RollbackClaimFailureAsync(
                    transaction,
                    "NEW_PLAYER_ACTIVITY_NOT_AVAILABLE",
                    "The requested activity version is not published for this weekly world.",
                    cancellationToken);
            }

            var marketBefore = GetBalance(
                normalized.AccountId,
                ExtractionCurrency.MarketCoin,
                seasonId: null);
            var voucherBefore = GetBalance(
                normalized.AccountId,
                ExtractionCurrency.SeasonVoucher,
                normalized.SeasonId);
            long marketAfter;
            long voucherAfter;
            try
            {
                marketAfter = checked(marketBefore.Balance + activity.MarketCoin);
                voucherAfter = checked(voucherBefore.Balance + activity.SeasonVoucher);
            }
            catch (OverflowException)
            {
                return await RollbackClaimFailureAsync(
                    transaction,
                    "NEW_PLAYER_ACTIVITY_BALANCE_OVERFLOW",
                    "The activity grant exceeds the supported wallet range.",
                    cancellationToken);
            }
            if (marketAfter > MaximumWebSafeInteger || voucherAfter > MaximumWebSafeInteger)
            {
                return await RollbackClaimFailureAsync(
                    transaction,
                    "NEW_PLAYER_ACTIVITY_BALANCE_OUT_OF_RANGE",
                    "The activity grant exceeds the exact integer range supported by the web portal.",
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var now = UtcNow();
            var marketBalance = marketBefore with
            {
                Balance = marketAfter,
                Revision = checked(marketBefore.Revision + 1),
                UpdatedAt = now
            };
            var voucherBalance = voucherBefore with
            {
                Balance = voucherAfter,
                Revision = checked(voucherBefore.Revision + 1),
                UpdatedAt = now
            };
            var grantId = Guid.NewGuid();
            var referenceId = activity.ActivityId.ToString("N");
            var reason = $"New-player activity {activity.ActivityKey} v{activity.Version}";
            var marketLedger = new WalletLedgerEntry(
                Guid.NewGuid(),
                normalized.AccountId,
                ExtractionCurrency.MarketCoin,
                null,
                activity.MarketCoin,
                marketAfter,
                reason,
                "new_player_activity",
                referenceId,
                normalized.Actor,
                now);
            var voucherLedger = new WalletLedgerEntry(
                Guid.NewGuid(),
                normalized.AccountId,
                ExtractionCurrency.SeasonVoucher,
                normalized.SeasonId,
                activity.SeasonVoucher,
                voucherAfter,
                reason,
                "new_player_activity",
                referenceId,
                normalized.Actor,
                now);
            var storeEvent = NewEvent(
                "new_player_activity.claimed",
                now,
                balances: [marketBalance, voucherBalance],
                ledgerEntries: [marketLedger, voucherLedger]);
            var grant = new NewPlayerActivityGrant(
                grantId,
                activity.ActivityId,
                activity.ActivityKey,
                activity.Version,
                normalized.AccountId,
                normalized.SeasonId,
                normalized.WorldId,
                normalized.PlayerUid,
                normalized.PlatformSubject,
                activity.MarketCoin,
                activity.SeasonVoucher,
                marketLedger.EntryId,
                voucherLedger.EntryId,
                marketAfter,
                voucherAfter,
                now);

            var payload = JsonSerializer.Serialize(storeEvent, JsonOptions);
            try
            {
                await using (var eventCommand = CreateInsertCommand(
                                 connection,
                                 transaction,
                                 storeEvent,
                                 payload))
                {
                    await eventCommand.ExecuteNonQueryAsync(CancellationToken.None);
                }
                await InsertNewPlayerActivityGrantAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    grant,
                    normalized.IdempotencyKey,
                    requestHash,
                    CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
                EnsureAuthoritativeMarker();
            }
            catch
            {
                _isReady = false;
                throw;
            }
            ApplyEvent(storeEvent);
            return new NewPlayerActivityClaimResult(
                activity,
                grant,
                CreateWalletSnapshot(normalized.AccountId, normalized.SeasonId),
                Created: true,
                IdempotentReplay: false,
                IdempotencyConflict: false,
                ErrorCode: null,
                ErrorMessage: null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void InitializeNewPlayerActivityDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS new_player_activities (
                activity_id TEXT PRIMARY KEY,
                activity_key TEXT NOT NULL COLLATE NOCASE,
                version INTEGER NOT NULL CHECK (version >= 1),
                state TEXT NOT NULL CHECK (state IN ('draft', 'published', 'closed')),
                title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 128),
                description TEXT NOT NULL CHECK (length(description) BETWEEN 1 AND 1024),
                market_coin INTEGER NOT NULL CHECK (market_coin > 0),
                season_voucher INTEGER NOT NULL CHECK (season_voucher > 0),
                created_by TEXT NOT NULL,
                created_at TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK (revision >= 0),
                published_season_id TEXT,
                published_world_id TEXT COLLATE NOCASE,
                published_by TEXT,
                published_at TEXT,
                closed_by TEXT,
                closed_at TEXT,
                UNIQUE (activity_key, version),
                CHECK (
                    (state = 'draft' AND published_season_id IS NULL AND
                     published_world_id IS NULL AND published_by IS NULL AND
                     published_at IS NULL AND closed_by IS NULL AND closed_at IS NULL)
                    OR
                    (state = 'published' AND published_season_id IS NOT NULL AND
                     published_world_id IS NOT NULL AND length(published_world_id) = 32 AND
                     published_by IS NOT NULL AND published_at IS NOT NULL AND
                     closed_by IS NULL AND closed_at IS NULL)
                    OR
                    (state = 'closed' AND published_season_id IS NOT NULL AND
                     published_world_id IS NOT NULL AND length(published_world_id) = 32 AND
                     published_by IS NOT NULL AND published_at IS NOT NULL AND
                     closed_by IS NOT NULL AND closed_at IS NOT NULL)
                )
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_new_player_activities_one_published
                ON new_player_activities (activity_key COLLATE NOCASE)
                WHERE state = 'published';
            CREATE TRIGGER IF NOT EXISTS trg_new_player_activities_draft_insert_only
            BEFORE INSERT ON new_player_activities
            WHEN NEW.state <> 'draft'
            BEGIN
                SELECT RAISE(ABORT, 'new-player activity versions must be created as drafts');
            END;
            CREATE TRIGGER IF NOT EXISTS trg_new_player_activities_immutable_after_publish
            BEFORE UPDATE ON new_player_activities
            WHEN OLD.state = 'closed' OR
                 (OLD.state = 'published' AND NOT (
                    NEW.state = 'closed' AND
                    NEW.activity_id = OLD.activity_id AND
                    NEW.activity_key = OLD.activity_key AND
                    NEW.version = OLD.version AND
                    NEW.title = OLD.title AND
                    NEW.description = OLD.description AND
                    NEW.market_coin = OLD.market_coin AND
                    NEW.season_voucher = OLD.season_voucher AND
                    NEW.created_by = OLD.created_by AND
                    NEW.created_at = OLD.created_at AND
                    NEW.published_season_id = OLD.published_season_id AND
                    NEW.published_world_id = OLD.published_world_id AND
                    NEW.published_by = OLD.published_by AND
                    NEW.published_at = OLD.published_at AND
                    NEW.closed_by IS NOT NULL AND
                    NEW.closed_at IS NOT NULL
                 ))
            BEGIN
                SELECT RAISE(ABORT, 'published new-player activity versions are immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS trg_new_player_activities_no_published_delete
            BEFORE DELETE ON new_player_activities
            WHEN OLD.state <> 'draft'
            BEGIN
                SELECT RAISE(ABORT, 'published new-player activity versions cannot be deleted');
            END;
            CREATE TABLE IF NOT EXISTS new_player_activity_grants (
                grant_id TEXT PRIMARY KEY,
                activity_id TEXT NOT NULL,
                activity_key TEXT NOT NULL COLLATE NOCASE,
                activity_version INTEGER NOT NULL CHECK (activity_version >= 1),
                account_id TEXT NOT NULL,
                season_id TEXT NOT NULL,
                world_id TEXT NOT NULL COLLATE NOCASE CHECK (length(world_id) = 32),
                player_uid TEXT NOT NULL COLLATE NOCASE CHECK (length(player_uid) = 32),
                platform_subject TEXT NOT NULL COLLATE NOCASE,
                idempotency_key TEXT NOT NULL,
                request_hash TEXT NOT NULL CHECK (length(request_hash) = 64),
                market_coin INTEGER NOT NULL CHECK (market_coin > 0),
                season_voucher INTEGER NOT NULL CHECK (season_voucher > 0),
                market_coin_ledger_entry_id TEXT NOT NULL UNIQUE,
                season_voucher_ledger_entry_id TEXT NOT NULL UNIQUE,
                market_coin_balance_after INTEGER NOT NULL CHECK (market_coin_balance_after >= 0),
                season_voucher_balance_after INTEGER NOT NULL CHECK (season_voucher_balance_after >= 0),
                claimed_at TEXT NOT NULL,
                UNIQUE (account_id, activity_id),
                UNIQUE (account_id, activity_key, activity_version),
                UNIQUE (account_id, idempotency_key)
            );
            CREATE INDEX IF NOT EXISTS ix_new_player_activity_grants_activity
                ON new_player_activity_grants (activity_id, claimed_at);
            CREATE INDEX IF NOT EXISTS ix_new_player_activity_grants_identity
                ON new_player_activity_grants (season_id, world_id, player_uid);
            CREATE TRIGGER IF NOT EXISTS trg_new_player_activity_grants_scope
            BEFORE INSERT ON new_player_activity_grants
            WHEN NOT EXISTS (
                SELECT 1
                FROM new_player_activities activity
                WHERE activity.activity_id = NEW.activity_id
                  AND activity.activity_key = NEW.activity_key COLLATE NOCASE
                  AND activity.version = NEW.activity_version
                  AND activity.state = 'published'
                  AND activity.published_season_id = NEW.season_id
                  AND activity.published_world_id = NEW.world_id COLLATE NOCASE)
            BEGIN
                SELECT RAISE(ABORT, 'new-player activity grant scope is invalid');
            END;
            CREATE TRIGGER IF NOT EXISTS trg_new_player_activity_grants_no_update
            BEFORE UPDATE ON new_player_activity_grants
            BEGIN
                SELECT RAISE(ABORT, 'new-player activity grants are immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS trg_new_player_activity_grants_no_delete
            BEFORE DELETE ON new_player_activity_grants
            BEGIN
                SELECT RAISE(ABORT, 'new-player activity grants cannot be deleted');
            END;
            """;
        command.ExecuteNonQuery();
    }

    private bool HasPersistedNewPlayerActivities()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM new_player_activities LIMIT 1);";
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private void ValidateNewPlayerActivityDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var scopeCommand = connection.CreateCommand())
        {
            scopeCommand.CommandText = """
                SELECT COUNT(*)
                FROM new_player_activity_grants grant_row
                LEFT JOIN new_player_activities activity
                  ON activity.activity_id = grant_row.activity_id
                WHERE activity.activity_id IS NULL
                   OR activity.activity_key <> grant_row.activity_key COLLATE NOCASE
                   OR activity.version <> grant_row.activity_version
                   OR activity.state NOT IN ('published', 'closed')
                   OR activity.published_season_id <> grant_row.season_id
                   OR activity.published_world_id <> grant_row.world_id COLLATE NOCASE
                   OR activity.market_coin <> grant_row.market_coin
                   OR activity.season_voucher <> grant_row.season_voucher;
                """;
            if (Convert.ToInt64(scopeCommand.ExecuteScalar()) != 0)
            {
                throw new InvalidDataException(
                    "A new-player activity grant does not match its immutable published version.");
            }
        }

        using var grantCommand = connection.CreateCommand();
        grantCommand.CommandText = $"""
            SELECT {NewPlayerActivityGrantColumns}
            FROM new_player_activity_grants
            ORDER BY claimed_at, grant_id;
            """;
        using var reader = grantCommand.ExecuteReader();
        while (reader.Read())
        {
            var grant = ReadNewPlayerActivityGrant(reader);
            if (grant.MarketCoinLedgerEntryId is not Guid marketLedgerId ||
                grant.SeasonVoucherLedgerEntryId is not Guid voucherLedgerId ||
                !_ledger.TryGetValue(marketLedgerId, out var marketLedger) ||
                !_ledger.TryGetValue(voucherLedgerId, out var voucherLedger) ||
                marketLedger.AccountId != grant.AccountId ||
                marketLedger.Currency != ExtractionCurrency.MarketCoin ||
                marketLedger.SeasonId is not null ||
                marketLedger.Delta != grant.MarketCoin ||
                marketLedger.BalanceAfter != grant.MarketCoinBalanceAfter ||
                voucherLedger.AccountId != grant.AccountId ||
                voucherLedger.Currency != ExtractionCurrency.SeasonVoucher ||
                voucherLedger.SeasonId != grant.SeasonId ||
                voucherLedger.Delta != grant.SeasonVoucher ||
                voucherLedger.BalanceAfter != grant.SeasonVoucherBalanceAfter ||
                !string.Equals(
                    marketLedger.ReferenceType,
                    "new_player_activity",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    voucherLedger.ReferenceType,
                    "new_player_activity",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    marketLedger.ReferenceId,
                    grant.ActivityId.ToString("N"),
                    StringComparison.Ordinal) ||
                !string.Equals(
                    voucherLedger.ReferenceId,
                    grant.ActivityId.ToString("N"),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"New-player activity grant '{grant.GrantId}' has inconsistent dual-wallet ledger evidence.");
            }
        }
    }

    private static async Task InsertNewPlayerActivityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NewPlayerActivity activity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO new_player_activities (
                activity_id, activity_key, version, state, title, description,
                market_coin, season_voucher, created_by, created_at, revision,
                published_season_id, published_world_id, published_by, published_at,
                closed_by, closed_at)
            VALUES (
                $activityId, $activityKey, $version, $state, $title, $description,
                $marketCoin, $seasonVoucher, $createdBy, $createdAt, $revision,
                NULL, NULL, NULL, NULL, NULL, NULL);
            """;
        AddNewPlayerActivityParameters(command, activity);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateNewPlayerActivityRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NewPlayerActivity activity,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE new_player_activities
            SET state = $state,
                title = $title,
                description = $description,
                market_coin = $marketCoin,
                season_voucher = $seasonVoucher,
                revision = $revision,
                published_season_id = $publishedSeasonId,
                published_world_id = $publishedWorldId,
                published_by = $publishedBy,
                published_at = $publishedAt,
                closed_by = $closedBy,
                closed_at = $closedAt
            WHERE activity_id = $activityId;
            """;
        AddNewPlayerActivityParameters(command, activity);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException("The new-player activity update did not affect exactly one row.");
        }
    }

    private static void AddNewPlayerActivityParameters(
        SqliteCommand command,
        NewPlayerActivity activity)
    {
        command.Parameters.AddWithValue("$activityId", activity.ActivityId.ToString("D"));
        command.Parameters.AddWithValue("$activityKey", activity.ActivityKey);
        command.Parameters.AddWithValue("$version", activity.Version);
        command.Parameters.AddWithValue("$state", NewPlayerActivityStateToStorage(activity.State));
        command.Parameters.AddWithValue("$title", activity.Title);
        command.Parameters.AddWithValue("$description", activity.Description);
        command.Parameters.AddWithValue("$marketCoin", activity.MarketCoin);
        command.Parameters.AddWithValue("$seasonVoucher", activity.SeasonVoucher);
        command.Parameters.AddWithValue("$createdBy", activity.CreatedBy);
        command.Parameters.AddWithValue("$createdAt", activity.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$revision", activity.Revision);
        command.Parameters.AddWithValue(
            "$publishedSeasonId",
            activity.PublishedSeasonId is Guid seasonId ? seasonId.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue(
            "$publishedWorldId",
            activity.PublishedWorldId is null ? DBNull.Value : activity.PublishedWorldId);
        command.Parameters.AddWithValue(
            "$publishedBy",
            activity.PublishedBy is null ? DBNull.Value : activity.PublishedBy);
        command.Parameters.AddWithValue(
            "$publishedAt",
            activity.PublishedAt is DateTimeOffset publishedAt
                ? publishedAt.ToString("O")
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$closedBy",
            activity.ClosedBy is null ? DBNull.Value : activity.ClosedBy);
        command.Parameters.AddWithValue(
            "$closedAt",
            activity.ClosedAt is DateTimeOffset closedAt ? closedAt.ToString("O") : DBNull.Value);
    }

    private static async Task<bool> HasPublishedNewPlayerActivityVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string activityKey,
        Guid excludedActivityId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM new_player_activities
                WHERE activity_key = $activityKey COLLATE NOCASE
                  AND state = 'published'
                  AND activity_id <> $excludedActivityId);
            """;
        command.Parameters.AddWithValue("$activityKey", activityKey);
        command.Parameters.AddWithValue("$excludedActivityId", excludedActivityId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<NewPlayerActivity?> ReadNewPlayerActivityAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string activityKey,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {NewPlayerActivityColumns}
            FROM new_player_activities
            WHERE activity_key = $activityKey COLLATE NOCASE AND version = $version;
            """;
        command.Parameters.AddWithValue("$activityKey", activityKey);
        command.Parameters.AddWithValue("$version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadNewPlayerActivity(reader)
            : null;
    }

    private static async Task<NewPlayerActivity> RequireNewPlayerActivityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string activityKey,
        int version,
        CancellationToken cancellationToken) =>
        await ReadNewPlayerActivityAsync(
            connection,
            transaction,
            activityKey,
            version,
            cancellationToken)
        ?? throw new NewPlayerActivityException(
            "NEW_PLAYER_ACTIVITY_NOT_FOUND",
            "The requested new-player activity version does not exist.",
            StatusCodes.Status404NotFound);

    private static async Task<NewPlayerActivity> RequireNewPlayerActivityByIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid activityId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {NewPlayerActivityColumns}
            FROM new_player_activities
            WHERE activity_id = $activityId;
            """;
        command.Parameters.AddWithValue("$activityId", activityId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadNewPlayerActivity(reader)
            : throw new InvalidDataException("A persisted new-player activity grant references a missing activity.");
    }

    private static NewPlayerActivity ReadNewPlayerActivity(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetInt32(2),
        NewPlayerActivityStateFromStorage(reader.GetString(3)),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetInt64(6),
        reader.GetInt64(7),
        reader.GetString(8),
        DateTimeOffset.Parse(reader.GetString(9)),
        reader.GetInt64(10),
        reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14)),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.IsDBNull(16) ? null : DateTimeOffset.Parse(reader.GetString(16)));

    private static async Task<bool> HasExactNewPlayerActivityBindingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NewPlayerActivityClaimRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM player_identity_bindings
                WHERE account_id = $accountId
                  AND season_id = $seasonId
                  AND world_id = $worldId COLLATE NOCASE
                  AND player_uid = $playerUid COLLATE NOCASE
                  AND platform_subject = $platformSubject COLLATE NOCASE);
            """;
        command.Parameters.AddWithValue("$accountId", request.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$seasonId", request.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$worldId", request.WorldId);
        command.Parameters.AddWithValue("$playerUid", request.PlayerUid);
        command.Parameters.AddWithValue("$platformSubject", request.PlatformSubject);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task InsertNewPlayerActivityGrantAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NewPlayerActivityGrant grant,
        string idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO new_player_activity_grants (
                grant_id, activity_id, activity_key, activity_version, account_id,
                season_id, world_id, player_uid, platform_subject, idempotency_key,
                request_hash, market_coin, season_voucher,
                market_coin_ledger_entry_id, season_voucher_ledger_entry_id,
                market_coin_balance_after, season_voucher_balance_after, claimed_at)
            VALUES (
                $grantId, $activityId, $activityKey, $activityVersion, $accountId,
                $seasonId, $worldId, $playerUid, $platformSubject, $idempotencyKey,
                $requestHash, $marketCoin, $seasonVoucher,
                $marketCoinLedgerEntryId, $seasonVoucherLedgerEntryId,
                $marketCoinBalanceAfter, $seasonVoucherBalanceAfter, $claimedAt);
            """;
        command.Parameters.AddWithValue("$grantId", grant.GrantId.ToString("D"));
        command.Parameters.AddWithValue("$activityId", grant.ActivityId.ToString("D"));
        command.Parameters.AddWithValue("$activityKey", grant.ActivityKey);
        command.Parameters.AddWithValue("$activityVersion", grant.ActivityVersion);
        command.Parameters.AddWithValue("$accountId", grant.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$seasonId", grant.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$worldId", grant.WorldId);
        command.Parameters.AddWithValue("$playerUid", grant.PlayerUid);
        command.Parameters.AddWithValue("$platformSubject", grant.PlatformSubject);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        command.Parameters.AddWithValue("$requestHash", requestHash);
        command.Parameters.AddWithValue("$marketCoin", grant.MarketCoin);
        command.Parameters.AddWithValue("$seasonVoucher", grant.SeasonVoucher);
        command.Parameters.AddWithValue(
            "$marketCoinLedgerEntryId",
            grant.MarketCoinLedgerEntryId?.ToString("D") ?? throw new InvalidDataException());
        command.Parameters.AddWithValue(
            "$seasonVoucherLedgerEntryId",
            grant.SeasonVoucherLedgerEntryId?.ToString("D") ?? throw new InvalidDataException());
        command.Parameters.AddWithValue("$marketCoinBalanceAfter", grant.MarketCoinBalanceAfter);
        command.Parameters.AddWithValue("$seasonVoucherBalanceAfter", grant.SeasonVoucherBalanceAfter);
        command.Parameters.AddWithValue("$claimedAt", grant.ClaimedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(NewPlayerActivityGrant Grant, string RequestHash)?>
        ReadNewPlayerActivityGrantByIdempotencyAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            Guid accountId,
            string idempotencyKey,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {NewPlayerActivityGrantColumns}, request_hash
            FROM new_player_activity_grants
            WHERE account_id = $accountId AND idempotency_key = $idempotencyKey;
            """;
        command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (ReadNewPlayerActivityGrant(reader), reader.GetString(16))
            : null;
    }

    private static async Task<NewPlayerActivityGrant?> ReadNewPlayerActivityGrantByActivityAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid accountId,
        Guid activityId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {NewPlayerActivityGrantColumns}
            FROM new_player_activity_grants
            WHERE account_id = $accountId AND activity_id = $activityId;
            """;
        command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
        command.Parameters.AddWithValue("$activityId", activityId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadNewPlayerActivityGrant(reader)
            : null;
    }

    private static NewPlayerActivityGrant ReadNewPlayerActivityGrant(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.GetInt32(3),
        Guid.Parse(reader.GetString(4)),
        Guid.Parse(reader.GetString(5)),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetInt64(9),
        reader.GetInt64(10),
        reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)),
        reader.IsDBNull(12) ? null : Guid.Parse(reader.GetString(12)),
        reader.GetInt64(13),
        reader.GetInt64(14),
        DateTimeOffset.Parse(reader.GetString(15)));

    private static NewPlayerActivityDefinition NormalizeNewPlayerActivityDefinition(
        NewPlayerActivityDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.MarketCoin is <= 0 or > MaximumWebSafeInteger ||
            definition.SeasonVoucher is <= 0 or > MaximumWebSafeInteger)
        {
            throw new ArgumentException(
                "Both wallet grants must be positive exact web integers.",
                nameof(definition));
        }
        return definition with
        {
            Title = NormalizeRequired(definition.Title, 128, nameof(definition.Title)),
            Description = NormalizeRequired(
                definition.Description,
                1024,
                nameof(definition.Description))
        };
    }

    private static NewPlayerActivityClaimRequest NormalizeNewPlayerActivityClaimRequest(
        NewPlayerActivityClaimRequest request)
    {
        if (request.AccountId == Guid.Empty || request.SeasonId == Guid.Empty)
        {
            throw new ArgumentException("Activity claims require non-empty account and season ids.");
        }
        ValidateNewPlayerActivityVersion(request.ActivityVersion);
        return request with
        {
            ActivityKey = NormalizeNewPlayerActivityKey(request.ActivityKey),
            WorldId = NormalizeBindingWorldId(request.WorldId),
            PlayerUid = NormalizePlayerUid(request.PlayerUid),
            PlatformSubject = NormalizePlatformSubject(request.PlatformSubject),
            IdempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey),
            Actor = NormalizeRequired(request.Actor, 256, nameof(request.Actor))
        };
    }

    private static string NormalizeNewPlayerActivityKey(string value)
    {
        var key = NormalizeRequired(value, 64, nameof(value)).ToUpperInvariant();
        if (key.Length < 3 || key.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "Activity key must contain 3 to 64 ASCII letters, digits, '-', '_' or '.'.",
                nameof(value));
        }
        return key;
    }

    private static void ValidateNewPlayerActivityVersion(int version)
    {
        if (version is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }
    }

    private static string HashNewPlayerActivityClaim(NewPlayerActivityClaimRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("operation", "new-player-activity.claim");
        writer.WriteString("activityKey", request.ActivityKey);
        writer.WriteNumber("activityVersion", request.ActivityVersion);
        writer.WriteString("accountId", request.AccountId);
        writer.WriteString("seasonId", request.SeasonId);
        writer.WriteString("worldId", request.WorldId);
        writer.WriteString("playerUid", request.PlayerUid);
        writer.WriteString("platformSubject", request.PlatformSubject);
        writer.WriteEndObject();
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static string NewPlayerActivityStateToStorage(NewPlayerActivityState state) => state switch
    {
        NewPlayerActivityState.Draft => "draft",
        NewPlayerActivityState.Published => "published",
        NewPlayerActivityState.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static NewPlayerActivityState NewPlayerActivityStateFromStorage(string state) => state switch
    {
        "draft" => NewPlayerActivityState.Draft,
        "published" => NewPlayerActivityState.Published,
        "closed" => NewPlayerActivityState.Closed,
        _ => throw new InvalidDataException($"Unknown new-player activity state '{state}'.")
    };

    private static NewPlayerActivityException ActivityConflict(string code, string message) =>
        new(code, message, StatusCodes.Status409Conflict);

    private static NewPlayerActivityClaimResult ActivityClaimFailure(
        string code,
        string message,
        bool idempotencyConflict = false) =>
        new(
            Activity: null,
            Grant: null,
            Wallet: null,
            Created: false,
            IdempotentReplay: false,
            IdempotencyConflict: idempotencyConflict,
            ErrorCode: code,
            ErrorMessage: message);

    private static async Task<NewPlayerActivityClaimResult> RollbackClaimFailureAsync(
        System.Data.Common.DbTransaction transaction,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);
        return ActivityClaimFailure(code, message);
    }
}
