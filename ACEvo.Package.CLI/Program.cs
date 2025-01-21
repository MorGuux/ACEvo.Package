using System.Buffers.Binary;
using System.IO.Compression;
using System.Globalization;

using Microsoft.Extensions.Logging;

using CommandLine;

using NLog;
using NLog.Extensions.Logging;

using System.Diagnostics;
using System.Reflection;

namespace ACEvo.Package.CLI;

public class Program
{
    private static ILoggerFactory _loggerFactory;
    private static Microsoft.Extensions.Logging.ILogger _logger;

    static void Main(string[] args)
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.AddNLog());
        _logger = _loggerFactory.CreateLogger<Program>();

        Console.WriteLine("------------------------------------------------------------");
        Console.WriteLine($"- ACEvo.Package.CLI {Assembly.GetEntryAssembly().GetName().Version}, originally written by Nenkai");
        Console.WriteLine("------------------------------------------------------------");
        Console.WriteLine("- https://github.com/Nenkai");
        Console.WriteLine("- https://twitter.com/Nenkaai");
        Console.WriteLine("------------------------------------------------------------");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("---- Modified by MorGuux for Vortex Mod Manager support ----");
        Console.ResetColor();
        Console.WriteLine("------------------------------------------------------------");
        Console.WriteLine("");

        var p = Parser.Default
            .ParseArguments<UnpackFileVerbs, UnpackVerbs, ListFilesVerbs, FileExistsVerbs, PackVerbs, PatchVerbs>(args)
            .WithParsed<UnpackFileVerbs>(UnpackFile)
            .WithParsed<UnpackVerbs>(Unpack)
            .WithParsed<ListFilesVerbs>(ListFiles)
            .WithParsed<FileExistsVerbs>(FileExists)
            .WithParsed<PackVerbs>(Pack)
            .WithParsed<PatchVerbs>(Patch);
    }

    static void UnpackFile(UnpackFileVerbs verbs)
    {
        if (!File.Exists(verbs.InputFile))
        {
            _logger.LogError("File '{path}' does not exist", verbs.InputFile);
            Environment.Exit(2);
            return;
        }

        if (string.IsNullOrEmpty(verbs.OutputPath))
        {
            string inputFileName = Path.GetFileNameWithoutExtension(verbs.InputFile);
            verbs.OutputPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(verbs.InputFile)), $"{inputFileName}.extracted");
        }

        try
        {
            using var pack = PackFile.Open(verbs.InputFile, _loggerFactory);

            _logger.LogInformation("Starting unpack process.");
            if (!pack.ExtractFile(verbs.FileToUnpack, verbs.OutputPath))
                _logger.LogWarning("File '{file}' not found in pack.", verbs.FileToUnpack);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unpack.");
            Environment.Exit(2);
        }
    }

    private static void Unpack(UnpackVerbs verbs)
    {
        if (!File.Exists(verbs.InputFile))
        {
            _logger.LogError("File '{path}' does not exist", verbs.InputFile);
            Environment.Exit(2);
            return;
        }

        if (string.IsNullOrEmpty(verbs.OutputPath))
        {
            string inputFileName = Path.GetFileNameWithoutExtension(verbs.InputFile);
            verbs.OutputPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(verbs.InputFile)), $"{inputFileName}.extracted");
        }

        try
        {
            using var pack = PackFile.Open(verbs.InputFile, _loggerFactory);

            _logger.LogInformation("Starting unpack process.");
            pack.ExtractAll(verbs.OutputPath);
            _logger.LogInformation("Done.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unpack.");
            Environment.Exit(13);
        }
    }

    private static void ListFiles(ListFilesVerbs verbs)
    {
        if (!File.Exists(verbs.InputFile))
        {
            _logger.LogError("File '{path}' does not exist", verbs.InputFile);
            Environment.Exit(2);
            return;
        }

        try
        {
            using var pack = PackFile.Open(verbs.InputFile, _loggerFactory);

            string inputFileName = Path.GetFileNameWithoutExtension(verbs.InputFile);
            string outputPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(verbs.InputFile)), $"{inputFileName}_files.txt");
            pack.ListFiles(outputPath);
            _logger.LogInformation("Done. ({path})", outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read pack.");
            Environment.Exit(13);
        }
    }

    private static void FileExists(FileExistsVerbs verbs)
    {
        if (!File.Exists(verbs.InputFile))
        {
            _logger.LogError("File '{path}' does not exist", verbs.InputFile);
            Environment.Exit(2);
            return;
        }

        try
        {
            using var pack = PackFile.Open(verbs.InputFile, _loggerFactory);

            var fileExists = pack.FileExists(verbs.FileToCheck);
            _logger.LogInformation("File \"{path}\" found: {exists}", verbs.FileToCheck, fileExists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read pack.");
            Environment.Exit(13);
        }
    }

    private static void Pack(PackVerbs verbs)
    {
        if (!Directory.Exists(verbs.InputDirectory))
        {
            _logger.LogError("Directory '{path}' does not exist", verbs.InputDirectory);
            Environment.Exit(2);
            return;
        }

        if (string.IsNullOrEmpty(verbs.OutputFile))
        {
            string inputDirName = new DirectoryInfo(verbs.InputDirectory).Name;
            verbs.OutputFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(verbs.InputDirectory)), $"{inputDirName}.kspkg");
        }

        try
        {
            using var pack = PackFile.Create(verbs.OutputFile, _loggerFactory);
            _logger.LogInformation("Starting pack process from '{dir}'", verbs.InputDirectory);

            // Get all files and directories
            var allFiles = Directory.GetFiles(verbs.InputDirectory, "*", SearchOption.AllDirectories);
            var allDirs = Directory.GetDirectories(verbs.InputDirectory, "*", SearchOption.AllDirectories);

            // Add directories first
            foreach (var dir in allDirs)
            {
                string relativePath = Path.GetRelativePath(verbs.InputDirectory, dir).Replace('\\', '/');
                pack.AddDirectory(relativePath);
            }

            // Then add files
            foreach (var file in allFiles)
            {
                string relativePath = Path.GetRelativePath(verbs.InputDirectory, file).Replace('\\', '/');
                pack.AddFile(relativePath, file);
            }

            pack.Finalize();
            _logger.LogInformation("Done. Pack file created at '{path}'", verbs.OutputFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create pack.");
            Environment.Exit(13);
        }
    }

    private static void Patch(PatchVerbs verbs)
    {
        if (!Directory.Exists(verbs.InputDirectory))
        {
            _logger.LogError("Directory '{path}' does not exist", verbs.InputDirectory);
            Environment.Exit(2);
            return;
        }

        if (!File.Exists(verbs.OutputFile))
        {
            _logger.LogError("File '{path}' does not exist", verbs.OutputFile);
            Environment.Exit(2);
            return;
        }

        try
        {
            using (var pack = PackFile.Open(verbs.OutputFile, _loggerFactory))
            {
                _logger.LogInformation("Starting patch process from '{dir}'", verbs.InputDirectory);
                _logger.LogInformation("Found {count} files in pack.", pack.FileCount);

                // Get all files and directories
                var allFiles = Directory.GetFiles(verbs.InputDirectory, "*", SearchOption.AllDirectories);
                var allDirs = Directory.GetDirectories(verbs.InputDirectory, "*", SearchOption.AllDirectories);

                // Add directories first
                foreach (var dir in allDirs)
                {
                    string relativePath = Path.GetRelativePath(verbs.InputDirectory, dir);
                    _logger.LogInformation("Adding directory '{path}'", relativePath);
                    pack.AddDirectory(relativePath);
                }

                // Then add files
                foreach (var file in allFiles)
                {
                    string relativePath = Path.GetRelativePath(verbs.InputDirectory, file);
                    _logger.LogInformation("Adding file '{path}'", relativePath);
                    pack.AddFile(relativePath, file, verbs.BackupPath);
                }

                pack.Finalize();
                _logger.LogInformation("Done. Pack file modified at '{path}'", verbs.OutputFile);

                //Success
                Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to modify pack.");
            Environment.Exit(13);
        }
    }
}

[Verb("unpack", HelpText = "Unpacks a .kspkg file.")]
public class UnpackVerbs
{
    [Option('i', "input", Required = true, HelpText = "Input .kspkg file")]
    public string InputFile { get; set; }

    [Option('o', "output", HelpText = "Output directory. Optional, defaults to a folder named the same as the .kspkg file.")]
    public string OutputPath { get; set; }
}

[Verb("unpack-file", HelpText = "Unpacks a specific file from a .kspkg pack.")]
public class UnpackFileVerbs
{
    [Option('i', "input", Required = true, HelpText = "Input .kspkg pack")]
    public string InputFile { get; set; }

    [Option('f', "file", Required = true, HelpText = "File to unpack.")]
    public string FileToUnpack { get; set; }

    [Option('o', "output", HelpText = "Optional. Output directory.")]
    public string OutputPath { get; set; }
}

[Verb("list-files", HelpText = "List files in a .kspkg pack.")]
public class ListFilesVerbs
{
    [Option('i', "input", Required = true, HelpText = "Input .kspkg pack")]
    public string InputFile { get; set; }
}

[Verb("file-exists", HelpText = "Does a file exists in a .kspkg pack. Returns true if the file exists, false if the file does not exist.")]
public class FileExistsVerbs
{
    [Option('i', "input", Required = true, HelpText = "Input .kspkg pack")]
    public string InputFile { get; set; }

    [Option('f', "file", Required = true, HelpText = "File to check.")]
    public string FileToCheck { get; set; }
}

[Verb("pack", HelpText = "Creates a new .kspkg file from a directory.")]
public class PackVerbs
{
    [Option('i', "input", Required = true, HelpText = "Input directory to pack")]
    public string InputDirectory { get; set; }

    [Option('o', "output", HelpText = "Output .kspkg file. Optional, defaults to directory name + .kspkg")]
    public string OutputFile { get; set; }
}

[Verb("patch", HelpText = "Modifies an existing .kspkg file.")]
public class PatchVerbs
{
    [Option('i', "input", Required = true, HelpText = "Input directory to patch")]
    public string InputDirectory { get; set; }

    [Option('o', "output", Required = true, HelpText = "Output .kspkg file to modify")]
    public string OutputFile { get; set; }

    [Option('b', "backup", HelpText = "Optional. Backup directory.")]
    public string BackupPath { get; set; }
}