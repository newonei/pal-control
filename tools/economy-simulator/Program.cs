using System.Text.Json;
using PalControl.ControlApi.Content;
using PalControl.EconomySimulator;

return await EconomySimulatorProgram.RunAsync(args);

internal static class EconomySimulatorProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Usage: dotnet run --project tools/economy-simulator -- [--content FILE] [--seed UINT64] [--players N] [--days N] [--output FILE] [--strict]");
            Console.WriteLine("FILE may be a raw EconomyContentDefinition or a published EconomyContentVersion JSON document.");
            return 0;
        }

        try
        {
            var contentPath = ValueAfter(args, "--content");
            var outputPath = ValueAfter(args, "--output");
            var defaultOptions = EconomySimulationOptions.ReproducibleDefault;
            var options = defaultOptions with
            {
                Seed = Parse(ValueAfter(args, "--seed"), defaultOptions.Seed),
                PlayerCount = Parse(ValueAfter(args, "--players"), defaultOptions.PlayerCount),
                BusinessDays = Parse(ValueAfter(args, "--days"), defaultOptions.BusinessDays)
            };
            var (definition, versionId) = contentPath is null
                ? (SchemeADefaultScenario.Create(), (Guid?)SchemeADefaultScenario.ContentVersionId)
                : await LoadContentAsync(contentPath);
            var report = EconomySimulation.Run(definition, options, versionId);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(EconomyContentJson.Options)
            {
                WriteIndented = true
            });
            if (outputPath is null)
            {
                Console.WriteLine(json);
            }
            else
            {
                await File.WriteAllTextAsync(outputPath, json + Environment.NewLine);
            }
            return args.Contains("--strict", StringComparer.OrdinalIgnoreCase) && !report.WithinDefaultTargets ? 2 : 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"economy-simulator: {exception.Message}");
            return 1;
        }
    }

    private static async Task<(EconomyContentDefinition Definition, Guid? VersionId)> LoadContentAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("definition", out _))
        {
            var version = JsonSerializer.Deserialize<EconomyContentVersion>(json, EconomyContentJson.Options)
                ?? throw new InvalidDataException("Published content version JSON is empty.");
            return (version.Definition, version.VersionId);
        }
        var definition = JsonSerializer.Deserialize<EconomyContentDefinition>(json, EconomyContentJson.Options)
            ?? throw new InvalidDataException("Economy content definition JSON is empty.");
        return (definition, null);
    }

    private static string? ValueAfter(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{name} requires a value.");
            }
            return args[index + 1];
        }
        return null;
    }

    private static ulong Parse(string? value, ulong defaultValue) =>
        value is null ? defaultValue : ulong.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static int Parse(string? value, int defaultValue) =>
        value is null ? defaultValue : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
