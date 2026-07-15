using PalControl.ControlApi.Extraction;

namespace PalControl.ControlApi.Content;

/// <summary>
/// The single deterministic mapping from an immutable economy-content version
/// to its player-visible commerce products.
/// </summary>
public static class EconomyContentProductProjection
{
    public static IReadOnlyList<ShopProductDefinition> Create(EconomyContentVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Definition.Products
            .Select(product => new ShopProductDefinition(
                product.Sku,
                product.DisplayName,
                product.Description,
                product.PriceCurrency,
                EconomyContentRuntimeService.CalculateEffectiveUnitPrice(version, product),
                product.ItemGrants.Select(grant =>
                    new ShopItemGrant(grant.ItemId, grant.Quantity)).ToArray(),
                product.PurchaseLimitPerSeason,
                product.Active,
                product.AvailableFrom,
                product.AvailableUntil,
                product.Category,
                product.Tags,
                product.FeaturedRank,
                product.GlobalStock,
                version.VersionId,
                version.ContentHash))
            .OrderBy(product => product.Sku, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool Matches(
        ShopProductDefinition expected,
        ShopProductDefinition actual) =>
        string.Equals(expected.Sku, actual.Sku, StringComparison.OrdinalIgnoreCase) &&
        expected.DisplayName == actual.DisplayName &&
        expected.Description == actual.Description &&
        expected.PriceCurrency == actual.PriceCurrency &&
        expected.UnitPrice == actual.UnitPrice &&
        expected.ItemGrants.SequenceEqual(actual.ItemGrants) &&
        expected.PurchaseLimitPerSeason == actual.PurchaseLimitPerSeason &&
        expected.Active == actual.Active &&
        expected.AvailableFrom?.ToUniversalTime() == actual.AvailableFrom?.ToUniversalTime() &&
        expected.AvailableUntil?.ToUniversalTime() == actual.AvailableUntil?.ToUniversalTime() &&
        expected.Category == actual.Category &&
        expected.Tags.SequenceEqual(actual.Tags, StringComparer.Ordinal) &&
        expected.FeaturedRank == actual.FeaturedRank &&
        expected.GlobalStock == actual.GlobalStock &&
        expected.ContentVersionId == actual.ContentVersionId &&
        string.Equals(expected.ContentHash, actual.ContentHash, StringComparison.Ordinal);
}
