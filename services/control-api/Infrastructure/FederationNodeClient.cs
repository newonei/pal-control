using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PalControl.ControlApi.Infrastructure;

public interface IFederationNodeTransport
{
    Task<FederationLocalProfile> GetProfileAsync(
        FederationNodeOptions node,
        FederationProfileRequest request,
        CancellationToken cancellationToken);

    Task<FederationNodeHealth> GetHealthAsync(
        FederationNodeOptions node,
        CancellationToken cancellationToken);
}

public sealed class FederationNodeClient : IFederationNodeTransport
{
    public const string ExpectedCombinationHeader =
        FederationProtocol.ExpectedCombinationHeader;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    private readonly HttpClient _httpClient;
    private readonly FederationOptions _options;
    private readonly Dictionary<string, OutboundSigningCredential> _nodeKeys;
    private readonly SemaphoreSlim _concurrency;
    private readonly TimeProvider _timeProvider;

    public FederationNodeClient(
        IHttpClientFactory httpClientFactory,
        IOptions<FederationOptions> options,
        IHostEnvironment environment,
        TimeProvider timeProvider)
        : this(
            httpClientFactory.CreateClient("FederationNodes"),
            options.Value,
            environment.ContentRootPath,
            timeProvider)
    {
    }

    internal FederationNodeClient(
        HttpClient httpClient,
        FederationOptions options,
        string contentRootPath,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _options = options;
        _nodeKeys = options.Nodes
            .Where(node => !node.Local)
            .ToDictionary(
                node => node.ServerId,
                node => new OutboundSigningCredential(
                    node.SigningKeyId,
                    FederationSecretResolver.ResolveRequired(
                        node.NodeKey,
                        node.NodeKeyFile,
                        contentRootPath,
                        $"Federation node key for '{node.ServerId}'")),
                StringComparer.Ordinal);
        _concurrency = new SemaphoreSlim(options.MaximumConcurrentRequests);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<FederationLocalProfile> GetProfileAsync(
        FederationNodeOptions node,
        FederationProfileRequest profileRequest,
        CancellationToken cancellationToken)
    {
        if (node.Local)
        {
            throw new ArgumentException(
                "The remote federation client cannot call the local node.",
                nameof(node));
        }
        if (profileRequest.ProtocolVersion != FederationOptions.CurrentProtocolVersion ||
            !string.Equals(profileRequest.CallerServerId, _options.LocalServerId, StringComparison.Ordinal) ||
            !string.Equals(profileRequest.TargetServerId, node.ServerId, StringComparison.Ordinal) ||
            !string.Equals(profileRequest.IdentityKeyId, node.IdentityKeyId, StringComparison.Ordinal))
        {
            throw new FederationException(
                "FEDERATION_OUTBOUND_BINDING_INVALID",
                "The outbound federation profile request is not bound to the configured caller, target, and identity key.",
                StatusCodes.Status503ServiceUnavailable);
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(profileRequest, JsonOptions);
        var request = CreateSignedRequest(
            node,
            HttpMethod.Post,
            "api/v1/internal/federation/profile",
            node.IdentityKeyId,
            payload);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await SendAsync<FederationLocalProfile>(
            request,
            node.ServerId,
            cancellationToken);
    }

    public async Task<FederationNodeHealth> GetHealthAsync(
        FederationNodeOptions node,
        CancellationToken cancellationToken)
    {
        if (node.Local)
        {
            throw new ArgumentException(
                "The remote federation client cannot call the local node.",
                nameof(node));
        }
        var request = CreateSignedRequest(
            node,
            HttpMethod.Get,
            "api/v1/internal/federation/health",
            FederationProtocol.NoIdentityKey,
            []);
        return await SendAsync<FederationNodeHealth>(
            request,
            node.ServerId,
            cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        HttpRequestMessage request,
        string serverId,
        CancellationToken cancellationToken)
    {
        using (request)
        {
            await _concurrency.WaitAsync(cancellationToken);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                timeout.CancelAfter(_options.RequestTimeoutMilliseconds);
                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw Unavailable(
                        "FEDERATION_NODE_TIMEOUT",
                        serverId,
                        "timed out");
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or IOException)
                {
                    throw Unavailable(
                        "FEDERATION_NODE_UNAVAILABLE",
                        serverId,
                        "could not be reached");
                }

                using (response)
                {
                    if ((int)response.StatusCode is >= 300 and < 400)
                    {
                        throw Unavailable(
                            "FEDERATION_REDIRECT_REJECTED",
                            serverId,
                            "returned a redirect");
                    }
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        throw Unavailable(
                            "FEDERATION_NODE_AUTH_REJECTED",
                            serverId,
                            "rejected node authentication");
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        throw Unavailable(
                            "FEDERATION_NODE_UNAVAILABLE",
                            serverId,
                            $"returned HTTP {(int)response.StatusCode}");
                    }
                    if (response.Content.Headers.ContentLength is long length &&
                        length > _options.MaximumResponseBytes)
                    {
                        throw Unavailable(
                            "FEDERATION_RESPONSE_OVERSIZED",
                            serverId,
                            "returned an oversized response");
                    }
                    if (response.Content.Headers.ContentType?.MediaType is not string mediaType ||
                        !string.Equals(
                            mediaType,
                            "application/json",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw Unavailable(
                            "FEDERATION_RESPONSE_CONTENT_TYPE_INVALID",
                            serverId,
                            "returned a non-JSON response");
                    }

                    byte[] payload;
                    try
                    {
                        payload = await ReadBoundedAsync(
                            response.Content,
                            _options.MaximumResponseBytes,
                            timeout.Token);
                    }
                    catch (OperationCanceledException) when (
                        !cancellationToken.IsCancellationRequested)
                    {
                        throw Unavailable(
                            "FEDERATION_NODE_TIMEOUT",
                            serverId,
                            "timed out while reading its response");
                    }
                    try
                    {
                        return JsonSerializer.Deserialize<T>(payload, JsonOptions)
                            ?? throw new JsonException("Empty federation response.");
                    }
                    catch (JsonException)
                    {
                        throw Unavailable(
                            "FEDERATION_RESPONSE_INVALID",
                            serverId,
                            "returned invalid JSON");
                    }
                }
            }
            finally
            {
                _concurrency.Release();
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }
            if (output.Length + read > maximumBytes)
            {
                throw new FederationException(
                    "FEDERATION_RESPONSE_OVERSIZED",
                    "Federation node returned an oversized response.",
                    StatusCodes.Status503ServiceUnavailable);
            }
            output.Write(buffer, 0, read);
        }
    }

    private HttpRequestMessage CreateSignedRequest(
        FederationNodeOptions node,
        HttpMethod method,
        string relativePath,
        string identityKeyId,
        ReadOnlySpan<byte> body)
    {
        var credential = RequireNodeKey(node.ServerId);
        var request = new HttpRequestMessage(
            method,
            new Uri(new Uri(node.BaseUri, UriKind.Absolute), relativePath));
        var path = "/" + relativePath.TrimStart('/');
        var timestamp = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var nonce = FederationProtocol.NewNonce();
        var contentHash = FederationProtocol.ContentSha256(body);
        var signature = FederationProtocol.Sign(
            credential.Secret,
            method.Method,
            path,
            _options.LocalServerId,
            node.ServerId,
            credential.KeyId,
            identityKeyId,
            node.ExpectedCombinationId,
            timestamp,
            nonce,
            contentHash);
        AddHeader(request, FederationProtocol.ProtocolHeader,
            FederationOptions.CurrentProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddHeader(request, FederationProtocol.CallerHeader, _options.LocalServerId);
        AddHeader(request, FederationProtocol.TargetHeader, node.ServerId);
        AddHeader(request, FederationProtocol.SigningKeyIdHeader, credential.KeyId);
        AddHeader(request, FederationProtocol.IdentityKeyIdHeader, identityKeyId);
        AddHeader(request, FederationProtocol.ExpectedCombinationHeader, node.ExpectedCombinationId);
        AddHeader(request, FederationProtocol.TimestampHeader,
            timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddHeader(request, FederationProtocol.NonceHeader, nonce);
        AddHeader(request, FederationProtocol.ContentSha256Header, contentHash);
        AddHeader(request, FederationProtocol.SignatureHeader, signature);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return request;
    }

    private static void AddHeader(HttpRequestMessage request, string name, string value)
    {
        if (!request.Headers.TryAddWithoutValidation(name, value))
        {
            throw new InvalidOperationException($"Could not add required federation header '{name}'.");
        }
    }

    private OutboundSigningCredential RequireNodeKey(string serverId) =>
        _nodeKeys.GetValueOrDefault(serverId)
        ?? throw new FederationException(
            "FEDERATION_NODE_KEY_MISSING",
            $"Federation node '{serverId}' has no outbound authentication key.",
            StatusCodes.Status503ServiceUnavailable);

    private sealed record OutboundSigningCredential(string KeyId, byte[] Secret);

    private static FederationException Unavailable(
        string code,
        string serverId,
        string reason) => new(
            code,
            $"Federation node '{serverId}' {reason}.",
            StatusCodes.Status503ServiceUnavailable);
}

public sealed class FederationAggregationService
{
    private readonly FederationOptions _options;
    private readonly CompatibilityMatrixSnapshot _matrix;
    private readonly FederationIdentityProtector _identity;
    private readonly FederationLocalProfileService _localProfiles;
    private readonly IFederationNodeTransport _transport;
    private readonly TimeProvider _timeProvider;

    public FederationAggregationService(
        IOptions<FederationOptions> options,
        CompatibilityMatrixStore matrix,
        FederationIdentityProtector identity,
        FederationLocalProfileService localProfiles,
        IFederationNodeTransport transport,
        TimeProvider timeProvider)
        : this(
            options.Value,
            matrix.Snapshot,
            identity,
            localProfiles,
            transport,
            timeProvider)
    {
    }

    internal FederationAggregationService(
        FederationOptions options,
        CompatibilityMatrixSnapshot matrix,
        FederationIdentityProtector identity,
        FederationLocalProfileService localProfiles,
        IFederationNodeTransport transport,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _matrix = matrix;
        _identity = identity;
        _localProfiles = localProfiles;
        _transport = transport;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<FederationAccountOverview> GetAccountOverviewAsync(
        string identityProvider,
        string externalUserId,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new FederationException(
                "FEDERATION_DISABLED",
                "Federation is disabled.",
                StatusCodes.Status404NotFound);
        }
        var tasks = _options.Nodes.Select(node =>
            GetNodeSummaryAsync(
                node,
                identityProvider,
                externalUserId,
                cancellationToken));
        var summaries = await Task.WhenAll(tasks);
        return new FederationAccountOverview(
            _options.LocalServerId,
            _matrix.Document.MatrixVersion,
            _matrix.CanonicalSha256,
            summaries,
            _timeProvider.GetUtcNow());
    }

    public async Task<IReadOnlyList<FederationNodeHealth>> GetHealthAsync(
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return [];
        }
        var tasks = _options.Nodes.Select(node =>
            GetNodeHealthAsync(node, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private async Task<FederationNodeSummary> GetNodeSummaryAsync(
        FederationNodeOptions node,
        string identityProvider,
        string externalUserId,
        CancellationToken cancellationToken)
    {
        try
        {
            var profile = node.Local
                ? await _localProfiles.GetLocalAsync(
                    identityProvider,
                    externalUserId,
                    cancellationToken)
                : await _transport.GetProfileAsync(
                    node,
                    new FederationProfileRequest(
                        FederationOptions.CurrentProtocolVersion,
                        _options.LocalServerId,
                        node.ServerId,
                        node.IdentityKeyId,
                        _identity.Protect(
                            node.IdentityKeyId,
                            _options.LocalServerId,
                            node.ServerId,
                            identityProvider,
                            externalUserId)),
                    cancellationToken);
            var validationError = ValidateRemoteProfile(node, profile);
            if (validationError is not null)
            {
                return UnavailableSummary(
                    node,
                    "incompatible",
                    validationError,
                    profile.Compatibility);
            }
            return new FederationNodeSummary(
                node.ServerId,
                node.DisplayName,
                node.PortalUrl,
                node.Local,
                "available",
                profile.AccountExists,
                profile.AccountDisplayName,
                profile.Season,
                profile.Balances,
                profile.BalancesAvailable,
                profile.Compatibility,
                SwitchAvailable: !node.Local,
                ErrorCode: null,
                profile.ObservedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FederationException exception)
        {
            return UnavailableSummary(
                node,
                "unavailable",
                exception.Code,
                compatibility: null);
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException or JsonException)
        {
            return UnavailableSummary(
                node,
                "unavailable",
                "FEDERATION_NODE_UNAVAILABLE",
                compatibility: null);
        }
    }

    private async Task<FederationNodeHealth> GetNodeHealthAsync(
        FederationNodeOptions node,
        CancellationToken cancellationToken)
    {
        try
        {
            var health = node.Local
                ? _localProfiles.GetHealth()
                : await _transport.GetHealthAsync(node, cancellationToken);
            if (!string.Equals(health.ServerId, node.ServerId, StringComparison.Ordinal) ||
                health.Compatibility is null)
            {
                return FailedHealth(node.ServerId, "FEDERATION_RESPONSE_MISMATCH");
            }
            var validation = ValidateCompatibility(node, health.Compatibility);
            return validation is null
                ? health
                : FailedHealth(node.ServerId, validation, health.Compatibility);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FederationException exception)
        {
            return FailedHealth(node.ServerId, exception.Code);
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException or JsonException)
        {
            return FailedHealth(node.ServerId, "FEDERATION_NODE_UNAVAILABLE");
        }
    }

    private string? ValidateRemoteProfile(
        FederationNodeOptions node,
        FederationLocalProfile profile)
    {
        if (!string.Equals(profile.ServerId, node.ServerId, StringComparison.Ordinal))
        {
            return "FEDERATION_SERVER_ID_MISMATCH";
        }
        if (profile.Compatibility is null)
        {
            return "FEDERATION_RESPONSE_MISMATCH";
        }
        if (profile.BalancesAvailable != (profile.Balances is not null) ||
            profile.BalancesAvailable && !profile.AccountExists)
        {
            return "FEDERATION_BALANCE_AVAILABILITY_INVALID";
        }
        if (!profile.AccountExists &&
            (profile.AccountDisplayName is not null || profile.Balances is not null))
        {
            return "FEDERATION_ACCOUNT_RESPONSE_INVALID";
        }
        if (profile.AccountDisplayName is { } displayName &&
            (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 160 ||
             displayName.Any(char.IsControl)))
        {
            return "FEDERATION_ACCOUNT_RESPONSE_INVALID";
        }
        const long maximumWebSafeInteger = 9_007_199_254_740_991;
        if (profile.Balances is { } balances &&
            (balances.MarketCoin is < 0 or > maximumWebSafeInteger ||
             balances.SeasonVoucher is < 0 or > maximumWebSafeInteger))
        {
            return "FEDERATION_BALANCE_RESPONSE_INVALID";
        }
        if (profile.Season is { } season &&
            (string.IsNullOrWhiteSpace(season.Code) || season.Code.Length > 128 ||
             string.IsNullOrWhiteSpace(season.DisplayName) || season.DisplayName.Length > 160 ||
             season.StartsAt >= season.EndsAt ||
             season.State is not ("draft" or "scheduled" or "active" or "closed" or "archived")))
        {
            return "FEDERATION_SEASON_RESPONSE_INVALID";
        }
        var now = _timeProvider.GetUtcNow();
        if (profile.ObservedAt < now.AddMinutes(-15) ||
            profile.ObservedAt > now.AddMinutes(5))
        {
            return "FEDERATION_RESPONSE_TIME_INVALID";
        }
        return ValidateCompatibility(node, profile.Compatibility);
    }

    private string? ValidateCompatibility(
        FederationNodeOptions node,
        FederationCompatibilityProfile compatibility)
    {
        if (!string.Equals(
                compatibility.CombinationId,
                node.ExpectedCombinationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                compatibility.MatrixSha256,
                _matrix.CanonicalSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                compatibility.MatrixVersion,
                _matrix.Document.MatrixVersion,
                StringComparison.Ordinal))
        {
            return "FEDERATION_COMPATIBILITY_MISMATCH";
        }
        var expected = _matrix.RequireCombination(node.ExpectedCombinationId);
        var expectedStatus = CompatibilityMatrixValidator.ToWireStatus(expected.Status);
        if (!string.Equals(compatibility.Status, expectedStatus, StringComparison.Ordinal) ||
            !string.Equals(compatibility.GameVersion, expected.GameVersion, StringComparison.Ordinal) ||
            !string.Equals(compatibility.SteamBuild, expected.SteamBuild, StringComparison.Ordinal) ||
            !string.Equals(compatibility.PalDefenderVersion, expected.PalDefenderVersion, StringComparison.Ordinal) ||
            !string.Equals(compatibility.Ue4ssCommit, expected.Ue4ssCommit, StringComparison.Ordinal) ||
            !string.Equals(compatibility.NativeProtocolVersion, expected.NativeProtocolVersion, StringComparison.Ordinal) ||
            !string.Equals(compatibility.NativeModVersion, expected.NativeModVersion, StringComparison.Ordinal) ||
            !string.Equals(compatibility.BridgeAvailability, expected.BridgeAvailability, StringComparison.Ordinal) ||
            compatibility.VerifiedAt != expected.VerifiedAt ||
            compatibility.Capabilities is null ||
            !compatibility.Capabilities.SequenceEqual(expected.Capabilities, StringComparer.Ordinal) ||
            expected.Status == CompatibilityStatus.Quarantined)
        {
            return expected.Status == CompatibilityStatus.Quarantined
                ? "FEDERATION_COMPATIBILITY_QUARANTINED"
                : "FEDERATION_COMPATIBILITY_STATUS_MISMATCH";
        }
        return null;
    }

    private FederationNodeSummary UnavailableSummary(
        FederationNodeOptions node,
        string availability,
        string errorCode,
        FederationCompatibilityProfile? compatibility) => new(
            node.ServerId,
            node.DisplayName,
            node.PortalUrl,
            node.Local,
            availability,
            AccountExists: null,
            AccountDisplayName: null,
            Season: null,
            Balances: null,
            BalancesAvailable: false,
            compatibility,
            SwitchAvailable: false,
            errorCode,
            _timeProvider.GetUtcNow());

    private FederationNodeHealth FailedHealth(
        string serverId,
        string errorCode,
        FederationCompatibilityProfile? compatibility = null) => new(
            serverId,
            compatibility is null ? "unavailable" : "incompatible",
            compatibility,
            errorCode,
            _timeProvider.GetUtcNow());
}
