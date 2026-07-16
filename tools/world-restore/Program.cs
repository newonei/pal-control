using System.Text;
using PalControl.WorldRestore;

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    Console.WriteLine(Arguments.Usage);
    return 0;
}

try
{
    var arguments = Arguments.Parse(args);
    var engine = new WorldRestoreEngine();
    CommandResult result = arguments.Command switch
    {
        "plan" => engine.CreatePlan(
            arguments.Required("backup-dir"),
            arguments.Required("active-world-dir"),
            arguments.Required("server-id"),
            arguments.Required("world-guid"),
            arguments.Required("settings-file"),
            arguments.Required("palserver-executable"),
            arguments.Required("evidence-dir"),
            arguments.Required("trust-store-sha256")),
        "apply" => engine.Apply(
            arguments.Required("plan-file"),
            arguments.HasFlag("execute"),
            arguments.Values("approval-file"),
            arguments.Optional("trust-store"),
            arguments.Optional("trust-store-sha256")),
        "status" => engine.Status(arguments.Required("plan-file")),
        "recover" => engine.Recover(
            arguments.Required("plan-file"),
            arguments.Required("trust-store-sha256"),
            arguments.Values("approval-file")),
        "approve" => engine.CreateApproval(
            arguments.Required("plan-file"),
            arguments.Required("subject"),
            arguments.Required("reason"),
            arguments.Required("private-key-file"),
            arguments.Required("output-file"),
            arguments.Required("trust-store-sha256"),
            arguments.OptionalInt("valid-for-minutes", 10)),
        "approve-recovery" => engine.CreateRecoveryApproval(
            arguments.Required("plan-file"),
            arguments.Required("subject"),
            arguments.Required("reason"),
            arguments.Required("private-key-file"),
            arguments.Required("output-file"),
            arguments.Required("trust-store-sha256"),
            arguments.OptionalInt("valid-for-minutes", 10)),
        "keygen" => engine.GenerateKeyPair(
            arguments.Required("private-key-file"),
            arguments.Required("public-key-file")),
        _ => throw new ArgumentException("Unknown command.\n" + Arguments.Usage)
    };
    Console.WriteLine(Encoding.UTF8.GetString(CanonicalJson.Serialize(result)));
    return 0;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine($"ERROR: {exception.Message}");
    return 1;
}

file sealed class Arguments
{
    private static readonly IReadOnlyDictionary<string, CommandShape> Shapes =
        new Dictionary<string, CommandShape>(StringComparer.Ordinal)
        {
            ["plan"] = new(
                [
                    "backup-dir", "active-world-dir", "server-id", "world-guid",
                    "settings-file", "palserver-executable", "evidence-dir",
                    "trust-store-sha256"
                ],
                [],
                []),
            ["apply"] = new(
                ["plan-file"],
                ["trust-store", "trust-store-sha256", "approval-file"],
                ["execute"]),
            ["status"] = new(
                ["plan-file"],
                [],
                []),
            ["recover"] = new(
                ["plan-file", "trust-store-sha256"],
                ["approval-file"],
                []),
            ["approve"] = new(
                [
                    "plan-file", "subject", "reason", "private-key-file",
                    "output-file", "trust-store-sha256"
                ],
                ["valid-for-minutes"],
                []),
            ["approve-recovery"] = new(
                [
                    "plan-file", "subject", "reason", "private-key-file",
                    "output-file", "trust-store-sha256"
                ],
                ["valid-for-minutes"],
                []),
            ["keygen"] = new(
                ["private-key-file", "public-key-file"],
                [],
                [])
        };

    private readonly Dictionary<string, List<string>> _options;
    private readonly HashSet<string> _flags;

    private Arguments(
        string command,
        Dictionary<string, List<string>> options,
        HashSet<string> flags)
    {
        Command = command;
        _options = options;
        _flags = flags;
    }

    public string Command { get; }

    public const string Usage = """
        Pal Control offline world restore

        Usage:
          plan --backup-dir <managed-backup> --active-world-dir <world>
               --server-id <id> --world-guid <guid>
               --settings-file <GameUserSettings.ini>
               --palserver-executable <PalServer.exe> --evidence-dir <dir>
               --trust-store-sha256 <externally-published-64-hex-pin>

          apply --plan-file <canonical-plan.json>
                [--execute --trust-store <trusted-approvers.json>
                 --trust-store-sha256 <externally-published-64-hex-pin>
                 --approval-file <signed-a.json> --approval-file <signed-b.json>]

          status --plan-file <canonical-plan.json>

          recover --plan-file <canonical-plan.json>
                  --trust-store-sha256 <externally-published-64-hex-pin>
                  --approval-file <current-recovery-a.json>
                  --approval-file <current-recovery-b.json>

          approve --plan-file <canonical-plan.json> --subject <trusted-subject>
                  --reason <text> --private-key-file <approver-private.pem>
                  --output-file <signed-approval.json>
                  --trust-store-sha256 <externally-published-64-hex-pin>
                  [--valid-for-minutes <1..15>]

          approve-recovery --plan-file <canonical-plan.json>
                  --subject <trusted-subject> --reason <text>
                  --private-key-file <approver-private.pem>
                  --output-file <signed-recovery-approval.json>
                  --trust-store-sha256 <externally-published-64-hex-pin>
                  [--valid-for-minutes <1..15>]

          keygen --private-key-file <new-private.pem> --public-key-file <new-public.pem>

        Safety:
          apply is plan-only unless --execute is present. Execution also requires
          a stopped matching PalServer.exe, two current approvals from different
          trusted ECDSA P-256 subjects, and an unchanged plan/manifest/staging tree.
          The trust-store pin is mandatory and must come from an external
          approved publication; this tool never derives or adopts it implicitly.
          status is read-only. Manual recover also requires two new, current,
          distinct recovery-purpose approvals bound to the exact journal bytes.
        """;

    public static Arguments Parse(string[] args)
    {
        var command = args[0].Trim().ToLowerInvariant();
        if (!Shapes.TryGetValue(command, out var shape))
        {
            throw new ArgumentException($"Unknown command '{args[0]}'.\n{Usage}");
        }
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length < 3)
            {
                throw new ArgumentException($"Unexpected argument '{token}'.\n{Usage}");
            }
            var name = token[2..];
            if (shape.Flags.Contains(name, StringComparer.Ordinal))
            {
                if (!flags.Add(name))
                {
                    throw new ArgumentException($"Duplicate flag '--{name}'.");
                }
                continue;
            }
            if (!shape.Required.Contains(name, StringComparer.Ordinal) &&
                !shape.Optional.Contains(name, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Option '--{name}' is not valid for {command}.");
            }
            if (index + 1 >= args.Length ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '--{name}' requires a value.");
            }
            var value = args[++index];
            if (!options.TryGetValue(name, out var values))
            {
                values = [];
                options.Add(name, values);
            }
            if (name != "approval-file" && values.Count > 0)
            {
                throw new ArgumentException($"Duplicate option '--{name}'.");
            }
            values.Add(value);
        }
        foreach (var required in shape.Required)
        {
            if (!options.TryGetValue(required, out var values) || values.Count != 1)
            {
                throw new ArgumentException($"Required option '--{required}' is missing.");
            }
        }
        return new Arguments(command, options, flags);
    }

    public string Required(string name) =>
        _options.TryGetValue(name, out var values) && values.Count == 1
            ? values[0]
            : throw new ArgumentException($"Required option '--{name}' is missing.");

    public string? Optional(string name) =>
        _options.TryGetValue(name, out var values) && values.Count == 1
            ? values[0]
            : null;

    public IReadOnlyList<string> Values(string name) =>
        _options.TryGetValue(name, out var values) ? values : [];

    public bool HasFlag(string name) => _flags.Contains(name);

    public int OptionalInt(string name, int fallback)
    {
        var value = Optional(name);
        return value is null
            ? fallback
            : int.TryParse(value, out var parsed)
                ? parsed
                : throw new ArgumentException($"Option '--{name}' must be an integer.");
    }

    private sealed record CommandShape(
        IReadOnlyList<string> Required,
        IReadOnlyList<string> Optional,
        IReadOnlyList<string> Flags);
}
