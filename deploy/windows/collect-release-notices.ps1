[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RepoRoot,

    [Parameter(Mandatory)]
    [string]$PublishRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRootPath = [System.IO.Path]::GetFullPath($RepoRoot)
$publishRootPath = [System.IO.Path]::GetFullPath($PublishRoot)
$noticeRoot = [System.IO.Path]::GetFullPath((Join-Path $publishRootPath "third-party"))
$publishPrefix = $publishRootPath.TrimEnd('\') + "\"

if (-not (Test-Path -LiteralPath $publishRootPath -PathType Container)) {
    throw "Published application directory does not exist: $publishRootPath"
}
if (-not $noticeRoot.StartsWith($publishPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to manage a third-party notice path outside the published application: $noticeRoot"
}

function Assert-File {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }
    if ((Get-Item -LiteralPath $Path).Length -eq 0) {
        throw "$Description is empty: $Path"
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)] [string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-Sha512Base64 {
    param([Parameter(Mandatory)] [string]$Path)

    $algorithm = [System.Security.Cryptography.SHA512]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        [Convert]::ToBase64String($algorithm.ComputeHash($stream))
    }
    finally {
        $stream.Dispose()
        $algorithm.Dispose()
    }
}

function Get-RelativeNoticePath {
    param([Parameter(Mandatory)] [string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $noticePrefix = $noticeRoot.TrimEnd('\') + "\"
    if (-not $fullPath.StartsWith($noticePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Notice file escaped the third-party directory: $fullPath"
    }
    $fullPath.Substring($noticePrefix.Length).Replace('\', '/')
}

function Copy-NoticeFile {
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [string]$RelativeDestination
    )

    Assert-File -Path $Source -Description "Third-party source file"
    $destination = [System.IO.Path]::GetFullPath((Join-Path $noticeRoot $RelativeDestination))
    $noticePrefix = $noticeRoot.TrimEnd('\') + "\"
    if (-not $destination.StartsWith($noticePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to copy a notice outside the third-party directory: $RelativeDestination"
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $destination -Force
    $copied = Get-Item -LiteralPath $destination
    [ordered]@{
        path = Get-RelativeNoticePath -Path $destination
        size = $copied.Length
        sha256 = Get-Sha256 -Path $destination
    }
}

function Read-JsonFile {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Description
    )

    Assert-File -Path $Path -Description $Description
    try {
        Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "${Description} is not valid JSON: $Path. $($_.Exception.Message)"
    }
}

function Get-RequiredXmlNode {
    param(
        [Parameter(Mandatory)] [System.Xml.XmlNode]$Parent,
        [Parameter(Mandatory)] [string]$LocalName,
        [Parameter(Mandatory)] [string]$Description
    )

    $node = $Parent.SelectSingleNode("./*[local-name()='$LocalName']")
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "$Description does not declare $LocalName."
    }
    $node
}

function Resolve-PackageFile {
    param(
        [Parameter(Mandatory)] [string]$PackageDirectory,
        [Parameter(Mandatory)] [string]$RelativePath,
        [Parameter(Mandatory)] [string]$Description
    )

    $packageRoot = [System.IO.Path]::GetFullPath($PackageDirectory)
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $packageRoot $RelativePath))
    $packagePrefix = $packageRoot.TrimEnd('\') + "\"
    if (-not $candidate.StartsWith($packagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description points outside its restored package directory: $RelativePath"
    }
    Assert-File -Path $candidate -Description $Description
    $candidate
}

if (Test-Path -LiteralPath $noticeRoot) {
    Remove-Item -LiteralPath $noticeRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null

$depsPath = Join-Path $publishRootPath "PalControl.ControlApi.deps.json"
$runtimeConfigPath = Join-Path $publishRootPath "PalControl.ControlApi.runtimeconfig.json"
$assetsPath = Join-Path $repoRootPath "services\control-api\obj\project.assets.json"
$packageLockPath = Join-Path $repoRootPath "package-lock.json"
$thirdPartySummarySource = Join-Path $repoRootPath "THIRD_PARTY_NOTICES.md"
$thirdPartySummaryPublished = Join-Path $publishRootPath "THIRD_PARTY_NOTICES.md"

$deps = Read-JsonFile -Path $depsPath -Description "Published dependency manifest"
$runtimeConfig = Read-JsonFile -Path $runtimeConfigPath -Description "Published runtime configuration"
$assets = Read-JsonFile -Path $assetsPath -Description "NuGet restore assets"
Assert-File -Path $packageLockPath -Description "npm lockfile"
Assert-File -Path $thirdPartySummarySource -Description "Repository third-party summary"
Assert-File -Path $thirdPartySummaryPublished -Description "Published third-party summary"
if ((Get-Sha256 -Path $thirdPartySummarySource) -ne (Get-Sha256 -Path $thirdPartySummaryPublished)) {
    throw "The published THIRD_PARTY_NOTICES.md does not match the repository source."
}

if ($null -eq $deps.runtimeTarget -or $deps.runtimeTarget.name -notmatch '/win-x64$') {
    throw "The dependency manifest is not a win-x64 publish: $($deps.runtimeTarget.name)"
}

$expectedNuGet = @(
    [pscustomobject]@{ Id = "Microsoft.Data.Sqlite"; Version = "10.0.9"; LicenseType = "expression"; License = "MIT" },
    [pscustomobject]@{ Id = "Microsoft.Data.Sqlite.Core"; Version = "10.0.9"; LicenseType = "expression"; License = "MIT" },
    [pscustomobject]@{ Id = "SourceGear.sqlite3"; Version = "3.50.4.5"; LicenseType = "file"; License = "LICENSE.txt" },
    [pscustomobject]@{ Id = "SQLitePCLRaw.bundle_e_sqlite3"; Version = "3.0.3"; LicenseType = "expression"; License = "Apache-2.0" },
    [pscustomobject]@{ Id = "SQLitePCLRaw.config.e_sqlite3"; Version = "3.0.3"; LicenseType = "expression"; License = "Apache-2.0" },
    [pscustomobject]@{ Id = "SQLitePCLRaw.core"; Version = "3.0.3"; LicenseType = "expression"; License = "Apache-2.0" },
    [pscustomobject]@{ Id = "SQLitePCLRaw.provider.e_sqlite3"; Version = "3.0.3"; LicenseType = "expression"; License = "Apache-2.0" }
)
$expectedNuGetKeys = @($expectedNuGet | ForEach-Object { "$($_.Id)/$($_.Version)" } | Sort-Object)
$actualNuGetProperties = @($deps.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq "package" })
$actualNuGetKeys = @($actualNuGetProperties | ForEach-Object { $_.Name } | Sort-Object)
$nugetDifference = @(Compare-Object -ReferenceObject $expectedNuGetKeys -DifferenceObject $actualNuGetKeys)
if ($nugetDifference.Count -ne 0) {
    $details = ($nugetDifference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join "; "
    throw "Published NuGet dependency closure changed. Review licenses and update the release allowlist: $details"
}

$packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
if ($packageFolders.Count -eq 0) {
    throw "NuGet restore assets do not declare a global package folder."
}
$assetLibraries = @{}
foreach ($property in @($assets.libraries.PSObject.Properties)) {
    if ($property.Value.type -eq "package") {
        $assetLibraries[$property.Name] = $property.Value
    }
}

$licenseTemplates = @{
    "MIT" = Join-Path $repoRootPath "deploy\windows\license-templates\MIT.txt"
    "Apache-2.0" = Join-Path $repoRootPath "deploy\windows\license-templates\Apache-2.0.txt"
}
foreach ($template in $licenseTemplates.GetEnumerator()) {
    Assert-File -Path $template.Value -Description "$($template.Key) license template"
}

$nugetInventory = @()
foreach ($expected in $expectedNuGet) {
    $key = "$($expected.Id)/$($expected.Version)"
    if (-not $assetLibraries.ContainsKey($key)) {
        throw "NuGet restore assets are missing the published package $key."
    }
    $assetEntry = $assetLibraries[$key]
    $packageDirectory = $null
    foreach ($packageFolder in $packageFolders) {
        $candidate = Join-Path $packageFolder (Join-Path $expected.Id.ToLowerInvariant() $expected.Version.ToLowerInvariant())
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $packageDirectory = [System.IO.Path]::GetFullPath($candidate)
            break
        }
    }
    if ($null -eq $packageDirectory) {
        throw "Restored NuGet package directory is missing for $key."
    }

    $nuspecFiles = @(Get-ChildItem -LiteralPath $packageDirectory -Filter "*.nuspec" -File)
    if ($nuspecFiles.Count -ne 1) {
        throw "Expected exactly one .nuspec for $key, found $($nuspecFiles.Count)."
    }
    # Let XmlDocument honor the encoding declared by the package instead of
    # passing UTF-8 through Windows PowerShell 5.1's locale-dependent reader.
    $nuspec = New-Object System.Xml.XmlDocument
    $nuspec.Load($nuspecFiles[0].FullName)
    $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    if ($null -eq $metadata) {
        throw "NuGet metadata is missing from $($nuspecFiles[0].FullName)."
    }
    $idNode = Get-RequiredXmlNode -Parent $metadata -LocalName "id" -Description "$key metadata"
    $versionNode = Get-RequiredXmlNode -Parent $metadata -LocalName "version" -Description "$key metadata"
    $authorsNode = Get-RequiredXmlNode -Parent $metadata -LocalName "authors" -Description "$key metadata"
    $licenseNode = Get-RequiredXmlNode -Parent $metadata -LocalName "license" -Description "$key metadata"
    if (-not $idNode.InnerText.Equals($expected.Id, [System.StringComparison]::OrdinalIgnoreCase) -or
        $versionNode.InnerText.Trim() -ne $expected.Version) {
        throw "NuGet metadata identity does not match the published dependency $key."
    }
    $actualLicenseType = $licenseNode.Attributes["type"].Value
    $actualLicense = $licenseNode.InnerText.Trim()
    if ($actualLicenseType -ne $expected.LicenseType -or $actualLicense -ne $expected.License) {
        throw "Unknown or changed license for ${key}: expected $($expected.LicenseType) $($expected.License), found $actualLicenseType $actualLicense."
    }

    $packageOutput = "nuget/$($expected.Id)-$($expected.Version)"
    $copiedFiles = @()
    $copiedFiles += Copy-NoticeFile -Source $nuspecFiles[0].FullName -RelativeDestination "$packageOutput/PACKAGE.nuspec"

    if ($actualLicenseType -eq "expression") {
        if (-not $licenseTemplates.ContainsKey($actualLicense)) {
            throw "No reviewed license text is available for NuGet expression $actualLicense ($key)."
        }
        $licenseUrlNode = $metadata.SelectSingleNode("./*[local-name()='licenseUrl']")
        $expectedLicenseUrl = "https://licenses.nuget.org/$actualLicense"
        if ($null -eq $licenseUrlNode -or $licenseUrlNode.InnerText.Trim() -ne $expectedLicenseUrl) {
            throw "NuGet license URL changed for ${key}; expected $expectedLicenseUrl."
        }
        $copiedFiles += Copy-NoticeFile -Source $licenseTemplates[$actualLicense] -RelativeDestination "$packageOutput/LICENSE.txt"
    }
    elseif ($actualLicenseType -eq "file") {
        $packageLicense = Resolve-PackageFile -PackageDirectory $packageDirectory -RelativePath $actualLicense -Description "$key license file"
        $copiedFiles += Copy-NoticeFile -Source $packageLicense -RelativeDestination "$packageOutput/LICENSE.txt"
    }
    else {
        throw "Unsupported NuGet license type for ${key}: $actualLicenseType"
    }

    $readmeNode = $metadata.SelectSingleNode("./*[local-name()='readme']")
    if ($null -ne $readmeNode -and -not [string]::IsNullOrWhiteSpace($readmeNode.InnerText)) {
        $readmeSource = Resolve-PackageFile -PackageDirectory $packageDirectory -RelativePath $readmeNode.InnerText.Trim() -Description "$key package readme"
        $copiedFiles += Copy-NoticeFile -Source $readmeSource -RelativeDestination "$packageOutput/README$([System.IO.Path]::GetExtension($readmeSource))"
    }

    $declaredFiles = @($assetEntry.files)
    $noticePaths = @($declaredFiles | Where-Object {
        [System.IO.Path]::GetFileName($_) -match '^(?i:NOTICE|THIRD[-_ ]PARTY(?:[-_ ]NOTICES?)?)(?:\..+)?$'
    } | Sort-Object -Unique)
    foreach ($noticePath in $noticePaths) {
        $noticeSource = Resolve-PackageFile -PackageDirectory $packageDirectory -RelativePath $noticePath -Description "$key package notice"
        $noticeName = [System.IO.Path]::GetFileName($noticeSource)
        $copiedFiles += Copy-NoticeFile -Source $noticeSource -RelativeDestination "$packageOutput/$noticeName"
    }

    $packageBaseName = "$($expected.Id.ToLowerInvariant()).$($expected.Version.ToLowerInvariant()).nupkg"
    $nupkgPath = Join-Path $packageDirectory $packageBaseName
    $nupkgShaPath = "$nupkgPath.sha512"
    Assert-File -Path $nupkgPath -Description "$key restored package archive"
    Assert-File -Path $nupkgShaPath -Description "$key package SHA-512"
    $recordedPackageSha512 = (Get-Content -LiteralPath $nupkgShaPath -Raw).Trim()
    $actualPackageSha512 = Get-Sha512Base64 -Path $nupkgPath
    if ($recordedPackageSha512 -ne $actualPackageSha512) {
        throw "Restored NuGet package hash validation failed for $key."
    }
    $copiedFiles += Copy-NoticeFile -Source $nupkgShaPath -RelativeDestination "$packageOutput/PACKAGE.nupkg.sha512"

    $copyrightNode = $metadata.SelectSingleNode("./*[local-name()='copyright']")
    $repositoryNode = $metadata.SelectSingleNode("./*[local-name()='repository']")
    $repository = $null
    if ($null -ne $repositoryNode) {
        $repositoryType = $repositoryNode.Attributes.GetNamedItem("type")
        $repositoryUrl = $repositoryNode.Attributes.GetNamedItem("url")
        $repositoryCommit = $repositoryNode.Attributes.GetNamedItem("commit")
        $repository = [ordered]@{
            type = if ($null -ne $repositoryType) { $repositoryType.Value } else { $null }
            url = if ($null -ne $repositoryUrl) { $repositoryUrl.Value } else { $null }
            commit = if ($null -ne $repositoryCommit) { $repositoryCommit.Value } else { $null }
        }
    }

    $nugetInventory += [ordered]@{
        id = $expected.Id
        version = $expected.Version
        authors = $authorsNode.InnerText.Trim()
        copyright = if ($null -ne $copyrightNode) { $copyrightNode.InnerText.Trim() } else { $null }
        license = [ordered]@{
            type = $actualLicenseType
            value = $actualLicense
        }
        repository = $repository
        packageSha512 = $recordedPackageSha512
        restoreContentHash = $assetEntry.sha512
        files = @($copiedFiles)
    }
}

$expectedNpm = @(
    [pscustomobject]@{ Path = "node_modules/react"; Name = "react"; Version = "19.2.7"; License = "MIT" },
    [pscustomobject]@{ Path = "node_modules/react-dom"; Name = "react-dom"; Version = "19.2.7"; License = "MIT" },
    [pscustomobject]@{ Path = "node_modules/scheduler"; Name = "scheduler"; Version = "0.27.0"; License = "MIT" }
)
$nodeCommand = Get-Command "node.exe" -ErrorAction Stop
$nodeScript = @'
const fs = require('fs');
const lock = JSON.parse(fs.readFileSync(process.argv[1], 'utf8'));
const entries = Object.entries(lock.packages || {})
  .filter(([path, value]) => path.startsWith('node_modules/') && !value.dev && !value.link)
  .map(([path, value]) => ({
    path,
    version: value.version || null,
    license: value.license || null,
    resolved: value.resolved || null,
    integrity: value.integrity || null
  }))
  .sort((a, b) => a.path.localeCompare(b.path));
process.stdout.write(JSON.stringify(entries));
'@
$nodeOutput = & $nodeCommand.Source -e $nodeScript $packageLockPath
if ($LASTEXITCODE -ne 0) {
    throw "Node.js could not inspect the npm lockfile."
}
try {
    # Windows PowerShell 5.1 can preserve a JSON array as one pipeline object
    # when ConvertFrom-Json is wrapped directly in @(...). Assign first so the
    # following array subexpression enumerates each dependency entry.
    $parsedNpmLockEntries = $nodeOutput | ConvertFrom-Json
    $npmLockEntries = @($parsedNpmLockEntries)
}
catch {
    throw "Node.js returned invalid npm dependency inventory: $($_.Exception.Message)"
}
$expectedNpmPaths = @($expectedNpm | ForEach-Object { $_.Path } | Sort-Object)
$actualNpmPaths = @($npmLockEntries | ForEach-Object { $_.path } | Sort-Object)
$npmDifference = @(Compare-Object -ReferenceObject $expectedNpmPaths -DifferenceObject $actualNpmPaths)
if ($npmDifference.Count -ne 0) {
    $details = ($npmDifference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join "; "
    throw "Frontend production dependency closure changed. Review licenses and update the release allowlist: $details"
}

$npmInventory = @()
foreach ($expected in $expectedNpm) {
    $lockEntry = @($npmLockEntries | Where-Object { $_.path -eq $expected.Path })[0]
    if ($lockEntry.version -ne $expected.Version -or $lockEntry.license -ne $expected.License -or
        [string]::IsNullOrWhiteSpace($lockEntry.resolved) -or [string]::IsNullOrWhiteSpace($lockEntry.integrity)) {
        throw "Unknown, incomplete, or changed npm metadata for $($expected.Path)."
    }
    $packageDirectory = Join-Path $repoRootPath $expected.Path.Replace('/', '\')
    $packageJsonPath = Join-Path $packageDirectory "package.json"
    $packageJson = Read-JsonFile -Path $packageJsonPath -Description "$($expected.Name) package metadata"
    if ($packageJson.name -ne $expected.Name -or $packageJson.version -ne $expected.Version -or $packageJson.license -ne $expected.License) {
        throw "Installed npm metadata does not match package-lock.json for $($expected.Name)."
    }
    $licensePath = Join-Path $packageDirectory "LICENSE"
    Assert-File -Path $licensePath -Description "$($expected.Name) license"
    $packageOutput = "npm/$($expected.Name)-$($expected.Version)"
    $copiedFiles = @(
        (Copy-NoticeFile -Source $licensePath -RelativeDestination "$packageOutput/LICENSE"),
        (Copy-NoticeFile -Source $packageJsonPath -RelativeDestination "$packageOutput/package.json")
    )
    $npmInventory += [ordered]@{
        name = $expected.Name
        version = $expected.Version
        license = $expected.License
        resolved = $lockEntry.resolved
        integrity = $lockEntry.integrity
        files = $copiedFiles
    }
}

$dotnetCommand = Get-Command "dotnet.exe" -ErrorAction Stop
$dotnetRoot = Split-Path -Parent $dotnetCommand.Source
$dotnetLicensePath = Join-Path $dotnetRoot "LICENSE.txt"
$dotnetNoticesPath = Join-Path $dotnetRoot "ThirdPartyNotices.txt"
Assert-File -Path $dotnetLicensePath -Description ".NET SDK distribution license"
Assert-File -Path $dotnetNoticesPath -Description ".NET SDK distribution third-party notices"
if ((Get-Content -LiteralPath $dotnetNoticesPath -Raw) -notmatch '\.NET Runtime uses third-party') {
    throw "The discovered .NET ThirdPartyNotices.txt does not identify itself as the .NET Runtime notice file."
}
$sdkVersion = (& $dotnetCommand.Source --version | Select-Object -First 1).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdkVersion)) {
    throw "Could not determine the .NET SDK version used for the release."
}
$includedFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
if ($includedFrameworks.Count -eq 0) {
    throw "The self-contained runtime configuration does not list included .NET frameworks."
}
$dotnetSdkFiles = @(
    (Copy-NoticeFile -Source $dotnetLicensePath -RelativeDestination "dotnet/sdk/LICENSE.txt"),
    (Copy-NoticeFile -Source $dotnetNoticesPath -RelativeDestination "dotnet/sdk/ThirdPartyNotices.txt")
)
$frameworkInventory = @($includedFrameworks | ForEach-Object {
    [ordered]@{
        name = $_.name
        version = $_.version
    }
})

# A self-contained publish embeds two runtime packs. Their package-local
# notices are versioned with the exact runtime bits and are therefore copied
# in addition to the SDK distribution's top-level license/notices.
$runtimePackByFramework = @{
    "Microsoft.NETCore.App" = "Microsoft.NETCore.App.Runtime.win-x64"
    "Microsoft.AspNetCore.App" = "Microsoft.AspNetCore.App.Runtime.win-x64"
}
$expectedRuntimePacks = @()
foreach ($framework in $includedFrameworks) {
    if (-not $runtimePackByFramework.ContainsKey($framework.name)) {
        throw "No reviewed win-x64 runtime-pack mapping exists for included framework $($framework.name)."
    }
    $expectedRuntimePacks += [pscustomobject]@{
        Id = $runtimePackByFramework[$framework.name]
        Version = $framework.version
        DependencyKey = "runtimepack.$($runtimePackByFramework[$framework.name])/$($framework.version)"
    }
}
$expectedRuntimePackKeys = @($expectedRuntimePacks | ForEach-Object { $_.DependencyKey } | Sort-Object)
$actualRuntimePackKeys = @($deps.libraries.PSObject.Properties |
    Where-Object { $_.Value.type -eq "runtimepack" } |
    ForEach-Object { $_.Name } |
    Sort-Object)
$runtimePackDifference = @(Compare-Object -ReferenceObject $expectedRuntimePackKeys -DifferenceObject $actualRuntimePackKeys)
if ($runtimePackDifference.Count -ne 0) {
    $details = ($runtimePackDifference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join "; "
    throw "Published .NET runtime-pack closure changed. Review exact runtime notices: $details"
}

$runtimePackInventory = @()
foreach ($expectedPack in $expectedRuntimePacks) {
    $packageDirectory = $null
    foreach ($packageFolder in $packageFolders) {
        $candidate = Join-Path $packageFolder (Join-Path $expectedPack.Id.ToLowerInvariant() $expectedPack.Version.ToLowerInvariant())
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $packageDirectory = [System.IO.Path]::GetFullPath($candidate)
            break
        }
    }
    if ($null -eq $packageDirectory) {
        throw "Restored .NET runtime pack is missing: $($expectedPack.Id)/$($expectedPack.Version)."
    }

    $nuspecFiles = @(Get-ChildItem -LiteralPath $packageDirectory -Filter "*.nuspec" -File)
    if ($nuspecFiles.Count -ne 1) {
        throw "Expected exactly one .nuspec for runtime pack $($expectedPack.Id), found $($nuspecFiles.Count)."
    }
    $nuspec = New-Object System.Xml.XmlDocument
    $nuspec.Load($nuspecFiles[0].FullName)
    $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    if ($null -eq $metadata) {
        throw "Runtime-pack NuGet metadata is missing from $($nuspecFiles[0].FullName)."
    }
    $idNode = Get-RequiredXmlNode -Parent $metadata -LocalName "id" -Description "$($expectedPack.Id) metadata"
    $versionNode = Get-RequiredXmlNode -Parent $metadata -LocalName "version" -Description "$($expectedPack.Id) metadata"
    $authorsNode = Get-RequiredXmlNode -Parent $metadata -LocalName "authors" -Description "$($expectedPack.Id) metadata"
    $licenseNode = Get-RequiredXmlNode -Parent $metadata -LocalName "license" -Description "$($expectedPack.Id) metadata"
    $licenseTypeAttribute = $licenseNode.Attributes.GetNamedItem("type")
    if (-not $idNode.InnerText.Equals($expectedPack.Id, [System.StringComparison]::OrdinalIgnoreCase) -or
        $versionNode.InnerText.Trim() -ne $expectedPack.Version -or
        $null -eq $licenseTypeAttribute -or $licenseTypeAttribute.Value -ne "expression" -or
        $licenseNode.InnerText.Trim() -ne "MIT") {
        throw "Unknown identity or license for .NET runtime pack $($expectedPack.Id)/$($expectedPack.Version)."
    }

    $licenseFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Name -ieq "LICENSE.txt" })
    $noticeFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Name -ieq "THIRD-PARTY-NOTICES.txt" })
    if ($licenseFiles.Count -ne 1 -or $noticeFiles.Count -ne 1) {
        throw "Exact LICENSE.txt or THIRD-PARTY-NOTICES.txt is missing from runtime pack $($expectedPack.Id)."
    }
    Assert-File -Path $licenseFiles[0].FullName -Description "$($expectedPack.Id) exact runtime license"
    Assert-File -Path $noticeFiles[0].FullName -Description "$($expectedPack.Id) exact runtime notices"

    $packageBaseName = "$($expectedPack.Id.ToLowerInvariant()).$($expectedPack.Version.ToLowerInvariant()).nupkg"
    $nupkgPath = Join-Path $packageDirectory $packageBaseName
    $nupkgShaPath = "$nupkgPath.sha512"
    Assert-File -Path $nupkgPath -Description "$($expectedPack.Id) restored runtime archive"
    Assert-File -Path $nupkgShaPath -Description "$($expectedPack.Id) runtime archive SHA-512"
    $recordedPackageSha512 = (Get-Content -LiteralPath $nupkgShaPath -Raw).Trim()
    if ($recordedPackageSha512 -ne (Get-Sha512Base64 -Path $nupkgPath)) {
        throw "Restored .NET runtime-pack hash validation failed for $($expectedPack.Id)."
    }

    $packageOutput = "dotnet/runtime/$($expectedPack.Id)-$($expectedPack.Version)"
    $copiedFiles = @(
        (Copy-NoticeFile -Source $licenseFiles[0].FullName -RelativeDestination "$packageOutput/LICENSE.txt"),
        (Copy-NoticeFile -Source $noticeFiles[0].FullName -RelativeDestination "$packageOutput/THIRD-PARTY-NOTICES.txt"),
        (Copy-NoticeFile -Source $nuspecFiles[0].FullName -RelativeDestination "$packageOutput/PACKAGE.nuspec"),
        (Copy-NoticeFile -Source $nupkgShaPath -RelativeDestination "$packageOutput/PACKAGE.nupkg.sha512")
    )
    $copyrightNode = $metadata.SelectSingleNode("./*[local-name()='copyright']")
    $repositoryNode = $metadata.SelectSingleNode("./*[local-name()='repository']")
    $repository = $null
    if ($null -ne $repositoryNode) {
        $repositoryType = $repositoryNode.Attributes.GetNamedItem("type")
        $repositoryUrl = $repositoryNode.Attributes.GetNamedItem("url")
        $repositoryCommit = $repositoryNode.Attributes.GetNamedItem("commit")
        $repository = [ordered]@{
            type = if ($null -ne $repositoryType) { $repositoryType.Value } else { $null }
            url = if ($null -ne $repositoryUrl) { $repositoryUrl.Value } else { $null }
            commit = if ($null -ne $repositoryCommit) { $repositoryCommit.Value } else { $null }
        }
    }
    $runtimePackInventory += [ordered]@{
        id = $expectedPack.Id
        version = $expectedPack.Version
        authors = $authorsNode.InnerText.Trim()
        copyright = if ($null -ne $copyrightNode) { $copyrightNode.InnerText.Trim() } else { $null }
        license = [ordered]@{ type = "expression"; value = "MIT" }
        repository = $repository
        packageSha512 = $recordedPackageSha512
        files = $copiedFiles
    }
}

$readmeText = @"
This directory is generated by deploy/windows/collect-release-notices.ps1.

inventory.json records the SDK distribution notices, exact self-contained
.NET runtime packs, NuGet packages, and npm production packages included in
this Windows release, plus SHA-256 hashes for every copied license or metadata
file. PACKAGE.nupkg.sha512 files record and verify the hashes of the restored
NuGet archives used for the build.

The repository-level THIRD_PARTY_NOTICES.md remains the human-readable summary.
Do not remove either file from a redistributed binary release.
"@
$readmePath = Join-Path $noticeRoot "README.txt"
[System.IO.File]::WriteAllText($readmePath, $readmeText.Trim() + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
$readmeRecord = [ordered]@{
    path = Get-RelativeNoticePath -Path $readmePath
    size = (Get-Item -LiteralPath $readmePath).Length
    sha256 = Get-Sha256 -Path $readmePath
}

$manifest = [ordered]@{
    schemaVersion = 1
    targetRid = "win-x64"
    sourceInputs = [ordered]@{
        packageLockSha256 = Get-Sha256 -Path $packageLockPath
        projectAssetsSha256 = Get-Sha256 -Path $assetsPath
        publishedDepsSha256 = Get-Sha256 -Path $depsPath
        publishedRuntimeConfigSha256 = Get-Sha256 -Path $runtimeConfigPath
        thirdPartySummarySha256 = Get-Sha256 -Path $thirdPartySummarySource
        reviewedLicenseTexts = @($licenseTemplates.GetEnumerator() | Sort-Object Key | ForEach-Object {
            [ordered]@{
                expression = $_.Key
                sha256 = Get-Sha256 -Path $_.Value
            }
        })
    }
    dotnet = [ordered]@{
        sdkVersion = $sdkVersion
        includedFrameworks = $frameworkInventory
        sdkDistributionFiles = $dotnetSdkFiles
        runtimePacks = $runtimePackInventory
    }
    nuget = $nugetInventory
    npm = $npmInventory
    generatedFiles = @($readmeRecord)
}
$manifestPath = Join-Path $noticeRoot "inventory.json"
$manifestJson = $manifest | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($manifestPath, $manifestJson + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Collected release notices: $($runtimePackInventory.Count) .NET runtime packs, $($nugetInventory.Count) NuGet packages, $($npmInventory.Count) npm packages, SDK $sdkVersion."
