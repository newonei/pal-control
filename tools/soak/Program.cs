using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalControl.Soak;

return await SoakProgram.RunAsync(args);

internal static class SoakProgram
{
    private static readonly HashSet<string> AllowedWorkloadPaths = new(StringComparer.Ordinal)
    {
        "/health/live",
        "/health/ready",
        "/api/v1/economy/observability",
        "/api/v1/extraction/admin/operations/overview?limit=1&refresh=false"
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += handler;
            try
            {
                var runner = new SoakRunner(options);
                var report = await runner.RunAsync(cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = report.SchemaVersion,
                    status = report.Status,
                    samples = report.Samples.Count,
                    violations = report.Analysis.Violations.Count
                }));
                return report.Analysis.Passed ? 0 : 2;
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }
        catch
        {
            Console.Error.WriteLine("Soak configuration is invalid or unavailable; no credential or response content was emitted.");
            return 1;
        }
    }

    private static SoakRunnerOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var ciMode = false;
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (string.Equals(name, "--ci-mode", StringComparison.Ordinal))
            {
                if (ciMode)
                {
                    throw new ArgumentException("Duplicate option.");
                }
                ciMode = true;
                continue;
            }
            if (!name.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new ArgumentException("Every option requires a value.");
            }
            if (!values.TryAdd(name, args[++index]))
            {
                throw new ArgumentException("Duplicate option.");
            }
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "--pid", "--base-uri", "--data-directory", "--log-directory",
            "--output-directory", "--api-key-environment-variable",
            "--duration-seconds", "--sample-interval-seconds", "--recovery-seconds",
            "--requests-per-second", "--request-timeout-seconds", "--workload-path",
            "--thresholds"
        };
        if (values.Keys.Any(key => !allowed.Contains(key)))
        {
            throw new ArgumentException("Unknown option.");
        }

        var processId = RequiredInt(values, "--pid");
        if (processId <= 0)
        {
            throw new ArgumentException("PID is invalid.");
        }
        var baseUri = new Uri(Required(values, "--base-uri"), UriKind.Absolute);
        if (baseUri.UserInfo.Length != 0 ||
            baseUri.Query.Length != 0 ||
            baseUri.Fragment.Length != 0 ||
            baseUri.Scheme is not ("http" or "https") ||
            !IsLoopback(baseUri) ||
            baseUri.AbsolutePath != "/")
        {
            throw new ArgumentException("The management base URI must be a credential-free loopback origin.");
        }

        var dataDirectory = ExistingDirectory(Required(values, "--data-directory"));
        var logDirectory = ExistingDirectory(Required(values, "--log-directory"));
        var outputDirectory = NewOutputDirectory(
            Required(values, "--output-directory"),
            dataDirectory,
            logDirectory);
        var apiKeyVariable = values.GetValueOrDefault(
            "--api-key-environment-variable",
            "PAL_CONTROL_SOAK_API_KEY");
        if (apiKeyVariable.Length is < 1 or > 64 ||
            apiKeyVariable.Any(character =>
                character is not (>= 'A' and <= 'Z') &&
                character is not (>= '0' and <= '9') &&
                character != '_'))
        {
            throw new ArgumentException("API key environment variable name is invalid.");
        }
        var apiKey = Environment.GetEnvironmentVariable(apiKeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length is < 16 or > 512 ||
            apiKey.Any(char.IsControl))
        {
            throw new InvalidOperationException("Viewer API key environment variable is unavailable.");
        }

        var duration = OptionalInt(values, "--duration-seconds", 24 * 60 * 60);
        var interval = OptionalInt(values, "--sample-interval-seconds", 60);
        var recovery = OptionalInt(values, "--recovery-seconds", 120);
        var requestsPerSecond = OptionalDouble(values, "--requests-per-second", 1);
        var requestTimeout = OptionalInt(values, "--request-timeout-seconds", 5);
        var workloadPath = values.GetValueOrDefault("--workload-path", "/health/live");
        if (!AllowedWorkloadPaths.Contains(workloadPath))
        {
            throw new ArgumentException("Workload path is not on the read-only allow-list.");
        }

        if (ciMode)
        {
            if (duration is < 3 or > 300 || interval is < 1 or > 60 ||
                recovery is < 1 or > 60 || requestTimeout is < 1 or > 30)
            {
                throw new ArgumentException("CI timing is outside its bounded range.");
            }
        }
        else if (duration is < 24 * 60 * 60 or > 7 * 24 * 60 * 60 ||
                 interval is < 10 or > 300 || recovery is < 60 or > 3600 ||
                 requestTimeout is < 1 or > 30)
        {
            throw new ArgumentException("Production soak must run for 24 hours to 7 days with bounded sampling and recovery.");
        }
        if (duration < interval * 2 ||
            !double.IsFinite(requestsPerSecond) ||
            requestsPerSecond is <= 0 or > 50)
        {
            throw new ArgumentException("Fixed read-only load settings are invalid.");
        }

        var thresholdDocument = ReadThresholds(
            values.GetValueOrDefault("--thresholds"),
            ciMode);
        var minimumSamples = checked(
            (int)Math.Ceiling(duration / (double)interval) + 1 +
            (int)Math.Ceiling(recovery / (double)interval));
        var run = new SoakRunConfiguration(
            duration,
            interval,
            recovery,
            requestsPerSecond,
            minimumSamples,
            Math.Max(1, (int)Math.Floor(duration * requestsPerSecond * 0.95d)),
            workloadPath);
        return new SoakRunnerOptions(
            processId,
            baseUri,
            dataDirectory,
            logDirectory,
            apiKey,
            outputDirectory,
            run,
            thresholdDocument.Thresholds,
            ciMode ? "ci-non-acceptance" : "production-24h-v1",
            thresholdDocument.Sha256,
            TimeSpan.FromSeconds(requestTimeout));
    }

    private static ThresholdDocument ReadThresholds(string? configuredPath, bool ciMode)
    {
        var embedded = ReadEmbeddedProductionThresholds();
        byte[] bytes;
        if (configuredPath is null)
        {
            bytes = embedded;
        }
        else
        {
            var fullPath = Path.GetFullPath(configuredPath);
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length is < 2 or > 64 * 1024 ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("The soak threshold document is unavailable or unsafe.");
            }
            bytes = File.ReadAllBytes(fullPath);
            if (!ciMode && !SHA256.HashData(bytes).AsSpan()
                    .SequenceEqual(SHA256.HashData(embedded)))
            {
                throw new InvalidDataException(
                    "Production soak thresholds must exactly match the embedded approved profile.");
            }
        }
        var thresholds = JsonSerializer.Deserialize<SoakThresholds>(bytes, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new JsonException("Threshold document is empty.");
        thresholds.Validate();
        return new ThresholdDocument(
            thresholds,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static byte[] ReadEmbeddedProductionThresholds()
    {
        using var stream = typeof(SoakProgram).Assembly.GetManifestResourceStream(
            "PalControl.Soak.thresholds.production.json")
            ?? throw new InvalidDataException("Embedded production soak thresholds are missing.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("Required option is missing.");

    private static int RequiredInt(IReadOnlyDictionary<string, string> values, string name) =>
        int.TryParse(Required(values, name), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException("Integer option is invalid.");

    private static int OptionalInt(
        IReadOnlyDictionary<string, string> values,
        string name,
        int fallback) =>
        values.TryGetValue(name, out var raw)
            ? int.TryParse(raw, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new ArgumentException("Integer option is invalid.")
            : fallback;

    private static double OptionalDouble(
        IReadOnlyDictionary<string, string> values,
        string name,
        double fallback) =>
        values.TryGetValue(name, out var raw)
            ? double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new ArgumentException("Number option is invalid.")
            : fallback;

    private static string ExistingDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new IOException("Required metrics directory is unavailable.");
        }
        RejectReparsePath(fullPath);
        return fullPath;
    }

    private static string NewOutputDirectory(
        string path,
        params string[] protectedDirectories)
    {
        var fullPath = Path.GetFullPath(path);
        RejectReparsePath(fullPath);
        if (protectedDirectories.Any(protectedDirectory =>
                PathsOverlap(fullPath, protectedDirectory)))
        {
            throw new IOException(
                "The soak output directory must be disjoint from data and log directories.");
        }
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw new IOException(
                "The soak output directory must not already exist; evidence directories are immutable.");
        }
        return fullPath;
    }

    private static bool PathsOverlap(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var firstRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var secondRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        if (string.Equals(firstRoot, secondRoot, comparison))
        {
            return true;
        }
        var separator = Path.DirectorySeparatorChar.ToString();
        return firstRoot.StartsWith(secondRoot + separator, comparison) ||
               secondRoot.StartsWith(firstRoot + separator, comparison);
    }

    private static void RejectReparsePath(string path)
    {
        for (DirectoryInfo? current = new DirectoryInfo(Path.GetFullPath(path));
             current is not null;
             current = current.Parent)
        {
            if (current.Exists &&
                current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Soak paths cannot resolve through reparse points.");
            }
        }
    }

    private static bool IsLoopback(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    private sealed record ThresholdDocument(
        SoakThresholds Thresholds,
        string Sha256);
}
