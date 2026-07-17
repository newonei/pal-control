$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot `
    "tools\acceptance-evidence\PalControl.AcceptanceEvidence.csproj"
$policyHarnessProject = Join-Path $repositoryRoot `
    "tests\acceptance-evidence-policy\PalControl.AcceptanceEvidence.PolicyHarness.csproj"
$schema = Join-Path $repositoryRoot `
    "tools\acceptance-evidence\acceptance-evidence.schema.v1.json"
$catalog = Join-Path $repositoryRoot `
    "tools\acceptance-evidence\gate-catalog.v1.json"
$example = Join-Path $repositoryRoot `
    "tools\acceptance-evidence\examples\manifest.template.json"
$trustStoreExample = Join-Path $repositoryRoot `
    "tools\acceptance-evidence\examples\identity-trust-store.template.json"
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ("pal-control-acceptance-evidence-" + [Guid]::NewGuid().ToString("N"))

function Get-Sha256([string]$path) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($path)
        try { $digest = $hasher.ComputeHash($stream) }
        finally { $stream.Dispose() }
    }
    finally { $hasher.Dispose() }
    return "sha256:" + [BitConverter]::ToString($digest).Replace("-", "").ToLowerInvariant()
}

function Get-TextSha256([string]$text) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $digest = $hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)) }
    finally { $hasher.Dispose() }
    return [BitConverter]::ToString($digest).Replace("-", "").ToLowerInvariant()
}

function Get-CombinationId([object]$combination) {
    $canonical = [ordered]@{
        '$schema' = "https://github.com/newonei/pal-control/schemas/version-combination/v1"
        caddyVersion = [string]$combination.caddyVersion
        configurationSha256 = [string]$combination.configurationSha256
        controlApiCommit = [string]$combination.controlApiCommit
        deploymentPackageSha256 = [string]$combination.deploymentPackageSha256
        nativeBridgeVersion = [string]$combination.nativeBridgeVersion
        nativeCapability = [string]$combination.nativeCapability
        palDefenderVersion = [string]$combination.palDefenderVersion
        palworldVersion = [string]$combination.palworldVersion
        steamBuild = [string]$combination.steamBuild
        ue4ssVersion = [string]$combination.ue4ssVersion
    }
    return "combo-sha256:" + (Get-TextSha256 (
        $canonical | ConvertTo-Json -Compress))
}

function Get-Subject([string]$value) {
    $hmac = [Security.Cryptography.HMACSHA256]::new(
        [Text.Encoding]::UTF8.GetBytes("acceptance-evidence-smoke-only"))
    try { $digest = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($value)) }
    finally { $hmac.Dispose() }
    return "subj:hmac-sha256:" + `
        [BitConverter]::ToString($digest).Replace("-", "").ToLowerInvariant()
}

function Write-Utf8([string]$path, [string]$content) {
    $parent = Split-Path -Parent $path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    [IO.File]::WriteAllText($path, $content, [Text.UTF8Encoding]::new($false))
}

function Write-Json([string]$path, [object]$value) {
    Write-Utf8 $path (($value | ConvertTo-Json -Depth 100) + "`n")
}

function New-TrustIdentity(
    [string]$keyId,
    [string]$subjectSeed,
    [string]$role,
    [bool]$implementationContributor) {
    $privatePath = Join-Path $tempRoot ("private-" + $keyId + ".pk8")
    $keyResult = Invoke-EvidenceTool ("keygen-" + $keyId) `
        @("keygen", $privatePath) 0 "" $true
    $script:privateKeys[$keyId] = $privatePath
    return [ordered]@{
        keyId = $keyId
        algorithm = "ecdsa-p256-sha256-p1363"
        publicKeySpkiBase64 = $keyResult.Stdout.Trim()
        revoked = $false
        subject = [ordered]@{
            subjectId = Get-Subject $subjectSeed
            identityProvider = "entra-id"
            role = $role
            implementationContributor = $implementationContributor
        }
    }
}

function Invoke-EvidenceTool(
    [string]$name,
    [string[]]$arguments,
    [int]$expectedExit,
    [string]$expectedCode = "",
    [bool]$usePolicyHarness = $false) {
    $stdoutPath = Join-Path $tempRoot ($name + ".stdout")
    $stderrPath = Join-Path $tempRoot ($name + ".stderr")
    $previousErrorAction = $ErrorActionPreference
    $selectedProject = if ($usePolicyHarness) { $policyHarnessProject } else { $project }
    [string[]]$effectiveArguments = @()
    if ($usePolicyHarness) {
        $effectiveArguments = @($arguments)
    }
    else {
        for ($index = 0; $index -lt $arguments.Count; $index += 1) {
            if ($arguments[$index] -in @("--schema", "--catalog")) {
                $index += 1
                continue
            }
            $effectiveArguments += $arguments[$index]
        }
        if ($effectiveArguments.Count -gt 0 -and
            $effectiveArguments[0] -eq "verify" -and
            $effectiveArguments -notcontains "--trust-store") {
            $effectiveArguments += @(
                "--trust-store", $script:trustStorePath,
                "--trust-store-sha256", $script:trustStoreHash)
        }
    }
    if ($usePolicyHarness -and $effectiveArguments.Count -eq 3 -and
        $effectiveArguments[0] -notin @("keygen", "sign", "create-soak-fixture")) {
        $effectiveArguments += @($script:trustStorePath, $script:trustStoreHash)
    }
    try {
        $ErrorActionPreference = "Continue"
        & dotnet run --project $selectedProject --configuration Release --no-restore -- `
            @effectiveArguments 1> $stdoutPath 2> $stderrPath
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
    $stdout = if (Test-Path -LiteralPath $stdoutPath) { [IO.File]::ReadAllText($stdoutPath) } else { "" }
    $stderr = if (Test-Path -LiteralPath $stderrPath) { [IO.File]::ReadAllText($stderrPath) } else { "" }
    if ($exitCode -ne $expectedExit) {
        throw "$name expected exit $expectedExit, received $exitCode. stdout=$stdout stderr=$stderr"
    }
    if (-not [string]::IsNullOrWhiteSpace($expectedCode) -and
        $stderr -notmatch [regex]::Escape($expectedCode)) {
        throw "$name expected error code $expectedCode. stderr=$stderr"
    }
    return [pscustomobject]@{ Stdout = $stdout; Stderr = $stderr }
}

function Sign-Manifest(
    [string]$name,
    [string]$path,
    [object]$manifest,
    [string]$executorKeyId,
    [string]$reviewerKeyId) {
    $signatureRecord = [ordered]@{
        payloadSchema = "https://github.com/newonei/pal-control/schemas/acceptance-signature-payload/v1"
        trustStoreSha256 = $script:trustStoreHash
        executor = [ordered]@{
            keyId = $executorKeyId
            algorithm = "ecdsa-p256-sha256-p1363"
            signatureBase64 = "unsigned"
        }
        reviewer = [ordered]@{
            keyId = $reviewerKeyId
            algorithm = "ecdsa-p256-sha256-p1363"
            signatureBase64 = "unsigned"
        }
    }
    if ($manifest -is [Collections.IDictionary]) {
        $manifest["signatures"] = $signatureRecord
    }
    elseif ($null -ne $manifest.PSObject.Properties["signatures"]) {
        $manifest.signatures = $signatureRecord
    }
    else {
        $manifest | Add-Member -NotePropertyName signatures -NotePropertyValue $signatureRecord
    }
    Write-Json $path $manifest
    $payloadPath = Join-Path $tempRoot ("payload-" + $name + "-" + [Guid]::NewGuid().ToString("N") + ".json")
    Invoke-EvidenceTool ("payload-" + $name) @(
        "signature-payload",
        "--manifest", $path,
        "--trust-store-sha256", $script:trustStoreHash,
        "--executor-key", $executorKeyId,
        "--reviewer-key", $reviewerKeyId,
        "--output", $payloadPath) 0 | Out-Null
    $payload = [IO.File]::ReadAllBytes($payloadPath)
    $executorSignature = Invoke-EvidenceTool ("sign-executor-" + $name) `
        @("sign", $script:privateKeys[$executorKeyId], $payloadPath) 0 "" $true
    $reviewerSignature = Invoke-EvidenceTool ("sign-reviewer-" + $name) `
        @("sign", $script:privateKeys[$reviewerKeyId], $payloadPath) 0 "" $true
    $manifest.signatures.executor.signatureBase64 = $executorSignature.Stdout.Trim()
    $manifest.signatures.reviewer.signatureBase64 = $reviewerSignature.Stdout.Trim()
    Write-Json $path $manifest
    Remove-Item -LiteralPath $payloadPath -Force
}

function Copy-Manifest([object]$source) {
    return ($source | ConvertTo-Json -Depth 100 | ConvertFrom-Json)
}

function New-Artifact(
    [string]$id,
    [string]$role,
    [string]$relativePath,
    [string]$mediaType,
    [string]$capturedAt,
    [string]$producer) {
    $fullPath = Join-Path $bundleRoot ($relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar))
    return [ordered]@{
        id = $id
        path = $relativePath
        role = $role
        mediaType = $mediaType
        sha256 = Get-Sha256 $fullPath
        sizeBytes = ([IO.FileInfo]::new($fullPath)).Length
        capturedAt = $capturedAt
        captureMode = "live"
        producer = $producer
    }
}

try {
    [IO.Directory]::CreateDirectory($tempRoot) | Out-Null
    $script:privateKeys = @{}
    $catalogDocument = [IO.File]::ReadAllText($catalog) | ConvertFrom-Json
    $todoLines = [IO.File]::ReadAllLines((Join-Path $repositoryRoot "TODO.md"))
    $referencedTodoLines = [Collections.Generic.HashSet[int]]::new()
    foreach ($gate in $catalogDocument.gates) {
        $todoReferences = @($gate.todoReferences | Where-Object {
            [string]$_ -match '^TODO\.md:[1-9][0-9]*$'
        })
        if ($todoReferences.Count -ne 1) {
            throw "Gate '$($gate.id)' must have exactly one TODO.md:<line> reference."
        }
        $lineNumber = [int](([string]$todoReferences[0]).Substring("TODO.md:".Length))
        if ($lineNumber -gt $todoLines.Count -or
            -not $referencedTodoLines.Add($lineNumber)) {
            throw "Gate '$($gate.id)' has an invalid or duplicate TODO line reference."
        }
        $line = $todoLines[$lineNumber - 1]
        $marker = "<!-- gate:$($gate.id) -->"
        if ($line -notmatch '^- \[ \]' -or -not $line.Contains($marker)) {
            throw "Gate '$($gate.id)' does not reference its marked unchecked TODO item."
        }
    }
    $markedTodoCount = @($todoLines | Where-Object {
        $_ -match '<!-- gate:[a-z0-9-]+ -->'
    }).Count
    if ($markedTodoCount -ne $catalogDocument.gates.Count) {
        throw "TODO gate marker count does not match the acceptance catalog."
    }
    & dotnet restore $project --nologo
    if ($LASTEXITCODE -ne 0) { throw "Acceptance evidence tool restore failed." }
    & dotnet restore $policyHarnessProject --nologo
    if ($LASTEXITCODE -ne 0) { throw "Acceptance policy harness restore failed." }
    $leaseRaceRoot = Join-Path $tempRoot "lease-race"
    $leaseRace = Invoke-EvidenceTool "verification-lease-race" `
        @("lease-race", $leaseRaceRoot) 0 "" $true
    if (($leaseRace.Stdout | ConvertFrom-Json).result -ne "pass") {
        throw "Verification lease did not block mutation or detect the changed file."
    }
    $securityExecutorKeyId = "security-executor-2026w27"
    $databaseExecutorKeyId = "database-executor-2026w27"
    $sreExecutorKeyId = "sre-executor-2026w27"
    $reviewerKeyId = "independent-reviewer-2026w27"
    $trustKeys = @(
        (New-TrustIdentity $securityExecutorKeyId "security-operator-a" "security-operator" $true),
        (New-TrustIdentity $databaseExecutorKeyId "database-operator-a" "database-operator" $true),
        (New-TrustIdentity $sreExecutorKeyId "sre-operator-a" "sre-operator" $true),
        (New-TrustIdentity $reviewerKeyId "independent-reviewer-a" "reviewer" $false),
        (New-TrustIdentity "administrator-a-2026w27" "administrator-a" "administrator" $true),
        (New-TrustIdentity "administrator-b-2026w27" "administrator-b" "administrator" $false))
    $script:trustStorePath = Join-Path $tempRoot "identity-trust-store.json"
    Write-Json $script:trustStorePath ([ordered]@{
        '$schema' = "https://github.com/newonei/pal-control/schemas/acceptance-identity-trust-store/v1"
        schemaVersion = "1.0.0"
        storeId = "acceptance-smoke-2026w27"
        keys = $trustKeys
    })
    $script:trustStoreHash = Get-Sha256 $script:trustStorePath
    $bundleRoot = Join-Path $tempRoot "valid-admin"
    $evidenceRoot = Join-Path $bundleRoot "evidence"
    [IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null

    Write-Utf8 (Join-Path $evidenceRoot "rbac-session-trace.json") `
        '{"requests":18,"anonymousDenied":6,"playerCookieDenied":6,"lowPrivilegeDenied":6}'
    Write-Utf8 (Join-Path $evidenceRoot "mfa-revocation-trace.json") `
        '{"mfaChallenges":4,"revocations":2,"postRevocationDenied":2}'
    Write-Utf8 (Join-Path $evidenceRoot "admin-audit-export.ndjson") `
        "{`"subjectRef`":`"opaque-a`",`"result`":`"denied`"}`n{`"subjectRef`":`"opaque-b`",`"result`":`"approved`"}`n"
    Write-Utf8 (Join-Path $evidenceRoot "sensitive-scan-report.json") `
        '{"scanner":"gitleaks","version":"8.30.1","findings":[]}'
    Write-Utf8 (Join-Path $evidenceRoot "review-record.json") `
        '{"decision":"approved","scope":"RBAC, MFA, revocation and actor attribution reviewed"}'

    $schemaHash = Get-Sha256 $schema
    $catalogHash = Get-Sha256 $catalog
    $controlCommit = "ab" * 20
    $runbookCommit = "cd" * 20
    $deploymentHash = "sha256:" + (Get-TextSha256 "release-package-2026w27")
    $configurationHash = "sha256:" + (Get-TextSha256 "redacted-production-config-2026w27")
    $combinationForHash = [ordered]@{
        palworldVersion = "v1.0.1.100619"
        steamBuild = "24181105"
        ue4ssVersion = "commit-7f2a9d1"
        nativeBridgeVersion = "pal-control-native-1.0.1"
        nativeCapability = "experimental"
        palDefenderVersion = "paldefender-1.9.4"
        caddyVersion = "2.10.0"
        controlApiCommit = $controlCommit
        deploymentPackageSha256 = $deploymentHash
        configurationSha256 = $configurationHash
    }
    $combinationId = Get-CombinationId $combinationForHash

    $manifest = [ordered]@{
        '$schema' = "https://github.com/newonei/pal-control/schemas/acceptance-evidence/v1"
        schemaVersion = "1.0.0"
        schemaSha256 = $schemaHash
        manifestId = "ae-admin-rbac-campaign-2026w27"
        gateId = "p0-07-real-admin-rbac"
        gateCatalogVersion = "1.0.0"
        gateCatalogSha256 = $catalogHash
        evidenceMode = "live"
        environment = [ordered]@{
            environmentId = "production-cn-01"
            kind = "production"
            isSynthetic = $false
            dataClassification = "redacted"
            serverIdentityHash = "sha256:" + (Get-TextSha256 "server-01-keyed-ref")
            worldIdentityHash = "sha256:" + (Get-TextSha256 "world-2026w27-keyed-ref")
        }
        versionCombination = [ordered]@{
            combinationId = $combinationId
            palworldVersion = "v1.0.1.100619"
            steamBuild = "24181105"
            ue4ssVersion = "commit-7f2a9d1"
            nativeBridgeVersion = "pal-control-native-1.0.1"
            nativeCapability = "experimental"
            palDefenderVersion = "paldefender-1.9.4"
            caddyVersion = "2.10.0"
            controlApiCommit = $controlCommit
            deploymentPackageSha256 = $deploymentHash
            configurationSha256 = $configurationHash
        }
        runbook = [ordered]@{ id = "admin-security-live-drill"; version = "1.0.0"; commit = $runbookCommit }
        execution = [ordered]@{
            executor = [ordered]@{
                subjectId = Get-Subject "security-operator-a"
                identityProvider = "entra-id"
                role = "security-operator"
                implementationContributor = $true
            }
            startedAt = "2026-07-01T00:00:00+00:00"
            endedAt = "2026-07-01T01:00:00+00:00"
        }
        participants = @(
            [ordered]@{ subjectId = Get-Subject "administrator-a"; identityProvider = "entra-id"; role = "administrator"; implementationContributor = $true },
            [ordered]@{ subjectId = Get-Subject "administrator-b"; identityProvider = "entra-id"; role = "administrator"; implementationContributor = $false })
        artifacts = @(
            (New-Artifact "rbac-session-trace" "rbac-session-trace" "evidence/rbac-session-trace.json" "application/json" "2026-07-01T00:50:00+00:00" "control-api@release-2026w27"),
            (New-Artifact "mfa-revocation-trace" "mfa-revocation-trace" "evidence/mfa-revocation-trace.json" "application/json" "2026-07-01T00:55:00+00:00" "control-api@release-2026w27"),
            (New-Artifact "admin-audit-export" "admin-audit-export" "evidence/admin-audit-export.ndjson" "application/x-ndjson" "2026-07-01T01:00:00+00:00" "control-api@release-2026w27"),
            (New-Artifact "sensitive-scan-report" "sensitive-scan-report" "evidence/sensitive-scan-report.json" "application/json" "2026-07-01T01:02:00+00:00" "gitleaks@8.30.1"),
            (New-Artifact "review-record" "review-record" "evidence/review-record.json" "application/json" "2026-07-01T01:03:00+00:00" "audit-workflow@1.0.0"))
        checks = @(
            [ordered]@{ id = "anonymous-and-player-cookie-denied"; result = "pass"; artifactIds = @("rbac-session-trace"); summary = "Anonymous and player-cookie requests were denied in every recorded attempt." },
            [ordered]@{ id = "low-privilege-access-denied"; result = "pass"; artifactIds = @("rbac-session-trace"); summary = "Low-privilege administrator requests were denied by the production API." },
            [ordered]@{ id = "two-admin-subjects-isolated"; result = "pass"; artifactIds = @("rbac-session-trace", "admin-audit-export"); summary = "Two distinct administrator subjects exercised isolated permissions." },
            [ordered]@{ id = "revocation-effective"; result = "pass"; artifactIds = @("mfa-revocation-trace"); summary = "Revoked administrator access was rejected after the revocation boundary." },
            [ordered]@{ id = "mfa-enforced"; result = "pass"; artifactIds = @("mfa-revocation-trace"); summary = "Every high-risk operation required a successful MFA challenge." },
            [ordered]@{ id = "actor-attribution-not-client-forgeable"; result = "pass"; artifactIds = @("admin-audit-export"); summary = "Audit actor references came from authenticated server context and resisted client overrides." })
        metrics = @([ordered]@{ id = "admin-subject-count"; value = 2; unit = "count"; artifactIds = @("admin-audit-export") })
        sensitiveDataScan = [ordered]@{
            scanner = "gitleaks"
            scannerVersion = "8.30.1"
            command = "gitleaks dir --source evidence --report-format json"
            scope = "evidence-artifacts"
            startedAt = "2026-07-01T01:01:00+00:00"
            endedAt = "2026-07-01T01:02:00+00:00"
            result = "pass"
            findingCount = 0
            reportArtifactId = "sensitive-scan-report"
            scannedArtifactIds = @("rbac-session-trace", "mfa-revocation-trace", "admin-audit-export")
        }
        review = [ordered]@{
            reviewer = [ordered]@{ subjectId = Get-Subject "independent-reviewer-a"; identityProvider = "entra-id"; role = "reviewer"; implementationContributor = $false }
            reviewedAt = "2026-07-01T01:04:00+00:00"
            decision = "approved"
            artifactIds = @("review-record")
            summary = "Independent reviewer approved version binding, redaction, RBAC, MFA and audit evidence."
        }
        relatedEvidence = @()
        conclusion = [ordered]@{ result = "pass"; decidedAt = "2026-07-01T01:04:00+00:00"; summary = "Production administrator security drill passed every catalog requirement." }
    }
    $validManifest = Join-Path $bundleRoot "manifest.json"
    Sign-Manifest "valid" $validManifest $manifest $securityExecutorKeyId $reviewerKeyId

    $listResult = Invoke-EvidenceTool "list-gates" @("list-gates", "--schema", $schema, "--catalog", $catalog) 0
    $gates = $listResult.Stdout | ConvertFrom-Json
    if ($gates.Count -ne 22) { throw "Expected 22 external gate policies, found $($gates.Count)." }

    $validResult = Invoke-EvidenceTool "valid" @("verify", "--manifest", $validManifest, "--schema", $schema, "--catalog", $catalog) 0
    $validJson = $validResult.Stdout | ConvertFrom-Json
    if (-not $validJson.valid -or $validJson.gateId -ne "p0-07-real-admin-rbac") {
        throw "Valid live manifest did not produce the expected verification summary."
    }

    $computedCombination = Invoke-EvidenceTool "combination" @("combination-id", "--manifest", $validManifest) 0
    if ($computedCombination.Stdout.Trim() -ne $combinationId) { throw "combination-id did not reproduce the version lock." }
    $computedArtifactHash = Invoke-EvidenceTool "hash" @("hash", "--file", (Join-Path $evidenceRoot "rbac-session-trace.json")) 0
    if ($computedArtifactHash.Stdout.Trim() -ne $manifest.artifacts[0].sha256) { throw "hash command did not reproduce the artifact digest." }

    Write-Utf8 (Join-Path $evidenceRoot "capacity-slo-decision.json") `
        '{"capacityTargetTriggered":false,"availabilityTargetTriggered":false,"decision":"retain-single-instance-sqlite"}'
    Write-Utf8 (Join-Path $evidenceRoot "sqlite-scope-review.json") `
        '{"deploymentScope":"single-instance","database":"sqlite","reviewed":true}'
    Write-Utf8 (Join-Path $evidenceRoot "capacity-sensitive-scan-report.json") `
        '{"scanner":"gitleaks","version":"8.30.1","findings":[]}'
    Write-Utf8 (Join-Path $evidenceRoot "capacity-review-record.json") `
        '{"decision":"approved-not-applicable","basis":"capacity and availability targets not triggered"}'
    $notApplicable = Copy-Manifest $manifest
    $notApplicable.manifestId = "ae-postgresql-capacity-decision-2026w27"
    $notApplicable.gateId = "p1-07-postgresql-capacity-migration"
    $databaseTrustKey = $trustKeys | Where-Object { $_.keyId -eq $databaseExecutorKeyId }
    $notApplicable.execution.executor = Copy-Manifest $databaseTrustKey.subject
    $notApplicable.participants = @()
    $notApplicable.versionCombination.nativeCapability = "stable"
    $notApplicable.versionCombination.combinationId = `
        Get-CombinationId $notApplicable.versionCombination
    $notApplicable.artifacts = @(
        (New-Artifact "capacity-slo-decision" "capacity-slo-decision" "evidence/capacity-slo-decision.json" "application/json" "2026-07-01T00:50:00+00:00" "sre-capacity-review@1.0.0"),
        (New-Artifact "sqlite-scope-review" "sqlite-scope-review" "evidence/sqlite-scope-review.json" "application/json" "2026-07-01T00:55:00+00:00" "architecture-review@1.0.0"),
        (New-Artifact "capacity-sensitive-scan-report" "sensitive-scan-report" "evidence/capacity-sensitive-scan-report.json" "application/json" "2026-07-01T01:02:00+00:00" "gitleaks@8.30.1"),
        (New-Artifact "capacity-review-record" "review-record" "evidence/capacity-review-record.json" "application/json" "2026-07-01T01:03:00+00:00" "audit-workflow@1.0.0"))
    $notApplicable.checks = @(
        [ordered]@{ id = "capacity-or-availability-target-not-triggered"; result = "pass"; artifactIds = @("capacity-slo-decision"); summary = "Approved SLO review confirms neither capacity nor availability target triggered migration." },
        [ordered]@{ id = "single-instance-sqlite-scope-retained"; result = "pass"; artifactIds = @("sqlite-scope-review"); summary = "Deployment remains explicitly single-instance with SQLite and no HA claim." })
    $notApplicable.metrics = @()
    $notApplicable.sensitiveDataScan.reportArtifactId = "capacity-sensitive-scan-report"
    $notApplicable.sensitiveDataScan.scannedArtifactIds = @("capacity-slo-decision", "sqlite-scope-review")
    $notApplicable.review.artifactIds = @("capacity-review-record")
    $notApplicable.review.summary = "Independent reviewer approved the evidence-backed conditional not-applicable decision."
    $notApplicable.conclusion.result = "not-applicable"
    $notApplicable.conclusion.summary = "PostgreSQL migration is not applicable because approved capacity and availability triggers remain false."
    $notApplicableManifest = Join-Path $bundleRoot "manifest-postgresql-not-applicable.json"
    Sign-Manifest "not-applicable" $notApplicableManifest $notApplicable `
        $databaseExecutorKeyId $reviewerKeyId
    $notApplicableResult = Invoke-EvidenceTool "not-applicable" `
        @("verify", "--manifest", $notApplicableManifest, "--schema", $schema, "--catalog", $catalog) 0
    if (($notApplicableResult.Stdout | ConvertFrom-Json).conclusion -ne "not-applicable") {
        throw "Conditional PostgreSQL gate did not return a verified not-applicable conclusion."
    }

    $adminBundleRoot = $bundleRoot
    $bundleRoot = Join-Path $tempRoot "production-soak"
    $evidenceRoot = Join-Path $bundleRoot "evidence"
    [IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
    $productionSoakOutput = Join-Path $evidenceRoot "soak"
    Invoke-EvidenceTool "create-production-soak" `
        @("create-soak-fixture", $productionSoakOutput, "production") 0 "" $true | Out-Null
    Write-Utf8 (Join-Path $evidenceRoot "resource-growth-analysis.json") `
        '{"memory":"bounded","handles":"bounded","logs":"bounded"}'
    Write-Utf8 (Join-Path $evidenceRoot "queue-session-analysis.json") `
        '{"queues":"bounded","sessions":"bounded"}'
    Write-Utf8 (Join-Path $evidenceRoot "sensitive-scan-report.json") `
        '{"scanner":"gitleaks","version":"8.30.1","findings":[]}'
    Write-Utf8 (Join-Path $evidenceRoot "review-record.json") `
        '{"decision":"approved","scope":"canonical soak report and production thresholds"}'
    $soakManifest = Copy-Manifest $manifest
    $soakManifest.manifestId = "ae-production-soak-2026w27"
    $soakManifest.gateId = "p1-07-24-hour-soak"
    $soakManifest.environment.kind = "controlled-live"
    $sreTrustKey = $trustKeys | Where-Object { $_.keyId -eq $sreExecutorKeyId }
    $soakManifest.execution.executor = Copy-Manifest $sreTrustKey.subject
    $soakManifest.execution.startedAt = "2026-06-30T00:00:00+00:00"
    $soakManifest.execution.endedAt = "2026-07-01T00:05:00+00:00"
    $soakManifest.participants = @()
    $soakManifest.versionCombination.nativeCapability = "stable"
    $soakManifest.versionCombination.combinationId = `
        Get-CombinationId $soakManifest.versionCombination
    $soakManifest.artifacts = @(
        (New-Artifact "soak-report" "soak-metric-series" "evidence/soak/report.json" "application/json" "2026-07-01T00:05:00+00:00" "pal-control-soak@1.0.0"),
        (New-Artifact "soak-report-hash" "soak-report-hash" "evidence/soak/report.json.sha256" "text/plain" "2026-07-01T00:05:00+00:00" "pal-control-soak@1.0.0"),
        (New-Artifact "resource-growth-analysis" "resource-growth-analysis" "evidence/resource-growth-analysis.json" "application/json" "2026-07-01T00:05:00+00:00" "pal-control-soak@1.0.0"),
        (New-Artifact "queue-session-analysis" "queue-session-growth-analysis" "evidence/queue-session-analysis.json" "application/json" "2026-07-01T00:05:00+00:00" "pal-control-soak@1.0.0"),
        (New-Artifact "soak-sensitive-scan-report" "sensitive-scan-report" "evidence/sensitive-scan-report.json" "application/json" "2026-07-01T00:07:00+00:00" "gitleaks@8.30.1"),
        (New-Artifact "soak-review-record" "review-record" "evidence/review-record.json" "application/json" "2026-07-01T00:08:00+00:00" "audit-workflow@1.0.0"))
    $soakManifest.checks = @(
        [ordered]@{ id = "memory-no-sustained-growth"; result = "pass"; artifactIds = @("soak-report", "resource-growth-analysis"); summary = "Canonical samples show no sustained memory growth beyond frozen production thresholds." },
        [ordered]@{ id = "handles-no-sustained-growth"; result = "pass"; artifactIds = @("soak-report", "resource-growth-analysis"); summary = "Canonical samples show no sustained handle growth beyond frozen production thresholds." },
        [ordered]@{ id = "logs-within-budget"; result = "pass"; artifactIds = @("soak-report", "resource-growth-analysis"); summary = "Canonical samples keep log growth within the frozen production budget." },
        [ordered]@{ id = "queues-no-sustained-growth"; result = "pass"; artifactIds = @("soak-report", "queue-session-analysis"); summary = "Canonical samples show no sustained queue growth beyond frozen production thresholds." },
        [ordered]@{ id = "sessions-no-sustained-growth"; result = "pass"; artifactIds = @("soak-report", "queue-session-analysis"); summary = "Canonical samples show no sustained session growth beyond frozen production thresholds." })
    $soakManifest.metrics = @(
        [ordered]@{ id = "observation-duration-hours"; value = 24; unit = "hours"; artifactIds = @("soak-report") },
        [ordered]@{ id = "metric-sample-count"; value = 290; unit = "count"; artifactIds = @("soak-report") },
        [ordered]@{ id = "sustained-growth-finding-count"; value = 0; unit = "count"; artifactIds = @("soak-report") })
    $soakManifest.sensitiveDataScan = [ordered]@{
        scanner = "gitleaks"
        scannerVersion = "8.30.1"
        command = "gitleaks dir --source evidence --report-format json"
        scope = "evidence-artifacts"
        startedAt = "2026-07-01T00:06:00+00:00"
        endedAt = "2026-07-01T00:07:00+00:00"
        result = "pass"
        findingCount = 0
        reportArtifactId = "soak-sensitive-scan-report"
        scannedArtifactIds = @(
            "soak-report", "soak-report-hash", "resource-growth-analysis", "queue-session-analysis")
    }
    $reviewerTrustKey = $trustKeys | Where-Object { $_.keyId -eq $reviewerKeyId }
    $soakManifest.review = [ordered]@{
        reviewer = (Copy-Manifest $reviewerTrustKey.subject)
        reviewedAt = "2026-07-01T00:09:00+00:00"
        decision = "approved"
        artifactIds = @("soak-review-record")
        summary = "Independent reviewer approved the canonical production soak report and frozen threshold result."
    }
    $soakManifest.relatedEvidence = @()
    $soakManifest.conclusion = [ordered]@{
        result = "pass"
        decidedAt = "2026-07-01T00:09:00+00:00"
        summary = "The full production-profile soak report passed every recomputed growth threshold."
    }
    $soakManifestPath = Join-Path $bundleRoot "manifest.json"
    Sign-Manifest "production-soak" $soakManifestPath $soakManifest `
        $sreExecutorKeyId $reviewerKeyId
    Invoke-EvidenceTool "production-soak-valid" @(
        "verify", "--manifest", $soakManifestPath) 0 | Out-Null

    $soakMetricDrift = Copy-Manifest $soakManifest
    $soakMetricDrift.metrics[1].value = 291
    $soakMetricDriftPath = Join-Path $bundleRoot "manifest-metric-drift.json"
    Sign-Manifest "production-soak-metric-drift" $soakMetricDriftPath $soakMetricDrift `
        $sreExecutorKeyId $reviewerKeyId
    Invoke-EvidenceTool "production-soak-metric-drift" @(
        "verify", "--manifest", $soakMetricDriftPath) `
        2 "ACCEPTANCE_SOAK_MANIFEST_METRIC_MISMATCH" | Out-Null

    $ciDirectory = Join-Path $tempRoot "ci-soak-report"
    Invoke-EvidenceTool "create-ci-soak" `
        @("create-soak-fixture", $ciDirectory, "ci") 0 "" $true | Out-Null
    Copy-Item -LiteralPath (Join-Path $ciDirectory "report.json") `
        -Destination (Join-Path $evidenceRoot "ci-report.json")
    Copy-Item -LiteralPath (Join-Path $ciDirectory "report.json.sha256") `
        -Destination (Join-Path $evidenceRoot "ci-report.json.sha256")
    $ciSoakManifest = Copy-Manifest $soakManifest
    $ciSoakManifest.artifacts[0].path = "evidence/ci-report.json"
    $ciSoakManifest.artifacts[0].sha256 = Get-Sha256 (Join-Path $evidenceRoot "ci-report.json")
    $ciSoakManifest.artifacts[0].sizeBytes = ([IO.FileInfo]::new(
        (Join-Path $evidenceRoot "ci-report.json"))).Length
    $ciSoakManifest.artifacts[1].path = "evidence/ci-report.json.sha256"
    $ciSoakManifest.artifacts[1].sha256 = Get-Sha256 (Join-Path $evidenceRoot "ci-report.json.sha256")
    $ciSoakManifest.artifacts[1].sizeBytes = ([IO.FileInfo]::new(
        (Join-Path $evidenceRoot "ci-report.json.sha256"))).Length
    $ciSoakManifestPath = Join-Path $bundleRoot "manifest-ci-profile.json"
    Sign-Manifest "ci-soak-profile" $ciSoakManifestPath $ciSoakManifest `
        $sreExecutorKeyId $reviewerKeyId
    Invoke-EvidenceTool "ci-soak-profile-rejected" @(
        "verify", "--manifest", $ciSoakManifestPath) `
        2 "ACCEPTANCE_SOAK_PROFILE_REJECTED" | Out-Null

    $paddedDirectory = Join-Path $tempRoot "padded-soak-report"
    Invoke-EvidenceTool "create-padded-soak" `
        @("create-soak-fixture", $paddedDirectory, "padded") 0 "" $true | Out-Null
    Copy-Item -LiteralPath (Join-Path $paddedDirectory "report.json") `
        -Destination (Join-Path $evidenceRoot "padded-report.json")
    Copy-Item -LiteralPath (Join-Path $paddedDirectory "report.json.sha256") `
        -Destination (Join-Path $evidenceRoot "padded-report.json.sha256")
    $paddedSoakManifest = Copy-Manifest $soakManifest
    $paddedSoakManifest.artifacts[0].path = "evidence/padded-report.json"
    $paddedSoakManifest.artifacts[0].sha256 = Get-Sha256 (
        Join-Path $evidenceRoot "padded-report.json")
    $paddedSoakManifest.artifacts[0].sizeBytes = ([IO.FileInfo]::new(
        (Join-Path $evidenceRoot "padded-report.json"))).Length
    $paddedSoakManifest.artifacts[1].path = "evidence/padded-report.json.sha256"
    $paddedSoakManifest.artifacts[1].sha256 = Get-Sha256 (
        Join-Path $evidenceRoot "padded-report.json.sha256")
    $paddedSoakManifest.artifacts[1].sizeBytes = ([IO.FileInfo]::new(
        (Join-Path $evidenceRoot "padded-report.json.sha256"))).Length
    $paddedSoakManifestPath = Join-Path $bundleRoot "manifest-padded-report.json"
    Sign-Manifest "padded-soak-report" $paddedSoakManifestPath $paddedSoakManifest `
        $sreExecutorKeyId $reviewerKeyId
    Invoke-EvidenceTool "padded-soak-report-rejected" @(
        "verify", "--manifest", $paddedSoakManifestPath) `
        2 "ACCEPTANCE_SOAK_ANALYSIS_MISMATCH" | Out-Null

    $bundleRoot = $adminBundleRoot
    $evidenceRoot = Join-Path $bundleRoot "evidence"

    $negativeCases = @()
    $case = Copy-Manifest $manifest
    $case.environment.isSynthetic = $true
    $negativeCases += [pscustomobject]@{ Name = "synthetic"; Value = $case; Code = "ACCEPTANCE_SYNTHETIC_ENVIRONMENT_REJECTED" }
    $case = Copy-Manifest $manifest
    $case.review.reviewer.subjectId = $case.execution.executor.subjectId
    $negativeCases += [pscustomobject]@{ Name = "same-subject"; Value = $case; Code = "ACCEPTANCE_SUBJECT_NOT_TRUSTED" }
    $case = Copy-Manifest $manifest
    $case.sensitiveDataScan.findingCount = 1; $case.sensitiveDataScan.result = "fail"
    $negativeCases += [pscustomobject]@{ Name = "scan-finding"; Value = $case; Code = "ACCEPTANCE_SENSITIVE_SCAN_FAILED" }
    $case = Copy-Manifest $manifest
    $case.metrics[0].value = 1
    $negativeCases += [pscustomobject]@{ Name = "metric-too-small"; Value = $case; Code = "ACCEPTANCE_METRIC_CONSTRAINT_FAILED" }
    $case = Copy-Manifest $manifest
    $case.participants = @($case.participants[0])
    $negativeCases += [pscustomobject]@{ Name = "participant-too-small"; Value = $case; Code = "ACCEPTANCE_PARTICIPANT_COUNT_INVALID" }
    $case = Copy-Manifest $manifest
    $case.conclusion.result = "pending"
    $negativeCases += [pscustomobject]@{ Name = "pending"; Value = $case; Code = "ACCEPTANCE_CONCLUSION_NOT_PASS" }
    $case = Copy-Manifest $manifest
    $case.conclusion.result = "not-applicable"
    $negativeCases += [pscustomobject]@{ Name = "forbidden-not-applicable"; Value = $case; Code = "ACCEPTANCE_NOT_APPLICABLE_FORBIDDEN" }
    $case = Copy-Manifest $manifest
    $case.versionCombination.combinationId = "combo-sha256:" + ("f" * 64)
    $negativeCases += [pscustomobject]@{ Name = "combination-drift"; Value = $case; Code = "ACCEPTANCE_VERSION_COMBINATION_ID_MISMATCH" }
    $case = Copy-Manifest $manifest
    $case.versionCombination.palworldVersion = "v1.0.1`nsteamBuild=forged"
    $negativeCases += [pscustomobject]@{ Name = "combination-control-injection"; Value = $case; Code = "ACCEPTANCE_CONTROL_CHARACTER_REJECTED" }
    $case = Copy-Manifest $manifest
    $case.gateCatalogSha256 = "sha256:" + ("e" * 64)
    $negativeCases += [pscustomobject]@{ Name = "catalog-drift"; Value = $case; Code = "ACCEPTANCE_MANIFEST_CATALOG_MISMATCH" }
    $case = Copy-Manifest $manifest
    $case.sensitiveDataScan.scannedArtifactIds = @("rbac-session-trace", "mfa-revocation-trace")
    $negativeCases += [pscustomobject]@{ Name = "unscanned-artifact"; Value = $case; Code = "ACCEPTANCE_ARTIFACT_NOT_SCANNED" }
    $case = Copy-Manifest $manifest
    $case.artifacts[0].sha256 = "sha256:" + ("f" * 64)
    $negativeCases += [pscustomobject]@{ Name = "hash-mismatch"; Value = $case; Code = "ACCEPTANCE_ARTIFACT_HASH_MISMATCH" }
    $case = Copy-Manifest $manifest
    $case.artifacts[0].path = "../outside.json"
    $negativeCases += [pscustomobject]@{ Name = "path-traversal"; Value = $case; Code = "ACCEPTANCE_ARTIFACT_PATH_INVALID" }
    foreach ($portablePath in @(
        "evidence/con.txt",
        "evidence/report.json:stream",
        "evidence/trailingdot.",
        "evidence/nonascii-证据.json")) {
        $case = Copy-Manifest $manifest
        $case.artifacts[0].path = $portablePath
        $negativeCases += [pscustomobject]@{
            Name = "portable-path-" + $negativeCases.Count
            Value = $case
            Code = "ACCEPTANCE_ARTIFACT_PATH_INVALID"
        }
    }
    $case = Copy-Manifest $manifest
    $case.participants[0].subjectId = Get-Subject "untrusted-administrator"
    $negativeCases += [pscustomobject]@{ Name = "untrusted-subject"; Value = $case; Code = "ACCEPTANCE_SUBJECT_NOT_TRUSTED" }
    $case = Copy-Manifest $manifest
    $replacement = if ($case.signatures.executor.signatureBase64[0] -eq 'A') { 'B' } else { 'A' }
    $case.signatures.executor.signatureBase64 = `
        $replacement + $case.signatures.executor.signatureBase64.Substring(1)
    $negativeCases += [pscustomobject]@{ Name = "signature-tamper"; Value = $case; Code = "ACCEPTANCE_SIGNATURE_INVALID" }
    $case = Copy-Manifest $manifest
    $case.signatures.reviewer.keyId = $case.signatures.executor.keyId
    $negativeCases += [pscustomobject]@{ Name = "same-signature-key"; Value = $case; Code = "ACCEPTANCE_SIGNATURE_KEYS_NOT_DISTINCT" }
    $case = Copy-Manifest $manifest
    $case | Add-Member -NotePropertyName unexpected -NotePropertyValue "rejected"
    $negativeCases += [pscustomobject]@{ Name = "unknown-property"; Value = $case; Code = "ACCEPTANCE_MANIFEST_INVALID_JSON" }

    foreach ($negative in $negativeCases) {
        $path = Join-Path $bundleRoot ("manifest-$($negative.Name).json")
        Write-Json $path $negative.Value
        Invoke-EvidenceTool $negative.Name @("verify", "--manifest", $path, "--schema", $schema, "--catalog", $catalog) 2 $negative.Code | Out-Null
    }

    $secretEnvelope = Copy-Manifest $manifest
    $secretEnvelope.review.summary = `
        "Independent review accidentally included apiKey=abcdefghijklmnopqrstuvwx in its final record."
    $secretEnvelopePath = Join-Path $bundleRoot "manifest-envelope-secret.json"
    Write-Json $secretEnvelopePath $secretEnvelope
    Invoke-EvidenceTool "envelope-secret" `
        @("verify", "--manifest", $secretEnvelopePath) `
        2 "ACCEPTANCE_ENVELOPE_SENSITIVE_DATA_FOUND" | Out-Null

    Invoke-EvidenceTool "wrong-trust-pin" @(
        "verify", "--manifest", $validManifest,
        "--trust-store", $script:trustStorePath,
        "--trust-store-sha256", ("sha256:" + ("f" * 64))) `
        2 "ACCEPTANCE_TRUST_STORE_PIN_MISMATCH" | Out-Null

    $duplicateKeyTrustStore = [IO.File]::ReadAllText(
        $script:trustStorePath) | ConvertFrom-Json
    $securityTrustKey = $duplicateKeyTrustStore.keys |
        Where-Object { $_.keyId -eq $securityExecutorKeyId }
    $reviewTrustKey = $duplicateKeyTrustStore.keys |
        Where-Object { $_.keyId -eq $reviewerKeyId }
    $reviewTrustKey.publicKeySpkiBase64 = $securityTrustKey.publicKeySpkiBase64
    $duplicateKeyTrustStorePath = Join-Path $tempRoot "duplicate-public-key-trust-store.json"
    Write-Json $duplicateKeyTrustStorePath $duplicateKeyTrustStore
    Invoke-EvidenceTool "duplicate-public-key" @(
        "verify", "--manifest", $validManifest,
        "--trust-store", $duplicateKeyTrustStorePath,
        "--trust-store-sha256", (Get-Sha256 $duplicateKeyTrustStorePath)) `
        2 "ACCEPTANCE_TRUST_PUBLIC_KEY_DUPLICATE" | Out-Null
    Invoke-EvidenceTool "now-override-rejected" @(
        "verify", "--manifest", $validManifest,
        "--now", "2026-07-01T02:00:00.0000000+00:00") `
        2 "ACCEPTANCE_CLI_ERROR" | Out-Null

    $linkedBundle = Join-Path $tempRoot "linked-valid-admin"
    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        New-Item -ItemType Junction -Path $linkedBundle -Target $bundleRoot | Out-Null
    }
    else {
        New-Item -ItemType SymbolicLink -Path $linkedBundle -Target $bundleRoot | Out-Null
    }
    Invoke-EvidenceTool "manifest-reparse-ancestor" @(
        "verify", "--manifest", (Join-Path $linkedBundle "manifest.json")) `
        2 "ACCEPTANCE_MANIFEST_NOT_FOUND" | Out-Null

    $validManifestText = [IO.File]::ReadAllText($validManifest)
    $trailingManifestPath = Join-Path $bundleRoot "manifest-trailing-space.json"
    Write-Utf8 $trailingManifestPath ($validManifestText + " ")
    Invoke-EvidenceTool "manifest-trailing-space" @(
        "verify", "--manifest", $trailingManifestPath) `
        2 "ACCEPTANCE_MANIFEST_TRAILING_BYTES_INVALID" | Out-Null

    $oversizedManifestPath = Join-Path $bundleRoot "manifest-oversized.json"
    Write-Utf8 $oversizedManifestPath ($validManifestText + (" " * (1024 * 1024)))
    Invoke-EvidenceTool "manifest-oversized" @(
        "verify", "--manifest", $oversizedManifestPath) `
        2 "ACCEPTANCE_STRUCTURED_DOCUMENT_TOO_LARGE" | Out-Null

    $duplicateNestedPath = Join-Path $bundleRoot "manifest-duplicate-nested.json"
    $duplicateNested = [regex]::new(
        '("environmentId"\s*:\s*"production-cn-01"\s*,)').Replace(
        $validManifestText,
        '$1' + "`n" + '    "environmentId": "forged-environment",',
        1)
    if ($duplicateNested -eq $validManifestText) { throw "Nested duplicate fixture injection failed." }
    Write-Utf8 $duplicateNestedPath $duplicateNested
    Invoke-EvidenceTool "manifest-duplicate-nested" @(
        "verify", "--manifest", $duplicateNestedPath) `
        2 "ACCEPTANCE_JSON_DUPLICATE_PROPERTY" | Out-Null

    $duplicateCredentialPath = Join-Path $bundleRoot "manifest-duplicate-credential.json"
    $reviewSummaryPattern = '("summary"\s*:\s*"Independent reviewer approved version binding, redaction, RBAC, MFA and audit evidence\.")'
    $duplicateCredential = [regex]::new($reviewSummaryPattern).Replace(
        $validManifestText,
        '"summary": "apiKey=abcdefghijklmnopqrstuvwx leaked in shadow value",' + "`n" +
        '    $1',
        1)
    if ($duplicateCredential -eq $validManifestText) { throw "Credential duplicate fixture injection failed." }
    Write-Utf8 $duplicateCredentialPath $duplicateCredential
    Invoke-EvidenceTool "manifest-duplicate-credential" @(
        "verify", "--manifest", $duplicateCredentialPath) `
        2 "ACCEPTANCE_JSON_DUPLICATE_PROPERTY" | Out-Null

    $trustStoreText = [IO.File]::ReadAllText($script:trustStorePath)
    $duplicateTrustPropertyPath = Join-Path $tempRoot "duplicate-property-trust-store.json"
    $duplicateTrustProperty = [regex]::new(
        '("role"\s*:\s*"security-operator"\s*,)').Replace(
        $trustStoreText,
        '$1' + "`n" + '        "role": "reviewer",',
        1)
    if ($duplicateTrustProperty -eq $trustStoreText) { throw "Trust duplicate fixture injection failed." }
    Write-Utf8 $duplicateTrustPropertyPath $duplicateTrustProperty
    Invoke-EvidenceTool "trust-store-duplicate-nested" @(
        "verify", "--manifest", $validManifest,
        "--trust-store", $duplicateTrustPropertyPath,
        "--trust-store-sha256", (Get-Sha256 $duplicateTrustPropertyPath)) `
        2 "ACCEPTANCE_JSON_DUPLICATE_PROPERTY" | Out-Null

    $emptyPath = Join-Path $evidenceRoot "zero-byte.json"
    [IO.File]::WriteAllBytes($emptyPath, [byte[]]@())
    $case = Copy-Manifest $manifest
    $case.artifacts[0].path = "evidence/zero-byte.json"
    $case.artifacts[0].sizeBytes = 0
    $case.artifacts[0].sha256 = Get-Sha256 $emptyPath
    $emptyManifest = Join-Path $bundleRoot "manifest-empty.json"
    Write-Json $emptyManifest $case
    Invoke-EvidenceTool "empty-artifact" @("verify", "--manifest", $emptyManifest, "--schema", $schema, "--catalog", $catalog) 2 "ACCEPTANCE_ARTIFACT_EMPTY" | Out-Null

    $mockPath = Join-Path $evidenceRoot "mock-rbac-trace.json"
    [IO.File]::Copy((Join-Path $evidenceRoot "rbac-session-trace.json"), $mockPath)
    $case = Copy-Manifest $manifest
    $case.artifacts[0].path = "evidence/mock-rbac-trace.json"
    $case.artifacts[0].sha256 = Get-Sha256 $mockPath
    $case.artifacts[0].sizeBytes = ([IO.FileInfo]::new($mockPath)).Length
    $mockManifest = Join-Path $bundleRoot "manifest-mock-path.json"
    Write-Json $mockManifest $case
    Invoke-EvidenceTool "mock-path" @("verify", "--manifest", $mockManifest, "--schema", $schema, "--catalog", $catalog) 2 "ACCEPTANCE_ARTIFACT_PATH_PLACEHOLDER" | Out-Null

    $largePlaceholderPath = Join-Path $evidenceRoot "large-rbac-trace.txt"
    Write-Utf8 $largePlaceholderPath (("a" * (1024 * 1024 + 128)) + " mock evidence")
    $case = Copy-Manifest $manifest
    $case.artifacts[0].path = "evidence/large-rbac-trace.txt"
    $case.artifacts[0].mediaType = "text/plain"
    $case.artifacts[0].sha256 = Get-Sha256 $largePlaceholderPath
    $case.artifacts[0].sizeBytes = ([IO.FileInfo]::new($largePlaceholderPath)).Length
    $largePlaceholderManifest = Join-Path $bundleRoot "manifest-large-placeholder.json"
    Write-Json $largePlaceholderManifest $case
    Invoke-EvidenceTool "large-placeholder" @("verify", "--manifest", $largePlaceholderManifest, "--schema", $schema, "--catalog", $catalog) 2 "ACCEPTANCE_ARTIFACT_CONTENT_PLACEHOLDER" | Out-Null

    $largeInvalidPath = Join-Path $evidenceRoot "large-invalid-utf8.txt"
    $largeInvalid = [byte[]]::new(1024 * 1024 + 129)
    $largeInvalid[$largeInvalid.Length - 1] = 0xff
    [IO.File]::WriteAllBytes($largeInvalidPath, $largeInvalid)
    $case = Copy-Manifest $manifest
    $case.artifacts[0].path = "evidence/large-invalid-utf8.txt"
    $case.artifacts[0].mediaType = "text/plain"
    $case.artifacts[0].sha256 = Get-Sha256 $largeInvalidPath
    $case.artifacts[0].sizeBytes = ([IO.FileInfo]::new($largeInvalidPath)).Length
    $largeInvalidManifest = Join-Path $bundleRoot "manifest-large-invalid.json"
    Write-Json $largeInvalidManifest $case
    Invoke-EvidenceTool "large-invalid-utf8" @("verify", "--manifest", $largeInvalidManifest, "--schema", $schema, "--catalog", $catalog) 2 "ACCEPTANCE_ARTIFACT_TEXT_INVALID" | Out-Null

    $catalogObject = [IO.File]::ReadAllText($catalog) | ConvertFrom-Json
    $childPolicy = Copy-Manifest ($catalogObject.gates | Where-Object { $_.id -eq "p0-07-real-admin-rbac" })
    $aggregatePolicy = Copy-Manifest $childPolicy
    $aggregatePolicy.id = "test-release-aggregate"
    $aggregatePolicy.title = "Recursive release review used only by the verifier smoke test"
    $aggregatePolicy.todoReferences = @("tests/integration/acceptance-evidence-smoke.ps1")
    $aggregatePolicy.requiredRelatedGates = @("p0-07-real-admin-rbac")
    $catalogObject.gates = @($childPolicy, $aggregatePolicy)
    $recursiveCatalog = Join-Path $tempRoot "recursive-catalog.json"
    Write-Json $recursiveCatalog $catalogObject
    $recursiveCatalogHash = Get-Sha256 $recursiveCatalog

    $recursiveChildRoot = Join-Path $tempRoot "recursive-child"
    [IO.Directory]::CreateDirectory($recursiveChildRoot) | Out-Null
    Copy-Item -LiteralPath $evidenceRoot -Destination $recursiveChildRoot -Recurse
    $recursiveChild = Copy-Manifest $manifest
    $recursiveChild.manifestId = "ae-recursive-child-admin-rbac"
    $recursiveChild.gateCatalogSha256 = $recursiveCatalogHash
    $recursiveChildManifest = Join-Path $recursiveChildRoot "manifest.json"
    Sign-Manifest "recursive-child" $recursiveChildManifest $recursiveChild `
        $securityExecutorKeyId $reviewerKeyId

    $aggregate = Copy-Manifest $recursiveChild
    $aggregate.manifestId = "ae-recursive-release-aggregate"
    $aggregate.gateId = "test-release-aggregate"
    foreach ($artifact in $aggregate.artifacts) {
        $artifact.path = "recursive-child/" + $artifact.path
    }
    $aggregate.relatedEvidence = @([ordered]@{
        gateId = "p0-07-real-admin-rbac"
        manifestPath = "recursive-child/manifest.json"
        sha256 = Get-Sha256 $recursiveChildManifest
    })
    $aggregateManifest = Join-Path $tempRoot "recursive-aggregate.json"
    Sign-Manifest "recursive-aggregate" $aggregateManifest $aggregate `
        $securityExecutorKeyId $reviewerKeyId
    $aggregateResult = Invoke-EvidenceTool "recursive-pass" `
        @($aggregateManifest, $schema, $recursiveCatalog) 0 "" $true
    if (($aggregateResult.Stdout | ConvertFrom-Json).relatedManifestCount -ne 1) {
        throw "Recursive release review did not verify exactly one bound child manifest."
    }
    $driftedChild = Copy-Manifest $recursiveChild
    $driftedChild.versionCombination.controlApiCommit = "ef" * 20
    $driftedChild.versionCombination.combinationId = `
        Get-CombinationId $driftedChild.versionCombination
    Sign-Manifest "recursive-child-drift" $recursiveChildManifest $driftedChild `
        $securityExecutorKeyId $reviewerKeyId
    $aggregate.relatedEvidence[0].sha256 = Get-Sha256 $recursiveChildManifest
    Sign-Manifest "recursive-aggregate-drift" $aggregateManifest $aggregate `
        $securityExecutorKeyId $reviewerKeyId
    Invoke-EvidenceTool "recursive-combination-drift" `
        @($aggregateManifest, $schema, $recursiveCatalog) `
        2 "ACCEPTANCE_RELATED_COMBINATION_MISMATCH" $true | Out-Null
    Sign-Manifest "recursive-child-restore" $recursiveChildManifest $recursiveChild `
        $securityExecutorKeyId $reviewerKeyId
    $aggregate.relatedEvidence[0].sha256 = Get-Sha256 $recursiveChildManifest
    Sign-Manifest "recursive-aggregate-restore" $aggregateManifest $aggregate `
        $securityExecutorKeyId $reviewerKeyId
    [IO.File]::AppendAllText($recursiveChildManifest, "`n", [Text.UTF8Encoding]::new($false))
    Invoke-EvidenceTool "recursive-tamper" `
        @($aggregateManifest, $schema, $recursiveCatalog) `
        2 "ACCEPTANCE_RELATED_MANIFEST_HASH_MISMATCH" $true | Out-Null

    Invoke-EvidenceTool "checked-in-template" @("verify", "--manifest", $example, "--schema", $schema, "--catalog", $catalog) 2 "ACCEPTANCE_EVIDENCE_NOT_LIVE" | Out-Null
    Invoke-EvidenceTool "checked-in-trust-template" @(
        "verify", "--manifest", $validManifest,
        "--trust-store", $trustStoreExample,
        "--trust-store-sha256", (Get-Sha256 $trustStoreExample)) `
        2 "ACCEPTANCE_TRUST_KEY_INVALID" | Out-Null

    $generatedTemplate = Join-Path $tempRoot "generated-template.json"
    Invoke-EvidenceTool "create-template" @("create-template", "--gate", "p0-04-native-persistence", "--output", $generatedTemplate, "--schema", $schema, "--catalog", $catalog) 0 | Out-Null
    Invoke-EvidenceTool "generated-template-rejected" @("verify", "--manifest", $generatedTemplate, "--schema", $schema, "--catalog", $catalog) 2 "ACCEPTANCE_EVIDENCE_NOT_LIVE" | Out-Null
    Invoke-EvidenceTool "template-no-overwrite" @("create-template", "--gate", "p0-04-native-persistence", "--output", $generatedTemplate, "--schema", $schema, "--catalog", $catalog) 2 "ACCEPTANCE_CLI_ERROR" | Out-Null

    $campaignRoot = Join-Path $tempRoot "generated-campaign"
    $campaignCreate = Invoke-EvidenceTool "create-campaign" `
        @("create-campaign", "--output", $campaignRoot) 0
    $campaignCreateSummary = $campaignCreate.Stdout | ConvertFrom-Json
    if ($campaignCreateSummary.gateCount -ne 22 -or
        $campaignCreateSummary.p0Count -ne 13 -or
        $campaignCreateSummary.p1Count -ne 6 -or
        $campaignCreateSummary.p2Count -ne 3) {
        throw "Campaign creation did not include the complete 13/6/3 external-gate catalog."
    }
    $campaignInspect = Invoke-EvidenceTool "inspect-campaign" `
        @("inspect-campaign", "--root", $campaignRoot) 0
    $campaignInspectSummary = $campaignInspect.Stdout | ConvertFrom-Json
    if ($campaignInspectSummary.complete -or
        $campaignInspectSummary.verifiedGateCount -ne 0 -or
        @($campaignInspectSummary.gates | Where-Object status -eq "template").Count -ne 22) {
        throw "Campaign inspection treated generated templates as verified evidence."
    }
    $releaseReviewPath = Join-Path $campaignRoot "p0-release-review.json"
    $releaseReview = [IO.File]::ReadAllText($releaseReviewPath) | ConvertFrom-Json
    if ($releaseReview.relatedEvidence.Count -ne 12 -or
        @($releaseReview.relatedEvidence | Where-Object {
            $_.manifestPath -match '(^|/)\.\.(/|$)' -or
            $_.manifestPath -match 'replace-' -or
            -not (Test-Path -LiteralPath (Join-Path $campaignRoot `
                ([string]$_.manifestPath).Replace('/', [IO.Path]::DirectorySeparatorChar)))
        }).Count -ne 0) {
        throw "P0 release-review template does not bind all twelve in-root P0 manifest paths."
    }
    $campaignVerify = Invoke-EvidenceTool "verify-template-campaign" @(
        "verify-campaign",
        "--root", $campaignRoot,
        "--trust-store", $script:trustStorePath,
        "--trust-store-sha256", $script:trustStoreHash) 2
    $campaignVerifySummary = $campaignVerify.Stdout | ConvertFrom-Json
    if ($campaignVerifySummary.complete -or
        $campaignVerifySummary.verifiedGateCount -ne 0 -or
        $campaignVerifySummary.gates.Count -ne 22) {
        throw "verify-campaign did not fail every untouched template closed."
    }
    Invoke-EvidenceTool "campaign-no-overwrite" `
        @("create-campaign", "--output", $campaignRoot) `
        2 "ACCEPTANCE_CLI_ERROR" | Out-Null

    Write-Host ("PASS: acceptance evidence v1 embedded policy, bounded duplicate-safe manifests, " +
        "unique pinned P-256 identities, dual signatures, canonical combination binding, " +
        "strictly recomputed soak samples, deny-write lease race/TOCTOU rehash, sensitive scan, " +
        "complete 22-gate campaign scaffolding, and rejected templates.")
}
finally {
    if ($null -ne $script:privateKeys) {
        foreach ($privatePath in @($script:privateKeys.Values)) {
            if (Test-Path -LiteralPath $privatePath) {
                [IO.File]::WriteAllBytes($privatePath, [byte[]]::new(
                    ([IO.FileInfo]::new($privatePath)).Length))
            }
        }
    }
    if (Test-Path -LiteralPath $tempRoot) {
        $resolvedTemp = [IO.Path]::GetFullPath($tempRoot)
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolvedTemp.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFileName($resolvedTemp)).StartsWith("pal-control-acceptance-evidence-", [StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected test path: $resolvedTemp"
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
