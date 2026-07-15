using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PalControl.ControlApi.Domain;
using PalControl.ControlApi.Extraction;
using PalControl.ControlApi.Infrastructure;

var directory = Path.Combine(Path.GetTempPath(), $"pal-control-safety-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
try
{
    var environment = new TestWebHostEnvironment(directory);
    var persistence = Options.Create(new ExtractionPersistenceOptions
    {
        DataDirectory = directory
    });
    var operationGate = new ExtractionOperationGate(persistence, environment);
    var dependencyProbe = new FakeDependencyProbe();
    var modeOptions = Options.Create(new ExtractionModeOptions { Enabled = true });
    var safetyOptions = Options.Create(new EconomySafetyOptions
    {
        DeliveryBacklogCapacity = 2,
        PalDefenderGrantReceiptSemanticsVerified = true
    });
    var gate = new EconomySafetyGate(
        dependencyProbe,
        operationGate,
        modeOptions,
        safetyOptions,
        persistence,
        environment);
    var readyQueue = new EconomyQueueSnapshot(true, 0, 2, 0, 2);

    Assert((await gate.EvaluateAsync(
        EconomyWriteFeature.Purchase,
        null,
        readyQueue,
        CancellationToken.None)).Enabled,
        "A healthy purchase gate was blocked.");
    Assert((await gate.EvaluateAsync(
        EconomyWriteFeature.ResourceExchange,
        null,
        readyQueue,
        CancellationToken.None)).Enabled,
        "A healthy resource-exchange gate was blocked.");

    var unverifiedReceiptGate = new EconomySafetyGate(
        dependencyProbe,
        operationGate,
        modeOptions,
        Options.Create(new EconomySafetyOptions { DeliveryBacklogCapacity = 2 }),
        persistence,
        environment);
    var unverifiedReceiptDecision = await unverifiedReceiptGate.EvaluateAsync(
        EconomyWriteFeature.Purchase,
        null,
        readyQueue,
        CancellationToken.None);
    Assert(!unverifiedReceiptDecision.Enabled &&
           unverifiedReceiptDecision.Blockers.Any(blocker =>
               blocker.Code == "PALDEFENDER_GRANT_RECEIPT_UNVERIFIED"),
        "Unverified PalDefender grant semantics did not fail purchase closed.");

    _ = await gate.SetCircuitAsync(
        EconomyWriteFeature.Purchase,
        writesEnabled: false,
        "purchase incident",
        "test-owner",
        CancellationToken.None);
    var purchaseClosed = await gate.EvaluateAsync(
        EconomyWriteFeature.Purchase,
        null,
        readyQueue,
        CancellationToken.None);
    var resourceStillOpen = await gate.EvaluateAsync(
        EconomyWriteFeature.ResourceExchange,
        null,
        readyQueue,
        CancellationToken.None);
    Assert(!purchaseClosed.Enabled &&
           purchaseClosed.Blockers.Any(blocker => blocker.Code == "PURCHASE_CIRCUIT_OPEN"),
        "The purchase circuit did not expose its stable blocker.");
    Assert(resourceStillOpen.Enabled,
        "Closing purchase also closed the independent resource-exchange circuit.");

    _ = await gate.SetCircuitAsync(
        EconomyWriteFeature.Purchase,
        writesEnabled: true,
        "purchase recovered",
        "test-owner",
        CancellationToken.None);
    Assert((await gate.EvaluateAsync(
        EconomyWriteFeature.Purchase,
        null,
        readyQueue,
        CancellationToken.None)).Enabled,
        "Purchase could not be reopened without restarting the service.");

    using (var lease = await gate.AcquireAsync(
               EconomyWriteFeature.ResourceExchange,
               null,
               readyQueue,
               CancellationToken.None))
    {
        var closeTask = gate.SetCircuitAsync(
            EconomyWriteFeature.ResourceExchange,
            writesEnabled: false,
            "drain active exchange",
            "test-owner",
            CancellationToken.None);
        await Task.Delay(50);
        Assert(!closeTask.IsCompleted,
            "Circuit close did not wait for the admitted resource operation to drain.");
        lease.Dispose();
        _ = await closeTask;
    }

    var reloaded = new EconomySafetyGate(
        dependencyProbe,
        operationGate,
        modeOptions,
        safetyOptions,
        persistence,
        environment);
    Assert(!reloaded.Current.ResourceExchange.WritesEnabled,
        "Persisted resource circuit state was lost across gate reconstruction.");
    _ = await reloaded.SetCircuitAsync(
        EconomyWriteFeature.ResourceExchange,
        writesEnabled: true,
        "exchange recovered",
        "test-owner",
        CancellationToken.None);

    var fullQueue = await reloaded.EvaluateAsync(
        EconomyWriteFeature.Purchase,
        null,
        new EconomyQueueSnapshot(true, 2, 2, 0, 2),
        CancellationToken.None);
    Assert(!fullQueue.Enabled &&
           fullQueue.Blockers.Any(blocker => blocker.Code == "SHOP_DELIVERY_QUEUE_FULL"),
        "A full delivery queue did not close purchase with a stable blocker.");

    dependencyProbe.PurchaseBlockers =
    [
        new ApiError("PALDEFENDER_VERSION_NOT_APPROVED", "version drift")
    ];
    Assert(!(await reloaded.EvaluateAsync(
        EconomyWriteFeature.Purchase,
        null,
        readyQueue,
        CancellationToken.None)).Enabled,
        "Purchase remained open after adapter-version drift.");
    Assert((await reloaded.EvaluateAsync(
        EconomyWriteFeature.ResourceExchange,
        null,
        readyQueue,
        CancellationToken.None)).Enabled,
        "Purchase adapter drift incorrectly killed resource-exchange writes.");

    var mutationRan = false;
    try
    {
        using var ignored = await reloaded.AcquireAsync(
            EconomyWriteFeature.Purchase,
            null,
            readyQueue,
            CancellationToken.None);
        mutationRan = true;
    }
    catch (ExtractionModeException exception)
    {
        Assert(exception.Code == "PALDEFENDER_VERSION_NOT_APPROVED",
            "Admission returned a non-stable dependency blocker.");
    }
    Assert(!mutationRan, "A blocked gate allowed the protected mutation to run.");

    using (var repository = new SqliteExtractionRepository(
               Path.Combine(directory, "write-probe")))
    {
        await repository.ProbeWriteAsync(CancellationToken.None);
        Assert(repository.IsReady, "The rolled-back write probe damaged store readiness.");
    }

    Assert(new EconomySafetyOptions().IsValid(out _),
        "Default non-Native safety options are invalid.");
    Assert(!new EconomySafetyOptions
    {
        RequireNativeForPurchase = true
    }.IsValid(out _),
        "Native purchase was accepted without approved build and capabilities.");
    Assert(new ExtractionRconOptions { Enabled = false }.IsValid(out _),
        "Disabled RCON should not require a password or version probe.");
    Assert(!new ExtractionRconOptions { Enabled = true }.IsValid(out _),
        "Enabled RCON was accepted without exactly one password source.");
    Assert(new ExtractionRconOptions
    {
        Enabled = true,
        Password = "test-only-password"
    }.IsValid(out _),
        "Structurally valid loopback RCON options were rejected.");

    var permissionClient = new PalDefenderRestClient(
        new HttpClient { BaseAddress = new Uri("http://127.0.0.1:17993/v1/pdapi/") },
        Options.Create(new PalDefenderRestOptions
        {
            Enabled = true,
            Token = "test-token",
            Permissions =
            [
                "REST.Version.Read",
                "REST.Players.Read",
                "REST.Items.Read",
                "REST.Items.Give"
            ]
        }),
        NullLogger<PalDefenderRestClient>.Instance);
    Assert((await permissionClient.ProbeConfiguredPermissionsAsync(
        ["REST.Version.Read", "REST.Items.Give"],
        CancellationToken.None)).Success,
        "Configured PalDefender purchase permissions were not recognized.");
    Assert(!(await permissionClient.ProbeConfiguredPermissionsAsync(
        ["REST.Punishments.Ban"],
        CancellationToken.None)).Success,
        "An undeclared PalDefender permission was treated as available.");

    _ = await operationGate.SetAsync(
        maintenance: true,
        "restart persistence check",
        "test-owner",
        CancellationToken.None);
    dependencyProbe.PurchaseBlockers = [];
    var maintenanceDecision = await gate.EvaluateAsync(
        EconomyWriteFeature.Purchase,
        null,
        readyQueue,
        CancellationToken.None);
    var reopenDecision = await gate.EvaluateForMaintenanceReopenAsync(
        EconomyWriteFeature.Purchase,
        readyQueue,
        CancellationToken.None);
    Assert(!maintenanceDecision.Enabled && maintenanceDecision.Blockers.Any(blocker =>
               blocker.Code == "EXTRACTION_MAINTENANCE") && reopenDecision.Enabled,
        "Maintenance-safe reopen probing did not ignore only the maintenance blocker.");
    var reloadedOperationGate = new ExtractionOperationGate(persistence, environment);
    Assert(reloadedOperationGate.Current.Maintenance,
        "Persisted operation maintenance state was lost across reconstruction.");
    _ = await reloadedOperationGate.SetAsync(
        maintenance: false,
        "restart persistence recovered",
        "test-owner",
        CancellationToken.None);

    Console.WriteLine(
        "PASS: independent circuits, drain, persistence, queue pressure, drift, admission, write/permission probes, and conditional options.");
}
finally
{
    try
    {
        Directory.Delete(directory, recursive: true);
    }
    catch
    {
        // Windows may briefly retain SQLite handles after process teardown.
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeDependencyProbe : IEconomySafetyDependencyProbe
{
    public IReadOnlyList<ApiError> PurchaseBlockers { get; set; } = [];
    public IReadOnlyList<ApiError> ResourceBlockers { get; set; } = [];

    public Task<IReadOnlyList<ApiError>> ProbeAsync(
        EconomyWriteFeature feature,
        EconomySafetyContext? context,
        CancellationToken cancellationToken) =>
        Task.FromResult(feature == EconomyWriteFeature.Purchase
            ? PurchaseBlockers
            : ResourceBlockers);
}

sealed class TestWebHostEnvironment : IWebHostEnvironment
{
    public TestWebHostEnvironment(string root)
    {
        ApplicationName = "PalControl.EconomySafety.ContractTests";
        EnvironmentName = "Testing";
        WebRootPath = root;
        WebRootFileProvider = new NullFileProvider();
        ContentRootPath = root;
        ContentRootFileProvider = new PhysicalFileProvider(root);
    }

    public string ApplicationName { get; set; }
    public IFileProvider WebRootFileProvider { get; set; }
    public string WebRootPath { get; set; }
    public string EnvironmentName { get; set; }
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
