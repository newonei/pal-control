using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;

namespace PalControl.ControlApi.Infrastructure;

public sealed class PalworldRestOptions
{
    public string BaseUrl { get; init; } = "http://127.0.0.1:8212/v1/api/";
    public string Username { get; init; } = "admin";
    public string Password { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 3;
}

public sealed record PalworldServerInfo(
    string Version,
    string ServerName,
    string Description,
    string WorldGuid);

public sealed record PalworldMetrics(
    int ServerFps,
    int CurrentPlayerNum,
    double ServerFrameTime,
    int MaxPlayerNum,
    long Uptime,
    int BaseCampNum,
    int Days);

public sealed record PalworldPlayerLocation(
    string PlayerId,
    string? Uid,
    string Name,
    int? Level,
    double LocationX,
    double LocationY);

public sealed record PalworldCommandResult(
    bool Success,
    bool Uncertain,
    int? HttpStatus,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static PalworldCommandResult Delivered(int status) =>
        new(true, false, status, null, null);

    public static PalworldCommandResult Failed(int status, string code, string message) =>
        new(false, false, status, code, message);

    public static PalworldCommandResult OutcomeUncertain(
        string code,
        string message,
        int? httpStatus = null) =>
        new(false, true, httpStatus, code, message);
}

public sealed class PalworldRestClient
{
    private readonly HttpClient _httpClient;
    private readonly PalworldRestOptions _options;
    private readonly ILogger<PalworldRestClient> _logger;

    public PalworldRestClient(
        HttpClient httpClient,
        IOptions<PalworldRestOptions> options,
        ILogger<PalworldRestClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PalworldServerInfo?> TryGetInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, "info");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Palworld REST info returned status {StatusCode}.",
                    (int)response.StatusCode);
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<InfoResponse>(cancellationToken);
            return dto is null
                ? null
                : new PalworldServerInfo(
                    dto.Version,
                    dto.ServerName,
                    dto.Description,
                    dto.WorldGuid);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Palworld REST info request timed out.");
            return null;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Palworld REST info is unavailable.");
            return null;
        }
    }

    public async Task<IReadOnlyList<PlayerSummary>?> TryGetPlayersAsync(
        CancellationToken cancellationToken)
    {
        var players = await TryGetPlayerLocationsAsync(cancellationToken);
        return players?.Select(player => new PlayerSummary(
                PlayerId: player.PlayerId,
                Uid: player.Uid,
                Name: player.Name,
                Online: true,
                Level: player.Level))
            .ToArray();
    }

    public async Task<IReadOnlyList<PalworldPlayerLocation>?> TryGetPlayerLocationsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, "players");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Palworld REST players returned status {StatusCode}.",
                    (int)response.StatusCode);
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<PlayersResponse>(cancellationToken);
            return dto?.Players.Select(player => new PalworldPlayerLocation(
                    PlayerId: player.PlayerId,
                    Uid: player.UserId,
                    Name: player.Name,
                    Level: player.Level,
                    LocationX: player.LocationX,
                    LocationY: player.LocationY))
                .ToArray() ?? [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Palworld REST players request timed out.");
            return null;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Palworld REST players is unavailable.");
            return null;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Palworld REST players returned invalid JSON.");
            return null;
        }
    }

    public async Task<PalworldMetrics?> TryGetMetricsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, "metrics");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<MetricsResponse>(cancellationToken);
            return dto is null
                ? null
                : new PalworldMetrics(
                    dto.ServerFps,
                    dto.CurrentPlayerNum,
                    dto.ServerFrameTime,
                    dto.MaxPlayerNum,
                    dto.Uptime,
                    dto.BaseCampNum,
                    dto.Days);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<PalworldCommandResult> AnnounceAsync(
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Post, "announce");
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { message }),
                Encoding.UTF8,
                "application/json");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return PalworldCommandResult.Delivered((int)response.StatusCode);
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var detail = string.IsNullOrWhiteSpace(responseBody)
                ? $"Official REST returned HTTP {(int)response.StatusCode}."
                : responseBody.Trim()[..Math.Min(responseBody.Trim().Length, 512)];
            _logger.LogWarning(
                "Palworld REST announce returned status {StatusCode}.",
                (int)response.StatusCode);
            if ((int)response.StatusCode >= 500)
            {
                return PalworldCommandResult.OutcomeUncertain(
                    "PALWORLD_ANNOUNCE_OUTCOME_UNCERTAIN",
                    $"Official REST returned HTTP {(int)response.StatusCode} after dispatch. The announcement will not be sent again automatically. {detail}",
                    (int)response.StatusCode);
            }
            return PalworldCommandResult.Failed(
                (int)response.StatusCode,
                "PALWORLD_ANNOUNCE_REJECTED",
                detail);
        }
        catch (OperationCanceledException)
        {
            return PalworldCommandResult.OutcomeUncertain(
                "PALWORLD_ANNOUNCE_OUTCOME_UNCERTAIN",
                "The official REST announcement timed out or was cancelled after dispatch; it will not be sent again automatically.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Palworld REST announce connection ended after dispatch.");
            return PalworldCommandResult.OutcomeUncertain(
                "PALWORLD_ANNOUNCE_OUTCOME_UNCERTAIN",
                "The official REST connection ended after dispatch; it will not be sent again automatically.");
        }
    }

    public async Task<PalworldCommandResult> SaveWorldAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Post, "save");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return PalworldCommandResult.Delivered((int)response.StatusCode);
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var trimmed = responseBody.Trim();
            var detail = trimmed.Length == 0
                ? $"Official REST returned HTTP {(int)response.StatusCode}."
                : trimmed[..Math.Min(trimmed.Length, 512)];
            _logger.LogWarning(
                "Palworld REST save returned status {StatusCode}.",
                (int)response.StatusCode);
            if ((int)response.StatusCode >= 500)
            {
                return PalworldCommandResult.OutcomeUncertain(
                    "PALWORLD_SAVE_OUTCOME_UNCERTAIN",
                    $"Official REST returned HTTP {(int)response.StatusCode} after the save request was dispatched. It will not be sent again automatically. {detail}",
                    (int)response.StatusCode);
            }

            return PalworldCommandResult.Failed(
                (int)response.StatusCode,
                "PALWORLD_SAVE_REJECTED",
                detail);
        }
        catch (OperationCanceledException)
        {
            return PalworldCommandResult.OutcomeUncertain(
                "PALWORLD_SAVE_OUTCOME_UNCERTAIN",
                "The official REST save request timed out or was cancelled after dispatch; it will not be sent again automatically.");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Palworld REST save connection ended after dispatch.");
            return PalworldCommandResult.OutcomeUncertain(
                "PALWORLD_SAVE_OUTCOME_UNCERTAIN",
                "The official REST connection ended after the save request was dispatched; it will not be sent again automatically.");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var credentials = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{_options.Username}:{_options.Password}"));
        var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private sealed record InfoResponse(
        string Version,
        [property: JsonPropertyName("servername")] string ServerName,
        string Description,
        [property: JsonPropertyName("worldguid")] string WorldGuid);

    private sealed record PlayersResponse(IReadOnlyList<PlayerResponse> Players);

    private sealed record PlayerResponse(
        string Name,
        string PlayerId,
        string UserId,
        [property: JsonPropertyName("location_x")] double LocationX,
        [property: JsonPropertyName("location_y")] double LocationY,
        int Level);

    private sealed record MetricsResponse(
        [property: JsonPropertyName("serverfps")] int ServerFps,
        [property: JsonPropertyName("currentplayernum")] int CurrentPlayerNum,
        [property: JsonPropertyName("serverframetime")] double ServerFrameTime,
        [property: JsonPropertyName("maxplayernum")] int MaxPlayerNum,
        long Uptime,
        [property: JsonPropertyName("basecampnum")] int BaseCampNum,
        int Days);
}
