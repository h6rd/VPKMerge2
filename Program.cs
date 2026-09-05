using System.Text.RegularExpressions;
using SteamDatabase.ValvePak;

namespace VPKMerge;

internal static partial class Program
{
    [GeneratedRegex(@"[^a-zA-Z0-9]$")]
    private static partial Regex GarbageSuffixRe();

    private static readonly string[] AsciiLines =
    {
        "██╗   ██╗██████╗ ██╗  ██╗███╗   ███╗███████╗██████╗  ██████╗ ███████╗",
        "██║   ██║██╔══██╗██║ ██╔╝████╗ ████║██╔════╝██╔══██╗██╔════╝ ██╔════╝",
        "██║   ██║██████╔╝█████╔╝ ██╔████╔██║█████╗  ██████╔╝██║  ███╗█████╗  ",
        "╚██╗ ██╔╝██╔═══╝ ██╔═██╗ ██║╚██╔╝██║██╔══╝  ██╔══██╗██║   ██║██╔══╝  ",
        " ╚████╔╝ ██║     ██║  ██╗██║ ╚═╝ ██║███████╗██║  ██║╚██████╔╝███████╗",
        "  ╚═══╝  ╚═╝     ╚═╝  ╚═╝╚═╝     ╚═╝╚══════╝╚═╝  ╚═╝ ╚═════╝ ╚══════╝",
    };

    private static void Main()
    {
        PrintAsciiArt();

        var cwd = Directory.GetCurrentDirectory();

        var vpks = Directory.GetFiles(cwd, "*.vpk")
            .OrderBy(f => Path.GetFileName(f).StartsWith('!'))
            .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (vpks.Count == 0)
        {
            WriteLine("No VPK files found in the current directory.", ConsoleColor.Red);
            return;
        }

        var tempDir = Path.Combine(cwd, "temp");
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);

        var seenFiles = new Dictionary<string, string>();
        var totalSkipped = 0;
        var totalExtracted = 0;

        foreach (var vpkFile in vpks)
        {
            WriteLine($"Extracting {Path.GetFileName(vpkFile)}", ConsoleColor.White);

            var (success, skippedFiles, extractedCount) = ExtractVpk(vpkFile, tempDir, seenFiles);

            if (success)
            {
                totalExtracted += extractedCount;
                totalSkipped += skippedFiles.Count;

                if (skippedFiles.Count > 0)
                {
                    WriteLine($"  Skipped {skippedFiles.Count} useless files", ConsoleColor.Yellow);
                    foreach (var s in skippedFiles.Take(3))
                        WriteLine($"    - {s}", ConsoleColor.Yellow);
                    if (skippedFiles.Count > 3)
                        WriteLine($"    ... and {skippedFiles.Count - 3} more", ConsoleColor.Yellow);
                }

                WriteLine($"  Extracted {extractedCount} files", ConsoleColor.Green);
                File.Delete(vpkFile);
            }
            else
            {
                WriteLine($"Failed to extract {Path.GetFileName(vpkFile)}", ConsoleColor.Red);
            }
        }

        if (totalSkipped > 0)
            WriteLine($"\nTotal files skipped: {totalSkipped}", ConsoleColor.Yellow);
        WriteLine($"Total files extracted: {totalExtracted}", ConsoleColor.Green);

        const string outputVpkName = "pak10_dir.vpk";
        var outputVpkPath = Path.Combine(cwd, outputVpkName);

        WriteLine("\nCreating combined VPK file...", ConsoleColor.Cyan);

        var (ok, buildMsg) = BuildVpk(tempDir, outputVpkPath);

        Directory.Delete(tempDir, true);

        if (!ok)
        {
            WriteLine($"Error creating VPK: {buildMsg}", ConsoleColor.Red);
            Thread.Sleep(3000);
            return;
        }

        WriteLine($"Built: {buildMsg}", ConsoleColor.DarkGreen);

        var (verifyOk, verifyMsg) = VerifyVpk(outputVpkPath);
        if (verifyOk)
        {
            Console.WriteLine();
            Write("Success created ", ConsoleColor.Green);
            WriteLine(outputVpkName, ConsoleColor.White);
            WriteLine($"Verified: {verifyMsg}", ConsoleColor.DarkGreen);
        }
        else
        {
            WriteLine($"\nWarning: VPK created but verification failed: {verifyMsg}", ConsoleColor.Red);
        }

        Thread.Sleep(3000);
    }

    private static bool ShouldSkipFile(string fileName)
    {
        if (fileName.Contains('"'))
            return true;
        if (GarbageSuffixRe().IsMatch(fileName))
            return true;
        return false;
    }

    private static (bool success, List<string> skipped, int extractedCount) ExtractVpk(
        string vpkPath, string outputDir, Dictionary<string, string> seenFiles)
    {
        var skipped = new List<string>();
        var extractedCount = 0;

        try
        {
            using var package = new Package();
            package.Read(vpkPath);

            foreach (var entry in package.Entries.Values.SelectMany(list => list))
            {
                var fullPath = entry.GetFullPath();
                var fileName = entry.GetFileName();

                if (ShouldSkipFile(fileName))
                {
                    skipped.Add(fullPath);
                    continue;
                }

                var norm = fullPath.Replace('\\', '/').ToLowerInvariant();
                seenFiles[norm] = Path.GetFileName(vpkPath);

                try
                {
                    package.ReadEntry(entry, out byte[] data);
                    var destPath = Path.Combine(outputDir, fullPath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    File.WriteAllBytes(destPath, data);
                    extractedCount++;
                }
                catch
                {
                    skipped.Add(fullPath);
                }
            }

            return (true, skipped, extractedCount);
        }
        catch
        {
            return (false, new List<string>(), 0);
        }
    }

    private static (bool ok, string message) BuildVpk(string sourceDir, string outputPath)
    {
        try
        {
            using var package = new Package();

            var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
                .Select(f => (full: f, rel: Path.GetRelativePath(sourceDir, f).Replace(Path.DirectorySeparatorChar, '/')))
                .OrderBy(t => t.rel, StringComparer.Ordinal)
                .ToList();

            foreach (var (full, rel) in files)
            {
                var data = File.ReadAllBytes(full);
                package.AddFile(rel, data);
            }

            package.Write(outputPath);

            var sizeMb = new FileInfo(outputPath).Length / (1024.0 * 1024.0);
            return (true, $"{files.Count} files, size {sizeMb:F2} MB");
        }
        catch (Exception e)
        {
            return (false, $"Error building VPK: {e.Message}");
        }
    }

    private static (bool ok, string message) VerifyVpk(string vpkPath)
    {
        try
        {
            using var package = new Package();
            package.Read(vpkPath);

            var totalFiles = package.Entries?.Values.Sum(list => list.Count) ?? 0;
            if (totalFiles == 0)
                return (false, "VPK contains no files");

            package.VerifyHashes();
            package.VerifyFileChecksums();

            return (true, $"{totalFiles} files, tree/whole-file MD5 and CRC32 all OK");
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }

    private static void PrintAsciiArt()
    {
        int width;
        try { width = Console.WindowWidth; } catch { width = 80; }
        if (width <= 0) width = 80;

        Console.WriteLine();
        foreach (var line in AsciiLines)
            WriteLine(Center(line, width), ConsoleColor.Magenta);
        WriteLine(Center("by @dota2pornfx", width), ConsoleColor.White);
        Console.WriteLine();
    }

    private static string Center(string s, int width)
    {
        if (s.Length >= width) return s;
        var totalPad = width - s.Length;
        var left = totalPad / 2;
        var right = totalPad - left;
        return new string(' ', left) + s + new string(' ', right);
    }

    private static void Write(string msg, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(msg);
        Console.ForegroundColor = prev;
    }

    private static void WriteLine(string msg, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(msg);
        Console.ForegroundColor = prev;
    }
}
