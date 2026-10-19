using System.Globalization;

namespace CrmRestApi.Domain;

// Deliberately excludes the legacy free-text address/location: neither is a public source.
public sealed record RealEstateLocationData(
    string? StateCode = null, string? CityId = null, string? City = null, string? Neighborhood = null,
    string? PostalCode = null, string? Street = null, string? StreetNumber = null, string? AddressComplement = null,
    decimal? Latitude = null, decimal? Longitude = null, string? MapsPlaceId = null,
    string AddressVisibility = "neighborhood", bool DisclosureAuthorized = false,
    DateTime? AuthorizationExpiresAt = null, DateTime? PortfolioConfirmedAt = null,
    string? Block = null, string? Plot = null, string? CondominiumName = null, string? BuildingName = null);

public sealed record PublicRealEstateLocation(
    string AddressVisibility, string? StateCode = null, string? CityId = null, string? City = null,
    string? Neighborhood = null, string? PostalCode = null, string? Street = null, string? StreetNumber = null,
    string? AddressComplement = null, decimal? Latitude = null, decimal? Longitude = null,
    string? MapsPlaceId = null, string? PublicLocation = null, string? MapsUrl = null,
    string? Block = null, string? Plot = null, string? CondominiumName = null, string? BuildingName = null);

public static class RealEstateLocationPolicy
{
    public static int PrecisionRank(string? value) => value switch
    {
        "city" => 0, "neighborhood" => 1, "street" => 2, "full" => 3, _ => -1
    };

    public static PublicRealEstateLocation Project(RealEstateLocationData source, string? globalPrecision, DateTime utcNow)
    {
        var rank = Math.Min(PrecisionRank(globalPrecision ?? "neighborhood"), PrecisionRank(source.AddressVisibility));
        if (rank < 0 || !source.DisclosureAuthorized || source.PortfolioConfirmedAt is null ||
            source.PortfolioConfirmedAt > utcNow.AddMinutes(1) || source.AuthorizationExpiresAt <= utcNow)
            return new("city");

        var visibility = rank switch { 0 => "city", 1 => "neighborhood", 2 => "street", _ => "full" };
        var city = Clean(source.City);
        var state = Clean(source.StateCode);
        var neighborhood = rank >= 1 ? Clean(source.Neighborhood) : null;
        var street = rank >= 2 ? Clean(source.Street) : null;
        var number = rank >= 3 ? Clean(source.StreetNumber) : null;
        var complement = rank >= 3 ? Clean(source.AddressComplement) : null;
        var postalCode = rank >= 3 ? Clean(source.PostalCode) : null;
        var latitude = rank >= 3 ? source.Latitude : null;
        var longitude = rank >= 3 ? source.Longitude : null;
        var placeId = rank >= 3 ? Clean(source.MapsPlaceId) : null;
        var block = rank >= 3 ? Clean(source.Block) : null;
        var plot = rank >= 3 ? Clean(source.Plot) : null;
        var condominium = rank >= 3 ? Clean(source.CondominiumName) : null;
        var building = rank >= 3 ? Clean(source.BuildingName) : null;
        var address = Join(street, number, complement, block is null ? null : "Bloco " + block,
            plot is null ? null : "Lote " + plot, condominium, building, neighborhood, city, state, postalCode);
        // Without a city or an explicitly authorized exact point, do not generate an ambiguous map link.
        var query = latitude is not null && longitude is not null
            ? latitude.Value.ToString(CultureInfo.InvariantCulture) + "," + longitude.Value.ToString(CultureInfo.InvariantCulture)
            : city is not null ? address : null;
        var mapsUrl = query is null ? null : "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(query);
        if (mapsUrl is not null && placeId is not null)
            mapsUrl += "&query_place_id=" + Uri.EscapeDataString(placeId);
        return new(visibility, state, Clean(source.CityId), city, neighborhood, postalCode, street, number,
            complement, latitude, longitude, placeId, address, mapsUrl, block, plot, condominium, building);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Join(params string?[] values)
    {
        var parts = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return parts.Length == 0 ? null : string.Join(", ", parts);
    }
}
