using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace PalControl.ControlApi.Infrastructure;

public sealed class PalDefenderRestOptions
{
    public bool Enabled { get; init; }
    public string BaseUrl { get; init; } = "http://127.0.0.1:17993/v1/pdapi/";
    public string Token { get; init; } = string.Empty;
    public string TokenFile { get; init; } = string.Empty;
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public string Origin { get; init; } = "http://127.0.0.1:5180";
    public int TimeoutSeconds { get; init; } = 7;

    public bool IsValid(out string? error)
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !uri.IsLoopback ||
            !uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            error = "BaseUrl must be an absolute loopback HTTP URL ending with '/'.";
            return false;
        }
        if (TimeoutSeconds is < 1 or > 30)
        {
            error = "TimeoutSeconds must be between 1 and 30.";
            return false;
        }
        if (!Uri.TryCreate(Origin, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttp ||
            !origin.IsLoopback)
        {
            error = "Origin must be an absolute loopback HTTP origin.";
            return false;
        }
        if (Enabled &&
            string.IsNullOrWhiteSpace(Token) &&
            string.IsNullOrWhiteSpace(TokenFile))
        {
            error = "An enabled adapter requires Token or TokenFile.";
            return false;
        }
        if (Permissions.Any(permission =>
                string.IsNullOrWhiteSpace(permission) || permission.Length > 128))
        {
            error = "Permissions must contain only non-empty names up to 128 characters.";
            return false;
        }
        error = null;
        return true;
    }
}

public sealed record PalDefenderApiResponse(
    int StatusCode,
    JsonNode? Json,
    string? Text,
    bool TransportError,
    bool OutcomeUncertain,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300 && !TransportError;

    public static PalDefenderApiResponse ConfigurationError(string code, string message) =>
        new(503, null, null, true, false, code, message);

    public static PalDefenderApiResponse TransportFailure(
        string code,
        string message,
        bool outcomeUncertain) =>
        new(503, null, null, true, outcomeUncertain, code, message);
}

public sealed record PalDefenderPermissionProbe(
    bool Success,
    IReadOnlyList<string> MissingPermissions,
    string? ErrorCode,
    string? ErrorMessage);

public sealed class PalDefenderRestClient
{
    private readonly HttpClient _httpClient;
    private readonly PalDefenderRestOptions _options;
    private readonly ILogger<PalDefenderRestClient> _logger;

    public PalDefenderRestClient(
        HttpClient httpClient,
        IOptions<PalDefenderRestOptions> options,
        ILogger<PalDefenderRestClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;
    public string BaseUrl => _options.BaseUrl;

    public async Task<PalDefenderPermissionProbe> ProbeConfiguredPermissionsAsync(
        IReadOnlyCollection<string> requiredPermissions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requiredPermissions);
        IReadOnlyCollection<string> configured;
        if (!string.IsNullOrWhiteSpace(_options.TokenFile))
        {
            try
            {
                var raw = await File.ReadAllTextAsync(_options.TokenFile, cancellationToken);
                using var document = JsonDocument.Parse(raw);
                if (!document.RootElement.TryGetProperty("Permissions", out var permissions) ||
                    permissions.ValueKind != JsonValueKind.Array)
                {
                    return new PalDefenderPermissionProbe(
                        false,
                        requiredPermissions.Order(StringComparer.Ordinal).ToArray(),
                        "PALDEFENDER_PERMISSION_DOCUMENT_INVALID",
                        "The configured PalDefender token file has no Permissions array.");
                }
                configured = permissions
                    .EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return new PalDefenderPermissionProbe(
                    false,
                    requiredPermissions.Order(StringComparer.Ordinal).ToArray(),
                    "PALDEFENDER_PERMISSION_DOCUMENT_UNAVAILABLE",
                    "The configured PalDefender token permissions could not be read.");
            }
        }
        else
        {
            configured = _options.Permissions;
        }

        var granted = configured.ToHashSet(StringComparer.Ordinal);
        var missing = requiredPermissions
            .Where(permission => !granted.Contains(permission))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return missing.Length == 0
            ? new PalDefenderPermissionProbe(true, [], null, null)
            : new PalDefenderPermissionProbe(
                false,
                missing,
                "PALDEFENDER_CAPABILITY_NOT_APPROVED",
                $"The PalDefender credential is missing: {string.Join(", ", missing)}.");
    }

    public Task<PalDefenderApiResponse> GetAsync(
        string relativePath,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, relativePath, null, cancellationToken);

    public Task<PalDefenderApiResponse> PostAsync(
        string relativePath,
        JsonNode? body,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, relativePath, body, cancellationToken);

    private async Task<PalDefenderApiResponse> SendAsync(
        HttpMethod method,
        string relativePath,
        JsonNode? body,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return PalDefenderApiResponse.ConfigurationError(
                "PALDEFENDER_DISABLED",
                "The PalDefender REST adapter is disabled.");
        }

        var tokenResult = await ResolveTokenAsync(cancellationToken);
        if (tokenResult.Error is not null)
        {
            return PalDefenderApiResponse.ConfigurationError(
                "PALDEFENDER_TOKEN_UNAVAILABLE",
                tokenResult.Error);
        }

        try
        {
            using var request = new HttpRequestMessage(method, relativePath);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                tokenResult.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("Origin", _options.Origin);
            if (body is not null)
            {
                request.Content = new StringContent(
                    body.ToJsonString(),
                    Encoding.UTF8,
                    "application/json");
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonNode? responseJson = null;
            if (!string.IsNullOrWhiteSpace(responseText))
            {
                try
                {
                    responseJson = JsonNode.Parse(responseText);
                }
                catch (JsonException)
                {
                    // Preserve non-JSON upstream diagnostics without treating them as transport errors.
                }
            }
            return new PalDefenderApiResponse(
                (int)response.StatusCode,
                responseJson,
                responseJson is null ? responseText : null,
                TransportError: false,
                OutcomeUncertain: false,
                ErrorCode: null,
                ErrorMessage: null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var isWrite = method != HttpMethod.Get;
            _logger.LogWarning(
                "PalDefender REST {Method} {Path} timed out.",
                method,
                LogSafeEndpoint(relativePath));
            return PalDefenderApiResponse.TransportFailure(
                isWrite
                    ? "PALDEFENDER_OUTCOME_UNCERTAIN"
                    : "PALDEFENDER_TIMEOUT",
                isWrite
                    ? "The PalDefender request timed out after dispatch. Its outcome is uncertain and it must not be retried automatically."
                    : "The PalDefender request timed out.",
                isWrite);
        }
        catch (HttpRequestException exception)
        {
            var isWrite = method != HttpMethod.Get;
            _logger.LogWarning(
                "PalDefender REST {Method} {Path} is unavailable ({ExceptionType}).",
                method,
                LogSafeEndpoint(relativePath),
                exception.GetType().Name);
            return PalDefenderApiResponse.TransportFailure(
                isWrite
                    ? "PALDEFENDER_OUTCOME_UNCERTAIN"
                    : "PALDEFENDER_UNAVAILABLE",
                isWrite
                    ? "The PalDefender connection ended after dispatch. Its outcome is uncertain and it must not be retried automatically."
                    : "The PalDefender REST API is unavailable.",
                isWrite);
        }
    }

    private async Task<(string Token, string? Error)> ResolveTokenAsync(
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.Token))
        {
            return (_options.Token.Trim(), null);
        }
        if (string.IsNullOrWhiteSpace(_options.TokenFile))
        {
            return (string.Empty, "No token source is configured.");
        }

        try
        {
            var raw = await File.ReadAllTextAsync(_options.TokenFile, cancellationToken);
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("Token", out var tokenElement) ||
                tokenElement.GetString() is not { Length: > 0 } token)
            {
                return (string.Empty, "The configured token file has no non-empty Token field.");
            }
            return (token.Trim(), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(
                "PalDefender token file could not be read ({ExceptionType}).",
                exception.GetType().Name);
            return (string.Empty, "The configured PalDefender token file could not be read.");
        }
    }

    private static string LogSafeEndpoint(string relativePath)
    {
        var path = relativePath.Split('?', 2)[0];
        var separator = path.IndexOf('/');
        return separator < 0 ? path : $"{path[..separator]}/<redacted>";
    }
}
