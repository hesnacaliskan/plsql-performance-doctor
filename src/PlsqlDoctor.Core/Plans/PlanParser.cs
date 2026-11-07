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

    public static bool LooksLikePlan(string text) => PlanHeader().IsMatch(text);

    public static ExecutionPlan Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var steps = new List<PlanStep>();
        var predicates = new List<(int Id, string Type, string Text)>();

        string[]? header = null;
        var inPredicates = false;
        int? currentPredicateId = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.Trim();

            if (trimmed.StartsWith("Predicate Information", StringComparison.OrdinalIgnoreCase))
            {
                inPredicates = true;
                continue;
            }

            if (inPredicates)
            {
                if (trimmed.Length == 0 || trimmed.StartsWith("---")) continue;

                // A non-indented line starts the next section ("Note", "Column Projection", ...).
                if (!char.IsWhiteSpace(line[0]))
                {
                    inPredicates = false;
                    continue;
                }

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

            if (!trimmed.StartsWith('|')) continue;

            var cells = SplitCells(trimmed);
            if (header is null)
            {
                if (cells.Any(c => c.Trim() == "Id") && cells.Any(c => c.Trim() == "Operation"))
                {
                    header = cells.Select(c => c.Trim()).ToArray();
                }

                continue;
            }

            if (cells.Length != header.Length) continue;

            var step = ParseRow(header, cells);
            if (step is not null) steps.Add(step);
        }

        foreach (var (id, type, predicateText) in predicates)
        {
            var step = steps.FirstOrDefault(s => s.Id == id);
            step?.Predicates.Add(new PlanPredicate(type, StripClosingParen(predicateText)));
        }

        LinkTree(steps);
        return new ExecutionPlan(steps);
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
