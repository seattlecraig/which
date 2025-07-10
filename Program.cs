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
using System.Text.RegularExpressions;

class Program
{
    static readonly string[] ExecutableExtensions = new[]
    {
        ".exe", ".bat", ".cmd", ".com", ".ps1", ".py", ".sh"
    };

    const string ColorGreen = "\x1b[32m";
    const string ColorBlue = "\x1b[34m";
    const string ColorWhite = "\x1b[37m";
    const string ColorReset = "\x1b[0m";

    static void Main(string[] args)
    {
        bool useRegex = false;
        bool matchAll = false;
        bool globalSearch = false;
        string drives = "";
        string pattern = "";
        string query = "";

        /*
         * Parse args
         */
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-?":
                    ShowHelp();
                    return;
                case "-a":
                    matchAll = true;
                    break;
                case "-r":
                    useRegex = true;
                    if (i + 1 < args.Length) pattern = args[++i];
                    else ExitWithError("Missing regex pattern after -r.");
                    break;
                case "-g":
                    globalSearch = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                        drives = args[++i].ToUpperInvariant();
                    break;
                default:
                    if (string.IsNullOrEmpty(query))
                        query = args[i];
                    else
                        ExitWithError($"Unrecognized argument: {args[i]}");
                    break;
            }
        }

        if (string.IsNullOrEmpty(query) && !useRegex)
        {
            Console.Error.WriteLine("Error: No search term provided.\n");
            ShowHelp();
            Environment.Exit(1);
        }

        /*
         * set up regular expression if needed
         */
        Regex? regex = null;
        if (useRegex)
        {
            try
            {
                regex = new Regex(pattern, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                ExitWithError($"Invalid regex: {ex.Message}");
            }
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        EnableVirtualTerminal();

        /*
         * search the entire set of specified drives
         */
        if (globalSearch)
        {
            if (string.IsNullOrEmpty(drives))
            {
                drives = Path.GetPathRoot(Environment.CurrentDirectory)[0].ToString().ToUpper();
            }

            foreach (char drive in drives)
            {
                string root = $"{drive}:\\";
                if (Directory.Exists(root))
                    RecursiveSearch(root, regex, query, matchAll, useRegex);
            }
        }
        /*
         * search the PATH environment variable
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
                    var matches = Directory.EnumerateFiles(dir)
                        .Where(file => MatchFile(file, regex, query, matchAll, useRegex));

                    foreach (var match in matches)
                        PrintColored(match);
                }
                catch { }
            }
        }
    } /* Main() */

    /*
     * RecursiveSearch
     * 
     * Recursively search directories for matching files useing regex or simple substring matching.
     */
    static void RecursiveSearch(string root, Regex? regex, string query, bool matchAll, bool useRegex)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root))
            {
                if (MatchFile(file, regex, query, matchAll, useRegex))
                    PrintColored(file);
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                RecursiveSearch(dir, regex, query, matchAll, useRegex);
            }
        }
        catch 
        { 
            /* silently skip access errors */
        }
    } /* RecursiveSearch */

    /*
     * MatchFile
     * 
     * Check if the file matches the search criteria based on regex or substring.
     */
    static bool MatchFile(string file, Regex? regex, string query, bool matchAll, bool useRegex)
    {
        string name = Path.GetFileName(file);
        if (!matchAll && !ExecutableExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            return false;

        return useRegex ? regex!.IsMatch(name) : name.ToLowerInvariant().Contains(query);

    } /* MatchFile */

    /*
     * PrintColored
     * 
     * Print the file path with appropriate color based on type (directory, executable, etc.)
     */
    static void PrintColored(string path)
    {
        string color = ColorWhite;

        if (Directory.Exists(path))
            color = ColorBlue;
        else if (ExecutableExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            color = ColorGreen;

        Console.WriteLine($"{color}{path}{ColorReset}");

    } /* PrintColored */

    /*
     * ShowHelp
     * 
     * Display the help message for the command.
     */
    static void ShowHelp()
    {
        Console.WriteLine("which [-a] [-r <regex>] [-g [drives]] <query>");
        Console.WriteLine("Searches for matching files in PATH or entire drives.\n");
        Console.WriteLine("Options:");
        Console.WriteLine("  -?            Show help");
        Console.WriteLine("  -a            Match any file, not just executables");
        Console.WriteLine("  -r <pattern>  Use regular expression matching");
        Console.WriteLine("  -g [CDZ]      Global recursive search on specified drives");
        Console.WriteLine("                If no drives specified, uses current drive.");
        Console.WriteLine();
    } /* ShowHelp */

    /*
     * ExitWithError
     * 
     * as it says, exits with an error message
     */
    static void ExitWithError(string message)
    {
        Console.Error.WriteLine("Error: " + message);
        Environment.Exit(1);
    } /* ExitWithError */

    /*
     * EnableVirtualTerminal
     * 
     * Enables ANSI escape codes for colored output on Windows 10+.
     */
    static void EnableVirtualTerminal()
    {
        // Enable ANSI escape codes on Windows 10+
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
     * Contains declarations for enabling virtual terminal processing on Windows.
     */
    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] internal static extern IntPtr GetStdHandle(int nStdHandle);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] internal static extern bool GetConsoleMode(IntPtr hConsoleHandle, out int lpMode);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] internal static extern bool SetConsoleMode(IntPtr hConsoleHandle, int dwMode);

    } /* NativeMethods */
}
