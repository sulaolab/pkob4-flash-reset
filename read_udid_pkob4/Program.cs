// read_udid_pkob4 - native wrapper around MPLAB X mdb.bat that reads the target
// dsPIC33AK Unique Device ID (UDID) over PKOB4, selecting a board by PKOB4 serial.
//
// It writes a temporary MDB script equivalent to:
//   Device <device>
//   Hwtool PKOB4 -p <sn><serial>
//   Quit
// then executes mdb.bat and parses the four "UDIDn = 0x........" lines that MDB
// prints during the target-connect / device-id readout.
//
// NOTE on the read path: the MDB `x /U4xw 0x7F2BE0` memory-read form does NOT work
// on the dsPIC33AK MP_DFP tested (it returns 0xFFFFFFFF / 0x00000000 garbage -- the
// "U" memory is not mapped for `x` in that pack). The reliable path is the UDID that
// MDB emits automatically on connect, which is what this tool parses. The values
// match the on-chip read (firmware reading 0x007F2BE0..EC) exactly.
//
// Exit codes: 0 success, 1 invalid args, 2 MPLAB X/mdb not found,
//             3 read failed (target/UDID not found), 4 timeout after retry,
//             5 unexpected exception.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

internal static class Program
{
    private const string DefaultDevice = "dsPIC33AK512MPS512";
    private const string ToolType = "PKOB4";
    private const int UdidWordCount = 4;

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Unexpected error: " + ex.Message);
            return 5;
        }
    }

    private static int Run(string[] args)
    {
        string? serial = null;
        string device = DefaultDevice;
        int timeoutSec = 60;
        int retry = 1;
        bool verbose = false;
        bool dryRun = false;
        bool list = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--list": list = true; break;
                case "--serial": serial = NextArg(args, ref i); break;
                case "--device": device = NextArg(args, ref i); break;
                case "--timeout":
                    if (!int.TryParse(NextArg(args, ref i), out timeoutSec) || timeoutSec <= 0)
                    {
                        return Invalid("--timeout must be a positive integer (seconds)");
                    }
                    break;
                case "--retry":
                    if (!int.TryParse(NextArg(args, ref i), out retry) || retry < 0)
                    {
                        return Invalid("--retry must be >= 0");
                    }
                    break;
                case "--verbose": verbose = true; break;
                case "--dry-run": dryRun = true; break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return 0;
                default:
                    return Invalid("unknown argument: " + arg);
            }
        }

        // --list just enumerates connected PKOB4 serials and exits (no serial needed).
        if (list)
        {
            return ListPkob4();
        }

        if (string.IsNullOrWhiteSpace(serial))
        {
            return Invalid("--serial <PKOB4 serial> is required");
        }

        if (!TryDetectMdb(out string mplabRoot, out string mdbBat, out string detectErr))
        {
            Console.Error.WriteLine("MPLAB X mdb.bat not found.");
            Console.Error.WriteLine("Reason: " + detectErr);
            return 2;
        }

        string scriptPath = Path.Combine(Path.GetTempPath(), $"read_udid_pkob4_{Environment.ProcessId}_{Guid.NewGuid():N}.txt");
        string[] scriptLines =
        {
            "Device " + device,
            $"Hwtool {ToolType} -p <sn>{serial}",
            "Quit",
        };

        if (verbose || dryRun)
        {
            Console.WriteLine("MPLAB X: " + mplabRoot);
            Console.WriteLine("MDB:     " + mdbBat);
            Console.WriteLine("Serial:  " + serial);
            Console.WriteLine("Device:  " + device);
            Console.WriteLine($"Timeout: {timeoutSec}s   Retry: {retry}");
            Console.WriteLine("MDB script:");
            foreach (string line in scriptLines)
            {
                Console.WriteLine("  " + line);
            }
        }

        if (dryRun)
        {
            Console.WriteLine("Dry-run: not reading target.");
            return 0;
        }

        int attempts = retry + 1;
        bool lastTimedOut = false;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                File.WriteAllLines(scriptPath, scriptLines, Encoding.ASCII);
                var result = RunMdb(mdbBat, scriptPath, timeoutSec, verbose);
                lastTimedOut = result.TimedOut;

                if (verbose)
                {
                    Console.WriteLine($"ExitCode: {result.ExitCode}{(result.TimedOut ? " (timeout)" : "")}");
                }

                if (result.Ok && TryParseUdid(result.Output, out uint[] words))
                {
                    PrintUdid(serial, words);
                    return 0;
                }

                if (attempt < attempts)
                {
                    Console.WriteLine(result.TimedOut
                        ? "PKOB4 UDID read timed out. Retrying..."
                        : "PKOB4 UDID read failed. Retrying...");
                }
            }
            finally
            {
                TryDelete(scriptPath);
            }
        }

        Console.WriteLine("PKOB4 UDID read failed.");
        Console.WriteLine("Serial: " + serial);
        Console.WriteLine("Reason: " + (lastTimedOut
            ? "timeout after retry"
            : "target not found or UDID not reported by mdb (run with --verbose to see the mdb output)"));
        return lastTimedOut ? 4 : 3;
    }

    // Parse the four "UDIDn = 0x........" lines MDB prints on connect into words[0..3]
    // (UDID1..UDID4 = bits 31:0 .. 127:96). Returns false unless all four are present.
    private static bool TryParseUdid(string output, out uint[] words)
    {
        words = new uint[UdidWordCount];
        bool[] seen = new bool[UdidWordCount];

        foreach (Match m in Regex.Matches(output, @"UDID([1-4])\s*=\s*0x([0-9A-Fa-f]{1,8})"))
        {
            int idx = int.Parse(m.Groups[1].Value) - 1;
            words[idx] = Convert.ToUInt32(m.Groups[2].Value, 16);
            seen[idx] = true;
        }

        foreach (bool s in seen)
        {
            if (!s)
            {
                return false;
            }
        }
        return true;
    }

    private static void PrintUdid(string serial, uint[] words)
    {
        // UDID128 = UDID4..UDID1 (high word first), matching the firmware boot banner.
        string udid128 = $"{words[3]:X8}{words[2]:X8}{words[1]:X8}{words[0]:X8}";
        Console.WriteLine("UDID1=" + words[0].ToString("X8"));
        Console.WriteLine("UDID2=" + words[1].ToString("X8"));
        Console.WriteLine("UDID3=" + words[2].ToString("X8"));
        Console.WriteLine("UDID4=" + words[3].ToString("X8"));
        Console.WriteLine("UDID128=" + udid128);
        Console.WriteLine("Serial: " + serial);

        bool allZero = words.All(w => w == 0x00000000u);
        bool allOne = words.All(w => w == 0xFFFFFFFFu);
        if (allZero || allOne)
        {
            Console.WriteLine("WARNING: UDID is all-" + (allZero ? "zero" : "one") + " (implausible / failed read).");
        }
    }

    private static string NextArg(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"missing value after {args[i]}");
        }
        return args[++i];
    }

    private static int Invalid(string message)
    {
        Console.Error.WriteLine("Invalid arguments: " + message);
        Console.Error.WriteLine("Try: read_udid_pkob4 --serial <SERIAL> [--device D] [--timeout S] [--retry N] [--verbose]");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("read_udid_pkob4 - read one PKOB4-attached target's Unique Device ID (UDID) by serial via MPLAB X mdb.");
        Console.WriteLine();
        Console.WriteLine("Usage: read_udid_pkob4 --serial <SERIAL> [options]");
        Console.WriteLine("       read_udid_pkob4 --list");
        Console.WriteLine("  --list            list connected PKOB4 serial numbers and exit");
        Console.WriteLine("  --serial  <sn>    PKOB4 serial number (required), e.g. 020085204RYN000318");
        Console.WriteLine("  --device  <dev>   MDB device token (default dsPIC33AK512MPS512)");
        Console.WriteLine("  --timeout <sec>   per-attempt timeout (default 60)");
        Console.WriteLine("  --retry   <n>     retries after the first attempt (default 1)");
        Console.WriteLine("  --verbose         print detected paths, script, mdb output and exit code");
        Console.WriteLine("  --dry-run         print what would run, do not read");
        Console.WriteLine();
        Console.WriteLine("Output: UDID1..UDID4 (32-bit words) + UDID128 (UDID4..UDID1 concatenated).");
        Console.WriteLine("Note: connecting the tool resets the target (the CDC console drops/reconnects).");
    }

    // List connected PKOB4 debuggers (USB VID_04D8 & PID_810B) and their serials.
    private static int ListPkob4()
    {
        var serials = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_04D8&PID_810B%'");
            foreach (System.Management.ManagementBaseObject mo in searcher.Get())
            {
                string id = mo["DeviceID"]?.ToString() ?? "";
                string[] parts = id.Split('\\');
                if (parts.Length == 3 && parts[2].Length > 0 && parts[2].IndexOf('&') < 0)
                {
                    serials.Add(parts[2]);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Could not enumerate PKOB4 devices: " + ex.Message);
            return 2;
        }

        if (serials.Count == 0)
        {
            Console.WriteLine("No PKOB4 debugger found (USB VID_04D8&PID_810B). Is a board connected?");
            return 3;
        }

        Console.WriteLine($"Connected PKOB4 serial(s): {serials.Count}");
        foreach (string s in serials)
        {
            Console.WriteLine("  " + s);
        }
        return 0;
    }

    private static bool TryDetectMdb(out string mplabRoot, out string mdbBat, out string err)
    {
        mplabRoot = "";
        mdbBat = "";
        err = "";

        var roots = new List<string>();
        foreach (string programFiles in new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files",
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)",
        })
        {
            string baseDir = Path.Combine(programFiles, "Microchip", "MPLABX");
            if (Directory.Exists(baseDir))
            {
                roots.Add(baseDir);
            }
        }

        if (roots.Count == 0)
        {
            err = @"no C:\Program Files\Microchip\MPLABX directory";
            return false;
        }

        var versions = new List<(Version Version, string Dir)>();
        foreach (string baseDir in roots)
        {
            foreach (string dir in Directory.GetDirectories(baseDir, "v*"))
            {
                string name = Path.GetFileName(dir).TrimStart('v', 'V');
                if (Version.TryParse(name, out Version? version))
                {
                    versions.Add((version, dir));
                }
            }
        }

        if (versions.Count == 0)
        {
            err = "no MPLABX vX.YY install folders found";
            return false;
        }

        foreach ((_, string dir) in versions.OrderByDescending(version => version.Version))
        {
            string candidate = Path.Combine(dir, "mplab_platform", "bin", "mdb.bat");
            if (!File.Exists(candidate))
            {
                continue;
            }

            mplabRoot = dir;
            mdbBat = candidate;
            return true;
        }

        err = "found MPLAB X but no install had mplab_platform\\bin\\mdb.bat";
        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private readonly record struct MdbResult(bool Ok, int ExitCode, bool TimedOut, string Output);

    private static MdbResult RunMdb(string mdbBat, string scriptPath, int timeoutSec, bool verbose)
    {
        var psi = new ProcessStartInfo
        {
            FileName = mdbBat,
            Arguments = Quote(scriptPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                return;
            }
            lock (output)
            {
                output.AppendLine(e.Data);
            }
            if (verbose)
            {
                Console.WriteLine("    " + e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                return;
            }
            lock (output)
            {
                output.AppendLine(e.Data);
            }
            if (verbose)
            {
                Console.WriteLine("    " + e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            process.StandardInput.Close();
        }
        catch
        {
            // Child receives stdin EOF.
        }

        if (!process.WaitForExit(timeoutSec * 1000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }
            try
            {
                process.WaitForExit(3000);
            }
            catch
            {
                // Best effort.
            }
            return new MdbResult(false, -1, true, output.ToString());
        }

        process.WaitForExit();

        string outText;
        lock (output)
        {
            outText = output.ToString();
        }

        // "Target device ... found." marks a successful connect. Note: mdb output
        // contains a benign 'SLF4J: Failed to load class ...' line, so do NOT treat a
        // bare "Failed to" as failure. Use specific connect-failure markers only; the
        // caller additionally requires all four UDID words to parse.
        bool foundTarget = outText.Contains("Target device", StringComparison.OrdinalIgnoreCase)
                        && outText.Contains(" found.", StringComparison.OrdinalIgnoreCase);
        bool failed = outText.Contains("Target device not found", StringComparison.OrdinalIgnoreCase)
                   || outText.Contains("No tool", StringComparison.OrdinalIgnoreCase)
                   || outText.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase)
                   || outText.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase);
        bool ok = foundTarget && !failed;

        return new MdbResult(ok, process.ExitCode, false, outText);
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
