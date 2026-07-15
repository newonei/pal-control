[CmdletBinding()]
param(
    [string]$Url = "http://127.0.0.1:5180"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
$appRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $appRoot "PalControl.ControlApi.exe"
$configPath = Join-Path $appRoot "appsettings.Local.json"
$dataDirectory = Join-Path $appRoot "data"
$logDirectory = Join-Path $appRoot "logs"
$pidPath = Join-Path $dataDirectory "pal-control.pid"
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent().Name

function Set-PrivatePathAcl {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [switch]$Directory
    )

    if ($Directory) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
        $identityGrant = "${currentIdentity}:(OI)(CI)(F)"
        $systemGrant = "*S-1-5-18:(OI)(CI)(F)"
    }
    else {
        $identityGrant = "${currentIdentity}:(F)"
        $systemGrant = "*S-1-5-18:(F)"
    }
    & icacls.exe $Path /inheritance:r /grant:r $identityGrant $systemGrant | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to restrict the ACL for '$Path'."
    }
}

function Test-Health {
    try {
        $response = Invoke-RestMethod -Uri "$Url/health/live" -TimeoutSec 2
        return $response.service -eq "pal-control-api"
    }
    catch {
        return $false
    }
}

function Test-ConfigurationComplete {
    if (-not (Test-Path -LiteralPath $configPath)) {
        return $false
    }
    try {
        $settings = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $securityProperty = $settings.PSObject.Properties["Security"]
        if (-not $securityProperty -or -not $securityProperty.Value) {
            return $false
        }
        $adminProperty = $securityProperty.Value.PSObject.Properties["AdminAuthentication"]
        if (-not $adminProperty -or -not $adminProperty.Value) {
            return $false
        }
        $principalsProperty = $adminProperty.Value.PSObject.Properties["Principals"]
        return $principalsProperty -and @($principalsProperty.Value).Count -gt 0
    }
    catch {
        return $false
    }
}

if (-not (Test-Path -LiteralPath $executable)) {
    [System.Windows.Forms.MessageBox]::Show(
        "程序文件不完整，请重新安装幻兽商域。`n`n缺少：$executable",
        "无法启动",
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    exit 1
}

if (-not (Test-ConfigurationComplete)) {
    & (Join-Path $PSScriptRoot "configure.ps1")
}

if (-not (Test-ConfigurationComplete)) {
    [System.Windows.Forms.MessageBox]::Show(
        "首次启动需要先完成配置。没有保存任何更改。",
        "已取消启动",
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    exit 0
}

Set-PrivatePathAcl -Path $configPath
Get-ChildItem -LiteralPath $appRoot -Filter "appsettings.Local.json.*.bak" -File |
    ForEach-Object { Set-PrivatePathAcl -Path $_.FullName }
@(
    "data",
    "data/extraction",
    "logs",
    "backups",
    "backups/savegames",
    "backups/economy",
    "staging",
    "staging/economy"
) | ForEach-Object {
    Set-PrivatePathAcl -Path (Join-Path $appRoot $_) -Directory
}

if (Test-Health) {
    Start-Process $Url
    exit 0
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$stdout = Join-Path $logDirectory "pal-control-$stamp.log"
$stderr = Join-Path $logDirectory "pal-control-$stamp.err.log"
$process = Start-Process `
    -FilePath $executable `
    -WorkingDirectory $appRoot `
    -WindowStyle Hidden `
    -RedirectStandardOutput $stdout `
    -RedirectStandardError $stderr `
    -PassThru
[System.IO.File]::WriteAllText($pidPath, [string]$process.Id)

$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if ($process.HasExited) {
        $detail = "后台服务提前退出（代码 $($process.ExitCode)）。"
        if (Test-Path -LiteralPath $stderr) {
            $tail = (Get-Content -LiteralPath $stderr -Tail 12 -ErrorAction SilentlyContinue) -join [Environment]::NewLine
            if ($tail) { $detail += "`n`n$tail" }
        }
        [System.Windows.Forms.MessageBox]::Show(
            "$detail`n`n日志目录：$logDirectory",
            "幻兽商域启动失败",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        exit 1
    }
    if (Test-Health) {
        Start-Process $Url
        exit 0
    }
}

[System.Windows.Forms.MessageBox]::Show(
    "后台服务在 40 秒内没有就绪。请确认 5180 端口未被其他程序占用，然后查看日志：`n$logDirectory",
    "幻兽商域启动超时",
    [System.Windows.Forms.MessageBoxButtons]::OK,
    [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
exit 1
