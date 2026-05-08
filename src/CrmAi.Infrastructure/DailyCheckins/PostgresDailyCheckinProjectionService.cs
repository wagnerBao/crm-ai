using System.Text.Json;
using CrmAi.Application;
using CrmAi.Domain;
using Npgsql;
using NpgsqlTypes;

namespace CrmAi.Infrastructure.DailyCheckins;

public sealed class PostgresDailyCheckinProjectionService(NpgsqlDataSource dataSource) : IDailyCheckinProjectionService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task ProjectAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken)
    {
        var deltas = BuildEventDeltas(opportunityEvent).ToArray();
        if (deltas.Length == 0)
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var visibleGroupIds = await ReadVisibleGroupIdsAsync(connection, cancellationToken);
        var snapshotKeys = await GetAffectedSnapshotKeysAsync(connection, visibleGroupIds, deltas, cancellationToken);

        foreach (var key in snapshotKeys.OrderBy(x => x.Date).ThenBy(x => x.GroupId ?? string.Empty))
        {
            var (snapshot, existed) = await GetOrCreateSnapshotAsync(connection, key.Date, key.GroupId, opportunityEvent.EventId, cancellationToken);
            if (!existed || HasProcessedEvent(snapshot, opportunityEvent.EventId))
            {
                continue;
            }

            var updated = ApplyDeltas(snapshot, deltas, opportunityEvent.EventId);
            await UpsertSnapshotAsync(connection, updated, key.GroupId, cancellationToken);
        }
    }

    public async Task GenerateDailySnapshotsAsync(DateOnly date, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var visibleGroupIds = await ReadVisibleGroupIdsAsync(connection, cancellationToken);

        await UpsertSnapshotAsync(connection, await BuildSnapshotAsync(connection, date, null, cancellationToken), null, cancellationToken);
        foreach (var groupId in visibleGroupIds)
        {
            await UpsertSnapshotAsync(connection, await BuildSnapshotAsync(connection, date, groupId, cancellationToken), groupId, cancellationToken);
        }
    }

    private static async Task<DailyCheckinSnapshotDto> BuildSnapshotAsync(NpgsqlConnection connection, DateOnly date, string? groupId, CancellationToken cancellationToken)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var dayEnd = dayStart.AddDays(1);
        var monthStart = new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthEnd = monthStart.AddMonths(1);

        var goals = await ReadGoalsAsync(connection, cancellationToken);
        var groups = await ReadGroupsAsync(connection, cancellationToken);
        var users = await ReadUsersAsync(connection, groupId, cancellationToken);
        var userIds = users.Select(x => x.Id).ToArray();

        var dailyActivities = await CountActivitiesByUserAndChannelAsync(connection, dayStart, dayEnd, userIds, cancellationToken);
        var monthlyActivities = await CountActivitiesByUserAndChannelAsync(connection, monthStart, monthEnd, userIds, cancellationToken);
        var dailyOpportunities = await CountOpportunitiesByUserAsync(connection, dayStart, dayEnd, userIds, cancellationToken);
        var monthlyOpportunities = await CountOpportunitiesByUserAsync(connection, monthStart, monthEnd, userIds, cancellationToken);
        var dailyNotes = await CountNotesByUserAsync(connection, dayStart, dayEnd, userIds, cancellationToken);
        var monthlyNotes = await CountNotesByUserAsync(connection, monthStart, monthEnd, userIds, cancellationToken);
        var visibleGroupIds = await ReadVisibleGroupIdsAsync(connection, cancellationToken);
        var rotationSeconds = await ReadRotationSecondsAsync(connection, cancellationToken);

        var groupsById = groups.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var groupIds = groups.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scores = users
            .Where(user => user.GroupId is null || groupIds.Contains(user.GroupId))
            .Select(user =>
            {
                var results = goals.Select(goal =>
                {
                    var actual = (goal.Unit, goal.Period) switch
                    {
                        ("activity", "daily") => GetActivityCount(dailyActivities, user.Id, goal.ActivityChannel),
                        ("activity", "monthly") => GetActivityCount(monthlyActivities, user.Id, goal.ActivityChannel),
                        ("opportunity", "daily") => dailyOpportunities.GetValueOrDefault(user.Id),
                        ("opportunity", "monthly") => monthlyOpportunities.GetValueOrDefault(user.Id),
                        ("note", "daily") => dailyNotes.GetValueOrDefault(user.Id),
                        ("note", "monthly") => monthlyNotes.GetValueOrDefault(user.Id),
                        _ => 0
                    };
                    var percent = goal.Target > 0 ? (int)Math.Round(actual / (decimal)goal.Target * 100, MidpointRounding.AwayFromZero) : 0;
                    return new DailyCheckinGoalResultDto(goal.Id, goal.Name, goal.Period, goal.Target, goal.Unit, goal.Animation, goal.ActivityChannel, actual, percent, percent >= 100);
                }).ToArray();

                var dailyPercent = AveragePercent(results.Where(x => x.Period == "daily"));
                var monthlyPercent = AveragePercent(results.Where(x => x.Period == "monthly"));
                var group = user.GroupId is not null && groupsById.TryGetValue(user.GroupId, out var foundGroup) ? foundGroup : null;
                return new DailyCheckinUserScoreDto(user, group, results, dailyPercent, monthlyPercent);
            })
            .OrderByDescending(x => x.DailyPercent + x.MonthlyPercent)
            .ThenBy(x => x.User.Name)
            .ToArray();

        var achieved = scores.SelectMany(x => x.Results).Count(x => x.Achieved);
        var total = scores.Length * goals.Count;
        var totals = new DailyCheckinTotalsDto(achieved, total, total > 0 ? (int)Math.Round(achieved / (decimal)total * 100, MidpointRounding.AwayFromZero) : 0);

        return new DailyCheckinSnapshotDto(date, DateTime.UtcNow, goals, groups, users, scores, totals, visibleGroupIds, rotationSeconds, []);
    }

    private static async Task<(DailyCheckinSnapshotDto Snapshot, bool Existed)> GetOrCreateSnapshotAsync(NpgsqlConnection connection, DateOnly date, string? groupId, string eventId, CancellationToken cancellationToken)
    {
        var existing = await ReadSnapshotAsync(connection, date, groupId, cancellationToken);
        if (existing is not null)
        {
            return (existing, true);
        }

        var created = AddProcessedEvent(await BuildSnapshotAsync(connection, date, groupId, cancellationToken), eventId);
        await UpsertSnapshotAsync(connection, created, groupId, cancellationToken);
        return (created, false);
    }

    private static async Task<DailyCheckinSnapshotDto?> ReadSnapshotAsync(NpgsqlConnection connection, DateOnly date, string? groupId, CancellationToken cancellationToken)
    {
        const string sql = """
            select payload_json
            from daily_checkin_snapshots
            where snapshot_date = @snapshotDate
              and group_id is not distinct from @groupId
            order by snapshot_at desc
            limit 1
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("snapshotDate", date);
        command.Parameters.AddWithValue("groupId", string.IsNullOrWhiteSpace(groupId) ? DBNull.Value : Guid.Parse(groupId));
        var payload = await command.ExecuteScalarAsync(cancellationToken);
        return payload is null || payload is DBNull
            ? null
            : JsonSerializer.Deserialize<DailyCheckinSnapshotDto>(payload.ToString() ?? string.Empty, SerializerOptions);
    }

    private static async Task<IReadOnlyCollection<DailyCheckinSnapshotKey>> GetAffectedSnapshotKeysAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<string> visibleGroupIds,
        IReadOnlyCollection<DailyCheckinEventDelta> deltas,
        CancellationToken cancellationToken)
    {
        var keys = new HashSet<DailyCheckinSnapshotKey>();
        foreach (var date in deltas.Select(x => x.Date).Distinct())
        {
            keys.Add(new DailyCheckinSnapshotKey(date, null));
            foreach (var groupId in visibleGroupIds)
            {
                keys.Add(new DailyCheckinSnapshotKey(date, groupId));
            }
        }

        foreach (var month in deltas.Select(x => new DateOnly(x.Date.Year, x.Date.Month, 1)).Distinct())
        {
            foreach (var key in await ReadExistingSnapshotKeysForMonthAsync(connection, month, cancellationToken))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static async Task<IReadOnlyCollection<DailyCheckinSnapshotKey>> ReadExistingSnapshotKeysForMonthAsync(NpgsqlConnection connection, DateOnly month, CancellationToken cancellationToken)
    {
        const string sql = """
            select snapshot_date, group_id
            from daily_checkin_snapshots
            where snapshot_date >= @startsAt
              and snapshot_date < @endsAt
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("startsAt", month);
        command.Parameters.AddWithValue("endsAt", month.AddMonths(1));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var keys = new List<DailyCheckinSnapshotKey>();
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(new DailyCheckinSnapshotKey(
                reader.GetFieldValue<DateOnly>(reader.GetOrdinal("snapshot_date")),
                ReadNullableGuid(reader, "group_id")));
        }

        return keys;
    }

    private static DailyCheckinSnapshotDto ApplyDeltas(DailyCheckinSnapshotDto snapshot, IReadOnlyCollection<DailyCheckinEventDelta> deltas, string eventId)
    {
        var updatedScores = snapshot.Scores
            .Select(score => ApplyDeltasToScore(snapshot.Date, score, deltas.Where(delta => delta.UserId == score.User.Id)))
            .OrderByDescending(x => x.DailyPercent + x.MonthlyPercent)
            .ThenBy(x => x.User.Name)
            .ToArray();

        var achieved = updatedScores.SelectMany(x => x.Results).Count(x => x.Achieved);
        var total = updatedScores.Length * snapshot.Goals.Count;
        var totals = new DailyCheckinTotalsDto(achieved, total, total > 0 ? (int)Math.Round(achieved / (decimal)total * 100, MidpointRounding.AwayFromZero) : 0);

        return AddProcessedEvent(snapshot with
        {
            UpdatedAt = DateTime.UtcNow,
            Scores = updatedScores,
            Totals = totals
        }, eventId);
    }

    private static DailyCheckinUserScoreDto ApplyDeltasToScore(DateOnly snapshotDate, DailyCheckinUserScoreDto score, IEnumerable<DailyCheckinEventDelta> deltas)
    {
        var deltasByGoal = deltas.ToArray();
        if (deltasByGoal.Length == 0)
        {
            return score;
        }

        var results = score.Results.Select(result =>
        {
            var amount = deltasByGoal
                .Where(delta => ShouldApplyDelta(snapshotDate, result, delta))
                .Sum(delta => delta.Amount);

            if (amount == 0)
            {
                return result;
            }

            var actual = Math.Max(0, result.Actual + amount);
            var percent = result.Target > 0 ? (int)Math.Round(actual / (decimal)result.Target * 100, MidpointRounding.AwayFromZero) : 0;
            return result with { Actual = actual, Percent = percent, Achieved = percent >= 100 };
        }).ToArray();

        return score with
        {
            Results = results,
            DailyPercent = AveragePercent(results.Where(x => x.Period == "daily")),
            MonthlyPercent = AveragePercent(results.Where(x => x.Period == "monthly"))
        };
    }

    private static bool ShouldApplyDelta(DateOnly snapshotDate, DailyCheckinGoalResultDto result, DailyCheckinEventDelta delta)
    {
        if (!string.Equals(result.Unit, delta.Unit, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(result.Unit, "activity", StringComparison.OrdinalIgnoreCase) &&
            !ActivityChannelMatches(result.ActivityChannel, delta.ActivityChannel))
        {
            return false;
        }

        return result.Period switch
        {
            "daily" => snapshotDate == delta.Date,
            "monthly" => snapshotDate.Year == delta.Date.Year && snapshotDate.Month == delta.Date.Month,
            _ => false
        };
    }

    private static bool ActivityChannelMatches(string? goalChannel, string? eventChannel)
    {
        if (string.IsNullOrWhiteSpace(goalChannel))
        {
            return true;
        }

        return string.Equals(goalChannel.Trim(), eventChannel?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasProcessedEvent(DailyCheckinSnapshotDto snapshot, string eventId) =>
        (snapshot.ProcessedEventIds ?? []).Contains(eventId, StringComparer.OrdinalIgnoreCase);

    private static DailyCheckinSnapshotDto AddProcessedEvent(DailyCheckinSnapshotDto snapshot, string eventId)
    {
        var processed = (snapshot.ProcessedEventIds ?? [])
            .Append(eventId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .TakeLast(500)
            .ToArray();

        return snapshot with { ProcessedEventIds = processed };
    }

    private static IEnumerable<DailyCheckinEventDelta> BuildEventDeltas(OpportunityEvent opportunityEvent)
    {
        switch (opportunityEvent.Type)
        {
            case "opportunity.created":
                if (!string.IsNullOrWhiteSpace(opportunityEvent.UserId))
                {
                    yield return new DailyCheckinEventDelta(opportunityEvent.UserId, "opportunity", null, DateOnly.FromDateTime(opportunityEvent.OccurredAt.ToUniversalTime()), 1);
                }
                break;

            case "opportunity.note.created":
                {
                    var userId = GetString(opportunityEvent, "authorUserId") ?? opportunityEvent.UserId;
                    if (!string.IsNullOrWhiteSpace(userId))
                    {
                        yield return new DailyCheckinEventDelta(userId, "note", null, DateOnly.FromDateTime(opportunityEvent.OccurredAt.ToUniversalTime()), 1);
                    }
                    break;
                }

            case "opportunity.activity.created":
                {
                    var userId = opportunityEvent.UserId;
                    var status = GetString(opportunityEvent, "status");
                    if (!string.IsNullOrWhiteSpace(userId) && IsDone(status))
                    {
                        yield return new DailyCheckinEventDelta(userId, "activity", GetString(opportunityEvent, "channel"), GetEventDate(opportunityEvent, "dateAt"), 1);
                    }
                    break;
                }

            case "opportunity.activity.updated":
                {
                    var newUserId = opportunityEvent.UserId;
                    var oldUserId = GetString(opportunityEvent, "oldOwnerUserId") ?? newUserId;
                    var newStatus = GetString(opportunityEvent, "newStatus");
                    var oldStatus = GetString(opportunityEvent, "oldStatus");
                    var newChannel = GetString(opportunityEvent, "channel");
                    var oldChannel = GetString(opportunityEvent, "oldChannel") ?? newChannel;
                    var newDate = GetEventDate(opportunityEvent, "dateAt");
                    var oldDate = GetEventDate(opportunityEvent, "oldDateAt", newDate);

                    if (!string.IsNullOrWhiteSpace(oldUserId) && IsDone(oldStatus))
                    {
                        yield return new DailyCheckinEventDelta(oldUserId, "activity", oldChannel, oldDate, -1);
                    }

                    if (!string.IsNullOrWhiteSpace(newUserId) && IsDone(newStatus))
                    {
                        yield return new DailyCheckinEventDelta(newUserId, "activity", newChannel, newDate, 1);
                    }
                    break;
                }
        }
    }

    private static DateOnly GetEventDate(OpportunityEvent opportunityEvent, string key) =>
        GetEventDate(opportunityEvent, key, DateOnly.FromDateTime(opportunityEvent.OccurredAt.ToUniversalTime()));

    private static DateOnly GetEventDate(OpportunityEvent opportunityEvent, string key, DateOnly fallback) =>
        GetDateTime(opportunityEvent, key) is { } dateTime
            ? DateOnly.FromDateTime(dateTime.ToUniversalTime())
            : fallback;

    private static bool IsDone(string? status) => string.Equals(status, "done", StringComparison.OrdinalIgnoreCase);

    private static string? GetString(OpportunityEvent opportunityEvent, string key) =>
        opportunityEvent.Data.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static DateTime? GetDateTime(OpportunityEvent opportunityEvent, string key)
    {
        if (!opportunityEvent.Data.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            DateTime dateTime => dateTime,
            string text when DateTime.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private static async Task UpsertSnapshotAsync(NpgsqlConnection connection, DailyCheckinSnapshotDto snapshot, string? groupId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(snapshot, SerializerOptions);
        var snapshotAt = DateTime.UtcNow;

        const string updateSql = """
            update daily_checkin_snapshots
            set snapshot_at = @snapshotAt,
                payload_json = @payload::jsonb,
                updated_at = @snapshotAt
            where snapshot_date = @snapshotDate
              and group_id is not distinct from @groupId
            """;

        await using (var command = new NpgsqlCommand(updateSql, connection))
        {
            AddSnapshotParameters(command, snapshot.Date, groupId, snapshotAt, payload);
            var updated = await command.ExecuteNonQueryAsync(cancellationToken);
            if (updated > 0)
            {
                return;
            }
        }

        const string insertSql = """
            insert into daily_checkin_snapshots (id, snapshot_date, group_id, snapshot_at, payload_json, created_at, updated_at)
            values (@id, @snapshotDate, @groupId, @snapshotAt, @payload::jsonb, @snapshotAt, @snapshotAt)
            """;

        await using (var command = new NpgsqlCommand(insertSql, connection))
        {
            AddSnapshotParameters(command, snapshot.Date, groupId, snapshotAt, payload);
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void AddSnapshotParameters(NpgsqlCommand command, DateOnly date, string? groupId, DateTime snapshotAt, string payload)
    {
        command.Parameters.AddWithValue("snapshotDate", date);
        command.Parameters.AddWithValue("groupId", string.IsNullOrWhiteSpace(groupId) ? DBNull.Value : Guid.Parse(groupId));
        command.Parameters.AddWithValue("snapshotAt", snapshotAt);
        command.Parameters.AddWithValue("payload", payload);
    }

    private static async Task<IReadOnlyCollection<DailyCheckinGoalDto>> ReadGoalsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            select id, name, period, target, unit, animation, activity_channel, is_active, sort_order, created_at, updated_at
            from daily_checkin_metrics
            where is_active = true
            order by sort_order, name
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var goals = new List<DailyCheckinGoalDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            goals.Add(new DailyCheckinGoalDto(
                ReadGuid(reader, "id"),
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetString(reader.GetOrdinal("period")),
                reader.GetInt32(reader.GetOrdinal("target")),
                reader.GetString(reader.GetOrdinal("unit")),
                reader.GetString(reader.GetOrdinal("animation")),
                ReadNullableString(reader, "activity_channel"),
                reader.GetBoolean(reader.GetOrdinal("is_active")),
                reader.GetInt32(reader.GetOrdinal("sort_order")),
                reader.GetDateTime(reader.GetOrdinal("created_at")),
                reader.GetDateTime(reader.GetOrdinal("updated_at"))));
        }

        return goals;
    }

    private static async Task<IReadOnlyCollection<DailyCheckinGroupDto>> ReadGroupsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = "select id, name, is_active from user_groups where is_active = true order by name";
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var groups = new List<DailyCheckinGroupDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add(new DailyCheckinGroupDto(ReadGuid(reader, "id"), reader.GetString(reader.GetOrdinal("name")), reader.GetBoolean(reader.GetOrdinal("is_active"))));
        }

        return groups;
    }

    private static async Task<IReadOnlyCollection<DailyCheckinUserDto>> ReadUsersAsync(NpgsqlConnection connection, string? groupId, CancellationToken cancellationToken)
    {
        const string sql = """
            select u.id, u.name, u.role, u.initials, u.is_active, u.group_id, g.name as group_name
            from users u
            left join user_groups g on g.id = u.group_id
            where u.is_active = true
              and (@groupId is null or u.group_id = @groupId)
            order by u.name
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("groupId", string.IsNullOrWhiteSpace(groupId) ? DBNull.Value : Guid.Parse(groupId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var users = new List<DailyCheckinUserDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(new DailyCheckinUserDto(
                ReadGuid(reader, "id"),
                reader.GetString(reader.GetOrdinal("name")),
                ReadNullableString(reader, "role"),
                reader.GetString(reader.GetOrdinal("initials")),
                reader.GetBoolean(reader.GetOrdinal("is_active")),
                ReadNullableGuid(reader, "group_id"),
                ReadNullableString(reader, "group_name")));
        }

        return users;
    }

    private static async Task<IReadOnlyCollection<string>> ReadVisibleGroupIdsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string visibleSql = "select group_id from daily_checkin_visible_groups order by group_id";
        await using (var command = new NpgsqlCommand(visibleSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            var visible = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                visible.Add(reader.GetGuid(0).ToString());
            }

            if (visible.Count != 0)
            {
                return visible;
            }
        }

        return (await ReadGroupsAsync(connection, cancellationToken)).Select(x => x.Id).ToArray();
    }

    private static async Task<int> ReadRotationSecondsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = "select rotation_seconds from daily_checkin_settings order by created_at limit 1";
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int value ? value : 5;
    }

    private static async Task<Dictionary<(string UserId, string Channel), int>> CountActivitiesByUserAndChannelAsync(NpgsqlConnection connection, DateTime startsAt, DateTime endsAt, string[] userIds, CancellationToken cancellationToken)
    {
        if (userIds.Length == 0)
        {
            return [];
        }

        const string sql = """
            select owner_user_id, lower(channel) as channel, count(*)::int as total
            from activities
            where owner_user_id = any(@userIds)
              and date_at >= @startsAt
              and date_at < @endsAt
              and status = 'done'
            group by owner_user_id, lower(channel)
            """;

        await using var command = CreateRangeCommand(connection, sql, startsAt, endsAt, userIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var counts = new Dictionary<(string UserId, string Channel), int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[(reader.GetGuid(reader.GetOrdinal("owner_user_id")).ToString(), reader.GetString(reader.GetOrdinal("channel")))] = reader.GetInt32(reader.GetOrdinal("total"));
        }

        return counts;
    }

    private static async Task<Dictionary<string, int>> CountOpportunitiesByUserAsync(NpgsqlConnection connection, DateTime startsAt, DateTime endsAt, string[] userIds, CancellationToken cancellationToken) =>
        await CountByUserAsync(connection, """
            select owner_user_id as user_id, count(*)::int as total
            from opportunities
            where owner_user_id = any(@userIds)
              and created_at >= @startsAt
              and created_at < @endsAt
            group by owner_user_id
            """, startsAt, endsAt, userIds, cancellationToken);

    private static async Task<Dictionary<string, int>> CountNotesByUserAsync(NpgsqlConnection connection, DateTime startsAt, DateTime endsAt, string[] userIds, CancellationToken cancellationToken) =>
        await CountByUserAsync(connection, """
            select author_user_id as user_id, count(*)::int as total
            from notes
            where author_user_id = any(@userIds)
              and created_at >= @startsAt
              and created_at < @endsAt
            group by author_user_id
            """, startsAt, endsAt, userIds, cancellationToken);

    private static async Task<Dictionary<string, int>> CountByUserAsync(NpgsqlConnection connection, string sql, DateTime startsAt, DateTime endsAt, string[] userIds, CancellationToken cancellationToken)
    {
        if (userIds.Length == 0)
        {
            return [];
        }

        await using var command = CreateRangeCommand(connection, sql, startsAt, endsAt, userIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[reader.GetGuid(reader.GetOrdinal("user_id")).ToString()] = reader.GetInt32(reader.GetOrdinal("total"));
        }

        return counts;
    }

    private static NpgsqlCommand CreateRangeCommand(NpgsqlConnection connection, string sql, DateTime startsAt, DateTime endsAt, string[] userIds)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("userIds", userIds.Select(Guid.Parse).ToArray());
        command.Parameters.AddWithValue("startsAt", NpgsqlDbType.TimestampTz, startsAt);
        command.Parameters.AddWithValue("endsAt", NpgsqlDbType.TimestampTz, endsAt);
        return command;
    }

    private static int GetActivityCount(IReadOnlyDictionary<(string UserId, string Channel), int> counts, string userId, string? activityChannel)
    {
        if (!string.IsNullOrWhiteSpace(activityChannel))
        {
            return counts.GetValueOrDefault((userId, activityChannel.Trim().ToLowerInvariant()));
        }

        return counts.Where(x => x.Key.UserId == userId).Sum(x => x.Value);
    }

    private static int AveragePercent(IEnumerable<DailyCheckinGoalResultDto> results)
    {
        var values = results.Select(x => Math.Min(x.Percent, 100)).ToArray();
        return values.Length == 0 ? 0 : (int)Math.Round(values.Average(), MidpointRounding.AwayFromZero);
    }

    private static string ReadGuid(NpgsqlDataReader reader, string name) => reader.GetGuid(reader.GetOrdinal(name)).ToString();

    private static string? ReadNullableGuid(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal).ToString();
    }

    private static string? ReadNullableString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private sealed record DailyCheckinEventDelta(string UserId, string Unit, string? ActivityChannel, DateOnly Date, int Amount);

    private sealed record DailyCheckinSnapshotKey(DateOnly Date, string? GroupId);
}
