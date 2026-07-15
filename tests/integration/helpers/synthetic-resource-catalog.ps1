function Write-SyntheticResourceCatalog([string] $contentRoot) {
    # The redistributable repository deliberately excludes the generated real
    # game-data catalog. Integration tests use identifiers already present in
    # source code and synthetic labels, so clean clones and CI stay offline and
    # do not redistribute third-party catalog content.
    $itemIds = @(
        "PalSphere", "Baked_Berries", "Herbs", "Medicines", "PalSphere_Mega",
        "RoughBullet", "BowGun", "Arrow", "RepairKit", "Cement", "BerrySeeds",
        "WheatSeeds", "PalSphere_Giga", "HandgunBullet", "AssaultRifleBullet",
        "Wood", "Stone", "Fiber", "Pal_crystal_S", "Leather", "Bone", "Cloth",
        "CopperOre", "CopperIngot", "Coal", "Sulfur", "Quartz", "PalOil", "Polymer",
        "CarbonFiber", "MachineParts2", "PalCrystal_Ex", "AncientParts2", "MeteorDrop",
        "CrudeOil", "Diamond", "Ruby", "Sapphire", "Eemerald", "Horn", "Wool",
        "Cloth2", "Charcoal", "MachineParts", "IronIngot", "StealIngot",
        "Processed_Wood", "HighGrade_Processed_Wood", "Wood_Fine", "Wood_Ancient",
        "BeastBone_Ancient", "AncientParts3", "ManganeseOre", "ManganeseIngot",
        "PalDarkParts", "YakushimaIngot001", "RainbowCrystal", "Wood_WorldTree",
        "NightStone", "WorldTreeOre", "WorldTreeIngot", "PredatorCrystal",
        "SkyIslandOre", "SkyislandIngot", "Thermal_Core", "AIcore"
    )
    $catalog = [ordered]@{
        schemaVersion = "synthetic-test-v1"
        revision = "scheme-a-integration-smoke"
        generatedAt = "2026-01-01T00:00:00Z"
        source = [ordered]@{
            name = "synthetic integration-test fixture"
            note = "Identifiers already referenced by the test target; no game data is redistributed."
            itemsUrl = "https://example.invalid/synthetic-items"
            palsUrl = "https://example.invalid/synthetic-pals"
            technologiesUrl = "https://example.invalid/synthetic-technologies"
        }
        coverage = [ordered]@{
            items = "built-in Scheme A identifiers only"
            pals = "single synthetic validation entry"
            eggs = "none"
            technologies = "none"
            templates = "none"
        }
        items = @($itemIds | ForEach-Object {
            [ordered]@{ id = $_; name = "Synthetic $_"; category = "Item" }
        })
        pals = @([ordered]@{
            id = "SyntheticPal"
            name = "Synthetic Pal"
            category = "Pal"
        })
        eggs = @()
        technologies = @()
    }

    $resourceRoot = Join-Path $contentRoot "Resources"
    New-Item -ItemType Directory -Force -Path $resourceRoot | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $resourceRoot "palworld-resource-catalog.json"),
        ($catalog | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
}
