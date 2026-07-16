using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

/// <summary>
/// Team membership, invitations and read-only economy projections. This store
/// shares the authoritative economy SQLite backup boundary, but it never
/// creates wallet, order, settlement, inventory or task facts.
/// </summary>
public sealed partial class TeamEconomyStore : IDisposable, IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private readonly TeamEconomyOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, bool> _isSubjectBanned;
    private readonly byte[] _pepper;
    private bool _disposed;

    public TeamEconomyStore(
        IOptions<ExtractionPersistenceOptions> persistence,
        IOptions<TeamEconomyOptions> options,
        IWebHostEnvironment environment,
        TimeProvider timeProvider,
        PlayerIdentitySecurityService identitySecurityService)
        : this(
            ResolveDataDirectory(persistence.Value.DataDirectory, environment.ContentRootPath),
            options.Value,
            timeProvider,
            identitySecurityService.IsVolatileBanned)
    {
        // Resolving the identity service also initializes its authoritative
        // ban schema before the team projection worker reads the shared DB.
        ArgumentNullException.ThrowIfNull(identitySecurityService);
    }

    public TeamEconomyStore(
        string dataDirectory,
        TeamEconomyOptions options,
        TimeProvider? timeProvider = null,
        Func<string, bool>? isSubjectBanned = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsValid(out var error))
        {
            throw new ArgumentException(error, nameof(options));
        }
        var directory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "extraction-commerce.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isSubjectBanned = isSubjectBanned ?? (static _ => false);
        _pepper = Encoding.UTF8.GetBytes(options.InvitePepper);
        Initialize();
    }

    public async Task<TeamEconomyMutationResponse> CreateTeamAsync(
        string serverId,
        Guid seasonId,
        Guid accountId,
        string name,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var server = NormalizeServer(serverId);
        ValidateIdentity(seasonId, accountId);
        var (displayName, normalizedName) = NormalizeTeamName(name);
        var requestHash = HashCanonical("create", server, seasonId, accountId, normalizedName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await TryReplayAsync<TeamEconomyMutationResponse>(
                connection, transaction, "create-team", server, seasonId, accountId,
                idempotencyKey, requestHash, cancellationToken);
            if (replay is not null)
            {
                transaction.Commit();
                return replay with { Replayed = true };
            }
            await EnsureSeasonScopeAsync(connection, transaction, server, seasonId, cancellationToken);
            if (await FindActiveMembershipAsync(
                    connection, transaction, server, seasonId, accountId, cancellationToken) is not null)
            {
                throw Conflict("TEAM_ALREADY_JOINED", "The authenticated account already belongs to a team in this weekly world.");
            }
            if (await TeamNameExistsAsync(
                    connection, transaction, server, seasonId, normalizedName, cancellationToken))
            {
                throw Conflict("TEAM_NAME_TAKEN", "That team name is already used in this weekly world.");
            }
            var now = UtcNow();
            var teamId = Guid.NewGuid();
            var team = new StoredTeam(
                teamId, server, seasonId, displayName, normalizedName,
                accountId, TeamEconomyStatus.Active, now, now, null, string.Empty);
            team = team with { RowHash = TeamHash(team) };
            await InsertTeamAsync(connection, transaction, team, cancellationToken);
            var membership = NewMembership(team, accountId, now);
            await InsertMembershipAsync(connection, transaction, membership, cancellationToken);
            await InsertGoalSnapshotAsync(connection, transaction, team, now, cancellationToken);
            var response = new TeamEconomyMutationResponse(
                teamId, displayName, TeamEconomyStatus.Active, 1, true, false, now);
            await SaveReplayAsync(
                connection, transaction, "create-team", server, seasonId, accountId,
                idempotencyKey, requestHash, response, cancellationToken);
            transaction.Commit();
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TeamEconomyInviteResponse> RotateInviteAsync(
        string serverId,
        Guid seasonId,
        Guid accountId,
        int? requestedMaximumUses,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var server = NormalizeServer(serverId);
        ValidateIdentity(seasonId, accountId);
        var maximumUses = requestedMaximumUses ?? _options.InviteMaximumUses;
        if (maximumUses is < 1 or > 100)
        {
            throw BadRequest("TEAM_INVITE_USES_INVALID", "Invitation maximum uses must be between 1 and 100.");
        }
        var requestHash = HashCanonical("rotate", server, seasonId, accountId, maximumUses);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await TryReplayAsync<TeamEconomyInviteResponse>(
                connection, transaction, "rotate-invite", server, seasonId, accountId,
                idempotencyKey, requestHash, cancellationToken);
            if (replay is not null)
            {
                transaction.Commit();
                return replay with { Token = null, TokenShown = false, Replayed = true };
            }
            var team = await RequireOwnedTeamAsync(
                connection, transaction, server, seasonId, accountId, cancellationToken);
            var now = UtcNow();
            await RevokeInvitesAsync(connection, transaction, team.TeamId, now, cancellationToken);
            var inviteId = Guid.NewGuid();
            var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
            var token = $"tm1.{inviteId:N}.{secret}";
            var digest = SecretDigest("invite", token);
            var expiresAt = now.AddMinutes(_options.InviteLifetimeMinutes);
            var invite = new StoredInvite(
                inviteId, team.TeamId, digest, 1, now, expiresAt,
                maximumUses, 0, null, string.Empty);
            invite = invite with { RowHash = InviteHash(invite) };
            await InsertInviteAsync(connection, transaction, invite, cancellationToken);
            var response = new TeamEconomyInviteResponse(
                team.TeamId, inviteId, token, true, expiresAt,
                maximumUses, maximumUses, false);
            // The durable replay deliberately omits the one-time bearer token.
            await SaveReplayAsync(
                connection, transaction, "rotate-invite", server, seasonId, accountId,
                idempotencyKey, requestHash,
                response with { Token = null, TokenShown = false }, cancellationToken);
            transaction.Commit();
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TeamEconomyMutationResponse> JoinAsync(
        string serverId,
        Guid seasonId,
        Guid accountId,
        string invitationToken,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var server = NormalizeServer(serverId);
        ValidateIdentity(seasonId, accountId);
        var (inviteId, token) = ParseInvitation(invitationToken);
        // Hash the bearer before entering persistence. The raw token is never logged or stored.
        var tokenDigest = SecretDigest("invite", token);
        var requestHash = HashCanonical("join", server, seasonId, accountId, tokenDigest);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await TryReplayAsync<TeamEconomyMutationResponse>(
                connection, transaction, "join-team", server, seasonId, accountId,
                idempotencyKey, requestHash, cancellationToken);
            if (replay is not null)
            {
                transaction.Commit();
                return replay with { Replayed = true };
            }
            if (await FindActiveMembershipAsync(
                    connection, transaction, server, seasonId, accountId, cancellationToken) is not null)
            {
                throw Conflict("TEAM_ALREADY_JOINED", "The authenticated account already belongs to a team in this weekly world.");
            }
            var invite = await ReadInviteAsync(connection, transaction, inviteId, cancellationToken)
                ?? throw NotFound("TEAM_INVITE_INVALID", "The invitation is invalid, expired, revoked, or exhausted.");
            ValidateInvite(invite);
            if (!FixedEquals(invite.TokenDigest, tokenDigest))
            {
                throw NotFound("TEAM_INVITE_INVALID", "The invitation is invalid, expired, revoked, or exhausted.");
            }
            var team = await ReadTeamAsync(connection, transaction, invite.TeamId, cancellationToken)
                ?? throw InvalidStore("TEAM_STORE_CORRUPT", "An invitation references a missing team.");
            ValidateTeam(team);
            if (team.Status != TeamEconomyStatus.Active ||
                !string.Equals(team.ServerId, server, StringComparison.Ordinal) ||
                team.SeasonId != seasonId)
            {
                throw NotFound("TEAM_INVITE_INVALID", "The invitation is invalid, expired, revoked, or exhausted.");
            }
            if (await IsAccountExcludedAsync(
                    connection,
                    transaction,
                    seasonId,
                    team.OwnerAccountId,
                    cancellationToken))
            {
                throw Conflict(
                    "TEAM_INVITE_OWNER_INELIGIBLE",
                    "The team owner is currently excluded; this invitation cannot be used until an audited unban or exclusion removal restores management.");
            }
            var now = UtcNow();
            if (invite.RevokedAt is not null || invite.ExpiresAt <= now ||
                invite.UseCount >= invite.MaximumUses)
            {
                throw Conflict("TEAM_INVITE_EXPIRED", "The invitation has expired, was rotated, or has no uses remaining.");
            }
            var membership = NewMembership(team, accountId, now);
            await InsertMembershipAsync(connection, transaction, membership, cancellationToken);
            var updatedInvite = invite with { UseCount = checked(invite.UseCount + 1) };
            updatedInvite = updatedInvite with { RowHash = InviteHash(updatedInvite) };
            await UpdateInviteAsync(connection, transaction, updatedInvite, cancellationToken);
            var memberCount = await CountActiveMembersAsync(
                connection, transaction, team.TeamId, now, cancellationToken);
            var response = new TeamEconomyMutationResponse(
                team.TeamId, team.DisplayName, team.Status,
                memberCount, false, false, now);
            await SaveReplayAsync(
                connection, transaction, "join-team", server, seasonId, accountId,
                idempotencyKey, requestHash, response, cancellationToken);
            transaction.Commit();
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<TeamEconomyMutationResponse> LeaveAsync(
        string serverId,
        Guid seasonId,
        Guid accountId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        CloseMembershipAsync(
            "leave-team", serverId, seasonId, accountId, null,
            idempotencyKey, cancellationToken);

    public async Task<TeamEconomyMutationResponse> TransferOwnershipAsync(
        string serverId,
        Guid seasonId,
        Guid accountId,
        string memberHandle,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var server = NormalizeServer(serverId);
        ValidateIdentity(seasonId, accountId);
        ValidateMemberHandle(memberHandle);
        var requestHash = HashCanonical(
            "transfer", server, seasonId, accountId, memberHandle.ToLowerInvariant());
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await TryReplayAsync<TeamEconomyMutationResponse>(
                connection, transaction, "transfer-owner", server, seasonId, accountId,
                idempotencyKey, requestHash, cancellationToken);
            if (replay is not null)
            {
                transaction.Commit();
                return replay with { Replayed = true };
            }
            var team = await RequireOwnedTeamAsync(
                connection, transaction, server, seasonId, accountId, cancellationToken);
            var members = await ListActiveMembershipsAsync(
                connection, transaction, team.TeamId, UtcNow(), cancellationToken);
            var target = members.FirstOrDefault(member =>
                member.AccountId != accountId &&
                FixedEquals(MemberHandle(team.TeamId, member.AccountId), memberHandle));
            if (target is null)
            {
                throw NotFound("TEAM_MEMBER_NOT_FOUND", "The selected active team member was not found.");
            }
            if (await IsAccountExcludedAsync(
                    connection,
                    transaction,
                    seasonId,
                    target.AccountId,
                    cancellationToken))
            {
                throw Conflict(
                    "TEAM_MEMBER_INELIGIBLE",
                    "Team ownership cannot be transferred to an excluded member.");
            }
            var now = UtcNow();
            var updated = team with { OwnerAccountId = target.AccountId, UpdatedAt = now };
            updated = updated with { RowHash = TeamHash(updated) };
            await UpdateTeamAsync(connection, transaction, updated, cancellationToken);
            var response = new TeamEconomyMutationResponse(
                team.TeamId, team.DisplayName, team.Status, members.Count,
                false, false, now);
            await SaveReplayAsync(
                connection, transaction, "transfer-owner", server, seasonId, accountId,
                idempotencyKey, requestHash, response, cancellationToken);
            transaction.Commit();
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<TeamEconomyMutationResponse> DissolveAsync(
        string serverId,
        Guid seasonId,
        Guid accountId,
        string confirmation,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        CloseMembershipAsync(
            "dissolve-team", serverId, seasonId, accountId, confirmation,
            idempotencyKey, cancellationToken);

    private async Task<TeamEconomyMutationResponse> CloseMembershipAsync(
        string operation,
        string serverId,
        Guid seasonId,
        Guid accountId,
        string? confirmation,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var server = NormalizeServer(serverId);
        ValidateIdentity(seasonId, accountId);
        var normalizedConfirmation = confirmation?.Trim() ?? string.Empty;
        var requestHash = HashCanonical(
            operation, server, seasonId, accountId, normalizedConfirmation);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var replay = await TryReplayAsync<TeamEconomyMutationResponse>(
                connection, transaction, operation, server, seasonId, accountId,
                idempotencyKey, requestHash, cancellationToken);
            if (replay is not null)
            {
                transaction.Commit();
                return replay with { Replayed = true };
            }
            var membership = await FindActiveMembershipAsync(
                connection, transaction, server, seasonId, accountId, cancellationToken)
                ?? throw NotFound("TEAM_NOT_JOINED", "The authenticated account is not in a team for this weekly world.");
            var team = await ReadTeamAsync(connection, transaction, membership.TeamId, cancellationToken)
                ?? throw InvalidStore("TEAM_STORE_CORRUPT", "An active membership references a missing team.");
            ValidateTeam(team);
            var now = UtcNow();
            var members = await ListActiveMembershipsAsync(
                connection, transaction, team.TeamId, now, cancellationToken);
            if (operation == "leave-team")
            {
                if (team.OwnerAccountId == accountId)
                {
                    throw Conflict(
                        "TEAM_OWNER_MUST_TRANSFER_OR_DISSOLVE",
                        members.Count > 1
                            ? "Transfer ownership before leaving the team."
                            : "The owner must explicitly dissolve the team.");
                }
                var closed = membership with { LeftAt = now };
                closed = closed with { RowHash = MembershipHash(closed) };
                await UpdateMembershipAsync(connection, transaction, closed, cancellationToken);
            }
            else
            {
                if (team.OwnerAccountId != accountId)
                {
                    throw Forbidden("TEAM_OWNER_REQUIRED", "Only the current team owner may dissolve the team.");
                }
                if (!string.Equals(normalizedConfirmation, team.DisplayName, StringComparison.Ordinal))
                {
                    throw BadRequest(
                        "TEAM_DISSOLVE_CONFIRMATION_REQUIRED",
                        "Type the exact team name to confirm dissolution.");
                }
                foreach (var member in members)
                {
                    var closed = member with { LeftAt = now };
                    closed = closed with { RowHash = MembershipHash(closed) };
                    await UpdateMembershipAsync(connection, transaction, closed, cancellationToken);
                }
                var dissolved = team with
                {
                    Status = TeamEconomyStatus.Dissolved,
                    UpdatedAt = now,
                    DissolvedAt = now
                };
                dissolved = dissolved with { RowHash = TeamHash(dissolved) };
                await UpdateTeamAsync(connection, transaction, dissolved, cancellationToken);
                await RevokeInvitesAsync(connection, transaction, team.TeamId, now, cancellationToken);
                team = dissolved;
            }
            var memberCount = operation == "dissolve-team"
                ? 0
                : await CountActiveMembersAsync(
                    connection, transaction, team.TeamId, now, cancellationToken);
            var response = new TeamEconomyMutationResponse(
                team.TeamId, team.DisplayName, team.Status, memberCount,
                false, false, now);
            await SaveReplayAsync(
                connection, transaction, operation, server, seasonId, accountId,
                idempotencyKey, requestHash, response, cancellationToken);
            transaction.Commit();
            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<TeamEconomyScope>> ListScopesAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT server_id, season_id
                FROM team_economy_teams
                ORDER BY server_id, season_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var scopes = new List<TeamEconomyScope>();
            while (await reader.ReadAsync(cancellationToken))
            {
                scopes.Add(new TeamEconomyScope(
                    reader.GetString(0), RequiredGuid(reader.GetString(1), "team season")));
            }
            return scopes;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS team_economy_schema (
                component TEXT PRIMARY KEY,
                version INTEGER NOT NULL CHECK (version > 0),
                applied_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS team_economy_teams (
                team_id TEXT PRIMARY KEY,
                server_id TEXT NOT NULL COLLATE NOCASE,
                season_id TEXT NOT NULL,
                display_name TEXT NOT NULL,
                normalized_name TEXT NOT NULL COLLATE NOCASE,
                owner_account_id TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('Active', 'Dissolved')),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                dissolved_at TEXT NULL,
                row_hash TEXT NOT NULL CHECK (length(row_hash) = 64),
                UNIQUE(server_id, season_id, normalized_name)
            );
            CREATE TABLE IF NOT EXISTS team_economy_memberships (
                membership_id TEXT PRIMARY KEY,
                team_id TEXT NOT NULL REFERENCES team_economy_teams(team_id),
                server_id TEXT NOT NULL COLLATE NOCASE,
                season_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                joined_at TEXT NOT NULL,
                left_at TEXT NULL,
                row_hash TEXT NOT NULL CHECK (length(row_hash) = 64)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_team_active_membership
                ON team_economy_memberships(server_id, season_id, account_id)
                WHERE left_at IS NULL;
            CREATE INDEX IF NOT EXISTS ix_team_membership_period
                ON team_economy_memberships(team_id, joined_at, left_at);
            CREATE TABLE IF NOT EXISTS team_economy_invites (
                invite_id TEXT PRIMARY KEY,
                team_id TEXT NOT NULL REFERENCES team_economy_teams(team_id),
                token_digest TEXT NOT NULL UNIQUE CHECK (length(token_digest) = 64),
                token_version INTEGER NOT NULL CHECK (token_version = 1),
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                maximum_uses INTEGER NOT NULL CHECK (maximum_uses BETWEEN 1 AND 100),
                use_count INTEGER NOT NULL CHECK (use_count BETWEEN 0 AND maximum_uses),
                revoked_at TEXT NULL,
                row_hash TEXT NOT NULL CHECK (length(row_hash) = 64)
            );
            CREATE INDEX IF NOT EXISTS ix_team_invites_team
                ON team_economy_invites(team_id, expires_at);
            CREATE TABLE IF NOT EXISTS team_economy_goal_snapshots (
                snapshot_id TEXT PRIMARY KEY,
                team_id TEXT NOT NULL UNIQUE REFERENCES team_economy_teams(team_id),
                server_id TEXT NOT NULL COLLATE NOCASE,
                season_id TEXT NOT NULL,
                template_version TEXT NOT NULL,
                goals_json TEXT NOT NULL,
                snapshot_hash TEXT NOT NULL CHECK (length(snapshot_hash) = 64),
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS team_economy_idempotency (
                operation_key_digest TEXT PRIMARY KEY CHECK (length(operation_key_digest) = 64),
                operation_scope TEXT NOT NULL,
                server_id TEXT NOT NULL COLLATE NOCASE,
                season_id TEXT NOT NULL,
                actor_account_id TEXT NOT NULL,
                request_hash TEXT NOT NULL CHECK (length(request_hash) = 64),
                response_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS team_economy_projection_events (
                event_key TEXT PRIMARY KEY CHECK (length(event_key) = 64),
                server_id TEXT NOT NULL COLLATE NOCASE,
                season_id TEXT NOT NULL,
                team_id TEXT NOT NULL REFERENCES team_economy_teams(team_id),
                membership_id TEXT NOT NULL REFERENCES team_economy_memberships(membership_id),
                account_id TEXT NOT NULL,
                source_kind TEXT NOT NULL CHECK (source_kind IN ('settlement', 'delivery', 'task')),
                source_id TEXT NOT NULL,
                source_hash TEXT NOT NULL CHECK (length(source_hash) = 64),
                occurred_at TEXT NOT NULL,
                resource_items INTEGER NOT NULL CHECK (resource_items >= 0),
                resource_value INTEGER NOT NULL CHECK (resource_value >= 0),
                task_points INTEGER NOT NULL CHECK (task_points >= 0),
                delivered_orders INTEGER NOT NULL CHECK (delivered_orders IN (0, 1)),
                currency_spent INTEGER NOT NULL CHECK (currency_spent >= 0),
                zone_id TEXT NULL,
                row_hash TEXT NOT NULL CHECK (length(row_hash) = 64)
            );
            CREATE INDEX IF NOT EXISTS ix_team_projection_scope
                ON team_economy_projection_events(server_id, season_id, team_id, occurred_at);
            CREATE INDEX IF NOT EXISTS ix_team_projection_account
                ON team_economy_projection_events(server_id, season_id, account_id, occurred_at);
            CREATE TABLE IF NOT EXISTS team_economy_projection_exclusions (
                server_id TEXT NOT NULL COLLATE NOCASE,
                season_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                row_hash TEXT NOT NULL CHECK (length(row_hash) = 64),
                PRIMARY KEY(server_id, season_id, account_id)
            );
            CREATE TABLE IF NOT EXISTS team_economy_goal_progress (
                team_id TEXT NOT NULL REFERENCES team_economy_teams(team_id),
                goal_kind TEXT NOT NULL,
                maximum_progress INTEGER NOT NULL CHECK (maximum_progress >= 0),
                reached_at TEXT NULL,
                PRIMARY KEY(team_id, goal_kind)
            );
            CREATE TABLE IF NOT EXISTS team_economy_projection_state (
                server_id TEXT NOT NULL COLLATE NOCASE,
                season_id TEXT NOT NULL,
                cutoff_at TEXT NOT NULL,
                source_hash TEXT NOT NULL CHECK (length(source_hash) = 64),
                snapshot_hash TEXT NOT NULL CHECK (length(snapshot_hash) = 64),
                projected_event_count INTEGER NOT NULL CHECK (projected_event_count >= 0),
                updated_at TEXT NOT NULL,
                state_hash TEXT NOT NULL CHECK (length(state_hash) = 64),
                PRIMARY KEY(server_id, season_id)
            );
            CREATE TABLE IF NOT EXISTS team_economy_projection_failures (
                server_id TEXT NOT NULL COLLATE NOCASE,
                season_id TEXT NOT NULL,
                error_code TEXT NOT NULL,
                failed_at TEXT NOT NULL,
                PRIMARY KEY(server_id, season_id)
            );
            INSERT OR IGNORE INTO team_economy_schema(component, version, applied_at)
            VALUES ('team-economy', 1, $appliedAt);
            """;
        command.Parameters.AddWithValue("$appliedAt", UtcNow().ToString("O"));
        command.ExecuteNonQuery();
        using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT version FROM team_economy_schema WHERE component = 'team-economy';";
        if (Convert.ToInt32(verify.ExecuteScalar(), CultureInfo.InvariantCulture) != SchemaVersion)
        {
            throw new InvalidDataException("The team-economy SQLite schema version is unsupported.");
        }
    }

    private async Task InsertGoalSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredTeam team,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var goals = GoalDefinitions();
        var goalsJson = JsonSerializer.Serialize(goals, JsonOptions);
        var snapshotId = Guid.NewGuid();
        var snapshotHash = HashCanonical(
            "goal-snapshot-v1", snapshotId, team.TeamId, team.ServerId,
            team.SeasonId, _options.GoalTemplateVersion.Trim(), goalsJson, now);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO team_economy_goal_snapshots(
                snapshot_id, team_id, server_id, season_id, template_version,
                goals_json, snapshot_hash, created_at)
            VALUES(
                $snapshotId, $teamId, $serverId, $seasonId, $templateVersion,
                $goalsJson, $snapshotHash, $createdAt);
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId.ToString("D"));
        command.Parameters.AddWithValue("$teamId", team.TeamId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", team.ServerId);
        command.Parameters.AddWithValue("$seasonId", team.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$templateVersion", _options.GoalTemplateVersion.Trim());
        command.Parameters.AddWithValue("$goalsJson", goalsJson);
        command.Parameters.AddWithValue("$snapshotHash", snapshotHash);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private IReadOnlyList<TeamEconomyGoalDefinition> GoalDefinitions() =>
    [
        new(TeamEconomyGoalKind.ResourceItems, "资源总件数", _options.ResourceItemsGoal, "件"),
        new(TeamEconomyGoalKind.ResourceValue, "兑换总价值", _options.ResourceValueGoal, "周兑换券"),
        new(TeamEconomyGoalKind.TaskPoints, "可靠任务积分", _options.ReliableTaskPointsGoal, "分"),
        new(TeamEconomyGoalKind.DeliveredOrders, "成功送达订单", _options.DeliveredOrdersGoal, "单")
    ];

    private static StoredMembership NewMembership(
        StoredTeam team,
        Guid accountId,
        DateTimeOffset now)
    {
        var membership = new StoredMembership(
            Guid.NewGuid(), team.TeamId, team.ServerId, team.SeasonId,
            accountId, now, null, string.Empty);
        return membership with { RowHash = MembershipHash(membership) };
    }

    private async Task<TeamEconomyMutationResponse?> TryReplayAsync<TeamEconomyMutationResponse>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operation,
        string serverId,
        Guid seasonId,
        Guid accountId,
        string idempotencyKey,
        string requestHash,
        CancellationToken cancellationToken)
        where TeamEconomyMutationResponse : class
    {
        var keyDigest = IdempotencyDigest(operation, accountId, idempotencyKey);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation_scope, server_id, season_id, actor_account_id,
                   request_hash, response_json
            FROM team_economy_idempotency
            WHERE operation_key_digest = $digest;
            """;
        command.Parameters.AddWithValue("$digest", keyDigest);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        if (!string.Equals(reader.GetString(0), operation, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), serverId, StringComparison.Ordinal) ||
            RequiredGuid(reader.GetString(2), "idempotency season") != seasonId ||
            RequiredGuid(reader.GetString(3), "idempotency actor") != accountId ||
            !string.Equals(reader.GetString(4), requestHash, StringComparison.Ordinal))
        {
            throw Conflict(
                "TEAM_IDEMPOTENCY_CONFLICT",
                "The Idempotency-Key was already bound to a different team operation request.");
        }
        try
        {
            return JsonSerializer.Deserialize<TeamEconomyMutationResponse>(reader.GetString(5), JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw InvalidStore("TEAM_IDEMPOTENCY_CORRUPT", "A durable team operation replay is corrupt.");
        }
    }

    private async Task SaveReplayAsync<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operation,
        string serverId,
        Guid seasonId,
        Guid accountId,
        string idempotencyKey,
        string requestHash,
        T response,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO team_economy_idempotency(
                operation_key_digest, operation_scope, server_id, season_id,
                actor_account_id, request_hash, response_json, created_at)
            VALUES(
                $digest, $scope, $serverId, $seasonId,
                $accountId, $requestHash, $responseJson, $createdAt);
            """;
        command.Parameters.AddWithValue("$digest", IdempotencyDigest(operation, accountId, idempotencyKey));
        command.Parameters.AddWithValue("$scope", operation);
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
        command.Parameters.AddWithValue("$requestHash", requestHash);
        command.Parameters.AddWithValue("$responseJson", JsonSerializer.Serialize(response, JsonOptions));
        command.Parameters.AddWithValue("$createdAt", UtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<StoredTeam> RequireOwnedTeamAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        Guid seasonId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var membership = await FindActiveMembershipAsync(
            connection, transaction, serverId, seasonId, accountId, cancellationToken)
            ?? throw NotFound("TEAM_NOT_JOINED", "The authenticated account is not in a team for this weekly world.");
        var team = await ReadTeamAsync(connection, transaction, membership.TeamId, cancellationToken)
            ?? throw InvalidStore("TEAM_STORE_CORRUPT", "An active membership references a missing team.");
        ValidateTeam(team);
        if (team.Status != TeamEconomyStatus.Active)
        {
            throw Conflict("TEAM_NOT_ACTIVE", "The team is no longer active.");
        }
        if (team.OwnerAccountId != accountId)
        {
            throw Forbidden("TEAM_OWNER_REQUIRED", "Only the current team owner may perform this operation.");
        }
        return team;
    }

    private static async Task EnsureSeasonScopeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, transaction, "extraction_events", cancellationToken))
        {
            throw InvalidStore("TEAM_SOURCE_SCHEMA_UNKNOWN", "The authoritative economy event table is unavailable.");
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM extraction_events ORDER BY sequence;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var matched = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<ProjectionEnvelope>(reader.GetString(0), JsonOptions);
                if (envelope?.Season is { } season && season.SeasonId == seasonId)
                {
                    if (!string.Equals(season.ServerId.Trim(), serverId, StringComparison.OrdinalIgnoreCase))
                    {
                        throw BadRequest("TEAM_SEASON_SERVER_MISMATCH", "The weekly world does not belong to this server.");
                    }
                    matched = true;
                }
            }
            catch (JsonException)
            {
                throw InvalidStore("TEAM_SOURCE_EVENT_INVALID", "An authoritative economy event cannot be validated.");
            }
        }
        if (!matched)
        {
            throw NotFound("TEAM_SEASON_NOT_FOUND", "The current weekly world was not found in authoritative economy state.");
        }
    }

    private async Task<bool> IsAccountExcludedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid seasonId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        if (await TableExistsAsync(
                connection,
                transaction,
                "season_leaderboard_exclusions",
                cancellationToken))
        {
            await using var exclusion = connection.CreateCommand();
            exclusion.Transaction = transaction;
            exclusion.CommandText = """
                SELECT 1
                FROM season_leaderboard_exclusions
                WHERE season_id = $seasonId
                  AND account_id = $accountId
                  AND active = 1
                LIMIT 1;
                """;
            exclusion.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
            exclusion.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
            if (await exclusion.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return true;
            }
        }
        if (!await TableExistsAsync(
                connection,
                transaction,
                "extraction_events",
                cancellationToken) ||
            !await TableExistsAsync(
                connection,
                transaction,
                "player_identity_bans",
                cancellationToken))
        {
            throw InvalidStore(
                "TEAM_SOURCE_SCHEMA_UNKNOWN",
                "The authoritative account or identity-ban schema is unavailable.");
        }
        await using var accountCommand = connection.CreateCommand();
        accountCommand.Transaction = transaction;
        accountCommand.CommandText = """
            SELECT payload
            FROM extraction_events
            WHERE json_extract(payload, '$.account.accountId') = $accountId
            ORDER BY sequence DESC
            LIMIT 1;
            """;
        accountCommand.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
        var payload = await accountCommand.ExecuteScalarAsync(cancellationToken) as string
            ?? throw InvalidStore(
                "TEAM_SOURCE_ACCOUNT_MISSING",
                "A team member has no authoritative account-to-security-subject mapping.");
        ExtractionAccount account;
        try
        {
            account = JsonSerializer.Deserialize<ProjectionEnvelope>(payload, JsonOptions)?.Account
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw InvalidStore(
                "TEAM_SOURCE_ACCOUNT_INVALID",
                "An authoritative account-to-security-subject mapping is invalid.");
        }
        if (account.AccountId != accountId ||
            string.IsNullOrWhiteSpace(account.ExternalUserId) ||
            account.ExternalUserId.Length > 128 ||
            account.ExternalUserId.Any(char.IsControl))
        {
            throw InvalidStore(
                "TEAM_SOURCE_ACCOUNT_INVALID",
                "An authoritative account-to-security-subject mapping is invalid.");
        }
        if (_isSubjectBanned(account.ExternalUserId))
        {
            return true;
        }
        await using var ban = connection.CreateCommand();
        ban.Transaction = transaction;
        ban.CommandText = """
            SELECT 1
            FROM player_identity_bans
            WHERE subject_fingerprint = $subjectFingerprint
              AND revoked_at IS NULL
            LIMIT 1;
            """;
        ban.Parameters.AddWithValue(
            "$subjectFingerprint",
            PlayerIdentitySecurityStore.FingerprintSubject(account.ExternalUserId));
        return await ban.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<StoredMembership?> FindActiveMembershipAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string serverId,
        Guid seasonId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT membership_id, team_id, server_id, season_id, account_id,
                   joined_at, left_at, row_hash
            FROM team_economy_memberships
            WHERE server_id = $serverId COLLATE NOCASE
              AND season_id = $seasonId
              AND account_id = $accountId
              AND left_at IS NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var membership = ReadMembership(reader);
        ValidateMembership(membership);
        return membership;
    }

    private static async Task<IReadOnlyList<StoredMembership>> ListActiveMembershipsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid teamId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT membership_id, team_id, server_id, season_id, account_id,
                   joined_at, left_at, row_hash
            FROM team_economy_memberships
            WHERE team_id = $teamId
              AND joined_at <= $at
              AND (left_at IS NULL OR left_at > $at)
            ORDER BY joined_at, membership_id;
            """;
        command.Parameters.AddWithValue("$teamId", teamId.ToString("D"));
        command.Parameters.AddWithValue("$at", at.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<StoredMembership>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var membership = ReadMembership(reader);
            ValidateMembership(membership);
            result.Add(membership);
        }
        return result;
    }

    private static async Task<int> CountActiveMembersAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid teamId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM team_economy_memberships
            WHERE team_id = $teamId
              AND joined_at <= $at
              AND (left_at IS NULL OR left_at > $at);
            """;
        command.Parameters.AddWithValue("$teamId", teamId.ToString("D"));
        command.Parameters.AddWithValue("$at", at.ToString("O"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> TeamNameExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        Guid seasonId,
        string normalizedName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM team_economy_teams
                WHERE server_id = $serverId COLLATE NOCASE
                  AND season_id = $seasonId
                  AND normalized_name = $name COLLATE NOCASE);
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        command.Parameters.AddWithValue("$name", normalizedName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<StoredTeam?> ReadTeamAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid teamId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT team_id, server_id, season_id, display_name, normalized_name,
                   owner_account_id, status, created_at, updated_at, dissolved_at, row_hash
            FROM team_economy_teams WHERE team_id = $teamId;
            """;
        command.Parameters.AddWithValue("$teamId", teamId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTeam(reader) : null;
    }

    private static async Task<StoredInvite?> ReadInviteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid inviteId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT invite_id, team_id, token_digest, token_version, created_at,
                   expires_at, maximum_uses, use_count, revoked_at, row_hash
            FROM team_economy_invites WHERE invite_id = $inviteId;
            """;
        command.Parameters.AddWithValue("$inviteId", inviteId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadInvite(reader) : null;
    }

    private static async Task InsertTeamAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredTeam team,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO team_economy_teams(
                team_id, server_id, season_id, display_name, normalized_name,
                owner_account_id, status, created_at, updated_at, dissolved_at, row_hash)
            VALUES(
                $teamId, $serverId, $seasonId, $displayName, $normalizedName,
                $ownerId, $status, $createdAt, $updatedAt, $dissolvedAt, $rowHash);
            """;
        BindTeam(command, team);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateTeamAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredTeam team,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE team_economy_teams SET
                server_id = $serverId, season_id = $seasonId,
                display_name = $displayName, normalized_name = $normalizedName,
                owner_account_id = $ownerId, status = $status,
                created_at = $createdAt, updated_at = $updatedAt,
                dissolved_at = $dissolvedAt, row_hash = $rowHash
            WHERE team_id = $teamId;
            """;
        BindTeam(command, team);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw InvalidStore("TEAM_STORE_WRITE_CONFLICT", "A team update lost its authoritative row.");
        }
    }

    private static void BindTeam(SqliteCommand command, StoredTeam team)
    {
        command.Parameters.AddWithValue("$teamId", team.TeamId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", team.ServerId);
        command.Parameters.AddWithValue("$seasonId", team.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$displayName", team.DisplayName);
        command.Parameters.AddWithValue("$normalizedName", team.NormalizedName);
        command.Parameters.AddWithValue("$ownerId", team.OwnerAccountId.ToString("D"));
        command.Parameters.AddWithValue("$status", team.Status.ToString());
        command.Parameters.AddWithValue("$createdAt", team.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", team.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$dissolvedAt", team.DissolvedAt is { } at ? at.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$rowHash", team.RowHash);
    }

    private static async Task InsertMembershipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredMembership membership,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO team_economy_memberships(
                membership_id, team_id, server_id, season_id, account_id,
                joined_at, left_at, row_hash)
            VALUES(
                $membershipId, $teamId, $serverId, $seasonId, $accountId,
                $joinedAt, $leftAt, $rowHash);
            """;
        BindMembership(command, membership);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateMembershipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredMembership membership,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE team_economy_memberships SET
                team_id = $teamId, server_id = $serverId, season_id = $seasonId,
                account_id = $accountId, joined_at = $joinedAt,
                left_at = $leftAt, row_hash = $rowHash
            WHERE membership_id = $membershipId;
            """;
        BindMembership(command, membership);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw InvalidStore("TEAM_STORE_WRITE_CONFLICT", "A membership update lost its authoritative row.");
        }
    }

    private static void BindMembership(SqliteCommand command, StoredMembership membership)
    {
        command.Parameters.AddWithValue("$membershipId", membership.MembershipId.ToString("D"));
        command.Parameters.AddWithValue("$teamId", membership.TeamId.ToString("D"));
        command.Parameters.AddWithValue("$serverId", membership.ServerId);
        command.Parameters.AddWithValue("$seasonId", membership.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$accountId", membership.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$joinedAt", membership.JoinedAt.ToString("O"));
        command.Parameters.AddWithValue("$leftAt", membership.LeftAt is { } at ? at.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$rowHash", membership.RowHash);
    }

    private static async Task InsertInviteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredInvite invite,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO team_economy_invites(
                invite_id, team_id, token_digest, token_version, created_at,
                expires_at, maximum_uses, use_count, revoked_at, row_hash)
            VALUES(
                $inviteId, $teamId, $digest, $version, $createdAt,
                $expiresAt, $maximumUses, $useCount, $revokedAt, $rowHash);
            """;
        BindInvite(command, invite);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateInviteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredInvite invite,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE team_economy_invites SET
                team_id = $teamId, token_digest = $digest,
                token_version = $version, created_at = $createdAt,
                expires_at = $expiresAt, maximum_uses = $maximumUses,
                use_count = $useCount, revoked_at = $revokedAt, row_hash = $rowHash
            WHERE invite_id = $inviteId;
            """;
        BindInvite(command, invite);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw InvalidStore("TEAM_STORE_WRITE_CONFLICT", "An invitation update lost its authoritative row.");
        }
    }

    private static void BindInvite(SqliteCommand command, StoredInvite invite)
    {
        command.Parameters.AddWithValue("$inviteId", invite.InviteId.ToString("D"));
        command.Parameters.AddWithValue("$teamId", invite.TeamId.ToString("D"));
        command.Parameters.AddWithValue("$digest", invite.TokenDigest);
        command.Parameters.AddWithValue("$version", invite.TokenVersion);
        command.Parameters.AddWithValue("$createdAt", invite.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$expiresAt", invite.ExpiresAt.ToString("O"));
        command.Parameters.AddWithValue("$maximumUses", invite.MaximumUses);
        command.Parameters.AddWithValue("$useCount", invite.UseCount);
        command.Parameters.AddWithValue("$revokedAt", invite.RevokedAt is { } at ? at.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$rowHash", invite.RowHash);
    }

    private static async Task RevokeInvitesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid teamId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT invite_id, team_id, token_digest, token_version, created_at,
                   expires_at, maximum_uses, use_count, revoked_at, row_hash
            FROM team_economy_invites
            WHERE team_id = $teamId AND revoked_at IS NULL;
            """;
        select.Parameters.AddWithValue("$teamId", teamId.ToString("D"));
        var invites = new List<StoredInvite>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var invite = ReadInvite(reader);
                ValidateInvite(invite);
                invites.Add(invite);
            }
        }
        foreach (var invite in invites)
        {
            var revoked = invite with { RevokedAt = now };
            revoked = revoked with { RowHash = InviteHash(revoked) };
            await UpdateInviteAsync(connection, transaction, revoked, cancellationToken);
        }
    }

    private string IdempotencyDigest(string operation, Guid accountId, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 128 || key.Any(char.IsControl))
        {
            throw BadRequest("TEAM_IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key must contain 8-128 non-control characters.");
        }
        return SecretDigest("idempotency", $"{operation}\n{accountId:N}\n{key}");
    }

    private string MemberHandle(Guid teamId, Guid accountId) =>
        SecretDigest("member-handle", $"{teamId:N}\n{accountId:N}")[..24];

    private static void ValidateMemberHandle(string value)
    {
        if (value.Length != 24 || !value.All(Uri.IsHexDigit))
        {
            throw BadRequest("TEAM_MEMBER_HANDLE_INVALID", "The selected member handle is invalid.");
        }
    }

    private string SecretDigest(string purpose, string value)
    {
        using var hmac = new HMACSHA256(_pepper);
        return Convert.ToHexString(hmac.ComputeHash(
            Encoding.UTF8.GetBytes($"pal-control-team-{purpose}-v1\n{value}"))).ToLowerInvariant();
    }

    private static bool FixedEquals(string first, string second)
    {
        if (first.Length != second.Length || !first.All(Uri.IsHexDigit) || !second.All(Uri.IsHexDigit))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(first), Convert.FromHexString(second));
    }

    private static (Guid InviteId, string Token) ParseInvitation(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
        {
            throw BadRequest("TEAM_INVITE_INVALID", "The invitation token is invalid.");
        }
        var token = value.Trim();
        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != "tm1" ||
            !Guid.TryParseExact(parts[1], "N", out var inviteId) ||
            parts[2].Length is < 40 or > 64 ||
            parts[2].Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw BadRequest("TEAM_INVITE_INVALID", "The invitation token is invalid.");
        }
        return (inviteId, token);
    }

    private static (string DisplayName, string NormalizedName) NormalizeTeamName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalized = name.Normalize(NormalizationForm.FormKC);
        if (normalized.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            throw BadRequest("TEAM_NAME_INVALID", "Team names cannot contain control or invalid Unicode characters.");
        }
        var display = Regex.Replace(normalized.Trim(), @"\s+", " ", RegexOptions.CultureInvariant);
        if (display.Length is < 2 or > 32)
        {
            throw BadRequest("TEAM_NAME_INVALID", "Team names must contain 2-32 characters.");
        }
        return (display, display.ToLowerInvariant());
    }

    private static string NormalizeServer(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 64 || value.Any(char.IsControl))
        {
            throw BadRequest("TEAM_SERVER_INVALID", "The server identity is invalid.");
        }
        return value.Trim().ToLowerInvariant();
    }

    private static void ValidateIdentity(Guid seasonId, Guid accountId)
    {
        if (seasonId == Guid.Empty || accountId == Guid.Empty)
        {
            throw BadRequest("TEAM_SCOPE_INVALID", "The authenticated weekly-world scope is invalid.");
        }
    }

    private static string TeamHash(StoredTeam value) => HashCanonical(
        "team-row-v1", value.TeamId, value.ServerId, value.SeasonId,
        value.DisplayName, value.NormalizedName, value.OwnerAccountId,
        value.Status, value.CreatedAt, value.UpdatedAt, value.DissolvedAt);

    private static string MembershipHash(StoredMembership value) => HashCanonical(
        "membership-row-v1", value.MembershipId, value.TeamId, value.ServerId,
        value.SeasonId, value.AccountId, value.JoinedAt, value.LeftAt);

    private static string InviteHash(StoredInvite value) => HashCanonical(
        "invite-row-v1", value.InviteId, value.TeamId, value.TokenDigest,
        value.TokenVersion, value.CreatedAt, value.ExpiresAt,
        value.MaximumUses, value.UseCount, value.RevokedAt);

    private static void ValidateTeam(StoredTeam team)
    {
        if (!string.Equals(team.RowHash, TeamHash(team), StringComparison.Ordinal) ||
            team.TeamId == Guid.Empty || team.SeasonId == Guid.Empty ||
            team.OwnerAccountId == Guid.Empty ||
            (team.Status == TeamEconomyStatus.Active && team.DissolvedAt is not null) ||
            (team.Status == TeamEconomyStatus.Dissolved && team.DissolvedAt is null))
        {
            throw InvalidStore("TEAM_STORE_INTEGRITY_FAILED", "Team state failed its integrity check.");
        }
    }

    private static void ValidateMembership(StoredMembership membership)
    {
        if (!string.Equals(membership.RowHash, MembershipHash(membership), StringComparison.Ordinal) ||
            membership.MembershipId == Guid.Empty || membership.TeamId == Guid.Empty ||
            membership.AccountId == Guid.Empty || membership.SeasonId == Guid.Empty ||
            membership.LeftAt < membership.JoinedAt)
        {
            throw InvalidStore("TEAM_STORE_INTEGRITY_FAILED", "Team membership failed its integrity check.");
        }
    }

    private static void ValidateInvite(StoredInvite invite)
    {
        if (!string.Equals(invite.RowHash, InviteHash(invite), StringComparison.Ordinal) ||
            invite.InviteId == Guid.Empty || invite.TeamId == Guid.Empty ||
            invite.TokenVersion != 1 || invite.TokenDigest.Length != 64 ||
            invite.MaximumUses is < 1 or > 100 ||
            invite.UseCount < 0 || invite.UseCount > invite.MaximumUses ||
            invite.ExpiresAt <= invite.CreatedAt || invite.RevokedAt < invite.CreatedAt)
        {
            throw InvalidStore("TEAM_STORE_INTEGRITY_FAILED", "Team invitation failed its integrity check.");
        }
    }

    private static StoredTeam ReadTeam(SqliteDataReader reader) => new(
        RequiredGuid(reader.GetString(0), "team id"),
        reader.GetString(1),
        RequiredGuid(reader.GetString(2), "team season"),
        reader.GetString(3),
        reader.GetString(4),
        RequiredGuid(reader.GetString(5), "team owner"),
        Enum.Parse<TeamEconomyStatus>(reader.GetString(6), ignoreCase: false),
        RequiredTimestamp(reader.GetString(7), "team creation"),
        RequiredTimestamp(reader.GetString(8), "team update"),
        reader.IsDBNull(9) ? null : RequiredTimestamp(reader.GetString(9), "team dissolution"),
        reader.GetString(10));

    private static StoredMembership ReadMembership(SqliteDataReader reader) => new(
        RequiredGuid(reader.GetString(0), "membership id"),
        RequiredGuid(reader.GetString(1), "membership team"),
        reader.GetString(2),
        RequiredGuid(reader.GetString(3), "membership season"),
        RequiredGuid(reader.GetString(4), "membership account"),
        RequiredTimestamp(reader.GetString(5), "membership join"),
        reader.IsDBNull(6) ? null : RequiredTimestamp(reader.GetString(6), "membership leave"),
        reader.GetString(7));

    private static StoredInvite ReadInvite(SqliteDataReader reader) => new(
        RequiredGuid(reader.GetString(0), "invite id"),
        RequiredGuid(reader.GetString(1), "invite team"),
        reader.GetString(2),
        reader.GetInt32(3),
        RequiredTimestamp(reader.GetString(4), "invite creation"),
        RequiredTimestamp(reader.GetString(5), "invite expiration"),
        reader.GetInt32(6),
        reader.GetInt32(7),
        reader.IsDBNull(8) ? null : RequiredTimestamp(reader.GetString(8), "invite revocation"),
        reader.GetString(9));

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName);
            """;
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static string HashCanonical(params object?[] values)
    {
        var material = string.Join('\n', values.Select(value => value switch
        {
            null => "null",
            DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Guid identifier => identifier.ToString("N"),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Guid RequiredGuid(string value, string field) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw InvalidStore("TEAM_STORE_INTEGRITY_FAILED", $"The {field} is invalid.");

    private static DateTimeOffset RequiredTimestamp(string value, string field) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : throw InvalidStore("TEAM_STORE_INTEGRITY_FAILED", $"The {field} is invalid.");

    private static TeamEconomyException BadRequest(string code, string message) =>
        new(code, message, StatusCodes.Status400BadRequest);
    private static TeamEconomyException Forbidden(string code, string message) =>
        new(code, message, StatusCodes.Status403Forbidden);
    private static TeamEconomyException NotFound(string code, string message) =>
        new(code, message, StatusCodes.Status404NotFound);
    private static TeamEconomyException Conflict(string code, string message) =>
        new(code, message, StatusCodes.Status409Conflict);
    private static TeamEconomyException InvalidStore(string code, string message) =>
        new(code, message, StatusCodes.Status503ServiceUnavailable);

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow();

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
        {
            throw new TeamEconomyException(
                "TEAM_ECONOMY_DISABLED",
                "Team collaboration is not enabled for this server.",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gate.Dispose();
        CryptographicOperations.ZeroMemory(_pepper);
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static string ResolveDataDirectory(string configured, string contentRoot) =>
        Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(contentRoot, configured));

    private sealed record StoredTeam(
        Guid TeamId,
        string ServerId,
        Guid SeasonId,
        string DisplayName,
        string NormalizedName,
        Guid OwnerAccountId,
        TeamEconomyStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? DissolvedAt,
        string RowHash);

    private sealed record StoredMembership(
        Guid MembershipId,
        Guid TeamId,
        string ServerId,
        Guid SeasonId,
        Guid AccountId,
        DateTimeOffset JoinedAt,
        DateTimeOffset? LeftAt,
        string RowHash);

    private sealed record StoredInvite(
        Guid InviteId,
        Guid TeamId,
        string TokenDigest,
        int TokenVersion,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        int MaximumUses,
        int UseCount,
        DateTimeOffset? RevokedAt,
        string RowHash);
}
