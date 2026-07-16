using System.Security.Cryptography;
using PalControl.ControlApi.Infrastructure;

try
{
    var arguments = Arguments.Parse(args);
    switch (arguments.Command)
    {
        case "generate":
            {
                var seasonId = arguments.RequiredGuid("season");
                var key = ReadSecret(arguments.Required("pseudonym-key-file"), 32, 4_096);
                try
                {
                    var timeZone = TimeZoneInfo.FindSystemTimeZoneById(arguments.Required("time-zone"));
                    var archive = new WeeklyEconomyReportArchive(
                        arguments.Required("data-dir"),
                        arguments.Required("archive-root"),
                        timeZone,
                        arguments.Required("review-trust-store"),
                        ReadExpectedReviewTrustSha256());
                    var result = await archive.GenerateAsync(
                        seasonId,
                        key,
                        arguments.OptionalGuid("previous-season"),
                        arguments.Optional("previous-review-head-sha256"),
                        arguments.Optional("expected-existing-review-head-sha256"),
                        arguments.HasFlag("html"));
                    Console.WriteLine(result.Created
                        ? "Weekly economy report archive created."
                        : "Weekly economy report archive verified as an idempotent replay.");
                    PrintStatus(
                        result.ArchiveDirectory,
                        result.ManifestSha256,
                        result.ReviewHeadSha256,
                        result.ReviewStatus);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
                break;
            }
        case "verify":
        case "status":
            {
                var archive = new WeeklyEconomyReportArchive(
                    arguments.Required("archive-root"),
                    arguments.Required("review-trust-store"),
                    ReadExpectedReviewTrustSha256());
                var verified = archive.Verify(
                    arguments.RequiredGuid("season"),
                    arguments.Required("expected-review-head-sha256"));
                Console.WriteLine("Weekly economy report archive verification passed.");
                PrintStatus(
                    verified.ArchiveDirectory,
                    verified.ManifestSha256,
                    verified.ReviewHeadSha256,
                    verified.ReviewStatus);
                break;
            }
        case "review":
            {
                var reviewerBytes = ReadSecret(
                    arguments.Required("reviewer-subject-file"),
                    1,
                    4_096);
                try
                {
                    var reviewer = System.Text.Encoding.UTF8.GetString(reviewerBytes).Trim();
                    var archive = new WeeklyEconomyReportArchive(
                        arguments.Required("archive-root"),
                        arguments.Required("review-trust-store"),
                        ReadExpectedReviewTrustSha256());
                    var verified = archive.AppendReview(
                        arguments.RequiredGuid("season"),
                        reviewer,
                        arguments.Required("reviewer-private-key-file"),
                        arguments.Required("decision"),
                        arguments.Required("reason"),
                        arguments.Required("expected-current-review-head-sha256"));
                    Console.WriteLine("Weekly economy report review revision appended and verified.");
                    PrintStatus(
                        verified.ArchiveDirectory,
                        verified.ManifestSha256,
                        verified.ReviewHeadSha256,
                        verified.ReviewStatus);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(reviewerBytes);
                }
                break;
            }
        default:
            throw new ArgumentException(
                "Command must be generate, verify, status, or review.\n" + Arguments.Usage);
    }
    return 0;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine($"ERROR: {exception.Message}");
    return 1;
}

static void PrintStatus(
    string directory,
    string manifestSha256,
    string reviewHeadSha256,
    WeeklyEconomyReportReviewStatus status)
{
    Console.WriteLine($"Archive: {directory}");
    Console.WriteLine($"Manifest SHA-256: {manifestSha256}");
    Console.WriteLine($"Review head SHA-256: {reviewHeadSha256}");
    Console.WriteLine(
        $"Review: {status.State} ({status.DistinctApprovals}/{status.RequiredApprovals} approvals)");
}

static byte[] ReadSecret(string path, int minimumBytes, int maximumBytes)
{
    var fullPath = Path.GetFullPath(path);
    RejectReparseAncestors(fullPath);
    ValidateRegularSecretFile(fullPath);
    using var stream = new FileStream(
        fullPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 4_096,
        FileOptions.SequentialScan);
    var length = stream.Length;
    if (length < minimumBytes || length > maximumBytes)
    {
        throw new InvalidDataException(
            $"Secret input must contain {minimumBytes} to {maximumBytes} bytes.");
    }
    var bytes = new byte[checked((int)length)];
    try
    {
        stream.ReadExactly(bytes);
        if (stream.Length != length || stream.Position != length)
        {
            throw new InvalidDataException("The secret input changed while it was being read.");
        }

        RejectReparseAncestors(fullPath);
        ValidateRegularSecretFile(fullPath);
        using var confirmationStream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4_096,
            FileOptions.SequentialScan);
        if (confirmationStream.Length != length)
        {
            throw new InvalidDataException("The secret input changed while it was being read.");
        }
        var confirmation = new byte[bytes.Length];
        try
        {
            confirmationStream.ReadExactly(confirmation);
            if (confirmationStream.Length != length ||
                confirmationStream.Position != length ||
                !CryptographicOperations.FixedTimeEquals(bytes, confirmation))
            {
                throw new InvalidDataException("The secret input changed while it was being read.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(confirmation);
        }
        return bytes;
    }
    catch
    {
        CryptographicOperations.ZeroMemory(bytes);
        throw;
    }
}

static void ValidateRegularSecretFile(string fullPath)
{
    var info = new FileInfo(fullPath);
    if (!info.Exists ||
        (info.Attributes & (FileAttributes.Directory |
                            FileAttributes.Device |
                            FileAttributes.ReparsePoint)) != 0)
    {
        throw new FileNotFoundException(
            "A secret input must be an existing regular file and cannot be a reparse point.",
            fullPath);
    }
}

static void RejectReparseAncestors(string path)
{
    var fullPath = Path.GetFullPath(path);
    var file = new FileInfo(fullPath);
    if (file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint))
    {
        throw new InvalidDataException("Secret input paths cannot contain reparse points.");
    }
    for (DirectoryInfo? current = file.Directory;
         current is not null;
         current = current.Parent)
    {
        if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Secret input paths cannot resolve through reparse points.");
        }
    }
}

static string ReadExpectedReviewTrustSha256()
{
    var value = Environment.GetEnvironmentVariable(
        "PAL_CONTROL_WEEKLY_REVIEW_TRUST_SHA256")?.Trim();
    if (value is null || value.Length != 64 ||
        value.Any(character => character is not (>= '0' and <= '9') and
            not (>= 'a' and <= 'f')))
    {
        throw new InvalidOperationException(
            "PAL_CONTROL_WEEKLY_REVIEW_TRUST_SHA256 must contain the externally approved lowercase SHA-256.");
    }
    return value;
}

file sealed class Arguments
{
    private readonly Dictionary<string, string> _options;
    private readonly HashSet<string> _flags;

    private Arguments(
        string command,
        Dictionary<string, string> options,
        HashSet<string> flags)
    {
        Command = command;
        _options = options;
        _flags = flags;
    }

    public string Command { get; }

    public const string Usage = """
        Usage:
          generate --data-dir <dir> --archive-root <dir> --season <guid>
                   --time-zone <id> --pseudonym-key-file <file>
                   --review-trust-store <file>
                   [--previous-season <guid> --previous-review-head-sha256 <sha256>]
                   [--expected-existing-review-head-sha256 <sha256>] [--html]
          verify   --archive-root <dir> --season <guid> --review-trust-store <file>
                   --expected-review-head-sha256 <sha256>
          status   --archive-root <dir> --season <guid> --review-trust-store <file>
                   --expected-review-head-sha256 <sha256>
          review   --archive-root <dir> --season <guid>
                   --review-trust-store <file> --reviewer-subject-file <file>
                   --reviewer-private-key-file <file>
                   --decision <approve|reject> --reason <text>
                   --expected-current-review-head-sha256 <sha256>
        """;

    public static Arguments Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            throw new ArgumentException(Usage);
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length < 3)
            {
                throw new ArgumentException($"Unexpected argument '{token}'.\n{Usage}");
            }
            var key = token[2..];
            if (key == "html")
            {
                if (!flags.Add(key))
                {
                    throw new ArgumentException($"Duplicate flag '--{key}'.");
                }
                continue;
            }
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '--{key}' requires a value.");
            }
            if (!options.TryAdd(key, args[++index]))
            {
                throw new ArgumentException($"Duplicate option '--{key}'.");
            }
        }
        return new Arguments(args[0].Trim().ToLowerInvariant(), options, flags);
    }

    public string Required(string name) =>
        _options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required option '--{name}'.\n{Usage}");

    public Guid RequiredGuid(string name) => ParseGuid(Required(name), name);

    public Guid? OptionalGuid(string name) =>
        _options.TryGetValue(name, out var value) ? ParseGuid(value, name) : null;

    public string? Optional(string name) =>
        _options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    public bool HasFlag(string name) => _flags.Contains(name);

    private static Guid ParseGuid(string value, string name) =>
        Guid.TryParse(value, out var result) && result != Guid.Empty
            ? result
            : throw new ArgumentException($"Option '--{name}' must be a non-empty GUID.");
}
