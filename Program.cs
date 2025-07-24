/*
 * program.cs 
 * 
 * replicates the unix WHICH command
 * 
 *  Date        Author          Description
 *  ====        ======          ===========
 *  07-10-25    Craig           initial implementation
 */
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json;

class Program
{
    /*
     * what is an executable?
     */
    static readonly string[] ExecutableExtensions = new[]
    {
        ".exe", ".bat", ".cmd", ".com", ".ps1", ".py", ".sh"
    };

    /*
     * colors
     */
    const string ColorGreen = "\x1b[32m";
    const string ColorBlue = "\x1b[34m";
    const string ColorWhite = "\x1b[37m";
    const string ColorGray = "\x1b[90m";
    const string ColorReset = "\x1b[0m";

    /*
     * flags
     */
    static bool matchAll = false;
    static bool globalSearch = false;
    static bool hiddenOnly = false;
    static bool noHidden = false;
    static bool firstMatch = false;
    static bool colorOutput = true;
    static bool outputJson = false;
    static bool outputCsv = false;
    static bool showSummary = false;
    static bool longFormat = false;
    static bool reverseSort = false;
    static bool disableExtensionFallback = false;
    static string sortField = "";

    static string drives = "";
    static string query = "";
    static Regex? regex = null;

    static HashSet<string> debugFlags = new(StringComparer.OrdinalIgnoreCase);
    static List<FileResult> results = new();

    /*
     * the data structure for results
     */
    class FileResult
    {
        public string Path { get; set; } = "";
        public string Type { get; set; } = "";
        public bool Hidden { get; set; }
        public FileAttributes Attributes { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
    }

    /*
     * Main
     * 
     * This is the entry point of the program.
     */
    static void Main(string[] args)
    {
        /*
         * expand the options
         */
        List<string> expandedArgs = new();
        foreach (var arg in args)
        {
            if (arg.StartsWith("-") && !arg.StartsWith("--") && arg.Length > 2 && !arg.StartsWith("-g"))
                foreach (var ch in arg[1..]) expandedArgs.Add("-" + ch);
            else
                expandedArgs.Add(arg);
        }

        /*
         * parse the arguments
         */
        for (int i = 0; i < expandedArgs.Count; i++)
        {
            string arg = expandedArgs[i];
            switch (arg)
            {
                case "-?": ShowHelp(); return;
                case "-a": matchAll = true; break;
                case "-l": longFormat = true; break;
                case "-n": disableExtensionFallback = true; break;
                case "-s": sortField = "size"; break;
                case "-t": sortField = "time"; break;
                case "-r": reverseSort = true; break;
                case "--hidden-only": hiddenOnly = true; break;
                case "--no-hidden": noHidden = true; break;
                case "--first-match": firstMatch = true; break;
                case "--no-color": colorOutput = false; break;
                case "--json": outputJson = true; break;
                case "--csv": outputCsv = true; break;
                case "--summary": showSummary = true; break;
                default:
                    if (arg.StartsWith("--debug="))
                        foreach (var d in arg[8..].Split(',')) debugFlags.Add(d.Trim());
                    else if (arg.StartsWith("-g"))
                    {
                        globalSearch = true; drives = arg.Length > 2 ? arg[2..].ToUpperInvariant() : "";
                    }
                    if (string.IsNullOrEmpty(query))
                    {
                        query = arg;

                        // If it has no extension, and fallback is enabled, treat as glob for any extension
                        if (!disableExtensionFallback && !query.Contains('.') && !query.Contains('*') && !query.Contains('?'))
                        {
                            query += ".*";
                        }

                        // Compile regex if it now has glob characters
                        if (query.Contains('*') || query.Contains('?'))
                            regex = new Regex(ConvertGlobToRegex(query), RegexOptions.IgnoreCase);
                    }
                    else ExitWithError($"Unrecognized argument: {arg}");
                    break;
            }
        }

        if (string.IsNullOrEmpty(query))
        {
            Console.Error.WriteLine("Error: No search term provided.\n");
            ShowHelp();
            Environment.Exit(1);
        }

        if (hiddenOnly && noHidden)
            ExitWithError("You cannot use both --hidden-only and --no-hidden.");

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (colorOutput) EnableVirtualTerminal();

        /*
         * look across the specified drives
         */
        if (globalSearch)
        {
            if (string.IsNullOrEmpty(drives))
                drives = Path.GetPathRoot(Environment.CurrentDirectory)[0].ToString().ToUpper();

            foreach (char drive in drives)
            {
                string root = $"{drive}:\\";
                if (Directory.Exists(root))
                {
                    RecursiveSearch(root);
                    if (firstMatch && results.Count > 0) break;
                }
            }
        }

        /*
         * look across the path
         */
        else
        {
            string[] paths = Environment.GetEnvironmentVariable("PATH")?
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

            foreach (string dir in paths)
            {
                if (!Directory.Exists(dir)) continue;

                try
                {
                    foreach (var file in Directory.EnumerateFileSystemEntries(dir))
                    {
                        if (MatchFile(file))
                        {
                            results.Add(FormatResult(file));
                            if (firstMatch) break;
                        }
                    }
                }
                catch { }
            }
        }

        /*
         * sort by size or time if requested
         */
        if (!string.IsNullOrEmpty(sortField))
        {
            results = sortField switch
            {
                "size" => results.OrderBy(r => r.Size).ToList(),
                "time" => results.OrderBy(r => r.Modified).ToList(),
                _ => results
            };
        }

        if (reverseSort) results.Reverse();

        /*
         * output formats
         */
        if (outputJson)
            Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        else if (outputCsv)
            foreach (var res in results)
                Console.WriteLine($"\"{res.Path}\",{res.Type},{res.Hidden}");
        else
            foreach (var res in results)
                if (longFormat) PrintLongFormat(res); else PrintColored(res);

        if (showSummary)
            Console.WriteLine($"{results.Count} match(es) found.");
    } /* Main() */

    /*
     * Recursive search through directories
     * 
     * This method scans the directory tree starting from the given root path.
     */
    static void RecursiveSearch(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFileSystemEntries(root))
            {
                if (debugFlags.Contains("scan")) Console.WriteLine($"SCANNING: {file}");
                if (MatchFile(file))
                {
                    if (debugFlags.Contains("match")) Console.WriteLine($"MATCHED: {file}");
                    results.Add(FormatResult(file));
                    if (firstMatch) return;
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                RecursiveSearch(dir);
                if (firstMatch && results.Count > 0) return;
            }
        }
        catch (Exception ex)
        {
            if (debugFlags.Contains("error"))
                Console.Error.WriteLine($"[ERROR] {root}: {ex.Message}");
        }
    } /* RecursiveSearch */

    /*
     * MatchFile
     * 
     * This method checks if a file matches the search criteria.
     */
    static bool MatchFile(string file)
    {
        string name = Path.GetFileName(file);
        bool isDir = Directory.Exists(file);

        try
        {
            var attr = File.GetAttributes(file);
            bool isHidden = attr.HasFlag(FileAttributes.Hidden);
            bool matchesPattern =
                                regex != null
                                    ? regex!.IsMatch(name)
                                    : name.Equals(query, StringComparison.OrdinalIgnoreCase);

            if (debugFlags.Contains("check"))
                Console.WriteLine($"CHECKING: {file} — H:{isHidden}, P:{matchesPattern}");

            if (hiddenOnly) return isHidden && matchesPattern;
            if (noHidden && isHidden) return false;
            if (!matchAll && !isDir && !ExecutableExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                return false;

            return matchesPattern;
        }
        catch { return false; }

    } /* MatchFile */

    /*
     * FormatResult
     * 
     * This method formats the file result into a structured object.
     */
    static FileResult FormatResult(string path)
    {
        var info = new FileInfo(path);
        var attr = info.Attributes;

        string type = attr.HasFlag(FileAttributes.Directory) ? "Directory" :
            ExecutableExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) ? "Executable" : "File";

        return new FileResult
        {
            Path = path,
            Type = type,
            Hidden = attr.HasFlag(FileAttributes.Hidden),
            Attributes = attr,
            Size = attr.HasFlag(FileAttributes.Directory) ? 0 : info.Length,
            Modified = info.LastWriteTime
        };
    } /* FormatResult */

    /*
     * PrintColored
     * 
     * print according to the type of file
     */
    static void PrintColored(FileResult res)
    {
        string color = res.Hidden ? ColorGray :
                       res.Type == "Directory" ? ColorBlue :
                       res.Type == "Executable" ? ColorGreen : ColorWhite;

        if (!colorOutput) color = "";
        Console.WriteLine($"{color}{res.Path}{ColorReset}");

    } /* PrintColored */

    /*
     * PrintLongFormat
     * 
     * print in long format with attributes, size, time, and full path
     */
    static void PrintLongFormat(FileResult res)
    {
        string attr = BuildAttrString(res.Attributes);
        string size = FormatSize(res.Size).PadLeft(10);
        string time = res.Modified.ToString("MMM dd yyyy  HH:mm");
        string name = res.Path; // ✅ full path now shown
        if (colorOutput)
            name = $"{GetColor(res)}{name}{ColorReset}";
        Console.WriteLine($"{attr}  {size}  {time}  {name}");

    } /* PrintLongFormat */

    /*
     * BuildAttrString
     * 
     * builds a string representation of file attributes
     */
    static string BuildAttrString(FileAttributes attr)
    {
        return string.Concat(
            attr.HasFlag(FileAttributes.Directory) ? 'd' : '-',
            attr.HasFlag(FileAttributes.Hidden) ? 'h' : '-',
            attr.HasFlag(FileAttributes.System) ? 's' : '-',
            attr.HasFlag(FileAttributes.ReadOnly) ? 'r' : '-',
            attr.HasFlag(FileAttributes.Archive) ? 'a' : '-',
            attr.HasFlag(FileAttributes.Temporary) ? 't' : '-',
            attr.HasFlag(FileAttributes.Normal) ? 'w' : '-'
        );
    } /* BuildAttrString */

    /*
     * FormatSize
     * 
     * formats the size in a human-readable format
     */
    static string FormatSize(long size)
    {
        if (size >= 1L << 30) return $"{size / (1L << 30):0.##} GB";
        if (size >= 1L << 20) return $"{size / (1L << 20):0.##} MB";
        if (size >= 1L << 10) return $"{size / (1L << 10):0.##} KB";
        return $"{size} B";
    } /* FormatSize */

    /*
     * GetColor
     * 
     * returns the color code for the file type
     */
    static string GetColor(FileResult res)
    {
        if (!colorOutput) return "";
        return res.Hidden ? ColorGray :
               res.Type == "Directory" ? ColorBlue :
               res.Type == "Executable" ? ColorGreen : ColorWhite;
    } /* GetColor */

    /*
     * ConvertGlobToRegex
     * 
     * converts a glob pattern (e.g. *.exe) to a regex pattern
     */
    static string ConvertGlobToRegex(string glob)
    {
        return "^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
    } /* ConvertGlobToRegex */

    /*
     * ShowHelp
     * 
     * displays the help message
     */
    static void ShowHelp()
    {
        Console.WriteLine("which [-a] [-l] [-s] [-t] [-r] [-g[drives]] [--debug=...] <query>");
        Console.WriteLine("Options:");
        Console.WriteLine("  -a                Match any file, including directories");
        Console.WriteLine("  -l                Long-format output");
        Console.WriteLine("  -n                Do not auto-append .* to extensionless queries");
        Console.WriteLine("  -s                Sort by size");
        Console.WriteLine("  -t                Sort by time");
        Console.WriteLine("  -r                Reverse sort order");
        Console.WriteLine("  -g[CDZ]           Global search on specified drives");
        Console.WriteLine("  --hidden-only     Show only hidden files");
        Console.WriteLine("  --no-hidden       Exclude hidden files");
        Console.WriteLine("  --first-match     Stop after first match");
        Console.WriteLine("  --no-color        Disable color output");
        Console.WriteLine("  --json            Output in JSON format");
        Console.WriteLine("  --csv             Output in CSV format");
        Console.WriteLine("  --summary         Show match count");
        Console.WriteLine("  --debug=FLAGS     Enable debug tracing (scan,match,check,error)");

    } /* ShowHelp */

    /*
     * ExitWithError
     * 
     *  as it sounds
     */
    static void ExitWithError(string message)
    {
        Console.Error.WriteLine("Error: " + message);
        Environment.Exit(1);

    } /* ExitWithError */

    /*
     * EnableVirtualTerminal
     * 
     * enables virtual terminal processing for colored output in Windows console
     */
    static void EnableVirtualTerminal()
    {
        try
        {
            var handle = NativeMethods.GetStdHandle(-11);
            NativeMethods.GetConsoleMode(handle, out int mode);
            NativeMethods.SetConsoleMode(handle, mode | 0x4);
        }
        catch { }
    } /* EnableVirtualTerminal */

    /*
     * NativeMethods
     * 
     * contains P/Invoke methods for console mode manipulation
     */
    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] internal static extern IntPtr GetStdHandle(int nStdHandle);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] internal static extern bool GetConsoleMode(IntPtr hConsoleHandle, out int lpMode);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] internal static extern bool SetConsoleMode(IntPtr hConsoleHandle, int dwMode);
    }
}
