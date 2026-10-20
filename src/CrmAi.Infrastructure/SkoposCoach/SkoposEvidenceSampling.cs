namespace CrmAi.Infrastructure.SkoposCoach;

// Stratifies existing analyses; it never schedules an analysis or contacts a provider.
internal static class SkoposEvidenceSampling
{
    internal const int MinimumTargetReports = 15;
    internal const int MaximumReports = 30;
    internal const int MaximumExcerptCharacters = 12_600;
    internal static (int Summary, int Observation) TextLimits(int sampleCount)
    {
        var count = Math.Max(1, sampleCount);
        return (Math.Min(350, 7_200 / count), Math.Min(160, 5_400 / (2 * count)));
    }
    internal sealed record Case(Guid Id, string CaseKey, bool WithoutOpportunity, DateTime OccurredAt, int? Score);

    internal static Guid[] Select(IEnumerable<Case> source)
    {
        var groups = source.DistinctBy(x => x.Id).GroupBy(x => x.WithoutOpportunity)
            .OrderByDescending(g => g.Key).Select(g => g.ToList()).ToArray();
        var selected = new List<Case>();
        for (var round = 0; selected.Count < MaximumReports; round++)
        {
            var added = false;
            foreach (var group in groups)
            {
                var available = group.Where(x => selected.All(s => s.Id != x.Id)
                    && selected.Count(s => s.CaseKey == x.CaseKey) < 2).ToArray();
                var unseen = available.Where(x => selected.All(s => s.CaseKey != x.CaseKey)).ToArray();
                var pool = unseen.Length > 0 ? unseen : available;
                var ordered = (round % 3) switch
                {
                    0 => pool.OrderBy(x => x.Score.HasValue ? 0 : 1).ThenBy(x => x.Score).ThenByDescending(x => x.OccurredAt),
                    1 => pool.OrderBy(x => x.Score.HasValue ? 0 : 1).ThenByDescending(x => x.Score).ThenByDescending(x => x.OccurredAt),
                    _ => pool.OrderByDescending(x => x.OccurredAt).ThenBy(x => x.Id)
                };
                var next = ordered.ThenBy(x => x.Id).FirstOrDefault();
                if (next is null) continue;
                selected.Add(next); added = true;
                if (selected.Count == MaximumReports) break;
            }
            if (!added) break;
        }
        return selected.Select(x => x.Id).ToArray();
    }
}
