using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Infrastructure;

public sealed class PlayerNotificationProjectionWorker : BackgroundService
{
    private static readonly TimeSpan ProjectionInterval = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly IExtractionRepository _repository;
    private readonly ExtractionRunStore _runs;
    private readonly ExtractionDeliveryReceiptStore _receipts;
    private readonly SeasonLeaderboardService _leaderboards;
    private readonly PlayerSeasonSettlementService _seasonSettlements;
    private readonly PlayerNotificationProjectionService _projection;
    private readonly ExtractionModeCoordinator _coordinator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PlayerNotificationProjectionWorker> _logger;

    public PlayerNotificationProjectionWorker(
        IExtractionRepository repository,
        ExtractionRunStore runs,
        ExtractionDeliveryReceiptStore receipts,
        SeasonLeaderboardService leaderboards,
        PlayerSeasonSettlementService seasonSettlements,
        PlayerNotificationProjectionService projection,
        ExtractionModeCoordinator coordinator,
        TimeProvider timeProvider,
        ILogger<PlayerNotificationProjectionWorker> logger)
    {
        _repository = repository;
        _runs = runs;
        _receipts = receipts;
        _leaderboards = leaderboards;
        _seasonSettlements = seasonSettlements;
        _projection = projection;
        _coordinator = coordinator;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var scope = ControlPlaneLog.BeginWorker(
                    _logger,
                    nameof(PlayerNotificationProjectionWorker),
                    "notification.projection");
                try
                {
                    await ProjectOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogSafeError(
                        exception,
                        "Player notification projection pass failed; authoritative economy actions were not retried.");
                }
                await Task.Delay(ProjectionInterval, _timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task ProjectOnceAsync(CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken))
        {
            return;
        }
        try
        {
            var seasons = (await _repository.ListSeasonsAsync(
                    _coordinator.ServerId,
                    cancellationToken))
                .OrderBy(season => season.StartsAt)
                .ToArray();
            var accounts = (await _repository.ListAccountsAsync(cancellationToken))
                .OrderBy(account => account.AccountId)
                .ToArray();

            foreach (var season in seasons)
            {
                foreach (var account in accounts)
                {
                    var orders = await _repository.ListOrdersAsync(
                        account.AccountId,
                        season.SeasonId,
                        1000,
                        cancellationToken);
                    foreach (var order in orders.OrderBy(order => order.CreatedAt))
                    {
                        ExtractionDeliveryReceiptOutcome? receiptOutcome = null;
                        if (order.State == ShopOrderState.DeliveryUncertain)
                        {
                            receiptOutcome = (await _receipts.GetAsync(
                                order.DeliveryId,
                                cancellationToken))?.Receipt?.Outcome;
                        }
                        var source = PlayerNotificationSourceProjector.FromOrder(
                            order,
                            receiptOutcome);
                        if (source is not null)
                        {
                            await ProjectSafelyAsync(source, cancellationToken);
                        }
                    }

                    var runs = await _runs.ListAsync(
                        account.AccountId,
                        season.SeasonId,
                        1000,
                        cancellationToken);
                    foreach (var run in runs.OrderBy(run => run.QuotedAt))
                    {
                        var source = PlayerNotificationSourceProjector.FromRun(run);
                        if (source is not null)
                        {
                            await ProjectSafelyAsync(source, cancellationToken);
                        }
                    }
                }

                if (await _leaderboards.GetAsync(season.SeasonId, cancellationToken) is null)
                {
                    continue;
                }
                foreach (var account in accounts)
                {
                    try
                    {
                        var response = await _seasonSettlements.GetAsync(
                            season.SeasonId,
                            account.AccountId,
                            cancellationToken);
                        if (response?.Settlement is not null)
                        {
                            await ProjectSafelyAsync(
                                PlayerNotificationSourceProjector.FromSeason(
                                    account,
                                    response.Settlement),
                                cancellationToken);
                        }
                    }
                    catch (Exception exception) when (
                        exception is InvalidDataException or InvalidOperationException)
                    {
                        _logger.LogSafeWarning(
                            exception,
                            "Player season notification evidence requires reconciliation for one account and season.");
                        await ProjectSafelyAsync(
                            PlayerNotificationSourceProjector.FromSeasonReconciliation(
                                account,
                                season.SeasonId,
                                _timeProvider.GetUtcNow()),
                            cancellationToken);
                    }
                }
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task ProjectSafelyAsync(
        PlayerNotificationSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            await _projection.ProjectAsync(source, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogSafeError(
                exception,
                "A player notification projection failed; no authoritative economy action was repeated.");
        }
    }
}
