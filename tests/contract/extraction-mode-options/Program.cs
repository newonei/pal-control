using PalControl.ControlApi.Infrastructure;

List<string> failures = [];

Check(
    "enabled legacy-v1 keeps the frozen 1000/300 grant",
    new ExtractionModeOptions
    {
        Enabled = true,
        BootstrapPolicyVersion = "legacy-v1",
        InitialMarketCoin = 1_000,
        InitialSeasonVoucher = 300,
        ExtractionZones = ValidZones()
    },
    expectedValid: true);
Check(
    "enabled legacy-v1 rejects a changed MarketCoin grant",
    new ExtractionModeOptions
    {
        Enabled = true,
        BootstrapPolicyVersion = "legacy-v1",
        InitialMarketCoin = 1_001,
        InitialSeasonVoucher = 300,
        ExtractionZones = ValidZones()
    },
    expectedValid: false);
Check(
    "enabled legacy-v1 rejects a changed SeasonVoucher grant",
    new ExtractionModeOptions
    {
        Enabled = true,
        BootstrapPolicyVersion = "legacy-v1",
        InitialMarketCoin = 1_000,
        InitialSeasonVoucher = 301,
        ExtractionZones = ValidZones()
    },
    expectedValid: false);
Check(
    "enabled legacy-v1 rejects an unversioned zero grant",
    new ExtractionModeOptions
    {
        Enabled = true,
        BootstrapPolicyVersion = "legacy-v1",
        InitialMarketCoin = 0,
        InitialSeasonVoucher = 0,
        ExtractionZones = ValidZones()
    },
    expectedValid: false);
Check(
    "enabled non-legacy policy rejects a positive MarketCoin grant",
    new ExtractionModeOptions
    {
        Enabled = true,
        BootstrapPolicyVersion = "production-v1",
        InitialMarketCoin = 1,
        InitialSeasonVoucher = 0,
        ExtractionZones = ValidZones()
    },
    expectedValid: false);
Check(
    "enabled non-legacy policy rejects a positive SeasonVoucher grant",
    new ExtractionModeOptions
    {
        Enabled = true,
        BootstrapPolicyVersion = "production-v1",
        InitialMarketCoin = 0,
        InitialSeasonVoucher = 1,
        ExtractionZones = ValidZones()
    },
    expectedValid: false);
Check(
    "enabled non-legacy zero-grant policy is valid",
    new ExtractionModeOptions
    {
        Enabled = true,
        BootstrapPolicyVersion = "production-v1",
        InitialMarketCoin = 0,
        InitialSeasonVoucher = 0,
        ExtractionZones = ValidZones()
    },
    expectedValid: true);
Check(
    "disabled production zero-grant policy is valid",
    new ExtractionModeOptions
    {
        Enabled = false,
        BootstrapPolicyVersion = "production-v1",
        InitialMarketCoin = 0,
        InitialSeasonVoucher = 0,
        ExtractionZones = ValidZones()
    },
    expectedValid: true);
Check(
    "duplicate extraction-zone ids are rejected",
    new ExtractionModeOptions
    {
        Enabled = false,
        BootstrapPolicyVersion = "production-v1",
        InitialMarketCoin = 0,
        InitialSeasonVoucher = 0,
        ExtractionZones = [new ExtractionZoneOptions(), new ExtractionZoneOptions()]
    },
    expectedValid: false);

if (failures.Count > 0)
{
    Console.Error.WriteLine("ExtractionModeOptions contract failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }
    return 1;
}

Console.WriteLine("PASS: ExtractionModeOptions bootstrap policy and zone contract (9 cases).");
return 0;

static IReadOnlyList<ExtractionZoneOptions> ValidZones() => [new ExtractionZoneOptions()];

void Check(
    string name,
    ExtractionModeOptions options,
    bool expectedValid)
{
    var actualValid = options.IsValid(out var error);
    if (actualValid != expectedValid)
    {
        failures.Add(
            $"{name}: expected valid={expectedValid}, actual valid={actualValid}, error={error ?? "<none>"}");
    }
}
