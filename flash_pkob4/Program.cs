// flash_pkob4 - native wrapper around MPLAB X mdb.bat programming, selecting a
// board by PKOB4 serial number.
//
// This is ONLY a wrapper: it writes a temporary MDB script equivalent to:
//   device <device>
//   hwtool pkob4 -p <sn><serial>
//   program "<hex>"
//   quit
// then executes mdb.bat with timeout, retry, output capture and success-marker
// checking. It does not touch the PKOB4 USB protocol directly.
//
// Exit codes: 0 success, 1 invalid args, 2 MPLAB X/mdb/hex not found,
//             3 programming failed, 4 timeout after retry, 5 unexpected exception,
//             6 reset-after-flash failed.

using System.Diagnostics;
using System.Text;

internal static class Program
{
    private const string DefaultDevice = "dsPIC33AK512MPS512";
    private const string ToolType = "pkob4";

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
        string? hex = null;
        string device = DefaultDevice;
        int timeoutSec = 120;
        int retry = 0;
        bool verbose = false;
        bool dryRun = false;
        bool list = false;
        bool resetAfterFlash = false;
        string? resetDevice = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--list": list = true; break;
                case "--serial": serial = NextArg(args, ref i); break;
                case "--hex": hex = NextArg(args, ref i); break;
                case "--device": device = NextArg(args, ref i); break;
                case "--reset-after-flash": resetAfterFlash = true; break;
                case "--reset-device": resetDevice = NextArg(args, ref i); break;
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

        // --list just enumerates connected PKOB4 serials and exits (no serial/hex needed).
        if (list)
        {
            return ListPkob4();
        }

        if (string.IsNullOrWhiteSpace(serial))
        {
            return Invalid("--serial <PKOB4 serial> is required");
        }
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Invalid("--hex <path> is required");
        }

        string hexPath = Path.GetFullPath(hex);
        if (!File.Exists(hexPath))
        {
            Console.Error.WriteLine("HEX file not found: " + hexPath);
            return 2;
        }

        if (!TryDetectMdb(out string mplabRoot, out string mdbBat, out string detectErr))
        {
            Console.Error.WriteLine("MPLAB X mdb.bat not found.");
            Console.Error.WriteLine("Reason: " + detectErr);
            return 2;
        }

        string scriptPath = Path.Combine(Path.GetTempPath(), $"flash_pkob4_{Environment.ProcessId}_{Guid.NewGuid():N}.txt");
        string resolvedResetDevice = resetDevice ?? ToBoostDeviceToken(device);
        string[] scriptLines =
        {
            "device " + device,
            $"hwtool {ToolType} -p <sn>{serial}",
            $"program \"{hexPath}\"",
            "quit",
        };

        if (verbose || dryRun)
        {
            Console.WriteLine("MPLAB X: " + mplabRoot);
            Console.WriteLine("MDB:     " + mdbBat);
            Console.WriteLine("Serial:  " + serial);
            Console.WriteLine("Device:  " + device);
            Console.WriteLine("HEX:     " + hexPath);
            Console.WriteLine($"Timeout: {timeoutSec}s   Retry: {retry}");
            Console.WriteLine($"Reset after flash: {(resetAfterFlash ? "yes" : "no")}");
            if (resetAfterFlash)
            {
                Console.WriteLine("Reset device token: " + resolvedResetDevice);
            }
            Console.WriteLine("MDB script:");
            foreach (string line in scriptLines)
            {
                Console.WriteLine("  " + line);
            }
        }

        if (dryRun)
        {
            Console.WriteLine("Dry-run: not programming target.");
            if (resetAfterFlash)
            {
                Console.WriteLine("Dry-run reset command:");
                Console.WriteLine("  " + DescribeResetCommand(serial, resolvedResetDevice, verbose));
            }
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

                if (result.Ok)
                {
                    Console.WriteLine(attempt > 1
                        ? "PKOB4 flash succeeded on retry."
                        : "PKOB4 flash succeeded.");
                    Console.WriteLine("Serial: " + serial);
                    Console.WriteLine("HEX: " + hexPath);
                    if (resetAfterFlash)
                    {
                        Console.WriteLine("Running reset after successful flash...");
                        int resetExit = RunResetAfterFlash(serial, resolvedResetDevice, verbose);
                        if (resetExit != 0)
                        {
                            Console.WriteLine("Flash succeeded, but reset-after-flash failed.");
                            Console.WriteLine("Reset exit code: " + resetExit);
                            return 6;
                        }
                    }
                    return 0;
                }

                if (attempt < attempts)
                {
                    Console.WriteLine(result.TimedOut
                        ? "PKOB4 flash timed out. Retrying..."
                        : "PKOB4 flash failed. Retrying...");
                }
            }
            finally
            {
                TryDelete(scriptPath);
            }
        }

        Console.WriteLine("PKOB4 flash failed.");
        Console.WriteLine("Serial: " + serial);
        Console.WriteLine("HEX: " + hexPath);
        Console.WriteLine("Reason: " + (lastTimedOut ? "timeout after retry" : "mdb reported failure"));
        return lastTimedOut ? 4 : 3;
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
        Console.Error.WriteLine("Try: flash_pkob4 --serial <SERIAL> --hex <HEX> [--device D] [--reset-after-flash] [--verbose] [--dry-run]");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("flash_pkob4 - flash one PKOB4-attached target by serial via MPLAB X mdb.");
        Console.WriteLine();
        Console.WriteLine("Usage: flash_pkob4 --serial <SERIAL> --hex <HEX> [options]");
        Console.WriteLine("       flash_pkob4 --list");
        Console.WriteLine("  --list            list connected PKOB4 serial numbers and exit");
        Console.WriteLine("  --serial  <sn>    PKOB4 serial number (required), e.g. 020085204RYN000318");
        Console.WriteLine("  --hex     <path>  HEX file to program (required)");
        Console.WriteLine("  --device  <dev>   MDB device token (default dsPIC33AK512MPS512)");
        Console.WriteLine("  --reset-after-flash");
        Console.WriteLine("                   after a successful flash, run reset_pkob4 for the same serial");
        Console.WriteLine("  --reset-device <dev>");
        Console.WriteLine("                   reset_pkob4 boost token (default derived from --device)");
        Console.WriteLine("  --timeout <sec>   per-attempt timeout (default 120)");
        Console.WriteLine("  --retry   <n>     retries after the first attempt (default 0)");
        Console.WriteLine("  --verbose         print detected paths, script, output and exit code");
        Console.WriteLine("  --dry-run         print what would run, do not program");
    }

    // List connected PKOB4 debuggers (USB VID_04D8 & PID_810B) and their serials,
    // so the user can discover the value to pass to --serial. The serial is the
    // third '\'-separated segment of the composite device's instance id; child
    // interface nodes (…&MI_xx\…) are skipped.
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

    private static string ToBoostDeviceToken(string mdbDevice)
    {
        foreach (string prefix in new[] { "dsPIC", "PIC" })
        {
            if (mdbDevice.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return mdbDevice.Substring(prefix.Length);
            }
        }
        return mdbDevice;
    }

    private static string DescribeResetCommand(string serial, string device, bool verbose)
    {
        if (TryFindResetExe(out string resetExe))
        {
            return $"{Quote(resetExe)} --serial {serial} --device {device}{(verbose ? " --verbose" : "")}";
        }

        if (TryFindResetProject(out string resetProject))
        {
            return $"dotnet run --project {Quote(resetProject)} -c Release -- --serial {serial} --device {device}{(verbose ? " --verbose" : "")}";
        }

        return $"reset_pkob4 --serial {serial} --device {device}{(verbose ? " --verbose" : "")}";
    }

    private static int RunResetAfterFlash(string serial, string device, bool verbose)
    {
        string fileName;
        string arguments;

        if (TryFindResetExe(out string resetExe))
        {
            fileName = resetExe;
            arguments = $"--serial {serial} --device {device}" + (verbose ? " --verbose" : "");
        }
        else if (TryFindResetProject(out string resetProject))
        {
            fileName = "dotnet";
            arguments = $"run --project {Quote(resetProject)} -c Release -- --serial {serial} --device {device}" + (verbose ? " --verbose" : "");
        }
        else
        {
            Console.Error.WriteLine("reset_pkob4 not found next to flash_pkob4 and reset_pkob4.csproj was not found.");
            Console.Error.WriteLine("Run reset_pkob4 manually, or publish/copy both tools into the same folder.");
            return 2;
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) Console.WriteLine("    reset> " + e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) Console.Error.WriteLine("    reset> " + e.Data);
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

        if (!process.WaitForExit(150000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }
            return 4;
        }

        try
        {
            process.WaitForExit(1000);
        }
        catch
        {
            // Best effort output drain.
        }

        return process.ExitCode;
    }

    private static bool TryFindResetExe(out string resetExe)
    {
        string baseDir = AppContext.BaseDirectory;
        foreach (string name in new[] { "reset_pkob4.exe", "reset_pkob4" })
        {
            string candidate = Path.Combine(baseDir, name);
            if (File.Exists(candidate))
            {
                resetExe = candidate;
                return true;
            }
        }

        resetExe = "";
        return false;
    }

    private static bool TryFindResetProject(out string resetProject)
    {
        DirectoryInfo? dir = new DirectoryInfo(Environment.CurrentDirectory);
        for (int depth = 0; dir != null && depth < 8; depth++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "reset_pkob4", "reset_pkob4.csproj");
            if (File.Exists(candidate))
            {
                resetProject = candidate;
                return true;
            }
        }

        resetProject = "";
        return false;
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
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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

        bool foundTarget = outText.Contains("Target device", StringComparison.OrdinalIgnoreCase)
                        && outText.Contains(" found.", StringComparison.OrdinalIgnoreCase);
        bool programSucceeded = outText.Contains("Program succeeded", StringComparison.OrdinalIgnoreCase)
                             || outText.Contains("Programming/Verify complete", StringComparison.OrdinalIgnoreCase);
        bool failed = outText.Contains("Program failed", StringComparison.OrdinalIgnoreCase)
                   || outText.Contains("Programming failed", StringComparison.OrdinalIgnoreCase)
                   || outText.Contains("Target device not found", StringComparison.OrdinalIgnoreCase)
                   || outText.Contains("No tool", StringComparison.OrdinalIgnoreCase);
        bool ok = foundTarget && programSucceeded && !failed;

        return new MdbResult(ok, process.ExitCode, false, outText);
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
