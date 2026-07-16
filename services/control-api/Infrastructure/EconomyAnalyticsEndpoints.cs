using System.Globalization;
using Microsoft.Extensions.Options;

namespace PalControl.ControlApi.Infrastructure;

public static class EconomyAnalyticsEndpoints
{
    public static RouteGroupBuilder MapEconomyAnalyticsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/economy/analytics", GetAsync)
            .RequireAuthorization(AdminPolicies.Viewer);
        return api;
    }

    private static async Task<IResult> GetAsync(
        string? serverId,
        string? from,
        string? to,
        string? dateBasis,
        Guid? seasonId,
        Guid? contentVersionId,
        int? limit,
        string? cursor,
        EconomyAnalyticsStore analytics,
        IOptions<ExtractionModeOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var basis = ParseDateBasis(dateBasis);
            var timeZone = options.Value.ResolveTimeZone();
            var today = basis == EconomyAnalyticsDateBasis.Utc
                ? DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime)
                : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
                    timeProvider.GetUtcNow(), timeZone).DateTime);
            var defaultTo = today.AddDays(-1);
            var query = new EconomyAnalyticsQuery(
                string.IsNullOrWhiteSpace(serverId) ? options.Value.ServerId : serverId.Trim(),
                ParseDate(from, defaultTo.AddDays(-6), "from"),
                ParseDate(to, defaultTo, "to"),
                basis,
                seasonId,
                contentVersionId,
                limit ?? 50,
                ParseCursor(cursor));
            return Results.Ok(await analytics.QueryAsync(query, cancellationToken));
        }
        catch (EconomyAnalyticsException exception)
        {
            return Results.Json(
                new
                {
                    code = exception.Code,
                    message = exception.Message
                },
                statusCode: exception.StatusCode);
        }
    }

    private static EconomyAnalyticsDateBasis ParseDateBasis(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "business" => EconomyAnalyticsDateBasis.Business,
            "utc" => EconomyAnalyticsDateBasis.Utc,
            _ => throw new EconomyAnalyticsException(
                "ANALYTICS_DATE_BASIS_INVALID",
                "dateBasis must be business or utc.",
                StatusCodes.Status400BadRequest)
        };

    private static DateOnly ParseDate(string? value, DateOnly fallback, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        if (!DateOnly.TryParseExact(
                value.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var result))
        {
            throw new EconomyAnalyticsException(
                "ANALYTICS_DATE_INVALID",
                $"{field} must use yyyy-MM-dd.",
                StatusCodes.Status400BadRequest);
        }
        return result;
    }

    private static int ParseCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }
        if (!int.TryParse(
                value.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var offset) || offset < 0)
        {
            throw new EconomyAnalyticsException(
                "ANALYTICS_CURSOR_INVALID",
                "cursor is invalid.",
                StatusCodes.Status400BadRequest);
        }
        return offset;
    }
}
