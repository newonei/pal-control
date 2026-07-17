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

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedPalServerExecutablePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^S-1-[0-9]+(?:-[0-9]+)+$')]
    [string]$ExpectedPalServerProcessSid,

    [Parameter(Mandatory)]
    [ValidateRange(1, 2147483647)]
    [int]$ExpectedPalServerProcessId,

    [Parameter(Mandatory)]
    [ValidateRange(1, [long]::MaxValue)]
    [long]$ExpectedPalServerProcessCreationTimeUtcFileTime,

    [switch]$AllowNoOnlinePlayer,

    [switch]$RawJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

try {
    $canonicalExpectedProcessSid = [Security.Principal.SecurityIdentifier]::new(
        $ExpectedPalServerProcessSid).Value
}
catch {
    throw "ExpectedPalServerProcessSid must be a valid canonical Windows SID."
}
if ($canonicalExpectedProcessSid -cne $ExpectedPalServerProcessSid) {
    throw "ExpectedPalServerProcessSid must be a valid canonical Windows SID."
}
if ($ExpectedPalServerExecutablePath -notmatch `
        '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+[\\/])') {
    throw "ExpectedPalServerExecutablePath must be an absolute path."
}
$expectedPalServerPath = [IO.Path]::GetFullPath(
    $ExpectedPalServerExecutablePath)
if (-not (Test-Path -LiteralPath $expectedPalServerPath -PathType Leaf)) {
    throw "ExpectedPalServerExecutablePath is not an existing file."
}

if ($null -eq ("PalControl.NativePipeProcessIdentity" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PalControl
{
    public static class NativePipeProcessIdentity
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;
        private const int TokenUser = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct SidAndAttributes
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenUserValue
        {
            public SidAndAttributes User;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTimeValue
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }

        public sealed class ServerIdentity
        {
            public uint ProcessId { get; private set; }
            public string ImagePath { get; private set; }
            public string ProcessSid { get; private set; }
            public long CreationTimeUtcFileTime { get; private set; }

            internal ServerIdentity(
                uint processId,
                string imagePath,
                string processSid,
                long creationTimeUtcFileTime)
            {
                ProcessId = processId;
                ImagePath = imagePath;
                ProcessSid = processSid;
                CreationTimeUtcFileTime = creationTimeUtcFileTime;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(
            IntPtr pipe,
            out uint serverProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(
            IntPtr process,
            uint flags,
            StringBuilder executablePath,
            ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(
            IntPtr process,
            out FileTimeValue creationTime,
            out FileTimeValue exitTime,
            out FileTimeValue kernelTime,
            out FileTimeValue userTime);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(
            IntPtr process,
            uint desiredAccess,
            out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            IntPtr token,
            int informationClass,
            IntPtr information,
            int informationLength,
            out int returnLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertSidToStringSid(
            IntPtr sid,
            out IntPtr stringSid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            IntPtr file,
            StringBuilder filePath,
            uint filePathLength,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        public static string GetCanonicalFilePath(IntPtr file)
        {
            var path = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(
                file,
                path,
                (uint)path.Capacity,
                0);
            if (length == 0 || length >= path.Capacity)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var value = path.ToString();
            const string uncPrefix = @"\\?\UNC\";
            const string pathPrefix = @"\\?\";
            if (value.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = @"\\" + value.Substring(uncPrefix.Length);
            }
            else if (value.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(pathPrefix.Length);
            }
            return System.IO.Path.GetFullPath(value);
        }

        public static ServerIdentity GetServerIdentity(IntPtr pipe)
        {
            uint processId;
            if (!GetNamedPipeServerProcessId(pipe, out processId) || processId == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var process = OpenProcess(
                ProcessQueryLimitedInformation,
                false,
                processId);
            if (process == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                var path = new StringBuilder(32768);
                var length = path.Capacity;
                if (!QueryFullProcessImageName(process, 0, path, ref length) || length <= 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                FileTimeValue creationTime;
                FileTimeValue exitTime;
                FileTimeValue kernelTime;
                FileTimeValue userTime;
                if (!GetProcessTimes(
                        process,
                        out creationTime,
                        out exitTime,
                        out kernelTime,
                        out userTime))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                var creationTimeUtcFileTime = unchecked((long)(
                    ((ulong)creationTime.HighDateTime << 32) |
                    creationTime.LowDateTime));
                if (creationTimeUtcFileTime <= 0)
                {
                    throw new InvalidOperationException(
                        "The Named Pipe server process has an invalid creation time.");
                }
                var token = IntPtr.Zero;
                if (!OpenProcessToken(process, TokenQuery, out token) ||
                    token == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                try
                {
                    int required;
                    GetTokenInformation(
                        token,
                        TokenUser,
                        IntPtr.Zero,
                        0,
                        out required);
                    if (required <= 0)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    var userBuffer = Marshal.AllocHGlobal(required);
                    try
                    {
                        if (!GetTokenInformation(
                                token,
                                TokenUser,
                                userBuffer,
                                required,
                                out required))
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        }
                        var user = (TokenUserValue)Marshal.PtrToStructure(
                            userBuffer,
                            typeof(TokenUserValue));
                        var stringSid = IntPtr.Zero;
                        if (user.User.Sid == IntPtr.Zero ||
                            !ConvertSidToStringSid(user.User.Sid, out stringSid) ||
                            stringSid == IntPtr.Zero)
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        }
                        try
                        {
                            var sid = Marshal.PtrToStringUni(stringSid);
                            if (String.IsNullOrWhiteSpace(sid))
                            {
                                throw new InvalidOperationException(
                                    "The Named Pipe server process token has no user SID.");
                            }
                            return new ServerIdentity(
                                processId,
                                System.IO.Path.GetFullPath(path.ToString()),
                                sid,
                                creationTimeUtcFileTime);
                        }
                        finally
                        {
                            LocalFree(stringSid);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(userBuffer);
                    }
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }
    }
}
'@ -ErrorAction Stop
}

$lockPath = Join-Path $PSScriptRoot `
    "..\mods\pal-control-native\dependencies.lock.json"
if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
    throw "Native dependency lock is missing: $lockPath"
}
$lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
$expectedProtocol = ([string]$lock.native.protocolVersion).Trim()
$expectedGameBuild = ([string]$lock.palworldTarget).Trim()
$expectedSteamBuild = ([string]$lock.steamBuild).Trim()
$expectedModVersion = ([string]$lock.native.modVersion).Trim()
$expectedPipeName = ([string]$lock.native.pipeName).Trim()
$expectedExecutableSha256 = `
    ([string]$lock.palServerExecutable.sha256).Trim().ToLowerInvariant()
$expectedExecutableSize = [long]$lock.palServerExecutable.size
$expectedNativeDllSha256 = `
    ([string]$lock.build.palControlNativeDllSha256).Trim().ToLowerInvariant()
$expectedNativeDllSize = [long]$lock.build.palControlNativeDllSize
$expectedUe4ssDllSha256 = `
    ([string]$lock.ue4ss.runtimeDllSha256).Trim().ToLowerInvariant()
$expectedUe4ssDllSize = [long]$lock.ue4ss.runtimeDllSize
if ($expectedProtocol -ne "1.1" -or
    $lock.native.capabilityStatus -ne "read-only-candidate-unverified" -or
    [bool]$lock.native.writeCapabilities -or
    $PipeName -cne $expectedPipeName -or
    $expectedExecutableSha256 -notmatch '^[0-9a-f]{64}$' -or
    $expectedExecutableSize -le 0 -or
    $expectedNativeDllSha256 -notmatch '^[0-9a-f]{64}$' -or
    $expectedNativeDllSize -le 0 -or
    $expectedUe4ssDllSha256 -notmatch '^[0-9a-f]{64}$' -or
    $expectedUe4ssDllSize -le 0) {
    throw "The repository lock or requested pipe is not the exact protocol 1.1 read-only candidate."
}

$writeCapabilities = @(
    "players.progression.mutate",
    "players.progression.write",
    "inventory.mutate",
    "inventory.write",
    "inventory.consume",
    "inventory.consume.experimental",
    "pals.mutate",
    "pals.write",
    "announcements.overlay.write",
    "announcements.banner.write",
    "ui.notifications.write"
)
$expectedReadOnlyCapabilities = @(
    "bridge.hello",
    "players.probe",
    "players.schema",
    "players.progression.schema",
    "players.progression.probe",
    "players.progression.read",
    "inventory.schema",
    "inventory.probe",
    "inventory.read",
    "pals.schema",
    "pals.probe",
    "pals.read",
    "pals.skills.catalog",
    "announcements.overlay.probe",
    "announcements.banner.probe",
    "ui.notifications.probe"
)
$expectedProbeValues = [ordered]@{
    "ue4ss.unreal_init" = $true
    "engine.tick.registered" = $true
    "pal.adapter.loaded" = $true
    "runtime.executable.sha256" = $true
    "runtime.native_dll.sha256" = $true
    "runtime.ue4ss_dll.sha256" = $true
    "runtime.write_enabled" = $false
}

function Read-ExactBytes {
    param(
        [Parameter(Mandatory)]
        [System.IO.Stream]$Stream,

        [Parameter(Mandatory)]
        [ValidateRange(1, 1048576)]
        [int]$Length,

        [Parameter(Mandatory)]
        [DateTime]$Deadline
    )

    $buffer = [byte[]]::new($Length)
    $offset = 0
    while ($offset -lt $Length) {
        $remaining = $Deadline - [DateTime]::UtcNow
        if ($remaining.TotalMilliseconds -le 0) {
            throw "Native bridge frame read exceeded the absolute probe deadline."
        }
        $cts = [Threading.CancellationTokenSource]::new($remaining)
        try {
            $read = $Stream.ReadAsync(
                $buffer,
                $offset,
                $Length - $offset,
                $cts.Token).GetAwaiter().GetResult()
        }
        catch {
            if ([DateTime]::UtcNow -ge $Deadline -or $cts.IsCancellationRequested) {
                throw "Native bridge frame read exceeded the absolute probe deadline."
            }
            throw
        }
        finally {
            $cts.Dispose()
        }
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
        [System.IO.Stream]$Stream,

        [Parameter(Mandatory)]
        [DateTime]$Deadline
    )

    $lengthBytes = Read-ExactBytes -Stream $Stream -Length 4 -Deadline $Deadline
    $length = [BitConverter]::ToUInt32($lengthBytes, 0)
    if ($length -lt 1 -or $length -gt 1048576) {
        throw "Native bridge returned an invalid frame length: $length."
    }
    $payload = Read-ExactBytes -Stream $Stream -Length ([int]$length) `
        -Deadline $Deadline
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

function Get-LivePlayerSetEvidence {
    param(
        [Parameter(Mandatory)][string]$ProbeOperation,
        [Parameter(Mandatory)][pscustomobject]$Data
    )

    if ($ProbeOperation -notin @(
        "players.probe",
        "players.progression.probe",
        "inventory.probe")) {
        return $null
    }

    $context = "Native '$ProbeOperation' live-player coverage"
    $values = @()
    switch -CaseSensitive ($ProbeOperation) {
        "players.probe" {
            if (-not ($Data.truncated -is [bool]) -or $Data.truncated) {
                throw "$context must contain the complete, untruncated player set."
            }
            $objects = @($Data.objects)
            if (-not (Test-JsonInteger $Data.objectCount) -or
                [long]$Data.objectCount -ne $objects.Count) {
                throw "$context objectCount does not match its complete objects array."
            }
            $values = @($objects | ForEach-Object {
                if ($_ -isnot [pscustomobject] -or
                    $_.identity -isnot [pscustomobject] -or
                    $_.identity.playerUId -isnot [string]) {
                    throw "$context contains an object without a string PlayerUID."
                }
                $_.identity.playerUId
            })
        }
        "players.progression.probe" {
            $players = @($Data.players)
            if (-not (Test-JsonInteger $Data.playerCount) -or
                [long]$Data.playerCount -ne $players.Count) {
                throw "$context playerCount does not match its players array."
            }
            $online = @($players | Where-Object {
                $_ -is [pscustomobject] -and
                $_.online -is [bool] -and $_.online
            })
            $values = @($online | ForEach-Object {
                if ($_.playerUId -isnot [string]) {
                    throw "$context contains an online entry without a string PlayerUID."
                }
                $_.playerUId
            })
        }
        "inventory.probe" {
            if (-not ($Data.truncated -is [bool]) -or $Data.truncated) {
                throw "$context must contain the complete, untruncated inventory set."
            }
            $inventories = @($Data.inventories)
            if (-not (Test-JsonInteger $Data.inventoryObjectCount) -or
                [long]$Data.inventoryObjectCount -ne $inventories.Count) {
                throw "$context inventoryObjectCount does not match its complete inventories array."
            }
            $online = @($inventories | Where-Object {
                $_ -is [pscustomobject] -and
                $_.ownerOnline -is [bool] -and $_.ownerOnline
            })
            if (-not (Test-JsonInteger $Data.onlineInventoryCount) -or
                [long]$Data.onlineInventoryCount -ne $online.Count -or
                -not (Test-JsonInteger $Data.onlinePlayerCount) -or
                [long]$Data.onlinePlayerCount -ne $online.Count) {
                throw "$context online player/inventory counts are not one-to-one."
            }
            $requiredContainers = @("common", "dropSlot", "food")
            foreach ($inventory in $online) {
                if ($inventory.ownerPlayerUId -isnot [string] -or
                    $inventory.containers -isnot [Array]) {
                    throw "$context contains an online inventory without a PlayerUID or container array."
                }
                $containerIds = @()
                foreach ($requiredKind in $requiredContainers) {
                    $matches = @($inventory.containers | Where-Object {
                        $_ -is [pscustomobject] -and
                        $_.kind -is [string] -and
                        $_.kind -ceq $requiredKind
                    })
                    if ($matches.Count -ne 1) {
                        throw "$context must contain exactly one '$requiredKind' container entry."
                    }
                    $container = $matches[0]
                    if ($container.resolved -isnot [bool] -or
                        -not $container.resolved -or
                        $container.containerId -isnot [string] -or
                        $container.containerId -notmatch
                            '^(?!00000000-0000-0000-0000-000000000000$)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$') {
                        throw "$context '$requiredKind' container is unresolved or has a non-canonical containerId."
                    }
                    $containerIds += $container.containerId
                }
                if (@($containerIds | Select-Object -Unique).Count -ne
                    $requiredContainers.Count) {
                    throw "$context common/dropSlot/food containerIds must be distinct."
                }
            }
            $values = @($online | ForEach-Object { $_.ownerPlayerUId })
        }
    }

    if ($values.Count -lt 1) {
        throw "$context contains no online PlayerUID."
    }
    $normalized = @($values | ForEach-Object {
        $value = ([string]$_).Trim().ToLowerInvariant()
        if ($value -notmatch
            '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$') {
            throw "$context contains a non-canonical PlayerUID."
        }
        $value
    } | Sort-Object -CaseSensitive)
    if (@($normalized | Select-Object -Unique).Count -ne $normalized.Count) {
        throw "$context contains a duplicate PlayerUID."
    }
    $canonical = "pal-control-live-player-set-v1`n$($normalized.Count)`n" +
        ($normalized -join "`n")
    return [pscustomobject]@{
        count = $normalized.Count
        sha256 = Get-Sha256Hex -Value $canonical
    }
}

function Get-FileSha256Hex {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedCanonicalPath,
        [Parameter(Mandatory)][long]$ExpectedSize,
        [Parameter(Mandatory)][DateTime]$Deadline
    )

    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read,
        1048576,
        [IO.FileOptions]::Asynchronous -bor [IO.FileOptions]::SequentialScan)
    $sha256 = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $canonicalPath = `
            [PalControl.NativePipeProcessIdentity]::GetCanonicalFilePath(
                $stream.SafeFileHandle.DangerousGetHandle())
        if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
                $canonicalPath,
                $ExpectedCanonicalPath)) {
            throw "Named Pipe server executable file handle did not resolve to the explicitly approved canonical path."
        }
        $size = $stream.Length
        if ($size -ne $ExpectedSize) {
            throw "Named Pipe server executable size does not match the repository lock."
        }
        $buffer = [byte[]]::new(1048576)
        while ($true) {
            $remaining = $Deadline - [DateTime]::UtcNow
            if ($remaining.TotalMilliseconds -le 0) {
                throw "Named Pipe server executable hash exceeded the absolute probe deadline."
            }
            $cts = [Threading.CancellationTokenSource]::new($remaining)
            try {
                $read = $stream.ReadAsync(
                    $buffer,
                    0,
                    $buffer.Length,
                    $cts.Token).GetAwaiter().GetResult()
            }
            catch {
                if ([DateTime]::UtcNow -ge $Deadline -or $cts.IsCancellationRequested) {
                    throw "Named Pipe server executable hash exceeded the absolute probe deadline."
                }
                throw
            }
            finally {
                $cts.Dispose()
            }
            if ($read -eq 0) {
                break
            }
            $sha256.AppendData($buffer, 0, $read)
        }
        if ($stream.Length -ne $size) {
            throw "Named Pipe server executable changed while hashing."
        }
        return [BitConverter]::ToString($sha256.GetHashAndReset()).Replace(
            '-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Test-JsonInteger {
    param([object]$Value)

    return $Value -is [byte] -or $Value -is [sbyte] -or
        $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int32] -or $Value -is [uint32] -or
        $Value -is [int64] -or $Value -is [uint64]
}

function Get-RequiredJsonProperty {
    param(
        [Parameter(Mandatory)][pscustomobject]$Object,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Context
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "$Context is missing required property '$Name'."
    }
    return $property
}

function Assert-NativeProbeData {
    param(
        [Parameter(Mandatory)][string]$ProbeOperation,
        [Parameter(Mandatory)][pscustomobject]$Data
    )

    $context = "Native '$ProbeOperation' result data"
    $require = {
        param([string]$Name)
        return Get-RequiredJsonProperty -Object $Data -Name $Name -Context $context
    }
    $requireTrue = {
        param([string]$Name)
        $value = (& $require $Name).Value
        if (-not ($value -is [bool]) -or $value -ne $true) {
            throw "$context property '$Name' must be true."
        }
    }
    $requireFalse = {
        param([string]$Name)
        $value = (& $require $Name).Value
        if (-not ($value -is [bool]) -or $value -ne $false) {
            throw "$context property '$Name' must be false."
        }
    }
    $requirePositiveInteger = {
        param([string]$Name)
        $value = (& $require $Name).Value
        if (-not (Test-JsonInteger $value) -or [long]$value -le 0) {
            throw "$context property '$Name' must be a positive integer."
        }
        return [long]$value
    }
    $requireArray = {
        param([string]$Name, [bool]$NonEmpty)
        $property = & $require $Name
        $value = $property.Value
        if (-not ($value -is [Array]) -or ($NonEmpty -and $value.Count -lt 1)) {
            $requirement = if ($NonEmpty) { "a non-empty" } else { "an" }
            throw "$context property '$Name' must be $requirement array."
        }
        return $property
    }
    $requireString = {
        param([string]$Name, [string]$Expected)
        $value = (& $require $Name).Value
        if (-not ($value -is [string]) -or [string]::IsNullOrWhiteSpace($value) -or
            ($Expected.Length -gt 0 -and $value -cne $Expected)) {
            throw "$context property '$Name' has an invalid string value."
        }
        return [string]$value
    }

    switch ($ProbeOperation) {
        "players.schema" {
            & $requireString "targetClass" "/Script/Pal.PalPlayerState" | Out-Null
            & $requireTrue "classFound"
            & $requirePositiveInteger "propertyCount" | Out-Null
            & $requireArray "properties" $true | Out-Null
            & $requireArray "identityCandidates" $true | Out-Null
            & $requireArray "candidateFunctions" $false | Out-Null
            & $requireArray "inheritance" $true | Out-Null
        }
        "players.probe" {
            & $requireString "targetClass" "/Script/Pal.PalPlayerState" | Out-Null
            & $requireTrue "classFound"
            $identityMapping = (& $require "identityMapping").Value
            if (-not ($identityMapping -is [pscustomobject])) {
                throw "$context property 'identityMapping' must be an object."
            }
            $ready = Get-RequiredJsonProperty $identityMapping "ready" $context
            if (-not ($ready.Value -is [bool]) -or $ready.Value -ne $true) {
                throw "$context identity mapping is not ready."
            }
            & $requirePositiveInteger "objectCount" | Out-Null
            $objects = @((& $requireArray "objects" $true).Value)
            $identified = @($objects | Where-Object {
                $_ -is [pscustomobject] -and
                $_.PSObject.Properties["identity"] -and
                $_.identity -is [pscustomobject] -and
                $_.identity.PSObject.Properties["playerUId"] -and
                $_.identity.playerUId -is [string] -and
                -not [string]::IsNullOrWhiteSpace($_.identity.playerUId)
            })
            if ($identified.Count -lt 1) {
                throw "$context contains no identified live player object."
            }
        }
        "players.progression.schema" {
            $candidateCount = & $requirePositiveInteger "candidateTypeCount"
            $foundCount = & $requirePositiveInteger "foundTypeCount"
            $missing = @((& $requireArray "missingTypes" $false).Value)
            & $requireArray "types" $true | Out-Null
            if ($foundCount -ne $candidateCount -or $missing.Count -ne 0) {
                throw "$context did not resolve every required progression type."
            }
        }
        "players.progression.probe" {
            & $requireTrue "mappingReady"
            & $requirePositiveInteger "playerCount" | Out-Null
            $players = @((& $requireArray "players" $true).Value)
            $online = @($players | Where-Object {
                $_ -is [pscustomobject] -and
                $_.PSObject.Properties["playerUId"] -and
                $_.playerUId -is [string] -and
                -not [string]::IsNullOrWhiteSpace($_.playerUId) -and
                $_.PSObject.Properties["online"] -and
                $_.online -is [bool] -and $_.online
            })
            if ($online.Count -lt 1) {
                throw "$context contains no identified online player progression snapshot."
            }
        }
        "inventory.schema" {
            & $requirePositiveInteger "typeCount" | Out-Null
            & $requireArray "missingTypes" $false | Out-Null
            & $requireArray "types" $true | Out-Null
            $invoker = (& $require "consumeInvokerProbe").Value
            if (-not ($invoker -is [pscustomobject])) {
                throw "$context property 'consumeInvokerProbe' must be an object."
            }
            foreach ($name in @(
                "ownerClassReady", "inventoryClassReady", "functionResolved",
                "signatureReady")) {
                $property = Get-RequiredJsonProperty $invoker $name $context
                if (-not ($property.Value -is [bool]) -or $property.Value -ne $true) {
                    throw "$context consume invoker '$name' is not ready."
                }
            }
        }
        "inventory.probe" {
            & $requireTrue "mappingReady"
            & $requireTrue "slotMetadataReady"
            & $requirePositiveInteger "inventoryObjectCount" | Out-Null
            & $requirePositiveInteger "onlinePlayerCount" | Out-Null
            & $requirePositiveInteger "onlineInventoryCount" | Out-Null
            & $requireArray "inventories" $true | Out-Null
        }
        "pals.schema" {
            & $requirePositiveInteger "candidateTypeCount" | Out-Null
            & $requirePositiveInteger "foundTypeCount" | Out-Null
            & $requireArray "types" $true | Out-Null
            & $requireArray "globalFunctionMatches" $false | Out-Null
        }
        "pals.probe" {
            & $requireTrue "mappingReady"
            & $requirePositiveInteger "palCount" | Out-Null
            & $requireArray "pals" $true | Out-Null
        }
        "pals.skills.catalog" {
            & $requireString "locale" "zh-Hans" | Out-Null
            & $requirePositiveInteger "activeSkillCount" | Out-Null
            & $requireArray "activeSkills" $true | Out-Null
            & $requirePositiveInteger "passiveSkillCount" | Out-Null
            & $requireArray "passiveSkills" $true | Out-Null
            & $requireString "catalogRevision" "" | Out-Null
        }
        "announcements.overlay.probe" {
            & $requireTrue "ready"
            & $requireFalse "dispatched"
            & $requireString "transport" "reliable-client-rpc" | Out-Null
            & $requireString "function" "/Script/Engine.PlayerController:ClientMessage" | Out-Null
        }
        "announcements.banner.probe" {
            & $requireTrue "ready"
            & $requireFalse "dispatched"
            & $requireString "function" "/Script/Pal.PalGameStateInGame:BroadcastServerNotice" | Out-Null
        }
        "ui.notifications.probe" {
            & $requireTrue "ready"
            & $requireFalse "dispatched"
            & $requireString "mode" "server-native-presets" | Out-Null
            $versions = @((& $requireArray "schemaVersions" $true).Value)
            $audiences = @((& $requireArray "supportedAudiences" $true).Value)
            & $requireArray "supportedPresets" $true | Out-Null
            if ($versions -cnotcontains "1" -or $audiences -cnotcontains "global") {
                throw "$context omits schema version 1 or the global audience."
            }
        }
        default {
            throw "No strict result-data validator exists for Native operation '$ProbeOperation'."
        }
    }
}

$pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
    ".",
    $PipeName,
    [System.IO.Pipes.PipeDirection]::InOut,
    [System.IO.Pipes.PipeOptions]::Asynchronous)
$dispatchState = "not-dispatched"

try {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $pipe.Connect($TimeoutSeconds * 1000)
    $serverIdentity = [PalControl.NativePipeProcessIdentity]::GetServerIdentity(
        $pipe.SafePipeHandle.DangerousGetHandle())
    if ([uint32]$serverIdentity.ProcessId -ne
            [uint32]$ExpectedPalServerProcessId) {
        throw "Named Pipe server process ID is not the explicitly approved process ID."
    }
    if ([long]$serverIdentity.CreationTimeUtcFileTime -ne
            $ExpectedPalServerProcessCreationTimeUtcFileTime) {
        throw "Named Pipe server process creation time is not the explicitly approved creation time."
    }
    $serverImagePath = [IO.Path]::GetFullPath($serverIdentity.ImagePath)
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
            $serverImagePath,
            $expectedPalServerPath)) {
        throw "Named Pipe server process is not running from the explicitly approved PalServer path."
    }
    if (-not [StringComparer]::Ordinal.Equals(
            $serverIdentity.ProcessSid,
            $ExpectedPalServerProcessSid)) {
        throw "Named Pipe server process does not use the explicitly approved account SID."
    }
    if ((Get-FileSha256Hex `
            -Path $serverImagePath `
            -ExpectedCanonicalPath $expectedPalServerPath `
            -ExpectedSize $expectedExecutableSize `
            -Deadline $deadline) -cne $expectedExecutableSha256) {
        throw "Named Pipe server process is not the exact locked PalServer executable."
    }

    $hello = (Read-BridgeFrame -Stream $pipe -Deadline $deadline) |
        ConvertFrom-Json
    if (-not ($hello.messageType -is [string]) -or
        $hello.messageType -cne "hello") {
        throw "Native bridge did not send hello as the first frame."
    }

    if (-not ($hello.protocolVersion -is [string]) -or
        $hello.protocolVersion -cne $expectedProtocol) {
        throw "Native bridge protocol '$($hello.protocolVersion)' is not supported."
    }
    if (-not ($hello.gameBuild -is [string]) -or
        $hello.gameBuild -cne $expectedGameBuild -or
        -not ($hello.steamBuild -is [string]) -or
        $hello.steamBuild -cne $expectedSteamBuild -or
        -not ($hello.modVersion -is [string]) -or
        $hello.modVersion -cne $expectedModVersion -or
        -not ($hello.runtimeExecutableSha256 -is [string]) -or
        $hello.runtimeExecutableSha256 -cne $expectedExecutableSha256 -or
        -not (Test-JsonInteger $hello.runtimeExecutableSize) -or
        [long]$hello.runtimeExecutableSize -ne $expectedExecutableSize -or
        -not ($hello.runtimeNativeDllSha256 -is [string]) -or
        $hello.runtimeNativeDllSha256 -cne $expectedNativeDllSha256 -or
        -not (Test-JsonInteger $hello.runtimeNativeDllSize) -or
        [long]$hello.runtimeNativeDllSize -ne $expectedNativeDllSize -or
        -not ($hello.runtimeUe4ssDllSha256 -is [string]) -or
        $hello.runtimeUe4ssDllSha256 -cne $expectedUe4ssDllSha256 -or
        -not (Test-JsonInteger $hello.runtimeUe4ssDllSize) -or
        [long]$hello.runtimeUe4ssDllSize -ne $expectedUe4ssDllSize -or
        -not ($hello.runtimeIdentityVerified -is [bool]) -or
        $hello.runtimeIdentityVerified -ne $true) {
        throw "Native bridge runtime identity does not match the repository lock."
    }
    if (-not ($hello.writeEnabled -is [bool]) -or $hello.writeEnabled -ne $false) {
        throw "The current compatibility campaign accepts a read-only Native candidate only."
    }
    if (-not ($hello.capabilities -is [Array])) {
        throw "Native bridge capabilities must be a JSON array."
    }
    $capabilities = @($hello.capabilities)
    if ($capabilities.Count -lt 1 -or
        @($capabilities | Where-Object { -not ($_ -is [string]) }).Count -ne 0 -or
        @($capabilities | Where-Object {
            [string]::IsNullOrWhiteSpace($_) -or $_.Length -gt 128 -or
            $_.IndexOfAny([char[]]@(0..31 + 127)) -ge 0
        }).Count -ne 0 -or
        @($capabilities | Where-Object { $_ -in $writeCapabilities }).Count -ne 0 -or
        (($capabilities | Sort-Object -CaseSensitive) -join "`n") -cne
            (($expectedReadOnlyCapabilities | Sort-Object -CaseSensitive) -join "`n")) {
        throw "The read-only Native hello capability set is not the exact reviewed set."
    }
    if (-not ($hello.probes -is [pscustomobject])) {
        throw "Native bridge runtime probes must be a JSON object."
    }
    $actualProbeNames = @($hello.probes.PSObject.Properties.Name)
    if (($actualProbeNames | Sort-Object -CaseSensitive) -join "`n" -cne
        (($expectedProbeValues.Keys | Sort-Object -CaseSensitive) -join "`n")) {
        throw "Native bridge runtime probe keys are not the exact reviewed set."
    }
    foreach ($probeName in $expectedProbeValues.Keys) {
        $probeValue = $hello.probes.PSObject.Properties[$probeName].Value
        if (-not ($probeValue -is [bool]) -or
            $probeValue -ne $expectedProbeValues[$probeName]) {
            throw "Native bridge runtime probe '$probeName' has an invalid value."
        }
    }
    if ($Operation -notin @($hello.capabilities)) {
        throw "Native bridge does not advertise the read-only operation '$Operation'."
    }

    $commandId = [Guid]::NewGuid()
    $payloadJson = "{}"
    $envelope = [ordered]@{
        protocolVersion = $expectedProtocol
        messageType = "command"
        messageId = [Guid]::NewGuid()
        sentAt = [DateTimeOffset]::UtcNow.ToString("o")
        commandId = $commandId
        idempotencyKey = "readonly-probe-$($commandId.ToString('N'))"
        requestHash = Get-Sha256Hex -Value $payloadJson
        serverId = $ServerId
        actorId = "native-bridge-probe"
        operation = $Operation
        deadline = $deadline.ToString("o")
        expectedRevision = 0
        reason = "Read-only compatibility probe"
        payload = @{}
    }
    $requestJson = $envelope | ConvertTo-Json -Depth 10 -Compress
    $requestBytes = [Text.Encoding]::UTF8.GetBytes($requestJson)
    $lengthBytes = [BitConverter]::GetBytes([uint32]$requestBytes.Length)
    $writeBytes = [byte[]]::new($lengthBytes.Length + $requestBytes.Length)
    [Array]::Copy($lengthBytes, 0, $writeBytes, 0, $lengthBytes.Length)
    [Array]::Copy(
        $requestBytes,
        0,
        $writeBytes,
        $lengthBytes.Length,
        $requestBytes.Length)
    $remaining = $deadline - [DateTime]::UtcNow
    if ($remaining.TotalMilliseconds -le 0) {
        throw "Native bridge probe exceeded the absolute deadline before command dispatch."
    }
    # From this point until FlushAsync completes, a failed write is ambiguous:
    # some command bytes may already have reached the server and retry is forbidden.
    $dispatchState = "dispatch-ambiguous"
    $writeCts = [Threading.CancellationTokenSource]::new($remaining)
    try {
        $pipe.WriteAsync(
            $writeBytes,
            0,
            $writeBytes.Length,
            $writeCts.Token).GetAwaiter().GetResult()
        $pipe.FlushAsync($writeCts.Token).GetAwaiter().GetResult()
        $dispatchState = "dispatched"
    }
    finally {
        $writeCts.Dispose()
    }

    $result = $null
    while ($null -eq $result -and [DateTime]::UtcNow -lt $deadline) {
        $messageJson = Read-BridgeFrame -Stream $pipe -Deadline $deadline
        $message = $messageJson | ConvertFrom-Json
        if (-not ($message.protocolVersion -is [string]) -or
            $message.protocolVersion -cne $expectedProtocol) {
            throw "Native bridge returned a frame with a mismatched protocol."
        }
        if (-not ($message.messageType -is [string])) {
            throw "Native bridge returned a frame without a string messageType."
        }
        switch -CaseSensitive ($message.messageType) {
            "hello" {
                throw "Native bridge sent more than one hello on the same connection."
            }
            "heartbeat" {
                continue
            }
            "result" {
                if (-not ($message.commandId -is [string]) -or
                    $message.commandId -cne [string]$commandId) {
                    throw "Native bridge returned a result for another command/session."
                }
                $result = $message
            }
            default {
                throw "Native bridge returned unsupported messageType '$($message.messageType)'."
            }
        }
    }
    if ($null -eq $result) {
        throw "Native bridge did not return '$Operation' within $TimeoutSeconds seconds."
    }
    $errorProperty = $result.PSObject.Properties["error"]
    $dataProperty = $result.PSObject.Properties["data"]
    if (-not ($result.state -is [string]) -or
        $result.state -notin @("succeeded", "failed", "uncertain") -or
        -not (Test-JsonInteger $result.observedRevision) -or
        [long]$result.observedRevision -lt 0 -or
        $null -eq $errorProperty -or
        $null -eq $dataProperty) {
        throw "Native bridge returned a malformed result envelope for '$Operation'."
    }
    if ($result.state -ceq "failed") {
        if ($null -ne $dataProperty.Value -or
            -not ($errorProperty.Value -is [pscustomobject]) -or
            -not ($errorProperty.Value.code -is [string]) -or
            [string]::IsNullOrWhiteSpace($errorProperty.Value.code) -or
            $errorProperty.Value.code.Length -gt 128 -or
            $errorProperty.Value.code.IndexOfAny([char[]]@(0..31 + 127)) -ge 0 -or
            -not ($errorProperty.Value.message -is [string]) -or
            [string]::IsNullOrWhiteSpace($errorProperty.Value.message) -or
            $errorProperty.Value.message.Length -gt 1024 -or
            $errorProperty.Value.message.IndexOfAny([char[]]@(0..31 + 127)) -ge 0) {
            throw "Native bridge returned a malformed failed result for '$Operation'."
        }
        $noOnlineOperations = @(
            "players.probe",
            "players.progression.probe",
            "inventory.probe"
        )
        if (-not $AllowNoOnlinePlayer -or
            $Operation -notin $noOnlineOperations -or
            $errorProperty.Value.code -cne "NO_ONLINE_PLAYER") {
            throw "Native bridge returned error code '$($errorProperty.Value.code)' for '$Operation'."
        }

        $output = [pscustomobject]@{
            serverIdentity = [pscustomobject]@{
                processId = [uint32]$serverIdentity.ProcessId
                creationTimeUtcFileTime =
                    [long]$serverIdentity.CreationTimeUtcFileTime
            }
            hello = $hello
            result = $result
            livePlayerSet = $null
        }
        if ($RawJson) {
            $output | ConvertTo-Json -Depth 100 -Compress
        }
        else {
            $output
        }
        return
    }
    if ($result.state -cne "succeeded" -or
        $null -ne $errorProperty.Value -or
        -not ($dataProperty.Value -is [pscustomobject])) {
        throw "Native bridge returned an uncertain or incomplete result for '$Operation'."
    }
    Assert-NativeProbeData -ProbeOperation $Operation -Data $dataProperty.Value
    $livePlayerSet = Get-LivePlayerSetEvidence `
        -ProbeOperation $Operation `
        -Data $dataProperty.Value
    if ($Operation -eq "inventory.probe") {
        $requiredContainers = @("common", "dropSlot", "food")
        if (-not ($result.data.mappingReady -is [bool]) -or
            -not ($result.data.inventories -is [Array])) {
            throw "Inventory probe returned invalid mapping or inventories JSON types."
        }
        $completeInventories = @($result.data.inventories | Where-Object {
            $inventory = $_
            $inventory.ownerPlayerUId -is [string] -and
            -not [string]::IsNullOrWhiteSpace($inventory.ownerPlayerUId) -and
            $inventory.ownerOnline -is [bool] -and
            $inventory.ownerOnline -eq $true -and
            $inventory.containers -is [Array] -and
            @($requiredContainers | Where-Object {
                $requiredKind = $_
                @($inventory.containers | Where-Object {
                    $_.kind -is [string] -and $_.kind -ceq $requiredKind -and
                    $_.resolved -is [bool] -and $_.resolved -eq $true
                }).Count -eq 1
            }).Count -eq $requiredContainers.Count
        })
        if ($result.data.mappingReady -ne $true -or $completeInventories.Count -lt 1) {
            throw "Inventory probe did not resolve common/dropSlot/food for an identified live player."
        }
    }

    $output = [pscustomobject]@{
        serverIdentity = [pscustomobject]@{
            processId = [uint32]$serverIdentity.ProcessId
            creationTimeUtcFileTime =
                [long]$serverIdentity.CreationTimeUtcFileTime
        }
        hello = $hello
        result = $result
        livePlayerSet = $livePlayerSet
    }
    if ($RawJson) {
        $output | ConvertTo-Json -Depth 100 -Compress
    }
    else {
        $output
    }
}
catch {
    $_.Exception.Data["NativeProbeDispatchState"] = $dispatchState
    throw
}
finally {
    $pipe.Dispose()
}
