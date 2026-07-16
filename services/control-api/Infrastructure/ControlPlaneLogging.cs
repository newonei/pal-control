using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PalControl.ControlApi.Infrastructure;

/// <summary>
/// Creates the structured correlation scopes used at every control-plane boundary.
/// Scope values deliberately contain only allow-listed labels, durable opaque IDs,
/// and one-way fingerprints. User identifiers and credentials must never be passed
/// as scope values.
/// </summary>
public static class ControlPlaneLog
{
    public const string CorrelationHeaderName = "X-Correlation-ID";
    public static readonly object HttpCorrelationItemKey = new();

    private static readonly AsyncLocal<CorrelationContext?> Ambient = new();

    public static string? CurrentCorrelationId => Ambient.Value?.CorrelationId;

    public static IDisposable BeginHttpRequest(
        ILogger logger,
        string correlationId,
        string method)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        return Begin(
            logger,
            scopeKind: "http",
            component: "ControlApi",
            operation: "http.request",
            stableOperationId: correlationId,
            explicitCorrelationId: correlationId,
            additionalFields: new Dictionary<string, object?>
            {
                ["HttpMethod"] = NormalizeLabel(method)
            });
    }

    public static IDisposable BeginWorker(
        ILogger logger,
        string component,
        string operation,
        object? stableOperationId = null,
        string? serverId = null,
        string? subjectFingerprint = null) => BeginBoundary(
            logger,
            "worker",
            component,
            operation,
            stableOperationId,
            serverId,
            subjectFingerprint);

    public static IDisposable BeginAdapter(
        ILogger logger,
        string component,
        string operation,
        object? stableOperationId = null,
        string? serverId = null,
        string? subjectFingerprint = null) => BeginBoundary(
            logger,
            "adapter",
            component,
            operation,
            stableOperationId,
            serverId,
            subjectFingerprint);

    public static IDisposable BeginOperation(
        ILogger logger,
        string component,
        string operation,
        object? stableOperationId = null,
        string? serverId = null,
        string? subjectFingerprint = null) => BeginBoundary(
            logger,
            "service",
            component,
            operation,
            stableOperationId,
            serverId,
            subjectFingerprint);

    public static string Fingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return $"sha256:{Convert.ToHexStringLower(digest.AsSpan(0, 12))}";
    }

    public static void LogSafeDebug(
        this ILogger logger,
        Exception exception,
        string message,
        params object?[] args) => LogSafe(
            logger,
            LogLevel.Debug,
            exception,
            message,
            args);

    public static void LogSafeWarning(
        this ILogger logger,
        Exception exception,
        string message,
        params object?[] args) => LogSafe(
            logger,
            LogLevel.Warning,
            exception,
            message,
            args);

    public static void LogSafeError(
        this ILogger logger,
        Exception exception,
        string message,
        params object?[] args) => LogSafe(
            logger,
            LogLevel.Error,
            exception,
            message,
            args);

    public static void LogSafeCritical(
        this ILogger logger,
        Exception exception,
        string message,
        params object?[] args) => LogSafe(
            logger,
            LogLevel.Critical,
            exception,
            message,
            args);

    private static IDisposable BeginBoundary(
        ILogger logger,
        string scopeKind,
        string component,
        string operation,
        object? stableOperationId,
        string? serverId,
        string? subjectFingerprint)
    {
        var additionalFields = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(serverId))
        {
            additionalFields["ServerFingerprint"] = Fingerprint(serverId);
        }
        if (!string.IsNullOrWhiteSpace(subjectFingerprint))
        {
            additionalFields["SubjectFingerprint"] = NormalizeFingerprint(subjectFingerprint);
        }

        return Begin(
            logger,
            scopeKind,
            component,
            operation,
            stableOperationId,
            explicitCorrelationId: null,
            additionalFields);
    }

    private static IDisposable Begin(
        ILogger logger,
        string scopeKind,
        string component,
        string operation,
        object? stableOperationId,
        string? explicitCorrelationId,
        IReadOnlyDictionary<string, object?> additionalFields)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var parent = Ambient.Value;
        var stableId = ToStableId(stableOperationId);
        var correlationId = parent?.CorrelationId ??
            explicitCorrelationId ??
            CreateRootCorrelationId(component, operation, stableId);
        var context = new CorrelationContext(
            correlationId,
            NormalizeLabel(scopeKind),
            NormalizeLabel(component),
            NormalizeLabel(operation),
            stableId is null ? "ephemeral" : Fingerprint(stableId));
        Ambient.Value = context;

        var fields = new Dictionary<string, object?>
        {
            ["CorrelationId"] = context.CorrelationId,
            ["OperationId"] = context.OperationId,
            ["Operation"] = context.Operation,
            ["Component"] = context.Component,
            ["ScopeKind"] = context.ScopeKind
        };
        foreach (var field in additionalFields)
        {
            fields[field.Key] = field.Value;
        }

        var loggingScope = logger.BeginScope(fields);
        return new ScopeLease(parent, loggingScope);
    }

    private static void LogSafe(
        ILogger logger,
        LogLevel level,
        Exception exception,
        string message,
        object?[] args)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exception);

        var fallbackScope = Ambient.Value is null
            ? BeginOperation(
                logger,
                "UnscopedLog",
                "exception",
                $"{exception.GetType().FullName}:{exception.HResult}")
            : NullScope.Instance;
        using (fallbackScope)
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["ExceptionType"] = exception.GetType().Name,
            ["ErrorFingerprint"] = Fingerprint(
                $"{exception.GetType().FullName}:{exception.HResult}")
        }))
        {
            // Passing null here is intentional. Exception.Message and exception
            // serialization are not trusted log input and may contain credentials.
            switch (level)
            {
                case LogLevel.Debug:
                    logger.LogDebug(message, args);
                    break;
                case LogLevel.Warning:
                    logger.LogWarning(message, args);
                    break;
                case LogLevel.Error:
                    logger.LogError(message, args);
                    break;
                case LogLevel.Critical:
                    logger.LogCritical(message, args);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(level), level, null);
            }
        }
    }

    private static string CreateRootCorrelationId(
        string component,
        string operation,
        string? stableOperationId)
    {
        if (stableOperationId is not null)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
                $"pal-control-log-v1\n{component}\n{operation}\n{stableOperationId}"));
            Span<char> id = stackalloc char[36];
            var hex = Convert.ToHexStringLower(digest.AsSpan(0, 16));
            hex.AsSpan(0, 8).CopyTo(id);
            id[8] = '-';
            hex.AsSpan(8, 4).CopyTo(id[9..]);
            id[13] = '-';
            hex.AsSpan(12, 4).CopyTo(id[14..]);
            id[18] = '-';
            hex.AsSpan(16, 4).CopyTo(id[19..]);
            id[23] = '-';
            hex.AsSpan(20, 12).CopyTo(id[24..]);
            return id.ToString();
        }

        var traceId = Activity.Current?.TraceId;
        return traceId is { } current && current != default
            ? current.ToString()
            : Guid.NewGuid().ToString("D");
    }

    private static string? ToStableId(object? stableOperationId) => stableOperationId switch
    {
        null => null,
        Guid guid => guid.ToString("N"),
        string value when !string.IsNullOrWhiteSpace(value) => value.Trim(),
        IFormattable value => value.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => stableOperationId.ToString()
    };

    private static string NormalizeLabel(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            value.Length <= 96 &&
            value.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '-' or '_'))
        {
            return value;
        }

        return $"redacted-{Fingerprint(value)}";
    }

    private static string NormalizeFingerprint(string value) =>
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.Length == 31 &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0
            ? value
            : Fingerprint(value);

    private sealed record CorrelationContext(
        string CorrelationId,
        string ScopeKind,
        string Component,
        string Operation,
        string OperationId);

    private sealed class ScopeLease(
        CorrelationContext? parent,
        IDisposable? loggingScope) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            loggingScope?.Dispose();
            Ambient.Value = parent;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

public sealed class ControlPlaneCorrelationMiddleware(
    RequestDelegate next,
    ILogger<ControlPlaneCorrelationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[ControlPlaneLog.CorrelationHeaderName];
        var correlationId = supplied.Count == 1 &&
            Guid.TryParseExact(supplied[0], "D", out var parsed)
                ? parsed.ToString("D")
                : Guid.NewGuid().ToString("D");

        context.Items[ControlPlaneLog.HttpCorrelationItemKey] = correlationId;
        context.Response.Headers[ControlPlaneLog.CorrelationHeaderName] = correlationId;
        using var scope = ControlPlaneLog.BeginHttpRequest(
            logger,
            correlationId,
            context.Request.Method);
        await next(context);
    }

    public static string GetCorrelationId(HttpContext context) =>
        context.Items.TryGetValue(ControlPlaneLog.HttpCorrelationItemKey, out var value) &&
        value is string correlationId
            ? correlationId
            : throw new InvalidOperationException(
                "The control-plane correlation middleware did not run for this request.");
}
