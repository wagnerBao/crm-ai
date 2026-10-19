using CrmAi.Application;
using CrmAi.Domain;
using CrmRestApi.Domain;
using Npgsql;
using System.Text.Json;

namespace CrmAi.Infrastructure.Persistence;

public sealed partial class PostgresOpportunityContextRepository
{
    private static async Task<RealEstateAnalysisContext?> ReadRealEstateAsync(
        NpgsqlConnection connection, Guid opportunityId, string? companyId,
        IReadOnlySet<string> enabled, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(companyId, out var tenant) || !RealEstateAnalysisContextPolicy.ContextKeys.Any(enabled.Contains)) return null;

        // Older installations and non-participating companies keep their original path.
        await using (var installed = new NpgsqlCommand("select to_regclass('company_modules') is not null", connection))
            if (await installed.ExecuteScalarAsync(cancellationToken) is not true) return null;
        await using var gate = new NpgsqlCommand("""
            select modules.revision, coalesce(settings.configuration #>> '{disclosure,addressPrecision}', 'neighborhood') as address_precision,
                   coalesce((settings.configuration #>> '{disclosure,publishIptu}')::boolean, true) as publish_iptu
            from company_modules modules
            join real_estate_settings_versions settings on settings.company_id=modules.company_id and settings.status='published'
            join opportunities opportunity on opportunity.company_id=modules.company_id and opportunity.id=@opportunityId
            where modules.company_id=@companyId and modules.module_key='real_estate' and modules.state='enabled'
            limit 1
            """, connection);
        gate.Parameters.AddWithValue("companyId", tenant);
        gate.Parameters.AddWithValue("opportunityId", opportunityId);
        int moduleRevision;
        string addressPrecision;
        bool publishIptu;
        await using (var reader = await gate.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            moduleRevision = reader.GetInt32(0);
            addressPrecision = reader.GetString(1);
            publishIptu = reader.GetBoolean(2);
        }
        var demands = new List<RealEstateDemandSnapshot>();
        if (enabled.Contains("real_estate_demand"))
        {
            await using var command = Scoped("""
                select demand.id, demand.purpose, demand.status, left(demand.data #>> '{propertyTypes,0}',80) as property_type,
                       left(demand.data->>'city',100) as city, left(demand.data->>'neighborhood',100) as neighborhood,
                       case when jsonb_typeof(demand.data->'budgetMax')='number' then (demand.data->>'budgetMax')::numeric end as budget_max,
                       demand.revision, demand.updated_at, qualification.status as qualification_status,
                       qualification.criteria_version, qualification.revision as qualification_revision,
                       array(select left(value,80) from jsonb_array_elements_text(case when jsonb_typeof(demand.data->'propertyTypes')='array' then demand.data->'propertyTypes' else '[]'::jsonb end) value limit 10) as property_types,
                       case when jsonb_typeof(demand.data->'minimumBedrooms')='number' then (demand.data->>'minimumBedrooms')::integer end as minimum_bedrooms,
                       case when jsonb_typeof(demand.data->'minimumArea')='number' then (demand.data->>'minimumArea')::numeric end as minimum_area,
                       qualification.updated_at as qualification_updated_at,
                       array(select left(value,80) from jsonb_array_elements_text(case when jsonb_typeof(demand.data->'propertySubtypes')='array' then demand.data->'propertySubtypes' else '[]'::jsonb end) value limit 20) as property_subtypes,
                       case when jsonb_typeof(demand.data->'minimumSuites')='number' then (demand.data->>'minimumSuites')::integer end as minimum_suites,
                       case when jsonb_typeof(demand.data->'minimumParkingSpaces')='number' then (demand.data->>'minimumParkingSpaces')::integer end as minimum_parking_spaces,
                       array(select left(value,80) from jsonb_array_elements_text(case when jsonb_typeof(demand.data->'requiredFeatures')='array' then demand.data->'requiredFeatures' else '[]'::jsonb end) value limit 12) as required_features
                from real_estate_demands demand
                left join real_estate_qualifications qualification on qualification.company_id=demand.company_id and qualification.demand_id=demand.id
                where demand.company_id=@companyId and demand.opportunity_id=@opportunityId
                order by demand.updated_at desc,demand.id limit 5
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) demands.Add(new(
                ReadGuid(reader,"id"),reader.GetString(1),reader.GetString(2),ReadNullableString(reader,"property_type"),
                ReadNullableString(reader,"city"),ReadNullableString(reader,"neighborhood"),null,ReadNullableDecimal(reader,"budget_max"),
                reader.GetInt32(7),reader.GetDateTime(8),ReadNullableString(reader,"qualification_status"),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),reader.IsDBNull(11) ? null : reader.GetInt32(11),reader.GetFieldValue<string[]>(12),
                reader.IsDBNull(13) ? null : reader.GetInt32(13),ReadNullableDecimal(reader,"minimum_area"),ReadNullableDateTime(reader,"qualification_updated_at"),
                reader.GetFieldValue<string[]>(reader.GetOrdinal("property_subtypes")),
                reader.IsDBNull(reader.GetOrdinal("minimum_suites")) ? null : reader.GetInt32(reader.GetOrdinal("minimum_suites")),
                reader.IsDBNull(reader.GetOrdinal("minimum_parking_spaces")) ? null : reader.GetInt32(reader.GetOrdinal("minimum_parking_spaces")),
                reader.GetFieldValue<string[]>(reader.GetOrdinal("required_features"))));
        }
        var offers = new List<RealEstateOfferSnapshot>();
        if (enabled.Contains("real_estate_offers"))
        {
            await using var command = Scoped("""
                select offer.id,property.id as property_id,left(property.code,80) as code,left(property.property_type,80) as property_type,
                       left(property.city,100) as city,left(property.neighborhood,100) as neighborhood,
                       offer.purpose,offer.amount,offer.currency,offer.condominium_fee,
                       case when @publishIptu then offer.iptu end as iptu,
                       case when @publishIptu then offer.iptu_periodicity end as iptu_periodicity,
                       offer.status,offer.revision,greatest(offer.updated_at,property.updated_at) as updated_at,
                       property.portfolio_confirmed_at,offer.valid_until,offer.other_monthly_costs,property.revision as property_revision,
                       property.state_code,property.city_id,property.postal_code,property.street,property.street_number,
                       property.address_complement,property.latitude,property.longitude,property.maps_place_id,property.address_visibility,
                       property.disclosure_authorized,property.authorization_expires_at,
                       property.property_subtype,property.private_area,property.total_area,property.land_area,property.suites,
                       property.condition,property.occupation,property.features::text as features,property.area,property.bedrooms,
                       case when jsonb_typeof(property.data->'parkingSpaces')='number' then (property.data->>'parkingSpaces')::integer end as parking_spaces,
                       property.block,property.plot,property.condominium_name,property.building_name
                from real_estate_opportunity_properties link
                join real_estate_offers offer on offer.company_id=link.company_id and offer.id=link.offer_id and offer.property_id=link.property_id
                join real_estate_properties property on property.company_id=offer.company_id and property.id=offer.property_id
                where link.company_id=@companyId and link.opportunity_id=@opportunityId
                  and property.disclosure_authorized and property.status='available'
                  and (property.authorization_expires_at is null or property.authorization_expires_at>now())
                  and (offer.valid_from is null or offer.valid_from<=now())
                  and offer.status<>'draft'
                order by offer.updated_at desc,offer.id limit 10
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var location = RealEstateLocationPolicy.Project(new(
                    StateCode: ReadNullableString(reader,"state_code"), CityId: ReadNullableString(reader,"city_id"),
                    City: ReadNullableString(reader,"city"), Neighborhood: ReadNullableString(reader,"neighborhood"),
                    PostalCode: ReadNullableString(reader,"postal_code"), Street: ReadNullableString(reader,"street"),
                    StreetNumber: ReadNullableString(reader,"street_number"), AddressComplement: ReadNullableString(reader,"address_complement"),
                    Latitude: ReadNullableDecimal(reader,"latitude"), Longitude: ReadNullableDecimal(reader,"longitude"),
                    MapsPlaceId: ReadNullableString(reader,"maps_place_id"), AddressVisibility: reader.GetString(reader.GetOrdinal("address_visibility")),
                    DisclosureAuthorized: reader.GetBoolean(reader.GetOrdinal("disclosure_authorized")),
                    AuthorizationExpiresAt: ReadNullableDateTime(reader,"authorization_expires_at"),
                    PortfolioConfirmedAt: ReadNullableDateTime(reader,"portfolio_confirmed_at"),
                    Block: ReadNullableString(reader,"block"), Plot: ReadNullableString(reader,"plot"),
                    CondominiumName: ReadNullableString(reader,"condominium_name"), BuildingName: ReadNullableString(reader,"building_name")), addressPrecision, DateTime.UtcNow);
                offers.Add(new(
                ReadGuid(reader,"id"),ReadGuid(reader,"property_id"),reader.GetString(2),reader.GetString(3),
                location.PublicLocation ?? string.Empty,
                reader.GetString(6),reader.GetDecimal(7),reader.GetString(8),ReadNullableDecimal(reader,"condominium_fee"),
                ReadNullableDecimal(reader,"iptu"),ReadNullableString(reader,"iptu_periodicity"),reader.GetString(12),reader.GetInt32(13),reader.GetDateTime(14),
                ReadNullableDateTime(reader,"portfolio_confirmed_at"),ReadNullableDateTime(reader,"valid_until"),ReadNullableDecimal(reader,"other_monthly_costs"),reader.GetInt32(18), location,
                PropertySubtype: ReadNullableString(reader,"property_subtype"), Area: ReadNullableDecimal(reader,"area"),
                PrivateArea: ReadNullableDecimal(reader,"private_area"), TotalArea: ReadNullableDecimal(reader,"total_area"), LandArea: ReadNullableDecimal(reader,"land_area"),
                Bedrooms: reader.IsDBNull(reader.GetOrdinal("bedrooms")) ? null : reader.GetInt32(reader.GetOrdinal("bedrooms")),
                Suites: reader.IsDBNull(reader.GetOrdinal("suites")) ? null : reader.GetInt32(reader.GetOrdinal("suites")),
                ParkingSpaces: reader.IsDBNull(reader.GetOrdinal("parking_spaces")) ? null : reader.GetInt32(reader.GetOrdinal("parking_spaces")),
                Condition: ReadNullableString(reader,"condition"), Occupation: ReadNullableString(reader,"occupation"),
                Features: JsonSerializer.Deserialize<Dictionary<string,bool?>>(reader.GetString(reader.GetOrdinal("features")))!
                    .Where(item=>RealEstatePropertyPolicy.FeatureKeys.Contains(item.Key)).ToDictionary(item=>item.Key,item=>item.Value),
                ComparableArea: RealEstatePropertyPolicy.ComparableArea(reader.GetString(3),ReadNullableDecimal(reader,"private_area"),ReadNullableDecimal(reader,"land_area"),ReadNullableDecimal(reader,"area"))));
            }
        }
        var visits = new List<RealEstateVisitSnapshot>();
        if (enabled.Contains("real_estate_visits"))
        {
            await using var command = Scoped("""
                select id,property_id,offer_id,status,starts_at,ends_at,owner_user_id,revision,updated_at
                from real_estate_visits where company_id=@companyId and opportunity_id=@opportunityId
                order by updated_at desc,id limit 10
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) visits.Add(new(
                ReadGuid(reader,"id"),ReadGuid(reader,"property_id"),ReadNullableGuid(reader,"offer_id"),reader.GetString(3),
                ReadNullableDateTime(reader,"starts_at"),ReadNullableDateTime(reader,"ends_at"),ReadNullableGuid(reader,"owner_user_id"),null,reader.GetInt32(7),reader.GetDateTime(8)));
        }
        var proposals = new List<RealEstateProposalSnapshot>();
        if (enabled.Contains("real_estate_proposals"))
        {
            await using var command = Scoped("""
                select id,property_id,offer_id,status,amount,currency,valid_until,revision,updated_at
                from real_estate_proposals where company_id=@companyId and opportunity_id=@opportunityId
                order by updated_at desc,id limit 5
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) proposals.Add(new(
                ReadGuid(reader,"id"),ReadGuid(reader,"property_id"),ReadGuid(reader,"offer_id"),reader.GetString(3),reader.GetDecimal(4),reader.GetString(5),ReadNullableDateTime(reader,"valid_until"),reader.GetInt32(7),reader.GetDateTime(8)));
        }
        var knowledge = new List<RealEstateKnowledgeSnapshot>();
        if (enabled.Contains("company_knowledge"))
        {
            // The public audience is deliberately conservative, even for internal analysis.
            // Text search selects only published excerpts pertinent to this opportunity's purpose.
            await using var command = Scoped("""
                select item.id,version.id as version_id,version.version,left(version.title,160) as title,
                       left(version.content,600) as excerpt,version.published_at,version.effective_until
                from company_knowledge_items item
                join company_knowledge_versions version on version.item_id=item.id and version.company_id=item.company_id and version.version=item.published_version
                where item.company_id=@companyId and item.module_key='real_estate' and item.product_id is null
                  and version.status='published' and version.visibility='customer_safe' and version.published_at<=now()
                  and (version.effective_from is null or version.effective_from<=now())
                  and (version.effective_until is null or version.effective_until>now())
                  and exists(select 1 from real_estate_demands demand where demand.company_id=@companyId and demand.opportunity_id=@opportunityId
                      and version.search_vector @@ plainto_tsquery('portuguese',case demand.purpose when 'rent' then 'locação' else 'venda' end))
                order by version.published_at desc,version.id limit 5
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) knowledge.Add(new(
                ReadGuid(reader,"id"),ReadGuid(reader,"version_id"),reader.GetInt32(2),reader.GetString(3),reader.GetString(4),reader.GetDateTime(5),ReadNullableDateTime(reader,"effective_until")));
        }
        return new(tenant.ToString(),moduleRevision,DateTime.UtcNow,demands,offers,visits,proposals,knowledge);

        NpgsqlCommand Scoped(string sql)
        {
            var command = new NpgsqlCommand(sql,connection);
            command.Parameters.AddWithValue("companyId",tenant);
            command.Parameters.AddWithValue("opportunityId",opportunityId);
            command.Parameters.AddWithValue("publishIptu",publishIptu);
            return command;
        }
    }
}
