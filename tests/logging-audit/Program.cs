using System.Reflection;
using System.Reflection.Emit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PalControl.ControlApi.Infrastructure;

VerifyStableCorrelationAndNestedAdapterScope();
VerifyExceptionRedaction();
await VerifyHttpBoundaryAsync();
VerifyServiceStructure();

Console.WriteLine(
    "PASS: deterministic worker correlation, nested adapter propagation, HTTP correlation, exception redaction, hosted-worker inventory, adapter inventory, and IL logging audit.");
return 0;

static void VerifyStableCorrelationAndNestedAdapterScope()
{
    var logger = new CapturingLogger<ProgramMarker>();
    var runId = Guid.Parse("b71e825c-58bf-4b5e-a13c-f96afb9f3158");
    string firstCorrelation;
    using (ControlPlaneLog.BeginWorker(
               logger,
               "SettlementWorker",
               "settlement.execute",
               runId,
               "local"))
    {
        firstCorrelation = Require(ControlPlaneLog.CurrentCorrelationId, "Worker correlation was absent.");
        using (ControlPlaneLog.BeginAdapter(
                   logger,
                   "NativeBridgeClient",
                   "inventory.consume",
                   Guid.Parse("9cb973f4-e12a-4479-9c42-55b346a05483"),
                   "local"))
        {
            Assert(
                ControlPlaneLog.CurrentCorrelationId == firstCorrelation,
                "A nested adapter replaced the worker correlation ID.");
            logger.LogInformation("Nested adapter probe.");
        }
    }
    Assert(ControlPlaneLog.CurrentCorrelationId is null, "A disposed scope leaked ambient correlation state.");

    string replayCorrelation;
    using (ControlPlaneLog.BeginWorker(
               logger,
               "SettlementWorker",
               "settlement.execute",
               runId,
               "local"))
    {
        replayCorrelation = Require(ControlPlaneLog.CurrentCorrelationId, "Replay correlation was absent.");
    }
    Assert(
        replayCorrelation == firstCorrelation,
        "The same durable worker operation did not reproduce its correlation ID.");

    using (ControlPlaneLog.BeginWorker(
               logger,
               "SettlementWorker",
               "settlement.execute",
               Guid.Parse("4da56aad-d53a-45ce-8cba-0bcb613ef86c"),
               "local"))
    {
        Assert(
            ControlPlaneLog.CurrentCorrelationId != firstCorrelation,
            "Distinct durable operations shared a correlation ID.");
    }

    var entry = logger.Entries.Single();
    Assert(entry.ScopeValue("ScopeKind") == "adapter", "The nested adapter scope was not recorded.");
    Assert(entry.ScopeValue("Operation") == "inventory.consume", "The adapter operation label was absent.");
    Assert(entry.ScopeValue("CorrelationId") == firstCorrelation, "The logged adapter scope lost correlation.");
}

static void VerifyExceptionRedaction()
{
    const string rawPlayerUid = "76561198000000000";
    var secrets = new[]
    {
        "pal-session-cookie-secret",
        "12345678",
        "bearer-token-secret",
        "rest-password-secret",
        rawPlayerUid
    };
    var exception = new InvalidOperationException(
        "Cookie=pal-session-cookie-secret; code=12345678; " +
        "token=Bearer bearer-token-secret; password=rest-password-secret; " +
        $"PlayerUID={rawPlayerUid}");
    var logger = new CapturingLogger<ProgramMarker>();
    var playerFingerprint = ControlPlaneLog.Fingerprint(rawPlayerUid);

    using (ControlPlaneLog.BeginWorker(
               logger,
               "RedactionWorker",
               "redaction.probe",
               Guid.Parse("25720161-8e79-4aae-a116-23aee05b90d5"),
               subjectFingerprint: playerFingerprint))
    {
        logger.LogSafeError(exception, "The redaction probe failed.");
    }

    var entry = logger.Entries.Single();
    Assert(entry.Exception is null, "The original exception object reached the logging provider.");
    var serialized = entry.AllCapturedText();
    foreach (var secret in secrets)
    {
        Assert(
            !serialized.Contains(secret, StringComparison.OrdinalIgnoreCase),
            $"Sensitive exception data reached the log capture: {secret}");
    }
    Assert(
        serialized.Contains("InvalidOperationException", StringComparison.Ordinal),
        "Safe exception metadata did not include the exception type.");
    Assert(
        serialized.Contains("ErrorFingerprint", StringComparison.Ordinal),
        "Safe exception metadata did not include an error fingerprint.");
    Assert(
        serialized.Contains(playerFingerprint, StringComparison.Ordinal),
        "The one-way player fingerprint was not retained for investigation.");
}

static async Task VerifyHttpBoundaryAsync()
{
    var logger = new CapturingLogger<ControlPlaneCorrelationMiddleware>();
    var expected = Guid.Parse("49a50bbb-9469-4f92-9252-21eecfe6834a").ToString("D");
    var middleware = new ControlPlaneCorrelationMiddleware(
        context =>
        {
            logger.LogInformation("HTTP boundary probe.");
            Assert(
                ControlPlaneCorrelationMiddleware.GetCorrelationId(context) == expected,
                "The request correlation was not available to downstream middleware.");
            return Task.CompletedTask;
        },
        logger);
    var context = new DefaultHttpContext();
    context.Request.Method = HttpMethods.Post;
    context.Request.Headers[ControlPlaneLog.CorrelationHeaderName] = expected;

    await middleware.InvokeAsync(context);

    Assert(
        context.Response.Headers[ControlPlaneLog.CorrelationHeaderName] == expected,
        "The validated correlation ID was not returned to the caller.");
    var entry = logger.Entries.Single();
    Assert(entry.ScopeValue("CorrelationId") == expected, "The HTTP log scope has the wrong correlation ID.");
    Assert(entry.ScopeValue("HttpMethod") == HttpMethods.Post, "The HTTP method was not scoped.");

    var invalidLogger = new CapturingLogger<ControlPlaneCorrelationMiddleware>();
    var invalidMiddleware = new ControlPlaneCorrelationMiddleware(
        _ =>
        {
            invalidLogger.LogInformation("Invalid header probe.");
            return Task.CompletedTask;
        },
        invalidLogger);
    var invalidContext = new DefaultHttpContext();
    invalidContext.Request.Headers[ControlPlaneLog.CorrelationHeaderName] =
        "code=12345678;Cookie=forged";
    await invalidMiddleware.InvokeAsync(invalidContext);
    var generated = invalidContext.Response.Headers[ControlPlaneLog.CorrelationHeaderName].ToString();
    Assert(Guid.TryParseExact(generated, "D", out _), "An invalid correlation header was not replaced.");
    Assert(
        !invalidLogger.Entries.Single().AllCapturedText().Contains("12345678", StringComparison.Ordinal),
        "An invalid caller-controlled correlation value reached the log scope.");
}

static void VerifyServiceStructure()
{
    var assembly = typeof(ControlPlaneLog).Assembly;
    var expectedWorkers = new HashSet<string>(StringComparer.Ordinal)
    {
        "PalControl.ControlApi.Content.EconomyContentStartupInitializer",
        "PalControl.ControlApi.Content.ReliableTaskProjectionRecoveryWorker",
        "PalControl.ControlApi.Infrastructure.AnnouncementCommandQueue",
        "PalControl.ControlApi.Infrastructure.EconomyObservabilityService",
        "PalControl.ControlApi.Infrastructure.ExtractionDeliveryWorker",
        "PalControl.ControlApi.Infrastructure.ExtractionSettlementQueue",
        "PalControl.ControlApi.Infrastructure.ExtractionSettlementRecoveryWorker",
        "PalControl.ControlApi.Infrastructure.InGameNotificationCommandQueue",
        "PalControl.ControlApi.Infrastructure.LiveMapService",
        "PalControl.ControlApi.Infrastructure.NativeBridgeClient",
        "PalControl.ControlApi.Infrastructure.PalDefenderCommandQueue",
        "PalControl.ControlApi.Infrastructure.PlayerNotificationProjectionWorker",
        "PalControl.ControlApi.Infrastructure.SaveCommandQueue",
        "PalControl.ControlApi.Infrastructure.TeamEconomyProjectionWorker"
    };
    var actualWorkers = assembly.GetTypes()
        .Where(type => !type.IsAbstract && typeof(IHostedService).IsAssignableFrom(type))
        .Select(type => Require(type.FullName, "A hosted type has no full name."))
        .ToHashSet(StringComparer.Ordinal);
    AssertSetEqual(expectedWorkers, actualWorkers, "hosted worker inventory");

    foreach (var workerName in expectedWorkers)
    {
        var worker = Require(assembly.GetType(workerName), $"Worker type not found: {workerName}");
        Assert(
            CallsControlPlaneMethod(worker, nameof(ControlPlaneLog.BeginWorker)),
            $"Hosted worker has no ControlPlaneLog.BeginWorker boundary: {workerName}");
    }

    var loggedAdapters = new[]
    {
        "PalControl.ControlApi.Infrastructure.NativeBridgeClient",
        "PalControl.ControlApi.Infrastructure.PalDefenderRestClient",
        "PalControl.ControlApi.Infrastructure.PalworldRestClient"
    };
    foreach (var adapterName in loggedAdapters)
    {
        var adapter = Require(assembly.GetType(adapterName), $"Adapter type not found: {adapterName}");
        Assert(
            CallsControlPlaneMethod(adapter, nameof(ControlPlaneLog.BeginAdapter)),
            $"Logged adapter has no ControlPlaneLog.BeginAdapter boundary: {adapterName}");
    }

    var loggerAudit = new Dictionary<string, AuditEntry>(StringComparer.Ordinal)
    {
        ["PalControl.ControlApi.Content.EconomyContentRuntimeService"] = new("service", "HTTP/startup parent scope; version/date only"),
        ["PalControl.ControlApi.Content.EconomyContentStartupInitializer"] = new("worker", "host-startup durable scope; no secret fields"),
        ["PalControl.ControlApi.Content.ReliableTaskProjectionRecoveryWorker"] = new("worker", "run/order GUID; no player identity"),
        ["PalControl.ControlApi.Infrastructure.AdminAuditMiddleware"] = new("middleware", "subject fingerprint; audited identity remains only in audit storage"),
        ["PalControl.ControlApi.Infrastructure.AnnouncementCommandQueue"] = new("worker", "command GUID and server fingerprint; content omitted"),
        ["PalControl.ControlApi.Infrastructure.AnnouncementStore"] = new("store", "parent or startup persistence scope; state/GUID only"),
        ["PalControl.ControlApi.Infrastructure.ControlPlaneCorrelationMiddleware"] = new("middleware", "validated GUID and allow-listed method; path omitted"),
        ["PalControl.ControlApi.Infrastructure.EconomyObservabilityService"] = new("worker", "observation time and server fingerprint; alert codes allow-listed"),
        ["PalControl.ControlApi.Infrastructure.ExtractionDeliveryWorker"] = new("worker", "delivery/order GUID and subject fingerprint; errors fingerprinted"),
        ["PalControl.ControlApi.Infrastructure.ExtractionModeCoordinator"] = new("service", "parent HTTP/worker scope; account already fingerprinted"),
        ["PalControl.ControlApi.Infrastructure.ExtractionSettlementQueue"] = new("worker", "run GUID and subject fingerprint"),
        ["PalControl.ControlApi.Infrastructure.ExtractionSettlementRecoveryWorker"] = new("worker", "run GUID; no player identity"),
        ["PalControl.ControlApi.Infrastructure.ExtractionSettlementService"] = new("service", "parent queue/recovery/HTTP scope; run and lease GUID only"),
        ["PalControl.ControlApi.Infrastructure.InGameNotificationCommandQueue"] = new("worker", "command GUID and server fingerprint; audience/content omitted"),
        ["PalControl.ControlApi.Infrastructure.InGameNotificationStore"] = new("store", "parent or startup persistence scope; state/GUID only"),
        ["PalControl.ControlApi.Infrastructure.LiveMapService"] = new("worker", "sample timestamp and server fingerprint; player data omitted"),
        ["PalControl.ControlApi.Infrastructure.NativeBridgeClient"] = new("worker-adapter", "command GUID/server fingerprint; payload and reason omitted"),
        ["PalControl.ControlApi.Infrastructure.PalDefenderCommandQueue"] = new("worker", "command GUID/server/subject fingerprints; body/path omitted"),
        ["PalControl.ControlApi.Infrastructure.PalDefenderRestClient"] = new("adapter", "allow-listed read/write label; token/path/body omitted"),
        ["PalControl.ControlApi.Infrastructure.PlayerNotificationProjectionWorker"] = new("worker", "source class/state only; player target, content and source ids omitted"),
        ["PalControl.ControlApi.Infrastructure.PalworldResourceCatalogService"] = new("service", "parent scope; template name fingerprinted"),
        ["PalControl.ControlApi.Infrastructure.PalworldRestClient"] = new("adapter", "allow-listed endpoint label; credentials/body/player data omitted"),
        ["PalControl.ControlApi.Infrastructure.SaveCommandQueue"] = new("worker", "command GUID/server fingerprint; labels and paths omitted"),
        ["PalControl.ControlApi.Infrastructure.SaveManagementService"] = new("service", "parent HTTP/worker scope; exceptions redacted"),
        ["PalControl.ControlApi.Infrastructure.TeamEconomyProjectionWorker"] = new("worker", "season GUID and server fingerprint; account, team and contribution data omitted; storage errors fingerprinted")
    };
    var actualLoggerTypes = assembly.GetTypes()
        .Where(HasLoggerDependency)
        .Select(type => Require(type.FullName, "A logger-bearing type has no full name."))
        .ToHashSet(StringComparer.Ordinal);
    AssertSetEqual(loggerAudit.Keys.ToHashSet(StringComparer.Ordinal), actualLoggerTypes, "logger-bearing type audit");
    Assert(
        loggerAudit.Values.All(entry =>
            !string.IsNullOrWhiteSpace(entry.Category) &&
            !string.IsNullOrWhiteSpace(entry.SensitiveFieldDecision)),
        "A logger audit entry is missing its category or sensitive-field decision.");

    var noDirectLoggerAdapters = new[]
    {
        "PalControl.ControlApi.Extraction.ExtractionRconAdapter",
        "PalControl.ControlApi.Extraction.SourceRconTransport",
        "PalControl.ControlApi.Infrastructure.ExtractionNativeInventoryAdapter",
        "PalControl.ControlApi.Infrastructure.PalDefenderItemGrantAdapter",
        "PalControl.ControlApi.Infrastructure.SteamOpenIdProviderClient"
    };
    foreach (var adapterName in noDirectLoggerAdapters)
    {
        var adapter = Require(assembly.GetType(adapterName), $"Audited adapter type not found: {adapterName}");
        Assert(!HasLoggerDependency(adapter), $"A no-log adapter gained an unaudited logger: {adapterName}");
    }

    var forbiddenCalls = assembly.GetTypes()
        .SelectMany(AllDeclaredMethods)
        .SelectMany(method => CalledMethods(method).Select(called => (Caller: method, Called: called)))
        .Where(call =>
            call.Called.DeclaringType?.FullName == "Microsoft.Extensions.Logging.LoggerExtensions" &&
            call.Called.Name.StartsWith("Log", StringComparison.Ordinal) &&
            call.Called.GetParameters().Any(parameter => parameter.ParameterType == typeof(Exception)))
        .Select(call => $"{call.Caller.DeclaringType?.FullName}.{call.Caller.Name} -> {call.Called}")
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();
    Assert(
        forbiddenCalls.Length == 0,
        "Raw Exception logger overloads remain:\n" + string.Join("\n", forbiddenCalls));
}

static bool HasLoggerDependency(Type type) =>
    type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Any(field => IsLoggerType(field.FieldType)) ||
    type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .SelectMany(constructor => constructor.GetParameters())
        .Any(parameter => IsLoggerType(parameter.ParameterType));

static bool IsLoggerType(Type type) =>
    type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ILogger<>);

static bool CallsControlPlaneMethod(Type root, string methodName) =>
    SelfAndNested(root)
        .SelectMany(AllDeclaredMethods)
        .SelectMany(CalledMethods)
        .Any(method =>
            method.DeclaringType == typeof(ControlPlaneLog) &&
            method.Name == methodName);

static IEnumerable<Type> SelfAndNested(Type type)
{
    yield return type;
    foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
    {
        foreach (var descendant in SelfAndNested(nested))
        {
            yield return descendant;
        }
    }
}

static IEnumerable<MethodBase> AllDeclaredMethods(Type type) =>
    type.GetMethods(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Cast<MethodBase>()
        .Concat(type.GetConstructors(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.DeclaredOnly));

static IEnumerable<MethodBase> CalledMethods(MethodBase caller)
{
    var body = caller.GetMethodBody();
    var bytes = body?.GetILAsByteArray();
    if (bytes is null)
    {
        yield break;
    }

    var typeArguments = caller.DeclaringType?.IsGenericType == true
        ? caller.DeclaringType.GetGenericArguments()
        : null;
    var methodArguments = caller.IsGenericMethod
        ? caller.GetGenericArguments()
        : null;
    for (var offset = 0; offset < bytes.Length;)
    {
        var opcode = ReadOpcode(bytes, ref offset);
        if (opcode.OperandType == OperandType.InlineSwitch)
        {
            var count = BitConverter.ToInt32(bytes, offset);
            offset += sizeof(int) + (count * sizeof(int));
            continue;
        }

        var operandSize = OperandSize(opcode.OperandType);
        if (opcode.OperandType == OperandType.InlineMethod)
        {
            var token = BitConverter.ToInt32(bytes, offset);
            MethodBase? resolved = null;
            try
            {
                resolved = caller.Module.ResolveMethod(token, typeArguments, methodArguments);
            }
            catch (ArgumentException)
            {
                // Invalid tokens cannot be emitted by a successful build, but a
                // generic context can occasionally prevent reflection resolution.
            }
            if (resolved is not null)
            {
                yield return resolved;
            }
        }
        offset += operandSize;
    }
}

static OpCode ReadOpcode(byte[] bytes, ref int offset)
{
    var first = bytes[offset++];
    if (first != 0xfe)
    {
        return OpcodeTables.OneByte[first];
    }
    return OpcodeTables.TwoByte[bytes[offset++]];
}

static int OperandSize(OperandType operandType) => operandType switch
{
    OperandType.InlineNone => 0,
    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
    OperandType.InlineVar => 2,
    OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or
        OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
        OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
    OperandType.InlineI8 or OperandType.InlineR => 8,
    _ => throw new InvalidOperationException($"Unsupported IL operand type: {operandType}")
};

static void AssertSetEqual(
    IReadOnlySet<string> expected,
    IReadOnlySet<string> actual,
    string label)
{
    var missing = expected.Except(actual, StringComparer.Ordinal).Order().ToArray();
    var unexpected = actual.Except(expected, StringComparer.Ordinal).Order().ToArray();
    Assert(
        missing.Length == 0 && unexpected.Length == 0,
        $"Unexpected {label}. Missing=[{string.Join(", ", missing)}], " +
        $"unexpected=[{string.Join(", ", unexpected)}].");
}

static T Require<T>(T? value, string message) where T : class =>
    value ?? throw new InvalidOperationException(message);

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed record AuditEntry(string Category, string SensitiveFieldDecision);

file sealed class ProgramMarker;

file static class OpcodeTables
{
    public static readonly OpCode[] OneByte = Build(twoByte: false);
    public static readonly OpCode[] TwoByte = Build(twoByte: true);

    private static OpCode[] Build(bool twoByte)
    {
        var values = new OpCode[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opcode)
            {
                continue;
            }
            var value = unchecked((ushort)opcode.Value);
            if ((!twoByte && value <= byte.MaxValue) ||
                (twoByte && (value & 0xff00) == 0xfe00))
            {
                values[value & 0xff] = opcode;
            }
        }
        return values;
    }
}

file sealed record CapturedLog(
    LogLevel Level,
    string Message,
    Exception? Exception,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Scopes)
{
    public string? ScopeValue(string key) => Scopes
        .Reverse()
        .SelectMany(scope => scope)
        .FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.Ordinal))
        .Value?.ToString();

    public string AllCapturedText() => string.Join(
        "\n",
        new[] { Message }
            .Concat(Scopes.SelectMany(scope => scope.Select(entry => $"{entry.Key}={entry.Value}"))));
}

file sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<object> _scopes = [];

    public List<CapturedLog> Entries { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        _scopes.Add(state);
        return new ScopeLease(_scopes, state);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new CapturedLog(
            logLevel,
            formatter(state, exception),
            exception,
            _scopes.Select(ToDictionary).ToArray()));
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(object state)
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            return pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        return new Dictionary<string, object?>
        {
            ["Scope"] = state.ToString()
        };
    }

    private sealed class ScopeLease(List<object> scopes, object state) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                var index = scopes.LastIndexOf(state);
                if (index >= 0)
                {
                    scopes.RemoveAt(index);
                }
            }
        }
    }
}
