using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Content;
using PalControl.ControlApi.Infrastructure;

namespace PalControl.ControlApi.Extraction;

public sealed partial class SqliteExtractionRepository :
    IExtractionRepository,
    IExtractionSettlementPersistence,
    IDisposable,
    IAsyncDisposable
{
    private const int StoreSchemaVersion = 1;
    private const long MaximumWebSafeInteger = 9_007_199_254_740_991;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, ExtractionSeason> _seasons = [];
    private readonly Dictionary<Guid, ExtractionAccount> _accounts = [];
    private readonly Dictionary<string, Guid> _accountIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WalletBalance> _balances = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, WalletLedgerEntry> _ledger = [];
    private readonly Dictionary<string, ShopProduct> _products = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ShopOrder> _orders = [];
    private readonly Dictionary<Guid, ShopDelivery> _deliveries = [];
    private readonly Dictionary<string, StoredIdempotency> _idempotency = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _eventIds = [];
    private readonly TimeProvider _timeProvider;
    private readonly IContentProductProjectionFaultInjector? _contentProjectionFaultInjector;
    private readonly string _databasePath;
    private readonly string _legacyEventPath;
    private readonly string _authoritativeMarkerPath;
    private readonly string _connectionString;
    private readonly FileStream _instanceLock;
    private volatile bool _isReady;
    private bool _disposed;

    public SqliteExtractionRepository(
        string dataDirectory,
        TimeProvider? timeProvider = null,
        IContentProductProjectionFaultInjector? contentProjectionFaultInjector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _contentProjectionFaultInjector = contentProjectionFaultInjector;
        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(fullDataDirectory);
        _databasePath = Path.Combine(fullDataDirectory, "extraction-commerce.db");
        _legacyEventPath = Path.Combine(fullDataDirectory, "extraction-commerce-events.jsonl");
        _authoritativeMarkerPath = Path.Combine(
            fullDataDirectory,
            "extraction-commerce.sqlite-authoritative.json");
        if (File.Exists(_authoritativeMarkerPath) && !File.Exists(_databasePath))
        {
            throw new InvalidDataException(
                "The authoritative extraction SQLite database is missing; refusing to recreate it from legacy JSONL.");
        }
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _instanceLock = new FileStream(
            Path.Combine(fullDataDirectory, "extraction-commerce.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.WriteThrough);

        InitializeDatabase();
        InitializeNewPlayerActivityDatabase();
        InitializeEconomyObservabilityDatabase();
        MigrateLegacyEventsIfNeeded();
        LoadEvents();
        ValidateNewPlayerActivityDatabase();
        if (File.Exists(_authoritativeMarkerPath) &&
            _eventIds.Count == 0 &&
            !HasPersistedSettlementRuns() &&
            !HasPersistedNewPlayerActivities())
        {
            throw new InvalidDataException(
                "The authoritative extraction SQLite database contains no events; refusing legacy rollback.");
        }
        if (_eventIds.Count > 0)
        {
            EnsureAuthoritativeMarker();
        }
        BackfillExtractionRunCredits();
        EnsureWritable();
        _isReady = true;
    }

    private bool HasPersistedSettlementRuns()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM extraction_settlement_runs LIMIT 1);";
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    public bool IsReady => _isReady && !_disposed;

    public async Task ProbeWriteAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO extraction_events (
                    event_id, event_type, occurred_at, payload)
                VALUES ($eventId, 'economy_write_probe', $occurredAt, '{}');
                """;
            command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$occurredAt", _timeProvider.GetUtcNow().ToString("O"));
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ExtractionSeason>> ListSeasonsAsync(
        string? serverId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _seasons.Values
                .Where(season => string.IsNullOrWhiteSpace(serverId) ||
                    string.Equals(season.ServerId, serverId.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(season => season.StartsAt)
                .Select(CloneSeason)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionSeason?> GetSeasonAsync(
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _seasons.TryGetValue(seasonId, out var season) ? CloneSeason(season) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionSeason> UpsertSeasonAsync(
        Guid? seasonId,
        ExtractionSeasonDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var serverId = NormalizeRequired(definition.ServerId, 64, nameof(definition.ServerId));
        var code = NormalizeRequired(definition.Code, 64, nameof(definition.Code));
        var displayName = NormalizeRequired(definition.DisplayName, 128, nameof(definition.DisplayName));
        var worldId = NormalizeOptional(definition.WorldId, 128, nameof(definition.WorldId));
        if (definition.EndsAt <= definition.StartsAt)
        {
            throw new ArgumentException("Season EndsAt must be later than StartsAt.", nameof(definition));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var id = seasonId ?? Guid.NewGuid();
            _seasons.TryGetValue(id, out var existing);
            VerifyRevision(existing?.Revision, expectedRevision, "season");
            if (_seasons.Values.Any(season =>
                    season.SeasonId != id &&
                    string.Equals(season.ServerId, serverId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(season.Code, code, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Season code '{code}' already exists for server '{serverId}'.");
            }
            if (definition.State == ExtractionSeasonState.Active &&
                _seasons.Values.Any(season =>
                    season.SeasonId != id &&
                    season.State == ExtractionSeasonState.Active &&
                    string.Equals(season.ServerId, serverId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Server '{serverId}' already has an active extraction season.");
            }

            var now = UtcNow();
            var season = new ExtractionSeason(
                id,
                serverId,
                code,
                displayName,
                worldId,
                definition.StartsAt.ToUniversalTime(),
                definition.EndsAt.ToUniversalTime(),
                definition.State,
                checked((existing?.Revision ?? 0) + 1),
                existing?.CreatedAt ?? now,
                now);
            await AppendAndApplyAsync(NewEvent("season.upserted", now, season: season), cancellationToken);
            return CloneSeason(season);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionAccount> GetOrCreateAccountAsync(
        string identityProvider,
        string externalUserId,
        string displayName,
        CancellationToken cancellationToken)
    {
        var provider = NormalizeRequired(identityProvider, 32, nameof(identityProvider)).ToLowerInvariant();
        var userId = NormalizeRequired(externalUserId, 128, nameof(externalUserId));
        var normalizedDisplayName = NormalizeRequired(displayName, 128, nameof(displayName));
        var identityKey = AccountIdentityKey(provider, userId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var now = UtcNow();
            if (_accountIdentities.TryGetValue(identityKey, out var accountId))
            {
                var existing = _accounts[accountId];
                if (string.Equals(existing.DisplayName, normalizedDisplayName, StringComparison.Ordinal))
                {
                    return CloneAccount(existing);
                }

                var updated = existing with
                {
                    DisplayName = normalizedDisplayName,
                    Revision = checked(existing.Revision + 1),
                    UpdatedAt = now
                };
                await AppendAndApplyAsync(NewEvent("account.updated", now, account: updated), cancellationToken);
                return CloneAccount(updated);
            }

            var account = new ExtractionAccount(
                Guid.NewGuid(),
                provider,
                userId,
                normalizedDisplayName,
                1,
                now,
                now);
            await AppendAndApplyAsync(NewEvent("account.created", now, account: account), cancellationToken);
            return CloneAccount(account);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionAccount?> GetAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _accounts.TryGetValue(accountId, out var account) ? CloneAccount(account) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionAccount?> FindAccountAsync(
        string identityProvider,
        string externalUserId,
        CancellationToken cancellationToken)
    {
        var provider = NormalizeRequired(identityProvider, 32, nameof(identityProvider)).ToLowerInvariant();
        var userId = NormalizeRequired(externalUserId, 128, nameof(externalUserId));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _accountIdentities.TryGetValue(AccountIdentityKey(provider, userId), out var accountId)
                ? CloneAccount(_accounts[accountId])
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ExtractionAccount>> ListAccountsAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _accounts.Values
                .OrderBy(account => account.AccountId)
                .Select(CloneAccount)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PlayerIdentityBindingResult> BindOrVerifyPlayerIdentityAsync(
        PlayerIdentityBindingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var subject = NormalizePlatformSubject(request.PlatformSubject);
        var worldId = NormalizeBindingWorldId(request.WorldId);
        var playerUid = NormalizePlayerUid(request.PlayerUid);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var account = RequireAccountValue(request.AccountId);
            var season = RequireSeasonValue(request.SeasonId);
            if (!string.Equals(
                    account.ExternalUserId,
                    subject,
                    StringComparison.OrdinalIgnoreCase))
            {
                await RecordIdentityConflictAsync(
                    "PLAYER_BINDING_SUBJECT_MISMATCH",
                    subject,
                    request.AccountId,
                    worldId,
                    playerUid,
                    cancellationToken);
                return BindingFailure(
                    "PLAYER_BINDING_SUBJECT_MISMATCH",
                    "The platform subject does not own the requested extraction account.");
            }
            if (season.State != ExtractionSeasonState.Active)
            {
                await RecordIdentityConflictAsync(
                    "PLAYER_BINDING_SEASON_NOT_ACTIVE",
                    subject,
                    request.AccountId,
                    worldId,
                    playerUid,
                    cancellationToken);
                return BindingFailure(
                    "PLAYER_BINDING_SEASON_NOT_ACTIVE",
                    "Player identities can only be bound to the active weekly season.");
            }
            if (season.WorldId is null || !string.Equals(
                    NormalizeBindingWorldId(season.WorldId),
                    worldId,
                    StringComparison.Ordinal))
            {
                await RecordIdentityConflictAsync(
                    "PLAYER_BINDING_WORLD_MISMATCH",
                    subject,
                    request.AccountId,
                    worldId,
                    playerUid,
                    cancellationToken);
                return BindingFailure(
                    "PLAYER_BINDING_WORLD_MISMATCH",
                    "The requested world is not the world bound to the active weekly season.");
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var accountBinding = await ReadPlayerIdentityBindingAsync(
                connection,
                transaction,
                "season_id = $seasonId AND account_id = $accountId",
                command =>
                {
                    command.Parameters.AddWithValue("$seasonId", request.SeasonId.ToString("D"));
                    command.Parameters.AddWithValue("$accountId", request.AccountId.ToString("D"));
                },
                cancellationToken);
            if (accountBinding is not null && !BindingMatches(
                    accountBinding,
                    subject,
                    request.SeasonId,
                    worldId,
                    playerUid,
                    request.AccountId))
            {
                await InsertIdentityConflictAsync(
                    connection,
                    transaction,
                    "PLAYER_ACCOUNT_ALREADY_BOUND",
                    subject,
                    request.AccountId,
                    worldId,
                    playerUid,
                    cancellationToken);
                await transaction.CommitAsync(CancellationToken.None);
                return BindingFailure(
                    "PLAYER_ACCOUNT_ALREADY_BOUND",
                    "This account is already bound to another PlayerUID in the current weekly world.");
            }

            var uidBinding = await ReadPlayerIdentityBindingAsync(
                connection,
                transaction,
                "world_id = $worldId AND player_uid = $playerUid",
                command =>
                {
                    command.Parameters.AddWithValue("$worldId", worldId);
                    command.Parameters.AddWithValue("$playerUid", playerUid);
                },
                cancellationToken);
            if (uidBinding is not null && !BindingMatches(
                    uidBinding,
                    subject,
                    request.SeasonId,
                    worldId,
                    playerUid,
                    request.AccountId))
            {
                await InsertIdentityConflictAsync(
                    connection,
                    transaction,
                    "PLAYER_UID_ALREADY_BOUND",
                    subject,
                    request.AccountId,
                    worldId,
                    playerUid,
                    cancellationToken);
                await transaction.CommitAsync(CancellationToken.None);
                return BindingFailure(
                    "PLAYER_UID_ALREADY_BOUND",
                    "This PlayerUID is already bound to another account in the current world.");
            }

            var subjectBinding = await ReadPlayerIdentityBindingAsync(
                connection,
                transaction,
                "season_id = $seasonId AND platform_subject = $platformSubject",
                command =>
                {
                    command.Parameters.AddWithValue("$seasonId", request.SeasonId.ToString("D"));
                    command.Parameters.AddWithValue("$platformSubject", subject);
                },
                cancellationToken);
            if (subjectBinding is not null && !BindingMatches(
                    subjectBinding,
                    subject,
                    request.SeasonId,
                    worldId,
                    playerUid,
                    request.AccountId))
            {
                await InsertIdentityConflictAsync(
                    connection,
                    transaction,
                    "PLAYER_SUBJECT_ALREADY_BOUND",
                    subject,
                    request.AccountId,
                    worldId,
                    playerUid,
                    cancellationToken);
                await transaction.CommitAsync(CancellationToken.None);
                return BindingFailure(
                    "PLAYER_SUBJECT_ALREADY_BOUND",
                    "This platform subject is already bound to another current-world identity.");
            }

            var now = UtcNow();
            var binding = accountBinding ?? uidBinding ?? subjectBinding;
            var created = binding is null;
            if (binding is null)
            {
                binding = new PlayerIdentityBinding(
                    Guid.NewGuid(),
                    subject,
                    request.SeasonId,
                    worldId,
                    playerUid,
                    request.AccountId,
                    now,
                    now);
                await InsertPlayerIdentityBindingAsync(
                    connection,
                    transaction,
                    binding,
                    cancellationToken);
            }
            else
            {
                binding = binding with { LastVerifiedAt = now };
                await UpdatePlayerIdentityBindingVerificationAsync(
                    connection,
                    transaction,
                    binding,
                    cancellationToken);
            }

            await InsertPlayerIdentityBindingHistoryAsync(
                connection,
                transaction,
                binding,
                created ? "bound" : "verified",
                now,
                cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
            EnsureAuthoritativeMarker();
            return new PlayerIdentityBindingResult(
                ClonePlayerIdentityBinding(binding),
                created,
                true,
                null,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PlayerIdentityBinding?> GetPlayerIdentityBindingAsync(
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
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            return await ReadPlayerIdentityBindingAsync(
                connection,
                null,
                "account_id = $accountId AND season_id = $seasonId AND world_id = $worldId",
                command =>
                {
                    command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
                    command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
                    command.Parameters.AddWithValue("$worldId", normalizedWorldId);
                },
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PlayerIdentityBinding?> FindActivePlayerIdentityBindingAsync(
        string serverId,
        string playerIdentifier,
        CancellationToken cancellationToken)
    {
        var normalizedServerId = NormalizeRequired(serverId, 64, nameof(serverId));
        var identifier = NormalizeRequired(playerIdentifier, 128, nameof(playerIdentifier));
        var subject = identifier.ToLowerInvariant();
        var playerUid = TryNormalizePlayerUid(identifier);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            var activeSeason = _seasons.Values.SingleOrDefault(season =>
                season.State == ExtractionSeasonState.Active &&
                string.Equals(
                    season.ServerId,
                    normalizedServerId,
                    StringComparison.OrdinalIgnoreCase));
            if (activeSeason is null)
            {
                return null;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            return await ReadPlayerIdentityBindingAsync(
                connection,
                null,
                "season_id = $seasonId AND (platform_subject = $platformSubject OR ($playerUid IS NOT NULL AND player_uid = $playerUid))",
                command =>
                {
                    command.Parameters.AddWithValue("$seasonId", activeSeason.SeasonId.ToString("D"));
                    command.Parameters.AddWithValue("$platformSubject", subject);
                    command.Parameters.AddWithValue(
                        "$playerUid",
                        playerUid is null ? DBNull.Value : playerUid);
                },
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PlayerIdentityBindingHistoryEntry>>
        ListPlayerIdentityBindingHistoryAsync(
            Guid? accountId,
            Guid? seasonId,
            int limit,
            CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT history_id, binding_id, platform_subject, season_id,
                       world_id, player_uid, account_id, action, occurred_at
                FROM player_identity_binding_history
                WHERE ($accountId IS NULL OR account_id = $accountId)
                  AND ($seasonId IS NULL OR season_id = $seasonId)
                ORDER BY occurred_at DESC, history_id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue(
                "$accountId",
                accountId is null ? DBNull.Value : accountId.Value.ToString("D"));
            command.Parameters.AddWithValue(
                "$seasonId",
                seasonId is null ? DBNull.Value : seasonId.Value.ToString("D"));
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
            var history = new List<PlayerIdentityBindingHistoryEntry>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                history.Add(ReadPlayerIdentityBindingHistory(reader));
            }
            return history;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionWalletSnapshot> GetWalletAsync(
        Guid accountId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            RequireAccount(accountId);
            RequireSeason(seasonId);
            return CreateWalletSnapshot(accountId, seasonId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WalletAdjustmentResult> AdjustWalletAsync(
        WalletAdjustmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = NormalizeWalletAdjustment(request);
        var requestHash = HashWalletAdjustment(normalized);
        var scope = IdempotencyScope("wallet", normalized.AccountId, normalized.IdempotencyKey);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (_idempotency.TryGetValue(scope, out var replay))
            {
                if (!string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    return new WalletAdjustmentResult(
                        null, null, false, true, "IDEMPOTENCY_CONFLICT",
                        "The idempotency key was already used for a different wallet adjustment.");
                }
                var existingEntry = _ledger[replay.ResourceId];
                return new WalletAdjustmentResult(
                    CloneBalance(GetBalance(existingEntry.AccountId, existingEntry.Currency, existingEntry.SeasonId)),
                    existingEntry,
                    false,
                    false,
                    null,
                    null);
            }

            if (!_accounts.ContainsKey(normalized.AccountId))
            {
                return WalletFailure("ACCOUNT_NOT_FOUND", "The extraction account does not exist.");
            }
            if (normalized.Currency == ExtractionCurrency.SeasonVoucher &&
                (normalized.SeasonId is null || !_seasons.ContainsKey(normalized.SeasonId.Value)))
            {
                return WalletFailure("SEASON_NOT_FOUND", "Season vouchers require an existing season.");
            }

            var current = GetBalance(normalized.AccountId, normalized.Currency, normalized.SeasonId);
            long nextBalance;
            try
            {
                nextBalance = checked(current.Balance + normalized.Delta);
            }
            catch (OverflowException)
            {
                return WalletFailure("BALANCE_OVERFLOW", "The wallet adjustment exceeds the supported balance range.");
            }
            if (nextBalance < 0)
            {
                return WalletFailure("INSUFFICIENT_FUNDS", "The wallet balance cannot become negative.");
            }
            if (nextBalance > MaximumWebSafeInteger)
            {
                return WalletFailure(
                    "BALANCE_OUT_OF_WEB_RANGE",
                    "The wallet balance cannot exceed the exact integer range supported by the web console.");
            }

            var now = UtcNow();
            var balance = current with
            {
                Balance = nextBalance,
                Revision = checked(current.Revision + 1),
                UpdatedAt = now
            };
            var ledger = new WalletLedgerEntry(
                Guid.NewGuid(),
                normalized.AccountId,
                normalized.Currency,
                normalized.SeasonId,
                normalized.Delta,
                nextBalance,
                normalized.Reason,
                normalized.ReferenceType,
                normalized.ReferenceId,
                normalized.Actor,
                now);
            var idempotency = new StoredIdempotency(
                scope,
                requestHash,
                "wallet-ledger",
                ledger.EntryId,
                now);
            await AppendAndApplyAsync(
                NewEvent(
                    "wallet.adjusted",
                    now,
                    balances: [balance],
                    ledgerEntries: [ledger],
                    idempotency: idempotency),
                cancellationToken);
            return new WalletAdjustmentResult(CloneBalance(balance), ledger, true, false, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WalletLedgerEntry>> GetLedgerAsync(
        Guid accountId,
        Guid? seasonId,
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            RequireAccount(accountId);
            return _ledger.Values
                .Where(entry => entry.AccountId == accountId &&
                    (seasonId is null || entry.SeasonId is null || entry.SeasonId == seasonId))
                .OrderByDescending(entry => entry.CreatedAt)
                .ThenByDescending(entry => entry.EntryId)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WalletLedgerEntry?> FindLedgerEntryByReferenceAsync(
        Guid accountId,
        ExtractionCurrency currency,
        Guid? seasonId,
        string referenceType,
        string referenceId,
        CancellationToken cancellationToken)
    {
        var normalizedReferenceType = NormalizeRequired(
            referenceType,
            64,
            nameof(referenceType));
        var normalizedReferenceId = NormalizeRequired(
            referenceId,
            128,
            nameof(referenceId));
        var scopedSeasonId = ScopeSeasonId(currency, seasonId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            RequireAccount(accountId);
            var matches = _ledger.Values
                .Where(entry => entry.AccountId == accountId &&
                    entry.Currency == currency &&
                    entry.SeasonId == scopedSeasonId &&
                    string.Equals(
                        entry.ReferenceType,
                        normalizedReferenceType,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        entry.ReferenceId,
                        normalizedReferenceId,
                        StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidDataException(
                    $"More than one wallet ledger entry exists for reference '{normalizedReferenceType}:{normalizedReferenceId}'.");
            }
            return matches.SingleOrDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<ExtractionSettlementRun> LoadAndMigrateSettlementRuns(
        string legacyJsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyJsonPath);
        _gate.Wait();
        try
        {
            EnsureReady();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var persisted = ReadAllSettlementRuns(connection);
            if (persisted.Count > 0 || !File.Exists(legacyJsonPath))
            {
                return persisted;
            }

            var legacy = JsonSerializer.Deserialize<ExtractionSettlementRun[]>(
                    File.ReadAllBytes(legacyJsonPath),
                    JsonOptions)
                ?? throw new InvalidDataException("The legacy extraction run store is invalid.");
            ValidateSettlementRuns(legacy);
            using var transaction = connection.BeginTransaction();
            foreach (var run in legacy.OrderBy(run => run.QuotedAt))
            {
                using var insert = CreateSettlementRunInsertCommand(connection, transaction, run);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            if (legacy.Length > 0)
            {
                EnsureAuthoritativeMarker();
            }
            return legacy.Select(CloneSettlementRun).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PersistSettlementRunWritesAsync(
        IReadOnlyCollection<ExtractionSettlementRunWrite> writes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        ValidateSettlementRunWrites(writes);
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
            foreach (var write in writes.OrderBy(write => write.Run.QuotedAt))
            {
                if (write.Expected is null)
                {
                    await using var insert = CreateSettlementRunInsertCommand(
                        connection,
                        transaction,
                        write.Run);
                    await insert.ExecuteNonQueryAsync(CancellationToken.None);
                }
                else
                {
                    await UpdateSettlementRunAsync(
                        connection,
                        transaction,
                        write.Expected,
                        write.Run,
                        CancellationToken.None);
                }
            }
            await transaction.CommitAsync(CancellationToken.None);
            if (writes.Count > 0)
            {
                EnsureAuthoritativeMarker();
            }
        }
        catch
        {
            _isReady = false;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ExtractionRunCreditCommit> CreditRemovedRunAsync(
        ExtractionSettlementRun expectedRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedRun);
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            RequireAccount(expectedRun.AccountId);
            RequireSeason(expectedRun.SeasonId);
            if (expectedRun.TotalValue <= 0)
            {
                throw new InvalidDataException(
                    $"Extraction run '{expectedRun.RunId}' has a non-positive credit amount.");
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
            var persistedRun = await ReadSettlementRunAsync(
                    connection,
                    transaction,
                    expectedRun.RunId)
                ?? throw new InvalidDataException(
                    $"Extraction run '{expectedRun.RunId}' is missing from SQLite.");
            EnsureMatchingCreditRun(expectedRun, persistedRun);

            var existingLedger = await ReadExtractionRunCreditAsync(
                connection,
                transaction,
                expectedRun,
                CancellationToken.None);
            if (persistedRun.State is
                ExtractionSettlementState.Credited or ExtractionSettlementState.Settled)
            {
                return existingLedger is null
                    ? throw new InvalidDataException(
                        $"Extraction run '{expectedRun.RunId}' is {persistedRun.State} without a unique credit row.")
                    : new ExtractionRunCreditCommit(
                        CloneSettlementRun(persistedRun),
                        existingLedger,
                        false);
            }
            if (persistedRun.State != ExtractionSettlementState.Removed ||
                persistedRun.Revision != expectedRun.Revision ||
                persistedRun.LeaseId != expectedRun.LeaseId)
            {
                throw new InvalidOperationException(
                    $"Extraction run '{expectedRun.RunId}' changed before atomic credit commit.");
            }

            var now = UtcNow();
            var creditedRun = persistedRun with
            {
                State = ExtractionSettlementState.Credited,
                Revision = checked(persistedRun.Revision + 1),
                StateChangedAt = now,
                UpdatedAt = now,
                LastHeartbeatAt = now,
                LeaseExpiresAt = now.Add(ExtractionRunStore.SettlementLeaseDuration)
            };
            if (existingLedger is not null)
            {
                await UpdateSettlementRunAsync(
                    connection,
                    transaction,
                    persistedRun,
                    creditedRun,
                    CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
                return new ExtractionRunCreditCommit(
                    CloneSettlementRun(creditedRun),
                    existingLedger,
                    false);
            }

            var reconciliationActor = string.IsNullOrWhiteSpace(persistedRun.ReconciliationActor)
                ? "system:extraction-settlement"
                : persistedRun.ReconciliationActor;
            var creditReason = persistedRun.ReconciliationActor is null
                ? $"撤离结算 {persistedRun.RunId:N}"
                : $"人工确认资源兑换 {persistedRun.RunId:N}：{persistedRun.ErrorMessage}";
            var request = NormalizeWalletAdjustment(new WalletAdjustmentRequest(
                persistedRun.AccountId,
                persistedRun.SeasonId,
                ExtractionCurrency.SeasonVoucher,
                persistedRun.TotalValue,
                creditReason,
                "extraction_run",
                persistedRun.RunId.ToString("N"),
                reconciliationActor,
                $"extraction-credit-{persistedRun.RunId:N}"));
            var requestHash = HashWalletAdjustment(request);
            var idempotencyScope = IdempotencyScope(
                "wallet",
                request.AccountId,
                request.IdempotencyKey);
            if (_idempotency.TryGetValue(idempotencyScope, out var replay))
            {
                throw new InvalidDataException(
                    $"Wallet idempotency '{idempotencyScope}' exists without an extraction run credit index (resource {replay.ResourceId}).");
            }

            var current = GetBalance(
                request.AccountId,
                request.Currency,
                request.SeasonId);
            var nextBalance = checked(current.Balance + request.Delta);
            if (nextBalance > MaximumWebSafeInteger)
            {
                throw new InvalidOperationException(
                    "The extraction credit would exceed the exact web integer range.");
            }
            var balance = current with
            {
                Balance = nextBalance,
                Revision = checked(current.Revision + 1),
                UpdatedAt = now
            };
            var ledger = new WalletLedgerEntry(
                Guid.NewGuid(),
                request.AccountId,
                request.Currency,
                request.SeasonId,
                request.Delta,
                nextBalance,
                request.Reason,
                request.ReferenceType,
                request.ReferenceId,
                request.Actor,
                now);
            var idempotency = new StoredIdempotency(
                idempotencyScope,
                requestHash,
                "wallet-ledger",
                ledger.EntryId,
                now);
            var storeEvent = NewEvent(
                "wallet.adjusted",
                now,
                balances: [balance],
                ledgerEntries: [ledger],
                idempotency: idempotency);
            var payload = JsonSerializer.Serialize(storeEvent, JsonOptions);

            await using (var eventCommand = CreateInsertCommand(
                             connection,
                             transaction,
                             storeEvent,
                             payload))
            {
                await eventCommand.ExecuteNonQueryAsync(CancellationToken.None);
            }
            await InsertExtractionRunCreditAsync(
                connection,
                transaction,
                creditedRun,
                ledger,
                CancellationToken.None);
            await UpdateSettlementRunAsync(
                connection,
                transaction,
                persistedRun,
                creditedRun,
                CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            EnsureAuthoritativeMarker();
            ApplyEvent(storeEvent);
            return new ExtractionRunCreditCommit(
                CloneSettlementRun(creditedRun),
                ledger,
                true);
        }
        catch
        {
            _isReady = false;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShopProduct>> ListProductsAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _products.Values
                .Where(product => includeInactive || product.Active)
                .OrderBy(product => product.Sku, StringComparer.OrdinalIgnoreCase)
                .Select(CloneProduct)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopProduct?> GetProductAsync(
        string sku,
        CancellationToken cancellationToken)
    {
        var normalizedSku = NormalizeSku(sku);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _products.TryGetValue(normalizedSku, out var product) ? CloneProduct(product) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopProduct> UpsertProductAsync(
        ShopProductDefinition definition,
        long? expectedRevision,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var normalized = NormalizeProductDefinition(definition);
        var normalizedActor = NormalizeRequired(actor, 128, nameof(actor));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            _products.TryGetValue(normalized.Sku, out var existing);
            VerifyRevision(existing?.Revision, expectedRevision, "product");
            var now = UtcNow();
            var product = new ShopProduct(
                existing?.ProductId ?? Guid.NewGuid(),
                normalized.Sku,
                normalized.DisplayName,
                normalized.Description,
                normalized.PriceCurrency,
                normalized.UnitPrice,
                normalized.ItemGrants.ToArray(),
                normalized.PurchaseLimitPerSeason,
                normalized.Active,
                normalized.AvailableFrom?.ToUniversalTime(),
                normalized.AvailableUntil?.ToUniversalTime(),
                checked((existing?.Revision ?? 0) + 1),
                normalizedActor,
                existing?.CreatedAt ?? now,
                now,
                normalized.Category,
                normalized.Tags.ToArray(),
                normalized.FeaturedRank,
                normalized.GlobalStock,
                normalized.ContentVersionId,
                normalized.ContentHash,
                normalized.IconKey,
                normalized.Rarity,
                normalized.Usage);
            await AppendAndApplyAsync(NewEvent("product.upserted", now, product: product), cancellationToken);
            return CloneProduct(product);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContentProductProjectionActivationResult> ActivateContentProductProjectionAsync(
        ContentProductProjectionActivation activation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        var serverId = NormalizeRequired(activation.ServerId, 64, nameof(activation.ServerId));
        if (activation.VersionId == Guid.Empty)
        {
            throw new ArgumentException("Content version id cannot be empty.", nameof(activation));
        }
        if (activation.ExpectedCurrentVersionId == Guid.Empty)
        {
            throw new ArgumentException("Expected current version id cannot be empty.", nameof(activation));
        }
        if (activation.VersionNumber <= 0)
        {
            throw new ArgumentException("Content version number must be positive.", nameof(activation));
        }
        var contentHash = NormalizeRequired(
            activation.ContentHash,
            64,
            nameof(activation.ContentHash)).ToLowerInvariant();
        if (contentHash.Length != 64 || contentHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Content hash must be a 64-character SHA-256 hexadecimal value.",
                nameof(activation));
        }
        var rulesVersion = NormalizeRequired(
            activation.RulesVersion,
            128,
            nameof(activation.RulesVersion));
        var action = NormalizeRequired(activation.Action, 16, nameof(activation.Action)).ToLowerInvariant();
        if (action is not ("publish" or "rollback"))
        {
            throw new ArgumentException("Content activation action must be publish or rollback.", nameof(activation));
        }
        var actor = NormalizeRequired(activation.Actor, 128, nameof(activation.Actor));
        if (activation.Products is null || activation.Products.Count == 0)
        {
            throw new ArgumentException("A content projection must contain at least one product.", nameof(activation));
        }
        var definitions = activation.Products
            .Select(NormalizeProductDefinition)
            .Select(definition => definition with { ContentHash = contentHash })
            .OrderBy(definition => definition.Sku, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (definitions.Select(definition => definition.Sku)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != definitions.Length)
        {
            throw new ArgumentException("A content projection cannot contain duplicate SKUs.", nameof(activation));
        }
        if (definitions.Any(definition =>
                definition.ContentVersionId != activation.VersionId ||
                !string.Equals(definition.ContentHash, contentHash, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Every projected product must carry the target content version id and hash.",
                nameof(activation));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            cancellationToken.ThrowIfCancellationRequested();
            var now = UtcNow();
            var targetSkus = definitions
                .Select(definition => definition.Sku)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var projected = new List<ShopProduct>(definitions.Length + _products.Count);
            foreach (var definition in definitions)
            {
                _products.TryGetValue(definition.Sku, out var existing);
                projected.Add(CreateProjectedProduct(existing, definition, actor, now));
            }
            foreach (var retired in _products.Values
                         .Where(product => product.Active && !targetSkus.Contains(product.Sku))
                         .OrderBy(product => product.Sku, StringComparer.OrdinalIgnoreCase))
            {
                projected.Add(CreateProjectedProduct(
                    retired,
                    new ShopProductDefinition(
                        retired.Sku,
                        retired.DisplayName,
                        retired.Description,
                        retired.PriceCurrency,
                        retired.UnitPrice,
                        retired.ItemGrants,
                        retired.PurchaseLimitPerSeason,
                        false,
                        retired.AvailableFrom,
                        retired.AvailableUntil,
                        retired.Category,
                        retired.Tags,
                        retired.FeaturedRank,
                        retired.GlobalStock,
                        activation.VersionId,
                        contentHash,
                        retired.IconKey,
                        retired.Rarity,
                        retired.Usage),
                    actor,
                    now));
            }

            var changedProducts = projected
                .Where(projectedProduct =>
                    !_products.TryGetValue(projectedProduct.Sku, out var existing) ||
                    !ProductProjectionMatches(existing, projectedProduct))
                .ToArray();
            var storeEvents = changedProducts
                .Select(product => NewEvent("content.product-projected", now, product: product))
                .ToArray();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
                await pragma.ExecuteNonQueryAsync(CancellationToken.None);
            }
            await using var transaction = connection.BeginTransaction(deferred: false);
            await VerifyContentProjectionTargetAsync(
                connection,
                transaction,
                serverId,
                activation.VersionId,
                activation.VersionNumber,
                activation.BusinessDate,
                rulesVersion,
                contentHash,
                definitions,
                CancellationToken.None);
            var currentVersionId = await ReadCurrentContentVersionIdAsync(
                connection,
                transaction,
                serverId,
                CancellationToken.None);
            if (currentVersionId != activation.ExpectedCurrentVersionId &&
                currentVersionId != activation.VersionId)
            {
                throw new ContentStoreException(
                    "CONTENT_POINTER_CONFLICT",
                    "The current content pointer changed before the complete product projection could be activated.");
            }

            for (var index = 0; index < storeEvents.Length; index++)
            {
                var storeEvent = storeEvents[index];
                var payload = JsonSerializer.Serialize(storeEvent, JsonOptions);
                await using var command = CreateInsertCommand(connection, transaction, storeEvent, payload);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
                _contentProjectionFaultInjector?.ThrowAfterProjectedProduct(
                    index + 1,
                    changedProducts[index].Sku);
            }

            var pointerChanged = currentVersionId != activation.VersionId;
            if (pointerChanged)
            {
                await CompareAndSwapContentPointerAsync(
                    connection,
                    transaction,
                    serverId,
                    activation.VersionId,
                    activation.ExpectedCurrentVersionId,
                    now,
                    CancellationToken.None);
                await InsertContentProjectionActivationAsync(
                    connection,
                    transaction,
                    serverId,
                    currentVersionId,
                    activation.VersionId,
                    action,
                    actor,
                    now,
                    CancellationToken.None);
            }
            await transaction.CommitAsync(CancellationToken.None);
            try
            {
                foreach (var storeEvent in storeEvents)
                {
                    ApplyEvent(storeEvent);
                }
                EnsureAuthoritativeMarker();
            }
            catch
            {
                // The SQLite commit is already authoritative. Fail closed so
                // no request can observe stale in-memory state; restart will
                // replay the complete committed event batch before readiness.
                _isReady = false;
                throw;
            }
            return new ContentProductProjectionActivationResult(
                activation.VersionId,
                currentVersionId,
                pointerChanged,
                !pointerChanged && storeEvents.Length == 0,
                now,
                projected.Select(CloneProduct).ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> GetGlobalPurchasedQuantityAsync(
        Guid seasonId,
        string sku,
        CancellationToken cancellationToken)
    {
        if (seasonId == Guid.Empty)
        {
            throw new ArgumentException("Season id cannot be empty.", nameof(seasonId));
        }
        var normalizedSku = NormalizeSku(sku);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            RequireSeason(seasonId);
            return GetGlobalPurchasedQuantity(seasonId, normalizedSku);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopPurchaseResult> PurchaseAsync(
        ShopPurchaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = NormalizePurchaseRequest(request);
        var requestHash = HashPurchase(normalized);
        var scope = IdempotencyScope("purchase", normalized.AccountId, normalized.IdempotencyKey);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (_idempotency.TryGetValue(scope, out var replay))
            {
                if (!string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    return PurchaseFailure(
                        "IDEMPOTENCY_CONFLICT",
                        "The idempotency key was already used for a different purchase.",
                        idempotencyConflict: true);
                }

                var replayedOrder = _orders[replay.ResourceId];
                var pendingDelivery = _deliveries.TryGetValue(replayedOrder.DeliveryId, out var replayedDelivery) &&
                    replayedDelivery.State == ShopDeliveryState.Pending
                        ? CreateDeliveryWorkItem(replayedOrder)
                        : null;
                return new ShopPurchaseResult(
                    CloneOrder(replayedOrder),
                    pendingDelivery,
                    false,
                    false,
                    null,
                    null);
            }

            if (!_accounts.ContainsKey(normalized.AccountId))
            {
                return PurchaseFailure("ACCOUNT_NOT_FOUND", "The extraction account does not exist.");
            }
            if (!_seasons.TryGetValue(normalized.SeasonId, out var season))
            {
                return PurchaseFailure("SEASON_NOT_FOUND", "The extraction season does not exist.");
            }
            var now = UtcNow();
            if (season.State != ExtractionSeasonState.Active || now < season.StartsAt || now >= season.EndsAt)
            {
                return PurchaseFailure("SEASON_NOT_ACTIVE", "Purchases require an active season within its time window.");
            }
            if (!string.Equals(season.ServerId, normalized.ServerId, StringComparison.OrdinalIgnoreCase))
            {
                return PurchaseFailure("SERVER_SEASON_MISMATCH", "The season does not belong to the target server.");
            }

            var products = new List<(ShopProduct Product, int Quantity)>();
            foreach (var requestedLine in normalized.Lines)
            {
                if (!_products.TryGetValue(requestedLine.Sku, out var product))
                {
                    return PurchaseFailure("PRODUCT_NOT_FOUND", $"Shop product '{requestedLine.Sku}' does not exist.");
                }
                if (!product.Active ||
                    (product.AvailableFrom is not null && now < product.AvailableFrom.Value) ||
                    (product.AvailableUntil is not null && now >= product.AvailableUntil.Value))
                {
                    return PurchaseFailure("PRODUCT_NOT_AVAILABLE", $"Shop product '{product.Sku}' is not available.");
                }
                if (normalized.ExpectedContentVersionId is Guid expectedVersionId &&
                    (product.ContentVersionId != expectedVersionId ||
                     !string.Equals(
                         product.ContentHash,
                         normalized.ExpectedContentHash,
                         StringComparison.Ordinal)))
                {
                    return PurchaseFailure(
                        "OFFER_NOT_AVAILABLE",
                        "The economy content changed before the purchase was committed; refresh the catalog.");
                }

                if (product.PurchaseLimitPerSeason is int limit)
                {
                    var alreadyPurchased = _orders.Values
                        .Where(order => order.AccountId == normalized.AccountId &&
                            order.SeasonId == normalized.SeasonId &&
                            order.State != ShopOrderState.Refunded)
                        .SelectMany(order => order.Lines)
                        .Where(line => string.Equals(line.Sku, product.Sku, StringComparison.OrdinalIgnoreCase))
                        .Sum(line => (long)line.Quantity);
                    if (alreadyPurchased + requestedLine.Quantity > limit)
                    {
                        return PurchaseFailure(
                            "PURCHASE_LIMIT_EXCEEDED",
                            $"Shop product '{product.Sku}' exceeds its per-season purchase limit of {limit}.");
                    }
                }
                if (product.GlobalStock is long globalStock)
                {
                    var globallyPurchased = GetGlobalPurchasedQuantity(
                        normalized.SeasonId,
                        product.Sku);
                    if (globallyPurchased + requestedLine.Quantity > globalStock)
                    {
                        return PurchaseFailure(
                            "GLOBAL_STOCK_EXCEEDED",
                            $"Shop product '{product.Sku}' exceeds its server-wide stock of {globalStock} for this season.");
                    }
                }
                products.Add((product, requestedLine.Quantity));
            }

            Guid orderId = Guid.NewGuid();
            List<ShopOrderLine> orderLines = [];
            Dictionary<ExtractionCurrency, long> chargeTotals = [];
            try
            {
                foreach (var entry in products)
                {
                    var lineTotal = checked(entry.Product.UnitPrice * entry.Quantity);
                    if (lineTotal > 0)
                    {
                        chargeTotals[entry.Product.PriceCurrency] = checked(
                            chargeTotals.GetValueOrDefault(entry.Product.PriceCurrency) + lineTotal);
                    }
                    orderLines.Add(new ShopOrderLine(
                        Guid.NewGuid(),
                        entry.Product.ProductId,
                        entry.Product.Sku,
                        entry.Product.DisplayName,
                        entry.Quantity,
                        entry.Product.PriceCurrency,
                        entry.Product.UnitPrice,
                        lineTotal,
                        entry.Product.ItemGrants.ToArray(),
                        entry.Product.Category,
                        entry.Product.Tags.ToArray(),
                        entry.Product.FeaturedRank,
                        entry.Product.GlobalStock,
                        entry.Product.ContentVersionId,
                        entry.Product.ContentHash));
                }
            }
            catch (OverflowException)
            {
                return PurchaseFailure("ORDER_TOTAL_OVERFLOW", "The requested order exceeds the supported amount range.");
            }

            List<WalletBalance> updatedBalances = [];
            List<WalletLedgerEntry> ledgerEntries = [];
            foreach (var charge in chargeTotals.OrderBy(item => item.Key))
            {
                var balanceSeasonId = ScopeSeasonId(charge.Key, normalized.SeasonId);
                var current = GetBalance(normalized.AccountId, charge.Key, balanceSeasonId);
                if (current.Balance < charge.Value)
                {
                    return PurchaseFailure(
                        "INSUFFICIENT_FUNDS",
                        $"The {charge.Key} balance is insufficient for this purchase.");
                }

                var nextBalance = current with
                {
                    Balance = checked(current.Balance - charge.Value),
                    Revision = checked(current.Revision + 1),
                    UpdatedAt = now
                };
                updatedBalances.Add(nextBalance);
                ledgerEntries.Add(new WalletLedgerEntry(
                    Guid.NewGuid(),
                    normalized.AccountId,
                    charge.Key,
                    balanceSeasonId,
                    -charge.Value,
                    nextBalance.Balance,
                    normalized.Reason,
                    "shop_order",
                    orderId.ToString("N"),
                    normalized.Actor,
                    now));
            }

            var deliveryId = Guid.NewGuid();
            var delivery = new ShopDelivery(
                deliveryId,
                orderId,
                1,
                ShopDeliveryState.Pending,
                DeliveryIdempotencyKey(orderId, 1),
                null,
                null,
                null,
                now,
                null,
                null);
            var order = new ShopOrder(
                orderId,
                normalized.AccountId,
                normalized.SeasonId,
                normalized.ServerId,
                normalized.PlayerIdentifier,
                orderLines.ToArray(),
                chargeTotals
                    .OrderBy(item => item.Key)
                    .Select(item => new ShopOrderCharge(item.Key, item.Value))
                    .ToArray(),
                ShopOrderState.PendingDelivery,
                deliveryId,
                1,
                normalized.IdempotencyKey,
                normalized.Actor,
                normalized.Reason,
                now,
                now,
                normalized.PlayerUid,
                normalized.WorldId);
            ShopDeliveryWorkItem workItem;
            try
            {
                workItem = CreateDeliveryWorkItem(order);
            }
            catch (OverflowException)
            {
                return PurchaseFailure(
                    "DELIVERY_QUANTITY_OVERFLOW",
                    "The requested item delivery exceeds the supported quantity range.");
            }

            var idempotency = new StoredIdempotency(
                scope,
                requestHash,
                "shop-order",
                orderId,
                now);
            var committed = await AppendAndApplyAsync(
                NewEvent(
                    "purchase.committed",
                    now,
                    balances: updatedBalances,
                    ledgerEntries: ledgerEntries,
                    order: order,
                    delivery: delivery,
                    idempotency: idempotency),
                cancellationToken,
                normalized.ExpectedContentVersionId is Guid expectedContentVersionId
                    ? new ContentOfferExpectation(
                        normalized.ServerId,
                        expectedContentVersionId,
                        normalized.ExpectedContentHash!)
                    : null);
            if (!committed)
            {
                return PurchaseFailure(
                    "OFFER_NOT_AVAILABLE",
                    "The economy content changed before the purchase was committed; refresh the catalog.");
            }
            return new ShopPurchaseResult(CloneOrder(order), workItem, true, false, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopOrder?> GetOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _orders.TryGetValue(orderId, out var order) ? CloneOrder(order) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShopOrder>> ListOrdersAsync(
        Guid? accountId,
        Guid? seasonId,
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _orders.Values
                .Where(order => (accountId is null || order.AccountId == accountId) &&
                    (seasonId is null || order.SeasonId == seasonId))
                .OrderByDescending(order => order.CreatedAt)
                .ThenByDescending(order => order.OrderId)
                .Take(Math.Clamp(limit, 1, 1000))
                .Select(CloneOrder)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShopOrder>> ListBlockingOrdersAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _orders.Values
                .Where(order => order.State is
                    ShopOrderState.PendingDelivery or
                    ShopOrderState.Dispatching or
                    ShopOrderState.DeliveryFailed or
                    ShopOrderState.DeliveryUncertain)
                .OrderBy(order => order.CreatedAt)
                .ThenBy(order => order.OrderId)
                .Select(CloneOrder)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShopDeliveryWorkItem>> ListPendingDeliveriesAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _deliveries.Values
                .Where(delivery => delivery.State == ShopDeliveryState.Pending)
                .OrderBy(delivery => delivery.CreatedAt)
                .Take(Math.Clamp(limit, 1, 100))
                .Select(delivery => CreateDeliveryWorkItem(_orders[delivery.OrderId]))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShopDeliveryWorkItem>> ListAllPendingDeliveriesAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _deliveries.Values
                .Where(delivery => delivery.State == ShopDeliveryState.Pending)
                .OrderBy(delivery => delivery.CreatedAt)
                .Select(delivery => CreateDeliveryWorkItem(_orders[delivery.OrderId]))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ShopDelivery>> ListInFlightDeliveriesAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return _deliveries.Values
                .Where(delivery =>
                    delivery.State == ShopDeliveryState.Dispatching &&
                    delivery.CommandId is not null)
                .OrderBy(delivery => delivery.DispatchedAt)
                .Take(Math.Clamp(limit, 1, 1000))
                .Select(delivery => delivery with { })
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopDeliveryUpdateResult> MarkDeliveryDispatchedAsync(
        Guid deliveryId,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("Command id cannot be empty.", nameof(commandId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_deliveries.TryGetValue(deliveryId, out var delivery))
            {
                return DeliveryFailure("DELIVERY_NOT_FOUND", "The shop delivery does not exist.");
            }
            var order = _orders[delivery.OrderId];
            if (delivery.State == ShopDeliveryState.Dispatching && delivery.CommandId == commandId)
            {
                return new ShopDeliveryUpdateResult(CloneOrder(order), delivery, false, null, null);
            }
            if (delivery.State != ShopDeliveryState.Pending)
            {
                return DeliveryFailure(
                    "INVALID_DELIVERY_STATE",
                    $"Delivery '{deliveryId}' cannot be dispatched from state '{delivery.State}'.",
                    order,
                    delivery);
            }

            var now = UtcNow();
            var updatedDelivery = delivery with
            {
                State = ShopDeliveryState.Dispatching,
                CommandId = commandId,
                DispatchedAt = now
            };
            var updatedOrder = order with { State = ShopOrderState.Dispatching, UpdatedAt = now };
            await AppendAndApplyAsync(
                NewEvent(
                    "delivery.dispatched",
                    now,
                    order: updatedOrder,
                    delivery: updatedDelivery),
                cancellationToken);
            return new ShopDeliveryUpdateResult(
                CloneOrder(updatedOrder),
                updatedDelivery,
                true,
                null,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopDeliveryUpdateResult> MarkDeliveryOutcomeAsync(
        Guid deliveryId,
        ShopDeliveryState state,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        if (state is not (ShopDeliveryState.Delivered or ShopDeliveryState.Failed or ShopDeliveryState.Uncertain))
        {
            throw new ArgumentException(
                "A delivery outcome must be Delivered, Failed, or Uncertain.",
                nameof(state));
        }
        var normalizedErrorCode = NormalizeOptional(errorCode, 64, nameof(errorCode));
        var normalizedErrorMessage = NormalizeOptional(errorMessage, 1024, nameof(errorMessage));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_deliveries.TryGetValue(deliveryId, out var delivery))
            {
                return DeliveryFailure("DELIVERY_NOT_FOUND", "The shop delivery does not exist.");
            }
            var order = _orders[delivery.OrderId];
            if (delivery.State == state)
            {
                return new ShopDeliveryUpdateResult(CloneOrder(order), delivery, false, null, null);
            }
            if (delivery.State is ShopDeliveryState.Delivered or ShopDeliveryState.Failed or ShopDeliveryState.Uncertain)
            {
                return DeliveryFailure(
                    "DELIVERY_ALREADY_TERMINAL",
                    $"Delivery '{deliveryId}' already has terminal state '{delivery.State}'.",
                    order,
                    delivery);
            }

            var now = UtcNow();
            var updatedDelivery = delivery with
            {
                State = state,
                ErrorCode = state == ShopDeliveryState.Delivered ? null : normalizedErrorCode,
                ErrorMessage = state == ShopDeliveryState.Delivered ? null : normalizedErrorMessage,
                CompletedAt = now
            };
            var orderState = state switch
            {
                ShopDeliveryState.Delivered => ShopOrderState.Delivered,
                ShopDeliveryState.Failed => ShopOrderState.DeliveryFailed,
                ShopDeliveryState.Uncertain => ShopOrderState.DeliveryUncertain,
                _ => throw new InvalidOperationException("Unsupported terminal delivery state.")
            };
            var updatedOrder = order with { State = orderState, UpdatedAt = now };
            await AppendAndApplyAsync(
                NewEvent(
                    $"delivery.{state.ToString().ToLowerInvariant()}",
                    now,
                    order: updatedOrder,
                    delivery: updatedDelivery),
                cancellationToken);
            return new ShopDeliveryUpdateResult(
                CloneOrder(updatedOrder),
                updatedDelivery,
                true,
                null,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopDeliveryUpdateResult> PrepareDeliveryRetryAsync(
        Guid orderId,
        string actor,
        string reason,
        CancellationToken cancellationToken)
    {
        _ = NormalizeRequired(actor, 128, nameof(actor));
        _ = NormalizeRequired(reason, 512, nameof(reason));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_orders.TryGetValue(orderId, out var order))
            {
                return DeliveryFailure("ORDER_NOT_FOUND", "The shop order does not exist.");
            }
            _deliveries.TryGetValue(order.DeliveryId, out var currentDelivery);
            if (order.State != ShopOrderState.DeliveryFailed ||
                currentDelivery is null ||
                currentDelivery.State != ShopDeliveryState.Failed)
            {
                return DeliveryFailure(
                    "RETRY_NOT_ALLOWED",
                    "Only a definitively failed delivery can be retried. Uncertain deliveries require reconciliation.",
                    order,
                    currentDelivery);
            }

            var attempt = checked(order.DeliveryAttempt + 1);
            var now = UtcNow();
            var delivery = new ShopDelivery(
                Guid.NewGuid(),
                order.OrderId,
                attempt,
                ShopDeliveryState.Pending,
                DeliveryIdempotencyKey(order.OrderId, attempt),
                null,
                null,
                null,
                now,
                null,
                null);
            var updatedOrder = order with
            {
                State = ShopOrderState.PendingDelivery,
                DeliveryId = delivery.DeliveryId,
                DeliveryAttempt = attempt,
                UpdatedAt = now
            };
            await AppendAndApplyAsync(
                NewEvent("delivery.retry-prepared", now, order: updatedOrder, delivery: delivery),
                cancellationToken);
            return new ShopDeliveryUpdateResult(
                CloneOrder(updatedOrder),
                delivery,
                true,
                null,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ShopDeliveryUpdateResult> MarkUncertainOrderDeliveredAsync(
        Guid orderId,
        string actor,
        string reason,
        CancellationToken cancellationToken)
    {
        _ = NormalizeRequired(actor, 128, nameof(actor));
        var normalizedReason = NormalizeRequired(reason, 512, nameof(reason));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_orders.TryGetValue(orderId, out var order))
            {
                return DeliveryFailure("ORDER_NOT_FOUND", "The shop order does not exist.");
            }
            if (!_deliveries.TryGetValue(order.DeliveryId, out var delivery))
            {
                return DeliveryFailure("DELIVERY_NOT_FOUND", "The shop delivery does not exist.", order);
            }
            if (order.State == ShopOrderState.Delivered && delivery.State == ShopDeliveryState.Delivered)
            {
                return new ShopDeliveryUpdateResult(CloneOrder(order), delivery, false, null, null);
            }
            if (order.State != ShopOrderState.DeliveryUncertain ||
                delivery.State != ShopDeliveryState.Uncertain)
            {
                return DeliveryFailure(
                    "RECONCILIATION_NOT_ALLOWED",
                    "Only an uncertain delivery can be manually confirmed as delivered.",
                    order,
                    delivery);
            }

            var now = UtcNow();
            var updatedDelivery = delivery with
            {
                State = ShopDeliveryState.Delivered,
                ErrorCode = "MANUALLY_RECONCILED_DELIVERED",
                ErrorMessage = normalizedReason,
                CompletedAt = delivery.CompletedAt ?? now
            };
            var updatedOrder = order with { State = ShopOrderState.Delivered, UpdatedAt = now };
            await AppendAndApplyAsync(
                NewEvent(
                    "delivery.manually-reconciled-delivered",
                    now,
                    order: updatedOrder,
                    delivery: updatedDelivery),
                cancellationToken);
            return new ShopDeliveryUpdateResult(
                CloneOrder(updatedOrder),
                updatedDelivery,
                true,
                null,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ShopRefundResult> RefundFailedOrderAsync(
        Guid orderId,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken) =>
        RefundOrderAsync(
            orderId,
            idempotencyKey,
            actor,
            reason,
            allowUncertain: false,
            cancellationToken);

    public Task<ShopRefundResult> RefundUncertainOrderAsync(
        Guid orderId,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken) =>
        RefundOrderAsync(
            orderId,
            idempotencyKey,
            actor,
            reason,
            allowUncertain: true,
            cancellationToken);

    private async Task<ShopRefundResult> RefundOrderAsync(
        Guid orderId,
        string idempotencyKey,
        string actor,
        string reason,
        bool allowUncertain,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        var normalizedActor = NormalizeRequired(actor, 128, nameof(actor));
        var normalizedReason = NormalizeRequired(reason, 512, nameof(reason));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            if (!_orders.TryGetValue(orderId, out var order))
            {
                return RefundFailure("ORDER_NOT_FOUND", "The shop order does not exist.");
            }
            var scope = IdempotencyScope("refund", order.AccountId, normalizedKey);
            var requestHash = HashRefund(orderId, normalizedReason);
            if (_idempotency.TryGetValue(scope, out var replay))
            {
                if (!string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    return new ShopRefundResult(
                        null,
                        [],
                        false,
                        true,
                        "IDEMPOTENCY_CONFLICT",
                        "The idempotency key was already used for a different refund.");
                }
                var replayedOrder = _orders[replay.ResourceId];
                var replayedEntries = _ledger.Values
                    .Where(entry => entry.ReferenceType == "shop_refund" &&
                        string.Equals(entry.ReferenceId, orderId.ToString("N"), StringComparison.Ordinal))
                    .OrderBy(entry => entry.Currency)
                    .ToArray();
                return new ShopRefundResult(CloneOrder(replayedOrder), replayedEntries, false, false, null, null);
            }
            if (order.State != ShopOrderState.DeliveryFailed &&
                !(allowUncertain && order.State == ShopOrderState.DeliveryUncertain))
            {
                return RefundFailure(
                    "REFUND_NOT_ALLOWED",
                    allowUncertain
                        ? "Only an uncertain order can be manually refunded by this reconciliation path."
                        : "Only a definitively failed order can be automatically refunded. Delivered or uncertain orders require manual reconciliation.",
                    order);
            }

            var now = UtcNow();
            List<WalletBalance> updatedBalances = [];
            List<WalletLedgerEntry> ledgerEntries = [];
            try
            {
                foreach (var charge in order.Charges)
                {
                    var seasonId = ScopeSeasonId(charge.Currency, order.SeasonId);
                    var current = GetBalance(order.AccountId, charge.Currency, seasonId);
                    var refundedBalance = checked(current.Balance + charge.Amount);
                    if (refundedBalance > MaximumWebSafeInteger)
                    {
                        return RefundFailure(
                            "BALANCE_OUT_OF_WEB_RANGE",
                            "The refund would exceed the exact integer range supported by the web console.",
                            order);
                    }
                    var next = current with
                    {
                        Balance = refundedBalance,
                        Revision = checked(current.Revision + 1),
                        UpdatedAt = now
                    };
                    updatedBalances.Add(next);
                    ledgerEntries.Add(new WalletLedgerEntry(
                        Guid.NewGuid(),
                        order.AccountId,
                        charge.Currency,
                        seasonId,
                        charge.Amount,
                        next.Balance,
                        normalizedReason,
                        "shop_refund",
                        order.OrderId.ToString("N"),
                        normalizedActor,
                        now));
                }
            }
            catch (OverflowException)
            {
                return RefundFailure("BALANCE_OVERFLOW", "The refund exceeds the supported balance range.", order);
            }

            var updatedOrder = order with { State = ShopOrderState.Refunded, UpdatedAt = now };
            var idempotency = new StoredIdempotency(
                scope,
                requestHash,
                "shop-order",
                order.OrderId,
                now);
            await AppendAndApplyAsync(
                NewEvent(
                    "order.refunded",
                    now,
                    balances: updatedBalances,
                    ledgerEntries: ledgerEntries,
                    order: updatedOrder,
                    idempotency: idempotency),
                cancellationToken);
            return new ShopRefundResult(
                CloneOrder(updatedOrder),
                ledgerEntries.ToArray(),
                true,
                false,
                null,
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _isReady = false;
        _instanceLock.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private ExtractionWalletSnapshot CreateWalletSnapshot(Guid accountId, Guid seasonId) => new(
        accountId,
        seasonId,
        CloneBalance(GetBalance(accountId, ExtractionCurrency.MarketCoin, null)),
        CloneBalance(GetBalance(accountId, ExtractionCurrency.SeasonVoucher, seasonId)));

    private long GetGlobalPurchasedQuantity(Guid seasonId, string sku) =>
        _orders.Values
            .Where(order =>
                order.SeasonId == seasonId &&
                order.State != ShopOrderState.Refunded)
            .SelectMany(order => order.Lines)
            .Where(line => string.Equals(line.Sku, sku, StringComparison.OrdinalIgnoreCase))
            .Sum(line => (long)line.Quantity);

    private WalletBalance GetBalance(
        Guid accountId,
        ExtractionCurrency currency,
        Guid? seasonId)
    {
        var scopedSeasonId = ScopeSeasonId(currency, seasonId);
        return _balances.TryGetValue(BalanceKey(accountId, currency, scopedSeasonId), out var balance)
            ? balance
            : new WalletBalance(accountId, currency, scopedSeasonId, 0, 0, UtcNow());
    }

    private ShopDeliveryWorkItem CreateDeliveryWorkItem(ShopOrder order)
    {
        Dictionary<string, int> items = new(StringComparer.Ordinal);
        foreach (var line in order.Lines)
        {
            foreach (var grant in line.ItemGrants)
            {
                var quantity = checked(grant.Quantity * line.Quantity);
                items[grant.ItemId] = checked(items.GetValueOrDefault(grant.ItemId) + quantity);
            }
        }
        var delivery = _deliveries.TryGetValue(order.DeliveryId, out var storedDelivery)
            ? storedDelivery
            : new ShopDelivery(
                order.DeliveryId,
                order.OrderId,
                order.DeliveryAttempt,
                ShopDeliveryState.Pending,
                DeliveryIdempotencyKey(order.OrderId, order.DeliveryAttempt),
                null,
                null,
                null,
                order.CreatedAt,
                null,
                null);
        return new ShopDeliveryWorkItem(
            delivery.DeliveryId,
            order.OrderId,
            order.ServerId,
            order.PlayerIdentifier,
            $"give/items/{Uri.EscapeDataString(order.PlayerIdentifier)}",
            delivery.IdempotencyKey,
            items.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new ShopItemGrant(item.Key, item.Value))
                .ToArray(),
            delivery.Attempt,
            order.PlayerUid,
            order.WorldId);
    }

    private static WalletAdjustmentRequest NormalizeWalletAdjustment(WalletAdjustmentRequest request)
    {
        if (request.AccountId == Guid.Empty)
        {
            throw new ArgumentException("Account id cannot be empty.", nameof(request));
        }
        if (request.Delta == 0)
        {
            throw new ArgumentException("Wallet adjustment delta cannot be zero.", nameof(request));
        }
        if (request.Delta is < -MaximumWebSafeInteger or > MaximumWebSafeInteger)
        {
            throw new ArgumentException(
                "Wallet adjustment delta must fit the exact integer range supported by the web console.",
                nameof(request));
        }
        if (request.Currency == ExtractionCurrency.MarketCoin && request.SeasonId is not null)
        {
            throw new ArgumentException("MarketCoin is permanent and must not have a season id.", nameof(request));
        }
        if (request.Currency == ExtractionCurrency.SeasonVoucher && request.SeasonId is null)
        {
            throw new ArgumentException("SeasonVoucher requires a season id.", nameof(request));
        }
        return request with
        {
            Reason = NormalizeRequired(request.Reason, 512, nameof(request.Reason)),
            ReferenceType = NormalizeRequired(request.ReferenceType, 64, nameof(request.ReferenceType)),
            ReferenceId = NormalizeRequired(request.ReferenceId, 128, nameof(request.ReferenceId)),
            Actor = NormalizeRequired(request.Actor, 128, nameof(request.Actor)),
            IdempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey)
        };
    }

    private static ShopProductDefinition NormalizeProductDefinition(ShopProductDefinition definition)
    {
        if (definition.UnitPrice is < 0 or > MaximumWebSafeInteger)
        {
            throw new ArgumentException(
                "Product unit price must be within the exact integer range supported by the web console.",
                nameof(definition));
        }
        if (definition.PurchaseLimitPerSeason is <= 0)
        {
            throw new ArgumentException("Purchase limit must be positive when supplied.", nameof(definition));
        }
        if (definition.GlobalStock is <= 0 or > MaximumWebSafeInteger)
        {
            throw new ArgumentException(
                "Global stock must be positive and within the exact integer range supported by the web console when supplied.",
                nameof(definition));
        }
        if (definition.FeaturedRank is <= 0)
        {
            throw new ArgumentException("Featured rank must be positive when supplied.", nameof(definition));
        }
        if (definition.AvailableFrom is not null &&
            definition.AvailableUntil is not null &&
            definition.AvailableUntil <= definition.AvailableFrom)
        {
            throw new ArgumentException("AvailableUntil must be later than AvailableFrom.", nameof(definition));
        }
        if (definition.ItemGrants is null || definition.ItemGrants.Count == 0)
        {
            throw new ArgumentException("A product must grant at least one item.", nameof(definition));
        }
        if (definition.ItemGrants.Count > 100)
        {
            throw new ArgumentException("A product cannot contain more than 100 item grants.", nameof(definition));
        }

        Dictionary<string, int> mergedGrants = new(StringComparer.Ordinal);
        foreach (var grant in definition.ItemGrants)
        {
            if (grant is null)
            {
                throw new ArgumentException("Product item grants cannot contain null values.", nameof(definition));
            }
            var itemId = NormalizeRequired(grant.ItemId, 128, nameof(grant.ItemId));
            if (grant.Quantity <= 0)
            {
                throw new ArgumentException("Product item grant quantity must be positive.", nameof(definition));
            }
            mergedGrants[itemId] = checked(mergedGrants.GetValueOrDefault(itemId) + grant.Quantity);
        }

        var category = string.IsNullOrWhiteSpace(definition.Category)
            ? "general"
            : NormalizeRequired(definition.Category, 64, nameof(definition.Category));
        var contentHash = string.IsNullOrWhiteSpace(definition.ContentHash)
            ? null
            : NormalizeRequired(definition.ContentHash, 128, nameof(definition.ContentHash));
        if ((definition.ContentVersionId is null) != (contentHash is null))
        {
            throw new ArgumentException(
                "Content version id and content hash must either both be supplied or both be absent for legacy products.",
                nameof(definition));
        }
        if (definition.ContentVersionId == Guid.Empty)
        {
            throw new ArgumentException("Content version id cannot be empty when supplied.", nameof(definition));
        }
        if (contentHash is not null &&
            (contentHash.Length != 64 || contentHash.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new ArgumentException(
                "Content hash must be a 64-character SHA-256 hexadecimal value when supplied.",
                nameof(definition));
        }
        var tags = (definition.Tags ?? [])
            .Select(tag => NormalizeRequired(tag, 64, "tag"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tags.Length > 20)
        {
            throw new ArgumentException("A product cannot contain more than 20 tags.", nameof(definition));
        }
        var presentationSupplied = definition.IconKey is not null ||
            definition.Rarity is not null || definition.Usage is not null;
        if (presentationSupplied &&
            (!EconomyContentPresentation.IsSafeIconKey(definition.IconKey) ||
             definition.Rarity is null || !Enum.IsDefined(definition.Rarity.Value) ||
             !EconomyContentPresentation.IsSafeUsage(definition.Usage)))
        {
            throw new ArgumentException(
                "Product presentation must be a complete approved iconKey/rarity/usage triple.",
                nameof(definition));
        }
        return definition with
        {
            Sku = NormalizeSku(definition.Sku),
            DisplayName = NormalizeRequired(definition.DisplayName, 128, nameof(definition.DisplayName)),
            Description = NormalizeRequired(definition.Description, 1024, nameof(definition.Description)),
            ItemGrants = mergedGrants
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new ShopItemGrant(item.Key, item.Value))
                .ToArray(),
            Category = category,
            Tags = tags,
            ContentHash = contentHash,
            IconKey = definition.IconKey?.Trim(' ').ToLowerInvariant(),
            Usage = definition.Usage?.Trim(' ')
        };
    }

    private static ShopPurchaseRequest NormalizePurchaseRequest(ShopPurchaseRequest request)
    {
        if (request.AccountId == Guid.Empty || request.SeasonId == Guid.Empty)
        {
            throw new ArgumentException("Account id and season id cannot be empty.", nameof(request));
        }
        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new ArgumentException("A purchase must contain at least one line.", nameof(request));
        }
        if (request.Lines.Count > 50)
        {
            throw new ArgumentException("A purchase cannot contain more than 50 lines.", nameof(request));
        }

        Dictionary<string, int> mergedLines = new(StringComparer.OrdinalIgnoreCase);
        foreach (var line in request.Lines)
        {
            if (line is null)
            {
                throw new ArgumentException("Purchase lines cannot contain null values.", nameof(request));
            }
            var sku = NormalizeSku(line.Sku);
            if (line.Quantity is <= 0 or > 1000)
            {
                throw new ArgumentException("Purchase line quantity must be between 1 and 1000.", nameof(request));
            }
            mergedLines[sku] = checked(mergedLines.GetValueOrDefault(sku) + line.Quantity);
            if (mergedLines[sku] > 1000)
            {
                throw new ArgumentException("Merged purchase quantity cannot exceed 1000 per SKU.", nameof(request));
            }
        }
        var playerUid = string.IsNullOrWhiteSpace(request.PlayerUid)
            ? null
            : NormalizePlayerUid(request.PlayerUid);
        var worldId = string.IsNullOrWhiteSpace(request.WorldId)
            ? null
            : NormalizeBindingWorldId(request.WorldId);
        if ((playerUid is null) != (worldId is null))
        {
            throw new ArgumentException(
                "A purchase target must include both PlayerUID and worldId, or neither for legacy data.",
                nameof(request));
        }
        var expectedContentHash = string.IsNullOrWhiteSpace(request.ExpectedContentHash)
            ? null
            : request.ExpectedContentHash.Trim().ToLowerInvariant();
        if ((request.ExpectedContentVersionId is null) != (expectedContentHash is null))
        {
            throw new ArgumentException(
                "Expected content version id and hash must either both be supplied or both be absent.",
                nameof(request));
        }
        if (request.ExpectedContentVersionId == Guid.Empty)
        {
            throw new ArgumentException("Expected content version id cannot be empty.", nameof(request));
        }
        if (expectedContentHash is not null &&
            (expectedContentHash.Length != 64 || expectedContentHash.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new ArgumentException(
                "Expected content hash must be a 64-character SHA-256 hexadecimal value.",
                nameof(request));
        }
        return request with
        {
            ServerId = NormalizeRequired(request.ServerId, 64, nameof(request.ServerId)),
            PlayerIdentifier = NormalizeRequired(request.PlayerIdentifier, 128, nameof(request.PlayerIdentifier)),
            Lines = mergedLines
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => new ShopPurchaseLineInput(item.Key, item.Value))
                .ToArray(),
            IdempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey),
            Actor = NormalizeRequired(request.Actor, 128, nameof(request.Actor)),
            Reason = NormalizeRequired(request.Reason, 512, nameof(request.Reason)),
            PlayerUid = playerUid,
            WorldId = worldId,
            ExpectedContentHash = expectedContentHash
        };
    }

    private static string HashWalletAdjustment(WalletAdjustmentRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("operation", "wallet.adjust");
        writer.WriteString("accountId", request.AccountId);
        writer.WriteString("currency", request.Currency.ToString());
        if (request.SeasonId is Guid seasonId)
        {
            writer.WriteString("seasonId", seasonId);
        }
        else
        {
            writer.WriteNull("seasonId");
        }
        writer.WriteNumber("delta", request.Delta);
        writer.WriteString("reason", request.Reason);
        writer.WriteString("referenceType", request.ReferenceType);
        writer.WriteString("referenceId", request.ReferenceId);
        writer.WriteEndObject();
        writer.Flush();
        return Sha256(buffer.WrittenSpan);
    }

    private static string HashPurchase(ShopPurchaseRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("operation", "shop.purchase");
        writer.WriteString("accountId", request.AccountId);
        writer.WriteString("seasonId", request.SeasonId);
        writer.WriteString("serverId", request.ServerId);
        writer.WriteString("playerIdentifier", request.PlayerIdentifier);
        writer.WriteStartArray("lines");
        foreach (var line in request.Lines)
        {
            writer.WriteStartObject();
            writer.WriteString("sku", line.Sku);
            writer.WriteNumber("quantity", line.Quantity);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (request.ExpectedContentVersionId is Guid expectedContentVersionId)
        {
            writer.WriteString("expectedContentVersionId", expectedContentVersionId);
            writer.WriteString("expectedContentHash", request.ExpectedContentHash);
        }
        writer.WriteEndObject();
        writer.Flush();
        return Sha256(buffer.WrittenSpan);
    }

    private static string HashRefund(Guid orderId, string reason)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("operation", "shop.refund");
        writer.WriteString("orderId", orderId);
        writer.WriteString("reason", reason);
        writer.WriteEndObject();
        writer.Flush();
        return Sha256(buffer.WrittenSpan);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ShopProduct CreateProjectedProduct(
        ShopProduct? existing,
        ShopProductDefinition definition,
        string actor,
        DateTimeOffset now)
    {
        var candidate = new ShopProduct(
            existing?.ProductId ?? Guid.NewGuid(),
            definition.Sku,
            definition.DisplayName,
            definition.Description,
            definition.PriceCurrency,
            definition.UnitPrice,
            definition.ItemGrants.ToArray(),
            definition.PurchaseLimitPerSeason,
            definition.Active,
            definition.AvailableFrom?.ToUniversalTime(),
            definition.AvailableUntil?.ToUniversalTime(),
            checked((existing?.Revision ?? 0) + 1),
            actor,
            existing?.CreatedAt ?? now,
            now,
            definition.Category,
            definition.Tags.ToArray(),
            definition.FeaturedRank,
            definition.GlobalStock,
            definition.ContentVersionId,
            definition.ContentHash,
            definition.IconKey,
            definition.Rarity,
            definition.Usage);
        return existing is not null && ProductProjectionMatches(existing, candidate)
            ? CloneProduct(existing)
            : candidate;
    }

    private static bool ProductProjectionMatches(ShopProduct left, ShopProduct right) =>
        left.ProductId == right.ProductId &&
        string.Equals(left.Sku, right.Sku, StringComparison.OrdinalIgnoreCase) &&
        left.DisplayName == right.DisplayName &&
        left.Description == right.Description &&
        left.PriceCurrency == right.PriceCurrency &&
        left.UnitPrice == right.UnitPrice &&
        left.ItemGrants.SequenceEqual(right.ItemGrants) &&
        left.PurchaseLimitPerSeason == right.PurchaseLimitPerSeason &&
        left.Active == right.Active &&
        left.AvailableFrom == right.AvailableFrom &&
        left.AvailableUntil == right.AvailableUntil &&
        left.Category == right.Category &&
        left.Tags.SequenceEqual(right.Tags, StringComparer.Ordinal) &&
        left.FeaturedRank == right.FeaturedRank &&
        left.GlobalStock == right.GlobalStock &&
        left.ContentVersionId == right.ContentVersionId &&
        string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal) &&
        left.IconKey == right.IconKey &&
        left.Rarity == right.Rarity &&
        left.Usage == right.Usage;

    private static async Task VerifyContentProjectionTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        Guid versionId,
        long versionNumber,
        DateOnly businessDate,
        string rulesVersion,
        string contentHash,
        IReadOnlyList<ShopProductDefinition> actualProducts,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT server_id, version_number, business_date, rules_version, content_hash,
                   document_json, source_draft_id, published_by, published_at
            FROM content_versions
            WHERE version_id = $versionId;
            """;
        command.Parameters.AddWithValue("$versionId", versionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ContentStoreException(
                "CONTENT_VERSION_NOT_FOUND",
                "The product projection target content version does not exist.");
        }
        if (!string.Equals(reader.GetString(0), serverId, StringComparison.OrdinalIgnoreCase) ||
            reader.GetInt64(1) != versionNumber ||
            !string.Equals(
                reader.GetString(2),
                businessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(3), rulesVersion, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(4), contentHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The requested product projection identity does not match its immutable content version.");
        }
        var definition = JsonSerializer.Deserialize<EconomyContentDefinition>(
            reader.GetString(5),
            EconomyContentJson.Options)
            ?? throw new InvalidDataException(
                "The immutable content version contains an empty definition.");
        if (!Guid.TryParse(reader.GetString(6), out var sourceDraftId) ||
            sourceDraftId == Guid.Empty ||
            !DateTimeOffset.TryParse(
                reader.GetString(8),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var publishedAt))
        {
            throw new InvalidDataException(
                "The immutable content version contains invalid publication identity fields.");
        }
        var targetVersion = new EconomyContentVersion(
            versionId,
            serverId,
            versionNumber,
            businessDate,
            rulesVersion,
            contentHash,
            definition,
            sourceDraftId,
            reader.GetString(7),
            publishedAt);
        var expectedProducts = EconomyContentProductProjection.Create(targetVersion);
        if (expectedProducts.Count != actualProducts.Count ||
            !expectedProducts.Zip(actualProducts)
                .All(pair => EconomyContentProductProjection.Matches(pair.First, pair.Second)))
        {
            throw new InvalidDataException(
                "The requested product projection does not match the immutable content document.");
        }
    }

    private static async Task<Guid?> ReadCurrentContentVersionIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT version_id
            FROM content_current
            WHERE server_id = $serverId COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return null;
        }
        return Guid.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out var versionId) &&
               versionId != Guid.Empty
            ? versionId
            : throw new InvalidDataException("The current content pointer contains an invalid version id.");
    }

    private static async Task CompareAndSwapContentPointerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        Guid versionId,
        Guid? expectedCurrentVersionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (expectedCurrentVersionId is null)
        {
            command.CommandText = """
                INSERT INTO content_current (server_id, version_id, updated_at)
                SELECT $serverId, $versionId, $updatedAt
                WHERE NOT EXISTS (
                    SELECT 1 FROM content_current WHERE server_id = $serverId COLLATE NOCASE);
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE content_current
                SET version_id = $versionId,
                    updated_at = $updatedAt
                WHERE server_id = $serverId COLLATE NOCASE
                  AND version_id = $expectedVersionId;
                """;
            command.Parameters.AddWithValue(
                "$expectedVersionId",
                expectedCurrentVersionId.Value.ToString("D"));
        }
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$versionId", versionId.ToString("D"));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new ContentStoreException(
                "CONTENT_POINTER_CONFLICT",
                "The content pointer compare-and-swap failed during product projection activation.");
        }
    }

    private static async Task InsertContentProjectionActivationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        Guid? previousVersionId,
        Guid versionId,
        string action,
        string actor,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO content_activations (
                activation_id, server_id, previous_version_id, version_id,
                reason, actor, activated_at)
            VALUES (
                $activationId, $serverId, $previousVersionId, $versionId,
                $reason, $actor, $activatedAt);
            """;
        command.Parameters.AddWithValue("$activationId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue(
            "$previousVersionId",
            previousVersionId is Guid previous ? previous.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$versionId", versionId.ToString("D"));
        command.Parameters.AddWithValue("$reason", action);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$activatedAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> AppendAndApplyAsync(
        StoreEvent storeEvent,
        CancellationToken cancellationToken,
        ContentOfferExpectation? offerExpectation = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(storeEvent, JsonOptions);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var transaction = connection.BeginTransaction(deferred: false);
            if (offerExpectation is not null && !await IsCurrentOfferAsync(
                    connection,
                    transaction,
                    offerExpectation,
                    CancellationToken.None))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }
            await using var command = CreateInsertCommand(connection, transaction, storeEvent, payload);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            EnsureAuthoritativeMarker();
        }
        catch
        {
            _isReady = false;
            throw;
        }
        ApplyEvent(storeEvent);
        return true;
    }

    private static async Task<bool> IsCurrentOfferAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ContentOfferExpectation expectation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT current.version_id, versions.content_hash
            FROM content_current AS current
            JOIN content_versions AS versions ON versions.version_id = current.version_id
            WHERE current.server_id = $serverId COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$serverId", expectation.ServerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) &&
               Guid.TryParse(reader.GetString(0), out var versionId) &&
               versionId == expectation.VersionId &&
               string.Equals(reader.GetString(1), expectation.ContentHash, StringComparison.Ordinal);
    }

    private void LoadEvents()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence, payload FROM extraction_events ORDER BY sequence;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sequence = reader.GetInt64(0);
            var storeEvent = DeserializeStoreEvent(reader.GetString(1), $"SQLite sequence {sequence}");
            ApplyEvent(storeEvent);
        }
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=5000;
            PRAGMA user_version=1;
            CREATE TABLE IF NOT EXISTS extraction_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                event_type TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS extraction_settlement_runs (
                run_id TEXT PRIMARY KEY,
                account_id TEXT NOT NULL,
                season_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                state TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK (revision >= 0),
                updated_at TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_extraction_settlement_runs_account_season
                ON extraction_settlement_runs (account_id, season_id, updated_at);
            CREATE INDEX IF NOT EXISTS ix_extraction_settlement_runs_state
                ON extraction_settlement_runs (state, updated_at);
            CREATE TABLE IF NOT EXISTS extraction_run_credits (
                run_id TEXT PRIMARY KEY,
                account_id TEXT NOT NULL,
                season_id TEXT NOT NULL,
                ledger_entry_id TEXT NOT NULL UNIQUE,
                amount INTEGER NOT NULL CHECK (amount > 0),
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS player_identity_bindings (
                binding_id TEXT PRIMARY KEY,
                platform_subject TEXT NOT NULL COLLATE NOCASE,
                season_id TEXT NOT NULL,
                world_id TEXT NOT NULL COLLATE NOCASE
                    CHECK (length(world_id) = 32),
                player_uid TEXT NOT NULL COLLATE NOCASE
                    CHECK (length(player_uid) = 32),
                account_id TEXT NOT NULL,
                first_bound_at TEXT NOT NULL,
                last_verified_at TEXT NOT NULL,
                UNIQUE (season_id, account_id),
                UNIQUE (season_id, platform_subject),
                UNIQUE (world_id, player_uid)
            );
            CREATE INDEX IF NOT EXISTS ix_player_identity_bindings_subject
                ON player_identity_bindings (platform_subject, season_id);
            CREATE TABLE IF NOT EXISTS player_identity_binding_history (
                history_id TEXT PRIMARY KEY,
                binding_id TEXT NOT NULL,
                platform_subject TEXT NOT NULL COLLATE NOCASE,
                season_id TEXT NOT NULL,
                world_id TEXT NOT NULL COLLATE NOCASE,
                player_uid TEXT NOT NULL COLLATE NOCASE,
                account_id TEXT NOT NULL,
                action TEXT NOT NULL CHECK (action IN ('bound', 'verified')),
                occurred_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_player_identity_binding_history_account
                ON player_identity_binding_history (account_id, occurred_at);
            CREATE INDEX IF NOT EXISTS ix_player_identity_binding_history_season
                ON player_identity_binding_history (season_id, occurred_at);
            """;
        command.ExecuteNonQuery();
    }

    private void BackfillExtractionRunCredits()
    {
        var credits = _ledger.Values
            .Where(entry =>
                entry.Currency == ExtractionCurrency.SeasonVoucher &&
                string.Equals(entry.ReferenceType, "extraction_run", StringComparison.Ordinal))
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.EntryId)
            .ToArray();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var ledger in credits)
        {
            if (!Guid.TryParseExact(ledger.ReferenceId, "N", out var runId) ||
                ledger.SeasonId is not Guid seasonId ||
                ledger.Delta <= 0)
            {
                throw new InvalidDataException(
                    $"Extraction ledger entry '{ledger.EntryId}' has an invalid immutable run reference.");
            }
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR IGNORE INTO extraction_run_credits (
                        run_id, account_id, season_id, ledger_entry_id, amount, created_at)
                    VALUES ($runId, $accountId, $seasonId, $ledgerEntryId, $amount, $createdAt);
                    """;
                insert.Parameters.AddWithValue("$runId", runId.ToString("D"));
                insert.Parameters.AddWithValue("$accountId", ledger.AccountId.ToString("D"));
                insert.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
                insert.Parameters.AddWithValue("$ledgerEntryId", ledger.EntryId.ToString("D"));
                insert.Parameters.AddWithValue("$amount", ledger.Delta);
                insert.Parameters.AddWithValue("$createdAt", ledger.CreatedAt.ToString("O"));
                insert.ExecuteNonQuery();
            }
            using var verify = connection.CreateCommand();
            verify.Transaction = transaction;
            verify.CommandText = """
                SELECT account_id, season_id, ledger_entry_id, amount
                FROM extraction_run_credits
                WHERE run_id = $runId;
                """;
            verify.Parameters.AddWithValue("$runId", runId.ToString("D"));
            using var reader = verify.ExecuteReader();
            if (!reader.Read() ||
                !Guid.TryParse(reader.GetString(0), out var storedAccountId) ||
                !Guid.TryParse(reader.GetString(1), out var storedSeasonId) ||
                !Guid.TryParse(reader.GetString(2), out var storedLedgerId) ||
                storedAccountId != ledger.AccountId ||
                storedSeasonId != seasonId ||
                storedLedgerId != ledger.EntryId ||
                reader.GetInt64(3) != ledger.Delta)
            {
                throw new InvalidDataException(
                    $"Extraction run '{runId}' has conflicting wallet ledger credits.");
            }
        }
        transaction.Commit();

        using var orphanCommand = connection.CreateCommand();
        orphanCommand.CommandText = "SELECT run_id, ledger_entry_id FROM extraction_run_credits;";
        using var orphanReader = orphanCommand.ExecuteReader();
        while (orphanReader.Read())
        {
            if (!Guid.TryParse(orphanReader.GetString(1), out var ledgerId) ||
                !_ledger.ContainsKey(ledgerId))
            {
                throw new InvalidDataException(
                    $"Extraction run credit '{orphanReader.GetString(0)}' references a missing ledger entry.");
            }
        }
    }

    private static async Task<PlayerIdentityBinding?> ReadPlayerIdentityBindingAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string predicate,
        Action<SqliteCommand> addParameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = $"""
            SELECT binding_id, platform_subject, season_id, world_id,
                   player_uid, account_id, first_bound_at, last_verified_at
            FROM player_identity_bindings
            WHERE {predicate}
            LIMIT 1;
            """;
        addParameters(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadPlayerIdentityBinding(reader)
            : null;
    }

    private static PlayerIdentityBinding ReadPlayerIdentityBinding(SqliteDataReader reader) => new(
        ParseStoredGuid(reader.GetString(0), "binding_id"),
        reader.GetString(1),
        ParseStoredGuid(reader.GetString(2), "season_id"),
        reader.GetString(3),
        reader.GetString(4),
        ParseStoredGuid(reader.GetString(5), "account_id"),
        ParseStoredTimestamp(reader.GetString(6), "first_bound_at"),
        ParseStoredTimestamp(reader.GetString(7), "last_verified_at"));

    private static PlayerIdentityBindingHistoryEntry ReadPlayerIdentityBindingHistory(
        SqliteDataReader reader) => new(
        ParseStoredGuid(reader.GetString(0), "history_id"),
        ParseStoredGuid(reader.GetString(1), "binding_id"),
        reader.GetString(2),
        ParseStoredGuid(reader.GetString(3), "season_id"),
        reader.GetString(4),
        reader.GetString(5),
        ParseStoredGuid(reader.GetString(6), "account_id"),
        reader.GetString(7),
        ParseStoredTimestamp(reader.GetString(8), "occurred_at"));

    private static async Task InsertPlayerIdentityBindingAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        PlayerIdentityBinding binding,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO player_identity_bindings (
                binding_id, platform_subject, season_id, world_id, player_uid,
                account_id, first_bound_at, last_verified_at)
            VALUES (
                $bindingId, $platformSubject, $seasonId, $worldId, $playerUid,
                $accountId, $firstBoundAt, $lastVerifiedAt);
            """;
        AddPlayerIdentityBindingParameters(command, binding);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdatePlayerIdentityBindingVerificationAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        PlayerIdentityBinding binding,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE player_identity_bindings
            SET last_verified_at = $lastVerifiedAt
            WHERE binding_id = $bindingId
              AND platform_subject = $platformSubject
              AND season_id = $seasonId
              AND world_id = $worldId
              AND player_uid = $playerUid
              AND account_id = $accountId;
            """;
        AddPlayerIdentityBindingParameters(command, binding);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Player identity binding '{binding.BindingId}' changed before verification.");
        }
    }

    private static async Task InsertPlayerIdentityBindingHistoryAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        PlayerIdentityBinding binding,
        string action,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO player_identity_binding_history (
                history_id, binding_id, platform_subject, season_id, world_id,
                player_uid, account_id, action, occurred_at)
            VALUES (
                $historyId, $bindingId, $platformSubject, $seasonId, $worldId,
                $playerUid, $accountId, $action, $occurredAt);
            """;
        command.Parameters.AddWithValue("$historyId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$bindingId", binding.BindingId.ToString("D"));
        command.Parameters.AddWithValue("$platformSubject", binding.PlatformSubject);
        command.Parameters.AddWithValue("$seasonId", binding.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$worldId", binding.WorldId);
        command.Parameters.AddWithValue("$playerUid", binding.PlayerUid);
        command.Parameters.AddWithValue("$accountId", binding.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$occurredAt", occurredAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddPlayerIdentityBindingParameters(
        SqliteCommand command,
        PlayerIdentityBinding binding)
    {
        command.Parameters.AddWithValue("$bindingId", binding.BindingId.ToString("D"));
        command.Parameters.AddWithValue("$platformSubject", binding.PlatformSubject);
        command.Parameters.AddWithValue("$seasonId", binding.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$worldId", binding.WorldId);
        command.Parameters.AddWithValue("$playerUid", binding.PlayerUid);
        command.Parameters.AddWithValue("$accountId", binding.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$firstBoundAt", binding.FirstBoundAt.ToString("O"));
        command.Parameters.AddWithValue("$lastVerifiedAt", binding.LastVerifiedAt.ToString("O"));
    }

    private static bool BindingMatches(
        PlayerIdentityBinding binding,
        string platformSubject,
        Guid seasonId,
        string worldId,
        string playerUid,
        Guid accountId) =>
        binding.AccountId == accountId &&
        binding.SeasonId == seasonId &&
        string.Equals(binding.PlatformSubject, platformSubject, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(binding.WorldId, worldId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(binding.PlayerUid, playerUid, StringComparison.OrdinalIgnoreCase);

    private static PlayerIdentityBindingResult BindingFailure(string code, string message) =>
        new(null, false, false, code, message);

    private static Guid ParseStoredGuid(string value, string columnName) =>
        Guid.TryParse(value, out var result) && result != Guid.Empty
            ? result
            : throw new InvalidDataException(
                $"Player identity binding column '{columnName}' contains an invalid GUID.");

    private static DateTimeOffset ParseStoredTimestamp(string value, string columnName) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var result)
            ? result
            : throw new InvalidDataException(
                $"Player identity binding column '{columnName}' contains an invalid timestamp.");

    private static IReadOnlyList<ExtractionSettlementRun> ReadAllSettlementRuns(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM extraction_settlement_runs ORDER BY updated_at, run_id;";
        using var reader = command.ExecuteReader();
        var runs = new List<ExtractionSettlementRun>();
        while (reader.Read())
        {
            runs.Add(DeserializeSettlementRun(reader.GetString(0)));
        }
        ValidateSettlementRuns(runs);
        return runs.Select(CloneSettlementRun).ToArray();
    }

    private static async Task<ExtractionSettlementRun?> ReadSettlementRunAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid runId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT payload FROM extraction_settlement_runs WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        var payload = await command.ExecuteScalarAsync(CancellationToken.None);
        return payload is string json ? DeserializeSettlementRun(json) : null;
    }

    private static SqliteCommand CreateSettlementRunInsertCommand(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ExtractionSettlementRun run)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO extraction_settlement_runs (
                run_id, account_id, season_id, user_id, state, revision, updated_at, payload)
            VALUES (
                $runId, $accountId, $seasonId, $userId, $state, $revision, $updatedAt, $payload);
            """;
        AddSettlementRunParameters(command, run);
        return command;
    }

    private static async Task UpdateSettlementRunAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ExtractionSettlementRun expected,
        ExtractionSettlementRun updated,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE extraction_settlement_runs
            SET account_id = $accountId,
                season_id = $seasonId,
                user_id = $userId,
                state = $state,
                revision = $revision,
                updated_at = $updatedAt,
                payload = $payload
            WHERE run_id = $runId
              AND revision = $expectedRevision
              AND state = $expectedState
              AND account_id = $expectedAccountId
              AND season_id = $expectedSeasonId
              AND user_id = $expectedUserId;
            """;
        AddSettlementRunParameters(command, updated);
        command.Parameters.AddWithValue("$expectedRevision", expected.Revision);
        command.Parameters.AddWithValue("$expectedState", expected.State.ToString());
        command.Parameters.AddWithValue("$expectedAccountId", expected.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$expectedSeasonId", expected.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$expectedUserId", expected.UserId);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (changed != 1)
        {
            throw new InvalidOperationException(
                $"Extraction run '{expected.RunId}' failed its SQLite state/revision compare-and-swap.");
        }
    }

    private static void AddSettlementRunParameters(
        SqliteCommand command,
        ExtractionSettlementRun run)
    {
        command.Parameters.AddWithValue("$runId", run.RunId.ToString("D"));
        command.Parameters.AddWithValue("$accountId", run.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$seasonId", run.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$userId", run.UserId);
        command.Parameters.AddWithValue("$state", run.State.ToString());
        command.Parameters.AddWithValue("$revision", run.Revision);
        command.Parameters.AddWithValue("$updatedAt", run.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(run, JsonOptions));
    }

    private async Task<WalletLedgerEntry?> ReadExtractionRunCreditAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ExtractionSettlementRun run,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            SELECT account_id, season_id, ledger_entry_id, amount
            FROM extraction_run_credits
            WHERE run_id = $runId;
            """;
        command.Parameters.AddWithValue("$runId", run.RunId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        if (!Guid.TryParse(reader.GetString(0), out var accountId) ||
            !Guid.TryParse(reader.GetString(1), out var seasonId) ||
            !Guid.TryParse(reader.GetString(2), out var ledgerId) ||
            accountId != run.AccountId ||
            seasonId != run.SeasonId ||
            reader.GetInt64(3) != run.TotalValue ||
            !_ledger.TryGetValue(ledgerId, out var ledger) ||
            ledger.AccountId != run.AccountId ||
            ledger.SeasonId != run.SeasonId ||
            ledger.Currency != ExtractionCurrency.SeasonVoucher ||
            ledger.Delta != run.TotalValue ||
            !string.Equals(ledger.ReferenceType, "extraction_run", StringComparison.Ordinal) ||
            !string.Equals(ledger.ReferenceId, run.RunId.ToString("N"), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Extraction run '{run.RunId}' has a conflicting unique credit row.");
        }
        return ledger;
    }

    private static async Task InsertExtractionRunCreditAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        ExtractionSettlementRun run,
        WalletLedgerEntry ledger,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO extraction_run_credits (
                run_id, account_id, season_id, ledger_entry_id, amount, created_at)
            VALUES ($runId, $accountId, $seasonId, $ledgerEntryId, $amount, $createdAt);
            """;
        command.Parameters.AddWithValue("$runId", run.RunId.ToString("D"));
        command.Parameters.AddWithValue("$accountId", run.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$seasonId", run.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$ledgerEntryId", ledger.EntryId.ToString("D"));
        command.Parameters.AddWithValue("$amount", ledger.Delta);
        command.Parameters.AddWithValue("$createdAt", ledger.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ExtractionSettlementRun DeserializeSettlementRun(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<ExtractionSettlementRun>(payload, JsonOptions)
                ?? throw new JsonException("The extraction settlement run is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A SQLite extraction settlement run is invalid JSON.", exception);
        }
    }

    private static void ValidateSettlementRuns(IEnumerable<ExtractionSettlementRun> runs)
    {
        HashSet<Guid> ids = [];
        foreach (var run in runs)
        {
            if (run.RunId == Guid.Empty ||
                run.AccountId == Guid.Empty ||
                run.SeasonId == Guid.Empty ||
                !ids.Add(run.RunId) ||
                run.Revision < 0 ||
                run.AttemptCount < 0 ||
                string.IsNullOrWhiteSpace(run.UserId))
            {
                throw new InvalidDataException("The extraction settlement run set contains invalid or duplicate rows.");
            }
        }
    }

    private static void ValidateSettlementRunWrites(
        IEnumerable<ExtractionSettlementRunWrite> writes)
    {
        var runIds = new HashSet<Guid>();
        foreach (var write in writes)
        {
            if (write is null || write.Run is null)
            {
                throw new InvalidDataException(
                    "The extraction settlement run write set contains a null write or run.");
            }

            ValidateSettlementRuns([write.Run]);
            if (!runIds.Add(write.Run.RunId))
            {
                throw new InvalidDataException(
                    "The extraction settlement run write set contains duplicate run ids.");
            }
            if (write.Expected is null)
            {
                continue;
            }

            ValidateSettlementRuns([write.Expected]);
            if (write.Expected.RunId != write.Run.RunId ||
                write.Expected.AccountId != write.Run.AccountId ||
                write.Expected.SeasonId != write.Run.SeasonId ||
                !string.Equals(
                    write.Expected.UserId,
                    write.Run.UserId,
                    StringComparison.Ordinal) ||
                write.Run.Revision < write.Expected.Revision)
            {
                throw new InvalidDataException(
                    $"Extraction settlement run '{write.Run.RunId}' has an invalid row-level update identity or revision.");
            }
        }
    }

    private static void EnsureMatchingCreditRun(
        ExtractionSettlementRun expected,
        ExtractionSettlementRun persisted)
    {
        if (persisted.RunId != expected.RunId ||
            persisted.AccountId != expected.AccountId ||
            persisted.SeasonId != expected.SeasonId ||
            persisted.TotalValue != expected.TotalValue ||
            !string.Equals(persisted.UserId, expected.UserId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Extraction run '{expected.RunId}' does not match its persisted credit identity.");
        }
    }

    private static ExtractionSettlementRun CloneSettlementRun(ExtractionSettlementRun run) => run with
    {
        Items = run.Items.ToArray(),
        PreDeleteTotals = run.PreDeleteTotals is null
            ? null
            : new Dictionary<string, long>(run.PreDeleteTotals, StringComparer.OrdinalIgnoreCase),
        DynamicEconomyEvidence = run.DynamicEconomyEvidence is null
            ? null
            : run.DynamicEconomyEvidence with
            {
                WorldEvents = run.DynamicEconomyEvidence.WorldEvents
                    .Select(worldEvent => worldEvent with { })
                    .ToArray()
            }
    };

    private void MigrateLegacyEventsIfNeeded()
    {
        if (!File.Exists(_legacyEventPath))
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(*) FROM extraction_events;";
            var eventCount = Convert.ToInt64(countCommand.ExecuteScalar());
            if (eventCount > 0)
            {
                return;
            }
            if (File.Exists(_authoritativeMarkerPath))
            {
                throw new InvalidDataException(
                    "The authoritative extraction SQLite database is empty; refusing to restore stale legacy JSONL.");
            }
        }

        RepairPartialLegacyFinalLine();
        var events = new List<(StoreEvent Event, string Payload)>();
        var eventIds = new HashSet<Guid>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(_legacyEventPath, Encoding.UTF8))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException($"Legacy extraction event line {lineNumber} is empty.");
            }
            var storeEvent = DeserializeStoreEvent(line, $"legacy JSONL line {lineNumber}");
            if (!eventIds.Add(storeEvent.EventId))
            {
                throw new InvalidDataException($"Duplicate legacy extraction event id '{storeEvent.EventId}'.");
            }
            events.Add((storeEvent, line));
        }
        if (events.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        foreach (var entry in events)
        {
            using var command = CreateInsertCommand(connection, transaction, entry.Event, entry.Payload);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private void EnsureAuthoritativeMarker()
    {
        if (File.Exists(_authoritativeMarkerPath))
        {
            return;
        }
        var tempPath = $"{_authoritativeMarkerPath}.{Guid.NewGuid():N}.tmp";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            authoritativeStore = Path.GetFileName(_databasePath),
            legacyStore = Path.GetFileName(_legacyEventPath),
            createdAt = DateTimeOffset.UtcNow
        }, JsonOptions);
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _authoritativeMarkerPath);
        }
        catch (IOException) when (File.Exists(_authoritativeMarkerPath))
        {
            // Another safe startup/write may have won the atomic marker creation race.
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static SqliteCommand CreateInsertCommand(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        StoreEvent storeEvent,
        string payload)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO extraction_events (event_id, event_type, occurred_at, payload)
            VALUES ($eventId, $eventType, $occurredAt, $payload);
            """;
        command.Parameters.AddWithValue("$eventId", storeEvent.EventId.ToString("D"));
        command.Parameters.AddWithValue("$eventType", storeEvent.EventType);
        command.Parameters.AddWithValue("$occurredAt", storeEvent.At.ToString("O"));
        command.Parameters.AddWithValue("$payload", payload);
        return command;
    }

    private static StoreEvent DeserializeStoreEvent(string payload, string location)
    {
        StoreEvent storeEvent;
        try
        {
            storeEvent = JsonSerializer.Deserialize<StoreEvent>(payload, JsonOptions)
                ?? throw new JsonException("The extraction event is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Extraction event at {location} is invalid JSON.", exception);
        }
        if (storeEvent.SchemaVersion != StoreSchemaVersion)
        {
            throw new InvalidDataException(
                $"Extraction event at {location} uses unsupported schema version {storeEvent.SchemaVersion}.");
        }
        return storeEvent;
    }

    private void ApplyEvent(StoreEvent storeEvent)
    {
        if (!_eventIds.Add(storeEvent.EventId))
        {
            throw new InvalidDataException($"Duplicate extraction event id '{storeEvent.EventId}'.");
        }
        if (storeEvent.Season is not null)
        {
            _seasons[storeEvent.Season.SeasonId] = CloneSeason(storeEvent.Season);
        }
        if (storeEvent.Account is not null)
        {
            var account = CloneAccount(storeEvent.Account);
            _accounts[account.AccountId] = account;
            _accountIdentities[AccountIdentityKey(account.IdentityProvider, account.ExternalUserId)] = account.AccountId;
        }
        if (storeEvent.Balances is not null)
        {
            foreach (var balance in storeEvent.Balances)
            {
                var clone = CloneBalance(balance);
                _balances[BalanceKey(clone.AccountId, clone.Currency, clone.SeasonId)] = clone;
            }
        }
        if (storeEvent.LedgerEntries is not null)
        {
            foreach (var entry in storeEvent.LedgerEntries)
            {
                _ledger[entry.EntryId] = entry;
            }
        }
        if (storeEvent.Product is not null)
        {
            var product = CloneProduct(storeEvent.Product);
            _products[product.Sku] = product;
        }
        if (storeEvent.Order is not null)
        {
            var order = CloneOrder(storeEvent.Order);
            _orders[order.OrderId] = order;
        }
        if (storeEvent.Delivery is not null)
        {
            _deliveries[storeEvent.Delivery.DeliveryId] = storeEvent.Delivery;
        }
        if (storeEvent.Idempotency is not null)
        {
            if (_idempotency.TryGetValue(storeEvent.Idempotency.Scope, out var existing) &&
                (!string.Equals(existing.RequestHash, storeEvent.Idempotency.RequestHash, StringComparison.Ordinal) ||
                 existing.ResourceId != storeEvent.Idempotency.ResourceId))
            {
                throw new InvalidDataException(
                    $"Extraction idempotency scope '{storeEvent.Idempotency.Scope}' has conflicting events.");
            }
            _idempotency[storeEvent.Idempotency.Scope] = storeEvent.Idempotency;
        }
    }

    private void RepairPartialLegacyFinalLine()
    {
        if (!File.Exists(_legacyEventPath))
        {
            return;
        }
        var bytes = File.ReadAllBytes(_legacyEventPath);
        if (bytes.Length == 0 || bytes[^1] == (byte)'\n')
        {
            return;
        }
        var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        using var stream = new FileStream(
            _legacyEventPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        stream.SetLength(lastNewline < 0 ? 0 : lastNewline + 1);
        stream.Flush(flushToDisk: true);
    }

    private void EnsureWritable()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        transaction.Rollback();
    }

    private static StoreEvent NewEvent(
        string eventType,
        DateTimeOffset at,
        ExtractionSeason? season = null,
        ExtractionAccount? account = null,
        IEnumerable<WalletBalance>? balances = null,
        IEnumerable<WalletLedgerEntry>? ledgerEntries = null,
        ShopProduct? product = null,
        ShopOrder? order = null,
        ShopDelivery? delivery = null,
        StoredIdempotency? idempotency = null) => new()
        {
            SchemaVersion = StoreSchemaVersion,
            EventId = Guid.NewGuid(),
            EventType = eventType,
            At = at,
            Season = season,
            Account = account,
            Balances = balances?.ToArray(),
            LedgerEntries = ledgerEntries?.ToArray(),
            Product = product,
            Order = order,
            Delivery = delivery,
            Idempotency = idempotency
        };

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isReady)
        {
            throw new IOException("The extraction commerce event store is not ready.");
        }
    }

    private void RequireAccount(Guid accountId)
    {
        if (!_accounts.ContainsKey(accountId))
        {
            throw new KeyNotFoundException($"Extraction account '{accountId}' does not exist.");
        }
    }

    private ExtractionAccount RequireAccountValue(Guid accountId)
    {
        RequireAccount(accountId);
        return _accounts[accountId];
    }

    private void RequireSeason(Guid seasonId)
    {
        if (!_seasons.ContainsKey(seasonId))
        {
            throw new KeyNotFoundException($"Extraction season '{seasonId}' does not exist.");
        }
    }

    private ExtractionSeason RequireSeasonValue(Guid seasonId)
    {
        RequireSeason(seasonId);
        return _seasons[seasonId];
    }

    private static Guid? ScopeSeasonId(ExtractionCurrency currency, Guid? seasonId) =>
        currency == ExtractionCurrency.MarketCoin
            ? null
            : seasonId ?? throw new ArgumentException("SeasonVoucher requires a season id.", nameof(seasonId));

    private static string AccountIdentityKey(string provider, string userId) => $"{provider}\n{userId}";

    private static string BalanceKey(
        Guid accountId,
        ExtractionCurrency currency,
        Guid? seasonId) => $"{accountId:N}\n{currency}\n{seasonId?.ToString("N") ?? "permanent"}";

    private static string IdempotencyScope(string operation, Guid accountId, string key) =>
        $"{operation}\n{accountId:N}\n{key}";

    private static string DeliveryIdempotencyKey(Guid orderId, int attempt) =>
        $"shop-delivery:{orderId:N}:{attempt}";

    private static string NormalizeSku(string value)
    {
        var sku = NormalizeRequired(value, 64, nameof(value)).ToUpperInvariant();
        if (sku.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "SKU may contain only ASCII letters, digits, '-', '_' and '.'.",
                nameof(value));
        }
        return sku;
    }

    private static string NormalizeIdempotencyKey(string value)
    {
        var key = NormalizeRequired(value, 128, nameof(value));
        if (key.Length < 8 || key.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Idempotency key must contain 8 to 128 non-control characters.",
                nameof(value));
        }
        return key;
    }

    private static string NormalizePlatformSubject(string value) =>
        NormalizeRequired(value, 128, nameof(value)).ToLowerInvariant();

    private static string NormalizeBindingWorldId(string value)
    {
        var normalized = NormalizeRequired(value, 32, nameof(value));
        if (!Guid.TryParseExact(normalized, "N", out var worldId) || worldId == Guid.Empty)
        {
            throw new ArgumentException(
                "A binding world id must be a complete 32-character GUID.",
                nameof(value));
        }
        return worldId.ToString("N").ToUpperInvariant();
    }

    private static string NormalizePlayerUid(string value) =>
        TryNormalizePlayerUid(NormalizeRequired(value, 64, nameof(value)))
        ?? throw new ArgumentException(
            "PlayerUID must be a complete non-empty GUID; short player ids are not accepted.",
            nameof(value));

    private static string? TryNormalizePlayerUid(string value) =>
        Guid.TryParse(value.Trim(), out var playerUid) && playerUid != Guid.Empty
            ? playerUid.ToString("N").ToLowerInvariant()
            : null;

    private static string NormalizeRequired(string value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maxLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{parameterName} must contain at most {maxLength} non-control characters.",
                parameterName);
        }
        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maxLength, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? null : NormalizeRequired(value, maxLength, parameterName);

    private static void VerifyRevision(long? actualRevision, long? expectedRevision, string resourceName)
    {
        if (expectedRevision is null)
        {
            return;
        }
        var actual = actualRevision ?? 0;
        if (actual != expectedRevision.Value)
        {
            throw new InvalidOperationException(
                $"The {resourceName} revision changed (expected {expectedRevision}, actual {actual}).");
        }
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow();

    private static ExtractionSeason CloneSeason(ExtractionSeason season) => season with { };

    private static ExtractionAccount CloneAccount(ExtractionAccount account) => account with { };

    private static PlayerIdentityBinding ClonePlayerIdentityBinding(
        PlayerIdentityBinding binding) => binding with { };

    private static WalletBalance CloneBalance(WalletBalance balance) => balance with { };

    private static ShopProduct CloneProduct(ShopProduct product) => product with
    {
        ItemGrants = product.ItemGrants.ToArray(),
        Category = string.IsNullOrWhiteSpace(product.Category) ? "legacy" : product.Category,
        Tags = product.Tags?.ToArray() ?? [],
        ContentHash = string.IsNullOrWhiteSpace(product.ContentHash)
            ? null
            : product.ContentHash
    };

    private static ShopOrder CloneOrder(ShopOrder order) => order with
    {
        Lines = order.Lines.Select(line => line with
        {
            ItemGrants = line.ItemGrants.ToArray(),
            Category = string.IsNullOrWhiteSpace(line.Category) ? "legacy" : line.Category,
            Tags = line.Tags?.ToArray() ?? [],
            ContentHash = string.IsNullOrWhiteSpace(line.ContentHash)
                ? null
                : line.ContentHash
        }).ToArray(),
        Charges = order.Charges.ToArray()
    };

    private static WalletAdjustmentResult WalletFailure(string code, string message) =>
        new(null, null, false, false, code, message);

    private static ShopPurchaseResult PurchaseFailure(
        string code,
        string message,
        bool idempotencyConflict = false) =>
        new(null, null, false, idempotencyConflict, code, message);

    private static ShopDeliveryUpdateResult DeliveryFailure(
        string code,
        string message,
        ShopOrder? order = null,
        ShopDelivery? delivery = null) =>
        new(order is null ? null : CloneOrder(order), delivery, false, code, message);

    private static ShopRefundResult RefundFailure(
        string code,
        string message,
        ShopOrder? order = null) =>
        new(order is null ? null : CloneOrder(order), [], false, false, code, message);

    private sealed record ContentOfferExpectation(
        string ServerId,
        Guid VersionId,
        string ContentHash);

    private sealed record StoredIdempotency(
        string Scope,
        string RequestHash,
        string ResourceType,
        Guid ResourceId,
        DateTimeOffset CreatedAt);

    private sealed class StoreEvent
    {
        public required int SchemaVersion { get; init; }
        public required Guid EventId { get; init; }
        public required string EventType { get; init; }
        public required DateTimeOffset At { get; init; }
        public ExtractionSeason? Season { get; init; }
        public ExtractionAccount? Account { get; init; }
        public IReadOnlyList<WalletBalance>? Balances { get; init; }
        public IReadOnlyList<WalletLedgerEntry>? LedgerEntries { get; init; }
        public ShopProduct? Product { get; init; }
        public ShopOrder? Order { get; init; }
        public ShopDelivery? Delivery { get; init; }
        public StoredIdempotency? Idempotency { get; init; }
    }
}
