using System.Text.Json;
using PalControl.ControlApi.Infrastructure;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

try
{
    var arguments = ParseArguments(args);
    var matrixPath = Require(arguments, "matrix");
    if (arguments.ContainsKey("compute-only"))
    {
        var computed = CompatibilityMatrixValidator.ComputeCanonicalSha256(
            File.ReadAllText(matrixPath));
        Console.WriteLine(computed);
        return 0;
    }

    var snapshot = CompatibilityMatrixValidator.Load(
        matrixPath,
        arguments.GetValueOrDefault("expected-sha256"));
    CompatibilityCombination? combination = null;
    if (arguments.TryGetValue("combination", out var combinationId) &&
        combinationId is not null)
    {
        combination = snapshot.RequireCombination(combinationId);
    }
    if (arguments.ContainsKey("require-stable"))
    {
        if (combination is null)
        {
            throw new ArgumentException("--require-stable requires --combination.");
        }
        CompatibilityMatrixValidator.RequireProductionStable(
            snapshot,
            combination.Id);
    }
    if (HasObservation(arguments) && combination is null)
    {
        throw new ArgumentException("Observed fields require --combination.");
    }
    if (combination is not null)
    {
        RequireObservedMatch(arguments, "game-version", combination.GameVersion);
        RequireObservedMatch(arguments, "steam-build", combination.SteamBuild);
        RequireObservedMatch(
            arguments,
            "paldefender-version",
            combination.PalDefenderVersion);
        RequireObservedMatch(arguments, "ue4ss-commit", combination.Ue4ssCommit);
        RequireObservedMatch(
            arguments,
            "native-protocol",
            combination.NativeProtocolVersion);
        RequireObservedMatch(
            arguments,
            "native-mod",
            combination.NativeModVersion);
        RequireObservedMatch(
            arguments,
            "bridge-availability",
            combination.BridgeAvailability);
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        valid = true,
        snapshot.Document.MatrixVersion,
        snapshot.CanonicalSha256,
        combination = combination is null
            ? null
            : new
            {
                combination.Id,
                status = CompatibilityMatrixValidator.ToWireStatus(combination.Status),
                combination.GameVersion,
                combination.SteamBuild,
                combination.BridgeAvailability
            }
    }, jsonOptions));
    return 0;
}
catch (Exception exception) when (
    exception is ArgumentException or IOException or
        UnauthorizedAccessException or CompatibilityMatrixException)
{
    var code = exception is CompatibilityMatrixException matrixException
        ? matrixException.Code
        : "COMPATIBILITY_GUARD_INVALID_ARGUMENT";
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        valid = false,
        code,
        message = exception.Message
    }, jsonOptions));
    return 2;
}

static Dictionary<string, string?> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string?>(StringComparer.Ordinal);
    for (var index = 0; index < args.Length; index++)
    {
        var current = args[index];
        if (!current.StartsWith("--", StringComparison.Ordinal) || current.Length <= 2)
        {
            throw new ArgumentException($"Unexpected argument '{current}'.");
        }
        var name = current[2..];
        if (!result.TryAdd(name, null))
        {
            throw new ArgumentException($"Duplicate argument '--{name}'.");
        }
        if (name is "require-stable" or "compute-only")
        {
            continue;
        }
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Argument '--{name}' requires a value.");
        }
        result[name] = args[index];
    }
    var allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "matrix",
        "expected-sha256",
        "combination",
        "require-stable",
        "compute-only",
        "game-version",
        "steam-build",
        "paldefender-version",
        "ue4ss-commit",
        "native-protocol",
        "native-mod",
        "bridge-availability"
    };
    var unknown = result.Keys.FirstOrDefault(key => !allowed.Contains(key));
    if (unknown is not null)
    {
        throw new ArgumentException($"Unknown argument '--{unknown}'.");
    }
    return result;
}

static string Require(IReadOnlyDictionary<string, string?> values, string name) =>
    values.GetValueOrDefault(name)
    ?? throw new ArgumentException($"--{name} is required.");

static bool HasObservation(IReadOnlyDictionary<string, string?> values) =>
    new[]
    {
        "game-version",
        "steam-build",
        "paldefender-version",
        "ue4ss-commit",
        "native-protocol",
        "native-mod",
        "bridge-availability"
    }.Any(values.ContainsKey);

static void RequireObservedMatch(
    IReadOnlyDictionary<string, string?> values,
    string field,
    string expected)
{
    if (values.TryGetValue(field, out var observed) &&
        !string.Equals(observed, expected, StringComparison.Ordinal))
    {
        throw new CompatibilityMatrixException(
            "COMPATIBILITY_OBSERVATION_MISMATCH",
            $"Observed {field} does not match combination '{expected}'.");
    }
}
