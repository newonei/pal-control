using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

/// <summary>
/// Replays authoritative SQLite facts into team-only read models. It has no
/// dependency on wallet, delivery, settlement, inventory or task writers, so
/// retries and crashes cannot repeat an economic action.
/// </summary>
public sealed class TeamEconomyProjectionWorker : BackgroundService
{
    private readonly TeamEconomyStore _store;
    private readonly ExtractionCommerceService _commerce;
    private readonly TeamEconomyOptions _options;
    private readonly ExtractionModeOptions _extractionOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TeamEconomyProjectionWorker> _logger;

    public TeamEconomyProjectionWorker(
        TeamEconomyStore store,
        ExtractionCommerceService commerce,
        IOptions<TeamEconomyOptions> options,
        IOptions<ExtractionModeOptions> extractionOptions,
        TimeProvider timeProvider,
        ILogger<TeamEconomyProjectionWorker> logger)
    {
        _store = store;
        _commerce = commerce;
        _options = options.Value;
        _extractionOptions = extractionOptions.Value;
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
            "select-active-season");
        TeamEconomyScope? teamScope;
        try
        {
            var seasons = await _commerce.ListSeasonsAsync(
                _extractionOptions.ServerId,
                cancellationToken);
            teamScope = SelectActiveScope(
                _extractionOptions.ServerId,
                seasons,
                _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TeamEconomyException exception)
        {
            _logger.LogWarning(
                "Team economy active-season selection failed closed. Code={Code}",
                exception.Code);
            return;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or SqliteException)
        {
            _logger.LogSafeWarning(
                exception,
                "Team economy active-season selection failed closed. Code={Code}",
                "TEAM_ACTIVE_SEASON_SOURCE_UNAVAILABLE");
            return;
        }

        // No active weekly world is a safe startup/maintenance state. Never
        // fall back to enumerating historical team scopes.
        if (teamScope is null)
        {
            return;
        }

        using var projectionScope = ControlPlaneLog.BeginWorker(
            _logger,
            "TeamEconomyProjection",
            "project-active-season",
            stableOperationId: teamScope.SeasonId,
            serverId: teamScope.ServerId);
        try
        {
            await _store.ProjectActiveSeasonAsync(
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
            exception is IOException or InvalidDataException or SqliteException)
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

    internal static TeamEconomyScope? SelectActiveScope(
        string configuredServerId,
        IReadOnlyList<ExtractionSeason> seasons,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredServerId);
        ArgumentNullException.ThrowIfNull(seasons);
        now = now.ToUniversalTime();
        var server = configuredServerId.Trim();
        var active = seasons
            .Where(season =>
                season.State == ExtractionSeasonState.Active &&
                !string.IsNullOrWhiteSpace(season.ServerId) &&
                string.Equals(season.ServerId.Trim(), server, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (active.Length == 0)
        {
            return null;
        }
        if (active.Length != 1)
        {
            throw new TeamEconomyException(
                "TEAM_ACTIVE_SEASON_AMBIGUOUS",
                "The authoritative economy store did not select exactly one active weekly world.",
                StatusCodes.Status503ServiceUnavailable);
        }

        var selected = active[0];
        if (selected.SeasonId == Guid.Empty ||
            string.IsNullOrWhiteSpace(selected.WorldId) ||
            selected.StartsAt == default ||
            selected.EndsAt <= selected.StartsAt ||
            now < selected.StartsAt ||
            now >= selected.EndsAt)
        {
            throw new TeamEconomyException(
                "TEAM_ACTIVE_SEASON_INVALID",
                "The authoritative active weekly world is unbound, malformed, or outside its valid window.",
                StatusCodes.Status503ServiceUnavailable);
        }

        return new TeamEconomyScope(server.ToLowerInvariant(), selected.SeasonId);
    }
}
