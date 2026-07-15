namespace PalControl.ControlApi.Infrastructure;

public sealed record EconomyOutboxObservability(
    bool Ready,
    int Pending,
    int Capacity,
    double? OldestPendingAgeSeconds,
    int Uncertain,
    IReadOnlyDictionary<string, int> States);

public sealed partial class PalDefenderCommandQueue
{
    public async Task<EconomyOutboxObservability> GetObservabilityAsync(
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var states = new[] { "accepted", "dispatched", "succeeded", "failed", "uncertain" }
                .ToDictionary(
                    state => state,
                    state => _commands.Values.Count(command =>
                        string.Equals(command.State, state, StringComparison.Ordinal)),
                    StringComparer.Ordinal);
            states["leased"] = _commands.Values.Count(command =>
                command.State == "accepted" && command.LeaseOwner is not null);
            states["deadLettered"] = _commands.Values.Count(command =>
                command.DeadLetteredAt is not null);
            var pending = _commands.Values
                .Where(command => command.State is "accepted" or "dispatched")
                .ToArray();
            return new EconomyOutboxObservability(
                IsReady,
                pending.Length,
                _capacity,
                pending.Length == 0
                    ? null
                    : Math.Max(
                        0d,
                        (observedAt - pending.Min(command => command.CreatedAt)).TotalSeconds),
                states["uncertain"],
                states);
        }
        finally
        {
            _gate.Release();
        }
    }
}
