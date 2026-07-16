namespace PalControl.ControlApi.Infrastructure;

/// <summary>
/// One authoritative set of limits for extraction-zone configuration, content,
/// settlement, map projections and offline calibration evidence.
/// </summary>
public static class ZoneGeometryLimits
{
    public const double MaximumAbsoluteCoordinate = 10_000_000d;
    public const double MaximumRadius = 10_000d;

    public static bool IsValid(double centerX, double centerY, double radius) =>
        double.IsFinite(centerX) && Math.Abs(centerX) <= MaximumAbsoluteCoordinate &&
        double.IsFinite(centerY) && Math.Abs(centerY) <= MaximumAbsoluteCoordinate &&
        double.IsFinite(radius) && radius > 0d && radius <= MaximumRadius;

    public static void Validate(double centerX, double centerY, double radius)
    {
        if (!IsValid(centerX, centerY, radius))
        {
            throw new ArgumentOutOfRangeException(
                nameof(radius),
                $"Zone center must be finite and within +/-{MaximumAbsoluteCoordinate:R}; " +
                $"radius must be finite and in (0, {MaximumRadius:R}].");
        }
    }
}

/// <summary>
/// Authoritative map-plane geometry used by both the live settlement gate and
/// the offline zone-calibration verifier. Keep eligibility in squared-distance
/// form so the boundary comparison is identical in both callers.
/// </summary>
public static class ExtractionZoneGeometry
{
    public const string FormulaId = "pal-control-map-circle-squared-distance-v1";

    public static double DistanceSquared(
        double x,
        double y,
        double centerX,
        double centerY)
    {
        var deltaX = x - centerX;
        var deltaY = y - centerY;
        return deltaX * deltaX + deltaY * deltaY;
    }

    public static double Distance(
        double x,
        double y,
        double centerX,
        double centerY) =>
        Math.Sqrt(DistanceSquared(x, y, centerX, centerY));

    public static bool IsInside(
        double x,
        double y,
        double centerX,
        double centerY,
        double radius) =>
        ZoneGeometryLimits.IsValid(centerX, centerY, radius) &&
        double.IsFinite(x) && double.IsFinite(y) &&
        DistanceSquared(x, y, centerX, centerY) <= radius * radius;
}
