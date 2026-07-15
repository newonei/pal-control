[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$appRoot = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $appRoot "appsettings.Local.json"
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

function Find-PalServerRoot {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:PALSERVER_ROOT) {
        $candidates.Add($env:PALSERVER_ROOT)
    }
    $candidates.Add("C:\PalServer")
    $candidates.Add("C:\Palworld\PalServer")
    if (${env:ProgramFiles(x86)}) {
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} "Steam\steamapps\common\PalServer"))
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} "Steam\steamapps\common\Palworld Dedicated Server"))
    }
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate "PalServer.exe"))) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    return ""
}

$initialRoot = Find-PalServerRoot
$initialPort = 8212
$initialPassword = ""
$existingAdminAuthentication = $null
$existing = $null

function Get-ConfigurationProperty {
    param(
        [object]$Value,
        [string]$Name
    )

    if ($Value -is [System.Collections.IDictionary]) {
        if ($Value.Contains($Name)) {
            return [pscustomobject]@{ Found = $true; Value = $Value[$Name] }
        }
        return [pscustomobject]@{ Found = $false; Value = $null }
    }
    if ($null -ne $Value) {
        $property = $Value.PSObject.Properties[$Name]
        if ($null -ne $property) {
            return [pscustomobject]@{ Found = $true; Value = $property.Value }
        }
    }
    return [pscustomobject]@{ Found = $false; Value = $null }
}

function Get-ConfigurationPropertyNames([object]$Value) {
    if ($Value -is [System.Collections.IDictionary]) {
        return @($Value.Keys | ForEach-Object { [string]$_ })
    }
    if ($null -eq $Value) {
        return @()
    }
    return @($Value.PSObject.Properties | ForEach-Object { $_.Name })
}

function Test-ConfigurationObject([object]$Value) {
    return $Value -is [System.Collections.IDictionary] -or
        $Value -is [System.Management.Automation.PSCustomObject]
}

function Merge-ConfigurationObject {
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Defaults,
        [object]$Existing
    )

    $result = [ordered]@{}
    foreach ($key in $Defaults.Keys) {
        $name = [string]$key
        $existingProperty = Get-ConfigurationProperty $Existing $name
        if (-not $existingProperty.Found) {
            $result[$name] = $Defaults[$key]
            continue
        }
        $defaultValue = $Defaults[$key]
        if ($defaultValue -is [System.Collections.IDictionary] -and
            (Test-ConfigurationObject $existingProperty.Value)) {
            $result[$name] = Merge-ConfigurationObject $defaultValue $existingProperty.Value
        }
        else {
            $result[$name] = $existingProperty.Value
        }
    }
    foreach ($name in Get-ConfigurationPropertyNames $Existing) {
        if (-not $result.Contains($name)) {
            $result[$name] = (Get-ConfigurationProperty $Existing $name).Value
        }
    }
    return $result
}

function New-Base32Secret {
    $alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
    $bytes = New-Object byte[] 20
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $builder = New-Object System.Text.StringBuilder
    $buffer = 0
    $bitsLeft = 0
    foreach ($value in $bytes) {
        $buffer = ($buffer -shl 8) -bor [int]$value
        $bitsLeft += 8
        while ($bitsLeft -ge 5) {
            $bitsLeft -= 5
            [void]$builder.Append($alphabet[($buffer -shr $bitsLeft) -band 31])
            if ($bitsLeft -eq 0) {
                $buffer = 0
            }
            else {
                $buffer = $buffer -band ((1 -shl $bitsLeft) - 1)
            }
        }
    }
    [Array]::Clear($bytes, 0, $bytes.Length)
    return $builder.ToString()
}

function New-AdminAuthenticationSettings {
    $random = New-Object byte[] 48
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($random) } finally { $rng.Dispose() }
    $apiKey = [Convert]::ToBase64String($random).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $digest = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $digest.ComputeHash([Text.Encoding]::UTF8.GetBytes($apiKey))
    }
    finally {
        $digest.Dispose()
        [Array]::Clear($random, 0, $random.Length)
    }
    $hashHex = -join ($hash | ForEach-Object { $_.ToString("x2") })
    [Array]::Clear($hash, 0, $hash.Length)
    $totpSecret = New-Base32Secret
    return [pscustomobject]@{
        ApiKey = $apiKey
        TotpSecret = $totpSecret
        Settings = [ordered]@{
            Enabled = $true
            EnableLoopbackDevelopmentPrincipal = $false
            DevelopmentPrincipalSubject = ""
            Principals = @(
                [ordered]@{
                    Subject = "local-owner"
                    ApiKeySha256 = $hashHex
                    Roles = @("Owner")
                    TotpSecretBase32 = $totpSecret
                    Enabled = $true
                }
            )
        }
    }
}

if (Test-Path -LiteralPath $configPath) {
    try {
        $existing = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($existing.Palworld.InstallRoot) {
            $initialRoot = [string]$existing.Palworld.InstallRoot
        }
        if ($existing.Palworld.OfficialRestApi.BaseUrl -match ":(?<port>\d+)(?:/|$)") {
            $initialPort = [int]$Matches.port
        }
        if ($existing.Palworld.OfficialRestApi.Password) {
            $initialPassword = [string]$existing.Palworld.OfficialRestApi.Password
        }
        $securityProperty = $existing.PSObject.Properties["Security"]
        if ($securityProperty -and $securityProperty.Value) {
            $adminProperty = $securityProperty.Value.PSObject.Properties["AdminAuthentication"]
            if ($adminProperty -and $adminProperty.Value) {
                $existingAdminAuthentication = $adminProperty.Value
            }
        }
    }
    catch {
        [System.Windows.Forms.MessageBox]::Show(
            "现有配置文件无法读取。保存新配置前，建议先备份：`n$configPath`n`n$($_.Exception.Message)",
            "幻兽商域配置",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
    }
}

$form = New-Object System.Windows.Forms.Form
$form.Text = "配置幻兽商域"
$form.StartPosition = "CenterScreen"
$form.ClientSize = New-Object System.Drawing.Size(680, 390)
$form.MinimumSize = New-Object System.Drawing.Size(696, 429)
$form.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 9)

$title = New-Object System.Windows.Forms.Label
$title.Text = "基础配置"
$title.Font = New-Object System.Drawing.Font("Microsoft YaHei UI", 17, [System.Drawing.FontStyle]::Bold)
$title.AutoSize = $true
$title.Location = New-Object System.Drawing.Point(28, 22)
$form.Controls.Add($title)

$intro = New-Object System.Windows.Forms.Label
$intro.Text = "选择已经安装好的 Palworld Dedicated Server 目录。首次配置会安全关闭高级适配器；`n再次保存只修改下列基础值，已存在的 PalDefender、玩家门户和 Native 配置会原样保留。"
$intro.AutoSize = $true
$intro.Location = New-Object System.Drawing.Point(31, 66)
$form.Controls.Add($intro)

$rootLabel = New-Object System.Windows.Forms.Label
$rootLabel.Text = "PalServer 目录"
$rootLabel.AutoSize = $true
$rootLabel.Location = New-Object System.Drawing.Point(31, 125)
$form.Controls.Add($rootLabel)

$rootBox = New-Object System.Windows.Forms.TextBox
$rootBox.Text = $initialRoot
$rootBox.Location = New-Object System.Drawing.Point(34, 148)
$rootBox.Size = New-Object System.Drawing.Size(514, 25)
$form.Controls.Add($rootBox)

$browseButton = New-Object System.Windows.Forms.Button
$browseButton.Text = "浏览..."
$browseButton.Location = New-Object System.Drawing.Point(558, 146)
$browseButton.Size = New-Object System.Drawing.Size(86, 29)
$form.Controls.Add($browseButton)

$portLabel = New-Object System.Windows.Forms.Label
$portLabel.Text = "官方 REST 端口"
$portLabel.AutoSize = $true
$portLabel.Location = New-Object System.Drawing.Point(31, 198)
$form.Controls.Add($portLabel)

$portBox = New-Object System.Windows.Forms.NumericUpDown
$portBox.Minimum = 1
$portBox.Maximum = 65535
$portBox.Value = $initialPort
$portBox.Location = New-Object System.Drawing.Point(34, 221)
$portBox.Size = New-Object System.Drawing.Size(150, 25)
$form.Controls.Add($portBox)

$passwordLabel = New-Object System.Windows.Forms.Label
$passwordLabel.Text = "服务器管理员密码（PalWorldSettings.ini 中的 AdminPassword）"
$passwordLabel.AutoSize = $true
$passwordLabel.Location = New-Object System.Drawing.Point(216, 198)
$form.Controls.Add($passwordLabel)

$passwordBox = New-Object System.Windows.Forms.TextBox
$passwordBox.Text = $initialPassword
$passwordBox.UseSystemPasswordChar = $true
$passwordBox.Location = New-Object System.Drawing.Point(219, 221)
$passwordBox.Size = New-Object System.Drawing.Size(425, 25)
$form.Controls.Add($passwordBox)

$showPassword = New-Object System.Windows.Forms.CheckBox
$showPassword.Text = "显示密码"
$showPassword.AutoSize = $true
$showPassword.Location = New-Object System.Drawing.Point(219, 252)
$form.Controls.Add($showPassword)

$notice = New-Object System.Windows.Forms.Label
$notice.Text = "安全提示：5180、8212、17993、25575 端口只应在服务器本机使用，不要做公网端口映射。"
$notice.ForeColor = [System.Drawing.Color]::FromArgb(160, 64, 0)
$notice.AutoSize = $true
$notice.Location = New-Object System.Drawing.Point(31, 294)
$form.Controls.Add($notice)

$saveButton = New-Object System.Windows.Forms.Button
$saveButton.Text = "保存配置"
$saveButton.Location = New-Object System.Drawing.Point(443, 330)
$saveButton.Size = New-Object System.Drawing.Size(96, 34)
$saveButton.BackColor = [System.Drawing.Color]::FromArgb(37, 99, 235)
$saveButton.ForeColor = [System.Drawing.Color]::White
$saveButton.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$form.Controls.Add($saveButton)

$cancelButton = New-Object System.Windows.Forms.Button
$cancelButton.Text = "取消"
$cancelButton.Location = New-Object System.Drawing.Point(548, 330)
$cancelButton.Size = New-Object System.Drawing.Size(96, 34)
$form.Controls.Add($cancelButton)
$form.CancelButton = $cancelButton

$browseButton.Add_Click({
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = "选择包含 PalServer.exe 的目录"
    $dialog.ShowNewFolderButton = $false
    if ($rootBox.Text -and (Test-Path -LiteralPath $rootBox.Text)) {
        $dialog.SelectedPath = $rootBox.Text
    }
    if ($dialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $rootBox.Text = $dialog.SelectedPath
    }
    $dialog.Dispose()
})

$showPassword.Add_CheckedChanged({
    $passwordBox.UseSystemPasswordChar = -not $showPassword.Checked
})

$cancelButton.Add_Click({ $form.Close() })

$saveButton.Add_Click({
    $serverRoot = $rootBox.Text.Trim().TrimEnd('\', '/')
    if ([string]::IsNullOrWhiteSpace($serverRoot)) {
        [System.Windows.Forms.MessageBox]::Show("请选择 PalServer 目录。", "配置未完成") | Out-Null
        return
    }
    if (-not (Test-Path -LiteralPath (Join-Path $serverRoot "PalServer.exe"))) {
        $choice = [System.Windows.Forms.MessageBox]::Show(
            "所选目录中没有找到 PalServer.exe。`n`n仍然保存并以离线模式使用管理台吗？",
            "未找到 Palworld 服务端",
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning)
        if ($choice -ne [System.Windows.Forms.DialogResult]::Yes) {
            return
        }
    }

    $adminAuthentication = $existingAdminAuthentication
    $newAdminCredential = $null
    if (-not $adminAuthentication) {
        $newAdminCredential = New-AdminAuthenticationSettings
        $adminAuthentication = $newAdminCredential.Settings
    }
    $adminAuthentication.EnableLoopbackDevelopmentPrincipal = $false
    $adminAuthentication.DevelopmentPrincipalSubject = ""

    $defaults = [ordered]@{
        Palworld = [ordered]@{
            InstallRoot = $serverRoot
            OfficialRestApi = [ordered]@{
                BaseUrl = "http://127.0.0.1:$([int]$portBox.Value)/v1/api/"
                Username = "admin"
                Password = $passwordBox.Text
            }
            PalDefenderRestApi = [ordered]@{
                Enabled = $false
            }
        }
        SaveManagement = [ordered]@{
            BackupRoot = "backups/savegames"
        }
        CommandPersistence = [ordered]@{
            DataDirectory = "data"
        }
        ExtractionMode = [ordered]@{
            Enabled = $false
            InitialMarketCoin = 0
            InitialSeasonVoucher = 0
            BootstrapPolicyVersion = "installed-v1"
            Persistence = [ordered]@{
                DataDirectory = "data/extraction"
            }
            Continuity = [ordered]@{
                BackupRoot = "backups/economy"
                StagingRoot = "staging/economy"
            }
            Rcon = [ordered]@{
                Enabled = $false
            }
        }
        PlayerPortal = [ordered]@{
            Enabled = $false
            AuthenticationMode = "TrustedGameCode"
            PublicSteam = $false
            CookieSecure = $false
            AllowedOrigins = @("http://127.0.0.1:5180")
        }
        Security = [ordered]@{
            DevelopmentMode = $false
            StartupValidation = [ordered]@{
                Strict = $true
                AllowNonLoopbackListenerBehindTrustedProxy = $false
                TrustedProxyAddresses = @()
                LogDirectory = "logs"
            }
            AllowedOrigins = @("http://127.0.0.1:5180", "http://localhost:5180")
            AdminAuthentication = $adminAuthentication
        }
    }

    $settings = if ($null -ne $existing) {
        Merge-ConfigurationObject $defaults $existing
    }
    else {
        $defaults
    }
    $settings["Palworld"]["InstallRoot"] = $serverRoot
    $settings["Palworld"]["OfficialRestApi"]["BaseUrl"] = "http://127.0.0.1:$([int]$portBox.Value)/v1/api/"
    $settings["Palworld"]["OfficialRestApi"]["Username"] = "admin"
    $settings["Palworld"]["OfficialRestApi"]["Password"] = $passwordBox.Text
    $settings["Security"]["AdminAuthentication"] = $adminAuthentication

    if (Test-Path -LiteralPath $configPath) {
        $backupPath = "$configPath.$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"
        Copy-Item -LiteralPath $configPath -Destination $backupPath -Force
        Set-PrivatePathAcl -Path $backupPath
    }
    $json = $settings | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText(
        $configPath,
        $json + [Environment]::NewLine,
        (New-Object System.Text.UTF8Encoding($false)))
    Set-PrivatePathAcl -Path $configPath

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

    if ($newAdminCredential) {
        $oneTimeText = "API Key: $($newAdminCredential.ApiKey)`r`nTOTP Secret: $($newAdminCredential.TotpSecret)"
        $copyChoice = [System.Windows.Forms.MessageBox]::Show(
            "管理员凭据已生成，只显示这一次。请保存到密码管理器。`n`n$oneTimeText`n`n是否复制到剪贴板？使用后请清空剪贴板。",
            "保存管理员凭据",
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning)
        if ($copyChoice -eq [System.Windows.Forms.DialogResult]::Yes) {
            [System.Windows.Forms.Clipboard]::SetText($oneTimeText)
        }
    }
    else {
        [System.Windows.Forms.MessageBox]::Show(
            "配置已保存。现在可以从开始菜单打开启动幻兽商域。",
            "配置完成",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    }
    $form.Tag = "saved"
    $form.Close()
})

[void]$form.ShowDialog()
$form.Dispose()
