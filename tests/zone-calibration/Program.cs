using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PalControl.ControlApi.Infrastructure;
using PalControl.ZoneCalibration;
using static TestHelpers;

var root = Path.Combine(Path.GetTempPath(), "pal-zone-calibration-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    ZoneGeometryLimits.Validate(0, 0, ZoneGeometryLimits.MaximumRadius);
    Assert(
        ExtractionZoneGeometry.IsInside(10_000, 0, 0, 0, 10_000) &&
        !ExtractionZoneGeometry.IsInside(10_000.001, 0, 0, 0, 10_000) &&
        !ExtractionZoneGeometry.IsInside(0, 0, 0, 0, 10_000.001),
        "shared map/settlement geometry boundary or radius limit drifted");
    using var fixture = Fixture.Create(Path.Combine(root, "base"));
    fixture.WriteUnsignedEvidence();
    var challenge = ZoneCalibrationVerifier.PrepareReview(
        fixture.EvidencePath,
        fixture.TrustStorePath,
        fixture.TrustStoreSha256,
        fixture.Expected,
        fixture.Now);
    fixture.CompleteReview(challenge);

    var report = Path.Combine(fixture.Root, "canonical.json");
    var reportHash = Path.Combine(fixture.Root, "canonical.sha256");
    var result = ZoneCalibrationVerifier.VerifyAndWriteReport(
        fixture.EvidencePath,
        fixture.TrustStorePath,
        fixture.TrustStoreSha256,
        fixture.Expected,
        report,
        reportHash,
        fixture.Now);
    Assert(result.Valid && result.BoundaryDirectionCount == 8 && result.ArtifactCount == 31,
        "positive verification summary is incomplete");
    var reportResult = ZoneCalibrationVerifier.VerifyCanonicalReport(
        fixture.EvidencePath,
        report,
        reportHash,
        fixture.TrustStorePath,
        fixture.TrustStoreSha256,
        fixture.Expected,
        fixture.Now);
    Assert(reportResult.Valid, "canonical report verification failed");

    var cliReport = Path.Combine(fixture.Root, "cli-canonical.json");
    var cliReportHash = Path.Combine(fixture.Root, "cli-canonical.sha256");
    var cliVerify = RunCli("verify", includePin: true, cliReport, cliReportHash);
    Assert(cliVerify.ExitCode == 0, "formal CLI verify failed: " + cliVerify.StandardError);
    var cliReportVerify = RunCli("verify-report", includePin: true, cliReport, cliReportHash);
    Assert(cliReportVerify.ExitCode == 0, "formal CLI verify-report failed: " + cliReportVerify.StandardError);
    var missingPin = RunCli("prepare-review", includePin: false, null, null);
    Assert(missingPin.ExitCode == 2 && missingPin.StandardError.Contains(
        "--trust-store-sha256 or PAL_CONTROL_ZONE_TRUST_STORE_SHA256 is required",
        StringComparison.Ordinal), "formal CLI did not fail closed without an external trust-store pin");

    var negativeCount = 0;
    RunFailure("trust-pin", "ZONE_TRUST_STORE_PIN_MISMATCH", copy =>
        Verify(copy, fixture.Expected, "sha256:" + HashRaw("wrong-trust-store-pin")));
    RunFailure("expected-server", "ZONE_EXPECTED_BINDING_MISMATCH", copy =>
        Verify(copy, fixture.Expected with { ServerId = "weekly-zone-03" }));
    RunFailure("expected-content", "ZONE_EXPECTED_BINDING_MISMATCH", copy =>
        Verify(copy, fixture.Expected with { ContentHash = HashRaw("different-controlled-content") }));
    RunFailure("expired", "ZONE_EVIDENCE_EXPIRED", copy =>
    {
        var evidence = ReadEvidence(copy);
        WriteEvidence(copy, evidence with { ExpiresAt = fixture.Now.AddMinutes(-1) });
        Verify(copy, fixture.Expected);
    });
    RunFailure("radius-limit", "ZONE_RADIUS_INVALID", copy =>
    {
        var evidence = ReadEvidence(copy);
        WriteEvidence(copy, evidence with { Zone = evidence.Zone with { Radius = 10_001 } });
        Verify(copy, fixture.Expected);
    });
    RunFailure("same-subject", "ZONE_REVIEW_NOT_INDEPENDENT", copy =>
    {
        var evidence = ReadEvidence(copy);
        WriteEvidence(copy, evidence with
        {
            Participants = evidence.Participants with
            {
                Reviewer = evidence.Participants.Reviewer with
                {
                    SubjectId = evidence.Participants.Executor.SubjectId
                }
            }
        });
        Verify(copy, fixture.Expected);
    });
    RunFailure("subject-domain", "ZONE_SUBJECT_DOMAIN_MISMATCH", copy =>
    {
        var evidence = ReadEvidence(copy);
        WriteEvidence(copy, evidence with
        {
            Participants = evidence.Participants with
            {
                Executor = evidence.Participants.Executor with
                {
                    PseudonymDomain = "zone-calibration:other-campaign"
                }
            }
        });
        Verify(copy, fixture.Expected);
    });
    RunFailure("artifact-hash", "ZONE_ARTIFACT_HASH_MISMATCH", copy =>
    {
        var evidence = ReadEvidence(copy);
        var items = evidence.Artifacts.ToArray();
        items[0] = items[0] with { Sha256 = "sha256:" + HashRaw("tampered-artifact") };
        WriteEvidence(copy, evidence with { Artifacts = items });
        Verify(copy, fixture.Expected);
    });
    RunFailure("review-producer-binding", "ZONE_REVIEW_BINDING_MISMATCH", copy =>
    {
        var evidence = ReadEvidence(copy);
        var artifacts = evidence.Artifacts.ToArray();
        var index = Array.FindIndex(artifacts, value => value.Id == "server-build");
        artifacts[index] = artifacts[index] with { Producer = "palworld-live-exporter.2" };
        WriteEvidence(copy, evidence with { Artifacts = artifacts });
        Verify(copy, fixture.Expected);
    });
    RunFailure("review-path-binding", "ZONE_REVIEW_BINDING_MISMATCH", copy =>
    {
        var evidence = ReadEvidence(copy);
        var artifacts = evidence.Artifacts.ToArray();
        var index = Array.FindIndex(artifacts, value => value.Id == "server-build");
        var originalPath = ResolveArtifact(copy, artifacts[index]);
        var relocatedRelativePath = "captures/server-build-relocated.json";
        var relocatedPath = Path.Combine(copy, relocatedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Copy(originalPath, relocatedPath);
        artifacts[index] = artifacts[index] with { Path = relocatedRelativePath };
        WriteEvidence(copy, evidence with { Artifacts = artifacts });
        Verify(copy, fixture.Expected);
    });
    RunFailure("capture-signature", "ZONE_CAPTURE_SIGNATURE_INVALID", copy =>
    {
        MutateEnvelope(copy, "server-build", (envelope, _) =>
        {
            var bytes = Convert.FromBase64String(envelope.Attestation.SignatureBase64);
            bytes[0] ^= 0x40;
            return envelope with
            {
                Attestation = envelope.Attestation with { SignatureBase64 = Convert.ToBase64String(bytes) }
            };
        });
        Verify(copy, fixture.Expected);
    });
    RunFailure("capture-key", "ZONE_CAPTURE_KEY_UNTRUSTED", copy =>
    {
        MutateEnvelope(copy, "server-build", (envelope, _) => envelope with
        {
            Attestation = envelope.Attestation with { KeyId = "absent-capture-key" }
        });
        Verify(copy, fixture.Expected);
    });
    RunFailure("nonce-reuse", "ZONE_CAPTURE_NONCE_INVALID", copy =>
    {
        var first = ReadEnvelope(copy, "server-build");
        MutateAndResign<RawContentZoneCapture>(
            copy,
            "content-zone",
            body => body,
            first.Attestation.Nonce);
        Verify(copy, fixture.Expected);
    });
    RunFailure("review-hash", "ZONE_REVIEW_BINDING_MISMATCH", copy =>
    {
        var evidence = ReadEvidence(copy);
        WriteEvidence(copy, evidence with
        {
            Review = evidence.Review with
            {
                EvidencePayloadSha256 = "sha256:" + HashRaw("wrong-review-payload")
            }
        });
        Verify(copy, fixture.Expected);
    });
    RunFailure("review-signature", "ZONE_REVIEW_SIGNATURE_INVALID", copy =>
    {
        var evidence = ReadEvidence(copy);
        var bytes = Convert.FromBase64String(evidence.Review.SignatureBase64);
        bytes[^1] ^= 0x20;
        WriteEvidence(copy, evidence with
        {
            Review = evidence.Review with { SignatureBase64 = Convert.ToBase64String(bytes) }
        });
        Verify(copy, fixture.Expected);
    });
    RunFailure("trust-key-reuse", "ZONE_TRUST_KEY_REUSED", copy =>
    {
        var store = ReadJson<ZoneCalibrationTrustStore>(Path.Combine(copy, "trust-store.json"));
        var reviewers = store.ReviewerKeys.ToArray();
        reviewers[0] = reviewers[0] with
        {
            PublicKeySpkiBase64 = store.CaptureKeys[0].PublicKeySpkiBase64
        };
        WriteJson(Path.Combine(copy, "trust-store.json"), store with { ReviewerKeys = reviewers });
        Verify(copy, fixture.Expected, ZoneCalibrationVerifier.ComputeFileSha256(Path.Combine(copy, "trust-store.json")));
    });
    RunFailure("route-transition", "ZONE_ROUTE_TRANSITION_INVALID", copy =>
    {
        MutateAndResign<RawRouteCapture>(copy, "route-ingress-a", body => body with
        {
            Trace =
            [
                body.Trace[0] with { X = fixture.CenterX + 92 },
                body.Trace[1],
                body.Trace[2]
            ]
        });
        Verify(copy, fixture.Expected);
    });
    RunFailure("outside-quote", "ZONE_OUTSIDE_QUOTE_NOT_REJECTED", copy =>
    {
        MutateAndResign<RawQuoteResponseCapture>(copy, "route-ingress-a-outside-response", body => body with
        {
            HttpStatus = 200,
            Result = "success",
            ErrorCode = null
        });
        Verify(copy, fixture.Expected);
    });
    RunFailure("risk-disposition", "ZONE_RISK_CHECK_FAILED", copy =>
    {
        var evidence = ReadEvidence(copy);
        MutateAndResign<RawRiskCapture>(copy, "risk-check", body => body with { Disposition = "denied" });
        WriteEvidence(copy, evidence with
        {
            Artifacts = ReadEvidence(copy).Artifacts,
            RiskCheck = evidence.RiskCheck with { Disposition = "denied" }
        });
        Verify(copy, fixture.Expected);
    });
    RunFailure("path-colon", "ZONE_ARTIFACT_PATH_INVALID", copy =>
    {
        var evidence = ReadEvidence(copy);
        var items = evidence.Artifacts.ToArray();
        items[0] = items[0] with { Path = "captures/server-build.json:stream" };
        WriteEvidence(copy, evidence with { Artifacts = items });
        Verify(copy, fixture.Expected);
    });
    RunFailure("ipv6-leak", "ZONE_IDENTITY_VALUE_FORBIDDEN", copy =>
    {
        var evidence = ReadEvidence(copy);
        var artifact = evidence.Artifacts.Single(value => value.Id == "server-build");
        var path = ResolveArtifact(copy, artifact);
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["body"]!.AsObject()["networkNote"] = "peer=[2001:db8::7]";
        File.WriteAllText(path, node.ToJsonString(ZoneCalibrationVerifier.SerializerOptions) + "\n", Utf8);
        RefreshArtifact(copy, "server-build");
        Verify(copy, fixture.Expected);
    });
    RunFailure("reserved-path", "ZONE_ARTIFACT_PATH_INVALID", copy =>
    {
        var evidence = ReadEvidence(copy);
        var items = evidence.Artifacts.ToArray();
        items[0] = items[0] with { Path = "captures/CON.json" };
        WriteEvidence(copy, evidence with { Artifacts = items });
        Verify(copy, fixture.Expected);
    });
    RunFailure("race-evidence", "ZONE_EVIDENCE_CHANGED_DURING_VERIFICATION", copy =>
    {
        RunWithSnapshotMutation("verify-before-write", () =>
            File.AppendAllText(Path.Combine(copy, "evidence.json"), " ", Utf8), () =>
            Verify(copy, fixture.Expected));
    });
    RunFailure("race-trust-store", "ZONE_TRUST_STORE_CHANGED_DURING_VERIFICATION", copy =>
    {
        RunWithSnapshotMutation("verify-before-write", () =>
            File.AppendAllText(Path.Combine(copy, "trust-store.json"), " ", Utf8), () =>
            Verify(copy, fixture.Expected));
    });
    RunFailure("race-artifact", "ZONE_ARTIFACT_CHANGED_DURING_VERIFICATION", copy =>
    {
        var evidence = ReadEvidence(copy);
        var artifactPath = ResolveArtifact(
            copy,
            evidence.Artifacts.Single(value => value.Id == "server-build"));
        RunWithSnapshotMutation("prepare-review", () =>
            File.AppendAllText(artifactPath, " ", Utf8), () =>
            ZoneCalibrationVerifier.PrepareReview(
                Path.Combine(copy, "evidence.json"),
                Path.Combine(copy, "trust-store.json"),
                fixture.TrustStoreSha256,
                fixture.Expected,
                fixture.Now));
    });
    RunFailure("race-written-report", "ZONE_REPORT_CHANGED_DURING_VERIFICATION", copy =>
    {
        RunWithSnapshotMutation("verify-after-write", () =>
            File.AppendAllText(Path.Combine(copy, "failure-report.json"), " ", Utf8), () =>
            Verify(copy, fixture.Expected));
    });
    RunFailure("race-written-sidecar", "ZONE_REPORT_HASH_CHANGED_DURING_VERIFICATION", copy =>
    {
        RunWithSnapshotMutation("verify-after-write", () =>
            File.AppendAllText(Path.Combine(copy, "failure-report.sha256"), " ", Utf8), () =>
            Verify(copy, fixture.Expected));
    });

    var reportTamper = Path.Combine(root, "report-tamper.json");
    var reportTamperHash = Path.Combine(root, "report-tamper.sha256");
    var reportDocument = ReadJson<CanonicalZoneCalibrationReport>(report);
    WriteJson(reportTamper, reportDocument with { RiskDisposition = "denied" });
    File.WriteAllText(reportTamperHash, ZoneCalibrationVerifier.ComputeFileSha256(reportTamper) + "\n", Utf8);
    ExpectFailure("ZONE_REPORT_ASSERTION_INVALID", () => ZoneCalibrationVerifier.VerifyCanonicalReport(
        fixture.EvidencePath,
        reportTamper, reportTamperHash, fixture.TrustStorePath, fixture.TrustStoreSha256, fixture.Expected, fixture.Now));
    negativeCount++;

    var reportSourceTamper = Path.Combine(root, "report-source-tamper.json");
    var reportSourceTamperHash = Path.Combine(root, "report-source-tamper.sha256");
    WriteJson(reportSourceTamper, reportDocument with { CenterDistance = reportDocument.CenterDistance + 1 });
    File.WriteAllText(
        reportSourceTamperHash,
        ZoneCalibrationVerifier.ComputeFileSha256(reportSourceTamper) + "\n",
        Utf8);
    ExpectFailure("ZONE_REPORT_SOURCE_MISMATCH", () => ZoneCalibrationVerifier.VerifyCanonicalReport(
        fixture.EvidencePath,
        reportSourceTamper,
        reportSourceTamperHash,
        fixture.TrustStorePath,
        fixture.TrustStoreSha256,
        fixture.Expected,
        fixture.Now));
    negativeCount++;

    var badSidecar = Path.Combine(root, "bad-sidecar.sha256");
    File.WriteAllText(badSidecar, "sha256:" + HashRaw("wrong-report-sidecar") + "\n", Utf8);
    ExpectFailure("ZONE_REPORT_HASH_MISMATCH", () => ZoneCalibrationVerifier.VerifyCanonicalReport(
        fixture.EvidencePath,
        report, badSidecar, fixture.TrustStorePath, fixture.TrustStoreSha256, fixture.Expected, fixture.Now));
    negativeCount++;

    ExpectFailure("ZONE_REPORT_EXPECTED_BINDING_MISMATCH", () => ZoneCalibrationVerifier.VerifyCanonicalReport(
        fixture.EvidencePath,
        report,
        reportHash,
        fixture.TrustStorePath,
        fixture.TrustStoreSha256,
        fixture.Expected with { ZoneId = "exchange-zone-03" },
        fixture.Now));
    negativeCount++;

    Assert(negativeCount >= 31, $"expected at least 31 negative cases, observed {negativeCount}");
    Console.WriteLine(
        $"PASS: signed live zone calibration, strict canonical report, 31 artifacts and {negativeCount} fail-closed mutations.");

    void RunFailure(string name, string code, Action<string> action)
    {
        var copy = Path.Combine(root, "case-" + name);
        CopyDirectory(fixture.Root, copy);
        ExpectFailure(code, () => action(copy));
        negativeCount++;
    }

    void Verify(string copy, ZoneCalibrationExpectedBinding expected, string? pin = null)
    {
        ZoneCalibrationVerifier.VerifyAndWriteReport(
            Path.Combine(copy, "evidence.json"),
            Path.Combine(copy, "trust-store.json"),
            pin ?? fixture.TrustStoreSha256,
            expected,
            Path.Combine(copy, "failure-report.json"),
            Path.Combine(copy, "failure-report.sha256"),
            fixture.Now);
    }

    void RunWithSnapshotMutation(string stage, Action mutation, Action operation)
    {
        var mutated = false;
        ZoneCalibrationVerifier.TestBeforeFinalSnapshotCheck = observedStage =>
        {
            if (!mutated && observedStage == stage)
            {
                mutated = true;
                mutation();
            }
        };
        try
        {
            operation();
        }
        finally
        {
            ZoneCalibrationVerifier.TestBeforeFinalSnapshotCheck = null;
        }
        Assert(mutated, $"Snapshot race hook '{stage}' was not reached.");
    }

    (int ExitCode, string StandardOutput, string StandardError) RunCli(
        string command,
        bool includePin,
        string? reportPath,
        string? reportHashPath)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment.Remove("PAL_CONTROL_ZONE_TRUST_STORE_SHA256");
        start.ArgumentList.Add(typeof(ZoneCalibrationException).Assembly.Location);
        start.ArgumentList.Add(command);
        if (command is "verify" or "prepare-review" or "verify-report")
        {
            Add("evidence", fixture.EvidencePath);
        }
        if (command is "verify" or "verify-report")
        {
            Add("report", reportPath!);
            Add("report-hash", reportHashPath!);
        }
        Add("trust-store", fixture.TrustStorePath);
        if (includePin)
        {
            Add("trust-store-sha256", fixture.TrustStoreSha256);
        }
        Add("expected-server-id", fixture.Expected.ServerId);
        Add("expected-game-build", fixture.Expected.GameBuild);
        Add("expected-steam-build", fixture.Expected.SteamBuild);
        Add("expected-content-version-id", fixture.Expected.ContentVersionId.ToString("D"));
        Add("expected-content-version-number", fixture.Expected.ContentVersionNumber.ToString(CultureInfo.InvariantCulture));
        Add("expected-content-hash", fixture.Expected.ContentHash);
        Add("expected-zone-id", fixture.Expected.ZoneId);
        Add("max-evidence-age-seconds", ((long)fixture.Expected.MaximumEvidenceAge.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch zone-calibration CLI.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);

        void Add(string name, string value)
        {
            start.ArgumentList.Add("--" + name);
            start.ArgumentList.Add(value);
        }
    }

    ZoneCalibrationEvidence ReadEvidence(string directory) =>
        ReadJson<ZoneCalibrationEvidence>(Path.Combine(directory, "evidence.json"));

    void WriteEvidence(string directory, ZoneCalibrationEvidence evidence) =>
        WriteJson(Path.Combine(directory, "evidence.json"), evidence);

    SignedCaptureEnvelope ReadEnvelope(string directory, string artifactId)
    {
        var evidence = ReadEvidence(directory);
        var artifact = evidence.Artifacts.Single(value => value.Id == artifactId);
        return ReadJson<SignedCaptureEnvelope>(ResolveArtifact(directory, artifact));
    }

    void MutateEnvelope(
        string directory,
        string artifactId,
        Func<SignedCaptureEnvelope, ZoneCalibrationEvidence, SignedCaptureEnvelope> mutation)
    {
        var evidence = ReadEvidence(directory);
        var artifact = evidence.Artifacts.Single(value => value.Id == artifactId);
        var path = ResolveArtifact(directory, artifact);
        var envelope = ReadJson<SignedCaptureEnvelope>(path);
        WriteJson(path, mutation(envelope, evidence));
        RefreshArtifact(directory, artifactId);
    }

    void MutateAndResign<T>(
        string directory,
        string artifactId,
        Func<T, T> mutation,
        string? nonce = null)
    {
        var evidence = ReadEvidence(directory);
        var artifact = evidence.Artifacts.Single(value => value.Id == artifactId);
        var path = ResolveArtifact(directory, artifact);
        var envelope = ReadJson<SignedCaptureEnvelope>(path);
        var body = envelope.Body.Deserialize<T>(ZoneCalibrationVerifier.SerializerOptions)
            ?? throw new InvalidOperationException("artifact body missing");
        fixture.WriteSignedArtifact(path, artifact, evidence, mutation(body), nonce);
        RefreshArtifact(directory, artifactId);
    }

    void RefreshArtifact(string directory, string artifactId)
    {
        var evidence = ReadEvidence(directory);
        var artifacts = evidence.Artifacts.ToArray();
        var index = Array.FindIndex(artifacts, value => value.Id == artifactId);
        var path = ResolveArtifact(directory, artifacts[index]);
        artifacts[index] = artifacts[index] with
        {
            SizeBytes = new FileInfo(path).Length,
            Sha256 = ZoneCalibrationVerifier.ComputeFileSha256(path)
        };
        WriteEvidence(directory, evidence with { Artifacts = artifacts });
    }
}
finally
{
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

internal static class TestHelpers
{
    public static readonly UTF8Encoding Utf8 = new(false);

    public static string ResolveArtifact(string directory, ArtifactRecord artifact) =>
        Path.Combine(directory, artifact.Path.Replace('/', Path.DirectorySeparatorChar));

    public static T ReadJson<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), ZoneCalibrationVerifier.SerializerOptions)
        ?? throw new InvalidOperationException($"JSON document is empty: {path}");

    public static void WriteJson<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ZoneCalibrationVerifier.SerializerOptions);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.WriteByte((byte)'\n');
    }

    public static string HashRaw(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string HashBytes(byte[] value) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(value));

    public static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void ExpectFailure(string expectedCode, Action action)
    {
        try
        {
            action();
        }
        catch (ZoneCalibrationException exception) when (exception.Code == expectedCode)
        {
            return;
        }
        catch (ZoneCalibrationException exception)
        {
            throw new InvalidOperationException(
                $"Expected {expectedCode}, observed {exception.Code}: {exception.Message}",
                exception);
        }
        throw new InvalidOperationException($"Expected failure {expectedCode}, but verification succeeded.");
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

}

internal sealed class Fixture : IDisposable
{
    private readonly ECDsa _captureKey;
    private readonly ECDsa _reviewerKey;
    private readonly List<ArtifactRecord> _artifacts = [];
    private ZoneCalibrationEvidence _evidence;

    private Fixture(string root, DateTimeOffset now, ECDsa captureKey, ECDsa reviewerKey)
    {
        Root = root;
        Now = now;
        _captureKey = captureKey;
        _reviewerKey = reviewerKey;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "captures"));
        var schemaHash = ZoneCalibrationVerifier.GetEmbeddedSchemaHashes()["evidenceSchema"];
        var started = now.AddMinutes(-35);
        var ended = started.AddMinutes(27);
        var reviewed = ended.AddMinutes(2);
        var domain = "zone-calibration:zc-weekly-zone-02-20260717";
        var executor = "subj:hmac-sha256:" + HashRaw("opaque-zone-operator-one");
        var reviewer = "subj:hmac-sha256:" + HashRaw("opaque-zone-reviewer-two");
        Expected = new ZoneCalibrationExpectedBinding(
            "weekly-zone-02",
            "0.6.4.75366",
            "18123456",
            Guid.Parse("2a55ed52-1835-4ec7-85dd-6c903c9e586c"),
            12,
            HashRaw("published-content-zone-02-v12"),
            "exchange-zone-02",
            TimeSpan.FromDays(1));
        _evidence = new ZoneCalibrationEvidence(
            ZoneCalibrationConstants.EvidenceSchemaId,
            ZoneCalibrationConstants.EvidenceSchemaVersion,
            schemaHash,
            "live",
            "zc-weekly-zone-02-20260717",
            ended.AddHours(2),
            new ServerBinding(Expected.ServerId, Expected.GameBuild, Expected.SteamBuild, "server-build"),
            new ContentBinding(Expected.ContentVersionId, Expected.ContentVersionNumber, Expected.ContentHash, "content-zone"),
            new ZoneAuthority(
                Expected.ZoneId,
                "published-content-version",
                new MapPoint(CenterX, CenterY),
                Radius,
                5,
                5,
                5),
            new SamplingWindow(started, ended, ZoneCalibrationConstants.AuthoritativePositionSource),
            new ParticipantPair(
                new EvidenceSubject(executor, domain, "operations-sso", "executor"),
                new EvidenceSubject(reviewer, domain, "operations-sso", "reviewer")),
            null!,
            [],
            [],
            null!,
            null!,
            new ReviewRecord(
                reviewed,
                "pass",
                "review-key-2026a",
                "sha256:" + HashRaw("pending-review-evidence"),
                "sha256:" + HashRaw("pending-review-artifacts"),
                ZoneCalibrationConstants.SignatureAlgorithm,
                Convert.ToBase64String(new byte[64])),
            []);
        BuildTrustStore(executor, reviewer, domain);
        BuildArtifacts();
    }

    public string Root { get; }
    public string EvidencePath => Path.Combine(Root, "evidence.json");
    public string TrustStorePath => Path.Combine(Root, "trust-store.json");
    public string TrustStoreSha256 { get; private set; } = string.Empty;
    public DateTimeOffset Now { get; }
    public ZoneCalibrationExpectedBinding Expected { get; }
    public double CenterX => 348;
    public double CenterY => -504;
    public double Radius => 100;

    public static Fixture Create(string root) =>
        new(root, DateTimeOffset.UtcNow, ECDsa.Create(ECCurve.NamedCurves.nistP256), ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public void WriteUnsignedEvidence() => WriteJson(EvidencePath, _evidence);

    public void CompleteReview(ReviewChallenge challenge)
    {
        var signature = _reviewerKey.SignData(
            Convert.FromBase64String(challenge.StatementBase64),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        _evidence = _evidence with
        {
            Review = _evidence.Review with
            {
                EvidencePayloadSha256 = challenge.EvidencePayloadSha256,
                ArtifactManifestSha256 = challenge.ArtifactManifestSha256,
                SignatureBase64 = Convert.ToBase64String(signature)
            }
        };
        WriteJson(EvidencePath, _evidence);
    }

    public void WriteSignedArtifact<T>(
        string path,
        ArtifactRecord artifact,
        ZoneCalibrationEvidence evidence,
        T body,
        string? nonce = null)
    {
        var serializedBody = JsonSerializer.SerializeToUtf8Bytes(body, ZoneCalibrationVerifier.SerializerOptions);
        using var document = JsonDocument.Parse(serializedBody);
        var bodyElement = document.RootElement.Clone();
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(bodyElement, ZoneCalibrationVerifier.SerializerOptions);
        var bodySha = HashBytes(bodyBytes);
        nonce ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var statement = BuildCaptureStatement(evidence, artifact, nonce, bodySha);
        var signature = _captureKey.SignData(
            statement,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var envelope = new SignedCaptureEnvelope(
            bodyElement,
            new CaptureAttestation(
                ZoneCalibrationConstants.SignatureAlgorithm,
                "capture-key-2026a",
                artifact.CapturedAt,
                nonce,
                bodySha,
                Convert.ToBase64String(signature)));
        var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ZoneCalibrationVerifier.SerializerOptions);
        using var verificationDocument = JsonDocument.Parse(envelopeBytes);
        var embeddedBodyBytes = Encoding.UTF8.GetBytes(
            verificationDocument.RootElement.GetProperty("body").GetRawText());
        if (HashBytes(embeddedBodyBytes) != bodySha)
        {
            throw new InvalidOperationException(
                $"Test signer body serialization drifted: {Encoding.UTF8.GetString(bodyBytes)} != {Encoding.UTF8.GetString(embeddedBodyBytes)}");
        }
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(envelopeBytes);
        stream.WriteByte((byte)'\n');
    }

    private void BuildTrustStore(string executor, string reviewer, string domain)
    {
        var validFrom = Now.AddDays(-1);
        var expires = Now.AddDays(30);
        var store = new ZoneCalibrationTrustStore(
            ZoneCalibrationConstants.TrustStoreSchemaId,
            ZoneCalibrationConstants.TrustStoreSchemaVersion,
            [new TrustedEvidenceKey(
                "capture-key-2026a",
                executor,
                domain,
                ZoneCalibrationConstants.SignatureAlgorithm,
                Convert.ToBase64String(_captureKey.ExportSubjectPublicKeyInfo()),
                validFrom,
                expires,
                false)],
            [new TrustedEvidenceKey(
                "review-key-2026a",
                reviewer,
                domain,
                ZoneCalibrationConstants.SignatureAlgorithm,
                Convert.ToBase64String(_reviewerKey.ExportSubjectPublicKeyInfo()),
                validFrom,
                expires,
                false)]);
        WriteJson(TrustStorePath, store);
        TrustStoreSha256 = ZoneCalibrationVerifier.ComputeFileSha256(TrustStorePath);
    }

    private void BuildArtifacts()
    {
        var started = _evidence.Sampling.StartedAt;
        var serverAt = started.AddMinutes(1);
        Add("server-build", "server-build-binding", serverAt, new RawServerBuildCapture(
            "pal-control-live-server-build-v1", Expected.ServerId, Expected.GameBuild, Expected.SteamBuild,
            serverAt, ZoneCalibrationConstants.AuthoritativeServerBuildSource));
        var contentAt = started.AddMinutes(2);
        Add("content-zone", "content-zone-binding", contentAt, new RawContentZoneCapture(
            "pal-control-live-content-zone-v1", Expected.ServerId, Expected.ContentVersionId,
            Expected.ContentVersionNumber, Expected.ContentHash, Expected.ZoneId,
            new MapPoint(CenterX, CenterY), Radius, contentAt, ZoneCalibrationConstants.AuthoritativeContentSource));
        var centerAt = started.AddMinutes(3);
        AddPosition("center-position", centerAt, CenterX, CenterY);
        var centerMeasurement = new PositionMeasurement(centerAt, CenterX, CenterY, "center-position");
        var pairs = new List<BoundaryMeasurementPair>();
        for (var index = 0; index < 8; index++)
        {
            var degrees = index * 45d;
            var radians = degrees * Math.PI / 180d;
            var insideX = Math.Round(CenterX + Math.Cos(radians) * 92, 6);
            var insideY = Math.Round(CenterY + Math.Sin(radians) * 92, 6);
            var outsideX = Math.Round(CenterX + Math.Cos(radians) * 108, 6);
            var outsideY = Math.Round(CenterY + Math.Sin(radians) * 108, 6);
            var suffix = ((int)degrees).ToString("D3", CultureInfo.InvariantCulture);
            var insideId = $"direction-{suffix}-inside";
            var outsideId = $"direction-{suffix}-outside";
            var insideAt = started.AddMinutes(4 + index * 2);
            var outsideAt = insideAt.AddSeconds(30);
            AddPosition(insideId, insideAt, insideX, insideY);
            AddPosition(outsideId, outsideAt, outsideX, outsideY);
            pairs.Add(new BoundaryMeasurementPair(
                degrees,
                new PositionMeasurement(insideAt, insideX, insideY, insideId),
                new PositionMeasurement(outsideAt, outsideX, outsideY, outsideId)));
        }
        var routes = new List<RouteCheck>();
        routes.Add(BuildRoute("route-ingress-a", "ingress", started.AddMinutes(20), started.AddMinutes(22)));
        routes.Add(BuildRoute("route-egress-a", "egress", started.AddMinutes(22), started.AddMinutes(24)));
        var accessAt = started.AddMinutes(25);
        Add("accessibility-check", "accessibility-capture", accessAt, new RawAccessibilityCapture(
            "pal-control-live-accessibility-v1", Expected.ServerId, Expected.ZoneId, accessAt,
            true, true, true, ZoneCalibrationConstants.AuthoritativeObservationSource));
        var riskAt = started.AddMinutes(26);
        Add("risk-check", "risk-capture", riskAt, new RawRiskCapture(
            "pal-control-live-risk-v1", Expected.ServerId, Expected.ZoneId, riskAt,
            "medium", "approved", true, true, true, ZoneCalibrationConstants.AuthoritativeObservationSource));
        _evidence = _evidence with
        {
            CenterMeasurement = centerMeasurement,
            BoundaryPairs = pairs,
            RouteChecks = routes,
            AccessibilityCheck = new AccessibilityCheck(accessAt, true, true, true, "accessibility-check"),
            RiskCheck = new RiskCheck(riskAt, "medium", "approved", true, true, true, "risk-check"),
            Artifacts = _artifacts
        };
    }

    private RouteCheck BuildRoute(string id, string kind, DateTimeOffset started, DateTimeOffset ended)
    {
        var outside = new RouteTracePoint(started, CenterX + 108, CenterY);
        var middle = new RouteTracePoint(started.AddMinutes(1), CenterX + 100, CenterY);
        var inside = new RouteTracePoint(ended, CenterX + 92, CenterY);
        IReadOnlyList<RouteTracePoint> trace = kind == "ingress"
            ? [outside, middle, inside]
            : [inside with { CapturedAt = started }, middle, outside with { CapturedAt = ended }];
        Add(id, "route-capture", ended, new RawRouteCapture(
            "pal-control-live-route-v1", Expected.ServerId, Expected.ZoneId, id, kind,
            started, ended, true, true, trace, ZoneCalibrationConstants.AuthoritativeObservationSource));
        var insideAnchor = kind == "ingress" ? trace[^1] : trace[0];
        var outsideAnchor = kind == "ingress" ? trace[0] : trace[^1];
        var insideBinding = AddQuote(id + "-inside", insideAnchor, true);
        var outsideBinding = AddQuote(id + "-outside", outsideAnchor, false);
        return new RouteCheck(id, kind, started, ended, true, true, id, insideBinding, outsideBinding);
    }

    private QuoteAttemptBinding AddQuote(string attemptId, RouteTracePoint point, bool inside)
    {
        var requestId = attemptId + "-request";
        var responseId = attemptId + "-response";
        Add(requestId, "quote-request-capture", point.CapturedAt, new RawQuoteRequestCapture(
            "pal-control-live-quote-request-v1", Expected.ServerId, Expected.ZoneId, attemptId,
            point.CapturedAt, new MapPoint(point.X, point.Y), ZoneCalibrationConstants.AuthoritativeQuoteSource));
        Add(responseId, "quote-response-capture", point.CapturedAt, new RawQuoteResponseCapture(
            "pal-control-live-quote-response-v1", Expected.ServerId, Expected.ZoneId, attemptId,
            point.CapturedAt, inside ? 200 : 409, inside ? "success" : "rejected",
            inside ? null : "PLAYER_OUTSIDE_EXTRACTION_ZONE", ZoneCalibrationConstants.AuthoritativeQuoteSource));
        return new QuoteAttemptBinding(requestId, responseId);
    }

    private void AddPosition(string id, DateTimeOffset capturedAt, double x, double y) =>
        Add(id, "position-capture", capturedAt, new RawPositionCapture(
            "pal-control-live-position-v1", Expected.ServerId, Expected.ZoneId, capturedAt,
            x, y, ZoneCalibrationConstants.AuthoritativePositionSource));

    private void Add<T>(string id, string role, DateTimeOffset capturedAt, T body)
    {
        var relative = "captures/" + id + ".json";
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        var artifact = new ArtifactRecord(
            id, role, relative, "application/json", "palworld-live-exporter.1", "live",
            capturedAt, 1, "sha256:" + HashRaw("pending-" + id));
        WriteSignedArtifact(path, artifact, _evidence, body);
        _artifacts.Add(artifact with
        {
            SizeBytes = new FileInfo(path).Length,
            Sha256 = ZoneCalibrationVerifier.ComputeFileSha256(path)
        });
    }

    private static byte[] BuildCaptureStatement(
        ZoneCalibrationEvidence evidence,
        ArtifactRecord artifact,
        string nonce,
        string bodySha)
    {
        var lines = new[]
        {
            ZoneCalibrationConstants.CaptureSignatureContext,
            evidence.Schema,
            evidence.SchemaVersion,
            evidence.SchemaSha256,
            evidence.CalibrationId,
            evidence.Server.ServerId,
            evidence.Server.GameBuild,
            evidence.Server.SteamBuild,
            evidence.Content.VersionId.ToString("D", CultureInfo.InvariantCulture),
            evidence.Content.VersionNumber.ToString(CultureInfo.InvariantCulture),
            evidence.Content.ContentHash,
            evidence.Zone.ZoneId,
            artifact.Id,
            artifact.Role,
            artifact.CapturedAt.ToString("O", CultureInfo.InvariantCulture),
            nonce,
            bodySha
        };
        return Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }

    public void Dispose()
    {
        _captureKey.Dispose();
        _reviewerKey.Dispose();
    }
}
