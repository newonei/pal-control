using System.Text.Json;
using PalControl.AcceptanceEvidence;

try
{
    var commandLine = CommandLine.Parse(args);
    switch (commandLine.Command)
    {
        case "verify":
        {
            commandLine.RequireOnly("manifest", "trust-store", "trust-store-sha256");
            var schema = EvidenceVerifier.LoadTrustedSchema();
            var catalog = EvidenceVerifier.LoadTrustedCatalog(schema);
            var trustStore = EvidenceVerifier.LoadTrustStore(
                commandLine.Require("trust-store"),
                commandLine.Require("trust-store-sha256"));
            var summary = EvidenceVerifier.Verify(
                commandLine.Require("manifest"),
                catalog,
                schema,
                trustStore,
                DateTimeOffset.UtcNow);
            Console.WriteLine(JsonSerializer.Serialize(summary, EvidenceVerifier.SerializerOptions));
            return 0;
        }
        case "signature-payload":
        {
            commandLine.RequireOnly(
                "manifest",
                "trust-store-sha256",
                "executor-key",
                "reviewer-key",
                "output");
            var payload = EvidenceVerifier.CreateSignaturePayload(
                commandLine.Require("manifest"),
                commandLine.Require("trust-store-sha256"),
                commandLine.Require("executor-key"),
                commandLine.Require("reviewer-key"));
            var output = Path.GetFullPath(commandLine.Require("output"));
            var parent = Path.GetDirectoryName(output)
                ?? throw new ArgumentException("--output must have a parent directory.");
            Directory.CreateDirectory(parent);
            using (var stream = new FileStream(
                       output,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                created = true,
                output,
                payloadSchema = EvidenceConstants.SignaturePayloadSchemaId,
                sizeBytes = payload.Length
            }, EvidenceVerifier.SerializerOptions));
            return 0;
        }
        case "hash":
        {
            commandLine.RequireOnly("file");
            Console.WriteLine(EvidenceVerifier.ComputeFileSha256(commandLine.Require("file")));
            return 0;
        }
        case "combination-id":
        {
            commandLine.RequireOnly("manifest");
            var manifest = EvidenceVerifier.ReadManifestForCombination(
                commandLine.Require("manifest"));
            Console.WriteLine(EvidenceVerifier.ComputeCombinationId(manifest.VersionCombination));
            return 0;
        }
        case "list-gates":
        {
            commandLine.RequireOnly();
            var schema = EvidenceVerifier.LoadTrustedSchema();
            var catalog = EvidenceVerifier.LoadTrustedCatalog(schema);
            Console.WriteLine(JsonSerializer.Serialize(
                catalog.Document.Gates.Select(gate => new
                {
                    gate.Id,
                    gate.Priority,
                    gate.Title,
                    gate.TodoReferences
                }),
                EvidenceVerifier.SerializerOptions));
            return 0;
        }
        case "create-template":
        {
            commandLine.RequireOnly("gate", "output");
            var schema = EvidenceVerifier.LoadTrustedSchema();
            var catalog = EvidenceVerifier.LoadTrustedCatalog(schema);
            var gateId = commandLine.Require("gate");
            if (!catalog.Policies.TryGetValue(gateId, out var gate))
            {
                throw new EvidenceValidationException(
                    "ACCEPTANCE_GATE_UNKNOWN",
                    $"Gate '{gateId}' is not present in the catalog.");
            }
            var output = Path.GetFullPath(commandLine.Require("output"));
            var parent = Path.GetDirectoryName(output)
                ?? throw new ArgumentException("--output must have a parent directory.");
            Directory.CreateDirectory(parent);
            var template = TemplateFactory.Create(gate, catalog, schema, DateTimeOffset.UtcNow);
            using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, template, EvidenceVerifier.SerializerOptions);
                stream.WriteByte((byte)'\n');
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                created = true,
                gateId,
                output,
                warning = "Template-only: verification must fail until real live evidence replaces every placeholder."
            }, EvidenceVerifier.SerializerOptions));
            return 0;
        }
        case "create-campaign":
        {
            commandLine.RequireOnly("output");
            var schema = EvidenceVerifier.LoadTrustedSchema();
            var catalog = EvidenceVerifier.LoadTrustedCatalog(schema);
            var summary = CampaignManager.Create(
                commandLine.Require("output"),
                catalog,
                schema,
                DateTimeOffset.UtcNow);
            Console.WriteLine(JsonSerializer.Serialize(
                summary,
                EvidenceVerifier.SerializerOptions));
            return 0;
        }
        case "inspect-campaign":
        {
            commandLine.RequireOnly("root");
            var schema = EvidenceVerifier.LoadTrustedSchema();
            var catalog = EvidenceVerifier.LoadTrustedCatalog(schema);
            var summary = CampaignManager.Inspect(
                commandLine.Require("root"),
                catalog);
            Console.WriteLine(JsonSerializer.Serialize(
                summary,
                EvidenceVerifier.SerializerOptions));
            return 0;
        }
        case "verify-campaign":
        {
            commandLine.RequireOnly("root", "trust-store", "trust-store-sha256");
            var schema = EvidenceVerifier.LoadTrustedSchema();
            var catalog = EvidenceVerifier.LoadTrustedCatalog(schema);
            var trustStore = EvidenceVerifier.LoadTrustStore(
                commandLine.Require("trust-store"),
                commandLine.Require("trust-store-sha256"));
            var summary = CampaignManager.Verify(
                commandLine.Require("root"),
                catalog,
                schema,
                trustStore,
                DateTimeOffset.UtcNow);
            Console.WriteLine(JsonSerializer.Serialize(
                summary,
                EvidenceVerifier.SerializerOptions));
            return summary.Complete ? 0 : 2;
        }
        default:
            throw new ArgumentException(
                "Usage: verify --manifest <manifest.json> --trust-store <identity-trust-store.json> --trust-store-sha256 <sha256:...> | " +
                "signature-payload --manifest <manifest.json> --trust-store-sha256 <sha256:...> --executor-key <key-id> --reviewer-key <key-id> --output <payload.json> | " +
                "hash --file <artifact> | combination-id --manifest <manifest.json> | list-gates | " +
                "create-template --gate <gate-id> --output <manifest.json> | " +
                "create-campaign --output <new-directory> | inspect-campaign --root <campaign-directory> | " +
                "verify-campaign --root <campaign-directory> --trust-store <identity-trust-store.json> --trust-store-sha256 <sha256:...>");
    }
}
catch (Exception exception) when (
    exception is EvidenceValidationException or ArgumentException or FormatException or
        IOException or UnauthorizedAccessException or JsonException)
{
    var code = exception is EvidenceValidationException validation
        ? validation.Code
        : "ACCEPTANCE_CLI_ERROR";
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        valid = false,
        code,
        message = exception.Message
    }, EvidenceVerifier.SerializerOptions));
    return 2;
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

    public bool TryGetValue(string name, out string value) =>
        Options.TryGetValue(name, out value!);

    public string GetValueOrDefault(string name, string fallback) =>
        Options.GetValueOrDefault(name) ?? fallback;

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
