[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9._-]{1,128}$')]
    [string]$PipeName = "pal-control.local.v1",

    [ValidateSet(
        "players.schema",
        "players.probe",
        "players.progression.schema",
        "players.progression.probe",
        "inventory.schema",
        "inventory.probe",
        "pals.schema",
        "pals.probe",
        "pals.skills.catalog",
        "announcements.overlay.probe",
        "announcements.banner.probe",
        "ui.notifications.probe")]
    [string]$Operation = "inventory.schema",

    [ValidatePattern('^[A-Za-z0-9._-]{1,64}$')]
    [string]$ServerId = "local",

    [ValidateRange(3, 30)]
    [int]$TimeoutSeconds = 15,

    [switch]$RawJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Read-ExactBytes {
    param(
        [Parameter(Mandatory)]
        [System.IO.Stream]$Stream,

        [Parameter(Mandatory)]
        [ValidateRange(1, 1048576)]
        [int]$Length
    )

    $buffer = [byte[]]::new($Length)
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Stream.Read($buffer, $offset, $Length - $offset)
        if ($read -le 0) {
            throw "Native bridge disconnected while reading a frame."
        }
        $offset += $read
    }
    return $buffer
}

function Read-BridgeFrame {
    param(
        [Parameter(Mandatory)]
        [System.IO.Stream]$Stream
    )

    $lengthBytes = Read-ExactBytes -Stream $Stream -Length 4
    $length = [BitConverter]::ToUInt32($lengthBytes, 0)
    if ($length -lt 1 -or $length -gt 1048576) {
        throw "Native bridge returned an invalid frame length: $length."
    }
    $payload = Read-ExactBytes -Stream $Stream -Length ([int]$length)
    return [Text.Encoding]::UTF8.GetString($payload)
}

function Get-Sha256Hex {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = [BitConverter]::ToString(
            $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))
        return $hash.Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

$pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
    ".",
    $PipeName,
    [System.IO.Pipes.PipeDirection]::InOut,
    [System.IO.Pipes.PipeOptions]::None)

try {
    $pipe.Connect($TimeoutSeconds * 1000)

    $hello = $null
    while ($null -eq $hello) {
        $message = (Read-BridgeFrame -Stream $pipe) | ConvertFrom-Json
        if ($message.messageType -eq "hello") {
            $hello = $message
        }
    }

    if ($hello.protocolVersion -ne "1.0") {
        throw "Native bridge protocol '$($hello.protocolVersion)' is not supported."
    }
    if ($Operation -notin @($hello.capabilities)) {
        throw "Native bridge does not advertise the read-only operation '$Operation'."
    }

    $commandId = [Guid]::NewGuid()
    $payloadJson = "{}"
    $envelope = [ordered]@{
        protocolVersion = "1.0"
        messageType = "command"
        messageId = [Guid]::NewGuid()
        sentAt = [DateTimeOffset]::UtcNow.ToString("o")
        commandId = $commandId
        idempotencyKey = "readonly-probe-$($commandId.ToString('N'))"
        requestHash = Get-Sha256Hex -Value $payloadJson
        serverId = $ServerId
        actorId = "native-bridge-probe"
        operation = $Operation
        deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds).ToString("o")
        expectedRevision = 0
        reason = "Read-only compatibility probe"
        payload = @{}
    }
    $requestJson = $envelope | ConvertTo-Json -Depth 10 -Compress
    $requestBytes = [Text.Encoding]::UTF8.GetBytes($requestJson)
    $lengthBytes = [BitConverter]::GetBytes([uint32]$requestBytes.Length)
    $pipe.Write($lengthBytes, 0, $lengthBytes.Length)
    $pipe.Write($requestBytes, 0, $requestBytes.Length)
    $pipe.Flush()

    $result = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($null -eq $result -and [DateTime]::UtcNow -lt $deadline) {
        $messageJson = Read-BridgeFrame -Stream $pipe
        $message = $messageJson | ConvertFrom-Json
        if ($message.messageType -eq "result" -and
            [string]$message.commandId -eq [string]$commandId) {
            $result = $message
        }
    }
    if ($null -eq $result) {
        throw "Native bridge did not return '$Operation' within $TimeoutSeconds seconds."
    }

    $output = [pscustomobject]@{
        hello = $hello
        result = $result
    }
    if ($RawJson) {
        $output | ConvertTo-Json -Depth 100 -Compress
    }
    else {
        $output
    }
}
finally {
    $pipe.Dispose()
}
