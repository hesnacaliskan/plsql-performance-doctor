using System.Globalization;
using System.Text.RegularExpressions;

namespace PlsqlDoctor.Core.Plans;

/// <summary>
/// Parses the text output of DBMS_XPLAN.DISPLAY / DISPLAY_CURSOR (TYPICAL, ALL or ALLSTATS LAST),
/// including the "Predicate Information" section.
/// </summary>
public static partial class PlanParser
{
    [GeneratedRegex(@"\|\s*Id\s*\|\s*Operation", RegexOptions.IgnoreCase)]
    private static partial Regex PlanHeader();

    [GeneratedRegex(@"^\s*(?<id>\d+)\s*-\s*(?<type>access|filter)\((?<text>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PredicateWithId();

    [GeneratedRegex(@"^\s*(?<type>access|filter)\((?<text>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PredicateContinuation();

    [GeneratedRegex(@"^(?<n>\d+(?:\.\d+)?)\s*(?<unit>[KMGTPE])?", RegexOptions.IgnoreCase)]
    private static partial Regex NumberWithUnit();

    // "SQL_ID  8h2kq0p3x5m1v, child number 0" starts a cursor in DISPLAY_CURSOR output.
    [GeneratedRegex(@"^SQL_ID[:\s]+(?<id>[A-Za-z0-9]+),?\s*child number\s+(?<child>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlIdLine();

    public static bool LooksLikePlan(string text) => PlanHeader().IsMatch(text);

    /// <summary>Parses the first plan in <paramref name="text"/>. Use <see cref="ParseAll"/> for multi-plan output.</summary>
    public static ExecutionPlan Parse(string text) => ParseAll(text).FirstOrDefault() ?? new ExecutionPlan([]);

    /// <summary>
    /// Parses every plan in <paramref name="text"/>. DISPLAY_CURSOR prints one plan per child cursor and
    /// DISPLAY_AWR one per plan hash value; each of them becomes its own <see cref="ExecutionPlan"/>.
    /// </summary>
    public static IReadOnlyList<ExecutionPlan> ParseAll(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var plans = new List<ExecutionPlan>();
        var steps = new List<PlanStep>();
        var predicates = new List<(int Id, string Type, string Text)>();

        string[]? header = null;
        string? label = null;
        var inPredicates = false;
        int? currentPredicateId = null;

        void Flush()
        {
            if (steps.Count > 0) plans.Add(Build(steps, predicates, label));
            steps = [];
            predicates = [];
            header = null;
            label = null;
            inPredicates = false;
            currentPredicateId = null;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.Trim();

            var sqlId = SqlIdLine().Match(trimmed);
            if (sqlId.Success)
            {
                Flush();
                label = $"SQL_ID {sqlId.Groups["id"].Value}, child {sqlId.Groups["child"].Value}";
                continue;
            }

            // Every plan has one "Plan hash value" line; a second one after plan rows means a second plan.
            if (trimmed.StartsWith("Plan hash value", StringComparison.OrdinalIgnoreCase))
            {
                if (steps.Count > 0) Flush();
                continue;
            }

            if (trimmed.StartsWith("Predicate Information", StringComparison.OrdinalIgnoreCase))
            {
                inPredicates = true;
                continue;
            }

            if (inPredicates)
            {
                if (trimmed.Length == 0 || trimmed.StartsWith("---")) continue;

                // A non-indented line starts the next section ("Note", "Column Projection", the next
                // plan table, ...). It falls through to the normal handling below.
                if (!char.IsWhiteSpace(line[0]))
                {
                    inPredicates = false;
                }
                else
                {
                    var withId = PredicateWithId().Match(line);
                    if (withId.Success)
                    {
                        currentPredicateId = int.Parse(withId.Groups["id"].Value, CultureInfo.InvariantCulture);
                        predicates.Add((currentPredicateId.Value, withId.Groups["type"].Value.ToLowerInvariant(), withId.Groups["text"].Value.Trim()));
                        continue;
                    }

                    var continuation = PredicateContinuation().Match(line);
                    if (continuation.Success && currentPredicateId is { } id)
                    {
                        predicates.Add((id, continuation.Groups["type"].Value.ToLowerInvariant(), continuation.Groups["text"].Value.Trim()));
                        continue;
                    }

                    // Wrapped predicate text.
                    if (predicates.Count > 0)
                    {
                        var last = predicates[^1];
                        predicates[^1] = last with { Text = last.Text + " " + trimmed };
                    }

                    continue;
                }
            }

            if (!trimmed.StartsWith('|')) continue;

            var cells = SplitCells(trimmed);
            if (IsHeader(cells))
            {
                // A second header without a SQL_ID or plan hash line in between is still a second plan.
                if (header is not null) Flush();
                header = cells.Select(c => c.Trim()).ToArray();
                continue;
            }

            if (header is null || cells.Length != header.Length) continue;

            var step = ParseRow(header, cells);
            if (step is not null) steps.Add(step);
        }

        Flush();
        return plans;
    }

    private static bool IsHeader(string[] cells) =>
        cells.Any(c => c.Trim() == "Id") && cells.Any(c => c.Trim() == "Operation");

    private static ExecutionPlan Build(List<PlanStep> steps, List<(int Id, string Type, string Text)> predicates, string? label)
    {
        foreach (var (id, type, predicateText) in predicates)
        {
            var step = steps.FirstOrDefault(s => s.Id == id);
            step?.Predicates.Add(new PlanPredicate(type, StripClosingParen(predicateText)));
        }

        LinkTree(steps);
        return new ExecutionPlan(steps, label);
    }

    internal static double? ParseNumber(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return null;
        var match = NumberWithUnit().Match(cell.Trim());
        if (!match.Success) return null;

        var value = double.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "K" => 1e3,
            "M" => 1e6,
            "G" => 1e9,
            "T" => 1e12,
            "P" => 1e15,
            "E" => 1e18,
            _ => 1d,
        };
        return value * multiplier;
    }

    private static string[] SplitCells(string row)
    {
        var parts = row.Split('|');
        // "| a | b |" -> ["", " a ", " b ", ""]
        var start = 1;
        var end = row.EndsWith('|') ? parts.Length - 1 : parts.Length;
        return parts[start..end];
    }

    private static PlanStep? ParseRow(string[] header, string[] cells)
    {
        string? Cell(params string[] names)
        {
            for (var i = 0; i < header.Length; i++)
            {
                if (names.Any(n => header[i].StartsWith(n, StringComparison.OrdinalIgnoreCase))) return cells[i];
            }

            return null;
        }

        var idCell = (Cell("Id") ?? "").Trim();
        var hasMarker = idCell.StartsWith('*');
        if (!int.TryParse(idCell.TrimStart('*').Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        var operationCell = Cell("Operation") ?? "";
        var operation = operationCell.Trim();
        if (operation.Length == 0) return null;

        // One space follows the '|', then one extra space per tree level.
        var leading = operationCell.Length - operationCell.TrimStart().Length;

        var name = Cell("Name")?.Trim();
        return new PlanStep
        {
            Id = id,
            Depth = Math.Max(0, leading - 1),
            Operation = operation,
            ObjectName = string.IsNullOrEmpty(name) ? null : name,
            EstimatedRows = ParseNumber(ExactCell(header, cells, "Rows") ?? ExactCell(header, cells, "E-Rows")),
            ActualRows = ParseNumber(ExactCell(header, cells, "A-Rows")),
            Starts = ParseNumber(ExactCell(header, cells, "Starts")),
            Cost = ParseNumber(Cell("Cost")),
            Buffers = ParseNumber(ExactCell(header, cells, "Buffers")),
            HasPredicateMarker = hasMarker,
        };
    }

    private static string? ExactCell(string[] header, string[] cells, string name)
    {
        var index = Array.FindIndex(header, h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? cells[index] : null;
    }

    private static string StripClosingParen(string text)
    {
        var t = text.Trim();
        return t.EndsWith(')') ? t[..^1] : t;
    }

    private static void LinkTree(List<PlanStep> steps)
    {
        var stack = new Stack<PlanStep>();
        foreach (var step in steps)
        {
            while (stack.Count > 0 && stack.Peek().Depth >= step.Depth) stack.Pop();
            if (stack.Count > 0)
            {
                step.Parent = stack.Peek();
                stack.Peek().Children.Add(step);
            }

            stack.Push(step);
        }
    }
}
