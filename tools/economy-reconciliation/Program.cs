using System.Text.Json;
using PalControl.EconomyReconciliation;

var jsonOptions = EconomyReconciliationAuditor.JsonOptions;
try
{
    var options = CommandLineOptions.Parse(args);
    var baseline = options.BaselinePath is null
        ? null
        : JsonSerializer.Deserialize<EconomyReconciliationReport>(
            await File.ReadAllTextAsync(options.BaselinePath),
            jsonOptions) ?? throw new InvalidDataException("The baseline report is empty.");
    var report = EconomyReconciliationAuditor.Audit(options.DatabasePath, baseline);
    var json = JsonSerializer.Serialize(report, jsonOptions) + Environment.NewLine;
    if (options.OutputPath is not null)
    {
        AtomicFile.WriteAllText(options.OutputPath, json);
    }
    Console.Write(json);
    return report.Success ? 0 : 2;
}
catch (Exception exception)
{
    var failure = new
    {
        schemaVersion = 1,
        success = false,
        fatalCode = "ECONOMY_RECONCILIATION_FATAL",
        errorType = exception.GetType().Name,
        message = "The read-only economy reconciliation could not be completed."
    };
    Console.Error.WriteLine(JsonSerializer.Serialize(failure, jsonOptions));
    return 3;
}

internal sealed record CommandLineOptions(
    string DatabasePath,
    string? BaselinePath,
    string? OutputPath)
{
    public static CommandLineOptions Parse(string[] args)
    {
        string? database = null;
        string? baseline = null;
        string? output = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            string RequireValue()
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                {
                    throw new ArgumentException($"{argument} requires a value.");
                }
                return args[index];
            }
            switch (argument)
            {
                case "--database":
                    database = RequireValue();
                    break;
                case "--baseline":
                    baseline = RequireValue();
                    break;
                case "--output":
                    output = RequireValue();
                    break;
                default:
                    throw new ArgumentException(
                        "Usage: --database <extraction-commerce.db> [--baseline <report.json>] [--output <report.json>]");
            }
        }
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new ArgumentException("--database is required.");
        }
        return new CommandLineOptions(
            SafePath.ExistingFile(database),
            baseline is null ? null : SafePath.ExistingFile(baseline),
            output is null ? null : SafePath.OutputFile(output));
    }
}
