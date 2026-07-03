namespace CrmAi.Domain;

public sealed record DailyCheckinGoalDto(string Id, string Name, string Period, int Target, string Unit, string Animation, string? ActivityChannel, string? GroupId, string? GroupName, string? PipelineId, string? PipelineName, bool IsActive, int SortOrder, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record DailyCheckinGoalResultDto(string Id, string Name, string Period, int Target, string Unit, string Animation, string? ActivityChannel, string? GroupId, string? GroupName, string? PipelineId, string? PipelineName, int Actual, int Percent, bool Achieved);
public sealed record DailyCheckinGroupDto(string Id, string Name, bool IsActive);
public sealed record DailyCheckinUserDto(string Id, string Name, string? Role, string Initials, bool IsActive, string? GroupId, string? GroupName);
public sealed record DailyCheckinUserScoreDto(DailyCheckinUserDto User, DailyCheckinGroupDto? Group, IReadOnlyCollection<DailyCheckinGoalResultDto> Results, int DailyPercent, int MonthlyPercent);
public sealed record DailyCheckinTotalsDto(int Achieved, int Total, int Percent);
public sealed record DailyCheckinSnapshotDto(DateOnly Date, DateTime UpdatedAt, IReadOnlyCollection<DailyCheckinGoalDto> Goals, IReadOnlyCollection<DailyCheckinGroupDto> Groups, IReadOnlyCollection<DailyCheckinUserDto> Users, IReadOnlyCollection<DailyCheckinUserScoreDto> Scores, DailyCheckinTotalsDto Totals, IReadOnlyCollection<string> VisibleGroupIds, int RotationSeconds, IReadOnlyCollection<string>? ProcessedEventIds = null);
