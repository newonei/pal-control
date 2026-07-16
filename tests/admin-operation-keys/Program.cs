using PalControl.ControlApi.Infrastructure;

var root = Path.Combine(
    Path.GetTempPath(),
    $"pal-control-admin-operation-keys-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    var firstStore = new AdminOperationKeyStore(root);
    var first = await firstStore.RegisterAsync(
        "operation-key-0001",
        "economy-maintenance",
        "true\nverified maintenance reason",
        "season-admin-a",
        CancellationToken.None);
    Assert(!first.Replayed, "The first registration was incorrectly reported as a replay.");

    // Constructing a new store proves the binding is authoritative across a
    // Control API restart rather than an in-memory cache.
    var restartedStore = new AdminOperationKeyStore(root);
    var replay = await restartedStore.RegisterAsync(
        "operation-key-0001",
        "economy-maintenance",
        "true\nverified maintenance reason",
        "season-admin-a",
        CancellationToken.None);
    Assert(replay.Replayed, "The cross-restart replay was not detected.");
    Assert(replay.RequestHash == first.RequestHash && replay.CreatedAt == first.CreatedAt,
        "The cross-restart replay did not retain the original immutable binding.");

    await AssertConflictAsync(() => restartedStore.RegisterAsync(
        "operation-key-0001",
        "economy-maintenance",
        "false\na different target",
        "season-admin-a",
        CancellationToken.None), "same key / different payload");
    await AssertConflictAsync(() => restartedStore.RegisterAsync(
        "operation-key-0001",
        "economy-maintenance",
        "true\nverified maintenance reason",
        "season-admin-b",
        CancellationToken.None), "same key / different authenticated subject");
    await AssertConflictAsync(() => restartedStore.RegisterAsync(
        "operation-key-0001",
        "wallet-adjustment",
        "true\nverified maintenance reason",
        "season-admin-a",
        CancellationToken.None), "same key / different operation scope");

    const string concurrentKey = "operation-key-concurrent-0001";
    var registrations = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
        restartedStore.RegisterAsync(
            concurrentKey,
            "run-reconciliation",
            "run-a\nsettled\nindependently verified",
            "economy-admin-a",
            CancellationToken.None)));
    Assert(registrations.Count(item => !item.Replayed) == 1,
        "Concurrent registration created more than one authoritative operation binding.");
    Assert(registrations.Select(item => item.RequestHash).Distinct(StringComparer.Ordinal).Count() == 1,
        "Concurrent replays returned inconsistent request hashes.");

    // SQLite stores only hashes and bounded metadata. Neither the human reason
    // nor the administrator subject may appear in the database or WAL bytes.
    var forbidden = new[]
    {
        "verified maintenance reason",
        "season-admin-a",
        "economy-admin-a",
        "independently verified"
    };
    foreach (var file in Directory.EnumerateFiles(root))
    {
        var bytes = await File.ReadAllBytesAsync(file);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var secret in forbidden)
        {
            Assert(!text.Contains(secret, StringComparison.Ordinal),
                $"The operation-key store leaked raw audit input into {Path.GetFileName(file)}.");
        }
    }

    Console.WriteLine("PASS: durable administrator operation keys, conflict fencing, 100-way concurrency and raw-input non-persistence.");
}
finally
{
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task AssertConflictAsync(Func<Task> action, string scenario)
{
    try
    {
        await action();
        throw new InvalidOperationException($"Expected conflict was not raised for {scenario}.");
    }
    catch (AdminOperationKeyConflictException)
    {
        // Expected stable conflict.
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
