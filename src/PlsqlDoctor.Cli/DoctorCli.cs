using System.Text;
using PlsqlDoctor.Core;
using PlsqlDoctor.Core.Findings;
using PlsqlDoctor.Core.Plans;
using PlsqlDoctor.Core.PlSql;
using PlsqlDoctor.Core.Reporting;

namespace PlsqlDoctor.Cli;

public static class DoctorCli
{
    public const int Ok = 0;
    public const int FindingsAboveThreshold = 1;
    public const int UsageError = 2;

    private static readonly HashSet<string> SqlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sql", ".pks", ".pkb", ".pck", ".pkg", ".pls", ".plb", ".prc", ".fnc", ".trg", ".typ", ".tps", ".tpb",
    };

    private static readonly HashSet<string> PlanExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".plan", ".xplan",
    };

    private sealed class UsageException(string message) : Exception(message);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, TextReader? stdin = null)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            WriteHelp(stdout);
            return args.Length == 0 ? UsageError : Ok;
        }

        if (args[0] is "-v" or "--version")
        {
            stdout.WriteLine(DoctorInfo.Version);
            return Ok;
        }

        try
        {
            var command = args[0].ToLowerInvariant();
            switch (command)
            {
                case "rules":
                    WriteRules(stdout);
                    return Ok;
                case "analyze":
                case "plan":
                    return Analyze(command, args[1..], stdout, stdin);
                default:
                    throw new UsageException($"Unknown command '{args[0]}'. Run 'plsql-doctor --help'.");
            }
        }
        catch (UsageException ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            return UsageError;
        }
    }

    private static int Analyze(string command, string[] args, TextWriter stdout, TextReader? stdin)
    {
        var paths = new List<string>();
        var format = "console";
        string? outFile = null;
        Severity? failOn = Severity.Critical;
        var noColor = false;
        var examples = false;
        Encoding? encoding = null;

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new UsageException($"Missing value for {args[i - 1]}.");

            switch (args[i])
            {
                case "-f" or "--format":
                    format = Next().ToLowerInvariant();
                    break;
                case "-o" or "--out":
                    outFile = Next();
                    break;
                case "--fail-on":
                    failOn = ParseFailOn(Next());
                    break;
                case "--no-color":
                    noColor = true;
                    break;
                case "-e" or "--examples":
                    examples = true;
                    break;
                case "--encoding":
                    encoding = SourceReader.ParseEncoding(Next()) ?? throw new UsageException(
                        $"Unknown encoding '{args[i]}'. Use auto, utf-8, a name such as windows-1254, or a code page number.");
                    if (encoding == SourceReader.Auto) encoding = null;
                    break;
                case "-":
                    paths.Add("-");
                    break;
                default:
                    if (args[i].StartsWith('-')) throw new UsageException($"Unknown option '{args[i]}'.");
                    paths.Add(args[i]);
                    break;
            }
        }

        if (paths.Count == 0) throw new UsageException("Give at least one file or directory, or '-' to read from stdin.");

        var useColor = !noColor && outFile is null && ReferenceEquals(stdout, Console.Out) &&
                       !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

        IReporter reporter = format switch
        {
            "console" => new ConsoleReporter(useColor, examples),
            "markdown" or "md" => new MarkdownReporter(),
            "json" => new JsonReporter(),
            "sarif" => new SarifReporter(
                SqlAnalyzer.DefaultRules.Select(r => r.Info).Concat(PlanAnalyzer.DefaultRules.Select(r => r.Info))),
            _ => throw new UsageException($"Unknown format '{format}'. Use console, markdown, json or sarif."),
        };

        var reports = CollectReports(command, paths, stdin, encoding);
        if (reports.Count == 0) throw new UsageException("No PL/SQL or execution plan files found.");

        if (outFile is not null)
        {
            using var file = new StreamWriter(outFile, false, new UTF8Encoding(false));
            reporter.Write(reports, file);
            stdout.WriteLine($"Report written to {outFile}");
        }
        else
        {
            reporter.Write(reports, stdout);
        }

        var failed = failOn is { } threshold && reports.SelectMany(r => r.Findings).Any(f => f.Severity >= threshold);
        return failed ? FindingsAboveThreshold : Ok;
    }

    private static List<AnalysisReport> CollectReports(string command, List<string> paths, TextReader? stdin, Encoding? encoding)
    {
        var sqlAnalyzer = new SqlAnalyzer();
        var planAnalyzer = new PlanAnalyzer();
        var reports = new List<AnalysisReport>();

        void AnalyzeText(string text, string name, bool forcePlan)
        {
            var isPlan = forcePlan || PlanParser.LooksLikePlan(text);
            if (isPlan) reports.AddRange(planAnalyzer.AnalyzeAll(text, name));
            else reports.Add(sqlAnalyzer.Analyze(text, name));
        }

        foreach (var path in paths)
        {
            if (path == "-")
            {
                AnalyzeText((stdin ?? Console.In).ReadToEnd(), "<stdin>", command == "plan");
            }
            else if (Directory.Exists(path))
            {
                var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    .Order(StringComparer.OrdinalIgnoreCase);

                foreach (var file in files)
                {
                    var ext = Path.GetExtension(file);
                    var isSql = SqlExtensions.Contains(ext);
                    var isPlanFile = PlanExtensions.Contains(ext);
                    if (command == "plan" ? !isPlanFile : !(isSql || isPlanFile)) continue;

                    var text = SourceReader.Read(file, encoding);
                    // In a directory, plain .txt files only count when they really contain a plan.
                    if (!isSql && !PlanParser.LooksLikePlan(text)) continue;
                    AnalyzeText(text, DisplayPath(file), command == "plan");
                }
            }
            else if (File.Exists(path))
            {
                AnalyzeText(SourceReader.Read(path, encoding), DisplayPath(path), command == "plan");
            }
            else
            {
                throw new UsageException($"Path not found: {path}");
            }
        }

        return reports;
    }

    private static string DisplayPath(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, full);
        return (relative.StartsWith("..") ? full : relative).Replace('\\', '/');
    }

    private static Severity? ParseFailOn(string value) => value.ToLowerInvariant() switch
    {
        "none" or "never" => null,
        "info" => Severity.Info,
        "warning" => Severity.Warning,
        "critical" => Severity.Critical,
        _ => throw new UsageException($"Unknown --fail-on value '{value}'. Use none, info, warning or critical."),
    };

    private static void WriteRules(TextWriter w)
    {
        w.WriteLine("PL/SQL rules");
        foreach (var r in SqlAnalyzer.DefaultRules.Select(r => r.Info)) WriteRule(w, r);
        w.WriteLine();
        w.WriteLine("Execution plan rules");
        foreach (var r in PlanAnalyzer.DefaultRules.Select(r => r.Info)) WriteRule(w, r);
    }

    private static void WriteRule(TextWriter w, RuleInfo r) =>
        w.WriteLine($"  {r.Id}  {r.Severity.ToString().ToUpperInvariant(),-8}  {r.Category,-20}  {r.Title}");

    private static void WriteHelp(TextWriter w)
    {
        w.WriteLine($"""
            {DoctorInfo.Name} v{DoctorInfo.Version}
            Finds performance anti-patterns in Oracle PL/SQL code and DBMS_XPLAN execution plans.

            Usage:
              plsql-doctor analyze <file|dir|->...  [options]   PL/SQL files; plan files are detected automatically
              plsql-doctor plan <file|->...         [options]   Treat every input as DBMS_XPLAN output
              plsql-doctor rules                                List all rules

            Options:
              -f, --format <console|markdown|json|sarif>
                                                     Output format (default: console)
              -o, --out <file>                       Write the report to a file
              -e, --examples                         Show fix examples in console output
                  --fail-on <none|info|warning|critical>
                                                     Exit with code 1 when a finding has at least this
                                                     severity (default: critical)
                  --no-color                         Disable colors
                  --encoding <auto|name|code page>   Source file encoding. auto (default) reads BOMs and
                                                     UTF-8, else falls back to the system ANSI code page

            Suppress a finding with a comment on the same line or above the code:
              -- plsql-doctor:ignore PD005 (covered by a function-based index)

            Exit codes: 0 = ok, 1 = findings at or above --fail-on, 2 = usage error
            """);
    }
}
