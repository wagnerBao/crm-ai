using CrmRestApi.Domain;

namespace CrmAi.Domain;

// An explicit public projection: private addresses, parties, documents and editorial
// drafts cannot accidentally enter a prompt through serialization of an entity.
public sealed record RealEstateAnalysisContext(
    string CompanyId,
    int ModuleRevision,
    DateTime SnapshotAt,
    IReadOnlyCollection<RealEstateDemandSnapshot> Demands,
    IReadOnlyCollection<RealEstateOfferSnapshot> Offers,
    IReadOnlyCollection<RealEstateVisitSnapshot> Visits,
    IReadOnlyCollection<RealEstateProposalSnapshot> Proposals,
    IReadOnlyCollection<RealEstateKnowledgeSnapshot> Knowledge);

public sealed record RealEstateDemandSnapshot(
    string Id, string Purpose, string Status, string? PropertyType, string? City,
    string? Neighborhood, decimal? BudgetMin, decimal? BudgetMax, int Revision, DateTime UpdatedAt,
    string? QualificationStatus = null, int? CriteriaVersion = null, int? QualificationRevision = null,
    IReadOnlyCollection<string>? PropertyTypes = null, int? MinimumBedrooms = null, decimal? MinimumArea = null,
    DateTime? QualificationUpdatedAt = null, IReadOnlyCollection<string>? PropertySubtypes = null,
    int? MinimumSuites = null, int? MinimumParkingSpaces = null, IReadOnlyCollection<string>? RequiredFeatures = null);

public sealed record RealEstateOfferSnapshot(
    string Id, string PropertyId, string Code, string PropertyType, string PublicLocation,
    string Purpose, decimal Price, string Currency, decimal? Condominium, decimal? PropertyTax,
    string? PropertyTaxPeriod, string Status, int Revision, DateTime UpdatedAt, DateTime? ConfirmedAt,
    DateTime? ValidUntil, decimal? OtherMonthlyCosts = null, int PropertyRevision = 1,
    PublicRealEstateLocation? Location = null, string? PropertySubtype = null,
    decimal? Area = null, decimal? PrivateArea = null, decimal? TotalArea = null, decimal? LandArea = null,
    int? Bedrooms = null, int? Suites = null, int? ParkingSpaces = null, string? Condition = null, string? Occupation = null,
    IReadOnlyDictionary<string, bool?>? Features = null, decimal? ComparableArea = null);

public sealed record RealEstateVisitSnapshot(
    string Id, string PropertyId, string? OfferId, string Status, DateTime? StartsAt,
    DateTime? EndsAt, string? ResponsibleUserId, string? Feedback, int Revision, DateTime UpdatedAt);

public sealed record RealEstateProposalSnapshot(
    string Id, string PropertyId, string OfferId, string Status, decimal Amount, string Currency,
    DateTime? ValidUntil, int Revision, DateTime UpdatedAt);

public sealed record RealEstateKnowledgeSnapshot(
    string Id, string VersionId, int Version, string Title, string Excerpt, DateTime PublishedAt,
    DateTime? ValidUntil);

public sealed record RealEstateAnalysisEvidence(
    string Id, string Kind, string SourceId, int Revision, DateTime ObservedAt, string Summary,
    bool IsCurrent);

public sealed record RealEstateAnalysisFinding(
    string Kind, string Summary, string Recommendation,
    IReadOnlyCollection<RealEstateAnalysisEvidence> Evidence,
    bool RequiresOperationalRevalidation = true);
