[CmdletBinding()]
param(
    [switch]$Silent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
$appRoot = Split-Path -Parent $PSScriptRoot
$expectedExecutable = [System.IO.Path]::GetFullPath((Join-Path $appRoot "PalControl.ControlApi.exe"))
$pidPath = Join-Path $appRoot "data\pal-control.pid"
$stopped = $false

if (Test-Path -LiteralPath $pidPath) {
    $processIdText = (Get-Content -LiteralPath $pidPath -Raw).Trim()
    if ($processIdText -match "^\d+$") {
        $process = Get-Process -Id ([int]$processIdText) -ErrorAction SilentlyContinue
        if ($process) {
            $actualPath = $null
            try { $actualPath = $process.Path } catch { }
            if ($actualPath -and
                [System.IO.Path]::GetFullPath($actualPath).Equals(
                    $expectedExecutable,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $process.Id -Force
                $process.WaitForExit(5000)
                $stopped = $true
            }
        }
    }
    Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
}

if ($Silent) {
    exit 0
}
elseif ($stopped) {
    [System.Windows.Forms.MessageBox]::Show("幻兽商域已停止。", "操作完成") | Out-Null
}
else {
    [System.Windows.Forms.MessageBox]::Show("没有发现由本安装目录启动的幻兽商域进程。", "无需停止") | Out-Null
}
