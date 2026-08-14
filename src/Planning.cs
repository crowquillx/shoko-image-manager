using Shoko.Abstractions.Metadata.Enums;

namespace Shoko.ImagePlanner;

public sealed record PlannerCandidate(
    string CandidateId,
    ImageEntityType ImageType,
    DataSource Source,
    Guid? ImageId,
    string? RemoteResourceId,
    int? Width,
    int? Height,
    string? LanguageCode,
    double? Rating,
    int? RatingVotes,
    bool IsPreferredHint,
    bool IsManualHint,
    string? ContentHash,
    int ProviderPriority = 0,
    string? DownloadUrl = null,
    bool IsAvailable = false)
{
    public string ExactKey => ImageId is { } imageId
        ? $"uuid:{imageId:D}"
        : ContentHash is { Length: > 0 } contentHash
            ? $"sha256:{contentHash.ToLowerInvariant()}"
            : $"candidate:{CandidateId}";
}

public sealed record PlannerSeries(
    int SeriesId,
    string Name,
    IReadOnlyList<PlannerCandidate> Candidates,
    PlannerCandidate? ProtectedChoice = null);

public sealed record PlannerAssignment(
    int SeriesId,
    ImageEntityType ImageType,
    string CandidateId,
    bool IsUnique,
    bool IsFallback,
    long Score,
    string? Reason);

public sealed record AssignmentResult(
    IReadOnlyList<PlannerAssignment> Assignments,
    int UniqueCandidateCount,
    int SeriesCount,
    bool HasInsufficientUniqueCandidates);

public static class PreferenceProtection
{
    public static bool CanReplace(Guid? currentImageId, Guid? pluginOwnedImageId, bool force)
        => force || currentImageId is null || pluginOwnedImageId == currentImageId;
}

public sealed class GlobalAssignmentPlanner
{
    public AssignmentResult Assign(ImageEntityType type, IReadOnlyList<PlannerSeries> input, string preferredLanguage)
    {
        ArgumentNullException.ThrowIfNull(input);
        var rows = input.OrderBy(item => item.SeriesId).ToArray();
        var clusters = DuplicateClusterer.Cluster(rows.SelectMany(item => item.Candidates).Where(item => item.ImageType == type));
        var clusterKeys = clusters.Values.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var columns = clusterKeys.Length + rows.Length;
        var weights = new long[rows.Length, columns];
        for (var row = 0; row < rows.Length; row++)
        {
            for (var column = 0; column < clusterKeys.Length; column++)
            {
                var candidates = rows[row].Candidates
                    .Where(candidate => candidate.ImageType == type && clusters[candidate.CandidateId] == clusterKeys[column])
                    .OrderByDescending(candidate => Score(candidate, preferredLanguage))
                    .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                    .ToArray();
                weights[row, column] = candidates.Length == 0 ? long.MinValue / 4 : Score(candidates[0], preferredLanguage);
            }
        }

        var chosenColumns = columns <= 20 ? ExactMax(weights, clusterKeys.Length) : HungarianMax(weights);
        var assignments = new List<PlannerAssignment>(rows.Length);
        for (var row = 0; row < rows.Length; row++)
        {
            var column = chosenColumns[row];
            if (column >= 0 && column < clusterKeys.Length && weights[row, column] > long.MinValue / 8)
            {
                var selected = rows[row].Candidates
                    .Where(candidate => candidate.ImageType == type && clusters[candidate.CandidateId] == clusterKeys[column])
                    .OrderByDescending(candidate => Score(candidate, preferredLanguage))
                    .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                    .First();
                assignments.Add(new PlannerAssignment(rows[row].SeriesId, type, selected.CandidateId, true, false, Score(selected, preferredLanguage), null));
                continue;
            }

            var fallback = rows[row].Candidates
                .Where(candidate => candidate.ImageType == type)
                .OrderByDescending(candidate => Score(candidate, preferredLanguage))
                .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (fallback is not null)
                assignments.Add(new PlannerAssignment(rows[row].SeriesId, type, fallback.CandidateId, false, true, Score(fallback, preferredLanguage), "No unused unique image was available."));
            else
                assignments.Add(new PlannerAssignment(rows[row].SeriesId, type, string.Empty, false, true, 0, "No candidate image was available."));
        }

        return new AssignmentResult(assignments, clusterKeys.Length, rows.Length, clusterKeys.Length < rows.Length);
    }

    private static long Score(PlannerCandidate candidate, string preferredLanguage)
    {
        var score = 100_000L;
        if (candidate.IsManualHint)
            score += 50_000;
        if (candidate.IsPreferredHint)
            score += 25_000;
        if (!string.IsNullOrWhiteSpace(preferredLanguage) && string.Equals(candidate.LanguageCode, preferredLanguage, StringComparison.OrdinalIgnoreCase))
            score += 10_000;
        if (candidate.Width is > 0 && candidate.Height is > 0)
        {
            var ratio = (double)candidate.Width.Value / candidate.Height.Value;
            score += (long)Math.Max(0, 5_000 - Math.Abs(ratio - (candidate.ImageType == ImageEntityType.Primary ? 0.667 : 1.778)) * 4_000);
            score += Math.Min(2_000, candidate.Width.Value / 10L);
        }
        if (candidate.Rating is { } rating)
            score += (long)Math.Round(Math.Clamp(rating, 1, 10) * 500, MidpointRounding.AwayFromZero);
        if (candidate.RatingVotes is { } votes)
            score += Math.Min(1_000, Math.Max(0, votes));
        score += candidate.ProviderPriority * 100;
        return score;
    }

    private static int[] ExactMax(long[,] weights, int realColumnCount)
    {
        var rowCount = weights.GetLength(0);
        var memo = new Dictionary<(int Row, int Mask), (long Score, int Column)>();
        (long Score, int Column) Solve(int row, int mask)
        {
            if (row == rowCount)
                return (0, -1);
            if (memo.TryGetValue((row, mask), out var cached))
                return cached;
            var best = (Solve(row + 1, mask).Score, Column: -1);
            for (var column = 0; column < realColumnCount; column++)
            {
                if ((mask & (1 << column)) != 0 || weights[row, column] <= long.MinValue / 8)
                    continue;
                var next = Solve(row + 1, mask | (1 << column));
                var score = weights[row, column] + next.Score;
                if (score > best.Score || score == best.Score && (best.Column < 0 || column < best.Column))
                    best = (score, column);
            }
            memo[(row, mask)] = best;
            return best;
        }

        var result = Enumerable.Repeat(-1, rowCount).ToArray();
        var mask = 0;
        for (var row = 0; row < rowCount; row++)
        {
            var choice = Solve(row, mask);
            result[row] = choice.Column;
            if (choice.Column >= 0)
                mask |= 1 << choice.Column;
        }
        return result;
    }

    private static int[] HungarianMax(long[,] weights)
    {
        var rowCount = weights.GetLength(0);
        var columnCount = weights.GetLength(1);
        if (rowCount == 0)
            return [];
        var maxWeight = 0L;
        for (var row = 0; row < rowCount; row++)
            for (var column = 0; column < columnCount; column++)
                maxWeight = Math.Max(maxWeight, weights[row, column] > long.MinValue / 8 ? weights[row, column] : 0);

        var u = new long[rowCount + 1];
        var v = new long[columnCount + 1];
        var p = new int[columnCount + 1];
        var way = new int[columnCount + 1];
        for (var row = 1; row <= rowCount; row++)
        {
            p[0] = row;
            var column0 = 0;
            var minv = Enumerable.Repeat(long.MaxValue / 4, columnCount + 1).ToArray();
            var used = new bool[columnCount + 1];
            do
            {
                used[column0] = true;
                var row0 = p[column0];
                var delta = long.MaxValue / 4;
                var column1 = 0;
                for (var column = 1; column <= columnCount; column++)
                {
                    if (used[column])
                        continue;
                    var weight = weights[row0 - 1, column - 1] > long.MinValue / 8 ? weights[row0 - 1, column - 1] : long.MinValue / 8;
                    var current = maxWeight - weight - u[row0] - v[column];
                    if (current < minv[column] || current == minv[column] && column < way[column])
                    {
                        minv[column] = current;
                        way[column] = column0;
                    }
                    if (minv[column] < delta || minv[column] == delta && column < column1)
                    {
                        delta = minv[column];
                        column1 = column;
                    }
                }
                for (var column = 0; column <= columnCount; column++)
                {
                    if (used[column])
                    {
                        u[p[column]] += delta;
                        v[column] -= delta;
                    }
                    else
                        minv[column] -= delta;
                }
                column0 = column1;
            } while (p[column0] != 0);
            do
            {
                var column1 = way[column0];
                p[column0] = p[column1];
                column0 = column1;
            } while (column0 != 0);
        }

        var result = Enumerable.Repeat(-1, rowCount).ToArray();
        for (var column = 1; column <= columnCount; column++)
            if (p[column] != 0)
                result[p[column] - 1] = column - 1;
        return result;
    }
}

internal static class DuplicateClusterer
{
    public static Dictionary<string, string> Cluster(IEnumerable<PlannerCandidate> candidates)
    {
        var ordered = candidates.OrderBy(candidate => candidate.ExactKey, StringComparer.Ordinal).ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal).ToArray();
        var parent = ordered.Select((_, index) => index).ToArray();
        for (var left = 0; left < ordered.Length; left++)
            for (var right = left + 1; right < ordered.Length; right++)
                if (string.Equals(ordered[left].ExactKey, ordered[right].ExactKey, StringComparison.Ordinal))
                    Union(parent, left, right);

        var names = new Dictionary<int, string>();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < ordered.Length; index++)
        {
            var root = Find(parent, index);
            if (!names.TryGetValue(root, out var name))
                names[root] = name = ordered[index].ExactKey;
            result[ordered[index].CandidateId] = name;
        }
        return result;
    }

    private static int Find(int[] parent, int value)
    {
        while (parent[value] != value)
        {
            parent[value] = parent[parent[value]];
            value = parent[value];
        }
        return value;
    }

    private static void Union(int[] parent, int left, int right)
    {
        left = Find(parent, left);
        right = Find(parent, right);
        if (left != right)
            parent[Math.Max(left, right)] = Math.Min(left, right);
    }
}
