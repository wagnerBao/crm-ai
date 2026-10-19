namespace CrmRestApi.Domain;

public sealed record RealEstatePropertyFacts(string PropertyType, string? PropertySubtype = null,
    decimal? Area = null, decimal? PrivateArea = null, decimal? TotalArea = null, decimal? LandArea = null,
    int? Suites = null, int? ParkingSpaces = null, IReadOnlyDictionary<string, bool?>? Features = null);

public sealed record RealEstateSearchCriteria(IReadOnlyCollection<string>? PropertyTypes = null,
    IReadOnlyCollection<string>? PropertySubtypes = null, decimal? MinimumArea = null,
    int? MinimumSuites = null, int? MinimumParkingSpaces = null, IReadOnlyCollection<string>? RequiredFeatures = null);

public static class RealEstatePropertyPolicy
{
    public static IReadOnlyDictionary<string, string[]> Subtypes { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["apartment"] = ["standard", "duplex", "penthouse", "studio", "loft", "garden"],
        ["house"] = ["standard", "townhouse", "gated_community"], ["land"] = ["standard", "condominium_lot"],
        ["commercial"] = ["office", "store", "warehouse", "building"], ["rural"] = ["farm", "smallholding"], ["other"] = []
    };
    public static IReadOnlyCollection<string> FeatureKeys { get; } = Array.AsReadOnly(new[]
    {
        "balcony", "barbecue", "furnished", "air_conditioning", "service_area", "accessible", "elevator",
        "pool", "gym", "party_room", "concierge_24h", "playground"
    });
    public static bool IsSubtype(string propertyType, string? subtype) => subtype is null ||
        Subtypes.TryGetValue(propertyType, out var allowed) && allowed.Contains(subtype, StringComparer.Ordinal);
    public static bool IsCondition(string? value) => value is null or "new" or "under_construction" or "good" or "renovated" or "needs_renovation";
    public static bool IsOccupation(string? value) => value is null or "vacant" or "owner_occupied" or "tenant_occupied";
    public static bool IsFeature(string key) => FeatureKeys.Contains(key, StringComparer.Ordinal);
    public static decimal? ComparableArea(string propertyType, decimal? privateArea, decimal? landArea, decimal? legacyArea)
        => (propertyType is "land" or "rural" ? landArea : privateArea) ?? legacyArea;
    public static bool CriteriaAreValid(RealEstateSearchCriteria criteria)
    {
        var types = criteria.PropertyTypes ?? [];
        var subtypes = criteria.PropertySubtypes ?? [];
        var features = criteria.RequiredFeatures ?? [];
        return types.Count <= 20 && subtypes.Count <= 20 && features.Count <= FeatureKeys.Count &&
            types.Distinct(StringComparer.Ordinal).Count() == types.Count && subtypes.Distinct(StringComparer.Ordinal).Count() == subtypes.Count &&
            features.Distinct(StringComparer.Ordinal).Count() == features.Count && features.All(IsFeature) &&
            subtypes.All(subtype => (types.Count == 0 ? Subtypes.Keys : types).Any(type => IsSubtype(type, subtype))) &&
            criteria.MinimumSuites is not < 0 && criteria.MinimumParkingSpaces is not < 0 &&
            (criteria.MinimumArea is null || criteria.MinimumArea >= 0 && criteria.MinimumArea <= 9999999999999999.99m && criteria.MinimumArea == decimal.Round(criteria.MinimumArea.Value, 2));
    }
    public static bool MatchesCriteria(RealEstatePropertyFacts property, RealEstateSearchCriteria criteria)
    {
        if (!CriteriaAreValid(criteria)) return false;
        if (criteria.PropertyTypes?.Count > 0 && !criteria.PropertyTypes.Contains(property.PropertyType, StringComparer.Ordinal)) return false;
        if (criteria.PropertySubtypes?.Count > 0 && (property.PropertySubtype is null || !criteria.PropertySubtypes.Contains(property.PropertySubtype, StringComparer.Ordinal))) return false;
        if (criteria.MinimumSuites is not null && (property.Suites is null || property.Suites < criteria.MinimumSuites)) return false;
        if (criteria.MinimumParkingSpaces is not null && (property.ParkingSpaces is null || property.ParkingSpaces < criteria.MinimumParkingSpaces)) return false;
        var area = ComparableArea(property.PropertyType, property.PrivateArea, property.LandArea, property.Area);
        if (criteria.MinimumArea is not null && (area is null || area < criteria.MinimumArea)) return false;
        return (criteria.RequiredFeatures ?? []).All(key => property.Features is not null && property.Features.TryGetValue(key, out var value) && value == true);
    }
}
