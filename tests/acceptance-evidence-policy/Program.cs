using System.Text.Json;
using System.Security.Cryptography;
using PalControl.AcceptanceEvidence;
using PalControl.Soak;

if (args.Length == 2 && args[0] == "lease-race")
{
    return RunLeaseRace(args[1]);
}

if (args.Length == 3 && args[0] == "create-soak-fixture")
{
    var production = args[2] is "production" or "padded";
    if (!production && args[2] != "ci")
    {
        return 2;
    }
    var duration = production ? 86400 : 3;
    var interval = production ? 300 : 1;
    var recovery = production ? 300 : 1;
    const double requestsPerSecond = 1;
    var minimumSamples = checked(
        (int)Math.Ceiling(duration / (double)interval) + 1 +
        (int)Math.Ceiling(recovery / (double)interval));
    var run = new SoakRunConfiguration(
        duration,
        interval,
        recovery,
        requestsPerSecond,
        minimumSamples,
        Math.Max(1, (int)Math.Floor(duration * requestsPerSecond * 0.95d)),
        "/health/live");
    using var thresholdStream = typeof(SoakReport).Assembly.GetManifestResourceStream(
        "PalControl.Soak.thresholds.production.json")!;
    using var thresholdBuffer = new MemoryStream();
    thresholdStream.CopyTo(thresholdBuffer);
    var thresholdBytes = thresholdBuffer.ToArray();
    var thresholds = JsonSerializer.Deserialize<SoakThresholds>(
        thresholdBytes,
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    var origin = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
    var samples = new List<SoakSample>();
    var sequence = 0;
    for (var elapsed = 0; elapsed <= duration; elapsed += interval)
    {
        samples.Add(CreateSample(
            sequence++,
            "load",
            origin,
            elapsed,
            elapsed == 0 ? 0 : interval));
    }
    for (var elapsed = duration + interval;
         elapsed <= duration + recovery;
         elapsed += interval)
    {
        samples.Add(CreateSample(sequence++, "recovery", origin, elapsed, 0));
    }
    var thresholdHash = Convert.ToHexStringLower(SHA256.HashData(thresholdBytes));
    var generatedAt = samples[^1].TimestampUtc;
    var report = SoakAnalyzer.Analyze(
        samples,
        run,
        thresholds,
        generatedAt,
        runnerFailed: false,
        production ? "production-24h-v1" : "ci-non-acceptance",
        thresholdHash);
    if (args[2] == "padded")
    {
        var paddedSamples = report.Samples.Select((sample, index) =>
            index switch
            {
                1 => sample with
                {
                    Workload = new WorkloadMeasurement(82_080, 82_080, 0, null)
                },
                > 1 and < 289 => sample with
                {
                    Phase = "ignored-padding",
                    Workload = new WorkloadMeasurement(0, 0, 0, null)
                },
                _ => sample
            }).ToArray();
        report = report with { Samples = paddedSamples };
    }
    CanonicalJson.WriteReport(args[1], report);
    return report.Analysis.Passed ? 0 : 2;
}

if (args.Length == 2 && args[0] == "keygen")
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using (var stream = new FileStream(
               args[1],
               FileMode.CreateNew,
               FileAccess.Write,
               FileShare.None))
    {
        stream.Write(key.ExportPkcs8PrivateKey());
        stream.Flush(flushToDisk: true);
    }
    Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    return 0;
}

if (args.Length == 3 && args[0] == "sign")
{
    using var key = ECDsa.Create();
    key.ImportPkcs8PrivateKey(File.ReadAllBytes(args[1]), out var read);
    if (read == 0 || key.KeySize != 256)
    {
        return 2;
    }
    var signature = key.SignData(
        File.ReadAllBytes(args[2]),
        HashAlgorithmName.SHA256,
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    Console.WriteLine(Convert.ToBase64String(signature));
    return 0;
}

if (args.Length != 5)
{
    Console.Error.WriteLine(
        "Policy harness requires manifest, schema, catalog, trust-store and trust-store-pin arguments.");
    return 2;
}

try
{
    var schema = EvidenceVerifier.LoadSchema(args[1]);
    var catalog = EvidenceVerifier.LoadCatalog(args[2], schema);
    var trustStore = EvidenceVerifier.LoadTrustStore(args[3], args[4]);
    var result = EvidenceVerifier.Verify(
        args[0],
        catalog,
        schema,
        trustStore,
        new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero));
    Console.WriteLine(JsonSerializer.Serialize(result, EvidenceVerifier.SerializerOptions));
    return 0;
}
catch (EvidenceValidationException exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        valid = false,
        code = exception.Code
    }, EvidenceVerifier.SerializerOptions));
    return 2;
}

static SoakSample CreateSample(
    int sequence,
    string phase,
    DateTimeOffset origin,
    int elapsedSeconds,
    int workloadAttempts)
{
    var probe = new ProbeMeasurement(true, 200, 5, null);
    return new SoakSample(
        sequence,
        phase,
        origin.AddSeconds(elapsedSeconds),
        elapsedSeconds,
        new ProcessMeasurement(true, 100_000_000, 90_000_000, 120, 20, null),
        new GcMeasurement(true, 20_000_000, 40_000_000, 3, 2, 1, null),
        new SqliteMeasurement(true, 1, 10_000_000, 0, 0, null),
        new LogMeasurement(true, 2, 2_000_000, null),
        new ApiMeasurement(
            probe,
            probe,
            probe,
            new InstanceMeasurement(true, new string('a', 64), null),
            new SessionMeasurement(true, 2, null),
            new QueueMeasurement(
                true,
                new QueueValue(0, 64),
                new QueueValue(0, 64),
                new QueueValue(0, 64),
                null)),
        new WorkloadMeasurement(
            workloadAttempts,
            workloadAttempts,
            0,
            workloadAttempts == 0 ? null : 10));
}

static int RunLeaseRace(string root)
{
    Directory.CreateDirectory(root);
    var manifestPath = Path.Combine(root, "manifest.json");
    var artifactPath = Path.Combine(root, "artifact.bin");
    var trustPath = Path.Combine(root, "trust-store.json");
    var largePath = Path.Combine(root, "later-large-artifact.bin");
    File.WriteAllText(manifestPath, "{\"lease\":\"manifest\"}\n");
    File.WriteAllText(artifactPath, "leased-artifact-before-large-file");
    File.WriteAllText(trustPath, "{\"lease\":\"trust-store\"}\n");
    using (var stream = new FileStream(
               largePath,
               FileMode.CreateNew,
               FileAccess.Write,
               FileShare.None))
    {
        stream.SetLength(128L * 1024 * 1024);
    }

    var blocked = 0;
    var mutated = 0;
    var validationFailed = false;
    using (var lease = new VerificationFileLease())
    {
        lease.Acquire(manifestPath, captureSample: true);
        lease.Acquire(artifactPath, captureSample: false);
        lease.Acquire(trustPath, captureSample: true);
        using var hashingStarted = new ManualResetEventSlim();
        var laterHash = Task.Run(() =>
        {
            hashingStarted.Set();
            lease.Acquire(largePath, captureSample: false);
        });
        hashingStarted.Wait(TimeSpan.FromSeconds(5));
        foreach (var path in new[] { manifestPath, artifactPath, trustPath })
        {
            if (TryMutateLeasedFile(path))
            {
                mutated += 1;
            }
            else
            {
                blocked += 1;
            }
        }
        laterHash.GetAwaiter().GetResult();
        try
        {
            lease.ValidateAllPathsAndHandles();
        }
        catch (EvidenceValidationException exception) when (
            exception.Code is "ACCEPTANCE_FILE_CHANGED_DURING_VERIFICATION" or
                "ACCEPTANCE_FILE_LEASE_PATH_CHANGED")
        {
            validationFailed = true;
        }
    }
    Directory.Delete(root, recursive: true);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        blocked,
        mutated,
        validationFailed,
        result = blocked == 3 || mutated > 0 && validationFailed
            ? "pass"
            : "fail"
    }));
    return blocked == 3 || mutated > 0 && validationFailed ? 0 : 2;
}

static bool TryMutateLeasedFile(string path)
{
    try
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Position = 0;
        stream.WriteByte((byte)'X');
        stream.Flush(flushToDisk: true);
        return true;
    }
    catch (IOException)
    {
        return false;
    }
    catch (UnauthorizedAccessException)
    {
        return false;
    }
}
