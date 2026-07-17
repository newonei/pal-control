# Process-scoped defaults for integration tests that start the Control API.
# Economy-specific smokes override ExtractionMode on their own command line.
$env:PlayerPortal__Enabled = "false"
$env:ExtractionMode__Enabled = "false"

# This fixed principal is accepted only because tracked development settings
# explicitly enable DevelopmentMode. It is test data, never production config.
$env:Security__AdminAuthentication__Enabled = "true"
$env:Security__AdminAuthentication__EnableLoopbackDevelopmentPrincipal = "true"
$env:Security__AdminAuthentication__DevelopmentPrincipalSubject = "integration-test"
$env:Security__AdminAuthentication__Principals__0__Subject = "integration-test"
$env:Security__AdminAuthentication__Principals__0__ApiKeySha256 =
    "87052f5138109134ec8e8b25a5e18545e39c90244679e52b6c40c364cb671060"
$env:Security__AdminAuthentication__Principals__0__Roles__0 = "Owner"
$env:Security__AdminAuthentication__Principals__0__TotpSecretBase32 =
    "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"

function Get-TestFileSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    # The child Windows PowerShell used by GitHub-hosted runners does not
    # consistently discover Get-FileHash. Keep the bridge identity fixture
    # self-contained and stream the exact executable bytes through SHA-256.
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::Open(
            $Path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        try {
            $digest = $sha256.ComputeHash($stream)
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $sha256.Dispose()
    }

    return [BitConverter]::ToString($digest).Replace("-", "").ToLowerInvariant()
}

function Set-TestNativeBridgeApprovedIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath,

        [string]$GameBuild = "v1.0.1.100619",
        [string]$SteamBuild = "24181105",
        [string]$ModVersion = "0.3.0-smoke"
    )

    $executable = Get-Item -LiteralPath $ExecutablePath -ErrorAction Stop
    if ($executable.PSIsContainer -or $executable.Length -le 0) {
        throw "Fake Native Bridge executable is missing or empty: $ExecutablePath"
    }
    $sha256 = Get-TestFileSha256 -Path $executable.FullName
    $processSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

    $env:ExtractionMode__Safety__ApprovedNativeProtocolVersion = "1.1"
    $env:ExtractionMode__Safety__ApprovedNativeGameBuild = $GameBuild
    $env:ExtractionMode__Safety__ApprovedNativeSteamBuild = $SteamBuild
    $env:ExtractionMode__Safety__ApprovedNativeModVersion = $ModVersion
    $env:ExtractionMode__Safety__ApprovedNativeExecutableSha256 = $sha256
    $env:ExtractionMode__Safety__ApprovedNativeExecutableSize = [string]$executable.Length
    $env:ExtractionMode__Safety__ApprovedPalServerExecutablePath = $executable.FullName
    $env:ExtractionMode__Safety__ApprovedPalServerProcessSid = $processSid
    $env:ExtractionMode__Safety__ApprovedNativeDllSha256 = $sha256
    $env:ExtractionMode__Safety__ApprovedNativeDllSize = [string]$executable.Length
    $env:ExtractionMode__Safety__ApprovedUe4ssDllSha256 = $sha256
    $env:ExtractionMode__Safety__ApprovedUe4ssDllSize = [string]$executable.Length
}
