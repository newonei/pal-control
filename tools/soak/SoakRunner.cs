using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PalControl.Soak;

public sealed record SoakRunnerOptions(
    int ProcessId,
    Uri BaseUri,
    string DataDirectory,
    string LogDirectory,
    string ApiKey,
    string OutputDirectory,
    SoakRunConfiguration Run,
    SoakThresholds Thresholds,
    string EvidenceProfile,
    string ThresholdsSha256,
    TimeSpan RequestTimeout);

public sealed class SoakRunner
{
    private const int MaximumOperationsResponseBytes = 1024 * 1024;
    private readonly SoakRunnerOptions _options;
    private readonly HttpClient _http;
    private readonly DateTime _expectedProcessStartUtc;
    private readonly string _expectedDataDirectoryFingerprint;
    private readonly string _expectedLogDirectoryFingerprint;
    private InstanceMeasurement? _verifiedInstance;

    public SoakRunner(SoakRunnerOptions options)
    {
        _options = options;
        using var process = Process.GetProcessById(options.ProcessId);
        _expectedProcessStartUtc = process.StartTime.ToUniversalTime();
        _expectedDataDirectoryFingerprint = PathFingerprint(options.DataDirectory);
        _expectedLogDirectoryFingerprint = PathFingerprint(options.LogDirectory);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = options.RequestTimeout
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = options.BaseUri,
            Timeout = options.RequestTimeout
        };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("pal-control-soak", "1"));
    }

    public async Task<SoakReport> RunAsync(CancellationToken cancellationToken)
    {
        var samples = new List<SoakSample>();
        var stopwatch = Stopwatch.StartNew();
        var sequence = 0;
        var runnerFailed = false;
        var workload = EmptyWorkload();
        try
        {
            var verifiedInstance = await ProbeInstanceAsync(cancellationToken);
            _verifiedInstance = verifiedInstance;
            if (!verifiedInstance.Available)
            {
                throw new InvalidOperationException(
                    "The unauthenticated instance binding did not match the requested process and directories.");
            }
            samples.Add(await CollectSampleAsync(
                sequence++, "load", stopwatch.Elapsed.TotalSeconds, workload, cancellationToken));
            ThrowIfTargetExited(samples[^1]);
            var nextSampleSeconds = Math.Min(
                _options.Run.SampleIntervalSeconds,
                _options.Run.DurationSeconds);
            while (stopwatch.Elapsed.TotalSeconds < _options.Run.DurationSeconds)
            {
                var windowEnd = Math.Min(nextSampleSeconds, _options.Run.DurationSeconds);
                workload = await RunWorkloadUntilAsync(stopwatch, windowEnd, cancellationToken);
                samples.Add(await CollectSampleAsync(
                    sequence++, "load", stopwatch.Elapsed.TotalSeconds, workload, cancellationToken));
                ThrowIfTargetExited(samples[^1]);
                if (windowEnd >= _options.Run.DurationSeconds)
                {
                    break;
                }
                nextSampleSeconds = Math.Min(
                    _options.Run.DurationSeconds,
                    nextSampleSeconds + _options.Run.SampleIntervalSeconds);
            }

            var recoveryStart = stopwatch.Elapsed.TotalSeconds;
            var recoveryTarget = recoveryStart + _options.Run.RecoverySeconds;
            var nextRecovery = Math.Min(
                recoveryTarget,
                recoveryStart + _options.Run.SampleIntervalSeconds);
            while (_options.Run.RecoverySeconds > 0 &&
                   stopwatch.Elapsed.TotalSeconds < recoveryTarget)
            {
                await DelayUntilAsync(stopwatch, nextRecovery, cancellationToken);
                samples.Add(await CollectSampleAsync(
                    sequence++,
                    "recovery",
                    stopwatch.Elapsed.TotalSeconds,
                    EmptyWorkload(),
                    cancellationToken));
                ThrowIfTargetExited(samples[^1]);
                if (nextRecovery >= recoveryTarget)
                {
                    break;
                }
                nextRecovery = Math.Min(
                    recoveryTarget,
                    nextRecovery + _options.Run.SampleIntervalSeconds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            runnerFailed = true;
        }
        catch
        {
            // The report is deliberately fail-closed and carries only a stable
            // runner_error code. Exception messages can contain machine paths,
            // URLs or response fragments and must never enter release evidence.
            runnerFailed = true;
        }
        finally
        {
            stopwatch.Stop();
            _http.Dispose();
        }

        var report = SoakAnalyzer.Analyze(
            samples,
            _options.Run,
            _options.Thresholds,
            DateTimeOffset.UtcNow,
            runnerFailed,
            _options.EvidenceProfile,
            _options.ThresholdsSha256);
        CanonicalJson.WriteReport(_options.OutputDirectory, report);
        return report;
    }

    private async Task<SoakSample> CollectSampleAsync(
        int sequence,
        string phase,
        double elapsedSeconds,
        WorkloadMeasurement workload,
        CancellationToken cancellationToken)
    {
        var process = MeasureProcess();
        var sqlite = MeasureSqlite();
        var logs = MeasureLogs();
        var live = await ProbeAsync("/health/live", cancellationToken);
        var ready = await ProbeAsync("/health/ready", cancellationToken);
        var operationsResult = await ProbeOperationsAsync(cancellationToken);
        return new SoakSample(
            sequence,
            phase,
            DateTimeOffset.UtcNow,
            elapsedSeconds,
            process,
            operationsResult.Gc,
            sqlite,
            logs,
            new ApiMeasurement(
                live,
                ready,
                operationsResult.Probe,
                operationsResult.Instance,
                operationsResult.Sessions,
                operationsResult.Queues),
            workload);
    }

    private ProcessMeasurement MeasureProcess()
    {
        try
        {
            using var process = Process.GetProcessById(_options.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime() != _expectedProcessStartUtc)
            {
                return UnavailableProcess("process_identity_changed");
            }
            process.Refresh();
            return new ProcessMeasurement(
                true,
                process.WorkingSet64,
                process.PrivateMemorySize64,
                process.HandleCount,
                process.Threads.Count,
                null);
        }
        catch (ArgumentException)
        {
            return UnavailableProcess("process_not_found");
        }
        catch
        {
            return UnavailableProcess("process_metrics_unavailable");
        }
    }

    private SqliteMeasurement MeasureSqlite()
    {
        try
        {
            var files = EnumerateFiles(_options.DataDirectory);
            long databaseBytes = 0;
            long walBytes = 0;
            long shmBytes = 0;
            var databaseCount = 0;
            foreach (var file in files)
            {
                var name = file.Name;
                if (name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase))
                {
                    walBytes = checked(walBytes + file.Length);
                }
                else if (name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
                {
                    shmBytes = checked(shmBytes + file.Length);
                }
                else if (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase))
                {
                    databaseBytes = checked(databaseBytes + file.Length);
                    databaseCount += 1;
                }
            }
            return new SqliteMeasurement(
                true,
                databaseCount,
                databaseBytes,
                walBytes,
                shmBytes,
                null);
        }
        catch
        {
            return new SqliteMeasurement(
                false, 0, null, null, null, "sqlite_metrics_unavailable");
        }
    }

    private LogMeasurement MeasureLogs()
    {
        try
        {
            var files = EnumerateFiles(_options.LogDirectory);
            var total = files.Aggregate(0L, (current, file) => checked(current + file.Length));
            return new LogMeasurement(true, files.Count, total, null);
        }
        catch
        {
            return new LogMeasurement(false, 0, null, "log_metrics_unavailable");
        }
    }

    private static IReadOnlyList<FileInfo> EnumerateFiles(string directory)
    {
        var root = new DirectoryInfo(directory);
        if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Metrics root is unavailable.");
        }
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        return root.EnumerateFiles("*", options).ToArray();
    }

    private async Task<ProbeMeasurement> ProbeAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
            if (relativePath.StartsWith("/api/", StringComparison.Ordinal))
            {
                request.Headers.TryAddWithoutValidation("X-Pal-Admin-Key", _options.ApiKey);
            }
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            _ = await ReadBoundedBodyAsync(response, 64 * 1024, cancellationToken);
            timer.Stop();
            return new ProbeMeasurement(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                timer.Elapsed.TotalMilliseconds,
                response.IsSuccessStatusCode ? null : "http_status_failure");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timer.Stop();
            return new ProbeMeasurement(
                false, null, timer.Elapsed.TotalMilliseconds, "probe_timeout");
        }
        catch (InvalidDataException)
        {
            timer.Stop();
            return new ProbeMeasurement(
                false, null, timer.Elapsed.TotalMilliseconds, "probe_response_too_large");
        }
        catch
        {
            timer.Stop();
            return new ProbeMeasurement(
                false, null, timer.Elapsed.TotalMilliseconds, "probe_transport_failure");
        }
    }

    private async Task<OperationsMeasurement> ProbeOperationsAsync(
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "/api/v1/extraction/admin/operations/overview?limit=1&refresh=false");
            request.Headers.TryAddWithoutValidation("X-Pal-Admin-Key", _options.ApiKey);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var bytes = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(bytes, cancellationToken);
                if (read == 0)
                {
                    break;
                }
                if (buffer.Length + read > MaximumOperationsResponseBytes)
                {
                    timer.Stop();
                    return UnavailableOperations(
                        new ProbeMeasurement(
                            false,
                            (int)response.StatusCode,
                            timer.Elapsed.TotalMilliseconds,
                            "operations_response_too_large"));
                }
                buffer.Write(bytes, 0, read);
            }
            timer.Stop();
            if (!response.IsSuccessStatusCode)
            {
                return UnavailableOperations(new ProbeMeasurement(
                    false,
                    (int)response.StatusCode,
                    timer.Elapsed.TotalMilliseconds,
                    "http_status_failure"));
            }

            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            if (!TryReadOperations(
                    document.RootElement,
                    out var instance,
                    out var sessions,
                    out var queues,
                    out var gc))
            {
                return UnavailableOperations(new ProbeMeasurement(
                    false,
                    (int)response.StatusCode,
                    timer.Elapsed.TotalMilliseconds,
                    "operations_schema_invalid"));
            }
            return new OperationsMeasurement(
                new ProbeMeasurement(
                    true,
                    (int)response.StatusCode,
                    timer.Elapsed.TotalMilliseconds,
                    null),
                instance,
                sessions,
                queues,
                gc);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timer.Stop();
            return UnavailableOperations(new ProbeMeasurement(
                false, null, timer.Elapsed.TotalMilliseconds, "probe_timeout"));
        }
        catch
        {
            timer.Stop();
            return UnavailableOperations(new ProbeMeasurement(
                false, null, timer.Elapsed.TotalMilliseconds, "probe_transport_failure"));
        }
    }

    private async Task<InstanceMeasurement> ProbeInstanceAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health/instance");
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var payload = await ReadBoundedBodyAsync(
                response,
                64 * 1024,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new InstanceMeasurement(false, null, "instance_http_status_failure");
            }
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            return ReadAndValidateInstance(document.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new InstanceMeasurement(false, null, "instance_probe_timeout");
        }
        catch
        {
            return new InstanceMeasurement(false, null, "instance_probe_invalid");
        }
    }

    private bool TryReadOperations(
        JsonElement root,
        out InstanceMeasurement instance,
        out SessionMeasurement sessions,
        out QueueMeasurement queues,
        out GcMeasurement gc)
    {
        instance = new InstanceMeasurement(false, null, "operations_schema_invalid");
        sessions = new SessionMeasurement(false, null, "operations_schema_invalid");
        queues = new QueueMeasurement(false, null, null, null, "operations_schema_invalid");
        gc = new GcMeasurement(false, null, null, null, null, null, "gc_metrics_unavailable");
        if (!TryGetElement(root, ["runtime", "instance"], out var instanceElement) ||
            !TryGetInt32(root, out var active, "runtime", "sessions", "active") || active < 0 ||
            !TryReadQueue(root, "delivery", out var delivery) ||
            !TryReadQueue(root, "settlement", out var settlement) ||
            !TryReadQueue(root, "outbox", out var outbox))
        {
            return false;
        }
        instance = ReadAndValidateInstance(instanceElement);
        if (!instance.Available || _verifiedInstance is null ||
            !string.Equals(
                instance.BindingSha256,
                _verifiedInstance.BindingSha256,
                StringComparison.Ordinal))
        {
            return false;
        }
        sessions = new SessionMeasurement(true, active, null);
        queues = new QueueMeasurement(true, delivery, settlement, outbox, null);

        if (TryGetInt64(root, out var heapSize, "runtime", "gc", "heapSizeBytes") &&
            TryGetInt64(root, out var totalAllocated, "runtime", "gc", "totalAllocatedBytes") &&
            TryGetInt32(root, out var gen0, "runtime", "gc", "gen0Collections") &&
            TryGetInt32(root, out var gen1, "runtime", "gc", "gen1Collections") &&
            TryGetInt32(root, out var gen2, "runtime", "gc", "gen2Collections") &&
            heapSize >= 0 && totalAllocated >= 0 && gen0 >= 0 && gen1 >= 0 && gen2 >= 0)
        {
            gc = new GcMeasurement(
                true, heapSize, totalAllocated, gen0, gen1, gen2, null);
        }
        return true;
    }

    private static bool TryReadQueue(
        JsonElement root,
        string name,
        out QueueValue? value)
    {
        value = null;
        if (!TryGetInt32(root, out var pending, "queues", name, "pending") ||
            !TryGetInt32(root, out var capacity, "queues", name, "capacity") ||
            pending < 0 || capacity <= 0 || pending > capacity)
        {
            return false;
        }
        value = new QueueValue(pending, capacity);
        return true;
    }

    private static bool TryGetInt32(
        JsonElement root,
        out int value,
        params string[] path)
    {
        value = default;
        return TryGetElement(root, path, out var element) && element.TryGetInt32(out value);
    }

    private static bool TryGetInt64(
        JsonElement root,
        out long value,
        params string[] path)
    {
        value = default;
        return TryGetElement(root, path, out var element) && element.TryGetInt64(out value);
    }

    private static bool TryGetElement(
        JsonElement root,
        IReadOnlyList<string> path,
        out JsonElement value)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }
        return true;
    }

    private InstanceMeasurement ReadAndValidateInstance(JsonElement root)
    {
        if (!TryGetInt32(root, out var processId, "processId") ||
            !TryGetString(root, out var startedAtText, "processStartedAtUtc") ||
            !TryGetString(root, out var dataFingerprint, "dataDirectoryFingerprint") ||
            !TryGetString(root, out var logFingerprint, "logDirectoryFingerprint") ||
            !DateTimeOffset.TryParse(
                startedAtText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var startedAt) ||
            processId != _options.ProcessId ||
            Math.Abs((startedAt.UtcDateTime - _expectedProcessStartUtc).TotalSeconds) > 1 ||
            !IsSha256(dataFingerprint) ||
            !IsSha256(logFingerprint) ||
            !string.Equals(
                dataFingerprint,
                _expectedDataDirectoryFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                logFingerprint,
                _expectedLogDirectoryFingerprint,
                StringComparison.Ordinal))
        {
            return new InstanceMeasurement(false, null, "instance_binding_mismatch");
        }
        var canonical = string.Join('\n',
            "pal-control-soak-instance-v1",
            processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _expectedProcessStartUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            dataFingerprint,
            logFingerprint);
        return new InstanceMeasurement(
            true,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
            null);
    }

    private static bool TryGetString(
        JsonElement root,
        out string value,
        params string[] path)
    {
        value = string.Empty;
        if (!TryGetElement(root, path, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            element.GetString() is not { Length: > 0 and <= 256 } text)
        {
            return false;
        }
        value = text;
        return true;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string PathFingerprint(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToUpperInvariant();
        }
        return Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"pal-control-runtime-path-v1\n{normalized}")));
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > maximumBytes)
        {
            throw new InvalidDataException("HTTP probe response exceeded its size limit.");
        }
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
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
                throw new InvalidDataException("HTTP probe response exceeded its size limit.");
            }
            output.Write(buffer, 0, read);
        }
    }

    private async Task<WorkloadMeasurement> RunWorkloadUntilAsync(
        Stopwatch stopwatch,
        double targetElapsedSeconds,
        CancellationToken cancellationToken)
    {
        var latencies = new List<double>();
        var attempted = 0;
        var succeeded = 0;
        var interval = 1d / _options.Run.RequestsPerSecond;
        var nextRequest = stopwatch.Elapsed.TotalSeconds;
        while (nextRequest < targetElapsedSeconds)
        {
            await DelayUntilAsync(stopwatch, nextRequest, cancellationToken);
            if (stopwatch.Elapsed.TotalSeconds >= targetElapsedSeconds)
            {
                break;
            }
            var probe = await ProbeAsync(_options.Run.WorkloadPath, cancellationToken);
            attempted += 1;
            if (probe.Success)
            {
                succeeded += 1;
            }
            latencies.Add(probe.ElapsedMilliseconds);
            nextRequest += interval;
        }
        await DelayUntilAsync(stopwatch, targetElapsedSeconds, cancellationToken);
        var sorted = latencies.Order().ToArray();
        double? p95 = sorted.Length == 0
            ? null
            : sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
        return new WorkloadMeasurement(
            attempted,
            succeeded,
            attempted - succeeded,
            p95);
    }

    private static async Task DelayUntilAsync(
        Stopwatch stopwatch,
        double targetElapsedSeconds,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = targetElapsedSeconds - stopwatch.Elapsed.TotalSeconds;
            if (remaining <= 0)
            {
                return;
            }
            await Task.Delay(
                TimeSpan.FromSeconds(Math.Min(remaining, 0.25)),
                cancellationToken);
        }
    }

    private static ProcessMeasurement UnavailableProcess(string code) =>
        new(false, null, null, null, null, code);

    private static void ThrowIfTargetExited(SoakSample sample)
    {
        if (!sample.Process.Available)
        {
            throw new InvalidOperationException("The sampled process is no longer available.");
        }
    }

    private static OperationsMeasurement UnavailableOperations(ProbeMeasurement probe) =>
        new(
            probe,
            new InstanceMeasurement(false, null, "operations_probe_failed"),
            new SessionMeasurement(false, null, "operations_probe_failed"),
            new QueueMeasurement(false, null, null, null, "operations_probe_failed"),
            new GcMeasurement(false, null, null, null, null, null, "gc_metrics_unavailable"));

    private static WorkloadMeasurement EmptyWorkload() => new(0, 0, 0, null);

    private sealed record OperationsMeasurement(
        ProbeMeasurement Probe,
        InstanceMeasurement Instance,
        SessionMeasurement Sessions,
        QueueMeasurement Queues,
        GcMeasurement Gc);
}
