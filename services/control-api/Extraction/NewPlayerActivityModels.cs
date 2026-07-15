using System.Text.Json.Serialization;

namespace PalControl.ControlApi.Extraction;

[JsonConverter(typeof(JsonStringEnumConverter<NewPlayerActivityState>))]
public enum NewPlayerActivityState
{
    Draft,
    Published,
    Closed
}

public sealed record NewPlayerActivityDefinition(
    string Title,
    string Description,
    long MarketCoin,
    long SeasonVoucher);

public sealed record NewPlayerActivity(
    Guid ActivityId,
    string ActivityKey,
    int Version,
    NewPlayerActivityState State,
    string Title,
    string Description,
    long MarketCoin,
    long SeasonVoucher,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    long Revision,
    Guid? PublishedSeasonId,
    string? PublishedWorldId,
    string? PublishedBy,
    DateTimeOffset? PublishedAt,
    string? ClosedBy,
    DateTimeOffset? ClosedAt);

public sealed record NewPlayerActivityGrant(
    Guid GrantId,
    Guid ActivityId,
    string ActivityKey,
    int ActivityVersion,
    Guid AccountId,
    Guid SeasonId,
    string WorldId,
    string PlayerUid,
    string PlatformSubject,
    long MarketCoin,
    long SeasonVoucher,
    Guid? MarketCoinLedgerEntryId,
    Guid? SeasonVoucherLedgerEntryId,
    long MarketCoinBalanceAfter,
    long SeasonVoucherBalanceAfter,
    DateTimeOffset ClaimedAt);

public sealed record NewPlayerActivityClaimRequest(
    string ActivityKey,
    int ActivityVersion,
    Guid AccountId,
    Guid SeasonId,
    string WorldId,
    string PlayerUid,
    string PlatformSubject,
    string IdempotencyKey,
    string Actor);

public sealed record NewPlayerActivityClaimResult(
    NewPlayerActivity? Activity,
    NewPlayerActivityGrant? Grant,
    ExtractionWalletSnapshot? Wallet,
    bool Created,
    bool IdempotentReplay,
    bool IdempotencyConflict,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record NewPlayerActivityAvailability(
    NewPlayerActivity Activity,
    NewPlayerActivityGrant? Grant);

public sealed class NewPlayerActivityException : Exception
{
    public NewPlayerActivityException(string code, string message, int statusCode)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public int StatusCode { get; }
}
