using System.Globalization;
using System.Text.Json;
using PalControl.ZoneCalibration;

try
{
    var commandLine = CommandLine.Parse(args);
    switch (commandLine.Command)
    {
        case "schema-info":
            commandLine.RequireOnly();
            Console.WriteLine(JsonSerializer.Serialize(
                ZoneCalibrationVerifier.GetEmbeddedSchemaHashes(),
                ZoneCalibrationVerifier.SerializerOptions));
            return 0;
        case "prepare-review":
            {
                commandLine.RequireOnly(CliArguments.CommonEvidence);
                var challenge = ZoneCalibrationVerifier.PrepareReview(
                    commandLine.Require("evidence"),
                    commandLine.Require("trust-store"),
                    commandLine.RequireTrustStorePin(),
                    commandLine.BuildExpectedBinding(),
                    DateTimeOffset.UtcNow);
                Console.WriteLine(JsonSerializer.Serialize(challenge, ZoneCalibrationVerifier.SerializerOptions));
                return 0;
            }
        case "verify":
            {
                commandLine.RequireOnly(CliArguments.CommonEvidence.Concat(["report", "report-hash"]).ToArray());
                var result = ZoneCalibrationVerifier.VerifyAndWriteReport(
                    commandLine.Require("evidence"),
                    commandLine.Require("trust-store"),
                    commandLine.RequireTrustStorePin(),
                    commandLine.BuildExpectedBinding(),
                    commandLine.Require("report"),
                    commandLine.Require("report-hash"),
                    DateTimeOffset.UtcNow);
                Console.WriteLine(JsonSerializer.Serialize(result, ZoneCalibrationVerifier.SerializerOptions));
                return 0;
            }
        case "verify-report":
            {
                commandLine.RequireOnly(CliArguments.CommonEvidence.Concat(["report", "report-hash"]).ToArray());
                var result = ZoneCalibrationVerifier.VerifyCanonicalReport(
                    commandLine.Require("evidence"),
                    commandLine.Require("report"),
                    commandLine.Require("report-hash"),
                    commandLine.Require("trust-store"),
                    commandLine.RequireTrustStorePin(),
                    commandLine.BuildExpectedBinding(),
                    DateTimeOffset.UtcNow);
                Console.WriteLine(JsonSerializer.Serialize(result, ZoneCalibrationVerifier.SerializerOptions));
                return 0;
            }
        case "hash":
            commandLine.RequireOnly("file");
            Console.WriteLine(ZoneCalibrationVerifier.ComputeFileSha256(commandLine.Require("file")));
            return 0;
        default:
            throw new ArgumentException(
                "Usage: schema-info | prepare-review/verify/verify-report with a pinned trust store and every --expected-* binding | hash --file <path>");
    }
}
catch (Exception exception) when (
    exception is ZoneCalibrationException or ArgumentException or FormatException or
        IOException or UnauthorizedAccessException or JsonException or OverflowException)
{
    var code = exception is ZoneCalibrationException validation
        ? validation.Code
        : "ZONE_CALIBRATION_CLI_ERROR";
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        valid = false,
        code,
        message = exception.Message
    }, ZoneCalibrationVerifier.SerializerOptions));
    return 2;
}

internal static class CliArguments
{
    public static readonly string[] CommonPolicy =
    [
        "trust-store",
        "trust-store-sha256",
        "expected-server-id",
        "expected-game-build",
        "expected-steam-build",
        "expected-content-version-id",
        "expected-content-version-number",
        "expected-content-hash",
        "expected-zone-id",
        "max-evidence-age-seconds"
    ];

    public static readonly string[] CommonEvidence = CommonPolicy.Concat(["evidence"]).ToArray();
}

internal sealed class CommandLine
{
    private CommandLine(string command, Dictionary<string, string> options)
    {
        Command = command;
        Options = options;
    }

    public string Command { get; }
    private Dictionary<string, string> Options { get; }

    public static CommandLine Parse(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("A command is required.");
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var current = args[index];
            if (!current.StartsWith("--", StringComparison.Ordinal) || current.Length <= 2)
            {
                throw new ArgumentException($"Unexpected argument '{current}'.");
            }
            var name = current[2..];
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Argument '--{name}' requires a value.");
            }
            if (!options.TryAdd(name, args[index]))
            {
                throw new ArgumentException($"Duplicate argument '--{name}'.");
            }
        }
        return new CommandLine(args[0], options);
    }

    public string Require(string name) =>
        Options.GetValueOrDefault(name)
        ?? throw new ArgumentException($"--{name} is required.");

    public string RequireTrustStorePin()
    {
        var value = Options.GetValueOrDefault("trust-store-sha256");
        if (string.IsNullOrWhiteSpace(value))
        {
            value = Environment.GetEnvironmentVariable("PAL_CONTROL_ZONE_TRUST_STORE_SHA256");
        }
        return value ?? throw new ArgumentException(
            "--trust-store-sha256 or PAL_CONTROL_ZONE_TRUST_STORE_SHA256 is required.");
    }

    public ZoneCalibrationExpectedBinding BuildExpectedBinding()
    {
        if (!Guid.TryParseExact(Require("expected-content-version-id"), "D", out var versionId) ||
            !long.TryParse(
                Require("expected-content-version-number"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var versionNumber) ||
            !long.TryParse(
                Require("max-evidence-age-seconds"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var maximumAgeSeconds))
        {
            throw new ArgumentException("Expected content version and maximum evidence age are invalid.");
        }
        return new ZoneCalibrationExpectedBinding(
            Require("expected-server-id"),
            Require("expected-game-build"),
            Require("expected-steam-build"),
            versionId,
            versionNumber,
            Require("expected-content-hash"),
            Require("expected-zone-id"),
            TimeSpan.FromSeconds(maximumAgeSeconds));
    }

    public void RequireOnly(params string[] allowed)
    {
        var allow = new HashSet<string>(allowed, StringComparer.Ordinal);
        var unknown = Options.Keys.FirstOrDefault(key => !allow.Contains(key));
        if (unknown is not null)
        {
            throw new ArgumentException($"Unknown argument '--{unknown}' for command '{Command}'.");
        }
    }
}
