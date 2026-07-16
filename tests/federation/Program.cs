using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var repositoryRoot = args.Length == 1
    ? Path.GetFullPath(args[0])
    : FindRepositoryRoot();
var matrixPath = Path.Combine(
    repositoryRoot,
    "services",
    "control-api",
    "Compatibility",
    "compatibility-matrix.v1.json");
var root = Path.Combine(
    Path.GetTempPath(),
    $"pal-control-federation-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);

try
{
    await VerifyCompatibilityMatrixAsync(matrixPath);
    await VerifyFederationAsync(root, matrixPath);
    await VerifyTransportGuardsAsync(root, matrixPath);
    Console.WriteLine(
        "PASS: canonical compatibility matrix fail-closed validation, 3-node/100-account SQLite federation, HMAC subject isolation, node-key auth, 20x replay, restart, partial failure, mismatch, timeout, redirect, oversize, SSRF and concurrency bounds.");
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch (IOException)
    {
        // Cleanup failure must not hide an invariant result.
    }
}

static async Task VerifyCompatibilityMatrixAsync(string matrixPath)
{
    var json = await File.ReadAllTextAsync(matrixPath);
    var snapshot = CompatibilityMatrixValidator.Parse(json, matrixPath);
    Assert(snapshot.Document.SchemaVersion == 1, "Matrix schema version drifted.");
    Assert(snapshot.Document.Combinations.Count == 2, "Matrix observations drifted.");
    var target = snapshot.RequireCombination("pal-1.0.0.100427-native-dev36");
    Assert(target.Status == CompatibilityStatus.Experimental &&
           target.GameVersion == "v1.0.0.100427" &&
           target.Ue4ssCommit == "c2ac246447a8bcd92541070cb474044e7a2bbbe6" &&
           target.NativeModVersion == "0.3.0-dev.36",
        "Experimental target observation is inaccurate.");
    var current = snapshot.RequireCombination(
        "pal-1.0.1.100619-build-24181105-quarantine");
    Assert(current.Status == CompatibilityStatus.Quarantined &&
           current.GameVersion == "v1.0.1.100619" &&
           current.SteamBuild == "24181105" &&
           current.BridgeAvailability == "unavailable",
        "Quarantined current-server observation is inaccurate.");
    ExpectMatrixFailure(
        () => CompatibilityMatrixValidator.RequireProductionStable(snapshot, target.Id),
        "COMPATIBILITY_COMBINATION_NOT_STABLE");
    ExpectMatrixFailure(
        () => CompatibilityMatrixValidator.Parse(
            json,
            expectedCanonicalSha256: new string('0', 64)),
        "COMPATIBILITY_MATRIX_PIN_MISMATCH");

    var tampered = JsonNode.Parse(json)!.AsObject();
    tampered["matrixVersion"] = "1.0.1";
    ExpectMatrixFailure(
        () => CompatibilityMatrixValidator.Parse(tampered.ToJsonString()),
        "COMPATIBILITY_MATRIX_HASH_MISMATCH");

    ExpectInvalidMutation(json, root =>
    {
        root["combinations"]![0]!["gameVersion"] = "latest";
    });
    ExpectInvalidMutation(json, root =>
    {
        root["generatedAt"] = "2099-01-01T00:00:00Z";
    });
    ExpectInvalidMutation(json, root =>
    {
        root["combinations"]![0]!.AsObject().Remove("steamBuild");
    });
    ExpectInvalidMutation(json, root =>
    {
        var combinations = root["combinations"]!.AsArray();
        combinations.Add(combinations[0]!.DeepClone());
    });
    ExpectInvalidMutation(json, root =>
    {
        root["combinations"]![0]!["status"] = "stable";
    }, "COMPATIBILITY_STABLE_REQUIREMENTS_NOT_MET");
    ExpectInvalidMutation(json, root =>
    {
        var combination = root["combinations"]![0]!;
        combination["steamBuild"] = "24123456";
        combination["palDefenderVersion"] = "1.8.1-alpha.1";
        combination["nativeModVersion"] = "0.3.0";
        combination["bridgeAvailability"] = "available";
        combination["capabilities"] = new JsonArray(
            "bridge.handshake",
            "inventory.consume",
            "inventory.probe",
            "player.position.read");
        combination["status"] = "stable";
    }, "COMPATIBILITY_STABLE_REQUIREMENTS_NOT_MET");
    ExpectInvalidMutation(json, root =>
    {
        root["combinations"]![0]!["evidence"]![0]!["artifactSha256"] = "dirty";
    });
    ExpectInvalidMutation(json, root =>
    {
        root["apiKey"] = "must-never-be-accepted";
    }, "COMPATIBILITY_MATRIX_INVALID_JSON");
}

static async Task VerifyFederationAsync(string root, string matrixPath)
{
    var matrix = CompatibilityMatrixValidator.Load(matrixPath);
    var identityKey = Encoding.UTF8.GetBytes("identity-" + new string('i', 48));
    var protector = CreateProtector(identityKey);
    var nodeKey = "node-auth-" + new string('n', 48);
    var aggregateOptions = CreateAggregateOptions(matrixPath, nodeKey);
    var directories = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["alpha"] = Path.Combine(root, "alpha"),
        ["beta"] = Path.Combine(root, "beta"),
        ["gamma"] = Path.Combine(root, "gamma")
    };
    var repositories = new Dictionary<string, SqliteExtractionRepository>(StringComparer.Ordinal);
    var profiles = new Dictionary<string, FederationLocalProfileService>(StringComparer.Ordinal);
    var seasons = new Dictionary<string, ExtractionSeason>(StringComparer.Ordinal);

    try
    {
        foreach (var (serverId, directory) in directories)
        {
            var repository = new SqliteExtractionRepository(directory);
            repositories[serverId] = repository;
            var offset = serverId switch
            {
                "alpha" => 100L,
                "beta" => 1_000L,
                "gamma" => 10_000L,
                _ => throw new InvalidOperationException()
            };
            var now = DateTimeOffset.UtcNow;
            var season = await repository.UpsertSeasonAsync(
                null,
                new ExtractionSeasonDefinition(
                    serverId,
                    $"week-{serverId}",
                    $"Week {serverId}",
                    Guid.NewGuid().ToString("N"),
                    now.AddDays(-1),
                    now.AddDays(6),
                    ExtractionSeasonState.Active),
                null,
                CancellationToken.None);
            seasons[serverId] = season;
            for (var index = 0; index < 100; index++)
            {
                var externalId = UserId(index);
                var account = await repository.GetOrCreateAccountAsync(
                    "steam",
                    externalId,
                    $"{serverId}-player-{index:D3}",
                    CancellationToken.None);
                await CreditAsync(
                    repository,
                    account.AccountId,
                    season.SeasonId,
                    ExtractionCurrency.MarketCoin,
                    offset + index,
                    $"market-{serverId}-{index}");
                await CreditAsync(
                    repository,
                    account.AccountId,
                    season.SeasonId,
                    ExtractionCurrency.SeasonVoucher,
                    offset * 2 + index,
                    $"voucher-{serverId}-{index}");
            }
            profiles[serverId] = CreateLocalProfile(
                repository,
                protector,
                CreateLocalOptions(serverId, matrixPath, nodeKey),
                matrix);
        }

        var transport = new InProcessFederationTransport(profiles);
        var aggregate = CreateAggregation(
            aggregateOptions,
            matrix,
            protector,
            profiles["alpha"],
            transport);
        var overview = await aggregate.GetAccountOverviewAsync(
            "steam",
            UserId(42),
            CancellationToken.None);
        Assert(overview.Servers.Count == 3 &&
               overview.Servers.All(server => server.Availability == "available"),
            "Healthy 3-node federation did not aggregate every node.");
        Assert(overview.Servers.Select(server => server.Season!.Code).SequenceEqual(
                ["week-alpha", "week-beta", "week-gamma"]),
            "Node-local weekly seasons were collapsed or transferred.");
        Assert(overview.Servers.Select(server => server.Balances!.MarketCoin).SequenceEqual(
                [142L, 1_042L, 10_042L]) &&
               overview.Servers.Select(server => server.Balances!.SeasonVoucher).SequenceEqual(
                [242L, 2_042L, 20_042L]),
            "Node-local permanent or weekly balances were altered during aggregation.");
        Assert(overview.Servers.All(server =>
                server.AccountDisplayName!.Contains("player-042", StringComparison.Ordinal)),
            "Federated display names did not come from each local account.");

        var another = await aggregate.GetAccountOverviewAsync(
            "steam",
            UserId(43),
            CancellationToken.None);
        Assert(another.Servers.All(server =>
                server.AccountDisplayName!.Contains("player-043", StringComparison.Ordinal)) &&
               another.Servers.All(server =>
                !server.AccountDisplayName!.Contains("player-042", StringComparison.Ordinal)),
            "Different platform identities crossed account boundaries.");

        var account42 = await repositories["alpha"].FindAccountAsync(
            "steam", UserId(42), CancellationToken.None)
            ?? throw new InvalidOperationException();
        var ledgerBefore = await repositories["alpha"].GetLedgerAsync(
            account42.AccountId,
            seasons["alpha"].SeasonId,
            100,
            CancellationToken.None);
        for (var replay = 0; replay < 20; replay++)
        {
            var repeated = await aggregate.GetAccountOverviewAsync(
                "steam", UserId(42), CancellationToken.None);
            Assert(repeated.Servers[0].Balances!.MarketCoin == 142,
                "Read-only federation replay changed a balance.");
        }
        var ledgerAfter = await repositories["alpha"].GetLedgerAsync(
            account42.AccountId,
            seasons["alpha"].SeasonId,
            100,
            CancellationToken.None);
        Assert(ledgerAfter.Count == ledgerBefore.Count,
            "Read-only federation replay appended authoritative ledger entries.");

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 100).Select(index =>
            aggregate.GetAccountOverviewAsync(
                "steam", UserId(index), CancellationToken.None)));
        for (var index = 0; index < concurrent.Length; index++)
        {
            Assert(concurrent[index].Servers.All(server =>
                    server.AccountDisplayName!.EndsWith(index.ToString("D3"), StringComparison.Ordinal)),
                $"Concurrent account {index} leaked another identity.");
        }

        transport.SetFailure("beta", "FEDERATION_NODE_TIMEOUT");
        transport.SetMatrixMismatch("gamma");
        var partial = await aggregate.GetAccountOverviewAsync(
            "steam", UserId(42), CancellationToken.None);
        Assert(partial.Servers.Single(server => server.ServerId == "alpha").Availability == "available",
            "A failed peer contaminated the healthy local node.");
        var beta = partial.Servers.Single(server => server.ServerId == "beta");
        Assert(beta.Availability == "unavailable" && beta.Balances is null &&
               beta.AccountExists is null && beta.ErrorCode == "FEDERATION_NODE_TIMEOUT",
            "Node timeout was presented as a zero balance.");
        var gamma = partial.Servers.Single(server => server.ServerId == "gamma");
        Assert(gamma.Availability == "incompatible" && gamma.Balances is null &&
               gamma.ErrorCode == "FEDERATION_COMPATIBILITY_MISMATCH",
            "Matrix drift was not isolated to the mismatched node.");

        transport.SetNullCompatibility("gamma");
        var malformed = await aggregate.GetAccountOverviewAsync(
            "steam", UserId(42), CancellationToken.None);
        gamma = malformed.Servers.Single(server => server.ServerId == "gamma");
        Assert(gamma.Availability == "incompatible" && gamma.Balances is null &&
               gamma.ErrorCode == "FEDERATION_RESPONSE_MISMATCH",
            "A null remote compatibility profile escaped fail-closed isolation.");

        foreach (var repository in repositories.Values)
        {
            repository.Dispose();
        }
        repositories.Clear();
        profiles.Clear();

        foreach (var (serverId, directory) in directories)
        {
            var repository = new SqliteExtractionRepository(directory);
            repositories[serverId] = repository;
            profiles[serverId] = CreateLocalProfile(
                repository,
                protector,
                CreateLocalOptions(serverId, matrixPath, nodeKey),
                CompatibilityMatrixValidator.Load(matrixPath));
        }
        var restartedMatrix = CompatibilityMatrixValidator.Load(matrixPath);
        Assert(restartedMatrix.CanonicalSha256 == matrix.CanonicalSha256,
            "Compatibility matrix changed across restart.");
        var restarted = CreateAggregation(
            aggregateOptions,
            restartedMatrix,
            protector,
            profiles["alpha"],
            new InProcessFederationTransport(profiles));
        var afterRestart = await restarted.GetAccountOverviewAsync(
            "steam", UserId(42), CancellationToken.None);
        Assert(afterRestart.Servers.Select(server => server.Balances!.MarketCoin).SequenceEqual(
                [142L, 1_042L, 10_042L]),
            "Authoritative SQLite federation state did not survive restart.");
    }
    finally
    {
        foreach (var repository in repositories.Values)
        {
            repository.Dispose();
        }
    }
}

static async Task VerifyTransportGuardsAsync(string root, string matrixPath)
{
    var matrix = CompatibilityMatrixValidator.Load(matrixPath);
    var nodeKey = "transport-node-" + new string('k', 48);
    var identityKey = "transport-identity-" + new string('h', 48);
    var options = CreateAggregateOptions(matrixPath, nodeKey) with
    {
        IdentityHmacKey = identityKey,
        RequestTimeoutMilliseconds = 100,
        MaximumResponseBytes = 1_024,
        MaximumConcurrentRequests = 3,
        InternalRequestsPerMinute = 10
    };
    var environment = new TestEnvironment(root);
    _ = FederationOptionsValidator.ValidateCore(options, root, isDevelopment: true);
    var protector = new FederationIdentityProtector(
        Options.Create(options),
        environment);
    var token = protector.Protect("steam", UserId(1));
    Assert(token.StartsWith("fed1_", StringComparison.Ordinal) && token.Length == 48,
        "Federation subject token format drifted.");
    Assert(token != protector.Protect("steam", UserId(2)),
        "Different identities produced the same federation subject token.");

    var authenticator = new FederationInternalAuthenticator(
        Options.Create(options),
        environment);
    var request = new DefaultHttpContext().Request;
    request.Headers[FederationInternalAuthenticator.NodeKeyHeader] = "wrong-" + new string('x', 40);
    Assert(!authenticator.Authenticate(request), "Wrong node key authenticated.");
    request.Headers[FederationInternalAuthenticator.NodeKeyHeader] = options.InboundNodeKey;
    Assert(authenticator.Authenticate(request), "Correct node key was rejected.");

    var overrideContext = new DefaultHttpContext();
    overrideContext.Request.QueryString = new QueryString("?userId=steam_forged");
    ExpectFederationFailure(
        () => FederationEndpointSecurity.RejectIdentityOverrides(overrideContext.Request),
        "FEDERATION_IDENTITY_OVERRIDE_FORBIDDEN");
    overrideContext = new DefaultHttpContext();
    overrideContext.Request.Headers["X-Federation-Subject"] = token;
    ExpectFederationFailure(
        () => FederationEndpointSecurity.RejectIdentityOverrides(overrideContext.Request),
        "FEDERATION_IDENTITY_OVERRIDE_FORBIDDEN");

    var requestGuard = new FederationInternalRequestGuard(
        Options.Create(options),
        TimeProvider.System);
    for (var index = 0; index < 10; index++)
    {
        Assert(requestGuard.TryAcquire("127.0.0.1", out var lease, out _),
            "Internal read request was limited too early.");
        lease!.Dispose();
    }
    Assert(!requestGuard.TryAcquire("127.0.0.1", out _, out var retryAfter) &&
           retryAfter > 0,
        "Internal read endpoint rate limit did not fail closed.");

    ExpectOptionsFailure(CreateAggregateOptions(matrixPath, nodeKey) with
    {
        Nodes =
        [
            CreateAggregateOptions(matrixPath, nodeKey).Nodes[0],
            CreateAggregateOptions(matrixPath, nodeKey).Nodes[1] with
            {
                BaseUri = "http://169.254.169.254/"
            }
        ]
    }, root, isDevelopment: true);
    ExpectOptionsFailure(CreateAggregateOptions(matrixPath, nodeKey), root, isDevelopment: false);
    ExpectOptionsFailure(CreateAggregateOptions(matrixPath, nodeKey) with
    {
        Nodes =
        [
            CreateAggregateOptions(matrixPath, nodeKey).Nodes[0],
            CreateAggregateOptions(matrixPath, nodeKey).Nodes[1] with
            {
                BaseUri = "http://user:pass@127.0.0.1:6202/?target=http://example.test"
            }
        ]
    }, root, isDevelopment: true);

    var profile = SampleProfile(matrix, "beta");
    await ExpectTransportFailureAsync(
        options,
        environment,
        new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("http://127.0.0.1:9999/") }
        }),
        token,
        "FEDERATION_REDIRECT_REJECTED");
    await ExpectTransportFailureAsync(
        options,
        environment,
        new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[1_025])
        }),
        token,
        "FEDERATION_RESPONSE_OVERSIZED");
    await ExpectTransportFailureAsync(
        options,
        environment,
        new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(5_000, cancellationToken);
            return JsonResponse(profile);
        }),
        token,
        "FEDERATION_NODE_TIMEOUT");

    var peak = 0;
    var active = 0;
    var concurrencyHandler = new DelegateHandler(async (requestMessage, cancellationToken) =>
    {
        var nowActive = Interlocked.Increment(ref active);
        UpdatePeak(ref peak, nowActive);
        try
        {
            Assert(requestMessage.RequestUri?.Host == "127.0.0.1" &&
                   requestMessage.RequestUri.AbsolutePath == "/api/v1/internal/federation/profile",
                "Transport accepted a request-controlled destination.");
            Assert(requestMessage.Headers.GetValues(
                    FederationInternalAuthenticator.NodeKeyHeader).Single() == nodeKey,
                "Transport omitted or altered the node key.");
            await Task.Delay(20, cancellationToken);
            return JsonResponse(profile);
        }
        finally
        {
            Interlocked.Decrement(ref active);
        }
    });
    var client = CreateNodeClient(options, environment, concurrencyHandler);
    await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
        client.GetProfileAsync(options.Nodes[1], token, CancellationToken.None)));
    Assert(peak <= options.MaximumConcurrentRequests,
        "Federation outbound concurrency exceeded its configured bound.");
}

static FederationOptions CreateAggregateOptions(string matrixPath, string nodeKey) => new()
{
    Enabled = true,
    LocalServerId = "alpha",
    MatrixPath = matrixPath,
    AllowExperimentalInDevelopment = true,
    IdentityHmacKey = "identity-config-" + new string('i', 48),
    InboundNodeKey = "inbound-config-" + new string('a', 48),
    RequestTimeoutMilliseconds = 500,
    MaximumResponseBytes = 32 * 1024,
    MaximumRequestBodyBytes = 2 * 1024,
    MaximumConcurrentRequests = 8,
    InternalRequestsPerMinute = 600,
    Nodes =
    [
        new FederationNodeOptions
        {
            ServerId = "alpha",
            DisplayName = "Alpha",
            Local = true,
            BaseUri = "http://127.0.0.1:6201/",
            PortalUrl = "http://127.0.0.1:7201/player/",
            ExpectedCombinationId = "pal-1.0.0.100427-native-dev36"
        },
        new FederationNodeOptions
        {
            ServerId = "beta",
            DisplayName = "Beta",
            BaseUri = "http://127.0.0.1:6202/",
            PortalUrl = "http://127.0.0.1:7202/player/",
            ExpectedCombinationId = "pal-1.0.0.100427-native-dev36",
            NodeKey = nodeKey
        },
        new FederationNodeOptions
        {
            ServerId = "gamma",
            DisplayName = "Gamma",
            BaseUri = "http://127.0.0.1:6203/",
            PortalUrl = "http://127.0.0.1:7203/player/",
            ExpectedCombinationId = "pal-1.0.0.100427-native-dev36",
            NodeKey = nodeKey
        }
    ]
};

static FederationOptions CreateLocalOptions(
    string serverId,
    string matrixPath,
    string nodeKey) => new()
{
    Enabled = true,
    LocalServerId = serverId,
    MatrixPath = matrixPath,
    AllowExperimentalInDevelopment = true,
    IdentityHmacKey = "identity-config-" + new string('i', 48),
    InboundNodeKey = nodeKey,
    Nodes =
    [
        new FederationNodeOptions
        {
            ServerId = serverId,
            DisplayName = serverId,
            Local = true,
            BaseUri = $"http://127.0.0.1:{6300 + serverId.Length}/",
            PortalUrl = $"http://127.0.0.1:{7300 + serverId.Length}/player/",
            ExpectedCombinationId = "pal-1.0.0.100427-native-dev36"
        }
    ]
};

static FederationIdentityProtector CreateProtector(byte[] key)
{
    var root = Path.GetTempPath();
    var options = new FederationOptions
    {
        Enabled = true,
        IdentityHmacKey = Encoding.UTF8.GetString(key)
    };
    return new FederationIdentityProtector(
        Options.Create(options),
        new TestEnvironment(root));
}

static FederationLocalProfileService CreateLocalProfile(
    SqliteExtractionRepository repository,
    FederationIdentityProtector protector,
    FederationOptions options,
    CompatibilityMatrixSnapshot matrix) => new(
        new ExtractionCommerceService(repository),
        protector,
        Options.Create(options),
        new CompatibilityMatrixStore(matrix),
        TimeProvider.System);

static FederationAggregationService CreateAggregation(
    FederationOptions options,
    CompatibilityMatrixSnapshot matrix,
    FederationIdentityProtector protector,
    FederationLocalProfileService local,
    IFederationNodeTransport transport) => new(
        Options.Create(options),
        new CompatibilityMatrixStore(matrix),
        protector,
        local,
        transport,
        TimeProvider.System);

static FederationNodeClient CreateNodeClient(
    FederationOptions options,
    IHostEnvironment environment,
    HttpMessageHandler handler) => new(
        new TestHttpClientFactory(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        }),
        Options.Create(options),
        environment);

static async Task ExpectTransportFailureAsync(
    FederationOptions options,
    IHostEnvironment environment,
    HttpMessageHandler handler,
    string token,
    string code)
{
    var client = CreateNodeClient(options, environment, handler);
    try
    {
        _ = await client.GetProfileAsync(
            options.Nodes[1], token, CancellationToken.None);
        throw new InvalidOperationException($"Expected transport failure {code}.");
    }
    catch (FederationException exception)
    {
        Assert(exception.Code == code,
            $"Expected transport error {code}, received {exception.Code}.");
    }
}

static FederationLocalProfile SampleProfile(
    CompatibilityMatrixSnapshot matrix,
    string serverId)
{
    var combination = matrix.RequireCombination("pal-1.0.0.100427-native-dev36");
    return new FederationLocalProfile(
        serverId,
        true,
        "sample",
        new FederationSeasonProfile(
            "week-sample", "Week sample", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7), "active"),
        new FederationBalanceProfile(1, 2),
        true,
        null,
        FederationLocalProfileService.ProjectCompatibility(matrix, combination),
        DateTimeOffset.UtcNow);
}

static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
{
    Content = new StringContent(
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        Encoding.UTF8,
        "application/json")
};

static async Task CreditAsync(
    SqliteExtractionRepository repository,
    Guid accountId,
    Guid seasonId,
    ExtractionCurrency currency,
    long amount,
    string key)
{
    var result = await repository.AdjustWalletAsync(
        new WalletAdjustmentRequest(
            accountId,
            currency == ExtractionCurrency.MarketCoin ? null : seasonId,
            currency,
            amount,
            "federation harness seed",
            "federation-harness",
            key,
            "federation-harness",
            key),
        CancellationToken.None);
    Assert(result.Created, $"Could not seed wallet {key}.");
}

static string UserId(int index) => $"steam_{76561198000000000L + index}";

static void ExpectInvalidMutation(
    string source,
    Action<JsonObject> mutation,
    string expectedCode = "COMPATIBILITY_MATRIX_INVALID")
{
    var root = JsonNode.Parse(source)!.AsObject();
    mutation(root);
    root["canonicalSha256"] = CompatibilityMatrixValidator.ComputeCanonicalSha256(
        root.ToJsonString());
    ExpectMatrixFailure(
        () => CompatibilityMatrixValidator.Parse(root.ToJsonString()),
        expectedCode);
}

static void ExpectMatrixFailure(Action action, string expectedCode)
{
    try
    {
        action();
        throw new InvalidOperationException(
            $"Expected compatibility failure {expectedCode}.");
    }
    catch (CompatibilityMatrixException exception)
    {
        Assert(exception.Code == expectedCode,
            $"Expected compatibility error {expectedCode}, received {exception.Code}.");
    }
}

static void ExpectFederationFailure(Action action, string expectedCode)
{
    try
    {
        action();
        throw new InvalidOperationException($"Expected federation failure {expectedCode}.");
    }
    catch (FederationException exception)
    {
        Assert(exception.Code == expectedCode,
            $"Expected federation error {expectedCode}, received {exception.Code}.");
    }
}

static void ExpectOptionsFailure(
    FederationOptions options,
    string contentRoot,
    bool isDevelopment)
{
    try
    {
        _ = FederationOptionsValidator.ValidateCore(options, contentRoot, isDevelopment);
        throw new InvalidOperationException("Unsafe federation options passed validation.");
    }
    catch (Exception exception) when (
        exception is ArgumentException or CompatibilityMatrixException)
    {
        // Expected fail-closed validation.
    }
}

static void UpdatePeak(ref int peak, int candidate)
{
    while (true)
    {
        var current = Volatile.Read(ref peak);
        if (candidate <= current || Interlocked.CompareExchange(ref peak, candidate, current) == current)
        {
            return;
        }
    }
}

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "README.md")) &&
            Directory.Exists(Path.Combine(current.FullName, "services", "control-api")))
        {
            return current.FullName;
        }
        current = current.Parent;
    }
    throw new DirectoryNotFoundException("Repository root was not found.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class InProcessFederationTransport(
    IReadOnlyDictionary<string, FederationLocalProfileService> profiles)
    : IFederationNodeTransport
{
    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _matrixMismatches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _nullCompatibilities = new(StringComparer.Ordinal);

    public void SetFailure(string serverId, string code) => _failures[serverId] = code;

    public void SetMatrixMismatch(string serverId) => _matrixMismatches[serverId] = 0;

    public void SetNullCompatibility(string serverId)
    {
        _matrixMismatches.TryRemove(serverId, out _);
        _nullCompatibilities[serverId] = 0;
    }

    public async Task<FederationLocalProfile> GetProfileAsync(
        FederationNodeOptions node,
        string subjectToken,
        CancellationToken cancellationToken)
    {
        if (_failures.TryGetValue(node.ServerId, out var failure))
        {
            throw new FederationException(
                failure,
                "Synthetic node failure.",
                StatusCodes.Status503ServiceUnavailable);
        }
        var profile = await profiles[node.ServerId].GetAsync(
            subjectToken,
            cancellationToken);
        if (_nullCompatibilities.ContainsKey(node.ServerId))
        {
            return profile with { Compatibility = null! };
        }
        return _matrixMismatches.ContainsKey(node.ServerId)
            ? profile with
            {
                Compatibility = profile.Compatibility with
                {
                    MatrixSha256 = new string('0', 64)
                }
            }
            : profile;
    }

    public Task<FederationNodeHealth> GetHealthAsync(
        FederationNodeOptions node,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_failures.TryGetValue(node.ServerId, out var failure))
        {
            throw new FederationException(
                failure,
                "Synthetic node failure.",
                StatusCodes.Status503ServiceUnavailable);
        }
        return Task.FromResult(profiles[node.ServerId].GetHealth());
    }
}

sealed class DelegateHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : this((request, _) => Task.FromResult(handler(request)))
    {
    }

    public DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => _handler(request, cancellationToken);
}

sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

sealed class TestEnvironment(string contentRoot) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "PalControl.Federation.Harness";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = contentRoot;
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
