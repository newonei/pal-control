using Microsoft.Extensions.Options;

namespace PalControl.ControlApi.Infrastructure;

/// <summary>
/// Replays authoritative SQLite facts into team-only read models. It has no
/// dependency on wallet, delivery, settlement, inventory or task writers, so
/// retries and crashes cannot repeat an economic action.
/// </summary>
public sealed class TeamEconomyProjectionWorker : BackgroundService
{
    private readonly TeamEconomyStore _store;
    private readonly TeamEconomyOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TeamEconomyProjectionWorker> _logger;

    public TeamEconomyProjectionWorker(
        TeamEconomyStore store,
        IOptions<TeamEconomyOptions> options,
        TimeProvider timeProvider,
        ILogger<TeamEconomyProjectionWorker> logger)
    {
        _store = store;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.ProjectionIntervalSeconds),
            _timeProvider);
        do
        {
            await ProjectOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ProjectOnceAsync(CancellationToken cancellationToken)
    {
        using var scanScope = ControlPlaneLog.BeginWorker(
            _logger,
            "TeamEconomyProjection",
            "scan-scopes");
        var scopes = await _store.ListScopesAsync(cancellationToken);
        foreach (var teamScope in scopes)
        {
            using var projectionScope = ControlPlaneLog.BeginWorker(
                _logger,
                "TeamEconomyProjection",
                "project-season",
                stableOperationId: teamScope.SeasonId,
                serverId: teamScope.ServerId);
            try
            {
                await _store.ProjectSeasonAsync(
                    teamScope.ServerId,
                    teamScope.SeasonId,
                    _timeProvider.GetUtcNow(),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TeamEconomyException exception)
            {
                await _store.RecordProjectionFailureAsync(
                    teamScope.ServerId, teamScope.SeasonId, exception.Code, CancellationToken.None);
                _logger.LogWarning(
                    "Team economy projection retained its last verified snapshot. Code={Code}",
                    exception.Code);
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
            {
                const string code = "TEAM_PROJECTION_SOURCE_UNAVAILABLE";
                await _store.RecordProjectionFailureAsync(
                    teamScope.ServerId, teamScope.SeasonId, code, CancellationToken.None);
                _logger.LogSafeWarning(
                    exception,
                    "Team economy projection retained its last verified snapshot. Code={Code}",
                    code);
            }
        }
    }
}
