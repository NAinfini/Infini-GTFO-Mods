using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;

namespace ForgeDevelopment.Native;

/// <summary>
/// Command files live in the repository under <c>probes/commands</c> and are copied once into
/// <c>BepInEx/config/ForgeDevelopment/commands</c>. After that the config directory is the source of truth,
/// so a new experiment is a new file and never a rebuild.
/// </summary>
internal static class ExperimentCatalog
{
    private const string ConfigFolder = "commands";

    private static readonly List<ExperimentCommand> Commands = new();
    private static readonly List<string> Problems = new();

    internal static IReadOnlyList<ExperimentCommand> All => Commands;
    internal static IReadOnlyList<string> LoadProblems => Problems;
    internal static string DirectoryPath => Path.Combine(Paths.BepInExRootPath, "config", "ForgeDevelopment", ConfigFolder);

    internal static void Load()
    {
        Commands.Clear();
        Problems.Clear();
        var directory = DirectoryPath;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
        }
        catch (Exception e)
        {
            Problems.Add("the command directory could not be created: " + e.Message);
            return;
        }

        var files = System.IO.Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
        {
            Problems.Add("no command files in " + directory + "; copy probes/commands there to run an experiment");
            return;
        }
        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception e)
            {
                Problems.Add(Path.GetFileName(file) + ": " + e.Message);
                continue;
            }
            var parsed = ExperimentParser.Parse(text, Path.GetFileName(file), out var error);
            if (error.Length != 0)
            {
                Problems.Add(error);
                continue;
            }
            if (parsed.Count > ExperimentParser.MaximumCommandsPerFile)
                Problems.Add(Path.GetFileName(file) + ": " + parsed.Count + " commands; the limit is " + ExperimentParser.MaximumCommandsPerFile);
            foreach (var command in parsed.Take(ExperimentParser.MaximumCommandsPerFile))
            {
                if (command.Valid) Commands.Add(command);
                else Problems.Add(command.Source + ": " + string.Join("; ", command.Problems));
            }
        }
    }

    /// <summary>First-run seeding from the shipped <c>probes/commands</c> folder, if the config folder is empty.
    /// Returns a sentence describing what happened, never null.</summary>
    internal static string Seed(string sourceDirectory)
    {
        if (!System.IO.Directory.Exists(sourceDirectory)) return "no shipped commands at " + sourceDirectory;
        var directory = DirectoryPath;
        var copied = 0;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            foreach (var file in System.IO.Directory.GetFiles(sourceDirectory, "*.json", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                var target = Path.Combine(directory, name);
                if (File.Exists(target)) continue;
                File.Copy(file, target);
                copied++;
            }
        }
        catch (Exception e)
        {
            return "seeding commands failed: " + e.Message;
        }
        return copied == 0 ? "no new command files to seed" : "seeded " + copied + " command files";
    }
}
