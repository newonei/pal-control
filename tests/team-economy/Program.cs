using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var root = Path.Combine(Path.GetTempPath(), $"pal-control-team-economy-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
var databasePath = Path.Combine(root, "extraction-commerce.db");
var serverId = "team-harness";
var seasonId = Guid.NewGuid();
var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero));
var options = new TeamEconomyOptions
{
    Enabled = true,
    InvitePepper = "harness-only-team-invitation-pepper-64-characters-0000000000000000",
    ProjectionIntervalSeconds = 5,
    InviteLifetimeMinutes = 60,
    InviteMaximumUses = 20,
    MinimumLeaderboardMembers = 1,
    MinimumResourceValue = 1,
    MinimumTaskPoints = 1,
    MinimumDeliveredOrders = 1,
    ResourceItemsGoal = 10,
    ResourceValueGoal = 100,
    ReliableTaskPointsGoal = 10,
    DeliveredOrdersGoal = 1,
    GoalTemplateVersion = "harness-goals-v1"
};

try
{
    InitializeAuthoritativeDatabase(databasePath);
    var identityStore = new PlayerIdentitySecurityStore(root);
    var volatileBans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    InsertSeason(databasePath, serverId, seasonId, clock.GetUtcNow());
    using (var disabledStore = new TeamEconomyStore(
               root,
               new TeamEconomyOptions { Enabled = false },
               clock))
    {
        var disabledDashboard = await disabledStore.GetDashboardAsync(
            serverId, seasonId, Guid.NewGuid(), default);
        Assert(!disabledDashboard.Enabled && !disabledDashboard.HasTeam &&
               !disabledDashboard.Projection.Ready,
            "The intentional disabled default did not return an explicit non-error dashboard state.");
    }
    var store = new TeamEconomyStore(root, options, clock, volatileBans.Contains);

    // A source fact before membership must never be backfilled.
    var firstOwner = Guid.NewGuid();
    InsertAccount(databasePath, firstOwner, clock.GetUtcNow().AddMinutes(-2));
    InsertTaskReward(databasePath, firstOwner, seasonId, 77, clock.GetUtcNow().AddMinutes(-1));
    var firstTeam = await store.CreateTeamAsync(
        serverId, seasonId, firstOwner, "晨星小队", "create-first-0001", default);
    var createReplay = await store.CreateTeamAsync(
        serverId, seasonId, firstOwner, "晨星小队", "create-first-0001", default);
    Assert(createReplay.Replayed && createReplay.TeamId == firstTeam.TeamId,
        "Create-team idempotency did not replay the durable result.");
    await AssertCodeAsync(
        () => store.CreateTeamAsync(
            serverId, seasonId, firstOwner, "不同名字", "create-first-0001", default),
        "TEAM_IDEMPOTENCY_CONFLICT",
        "same key / different create request");

    var invitation = await store.RotateInviteAsync(
        serverId, seasonId, firstOwner, 20, "invite-first-0001", default);
    Assert(invitation.TokenShown && !string.IsNullOrWhiteSpace(invitation.Token),
        "Fresh invitation did not show its bearer token exactly once.");
    var invitationReplay = await store.RotateInviteAsync(
        serverId, seasonId, firstOwner, 20, "invite-first-0001", default);
    Assert(invitationReplay.Replayed && !invitationReplay.TokenShown && invitationReplay.Token is null,
        "Invitation replay exposed the bearer token again.");

    // Build 100 accounts across 10 teams. Nine joiners per invitation exercise
    // concurrent use-count fencing and the one-active-team-per-week constraint.
    var teams = new List<(Guid TeamId, Guid Owner, string Name)> { (firstTeam.TeamId, firstOwner, "晨星小队") };
    var accounts = new List<Guid> { firstOwner };
    var firstJoiners = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToArray();
    foreach (var account in firstJoiners)
    {
        InsertAccount(databasePath, account, clock.GetUtcNow());
    }
    accounts.AddRange(firstJoiners);
    var firstJoins = await Task.WhenAll(firstJoiners.Select((account, index) =>
        store.JoinAsync(
            serverId, seasonId, account, invitation.Token!,
            $"join-first-{index:0000}", default)));
    Assert(firstJoins.All(result => result.TeamId == firstTeam.TeamId),
        "Concurrent invitation joins did not stay in the invited team.");

    for (var teamIndex = 1; teamIndex < 10; teamIndex++)
    {
        var owner = Guid.NewGuid();
        InsertAccount(databasePath, owner, clock.GetUtcNow());
        accounts.Add(owner);
        var name = $"测试小队 {teamIndex + 1}";
        var created = await store.CreateTeamAsync(
            serverId, seasonId, owner, name, $"create-{teamIndex:0000}", default);
        teams.Add((created.TeamId, owner, name));
        var invite = await store.RotateInviteAsync(
            serverId, seasonId, owner, 9, $"invite-{teamIndex:0000}", default);
        var joiners = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var account in joiners)
        {
            InsertAccount(databasePath, account, clock.GetUtcNow());
        }
        accounts.AddRange(joiners);
        var joins = await Task.WhenAll(joiners.Select((account, index) =>
            store.JoinAsync(
                serverId, seasonId, account, invite.Token!,
                $"join-{teamIndex:0000}-{index:0000}", default)));
        Assert(joins.Count(result => result.TeamId == created.TeamId) == 9,
            "A team invitation did not admit exactly its nine intended accounts.");
    }
    Assert(accounts.Count == 100 && accounts.Distinct().Count() == 100,
        "The 100-account / 10-team fixture is incomplete.");

    // A capped invitation raced by 100 accounts must admit only five. This is
    // isolated in a second authoritative weekly world.
    var raceSeason = Guid.NewGuid();
    InsertSeason(databasePath, serverId, raceSeason, clock.GetUtcNow());
    var raceOwner = Guid.NewGuid();
    InsertAccount(databasePath, raceOwner, clock.GetUtcNow());
    await store.CreateTeamAsync(
        serverId, raceSeason, raceOwner, "并发邀请小队", "race-create-0001", default);
    var raceInvite = await store.RotateInviteAsync(
        serverId, raceSeason, raceOwner, 5, "race-invite-0001", default);
    var racers = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();
    foreach (var account in racers)
    {
        InsertAccount(databasePath, account, clock.GetUtcNow());
    }
    var raceResults = await Task.WhenAll(racers.Select((account, index) => AttemptAsync(() =>
        store.JoinAsync(
            serverId, raceSeason, account, raceInvite.Token!,
            $"race-join-{index:0000}", default))));
    Assert(raceResults.Count(result => result.Success) == 5,
        "A 100-way invitation race did not enforce exactly five permitted uses.");
    Assert(raceResults.Where(result => !result.Success).All(result =>
            result.Code is "TEAM_INVITE_EXPIRED"),
        "Invitation losers did not receive the stable exhausted-token conflict.");
    foreach (var racer in racers)
    {
        ExcludeAccount(databasePath, raceSeason, racer);
    }
    InsertTaskReward(databasePath, raceOwner, raceSeason, 1, clock.GetUtcNow());
    await store.ProjectSeasonAsync(serverId, raceSeason, clock.GetUtcNow(), default);
    var raceDashboard = await store.GetDashboardAsync(serverId, raceSeason, raceOwner, default);
    Assert(raceDashboard.MemberCount == 1,
        "Banned accounts were counted as eligible team members.");
    store.Dispose();
    options.MinimumLeaderboardMembers = 2;
    store = new TeamEconomyStore(root, options, clock, volatileBans.Contains);
    var bannedThresholdBoard = await store.GetLeaderboardAsync(
        serverId, raceSeason, raceOwner, TeamEconomyMetric.TaskPoints, 0, 50, default);
    Assert(bannedThresholdBoard.Total == 0,
        "One normal member plus banned accounts incorrectly met the two-member leaderboard threshold.");
    store.Dispose();
    options.MinimumLeaderboardMembers = 1;
    store = new TeamEconomyStore(root, options, clock, volatileBans.Contains);

    // Transfer and leave use opaque member handles. 100 concurrent attempts
    // result in one legal state transition and no split ownership/member state.
    var ownerDashboard = await store.GetDashboardAsync(
        serverId, seasonId, firstOwner, default);
    var transferTarget = ownerDashboard.TransferCandidates.First();
    var transferResults = await Task.WhenAll(Enumerable.Range(0, 100).Select(index => AttemptAsync(() =>
        store.TransferOwnershipAsync(
            serverId, seasonId, firstOwner, transferTarget.MemberHandle,
            $"transfer-race-{index:0000}", default))));
    Assert(transferResults.Count(result => result.Success) == 1,
        "A 100-way owner transfer race produced more or fewer than one legal transition.");
    var ownerStates = await Task.WhenAll(firstJoiners.Select(async account => new
    {
        Account = account,
        Dashboard = await store.GetDashboardAsync(serverId, seasonId, account, default)
    }));
    var targetAccount = ownerStates.Single(item => item.Dashboard.IsOwner).Account;
    var targetDashboard = ownerStates.Single(item => item.Account == targetAccount).Dashboard;
    Assert(targetDashboard.IsOwner,
        "The opaque transfer target did not become the only owner.");
    var cachedTransferHandle = targetDashboard.TransferCandidates.First().MemberHandle;
    var transferBlockedAccounts = accounts.Take(10)
        .Where(account => account != targetAccount)
        .ToArray();
    foreach (var account in transferBlockedAccounts)
    {
        identityStore.SetBan(
            SyntheticSubject(account), true, $"ban-transfer-{account:N}",
            new string('b', 64), clock.GetUtcNow());
    }
    await AssertCodeAsync(
        () => store.TransferOwnershipAsync(
            serverId, seasonId, targetAccount, cachedTransferHandle,
            "transfer-banned-member-0001", default),
        "TEAM_MEMBER_INELIGIBLE",
        "cached handle for an identity-banned transfer target");
    foreach (var account in transferBlockedAccounts)
    {
        identityStore.SetBan(
            SyntheticSubject(account), false, $"unban-transfer-{account:N}",
            new string('b', 64), clock.GetUtcNow());
    }
    var leavingAccount = firstJoiners.First(account => account != targetAccount);
    var leaveResults = await Task.WhenAll(Enumerable.Range(0, 100).Select(index => AttemptAsync(() =>
        store.LeaveAsync(
            serverId, seasonId, leavingAccount,
            $"leave-race-{index:0000}", default))));
    Assert(leaveResults.Count(result => result.Success) == 1,
        "A 100-way leave race produced more or fewer than one legal transition.");

    // Authoritative facts after membership: settled run, delivered order and
    // task reward. Uncertain/quoted/refunded facts are deliberately excluded.
    clock.Advance(TimeSpan.FromMinutes(10));
    var factAt = clock.GetUtcNow();
    InsertSettledRun(databasePath, targetAccount, seasonId, factAt, 12, 240, "zone-a");
    InsertRun(databasePath, targetAccount, seasonId, factAt, ExtractionSettlementState.Quoted, 99, 9_999, "zone-q");
    InsertRun(databasePath, targetAccount, seasonId, factAt, ExtractionSettlementState.Uncertain, 99, 9_999, "zone-u");
    InsertOrder(databasePath, serverId, targetAccount, seasonId, factAt, ShopOrderState.Delivered, ShopDeliveryState.Delivered, 33, repeats: 20);
    InsertOrder(databasePath, serverId, targetAccount, seasonId, factAt, ShopOrderState.Refunded, ShopDeliveryState.Delivered, 9_999, repeats: 1);
    InsertOrder(databasePath, serverId, targetAccount, seasonId, factAt, ShopOrderState.DeliveryUncertain, ShopDeliveryState.Uncertain, 9_999, repeats: 1);
    InsertTaskReward(databasePath, targetAccount, seasonId, 15, factAt);

    var projections = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
        store.ProjectSeasonAsync(serverId, seasonId, clock.GetUtcNow(), default)));
    Assert(projections.Select(result => result.SnapshotHash).Distinct(StringComparer.Ordinal).Count() == 1,
        "20 projection replays produced divergent snapshots.");
    var projected = await store.GetDashboardAsync(serverId, seasonId, targetAccount, default);
    Assert(projected.TeamContribution is { ResourceItems: 12, ResourceValue: 240, TaskPoints: 15, DeliveredOrders: 1, ActualCurrencySpent: 33 },
        "The projection included a quoted/uncertain/refunded fact or lost a final authoritative fact.");
    Assert(projected.MyContribution == projected.TeamContribution,
        "The player-only contribution view was not bound to the authenticated account fixture.");
    Assert(projected.Goals.All(goal => goal.Progress >= 0) &&
           projected.Goals.Single(goal => goal.Kind == TeamEconomyGoalKind.ResourceValue).Achieved,
        "Frozen team goals did not advance monotonically from authoritative facts.");

    // A real identity ban applied after contribution must remove that account
    // from the current projection, member threshold and public leaderboard.
    // The frozen goal milestone remains monotonic. Restart and unban must
    // reproduce the same two deterministic snapshots.
    var verifiedSnapshotHash = projections[0].SnapshotHash;
    identityStore.SetBan(
        SyntheticSubject(targetAccount), true, "ban-after-contribution",
        new string('b', 64), clock.GetUtcNow());
    var bannedProjection = await store.ProjectSeasonAsync(
        serverId, seasonId, clock.GetUtcNow(), default);
    Assert(bannedProjection.Changed &&
           bannedProjection.SnapshotHash != verifiedSnapshotHash,
        "A post-contribution identity ban did not replace the eligible projection snapshot.");
    var bannedDashboard = await store.GetDashboardAsync(
        serverId, seasonId, firstOwner, default);
    Assert(bannedDashboard.MemberCount == projected.MemberCount - 1 &&
           bannedDashboard.TeamContribution is
           {
               ResourceItems: 0,
               ResourceValue: 0,
               TaskPoints: 0,
               DeliveredOrders: 0,
               ActualCurrencySpent: 0
           },
        "A post-contribution identity ban did not remove current member/contribution eligibility.");
    Assert(bannedDashboard.Goals.SequenceEqual(projected.Goals),
        "A safety exclusion rewound a frozen team goal milestone.");
    var bannedBoard = await store.GetLeaderboardAsync(
        serverId, seasonId, firstOwner, TeamEconomyMetric.TaskPoints, 0, 50, default);
    Assert(bannedBoard.Total == 0,
        "An identity-banned contribution remained in the team leaderboard.");
    store.Dispose();
    store = new TeamEconomyStore(root, options, clock, volatileBans.Contains);
    var restartedBanProjection = await store.ProjectSeasonAsync(
        serverId, seasonId, clock.GetUtcNow(), default);
    Assert(!restartedBanProjection.Changed &&
           restartedBanProjection.SnapshotHash == bannedProjection.SnapshotHash,
        "The identity-ban exclusion changed across process restart.");
    identityStore.SetBan(
        SyntheticSubject(targetAccount), false, "unban-after-contribution",
        new string('b', 64), clock.GetUtcNow());
    var unbannedProjection = await store.ProjectSeasonAsync(
        serverId, seasonId, clock.GetUtcNow(), default);
    Assert(unbannedProjection.Changed &&
           unbannedProjection.SnapshotHash == verifiedSnapshotHash,
        "An audited unban did not restore the deterministic eligible snapshot.");
    var restoredDashboard = await store.GetDashboardAsync(
        serverId, seasonId, targetAccount, default);
    Assert(restoredDashboard.TeamContribution == projected.TeamContribution &&
           restoredDashboard.MemberCount == projected.MemberCount &&
           restoredDashboard.Goals.SequenceEqual(projected.Goals),
        "Unban did not restore contribution/member eligibility while retaining goal history.");
    store.Dispose();
    store = new TeamEconomyStore(root, options, clock, volatileBans.Contains);
    var restartedUnbanProjection = await store.ProjectSeasonAsync(
        serverId, seasonId, clock.GetUtcNow(), default);
    Assert(!restartedUnbanProjection.Changed &&
           restartedUnbanProjection.SnapshotHash == verifiedSnapshotHash,
        "The restored unban projection changed across process restart.");

    volatileBans.Add(SyntheticSubject(targetAccount));
    var volatileBanProjection = await store.ProjectSeasonAsync(
        serverId, seasonId, clock.GetUtcNow(), default);
    Assert(volatileBanProjection.Changed &&
           volatileBanProjection.SnapshotHash == bannedProjection.SnapshotHash,
        "The fail-closed in-memory moderation fence did not exclude team contribution.");
    var volatileBanDashboard = await store.GetDashboardAsync(
        serverId, seasonId, firstOwner, default);
    Assert(volatileBanDashboard.TeamContribution is { ResourceValue: 0, TaskPoints: 0 },
        "The fail-closed in-memory moderation fence leaked current contribution.");
    volatileBans.Remove(SyntheticSubject(targetAccount));
    var volatileBanCleared = await store.ProjectSeasonAsync(
        serverId, seasonId, clock.GetUtcNow(), default);
    Assert(volatileBanCleared.Changed &&
           volatileBanCleared.SnapshotHash == verifiedSnapshotHash,
        "Clearing the in-memory moderation fence did not restore eligibility.");

    UpdateTaskReward(databasePath, targetAccount, seasonId, 15, 14);
    await AssertCodeAsync(
        () => store.ProjectSeasonAsync(serverId, seasonId, clock.GetUtcNow(), default),
        "TEAM_PROJECTION_NON_MONOTONIC",
        "non-excluded authoritative source correction");
    var preservedAfterCorrection = await store.GetDashboardAsync(
        serverId, seasonId, targetAccount, default);
    Assert(preservedAfterCorrection.TeamContribution == projected.TeamContribution,
        "A non-excluded source correction replaced the last verified projection.");
    UpdateTaskReward(databasePath, targetAccount, seasonId, 14, 15);
    var correctedBack = await store.ProjectSeasonAsync(
        serverId, seasonId, clock.GetUtcNow(), default);
    Assert(!correctedBack.Changed && correctedBack.SnapshotHash == verifiedSnapshotHash,
        "Restoring an authoritative source did not recover the verified projection.");

    // A post-leave fact is excluded. Rejoining another team does not transfer
    // prior contribution, while facts after the new join accrue only there.
    clock.Advance(TimeSpan.FromMinutes(1));
    InsertTaskReward(databasePath, leavingAccount, seasonId, 30, clock.GetUtcNow());
    await store.ProjectSeasonAsync(serverId, seasonId, clock.GetUtcNow(), default);
    var oldTeamAfterLeave = await store.GetDashboardAsync(
        serverId, seasonId, targetAccount, default);
    Assert(oldTeamAfterLeave.TeamContribution!.TaskPoints == 15,
        "A contribution after leave leaked into the old team.");
    clock.Advance(TimeSpan.FromSeconds(1));
    var newOwner = teams[1].Owner;
    var newInvite = await store.RotateInviteAsync(
        serverId, seasonId, newOwner, 20, "switch-invite-0001", default);
    identityStore.SetBan(
        SyntheticSubject(newOwner), true, "ban-invite-owner",
        new string('b', 64), clock.GetUtcNow());
    await AssertCodeAsync(
        () => store.JoinAsync(
            serverId, seasonId, leavingAccount, newInvite.Token!,
            "switch-join-owner-banned", default),
        "TEAM_INVITE_OWNER_INELIGIBLE",
        "invitation whose team owner is identity-banned");
    identityStore.SetBan(
        SyntheticSubject(newOwner), false, "unban-invite-owner",
        new string('b', 64), clock.GetUtcNow());
    await store.JoinAsync(
        serverId, seasonId, leavingAccount, newInvite.Token!, "switch-join-0001", default);
    clock.Advance(TimeSpan.FromMinutes(1));
    InsertTaskReward(databasePath, leavingAccount, seasonId, 40, clock.GetUtcNow());
    var switchedProjection = await store.ProjectSeasonAsync(serverId, seasonId, clock.GetUtcNow(), default);
    var switched = await store.GetDashboardAsync(serverId, seasonId, leavingAccount, default);
    Assert(switched.MyContribution is { TaskPoints: 40 },
        "Switching teams backfilled or transferred historical personal contribution.");
    Assert(oldTeamAfterLeave.TeamContribution.TaskPoints == 15,
        "Switching teams mutated the frozen old-team contribution.");

    // Restart replay proves durable command and projection behavior. Token
    // material and fake raw player identities must not be present in DB/WAL.
    store.Dispose();
    store = new TeamEconomyStore(root, options, clock, volatileBans.Contains);
    var restartedReplay = await store.RotateInviteAsync(
        serverId, seasonId, firstOwner, 20, "invite-first-0001", default);
    Assert(restartedReplay.Replayed && restartedReplay.Token is null,
        "Restart replay exposed or regenerated an invitation bearer token.");
    var restartedProjection = await store.ProjectSeasonAsync(
        serverId, seasonId, clock.GetUtcNow(), default);
    Assert(restartedProjection.SnapshotHash == switchedProjection.SnapshotHash && !restartedProjection.Changed,
        "Restart projection was not deterministic after newly appended facts.");
    var databaseBytes = await ReadDatabaseFamilyAsync(root);
    foreach (var forbidden in new[]
             {
                 invitation.Token!, raceInvite.Token!, newInvite.Token!,
                 "steam_76561198000000000", "00000000000000000000000000000000"
             })
    {
        Assert(!databaseBytes.Contains(forbidden, StringComparison.Ordinal),
            "Team persistence leaked a bearer token, UserId, or PlayerUID.");
    }

    // 1001-team leaderboard: stable complete pagination, deterministic ties
    // and no member/account identity disclosure.
    var leaderboardSeason = Guid.NewGuid();
    InsertSeason(databasePath, serverId, leaderboardSeason, clock.GetUtcNow());
    var leaderboardOwners = new List<Guid>();
    for (var index = 0; index < 1_001; index++)
    {
        var owner = Guid.NewGuid();
        InsertAccount(databasePath, owner, clock.GetUtcNow());
        leaderboardOwners.Add(owner);
        await store.CreateTeamAsync(
            serverId, leaderboardSeason, owner, $"排行榜队伍 {index:0000}",
            $"leaderboard-create-{index:0000}", default);
        InsertTaskReward(databasePath, owner, leaderboardSeason, 1, clock.GetUtcNow());
    }
    await store.ProjectSeasonAsync(serverId, leaderboardSeason, clock.GetUtcNow(), default);
    var firstPage = await store.GetLeaderboardAsync(
        serverId, leaderboardSeason, leaderboardOwners[0],
        TeamEconomyMetric.TaskPoints, 0, 100, default);
    var lastPage = await store.GetLeaderboardAsync(
        serverId, leaderboardSeason, leaderboardOwners[0],
        TeamEconomyMetric.TaskPoints, 1_000, 100, default);
    Assert(firstPage.Total == 1_001 && firstPage.Items.Count == 100 &&
           firstPage.NextCursor == "100" && lastPage.Items.Count == 1,
        "The 1001-team leaderboard was truncated or paged unstably.");
    Assert(firstPage.Items.SequenceEqual(firstPage.Items
            .OrderBy(item => item.ReachedAt).ThenBy(item => item.TeamId)),
        "Leaderboard tie-break is not reachedAt asc then teamId asc.");
    var boardJson = JsonSerializer.Serialize(firstPage);
    Assert(!boardJson.Contains(leaderboardOwners[0].ToString("D"), StringComparison.OrdinalIgnoreCase),
        "Public leaderboard exposed an account identifier.");
    store.Dispose();
    store = new TeamEconomyStore(root, options, clock, volatileBans.Contains);
    var restartedPage = await store.GetLeaderboardAsync(
        serverId, leaderboardSeason, leaderboardOwners[0],
        TeamEconomyMetric.TaskPoints, 0, 100, default);
    Assert(JsonSerializer.Serialize(restartedPage.Items) == JsonSerializer.Serialize(firstPage.Items),
        "Leaderboard ordering changed across process restart.");

    // Overflow is fail-closed and preserves the previous verified snapshot.
    InsertTaskReward(databasePath, leaderboardOwners[0], leaderboardSeason,
        TeamEconomyLimits.WebSafeInteger + 1, clock.GetUtcNow());
    await AssertCodeAsync(
        () => store.ProjectSeasonAsync(serverId, leaderboardSeason, clock.GetUtcNow(), default),
        "TEAM_PROJECTION_OVERFLOW",
        "unsafe task points");
    var preservedPage = await store.GetLeaderboardAsync(
        serverId, leaderboardSeason, leaderboardOwners[0],
        TeamEconomyMetric.TaskPoints, 0, 100, default);
    Assert(JsonSerializer.Serialize(preservedPage.Items) == JsonSerializer.Serialize(firstPage.Items),
        "Overflow replaced the last verified leaderboard snapshot.");

    // Tampering with a projection row must fail closed instead of returning a
    // fabricated zero or continuing from a corrupt snapshot.
    TamperProjection(databasePath, serverId, leaderboardSeason);
    await AssertCodeAsync(
        () => store.GetLeaderboardAsync(
            serverId, leaderboardSeason, leaderboardOwners[0],
            TeamEconomyMetric.TaskPoints, 0, 100, default),
        "TEAM_PROJECTION_INTEGRITY_FAILED",
        "projection row tamper");

    Console.WriteLine(
        "PASS: team schema/commands, one-time HMAC invitations, 100-account/10-team concurrency, " +
        "authoritative membership-time projection, goals, 20x/restart replay, 1001-team ranking, " +
        "privacy, overflow and corruption fail-closed behavior.");
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

static void InitializeAuthoritativeDatabase(string path)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = """
        PRAGMA journal_mode=WAL;
        CREATE TABLE extraction_events(
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            event_id TEXT NOT NULL UNIQUE,
            event_type TEXT NOT NULL,
            occurred_at TEXT NOT NULL,
            payload TEXT NOT NULL);
        CREATE TABLE extraction_settlement_runs(
            run_id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL,
            season_id TEXT NOT NULL,
            user_id TEXT NOT NULL,
            state TEXT NOT NULL,
            revision INTEGER NOT NULL,
            updated_at TEXT NOT NULL,
            payload TEXT NOT NULL);
        CREATE TABLE reliable_task_ranking_rewards(
            entry_id TEXT PRIMARY KEY,
            instance_id TEXT NOT NULL UNIQUE,
            account_id TEXT NOT NULL,
            season_id TEXT NOT NULL,
            points INTEGER NOT NULL,
            balance_after INTEGER NOT NULL,
            created_at TEXT NOT NULL);
        CREATE TABLE season_leaderboard_exclusions(
            season_id TEXT NOT NULL,
            account_id TEXT NOT NULL,
            active INTEGER NOT NULL,
            PRIMARY KEY(season_id, account_id));
        """;
    command.ExecuteNonQuery();
}

static void ExcludeAccount(string path, Guid seasonId, Guid accountId)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO season_leaderboard_exclusions(season_id, account_id, active)
        VALUES($seasonId, $accountId, 1);
        """;
    command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
    command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
    command.ExecuteNonQuery();
}

static void InsertSeason(string path, string serverId, Guid seasonId, DateTimeOffset at)
{
    var season = new ExtractionSeason(
        seasonId, serverId, $"S-{seasonId:N}"[..12], "Harness week", null,
        at.AddDays(-1), at.AddDays(7), ExtractionSeasonState.Active, 1, at, at);
    InsertEnvelope(path, "season_upserted", at, season: season);
}

static string SyntheticSubject(Guid accountId) => $"steam_test-{accountId:N}";

static void InsertAccount(string path, Guid accountId, DateTimeOffset at)
{
    var account = new ExtractionAccount(
        accountId,
        "steam",
        SyntheticSubject(accountId),
        "Synthetic team harness account",
        1,
        at,
        at);
    InsertEnvelope(path, "account_created", at, account: account);
}

static void InsertTaskReward(
    string path,
    Guid accountId,
    Guid seasonId,
    long points,
    DateTimeOffset at)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO reliable_task_ranking_rewards(
            entry_id, instance_id, account_id, season_id, points, balance_after, created_at)
        VALUES($entryId, $instanceId, $accountId, $seasonId, $points, $balance, $createdAt);
        """;
    command.Parameters.AddWithValue("$entryId", Guid.NewGuid().ToString("D"));
    command.Parameters.AddWithValue("$instanceId", Guid.NewGuid().ToString("D"));
    command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
    command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
    command.Parameters.AddWithValue("$points", points);
    command.Parameters.AddWithValue("$balance", points);
    command.Parameters.AddWithValue("$createdAt", at.ToString("O"));
    command.ExecuteNonQuery();
}

static void UpdateTaskReward(
    string path,
    Guid accountId,
    Guid seasonId,
    long expectedPoints,
    long replacementPoints)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = """
        UPDATE reliable_task_ranking_rewards
        SET points = $replacement,
            balance_after = $replacement
        WHERE account_id = $accountId
          AND season_id = $seasonId
          AND points = $expected;
        """;
    command.Parameters.AddWithValue("$replacement", replacementPoints);
    command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
    command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
    command.Parameters.AddWithValue("$expected", expectedPoints);
    Assert(command.ExecuteNonQuery() == 1,
        "The task correction fixture did not update exactly one source row.");
}

static void InsertSettledRun(
    string path,
    Guid accountId,
    Guid seasonId,
    DateTimeOffset at,
    int quantity,
    long value,
    string zoneId) => InsertRun(
        path, accountId, seasonId, at,
        ExtractionSettlementState.Settled, quantity, value, zoneId);

static void InsertRun(
    string path,
    Guid accountId,
    Guid seasonId,
    DateTimeOffset at,
    ExtractionSettlementState state,
    int quantity,
    long value,
    string zoneId)
{
    var runId = Guid.NewGuid();
    var run = new ExtractionSettlementRun(
        runId, accountId, seasonId, "redacted-harness-user", zoneId, zoneId,
        state,
        [new ExtractionLootLine("Stone", "Stone", quantity, value / quantity, value)],
        quantity, value, new string('a', 64), null, null, null, null,
        at.AddMinutes(-1), at.AddMinutes(5), at,
        state == ExtractionSettlementState.Settled ? at : null)
    {
        Revision = 2,
        StateChangedAt = at
    };
    var json = JsonSerializer.Serialize(run, JsonOptions());
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO extraction_settlement_runs(
            run_id, account_id, season_id, user_id, state, revision, updated_at, payload)
        VALUES($runId, $accountId, $seasonId, 'redacted', $state, $revision, $updatedAt, $payload);
        """;
    command.Parameters.AddWithValue("$runId", runId.ToString("D"));
    command.Parameters.AddWithValue("$accountId", accountId.ToString("D"));
    command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
    command.Parameters.AddWithValue("$state", state.ToString());
    command.Parameters.AddWithValue("$revision", run.Revision);
    command.Parameters.AddWithValue("$updatedAt", at.ToString("O"));
    command.Parameters.AddWithValue("$payload", json);
    command.ExecuteNonQuery();
}

static void InsertOrder(
    string path,
    string serverId,
    Guid accountId,
    Guid seasonId,
    DateTimeOffset at,
    ShopOrderState orderState,
    ShopDeliveryState deliveryState,
    long charge,
    int repeats)
{
    var orderId = Guid.NewGuid();
    var deliveryId = Guid.NewGuid();
    var line = new ShopOrderLine(
        Guid.NewGuid(), Guid.NewGuid(), "harness-item", "Harness", 1,
        ExtractionCurrency.MarketCoin, charge, charge, [], "general", []);
    for (var index = 0; index < repeats; index++)
    {
        var eventAt = at.AddTicks(index);
        var order = new ShopOrder(
            orderId, accountId, seasonId, serverId, "redacted", [line],
            [new ShopOrderCharge(ExtractionCurrency.MarketCoin, charge)],
            orderState, deliveryId, 1, "redacted", "system", "harness",
            at.AddMinutes(-1), at);
        var delivery = new ShopDelivery(
            deliveryId, orderId, 1, deliveryState, "redacted", Guid.NewGuid(),
            null, null, at.AddMinutes(-1), at.AddSeconds(-1), at);
        InsertEnvelope(path, "delivery_updated", eventAt, order: order, delivery: delivery);
    }
}

static void InsertEnvelope(
    string path,
    string eventType,
    DateTimeOffset at,
    ExtractionSeason? season = null,
    ExtractionAccount? account = null,
    ShopOrder? order = null,
    ShopDelivery? delivery = null)
{
    var eventId = Guid.NewGuid();
    var payload = JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        eventId,
        eventType,
        at,
        season,
        account,
        order,
        delivery
    }, JsonOptions());
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO extraction_events(event_id, event_type, occurred_at, payload)
        VALUES($eventId, $eventType, $occurredAt, $payload);
        """;
    command.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
    command.Parameters.AddWithValue("$eventType", eventType);
    command.Parameters.AddWithValue("$occurredAt", at.ToString("O"));
    command.Parameters.AddWithValue("$payload", payload);
    command.ExecuteNonQuery();
}

static void TamperProjection(string path, string serverId, Guid seasonId)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = """
        UPDATE team_economy_projection_events
        SET task_points = task_points + 1
        WHERE event_key = (
            SELECT event_key FROM team_economy_projection_events
            WHERE server_id = $serverId AND season_id = $seasonId
            ORDER BY event_key LIMIT 1);
        """;
    command.Parameters.AddWithValue("$serverId", serverId);
    command.Parameters.AddWithValue("$seasonId", seasonId.ToString("D"));
    Assert(command.ExecuteNonQuery() == 1, "Projection tamper fixture found no row.");
}

static SqliteConnection Open(string path)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = false
    }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
    command.ExecuteNonQuery();
    return connection;
}

static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web);

static async Task<string> ReadDatabaseFamilyAsync(string directory)
{
    var bytes = new List<byte>();
    foreach (var file in Directory.EnumerateFiles(directory, "extraction-commerce.db*"))
    {
        bytes.AddRange(await File.ReadAllBytesAsync(file));
    }
    return Encoding.UTF8.GetString(bytes.ToArray());
}

static async Task<(bool Success, string? Code)> AttemptAsync(Func<Task> action)
{
    try
    {
        await action();
        return (true, null);
    }
    catch (TeamEconomyException exception)
    {
        return (false, exception.Code);
    }
}

static async Task AssertCodeAsync(Func<Task> action, string expectedCode, string scenario)
{
    try
    {
        await action();
        throw new InvalidOperationException($"Expected {expectedCode} for {scenario}.");
    }
    catch (TeamEconomyException exception) when (exception.Code == expectedCode)
    {
        // Expected stable failure.
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset _utcNow = initial;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan value) => _utcNow = _utcNow.Add(value);
}
