using System.Text.Json;
using ForgeDevelopment.Native;

internal static class ShippedCommandTests
{
    internal static void Run(Suite suite)
    {
        var directory = Locate("ForgeDevelopment/probes/commands");
        if (directory == null)
        {
            suite.Check("commands.directory", false, "ForgeDevelopment/probes/commands was not found from " + Environment.CurrentDirectory);
            return;
        }
        var files = Directory.GetFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        suite.Check("commands.present", files.Length > 0, "no command files in " + directory);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var commands = ExperimentParser.Parse(File.ReadAllText(file), name, out var error);
            suite.Check("commands.parse." + name, error.Length == 0, error);
            foreach (var command in commands)
            {
                suite.Check("commands.valid." + name + ":" + command.Id, command.Valid, string.Join("; ", command.Problems));
                suite.Check("commands.uniqueId." + command.Id, ids.Add(command.Id), "duplicate command id " + command.Id);
                suite.Check("commands.points." + command.Id, command.Points.Count > 0, "every command names the points it covers");
                suite.Check("commands.distinctPoints." + command.Id, command.Points.Distinct(StringComparer.Ordinal).Count() == command.Points.Count,
                    "a command lists the same point twice: " + string.Join(", ", command.Points));
                foreach (var point in command.Points) covered.Add(point);
            }
        }
        CheckAgainstPointList(suite, covered);
    }

    /// <summary>
    /// Every point a command claims must exist in the capture batch's point list, and any point that says
    /// it is collected by exp:&lt;file&gt; must be claimed by a command in that file.
    /// </summary>
    private static void CheckAgainstPointList(Suite suite, HashSet<string> covered)
    {
        var points = Locate("ForgeDevelopment/probes/points.tsv");
        if (points == null)
        {
            suite.Check("commands.pointsFile", false, "ForgeDevelopment/probes/points.tsv was not found from " + Environment.CurrentDirectory +
                "; direct=" + File.Exists(Path.Combine(Environment.CurrentDirectory, "ForgeDevelopment", "probes", "points.tsv")));
            return;
        }
        var known = new HashSet<string>(StringComparer.Ordinal);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(points).Skip(1))
        {
            var columns = line.Split('\t');
            if (columns.Length < 5) continue;
            known.Add(columns[0]);
            if (!columns[4].StartsWith("exp:", StringComparison.Ordinal)) continue;
            foreach (var part in columns[4].Split(';'))
            {
                var capture = part.Trim();
                if (!capture.StartsWith("exp:", StringComparison.Ordinal)) continue;
                expected[capture[4..].Trim()] = columns[0];
            }
        }
        suite.Check("commands.pointListRead", known.Count > 0, "the point list has no rows");
        foreach (var point in covered.OrderBy(value => value, StringComparer.Ordinal))
            suite.Check("commands.pointExists." + point, known.Contains(point), "the command names a point id that points.tsv does not contain");
        foreach (var (file, point) in expected.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var key = file + ":" + point;
            suite.Check("commands.pointCovered." + key, covered.Contains(point),
                "points.tsv says exp:" + file + " collects " + point + ", but no command in probes/commands claims it");
        }
    }

    /// <summary>Finds a repository file from the process working directory or the assembly location.</summary>
    internal static string? Locate(string relative)
    {
        // The suite runs from the repository root (`dotnet <artifact>.dll`); the base directory is a
        // temporary artifacts path outside the tree, so walking up from it never reaches the sources.
        foreach (var root in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(root); directory != null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}

