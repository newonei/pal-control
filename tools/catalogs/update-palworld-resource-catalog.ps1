[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\..\services\control-api\Resources\palworld-resource-catalog.json")
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Set-StrictMode -Version Latest

function Decode-JsonString {
    param([Parameter(Mandatory)][string]$Value)
    return ('"' + $Value + '"') | ConvertFrom-Json
}

function Get-Page {
    param([Parameter(Mandatory)][string]$Uri)
    return (Invoke-WebRequest -UseBasicParsing -Uri $Uri).Content
}

$itemsUrl = "https://api.paldeck.cc/items"
$palsUrl = "https://api.paldeck.cc/pals"
$technologyUrl = "https://api.paldeck.cc/technology"
$palNamesUrl = "https://raw.githubusercontent.com/tylercamp/palcalc/main/PalCalc.Model/db.json"
$itemChineseNamesUrl = "https://wiki.biligame.com/palworld/api.php?action=query&prop=revisions&rvprop=content&rvslots=main&titles=MediaWiki%3AItem.json&format=json&formatversion=2"

$itemsHtml = Get-Page $itemsUrl
$palsHtml = Get-Page $palsUrl
$technologyHtml = Get-Page $technologyUrl
$palNamesDatabase = (Get-Page $palNamesUrl) | ConvertFrom-Json
$itemChineseNamesResponse = (Get-Page $itemChineseNamesUrl) | ConvertFrom-Json
$itemChineseNamesDatabase = $itemChineseNamesResponse.query.pages[0].revisions[0].slots.main.content | ConvertFrom-Json
$itemChineseNames = @{}
$itemNameProperty = Decode-JsonString '\u540d\u79f0'
foreach ($property in @($itemChineseNamesDatabase.PSObject.Properties)) {
    $chineseName = [string]$property.Value.PSObject.Properties[$itemNameProperty].Value
    if (-not [string]::IsNullOrWhiteSpace($property.Name) -and
        -not [string]::IsNullOrWhiteSpace($chineseName) -and
        $chineseName -ne "zh-hans text") {
        $itemChineseNames[$property.Name] = $chineseName
    }
}

$palChineseNames = @{}
foreach ($pal in @($palNamesDatabase.Pals)) {
    $internalName = [string]$pal.InternalName
    $chineseName = [string]$pal.LocalizedNames."zh-Hans"
    if (-not [string]::IsNullOrWhiteSpace($internalName) -and -not [string]::IsNullOrWhiteSpace($chineseName)) {
        $palChineseNames[$internalName] = $chineseName
    }
}

# PalCalc intentionally excludes a few unavailable/internal variants. Reuse the
# official species name for tower variants and keep one current Paldeck entry.
$palChineseNames["GrassPanda_Electric_Tower"] = $palChineseNames["GrassPanda_Electric"]
$palChineseNames["LazyDragon_Electric_Tower"] = $palChineseNames["LazyDragon_Electric"]
$palChineseNames["WorldTreeDragon"] = Decode-JsonString '\u67af\u661f\u9f99'

$itemPattern = '\\"name\\":\\"(?<name>(?:\\\\.|[^\\"])*)\\",\\"description\\":\\"(?<description>(?:\\\\.|[^\\"])*)\\",\\"asset\\":\\"(?<id>(?:\\\\.|[^\\"])*)\\",\\"asset_image\\":\\"(?<image>(?:\\\\.|[^\\"])*)\\",\\"category\\":\\"(?<category>(?:\\\\.|[^\\"])*)\\"'
$items = [regex]::Matches($itemsHtml, $itemPattern) |
    ForEach-Object {
        $id = Decode-JsonString $_.Groups["id"].Value
        $englishName = Decode-JsonString $_.Groups["name"].Value
        $chineseName = if ($itemChineseNames.ContainsKey($id)) { $itemChineseNames[$id] } else { "$(Decode-JsonString '\u672a\u6536\u5f55\u4e2d\u6587\u540d') - $englishName" }
        [pscustomobject][ordered]@{
            id = $id
            name = $chineseName
            englishName = $englishName
            category = Decode-JsonString $_.Groups["category"].Value
        }
    } |
    Group-Object id |
    ForEach-Object { $_.Group[0] } |
    Sort-Object category, name, id

$palPattern = '\\"name\\":\\"(?<name>(?:\\\\.|[^\\"])*)\\",\\"asset_name\\":\\"(?<id>[^\\"]+)\\",\\"dexkey\\":\\"(?<dex>[^\\"]+)\\"'
$pals = [regex]::Matches($palsHtml, $palPattern) |
    ForEach-Object {
        $id = Decode-JsonString $_.Groups["id"].Value
        $englishName = Decode-JsonString $_.Groups["name"].Value
        $chineseName = if ($palChineseNames.ContainsKey($id)) { $palChineseNames[$id] } else { $englishName }
        [pscustomobject][ordered]@{
            id = $id
            name = $chineseName
            englishName = $englishName
            dex = Decode-JsonString $_.Groups["dex"].Value
            category = "Pal"
        }
    } |
    Group-Object id |
    ForEach-Object { $_.Group[0] } |
    Sort-Object dex, name, id

$technologyPattern = '\\"key\\":\\"(?<id>[^\\"]+)\\",\\"name\\":\\"(?<name>(?:\\\\.|[^\\"])*)\\",\\"description\\":\\"(?<description>(?:\\\\.|[^\\"])*)\\",\\"icon\\":\\"(?<icon>[^\\"]+)\\"'
$technologies = [regex]::Matches($technologyHtml, $technologyPattern) |
    ForEach-Object {
        [pscustomobject][ordered]@{
            id = Decode-JsonString $_.Groups["id"].Value
            name = Decode-JsonString $_.Groups["name"].Value
            category = "Technology"
        }
    } |
    Group-Object id |
    ForEach-Object { $_.Group[0] } |
    Sort-Object name, id

$eggElements = [ordered]@{
    Dark = "Dark"
    Dragon = "Dragon"
    Earth = "Ground"
    Electricity = "Electric"
    Fire = "Fire"
    Ice = "Ice"
    Leaf = "Grass"
    Normal = "Neutral"
    Water = "Water"
}
$eggs = foreach ($element in $eggElements.GetEnumerator()) {
    foreach ($size in 1..5) {
        $sizeCode = $size.ToString("00", [Globalization.CultureInfo]::InvariantCulture)
        [pscustomobject][ordered]@{
            id = "PalEgg_$($element.Key)_$sizeCode"
            name = "$($element.Value) Pal Egg - Size $sizeCode"
            category = $element.Value
        }
    }
}

$localizedItems = @($items | Where-Object { $itemChineseNames.ContainsKey($_.id) }).Count
$localizedPals = @($pals | Where-Object { $_.name -ne $_.englishName }).Count
if ($items.Count -lt 1000 -or $pals.Count -lt 150 -or $technologies.Count -lt 400 -or $localizedPals -ne $pals.Count) {
    throw "Catalog extraction returned an unexpectedly small or partially localized result: items=$($items.Count), pals=$($pals.Count), localizedPals=$localizedPals, technologies=$($technologies.Count)."
}

$catalog = [ordered]@{
    schemaVersion = "1"
    revision = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ", [Globalization.CultureInfo]::InvariantCulture)
    generatedAt = (Get-Date).ToUniversalTime().ToString("O", [Globalization.CultureInfo]::InvariantCulture)
    source = [ordered]@{
        name = "Paldeck"
        note = "Versioned reference snapshot. Simplified Chinese item names come from the Palworld Chinese community wiki; Pal names come from the MIT-licensed PalCalc database generated from Palworld game files."
        itemsUrl = $itemsUrl
        palsUrl = $palsUrl
        technologiesUrl = $technologyUrl
        palNamesUrl = $palNamesUrl
        palNamesLicense = "MIT"
        itemChineseNamesUrl = $itemChineseNamesUrl
    }
    coverage = [ordered]@{
        items = "reference-zh-Hans"
        pals = "reference-zh-Hans"
        eggs = "paldefender-documented"
        technologies = "reference"
        templates = "server-verified"
    }
    items = @($items)
    pals = @($pals)
    eggs = @($eggs)
    technologies = @($technologies)
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$directory = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$catalog | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedOutput -Encoding UTF8

[pscustomobject]@{
    OutputPath = $resolvedOutput
    Items = $items.Count
    ChineseItems = $localizedItems
    Pals = $pals.Count
    ChinesePals = $localizedPals
    Eggs = $eggs.Count
    Technologies = $technologies.Count
    Revision = $catalog.revision
}
