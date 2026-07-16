using System.Diagnostics;

var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();
var dataDirectory = builder.Configuration["data-directory"]
    ?? throw new InvalidOperationException("Synthetic target data directory is missing.");
var logDirectory = builder.Configuration["log-directory"]
    ?? throw new InvalidOperationException("Synthetic target log directory is missing.");
var operationsRequests = 0;

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready", readReady = true }));
app.MapGet("/health/instance", () =>
{
    using var process = Process.GetCurrentProcess();
    return Results.Ok(new
    {
        schemaVersion = 1,
        service = "pal-control-api",
        processId = Environment.ProcessId,
        processStartedAtUtc = process.StartTime.ToUniversalTime(),
        dataDirectoryFingerprint = PathFingerprint(dataDirectory),
        logDirectoryFingerprint = PathFingerprint(logDirectory)
    });
});
app.MapGet("/api/v1/economy/observability", () => Results.Ok(new
{
    schemaVersion = 1,
    credentialCanary = "api-response-secret-must-not-enter-report"
}));
app.MapGet("/api/v1/extraction/admin/operations/overview", () =>
{
    Interlocked.Increment(ref operationsRequests);
    var gc = GC.GetGCMemoryInfo();
    return Results.Ok(new
    {
        schemaVersion = 1,
        credentialCanary = "api-response-secret-must-not-enter-report",
        runtime = new
        {
            instance = Instance(),
            sessions = new { active = 0 },
            gc = new
            {
                heapSizeBytes = gc.HeapSizeBytes,
                totalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false),
                gen0Collections = GC.CollectionCount(0),
                gen1Collections = GC.CollectionCount(1),
                gen2Collections = GC.CollectionCount(2)
            }
        },
        queues = new
        {
            delivery = new { pending = 0, capacity = 128 },
            settlement = new { pending = 0, capacity = 32 },
            outbox = new { pending = 0, capacity = 256 }
        }
    });
});
app.MapGet("/test/operations-count", () => Results.Ok(new
{
    count = Volatile.Read(ref operationsRequests)
}));

await app.RunAsync();

object Instance()
{
    using var process = Process.GetCurrentProcess();
    return new
    {
        processId = Environment.ProcessId,
        processStartedAtUtc = process.StartTime.ToUniversalTime(),
        dataDirectoryFingerprint = PathFingerprint(dataDirectory),
        logDirectoryFingerprint = PathFingerprint(logDirectory)
    };
}

static string PathFingerprint(string path)
{
    var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    if (OperatingSystem.IsWindows())
    {
        normalized = normalized.ToUpperInvariant();
    }
    return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"pal-control-runtime-path-v1\n{normalized}")));
}
