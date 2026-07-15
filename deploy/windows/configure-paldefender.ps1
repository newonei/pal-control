[CmdletBinding()]
param(
    [string]$InstallRoot = "C:\PalServerRuntime",
    [ValidateRange(1, 65535)]
    [int]$Port = 17993,
    [string]$TokenName = "PalControl",
    [string]$AllowedOrigin = "http://127.0.0.1:5180",
    [string[]]$Permissions = @(
        "REST.Version.Read",
        "REST.Players.Read",
        "REST.Player.Read",
        "REST.Pals.Read",
        "REST.Items.Read",
        "REST.Techs.Read",
        "REST.Progression.Read",
        "REST.Guilds.Read",
        "REST.Guild.Read",
        "REST.Banlist.Read",
        "REST.Items.Give",
        "REST.Pals.Give",
        "REST.PalTemplates.Give",
        "REST.PalEggs.Give",
        "REST.Progression.Give",
        "REST.Techs.Learn",
        "REST.Techs.Forget",
        "REST.Punishments.Ban",
        "REST.Punishments.Unban",
        "REST.Punishments.BanIP",
        "REST.Punishments.UnbanIP",
        "REST.Punishments.Kick",
        "REST.Messages.Send.PlayerChat",
        "REST.Messages.Send.GlobalChat",
        "REST.Messages.Send.GuildChat",
        "REST.Messages.Send.Log.Normal",
        "REST.Messages.Send.Log.Important",
        "REST.Messages.Send.Log.VeryImportant",
        "REST.Messages.Broadcast",
        "REST.Messages.Alert",
        "REST.Reload.Config"
    )
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-JsonAtomically {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [object]$Value
    )

    $temporaryPath = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = $Value | ConvertTo-Json -Depth 32
        [IO.File]::WriteAllText(
            $temporaryPath,
            $json + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

if (Get-Process PalServer, PalServer-Win64-Shipping-Cmd -ErrorAction SilentlyContinue) {
    throw "Stop PalServer before changing PalDefender startup configuration."
}

$resolvedRoot = (Resolve-Path -LiteralPath $InstallRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $resolvedRoot "PalServer.exe"))) {
    throw "InstallRoot is not a Palworld dedicated-server root: $resolvedRoot"
}

$palDefenderRoot = Join-Path $resolvedRoot "Pal\Binaries\Win64\PalDefender"
$restConfigPath = Join-Path $palDefenderRoot "RESTAPI\RESTConfig.json"
$mainConfigPath = Join-Path $palDefenderRoot "Config.json"
$tokensRoot = Join-Path $palDefenderRoot "RESTAPI\Tokens"
if (-not (Test-Path -LiteralPath $restConfigPath) -or
    -not (Test-Path -LiteralPath $mainConfigPath)) {
    throw "PalDefender configuration was not generated. Install the DLLs and start the server once first."
}

$restConfig = Get-Content -LiteralPath $restConfigPath -Raw | ConvertFrom-Json
$restConfig.Enabled = $true
$restConfig.LogConsole = $false
$restConfig.Address = "127.0.0.1"
$restConfig.Port = $Port
if ($null -ne $restConfig.Cors) {
    $restConfig.Cors.'Allowed-Origins' = $AllowedOrigin
}
Write-JsonAtomically -Path $restConfigPath -Value $restConfig

$mainConfig = Get-Content -LiteralPath $mainConfigPath -Raw | ConvertFrom-Json
$mainConfig.exitServerOnStartupFailure = $false
$mainConfig.shouldWarnCheaters = $true
$mainConfig.shouldWarnCheatersReason = $true
$mainConfig.shouldKickCheaters = $false
$mainConfig.shouldBanCheaters = $false
$mainConfig.shouldIPBanCheaters = $false
Write-JsonAtomically -Path $mainConfigPath -Value $mainConfig

New-Item -ItemType Directory -Force -Path $tokensRoot | Out-Null
$tokenPath = Join-Path $tokensRoot "$TokenName.json"
$token = $null
if (Test-Path -LiteralPath $tokenPath) {
    $existing = Get-Content -LiteralPath $tokenPath -Raw | ConvertFrom-Json
    $token = [string]$existing.Token
}
if ([string]::IsNullOrWhiteSpace($token)) {
    $bytes = [byte[]]::new(48)
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }
    $token = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$tokenDocument = [ordered]@{
    Name = $TokenName
    Token = $token
    Permissions = @($Permissions | Sort-Object -Unique)
}
Write-JsonAtomically -Path $tokenPath -Value $tokenDocument

$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls $tokenPath /inheritance:r /grant:r "${identity}:(F)" "*S-1-5-18:(F)" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Failed to restrict the PalDefender token file ACL."
}

[pscustomobject]@{
    Enabled = $true
    Address = "127.0.0.1"
    Port = $Port
    TokenFile = $tokenPath
    PermissionCount = @($tokenDocument.Permissions).Count
    MonitorOnly = $true
    DeleteBasePermissionGranted = $Permissions -contains "REST.Base.Delete"
}
