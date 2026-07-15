[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_.:@-]{3,128}$')]
    [string] $Subject,

    [ValidateSet('Viewer', 'Operator', 'EconomyAdmin', 'SeasonAdmin', 'Owner')]
    [string[]] $Roles = @('Owner')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-Base64Url([byte[]] $bytes) {
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function ConvertTo-Base32([byte[]] $bytes) {
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
    $builder = New-Object Text.StringBuilder
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
    if ($bitsLeft -gt 0) {
        [void]$builder.Append($alphabet[($buffer -shl (5 - $bitsLeft)) -band 31])
    }
    return $builder.ToString()
}

$apiKeyBytes = New-Object byte[] 32
$totpBytes = New-Object byte[] 20
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $rng.GetBytes($apiKeyBytes)
    $rng.GetBytes($totpBytes)
}
finally {
    $rng.Dispose()
}

$apiKey = ConvertTo-Base64Url $apiKeyBytes
$totpSecret = ConvertTo-Base32 $totpBytes
$sha256 = [Security.Cryptography.SHA256]::Create()
$apiKeyHashBytes = $null
try {
    $apiKeyHashBytes = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($apiKey))
    $apiKeyHash = -join ($apiKeyHashBytes | ForEach-Object { $_.ToString('x2') })
}
finally {
    $sha256.Dispose()
    [Array]::Clear($apiKeyBytes, 0, $apiKeyBytes.Length)
    [Array]::Clear($totpBytes, 0, $totpBytes.Length)
    if ($apiKeyHashBytes) {
        [Array]::Clear($apiKeyHashBytes, 0, $apiKeyHashBytes.Length)
    }
}

$principal = [ordered]@{
    Subject = $Subject
    ApiKeySha256 = $apiKeyHash
    Roles = @($Roles)
    TotpSecretBase32 = $totpSecret
    Enabled = $true
}

Write-Host ''
Write-Host 'Administrator credentials generated. Save the API key and TOTP secret now; they are shown only once.' `
    -ForegroundColor Yellow
Write-Host ''
Write-Host "API Key:    $apiKey"
Write-Host "TOTP Secret: $totpSecret"
Write-Host ''
Write-Host 'Add this principal to Security:AdminAuthentication:Principals:'
$principal | ConvertTo-Json -Depth 5
