using CrmAi.Application;
using CrmAi.Domain;
using Npgsql;

namespace CrmAi.Infrastructure.Gamification;

public sealed class PostgresGamificationProjectionService(NpgsqlDataSource dataSource) : IGamificationProjectionService
{
    private static readonly IReadOnlySet<string> SupportedEventTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "opportunity.created",
        "opportunity.activity.created",
        "opportunity.activity.updated",
        "opportunity.note.created",
        "opportunity.stage.changed",
        "opportunity.pipeline.changed",
        "opportunity.updated"
    };

    public async Task ProjectAsync(OpportunityEvent opportunityEvent, CancellationToken cancellationToken)
    {
        if (!SupportedEventTypes.Contains(opportunityEvent.Type) ||
            !Guid.TryParse(opportunityEvent.OpportunityId, out var opportunityId))
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var opportunity = await ReadOpportunityAsync(connection, opportunityId, cancellationToken);
        if (opportunity is null)
        {
            return;
        }

        var userId = Guid.TryParse(opportunityEvent.UserId, out var eventUserId) ? eventUserId : opportunity.OwnerUserId;
        if (userId is null)
        {
            return;
        }

        var user = await ReadUserAsync(connection, userId.Value, cancellationToken);
        if (user is null)
        {
            return;
        }

        var scoredAt = opportunityEvent.OccurredAt.ToUniversalTime();
        var opportunityTags = await ReadOpportunityTagsAsync(connection, opportunity.Id, cancellationToken);
        var games = await ReadActiveGamesAsync(connection, user.GroupId, scoredAt, cancellationToken);
        var rules = await ReadRulesAsync(connection, games.Select(x => x.Id).ToArray(), cancellationToken);
        var activeTags = await ReadActiveTagsAsync(connection, rules.Select(x => x.TagId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray(), cancellationToken);

        foreach (var game in games)
        {
            foreach (var rule in rules.Where(x => x.GameId == game.Id && RuleApplies(x, opportunity, opportunityTags, activeTags, scoredAt)))
            {
                if (string.Equals(rule.AccumulationMode, "single", StringComparison.OrdinalIgnoreCase) &&
                    await ScoreExistsForOpportunityAsync(connection, game.Id, rule.Id, user.Id, opportunity.Id, cancellationToken))
                {
                    continue;
                }

                var sourceEventKey = $"{opportunityEvent.EventId}:{rule.Id}";
                if (await ScoreExistsForEventAsync(connection, sourceEventKey, cancellationToken))
                {
                    continue;
                }

                await InsertScoreAsync(connection, game.Id, rule.Id, user.Id, opportunity.Id, rule.Points, opportunity.Value, scoredAt, sourceEventKey, opportunityEvent.Type, cancellationToken);
            }
        }
    }

    private static bool RuleApplies(
        GamificationRuleSnapshot rule,
        GamificationOpportunitySnapshot opportunity,
        IReadOnlySet<string> opportunityTags,
        IReadOnlyDictionary<Guid, string> activeTags,
        DateTime scoredAt)
    {
        if (!string.Equals(rule.Status, "active", StringComparison.OrdinalIgnoreCase) ||
            rule.StartsAt > scoredAt ||
            rule.EndsAt < scoredAt ||
            (rule.StageId is not null && rule.StageId != opportunity.StageId) ||
            (rule.MinimumValue.HasValue && opportunity.Value < rule.MinimumValue.Value))
        {
            return false;
        }

        if (rule.TagId is null)
        {
            return true;
        }

        return activeTags.TryGetValue(rule.TagId.Value, out var tagName) && opportunityTags.Contains(tagName);
    }

    private static async Task<GamificationOpportunitySnapshot?> ReadOpportunityAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = "select id, stage_id, owner_user_id, value from opportunities where id = @id";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", opportunityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new GamificationOpportunitySnapshot(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("stage_id")),
                ReadNullableGuid(reader, "owner_user_id"),
                reader.GetDecimal(reader.GetOrdinal("value")))
            : null;
    }

    private static async Task<GamificationUserSnapshot?> ReadUserAsync(NpgsqlConnection connection, Guid userId, CancellationToken cancellationToken)
    {
        const string sql = "select id, group_id from users where id = @id and is_active = true";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new GamificationUserSnapshot(reader.GetGuid(reader.GetOrdinal("id")), ReadNullableGuid(reader, "group_id"))
            : null;
    }

    private static async Task<IReadOnlySet<string>> ReadOpportunityTagsAsync(NpgsqlConnection connection, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = "select lower(tag) from opportunity_tags where opportunity_id = @opportunityId";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            tags.Add(reader.GetString(0));
        }

        return tags;
    }

    private static async Task<IReadOnlyCollection<GamificationGameSnapshot>> ReadActiveGamesAsync(NpgsqlConnection connection, Guid? userGroupId, DateTime scoredAt, CancellationToken cancellationToken)
    {
        const string sql = """
            select id
            from gamification_games
            where status = 'active'
              and starts_at <= @scoredAt
              and ends_at >= @scoredAt
              and (group_id is null or group_id is not distinct from @groupId)
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("scoredAt", scoredAt);
        command.Parameters.AddWithValue("groupId", userGroupId.HasValue ? userGroupId.Value : DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var games = new List<GamificationGameSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            games.Add(new GamificationGameSnapshot(reader.GetGuid(0)));
        }

        return games;
    }

    private static async Task<IReadOnlyCollection<GamificationRuleSnapshot>> ReadRulesAsync(NpgsqlConnection connection, Guid[] gameIds, CancellationToken cancellationToken)
    {
        if (gameIds.Length == 0)
        {
            return [];
        }

        const string sql = """
            select id, game_id, tag_id, stage_id, points, minimum_value, starts_at, ends_at, status, accumulation_mode
            from gamification_scoring_rules
            where game_id = any(@gameIds)
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("gameIds", gameIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rules = new List<GamificationRuleSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new GamificationRuleSnapshot(
                reader.GetGuid(reader.GetOrdinal("id")),
                reader.GetGuid(reader.GetOrdinal("game_id")),
                ReadNullableGuid(reader, "tag_id"),
                ReadNullableGuid(reader, "stage_id"),
                reader.GetInt32(reader.GetOrdinal("points")),
                ReadNullableDecimal(reader, "minimum_value"),
                reader.GetDateTime(reader.GetOrdinal("starts_at")),
                reader.GetDateTime(reader.GetOrdinal("ends_at")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetString(reader.GetOrdinal("accumulation_mode"))));
        }

        return rules;
    }

    private static async Task<IReadOnlyDictionary<Guid, string>> ReadActiveTagsAsync(NpgsqlConnection connection, Guid[] tagIds, CancellationToken cancellationToken)
    {
        if (tagIds.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        const string sql = "select id, lower(name) as name from tags where id = any(@tagIds) and status = 'active'";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tagIds", tagIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tags = new Dictionary<Guid, string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tags[reader.GetGuid(reader.GetOrdinal("id"))] = reader.GetString(reader.GetOrdinal("name"));
        }

        return tags;
    }

    private static async Task<bool> ScoreExistsForOpportunityAsync(NpgsqlConnection connection, Guid gameId, Guid ruleId, Guid userId, Guid opportunityId, CancellationToken cancellationToken)
    {
        const string sql = """
            select exists (
                select 1
                from gamification_scores
                where game_id = @gameId
                  and rule_id = @ruleId
                  and user_id = @userId
                  and opportunity_id = @opportunityId)
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("gameId", gameId);
        command.Parameters.AddWithValue("ruleId", ruleId);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<bool> ScoreExistsForEventAsync(NpgsqlConnection connection, string sourceEventKey, CancellationToken cancellationToken)
    {
        const string sql = "select exists (select 1 from gamification_scores where source_event_key = @sourceEventKey)";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("sourceEventKey", sourceEventKey);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task InsertScoreAsync(
        NpgsqlConnection connection,
        Guid gameId,
        Guid ruleId,
        Guid userId,
        Guid opportunityId,
        int points,
        decimal consideredValue,
        DateTime scoredAt,
        string sourceEventKey,
        string metadata,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into gamification_scores (
                id, game_id, rule_id, user_id, opportunity_id, points, considered_value, scored_at, source_event_key, metadata, created_at)
            values (
                @id, @gameId, @ruleId, @userId, @opportunityId, @points, @consideredValue, @scoredAt, @sourceEventKey, @metadata, @createdAt)
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("gameId", gameId);
        command.Parameters.AddWithValue("ruleId", ruleId);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("opportunityId", opportunityId);
        command.Parameters.AddWithValue("points", points);
        command.Parameters.AddWithValue("consideredValue", consideredValue);
        command.Parameters.AddWithValue("scoredAt", scoredAt);
        command.Parameters.AddWithValue("sourceEventKey", sourceEventKey);
        command.Parameters.AddWithValue("metadata", metadata);
        command.Parameters.AddWithValue("createdAt", DateTime.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Guid? ReadNullableGuid(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    }

    private static decimal? ReadNullableDecimal(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    }

    private sealed record GamificationOpportunitySnapshot(Guid Id, Guid StageId, Guid? OwnerUserId, decimal Value);
    private sealed record GamificationUserSnapshot(Guid Id, Guid? GroupId);
    private sealed record GamificationGameSnapshot(Guid Id);
    private sealed record GamificationRuleSnapshot(Guid Id, Guid GameId, Guid? TagId, Guid? StageId, int Points, decimal? MinimumValue, DateTime StartsAt, DateTime EndsAt, string Status, string AccumulationMode);
}
