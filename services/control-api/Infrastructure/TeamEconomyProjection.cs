using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed partial class TeamEconomyStore
{
    public async Task<TeamEconomyProjectionResult> ProjectSeasonAsync(
        string serverId,
        Guid seasonId,
        DateTimeOffset cutoffAt,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var server = NormalizeServer(serverId);
        if (seasonId == Guid.Empty || cutoffAt == default)
        {
            throw BadRequest("TEAM_PROJECTION_SCOPE_INVALID", "The team projection scope is invalid.");
        }
        cutoffAt = cutoffAt.ToUniversalTime();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            ProjectionInput input;
            await using (var read = Open())
            {
                await using var transaction = await read.BeginTransactionAsync(cancellationToken);
                var teams = await LoadTeamsAsync(read, (SqliteTransaction)transaction, server, seasonId, cancellationToken);
                var memberships = await LoadMembershipsAsync(read, (SqliteTransaction)transaction, server, seasonId, cancellationToken);
                ValidateMembershipTimeline(teams, memberships);
                var sources = await LoadAuthoritativeSourcesAsync(
                    read, (SqliteTransaction)transaction, server, seasonId, cutoffAt,
                    memberships.Select(member => member.AccountId).ToHashSet(),
                    cancellationToken);
                input = new ProjectionInput(teams, memberships, sources.Events, sources.ExcludedAccounts, sources.SourceFacts);
                await transaction.CommitAsync(cancellationToken);
            }

            var desired = BuildProjection(input, server, seasonId);
            var sourceHash = HashCanonical(
                "team-projection-source-v2", server, seasonId,
                string.Join('\n', input.SourceFacts.Order(StringComparer.Ordinal)),
                string.Join('\n', input.Teams.Select(team => team.RowHash).Order(StringComparer.Ordinal)),
                string.Join('\n', input.Memberships.Select(member => member.RowHash).Order(StringComparer.Ordinal)));
            var snapshotHash = ProjectionSnapshotHash(
                desired, server, seasonId, input.ExcludedAccounts);
            var now = UtcNow();
            await using var write = Open();
            using var writeTransaction = write.BeginTransaction(deferred: false);
            var existing = await ValidateExistingProjectionAsync(
                write, writeTransaction, server, seasonId, cancellationToken);
            await EnforceProjectionCorrectionsAsync(
                write, writeTransaction, server, seasonId, desired,
                input.ExcludedAccounts, cancellationToken);
            var changed = existing is null ||
                !string.Equals(existing.SourceHash, sourceHash, StringComparison.Ordinal) ||
                !string.Equals(existing.SnapshotHash, snapshotHash, StringComparison.Ordinal);
            if (changed)
            {
                await DeleteProjectionAsync(write, writeTransaction, server, seasonId, cancellationToken);
                foreach (var row in desired.OrderBy(row => row.EventKey, StringComparer.Ordinal))
                {
                    await InsertProjectionAsync(write, writeTransaction, row, cancellationToken);
                }
                foreach (var accountId in input.ExcludedAccounts.Order())
                {
                    await InsertProjectionExclusionAsync(
                        write, writeTransaction, server, seasonId, accountId, cancellationToken);
                }
            }
            await SaveGoalProgressAsync(write, writeTransaction, input.Teams, desired, cancellationToken);
            var state = new ProjectionState(
                server, seasonId, cutoffAt, sourceHash, snapshotHash,
                desired.Count, now, string.Empty);
            state = state with { StateHash = ProjectionStateHash(state) };
            await SaveProjectionStateAsync(write, writeTransaction, state, cancellationToken);
            await ClearProjectionFailureAsync(write, writeTransaction, server, seasonId, cancellationToken);
            writeTransaction.Commit();
            return new TeamEconomyProjectionResult(
                server, seasonId, cutoffAt, input.Events.Count, desired.Count,
                changed, sourceHash, snapshotHash);
        }
        catch (OverflowException)
        {
            throw new TeamEconomyException(
                "TEAM_PROJECTION_OVERFLOW",
                "Authoritative team contribution exceeds the safe integer boundary.",
                StatusCodes.Status503ServiceUnavailable);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordProjectionFailureAsync(
        string serverId,
        Guid seasonId,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var server = NormalizeServer(serverId);
        if (seasonId == Guid.Empty || string.IsNullOrWhiteSpace(errorCode) ||
            errorCode.Length > 96 || errorCode.Any(char.IsControl))
        {
            return;
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = Open();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO team_economy_projection_failures(
                    server_id, season_id, error_code, failed_at)
                VALUES($serverId, $seasonId, $errorCode, $failedAt)
                ON CONFLICT(server_id, season_id) DO UPDATE SET
                    error_code = excluded.error_code,
                    failed_at = excluded.failed_at;
                """;
            command.Parameters.AddWithValue("$serverId", server);
            command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
            command.Parameters.AddWithValue("$errorCode", errorCode.Trim());
            command.Parameters.AddWithValue("$failedAt", UtcNow().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TeamEconomyDashboard> GetDashboardAsync(
        string serverId,
        Guid seasonId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var server = NormalizeServer(serverId);
        ValidateIdentity(seasonId, accountId);
        if (!_options.Enabled)
        {
            return new TeamEconomyDashboard(
                false, false, null, null, null, false, 0, null,
                [], null, null, [],
                new TeamEconomyProjectionHealth(
                    false, false, null, null, null, null, null),
                "本服务器尚未启用周世界团队协作；不会创建队伍、邀请或排行榜数据。");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = Open();
            var health = await ReadProjectionHealthAsync(
                connection, server, seasonId, cancellationToken);
            var membership = await FindActiveMembershipAsync(
                connection, null, server, seasonId, accountId, cancellationToken);
            if (membership is null)
            {
                return new TeamEconomyDashboard(
                    true, false, null, null, null, false, 0, null,
                    [], null, null, [], health,
                    TeamPolicyNotice);
            }
            var team = await ReadTeamAsync(connection, null, membership.TeamId, cancellationToken)
                ?? throw InvalidStore("TEAM_STORE_CORRUPT", "An active membership references a missing team.");
            ValidateTeam(team);
            var activeMembers = await ListActiveMembershipsAsync(
                connection, null, team.TeamId, UtcNow(), cancellationToken);
            var excludedAccounts = await ReadProjectionExclusionsAsync(
                connection, null, server, seasonId, cancellationToken);
            var rankedMembers = activeMembers
                .Where(member => !excludedAccounts.Contains(member.AccountId))
                .ToArray();
            if (!health.Ready)
            {
                return new TeamEconomyDashboard(
                    true, true, team.TeamId, team.DisplayName, team.Status,
                    team.OwnerAccountId == accountId, rankedMembers.Length,
                    membership.JoinedAt, [], null, null,
                    team.OwnerAccountId == accountId
                        ? TransferCandidates(team, rankedMembers, accountId)
                        : [],
                    health, TeamPolicyNotice);
            }
            var events = await ReadProjectionEventsAsync(
                connection, server, seasonId, cancellationToken);
            var teamEvents = events.Where(row => row.TeamId == team.TeamId).ToArray();
            var teamContribution = SumContribution(teamEvents);
            var myContribution = SumContribution(
                teamEvents.Where(row => row.AccountId == accountId));
            var goals = await LoadGoalProgressAsync(
                connection, team, teamEvents, teamContribution, cancellationToken);
            return new TeamEconomyDashboard(
                true, true, team.TeamId, team.DisplayName, team.Status,
                team.OwnerAccountId == accountId, rankedMembers.Length,
                membership.JoinedAt, goals, teamContribution, myContribution,
                team.OwnerAccountId == accountId
                    ? TransferCandidates(team, rankedMembers, accountId)
                    : [],
                health, TeamPolicyNotice);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TeamEconomyLeaderboardPage> GetLeaderboardAsync(
        string serverId,
        Guid seasonId,
        Guid accountId,
        TeamEconomyMetric metric,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var server = NormalizeServer(serverId);
        ValidateIdentity(seasonId, accountId);
        if (offset < 0 || limit is < 1 or > TeamEconomyLimits.MaximumPageSize)
        {
            throw BadRequest("TEAM_LEADERBOARD_PAGE_INVALID", "Leaderboard offset/limit is invalid.");
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureNotDisposed();
            await using var connection = Open();
            var health = await ReadProjectionHealthAsync(
                connection, server, seasonId, cancellationToken);
            if (!health.Ready || health.CutoffAt is not DateTimeOffset cutoffAt)
            {
                throw new TeamEconomyException(
                    "TEAM_PROJECTION_NOT_READY",
                    "The authoritative team projection is not ready; no zero-valued leaderboard was fabricated.",
                    StatusCodes.Status503ServiceUnavailable);
            }
            var teams = await LoadTeamsAsync(connection, null, server, seasonId, cancellationToken);
            var memberships = await LoadMembershipsAsync(connection, null, server, seasonId, cancellationToken);
            ValidateMembershipTimeline(teams, memberships);
            var excludedAccounts = await ReadProjectionExclusionsAsync(
                connection, null, server, seasonId, cancellationToken);
            var events = await ReadProjectionEventsAsync(
                connection, server, seasonId, cancellationToken);
            var myMembership = memberships.FirstOrDefault(member =>
                member.AccountId == accountId && member.LeftAt is null);
            var eligible = new List<(StoredTeam Team, int Members, long Value, DateTimeOffset ReachedAt)>();
            foreach (var team in teams.Where(team => team.Status == TeamEconomyStatus.Active))
            {
                var memberCount = memberships.Count(member =>
                    member.TeamId == team.TeamId && member.JoinedAt <= cutoffAt &&
                    (member.LeftAt is null || member.LeftAt > cutoffAt) &&
                    !excludedAccounts.Contains(member.AccountId));
                if (memberCount < _options.MinimumLeaderboardMembers)
                {
                    continue;
                }
                var teamEvents = events.Where(row => row.TeamId == team.TeamId).ToArray();
                var value = MetricValue(teamEvents, metric);
                if (value < MetricMinimum(metric))
                {
                    continue;
                }
                var reachedAt = teamEvents
                    .Where(row => MetricValue(row, metric) > 0)
                    .OrderBy(row => row.OccurredAt)
                    .ThenBy(row => row.EventKey, StringComparer.Ordinal)
                    .Last().OccurredAt;
                eligible.Add((team, memberCount, value, reachedAt));
            }
            var ordered = eligible
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.ReachedAt)
                .ThenBy(item => item.Team.TeamId)
                .ToArray();
            var ranked = ordered.Select((item, index) => new TeamEconomyLeaderboardEntry(
                index + 1, item.Team.TeamId, item.Team.DisplayName,
                item.Members, item.Value, item.ReachedAt,
                myMembership?.TeamId == item.Team.TeamId)).ToArray();
            var page = ranked.Skip(offset).Take(limit).ToArray();
            var next = offset + limit < ranked.Length
                ? (offset + limit).ToString(CultureInfo.InvariantCulture)
                : null;
            return new TeamEconomyLeaderboardPage(
                metric, cutoffAt, offset, limit, ranked.Length, next, page,
                "value desc, reachedAt asc, teamId asc",
                $"active teams; at least {_options.MinimumLeaderboardMembers} members at cutoff; " +
                "banned-account contributions omitted; dissolved teams hidden",
                health);
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<TeamEconomyTransferCandidate> TransferCandidates(
        StoredTeam team,
        IReadOnlyList<StoredMembership> members,
        Guid ownerId) => members
        .Where(member => member.AccountId != ownerId)
        .OrderBy(member => member.JoinedAt)
        .ThenBy(member => member.MembershipId)
        .Select((member, index) => new TeamEconomyTransferCandidate(
            MemberHandle(team.TeamId, member.AccountId), $"成员 {index + 1}", member.JoinedAt))
        .ToArray();

    private async Task<IReadOnlyList<TeamEconomyGoalProgress>> LoadGoalProgressAsync(
        SqliteConnection connection,
        StoredTeam team,
        IReadOnlyList<ProjectionRow> events,
        TeamEconomyContribution contribution,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_id, server_id, season_id, template_version,
                   goals_json, snapshot_hash, created_at
            FROM team_economy_goal_snapshots WHERE team_id = $teamId;
            """;
        command.Parameters.AddWithValue("$teamId", team.TeamId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw InvalidStore("TEAM_GOAL_SNAPSHOT_MISSING", "The team goal snapshot is missing.");
        }
        var snapshotId = RequiredGuid(reader.GetString(0), "goal snapshot");
        var serverId = reader.GetString(1);
        var seasonId = RequiredGuid(reader.GetString(2), "goal season");
        var template = reader.GetString(3);
        var goalsJson = reader.GetString(4);
        var storedHash = reader.GetString(5);
        var createdAt = RequiredTimestamp(reader.GetString(6), "goal creation");
        var expected = HashCanonical(
            "goal-snapshot-v1", snapshotId, team.TeamId, serverId,
            seasonId, template, goalsJson, createdAt);
        if (!string.Equals(storedHash, expected, StringComparison.Ordinal) ||
            !string.Equals(serverId, team.ServerId, StringComparison.Ordinal) ||
            seasonId != team.SeasonId)
        {
            throw InvalidStore("TEAM_GOAL_SNAPSHOT_CORRUPT", "The frozen team goal snapshot failed validation.");
        }
        TeamEconomyGoalDefinition[] goals;
        try
        {
            goals = JsonSerializer.Deserialize<TeamEconomyGoalDefinition[]>(goalsJson, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw InvalidStore("TEAM_GOAL_SNAPSHOT_CORRUPT", "The frozen team goal snapshot is invalid.");
        }
        await reader.DisposeAsync();
        var storedProgress = new Dictionary<
            TeamEconomyGoalKind,
            (long MaximumProgress, DateTimeOffset? ReachedAt)>();
        await using (var progress = connection.CreateCommand())
        {
            progress.CommandText = """
                SELECT goal_kind, maximum_progress, reached_at
                FROM team_economy_goal_progress
                WHERE team_id = $teamId
                ORDER BY goal_kind;
                """;
            progress.Parameters.AddWithValue("$teamId", team.TeamId.ToString("D"));
            await using var progressReader = await progress.ExecuteReaderAsync(cancellationToken);
            while (await progressReader.ReadAsync(cancellationToken))
            {
                if (!Enum.TryParse<TeamEconomyGoalKind>(
                        progressReader.GetString(0),
                        ignoreCase: false,
                        out var kind) ||
                    storedProgress.ContainsKey(kind))
                {
                    throw InvalidStore(
                        "TEAM_GOAL_PROGRESS_CORRUPT",
                        "A frozen team goal progress row is invalid or duplicated.");
                }
                var maximum = progressReader.GetInt64(1);
                ValidateSafe(maximum, "team goal maximum progress");
                storedProgress[kind] = (
                    maximum,
                    progressReader.IsDBNull(2)
                        ? null
                        : RequiredTimestamp(
                            progressReader.GetString(2),
                            "team goal reached timestamp"));
            }
        }
        return goals.Select(goal =>
        {
            if (goal.Target is < 1 or > TeamEconomyLimits.WebSafeInteger)
            {
                throw InvalidStore("TEAM_GOAL_SNAPSHOT_CORRUPT", "A frozen team goal target is unsafe.");
            }
            if (!storedProgress.TryGetValue(goal.Kind, out var stored) ||
                stored.MaximumProgress < GoalValue(contribution, goal.Kind) ||
                (stored.MaximumProgress >= goal.Target) != (stored.ReachedAt is not null))
            {
                throw InvalidStore(
                    "TEAM_GOAL_PROGRESS_CORRUPT",
                    "Frozen team goal progress is missing or conflicts with the verified projection.");
            }
            var progress = stored.MaximumProgress;
            return new TeamEconomyGoalProgress(
                goal.Kind, goal.DisplayName, progress, goal.Target,
                goal.Unit, progress >= goal.Target, stored.ReachedAt);
        }).ToArray();
    }

    private static DateTimeOffset? ReachedAt(
        IEnumerable<ProjectionRow> events,
        TeamEconomyGoalKind kind,
        long target)
    {
        long running = 0;
        foreach (var row in events.OrderBy(row => row.OccurredAt).ThenBy(row => row.EventKey, StringComparer.Ordinal))
        {
            running = SafeAdd(running, GoalValue(row, kind));
            if (running >= target)
            {
                return row.OccurredAt;
            }
        }
        return null;
    }

    private static TeamEconomyContribution SumContribution(IEnumerable<ProjectionRow> rows)
    {
        long items = 0;
        long value = 0;
        long tasks = 0;
        long orders = 0;
        long spent = 0;
        foreach (var row in rows)
        {
            items = SafeAdd(items, row.ResourceItems);
            value = SafeAdd(value, row.ResourceValue);
            tasks = SafeAdd(tasks, row.TaskPoints);
            orders = SafeAdd(orders, row.DeliveredOrders);
            spent = SafeAdd(spent, row.CurrencySpent);
        }
        return new TeamEconomyContribution(items, value, tasks, orders, spent);
    }

    private async Task<TeamEconomyProjectionHealth> ReadProjectionHealthAsync(
        SqliteConnection connection,
        string serverId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        var state = await ValidateExistingProjectionAsync(
            connection, null, serverId, seasonId, cancellationToken);
        string? errorCode = null;
        await using (var failure = connection.CreateCommand())
        {
            failure.CommandText = """
                SELECT error_code FROM team_economy_projection_failures
                WHERE server_id = $serverId COLLATE NOCASE AND season_id = $seasonId;
                """;
            failure.Parameters.AddWithValue("$serverId", serverId);
            failure.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
            errorCode = await failure.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (state is null)
        {
            return new TeamEconomyProjectionHealth(
                false, errorCode is not null, null, null, null, null, errorCode);
        }
        var staleAfter = TimeSpan.FromSeconds(_options.ProjectionIntervalSeconds * 3L);
        var stale = errorCode is not null || UtcNow() - state.UpdatedAt > staleAfter;
        return new TeamEconomyProjectionHealth(
            true, stale, state.CutoffAt, state.UpdatedAt,
            state.SourceHash, state.SnapshotHash, errorCode);
    }

    private static async Task<IReadOnlyList<StoredTeam>> LoadTeamsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string serverId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT team_id, server_id, season_id, display_name, normalized_name,
                   owner_account_id, status, created_at, updated_at, dissolved_at, row_hash
            FROM team_economy_teams
            WHERE server_id = $serverId COLLATE NOCASE AND season_id = $seasonId
            ORDER BY team_id;
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<StoredTeam>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var team = ReadTeam(reader);
            ValidateTeam(team);
            result.Add(team);
        }
        return result;
    }

    private static async Task<IReadOnlyList<StoredMembership>> LoadMembershipsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string serverId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT membership_id, team_id, server_id, season_id, account_id,
                   joined_at, left_at, row_hash
            FROM team_economy_memberships
            WHERE server_id = $serverId COLLATE NOCASE AND season_id = $seasonId
            ORDER BY account_id, joined_at, membership_id;
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
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

    private static void ValidateMembershipTimeline(
        IReadOnlyList<StoredTeam> teams,
        IReadOnlyList<StoredMembership> memberships)
    {
        var teamIds = teams.Select(team => team.TeamId).ToHashSet();
        foreach (var membership in memberships)
        {
            if (!teamIds.Contains(membership.TeamId))
            {
                throw InvalidStore("TEAM_STORE_INTEGRITY_FAILED", "A membership references a team outside its server/season scope.");
            }
        }
        foreach (var account in memberships.GroupBy(member => member.AccountId))
        {
            var periods = account.OrderBy(member => member.JoinedAt).ThenBy(member => member.MembershipId).ToArray();
            for (var index = 1; index < periods.Length; index++)
            {
                if (periods[index - 1].LeftAt is null || periods[index - 1].LeftAt > periods[index].JoinedAt)
                {
                    throw InvalidStore("TEAM_MEMBERSHIP_OVERLAP", "An account has overlapping team membership periods.");
                }
            }
        }
    }

    private static IReadOnlyList<ProjectionRow> BuildProjection(
        ProjectionInput input,
        string serverId,
        Guid seasonId)
    {
        var result = new List<ProjectionRow>();
        foreach (var source in input.Events.OrderBy(item => item.OccurredAt).ThenBy(item => item.EventKey, StringComparer.Ordinal))
        {
            if (input.ExcludedAccounts.Contains(source.AccountId))
            {
                continue;
            }
            var candidates = input.Memberships.Where(member =>
                member.AccountId == source.AccountId &&
                member.JoinedAt <= source.OccurredAt &&
                (member.LeftAt is null || member.LeftAt > source.OccurredAt)).ToArray();
            if (candidates.Length > 1)
            {
                throw InvalidStore("TEAM_MEMBERSHIP_OVERLAP", "An authoritative event matches more than one team membership.");
            }
            if (candidates.Length == 0)
            {
                continue;
            }
            var membership = candidates[0];
            var eventKey = HashCanonical(
                "team-projection-event-v1", source.SourceKind,
                source.SourceId, membership.MembershipId);
            var row = new ProjectionRow(
                eventKey, serverId, seasonId, membership.TeamId,
                membership.MembershipId, source.AccountId, source.SourceKind,
                source.SourceId, source.SourceHash, source.OccurredAt,
                source.ResourceItems, source.ResourceValue, source.TaskPoints,
                source.DeliveredOrders, source.CurrencySpent, source.ZoneId, string.Empty);
            ValidateSafe(row.ResourceItems, "resource items");
            ValidateSafe(row.ResourceValue, "resource value");
            ValidateSafe(row.TaskPoints, "task points");
            ValidateSafe(row.CurrencySpent, "currency spent");
            row = row with { RowHash = ProjectionRowHash(row) };
            result.Add(row);
        }
        if (result.Select(row => row.EventKey).Distinct(StringComparer.Ordinal).Count() != result.Count)
        {
            throw InvalidStore("TEAM_PROJECTION_DUPLICATE_EVENT", "A source event maps to duplicate team projection keys.");
        }
        // Validate aggregate arithmetic before persistence.
        foreach (var group in result.GroupBy(row => row.TeamId))
        {
            _ = SumContribution(group);
        }
        return result;
    }

    private async Task<AuthoritativeSources> LoadAuthoritativeSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        Guid seasonId,
        DateTimeOffset cutoffAt,
        IReadOnlySet<Guid> membershipAccounts,
        CancellationToken cancellationToken)
    {
        foreach (var table in new[]
                 {
                     "extraction_events", "extraction_settlement_runs",
                     "reliable_task_ranking_rewards"
                 })
        {
            if (!await TableExistsAsync(connection, transaction, table, cancellationToken))
            {
                throw InvalidStore("TEAM_SOURCE_SCHEMA_UNKNOWN", $"Required authoritative source table '{table}' is missing.");
            }
        }
        var facts = new List<SourceFact>();
        var sourceFacts = new List<string>();
        var orders = new Dictionary<Guid, Sequenced<ShopOrder>>();
        var deliveries = new Dictionary<Guid, Sequenced<ShopDelivery>>();
        var seasons = new Dictionary<Guid, ExtractionSeason>();
        var accounts = new Dictionary<Guid, Sequenced<ExtractionAccount>>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT sequence, event_id, event_type, occurred_at, payload
                FROM extraction_events
                WHERE occurred_at <= $cutoff
                ORDER BY sequence;
                """;
            command.Parameters.AddWithValue("$cutoff", cutoffAt.ToString("O"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var sequence = reader.GetInt64(0);
                var eventId = RequiredGuid(reader.GetString(1), "economy event");
                var eventType = reader.GetString(2);
                var occurredAt = RequiredTimestamp(reader.GetString(3), "economy event occurrence");
                ProjectionEnvelope envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<ProjectionEnvelope>(reader.GetString(4), JsonOptions)
                        ?? throw new JsonException();
                }
                catch (JsonException)
                {
                    throw InvalidStore("TEAM_SOURCE_EVENT_INVALID", "An authoritative economy event payload is invalid.");
                }
                if (envelope.SchemaVersion != 1 || envelope.EventId != eventId ||
                    !string.Equals(envelope.EventType, eventType, StringComparison.Ordinal) ||
                    envelope.At != occurredAt)
                {
                    throw InvalidStore("TEAM_SOURCE_EVENT_MISMATCH", "An authoritative event envelope conflicts with its SQL index.");
                }
                if (envelope.Season is { } season)
                {
                    seasons[season.SeasonId] = season;
                }
                if (envelope.Account is { } account)
                {
                    if (account.AccountId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(account.ExternalUserId) ||
                        account.ExternalUserId.Length > 128 ||
                        account.ExternalUserId.Any(char.IsControl) ||
                        string.IsNullOrWhiteSpace(account.IdentityProvider) ||
                        account.IdentityProvider.Length > 32 ||
                        account.IdentityProvider.Any(char.IsControl) ||
                        account.Revision < 1 || account.CreatedAt == default ||
                        account.UpdatedAt < account.CreatedAt || account.UpdatedAt > cutoffAt)
                    {
                        throw InvalidStore(
                            "TEAM_SOURCE_ACCOUNT_INVALID",
                            "An authoritative economy account mapping is invalid.");
                    }
                    accounts[account.AccountId] = new Sequenced<ExtractionAccount>(
                        account,
                        sequence);
                }
                if (envelope.Order is { } order)
                {
                    orders[order.OrderId] = new Sequenced<ShopOrder>(order, sequence);
                }
                if (envelope.Delivery is { } delivery)
                {
                    deliveries[delivery.DeliveryId] = new Sequenced<ShopDelivery>(delivery, sequence);
                }
            }
        }
        if (!seasons.TryGetValue(seasonId, out var requestedSeason) ||
            !string.Equals(requestedSeason.ServerId.Trim(), serverId, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidStore("TEAM_SOURCE_SEASON_MISMATCH", "The authoritative weekly-world/server mapping is unavailable or inconsistent.");
        }
        foreach (var order in orders.Values.Select(value => value.Value)
                     .Where(order => order.SeasonId == seasonId &&
                         string.Equals(order.ServerId.Trim(), serverId, StringComparison.OrdinalIgnoreCase)))
        {
            if (order.State != ShopOrderState.Delivered ||
                !deliveries.TryGetValue(order.DeliveryId, out var deliveryEntry) ||
                deliveryEntry.Value.OrderId != order.OrderId ||
                deliveryEntry.Value.State != ShopDeliveryState.Delivered)
            {
                continue;
            }
            var deliveredAt = deliveryEntry.Value.CompletedAt ?? order.UpdatedAt;
            if (deliveredAt > cutoffAt || deliveredAt < order.CreatedAt)
            {
                throw InvalidStore("TEAM_SOURCE_DELIVERY_INVALID", "A delivered order has an invalid authoritative completion timestamp.");
            }
            long spent = 0;
            foreach (var charge in order.Charges)
            {
                if (charge.Amount <= 0)
                {
                    throw InvalidStore("TEAM_SOURCE_DELIVERY_INVALID", "A delivered order has a non-positive actual charge.");
                }
                spent = SafeAdd(spent, charge.Amount);
            }
            ValidateSafe(spent, "delivered order currency spend");
            var sourceHash = HashCanonical(
                "delivery-source-v1", order.OrderId, order.AccountId, order.SeasonId,
                order.State, order.UpdatedAt, deliveryEntry.Value.DeliveryId,
                deliveryEntry.Value.State, deliveredAt, spent);
            var key = HashCanonical("delivery-key-v1", order.OrderId);
            facts.Add(new SourceFact(
                key, "delivery", order.OrderId.ToString("N"), sourceHash,
                order.AccountId, deliveredAt, 0, 0, 0, 1, spent, null));
            sourceFacts.Add($"delivery|{key}|{sourceHash}");
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT run_id, account_id, season_id, state, revision, updated_at, payload
                FROM extraction_settlement_runs
                WHERE season_id = $seasonId
                ORDER BY run_id;
                """;
            command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var runId = RequiredGuid(reader.GetString(0), "settlement run");
                ExtractionSettlementRun run;
                try
                {
                    run = JsonSerializer.Deserialize<ExtractionSettlementRun>(reader.GetString(6), JsonOptions)
                        ?? throw new JsonException();
                }
                catch (JsonException)
                {
                    throw InvalidStore("TEAM_SOURCE_RUN_INVALID", "A settlement run payload is invalid.");
                }
                if (run.RunId != runId || run.AccountId != RequiredGuid(reader.GetString(1), "run account") ||
                    run.SeasonId != seasonId || !string.Equals(run.State.ToString(), reader.GetString(3), StringComparison.Ordinal) ||
                    run.Revision != reader.GetInt64(4) || run.UpdatedAt != RequiredTimestamp(reader.GetString(5), "run update"))
                {
                    throw InvalidStore("TEAM_SOURCE_RUN_MISMATCH", "A settlement run payload conflicts with its SQL index.");
                }
                if (run.State != ExtractionSettlementState.Settled ||
                    run.SettledAt is not DateTimeOffset settledAt || settledAt > cutoffAt)
                {
                    continue;
                }
                long itemCount = 0;
                long value = 0;
                foreach (var line in run.Items)
                {
                    if (line.Quantity <= 0 || line.UnitValue <= 0 || line.TotalValue <= 0 ||
                        checked((long)line.Quantity * line.UnitValue) != line.TotalValue)
                    {
                        throw InvalidStore("TEAM_SOURCE_RUN_INVALID", "A settled run contains invalid resource evidence.");
                    }
                    itemCount = SafeAdd(itemCount, line.Quantity);
                    value = SafeAdd(value, line.TotalValue);
                }
                if (itemCount != run.ItemCount || value != run.TotalValue || itemCount == 0 || value == 0)
                {
                    throw InvalidStore("TEAM_SOURCE_RUN_INVALID", "A settled run total conflicts with its immutable resource lines.");
                }
                var sourceHash = HashCanonical(
                    "settlement-source-v1", run.RunId, run.AccountId, run.SeasonId,
                    run.Revision, settledAt, run.ZoneId, itemCount, value,
                    HashCanonical(string.Join('|', run.Items.OrderBy(line => line.ItemId, StringComparer.OrdinalIgnoreCase)
                        .Select(line => $"{line.ItemId.ToLowerInvariant()}:{line.Quantity}:{line.UnitValue}:{line.TotalValue}"))));
                var key = HashCanonical("settlement-key-v1", run.RunId);
                facts.Add(new SourceFact(
                    key, "settlement", run.RunId.ToString("N"), sourceHash,
                    run.AccountId, settledAt, itemCount, value, 0, 0, 0, run.ZoneId));
                sourceFacts.Add($"settlement|{key}|{sourceHash}");
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT entry_id, account_id, points, created_at
                FROM reliable_task_ranking_rewards
                WHERE season_id = $seasonId AND created_at <= $cutoff
                ORDER BY entry_id;
                """;
            command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
            command.Parameters.AddWithValue("$cutoff", cutoffAt.ToString("O"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var entryId = RequiredGuid(reader.GetString(0), "task reward");
                var accountId = RequiredGuid(reader.GetString(1), "task reward account");
                var points = reader.GetInt64(2);
                var createdAt = RequiredTimestamp(reader.GetString(3), "task reward creation");
                if (points <= 0)
                {
                    throw InvalidStore("TEAM_SOURCE_TASK_INVALID", "A reliable-task ranking reward has non-positive points.");
                }
                if (points > TeamEconomyLimits.WebSafeInteger)
                {
                    throw new TeamEconomyException(
                        "TEAM_PROJECTION_OVERFLOW",
                        "A reliable-task ranking reward exceeds the web-safe integer boundary.",
                        StatusCodes.Status503ServiceUnavailable);
                }
                var sourceHash = HashCanonical(
                    "task-source-v1", entryId, accountId, seasonId, points, createdAt);
                var key = HashCanonical("task-key-v1", entryId);
                facts.Add(new SourceFact(
                    key, "task", entryId.ToString("N"), sourceHash,
                    accountId, createdAt, 0, 0, points, 0, 0, null));
                sourceFacts.Add($"task|{key}|{sourceHash}");
            }
        }
        var requiredAccounts = facts.Select(fact => fact.AccountId)
            .Concat(membershipAccounts)
            .ToHashSet();
        foreach (var accountId in requiredAccounts.Order())
        {
            if (!accounts.TryGetValue(accountId, out var accountEntry))
            {
                throw InvalidStore(
                    "TEAM_SOURCE_ACCOUNT_MISSING",
                    "A team member or authoritative contribution has no account-to-security-subject mapping.");
            }
            var account = accountEntry.Value;
            var subjectFingerprint = PlayerIdentitySecurityStore.FingerprintSubject(
                account.ExternalUserId);
            sourceFacts.Add(
                $"account-map|{HashCanonical(account.AccountId, account.IdentityProvider.ToLowerInvariant(), subjectFingerprint, account.Revision, account.UpdatedAt)}");
        }

        var excluded = new HashSet<Guid>();
        if (!await TableExistsAsync(
                connection,
                transaction,
                "player_identity_bans",
                cancellationToken))
        {
            throw InvalidStore(
                "TEAM_SOURCE_SCHEMA_UNKNOWN",
                "The authoritative player identity ban table is missing.");
        }
        var activeBanFingerprints = new Dictionary<string, DateTimeOffset>(
            StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT subject_fingerprint, banned_at
                FROM player_identity_bans
                WHERE revoked_at IS NULL
                ORDER BY subject_fingerprint;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var fingerprint = reader.GetString(0);
                if (fingerprint.Length != 64 ||
                    fingerprint.Any(character => !Uri.IsHexDigit(character)))
                {
                    throw InvalidStore(
                        "TEAM_SOURCE_BAN_INVALID",
                        "An authoritative identity ban fingerprint is invalid.");
                }
                activeBanFingerprints[fingerprint] = RequiredTimestamp(
                    reader.GetString(1),
                    "identity ban timestamp");
            }
        }
        foreach (var accountId in requiredAccounts.Order())
        {
            var account = accounts[accountId].Value;
            var subjectFingerprint = PlayerIdentitySecurityStore.FingerprintSubject(
                account.ExternalUserId);
            if (activeBanFingerprints.TryGetValue(subjectFingerprint, out var bannedAt))
            {
                excluded.Add(accountId);
                sourceFacts.Add(
                    $"ban|{HashCanonical(accountId, subjectFingerprint, bannedAt)}");
            }
            else if (_isSubjectBanned(account.ExternalUserId))
            {
                // ApplyModeration installs this in-memory fence before durable
                // I/O. If that write fails, team eligibility must still fail
                // closed for the lifetime of the process.
                excluded.Add(accountId);
                sourceFacts.Add(
                    $"volatile-ban|{HashCanonical(accountId, subjectFingerprint)}");
            }
        }
        if (await TableExistsAsync(connection, transaction, "season_leaderboard_exclusions", cancellationToken))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT account_id FROM season_leaderboard_exclusions
                WHERE season_id = $seasonId AND active = 1
                ORDER BY account_id;
                """;
            command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var accountId = RequiredGuid(reader.GetString(0), "leaderboard exclusion");
                excluded.Add(accountId);
                sourceFacts.Add($"exclusion|{HashCanonical(accountId)}");
            }
        }
        return new AuthoritativeSources(facts, excluded, sourceFacts);
    }

    private static async Task<ProjectionState?> ValidateExistingProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string serverId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        ProjectionState? state = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT server_id, season_id, cutoff_at, source_hash, snapshot_hash,
                       projected_event_count, updated_at, state_hash
                FROM team_economy_projection_state
                WHERE server_id = $serverId COLLATE NOCASE AND season_id = $seasonId;
                """;
            command.Parameters.AddWithValue("$serverId", serverId);
            command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                state = new ProjectionState(
                    reader.GetString(0), RequiredGuid(reader.GetString(1), "projection season"),
                    RequiredTimestamp(reader.GetString(2), "projection cutoff"),
                    reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
                    RequiredTimestamp(reader.GetString(6), "projection update"), reader.GetString(7));
                if (!string.Equals(state.StateHash, ProjectionStateHash(state), StringComparison.Ordinal) ||
                    !string.Equals(state.ServerId, serverId, StringComparison.OrdinalIgnoreCase) ||
                    state.SeasonId != seasonId)
                {
                    throw InvalidStore("TEAM_PROJECTION_INTEGRITY_FAILED", "The team projection state hash is invalid.");
                }
            }
        }
        var rows = await ReadProjectionEventsAsync(
            connection, transaction, serverId, seasonId, cancellationToken);
        var excludedAccounts = await ReadProjectionExclusionsAsync(
            connection, transaction, serverId, seasonId, cancellationToken);
        if (state is null)
        {
            if (rows.Count != 0 || excludedAccounts.Count != 0)
            {
                throw InvalidStore("TEAM_PROJECTION_INTEGRITY_FAILED", "Team projection rows exist without a state snapshot.");
            }
            return null;
        }
        if (rows.Count != state.ProjectedEventCount ||
            !string.Equals(
                ProjectionSnapshotHash(rows, serverId, seasonId, excludedAccounts),
                state.SnapshotHash,
                StringComparison.Ordinal))
        {
            throw InvalidStore("TEAM_PROJECTION_INTEGRITY_FAILED", "The team projection snapshot hash is invalid.");
        }
        return state;
    }

    private static Task<IReadOnlyList<ProjectionRow>> ReadProjectionEventsAsync(
        SqliteConnection connection,
        string serverId,
        Guid seasonId,
        CancellationToken cancellationToken) =>
        ReadProjectionEventsAsync(connection, null, serverId, seasonId, cancellationToken);

    private static async Task<IReadOnlyList<ProjectionRow>> ReadProjectionEventsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string serverId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_key, server_id, season_id, team_id, membership_id,
                   account_id, source_kind, source_id, source_hash, occurred_at,
                   resource_items, resource_value, task_points, delivered_orders,
                   currency_spent, zone_id, row_hash
            FROM team_economy_projection_events
            WHERE server_id = $serverId COLLATE NOCASE AND season_id = $seasonId
            ORDER BY event_key;
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ProjectionRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new ProjectionRow(
                reader.GetString(0), reader.GetString(1),
                RequiredGuid(reader.GetString(2), "projected season"),
                RequiredGuid(reader.GetString(3), "projected team"),
                RequiredGuid(reader.GetString(4), "projected membership"),
                RequiredGuid(reader.GetString(5), "projected account"),
                reader.GetString(6), reader.GetString(7), reader.GetString(8),
                RequiredTimestamp(reader.GetString(9), "projected occurrence"),
                reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12),
                reader.GetInt64(13), reader.GetInt64(14),
                reader.IsDBNull(15) ? null : reader.GetString(15), reader.GetString(16));
            if (!string.Equals(row.RowHash, ProjectionRowHash(row), StringComparison.Ordinal) ||
                !string.Equals(row.ServerId, serverId, StringComparison.OrdinalIgnoreCase) ||
                row.SeasonId != seasonId || row.DeliveredOrders is < 0 or > 1)
            {
                throw InvalidStore("TEAM_PROJECTION_INTEGRITY_FAILED", "A team projection row failed its integrity check.");
            }
            ValidateSafe(row.ResourceItems, "projected resource items");
            ValidateSafe(row.ResourceValue, "projected resource value");
            ValidateSafe(row.TaskPoints, "projected task points");
            ValidateSafe(row.CurrencySpent, "projected currency spend");
            result.Add(row);
        }
        return result;
    }

    private static async Task DeleteProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM team_economy_projection_events
            WHERE server_id = $serverId COLLATE NOCASE AND season_id = $seasonId;
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var exclusions = connection.CreateCommand();
        exclusions.Transaction = transaction;
        exclusions.CommandText = """
            DELETE FROM team_economy_projection_exclusions
            WHERE server_id = $serverId COLLATE NOCASE AND season_id = $seasonId;
            """;
        exclusions.Parameters.AddWithValue("$serverId", serverId);
        exclusions.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        await exclusions.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertProjectionExclusionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        Guid seasonId,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO team_economy_projection_exclusions(
                server_id, season_id, account_id, row_hash)
            VALUES($serverId, $seasonId, $accountId, $rowHash);
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
        command.Parameters.AddWithValue(
            "$rowHash", ProjectionExclusionHash(serverId, seasonId, accountId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlySet<Guid>> ReadProjectionExclusionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string serverId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT account_id, row_hash
            FROM team_economy_projection_exclusions
            WHERE server_id = $serverId COLLATE NOCASE AND season_id = $seasonId
            ORDER BY account_id;
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new HashSet<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var accountId = RequiredGuid(reader.GetString(0), "projected exclusion account");
            if (!string.Equals(
                    reader.GetString(1),
                    ProjectionExclusionHash(serverId, seasonId, accountId),
                    StringComparison.Ordinal))
            {
                throw InvalidStore(
                    "TEAM_PROJECTION_INTEGRITY_FAILED",
                    "A projected leaderboard exclusion failed its integrity check.");
            }
            result.Add(accountId);
        }
        return result;
    }

    private static async Task InsertProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectionRow row,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO team_economy_projection_events(
                event_key, server_id, season_id, team_id, membership_id,
                account_id, source_kind, source_id, source_hash, occurred_at,
                resource_items, resource_value, task_points, delivered_orders,
                currency_spent, zone_id, row_hash)
            VALUES(
                $eventKey, $serverId, $seasonId, $teamId, $membershipId,
                $accountId, $sourceKind, $sourceId, $sourceHash, $occurredAt,
                $resourceItems, $resourceValue, $taskPoints, $deliveredOrders,
                $currencySpent, $zoneId, $rowHash);
            """;
        command.Parameters.AddWithValue("$eventKey", row.EventKey);
        command.Parameters.AddWithValue("$serverId", row.ServerId);
        command.Parameters.AddWithValue("$seasonId", row.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$teamId", row.TeamId.ToString("D"));
        command.Parameters.AddWithValue("$membershipId", row.MembershipId.ToString("D"));
        command.Parameters.AddWithValue("$accountId", row.AccountId.ToString("D"));
        command.Parameters.AddWithValue("$sourceKind", row.SourceKind);
        command.Parameters.AddWithValue("$sourceId", row.SourceId);
        command.Parameters.AddWithValue("$sourceHash", row.SourceHash);
        command.Parameters.AddWithValue("$occurredAt", row.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$resourceItems", row.ResourceItems);
        command.Parameters.AddWithValue("$resourceValue", row.ResourceValue);
        command.Parameters.AddWithValue("$taskPoints", row.TaskPoints);
        command.Parameters.AddWithValue("$deliveredOrders", row.DeliveredOrders);
        command.Parameters.AddWithValue("$currencySpent", row.CurrencySpent);
        command.Parameters.AddWithValue("$zoneId", row.ZoneId is null ? DBNull.Value : row.ZoneId);
        command.Parameters.AddWithValue("$rowHash", row.RowHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnforceProjectionCorrectionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        Guid seasonId,
        IReadOnlyList<ProjectionRow> desired,
        IReadOnlySet<Guid> excludedAccounts,
        CancellationToken cancellationToken)
    {
        var previous = await ReadProjectionEventsAsync(
            connection,
            transaction,
            serverId,
            seasonId,
            cancellationToken);
        var desiredByKey = desired.ToDictionary(
            row => row.EventKey,
            StringComparer.Ordinal);
        foreach (var row in previous)
        {
            if (desiredByKey.TryGetValue(row.EventKey, out var current) &&
                string.Equals(row.RowHash, current.RowHash, StringComparison.Ordinal))
            {
                continue;
            }
            if (excludedAccounts.Contains(row.AccountId) &&
                !desiredByKey.ContainsKey(row.EventKey))
            {
                // A current identity ban or explicit season exclusion is the
                // only legal reason to remove a previously verified source
                // row. Goal milestones remain monotonic in their dedicated
                // maximum-progress table, while current contributions and
                // leaderboards exclude the account immediately.
                continue;
            }
            throw InvalidStore(
                "TEAM_PROJECTION_NON_MONOTONIC",
                "An authoritative correction changed or removed a previously verified team fact outside the current exclusion policy; the old snapshot was retained for reconciliation.");
        }
    }

    private static async Task SaveGoalProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<StoredTeam> teams,
        IReadOnlyList<ProjectionRow> desired,
        CancellationToken cancellationToken)
    {
        foreach (var team in teams)
        {
            var rows = desired.Where(row => row.TeamId == team.TeamId).ToArray();
            var contribution = SumContribution(rows);
            await using var snapshot = connection.CreateCommand();
            snapshot.Transaction = transaction;
            snapshot.CommandText = """
                SELECT goals_json
                FROM team_economy_goal_snapshots
                WHERE team_id = $teamId;
                """;
            snapshot.Parameters.AddWithValue("$teamId", team.TeamId.ToString("D"));
            var goalsJson = await snapshot.ExecuteScalarAsync(cancellationToken) as string
                ?? throw InvalidStore(
                    "TEAM_GOAL_SNAPSHOT_MISSING",
                    "The team goal snapshot is missing.");
            TeamEconomyGoalDefinition[] goals;
            try
            {
                goals = JsonSerializer.Deserialize<TeamEconomyGoalDefinition[]>(
                    goalsJson,
                    JsonOptions) ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw InvalidStore(
                    "TEAM_GOAL_SNAPSHOT_CORRUPT",
                    "The frozen team goal snapshot is invalid.");
            }
            if (goals.Length != Enum.GetValues<TeamEconomyGoalKind>().Length ||
                goals.Select(goal => goal.Kind).Distinct().Count() != goals.Length)
            {
                throw InvalidStore(
                    "TEAM_GOAL_SNAPSHOT_CORRUPT",
                    "The frozen team goal snapshot does not define every goal exactly once.");
            }
            foreach (var goal in goals)
            {
                if (goal.Target is < 1 or > TeamEconomyLimits.WebSafeInteger)
                {
                    throw InvalidStore(
                        "TEAM_GOAL_SNAPSHOT_CORRUPT",
                        "A frozen team goal target is unsafe.");
                }
                var value = GoalValue(contribution, goal.Kind);
                var reachedAt = value >= goal.Target
                    ? ReachedAt(rows, goal.Kind, goal.Target)
                    : null;
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO team_economy_goal_progress(
                        team_id, goal_kind, maximum_progress, reached_at)
                    VALUES($teamId, $kind, $progress, $reachedAt)
                    ON CONFLICT(team_id, goal_kind) DO UPDATE SET
                        maximum_progress = MAX(maximum_progress, excluded.maximum_progress),
                        reached_at = CASE
                            WHEN maximum_progress >= $target THEN reached_at
                            WHEN excluded.maximum_progress >= $target
                                THEN excluded.reached_at
                            ELSE NULL
                        END;
                    """;
                command.Parameters.AddWithValue("$teamId", team.TeamId.ToString("D"));
                command.Parameters.AddWithValue("$kind", goal.Kind.ToString());
                command.Parameters.AddWithValue("$progress", value);
                command.Parameters.AddWithValue("$target", goal.Target);
                command.Parameters.AddWithValue("$reachedAt", reachedAt is { } at ? at.ToString("O") : DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task SaveProjectionStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectionState state,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO team_economy_projection_state(
                server_id, season_id, cutoff_at, source_hash, snapshot_hash,
                projected_event_count, updated_at, state_hash)
            VALUES(
                $serverId, $seasonId, $cutoffAt, $sourceHash, $snapshotHash,
                $eventCount, $updatedAt, $stateHash)
            ON CONFLICT(server_id, season_id) DO UPDATE SET
                cutoff_at = excluded.cutoff_at,
                source_hash = excluded.source_hash,
                snapshot_hash = excluded.snapshot_hash,
                projected_event_count = excluded.projected_event_count,
                updated_at = excluded.updated_at,
                state_hash = excluded.state_hash;
            """;
        command.Parameters.AddWithValue("$serverId", state.ServerId);
        command.Parameters.AddWithValue("$seasonId", state.SeasonId.ToString("D"));
        command.Parameters.AddWithValue("$cutoffAt", state.CutoffAt.ToString("O"));
        command.Parameters.AddWithValue("$sourceHash", state.SourceHash);
        command.Parameters.AddWithValue("$snapshotHash", state.SnapshotHash);
        command.Parameters.AddWithValue("$eventCount", state.ProjectedEventCount);
        command.Parameters.AddWithValue("$updatedAt", state.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$stateHash", state.StateHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ClearProjectionFailureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string serverId,
        Guid seasonId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM team_economy_projection_failures
            WHERE server_id = $serverId COLLATE NOCASE AND season_id = $seasonId;
            """;
        command.Parameters.AddWithValue("$serverId", serverId);
        command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private long MetricMinimum(TeamEconomyMetric metric) => metric switch
    {
        TeamEconomyMetric.ResourceValue => _options.MinimumResourceValue,
        TeamEconomyMetric.TaskPoints => _options.MinimumTaskPoints,
        TeamEconomyMetric.DeliveredOrders => _options.MinimumDeliveredOrders,
        _ => throw BadRequest("TEAM_LEADERBOARD_METRIC_INVALID", "The leaderboard metric is invalid.")
    };

    private static long MetricValue(IEnumerable<ProjectionRow> rows, TeamEconomyMetric metric)
    {
        long value = 0;
        foreach (var row in rows)
        {
            value = SafeAdd(value, MetricValue(row, metric));
        }
        return value;
    }

    private static long MetricValue(ProjectionRow row, TeamEconomyMetric metric) => metric switch
    {
        TeamEconomyMetric.ResourceValue => row.ResourceValue,
        TeamEconomyMetric.TaskPoints => row.TaskPoints,
        TeamEconomyMetric.DeliveredOrders => row.DeliveredOrders,
        _ => throw BadRequest("TEAM_LEADERBOARD_METRIC_INVALID", "The leaderboard metric is invalid.")
    };

    private static long GoalValue(TeamEconomyContribution contribution, TeamEconomyGoalKind kind) => kind switch
    {
        TeamEconomyGoalKind.ResourceItems => contribution.ResourceItems,
        TeamEconomyGoalKind.ResourceValue => contribution.ResourceValue,
        TeamEconomyGoalKind.TaskPoints => contribution.TaskPoints,
        TeamEconomyGoalKind.DeliveredOrders => contribution.DeliveredOrders,
        _ => throw InvalidStore("TEAM_GOAL_KIND_INVALID", "A team goal kind is invalid.")
    };

    private static long GoalValue(ProjectionRow row, TeamEconomyGoalKind kind) => kind switch
    {
        TeamEconomyGoalKind.ResourceItems => row.ResourceItems,
        TeamEconomyGoalKind.ResourceValue => row.ResourceValue,
        TeamEconomyGoalKind.TaskPoints => row.TaskPoints,
        TeamEconomyGoalKind.DeliveredOrders => row.DeliveredOrders,
        _ => throw InvalidStore("TEAM_GOAL_KIND_INVALID", "A team goal kind is invalid.")
    };

    private static long SafeAdd(long first, long second)
    {
        var result = checked(first + second);
        ValidateSafe(result, "team aggregate");
        return result;
    }

    private static void ValidateSafe(long value, string field)
    {
        if (value is < 0 or > TeamEconomyLimits.WebSafeInteger)
        {
            throw new OverflowException($"The {field} exceeds the web-safe integer boundary.");
        }
    }

    private static string ProjectionRowHash(ProjectionRow value) => HashCanonical(
        "projection-row-v1", value.EventKey, value.ServerId, value.SeasonId,
        value.TeamId, value.MembershipId, value.AccountId, value.SourceKind,
        value.SourceId, value.SourceHash, value.OccurredAt, value.ResourceItems,
        value.ResourceValue, value.TaskPoints, value.DeliveredOrders,
        value.CurrencySpent, value.ZoneId);

    private static string ProjectionSnapshotHash(
        IEnumerable<ProjectionRow> rows,
        string serverId,
        Guid seasonId,
        IEnumerable<Guid> excludedAccounts) =>
        HashCanonical(
            "projection-snapshot-v1",
            string.Join('\n', rows.OrderBy(row => row.EventKey, StringComparer.Ordinal)
                .Select(row => row.RowHash)),
            string.Join('\n', excludedAccounts.Order()
                .Select(accountId => ProjectionExclusionHash(serverId, seasonId, accountId))));

    private static string ProjectionExclusionHash(
        string serverId,
        Guid seasonId,
        Guid accountId) => HashCanonical(
            "projection-exclusion-v1", serverId, seasonId, accountId);

    private static string ProjectionStateHash(ProjectionState value) => HashCanonical(
        "projection-state-v1", value.ServerId, value.SeasonId, value.CutoffAt,
        value.SourceHash, value.SnapshotHash, value.ProjectedEventCount, value.UpdatedAt);

    private const string TeamPolicyNotice =
        "团队协作只统计服务端权威的已结算资源、成功送达订单和可靠任务积分；不接收客户端自报，不自动发币或扣币，也不开放跨服团队/交易。";

    private sealed record SourceFact(
        string EventKey,
        string SourceKind,
        string SourceId,
        string SourceHash,
        Guid AccountId,
        DateTimeOffset OccurredAt,
        long ResourceItems,
        long ResourceValue,
        long TaskPoints,
        long DeliveredOrders,
        long CurrencySpent,
        string? ZoneId);

    private sealed record ProjectionRow(
        string EventKey,
        string ServerId,
        Guid SeasonId,
        Guid TeamId,
        Guid MembershipId,
        Guid AccountId,
        string SourceKind,
        string SourceId,
        string SourceHash,
        DateTimeOffset OccurredAt,
        long ResourceItems,
        long ResourceValue,
        long TaskPoints,
        long DeliveredOrders,
        long CurrencySpent,
        string? ZoneId,
        string RowHash);

    private sealed record ProjectionState(
        string ServerId,
        Guid SeasonId,
        DateTimeOffset CutoffAt,
        string SourceHash,
        string SnapshotHash,
        int ProjectedEventCount,
        DateTimeOffset UpdatedAt,
        string StateHash);

    private sealed record ProjectionInput(
        IReadOnlyList<StoredTeam> Teams,
        IReadOnlyList<StoredMembership> Memberships,
        IReadOnlyList<SourceFact> Events,
        IReadOnlySet<Guid> ExcludedAccounts,
        IReadOnlyList<string> SourceFacts);

    private sealed record AuthoritativeSources(
        IReadOnlyList<SourceFact> Events,
        IReadOnlySet<Guid> ExcludedAccounts,
        IReadOnlyList<string> SourceFacts);

    private sealed record Sequenced<T>(T Value, long Sequence);

    private sealed class ProjectionEnvelope
    {
        public int SchemaVersion { get; init; }
        public Guid EventId { get; init; }
        public string EventType { get; init; } = string.Empty;
        public DateTimeOffset At { get; init; }
        public ExtractionSeason? Season { get; init; }
        public ExtractionAccount? Account { get; init; }
        public ShopOrder? Order { get; init; }
        public ShopDelivery? Delivery { get; init; }
    }
}
