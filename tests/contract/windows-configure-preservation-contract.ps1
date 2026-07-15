$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $repositoryRoot "deploy\windows\package\tools\configure.ps1"
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath,
    [ref]$tokens,
    [ref]$parseErrors)
if (@($parseErrors).Count -ne 0) {
    throw "configure.ps1 must parse without errors: $($parseErrors -join '; ')"
}

$requiredFunctions = @(
    "Get-ConfigurationProperty",
    "Get-ConfigurationPropertyNames",
    "Test-ConfigurationObject",
    "Merge-ConfigurationObject"
)
$definitions = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $requiredFunctions -contains $node.Name
}, $true))
if ($definitions.Count -ne $requiredFunctions.Count) {
    throw "The installer configuration merge functions are incomplete."
}
Invoke-Expression (($definitions | ForEach-Object { $_.Extent.Text }) -join [Environment]::NewLine)

$defaults = [ordered]@{
    Palworld = [ordered]@{
        InstallRoot = "C:\PalServer"
        OfficialRestApi = [ordered]@{
            BaseUrl = "http://127.0.0.1:8212/v1/api/"
            Username = "admin"
            Password = ""
        }
        PalDefenderRestApi = [ordered]@{ Enabled = $false }
    }
    ExtractionMode = [ordered]@{
        Enabled = $false
        Rcon = [ordered]@{ Enabled = $false }
    }
    PlayerPortal = [ordered]@{ Enabled = $false; PublicSteam = $false }
    Security = [ordered]@{
        DevelopmentMode = $false
        AdminAuthentication = [ordered]@{ Enabled = $true }
    }
}

$existing = @'
{
  "Palworld": {
    "InstallRoot": "D:\\OldServer",
    "OfficialRestApi": {
      "BaseUrl": "http://127.0.0.1:9000/v1/api/",
      "Username": "admin",
      "Password": "old-secret",
      "TimeoutSeconds": 11
    },
    "PalDefenderRestApi": {
      "Enabled": true,
      "ExpectedVersion": "9.9.9"
    }
  },
  "ExtractionMode": {
    "Enabled": true,
    "Rcon": {
      "Enabled": true,
      "PasswordFile": "C:\\ProgramData\\PalControl\\rcon.secret"
    },
    "Settlement": {
      "RequireNativeForResourceExchange": true
    }
  },
  "PlayerPortal": {
    "Enabled": true,
    "PublicSteam": true,
    "PublicBaseUrl": "https://pal.example.invalid"
  },
  "Security": {
    "DevelopmentMode": false,
    "AdminAuthentication": {
      "Enabled": true,
      "Principals": [{ "Subject": "owner" }]
    }
  },
  "FutureSection": {
    "Marker": "must-survive"
  }
}
'@ | ConvertFrom-Json

$settings = Merge-ConfigurationObject $defaults $existing
$settings["Palworld"]["InstallRoot"] = "E:\NewServer"
$settings["Palworld"]["OfficialRestApi"]["BaseUrl"] = "http://127.0.0.1:8212/v1/api/"
$settings["Palworld"]["OfficialRestApi"]["Username"] = "admin"
$settings["Palworld"]["OfficialRestApi"]["Password"] = "new-secret"

function Assert-Equal([object]$Actual, [object]$Expected, [string]$Message) {
    if ($Actual -ne $Expected) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

Assert-Equal $settings.Palworld.InstallRoot "E:\NewServer" "The form-owned install root must change."
Assert-Equal $settings.Palworld.OfficialRestApi.Password "new-secret" "The form-owned REST password must change."
Assert-Equal $settings.Palworld.OfficialRestApi.TimeoutSeconds 11 "Unknown REST settings must survive."
Assert-Equal $settings.Palworld.PalDefenderRestApi.Enabled $true "PalDefender must not be disabled on reconfigure."
Assert-Equal $settings.ExtractionMode.Enabled $true "Scheme A must not be disabled on reconfigure."
Assert-Equal $settings.ExtractionMode.Rcon.PasswordFile "C:\ProgramData\PalControl\rcon.secret" "RCON diagnostics configuration must survive."
Assert-Equal $settings.ExtractionMode.Settlement.RequireNativeForResourceExchange $true "Native-only settlement must survive."
Assert-Equal $settings.PlayerPortal.PublicSteam $true "Public Steam mode must survive."
Assert-Equal $settings.Security.AdminAuthentication.Principals[0].Subject "owner" "Administrator principals must survive."
Assert-Equal $settings.FutureSection.Marker "must-survive" "Future top-level sections must survive."

$source = Get-Content -LiteralPath $scriptPath -Raw -Encoding UTF8
if ($source -notmatch 'Merge-ConfigurationObject\s+\$defaults\s+\$existing') {
    throw "configure.ps1 must merge an existing file instead of replacing it."
}

Write-Host "PASS: Windows installer reconfiguration preserves advanced and unknown settings."
