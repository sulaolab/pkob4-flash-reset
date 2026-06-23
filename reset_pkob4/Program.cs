// reset_pkob4 - native wrapper around the known-working MPLAB X IPECMDBoost
// "release from reset" operation, selecting a board by PKOB4 serial number.
//
// This is ONLY a wrapper: it builds the same boost command the existing
// pkob4_reset_auto.ps1 uses, then adds timeout, retry, safe cleanup and a small
// stable CLI. It does not touch the PKOB4 USB protocol, does not use mdb Reset or
// ipecmd -OL as the reset, does not flash, and does not blindly kill all java.
//
// Known-working boost command (from pkob4_reset_auto.ps1), with /TS added so a
// specific board can be selected when several PKOB4 are connected:
//   "<java.exe>" -jar "<ipecmdboost.jar>" /P<device> /TPPKOB4 /TS<serial> /OK /OL /OY<port>
// (stdin is closed = the script's "< NUL", which avoids a boost stdin wait.)
//
// Exit codes: 0 success, 1 invalid args, 2 MPLAB X/Java/Boost not found,
//             3 boost reset failed, 4 timeout after retry, 5 unexpected exception,
//             6 PKOB4 wedged (USB unplug/replug required -- not retryable),
//             8 --check-java only: a stuck boost state was detected.

using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    const int BoostPort = 2012;          // matches pkob4_reset_auto.ps1
    const string ToolType = "PKOB4";

    static int Main(string[] args)
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

    static int Run(string[] args)
    {
        // ---- arguments ----
        string? serial = null;
        string device = "33AK512MPS512";
        int timeoutSec = 15;  // healthy boost returns in ~2-3s and we exit as soon
                              // as it does, so a higher cap is free; a cold / just
                              // re-enumerated PKOB4 can take longer than 5s to connect.
        int retry = 1;
        bool verbose = false;
        bool dryRun = false;
        bool list = false;
        bool probe = false;
        bool timeoutExplicit = false;
        bool cleanJava = false;
        bool checkJava = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--list":    list = true; break;
                case "--probe":   probe = true; break;
                case "--clean-java": cleanJava = true; break;
                case "--check-java":
                case "--doctor":  checkJava = true; break;
                case "--serial":  serial = NextArg(args, ref i); break;
                case "--device":  device = NextArg(args, ref i); break;
                case "--timeout":
                    if (!int.TryParse(NextArg(args, ref i), out timeoutSec) || timeoutSec <= 0) return Invalid("--timeout must be a positive integer (seconds)");
                    timeoutExplicit = true;
                    break;
                case "--retry":   if (!int.TryParse(NextArg(args, ref i), out retry) || retry < 0) return Invalid("--retry must be >= 0"); break;
                case "--verbose": verbose = true; break;
                case "--dry-run": dryRun = true; break;
                case "-h": case "--help": PrintUsage(); return 0;
                default: return Invalid($"unknown argument: {a}");
            }
        }

        // Java maintenance modes (no target contact, no --serial needed). These are
        // opt-in escape hatches for when a stale IPECMDBoost server (port 2012) wedges
        // the workflow -- not for routine use (every reset already self-cleans).
        if (checkJava) return CheckJava(verbose);
        if (cleanJava) return CleanJava(verbose);

        // Guard a very common mistake: boost/ipecmd want the SHORT device token
        // (e.g. 33AK512MPS512). The MPLAB IDE / MDB form 'dsPIC33AK512MPS512' gets
        // turned into 'PICDSPIC...' by ipecmd and the connect fails confusingly.
        // Point out the fix and stop, rather than running into that failure.
        if (TryStripPicPrefix(device, out string shortDev))
        {
            Console.Error.WriteLine($"Invalid arguments: --device '{device}' is the MPLAB IDE/MDB form.");
            Console.Error.WriteLine($"IPECMDBoost expects the short token (no 'dsPIC'/'PIC' prefix).");
            Console.Error.WriteLine($"Did you mean:  --device {shortDev}");
            return 1;
        }

        // Probe performs a real target connection and can include Java/Boost/PKOB4
        // cold-start time. Keep normal reset fast, but make probe tolerant unless
        // the caller explicitly chose a timeout.
        if (list && probe && !timeoutExplicit)
            timeoutSec = 60;

        // --list enumerates connected PKOB4 serials and exits (no serial needed).
        // With --probe it also connects to each board to report its device token.
        if (list)
            return ListPkob4(probe, device, timeoutSec, verbose);

        if (string.IsNullOrWhiteSpace(serial))
            return Invalid("--serial <PKOB4 serial> is required");

        // ---- locate MPLAB X / java / boost ----
        if (!TryDetectMplab(out string mplabRoot, out string javaExe, out string boostJar, out string detectErr))
        {
            Console.Error.WriteLine("MPLAB X / Java / IPECMDBoost not found.");
            Console.Error.WriteLine("Reason: " + detectErr);
            return 2;
        }

        // Known-working boost args + /TS<serial> for tool selection.
        string boostArgs =
            $"-jar \"{boostJar}\" /P{device} /TP{ToolType} /TS{serial} /OK /OL /OY{BoostPort}";

        if (verbose || dryRun)
        {
            Console.WriteLine("MPLAB X: " + mplabRoot);
            Console.WriteLine("Java:    " + javaExe);
            Console.WriteLine("Boost:   " + boostJar);
            Console.WriteLine($"Command: \"{javaExe}\" {boostArgs}");
            Console.WriteLine($"Serial:  {serial}   Device: {device}   Timeout: {timeoutSec}s   Retry: {retry}");
        }

        if (dryRun)
        {
            Console.WriteLine("Dry-run: not executing reset.");
            return 0;
        }

        // ---- run with retry ----
        int attempts = retry + 1;
        bool lastTimedOut = false;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            // Safe pre-run cleanup. Kill any stuck boost java FIRST so it releases
            // its lock/ini file handles, then remove the stale lock/ini. Whatever
            // was actually cleaned is reported in normal output (not just
            // --verbose) so the operational state is visible after the fact.
            var cleanup = new List<string>();
            KillStaleBoostJava(verbose, cleanup);
            KillBoostPortOwner(verbose, cleanup);   // catches the WMI-invisible boost java still holding port 2012
            CleanBoostState(verbose, cleanup);
            if (cleanup.Count > 0)
                Console.WriteLine($"Cleanup (attempt {attempt}): " + string.Join("; ", cleanup));

            var result = RunBoost(javaExe, boostArgs, timeoutSec, verbose);
            lastTimedOut = result.TimedOut;

            if (verbose)
                Console.WriteLine($"ExitCode: {result.ExitCode}{(result.TimedOut ? " (timeout)" : "")}");

            // Non-retryable: the PKOB4 firmware reported it was unloaded while busy.
            // No amount of host-side cleanup/retry clears this -- the USB device
            // itself must be re-enumerated. Bail out immediately with clear guidance
            // instead of retrying into a hang.
            if (LooksWedged(result.Output))
            {
                Console.WriteLine("PKOB4 appears wedged: boost reported the USB tool was unloaded while still busy.");
                Console.WriteLine("Serial: " + serial);
                Console.WriteLine("Fix: unplug and reconnect the PKOB4 USB cable, then retry (retrying alone will not clear this).");
                return 6;
            }

            if (result.Ok)
            {
                Console.WriteLine(attempt > 1
                    ? "PKOB4 Boost reset succeeded on retry."
                    : "PKOB4 Boost reset succeeded.");
                Console.WriteLine("Serial: " + serial);
                return 0;
            }

            if (result.TimedOut && attempt < attempts)
                Console.WriteLine("PKOB4 Boost reset timed out. Cleaning up Java process and retrying...");
            else if (!result.TimedOut && attempt < attempts)
                Console.WriteLine("PKOB4 Boost reset failed. Retrying...");
        }

        // ---- give up ----
        Console.WriteLine("PKOB4 Boost reset failed.");
        Console.WriteLine("Serial: " + serial);
        Console.WriteLine("Reason: " + (lastTimedOut ? "timeout after retry" : "boost reported failure"));
        Console.WriteLine("Fallback: press the physical reset button.");
        return lastTimedOut ? 4 : 3;
    }

    // ---------------------------------------------------------------- helpers

    static string NextArg(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"missing value after {args[i]}");
        return args[++i];
    }

    // The boost device token is the short form (e.g. 33AK512MPS512). If the caller
    // passed the IDE/MDB form ('dsPIC33AK512MPS512' / 'PIC33...'), return true and
    // the corrected short token in 'shortForm'. Otherwise false.
    static bool TryStripPicPrefix(string device, out string shortForm)
    {
        shortForm = device;
        foreach (string pfx in new[] { "dsPIC", "PIC" })
        {
            if (device.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
            {
                shortForm = device.Substring(pfx.Length);
                return true;
            }
        }
        return false;
    }

    static int Invalid(string msg)
    {
        Console.Error.WriteLine("Invalid arguments: " + msg);
        Console.Error.WriteLine("Try: reset_pkob4 --serial <SERIAL> [--device D] [--timeout S] [--retry N] [--verbose] [--dry-run]");
        return 1;
    }

    static void PrintUsage()
    {
        Console.WriteLine("reset_pkob4 - reset one PKOB4-attached target by serial via MPLAB X IPECMDBoost.");
        Console.WriteLine();
        Console.WriteLine("Usage: reset_pkob4 --serial <SERIAL> [options]");
        Console.WriteLine("       reset_pkob4 --list [--probe] [--device <dev>]");
        Console.WriteLine("       reset_pkob4 --check-java | --clean-java");
        Console.WriteLine("  --check-java     fast, side-effect-free check for a stuck IPECMDBoost server");
        Console.WriteLine("                   (boost java age, port " + BoostPort + ", stale lock/ini); exit 8 = stuck");
        Console.WriteLine("  --clean-java     kill a stuck boost java + remove stale lock/ini, then exit");
        Console.WriteLine("                   (opt-in escape hatch; a normal reset already self-cleans)");
        Console.WriteLine("  --list           list connected PKOB4 serial numbers and exit");
        Console.WriteLine("  --probe          with --list: also connect to each board and report its");
        Console.WriteLine("                   device token + Device Id (RESETS each board; needs --device)");
        Console.WriteLine("  --serial  <sn>   PKOB4 serial number (required), e.g. 020085204RYN000318");
        Console.WriteLine("  --device  <dev>  device token (default 33AK512MPS512)");
        Console.WriteLine("  --timeout <sec>  per-attempt timeout (default 15; --list --probe default 60)");
        Console.WriteLine("  --retry   <n>    retries after the first attempt (default 1)");
        Console.WriteLine("  --verbose        print detected paths, command and exit code");
        Console.WriteLine("  --dry-run        print what would run, do nothing");
    }

    // Enumerate connected PKOB4 debuggers (USB VID_04D8 & PID_810B) and return
    // their serials. The serial is the third '\'-separated segment of the
    // composite device's instance id; child interface nodes (...&MI_xx\...) are
    // skipped. This is USB enumeration only -- instant and side-effect free.
    static SortedSet<string> EnumeratePkob4Serials(out string? err)
    {
        err = null;
        var serials = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_04D8&PID_810B%'");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                string id = mo["DeviceID"]?.ToString() ?? "";
                string[] parts = id.Split('\\');
                if (parts.Length == 3 && parts[2].Length > 0 && parts[2].IndexOf('&') < 0)
                    serials.Add(parts[2]);
            }
        }
        catch (Exception ex) { err = ex.Message; }
        return serials;
    }

    // --list [--probe]. Without --probe: print serials only (instant, no target
    // contact). With --probe: additionally connect to each board with the expected
    // device token and print its device name + Device Id. NOTE: probing RESETS each
    // board and briefly drops its USB-CDC console; it can only confirm the given
    // token (a USB serial scan cannot reveal an unknown target on its own), and it
    // is serialized so only one boost server uses the port at a time.
    static int ListPkob4(bool probe, string device, int timeoutSec, bool verbose)
    {
        var serials = EnumeratePkob4Serials(out string? enumErr);
        if (enumErr != null)
        {
            Console.Error.WriteLine("Could not enumerate PKOB4 devices: " + enumErr);
            return 2;
        }
        if (serials.Count == 0)
        {
            Console.WriteLine("No PKOB4 debugger found (USB VID_04D8&PID_810B). Is a board connected?");
            return 3;
        }

        if (!probe)
        {
            Console.WriteLine($"Connected PKOB4 serial(s): {serials.Count}");
            foreach (string s in serials)
                Console.WriteLine("  " + s);
            return 0;
        }

        // --probe: connect to each board to read its device token.
        if (!TryDetectMplab(out _, out string javaExe, out string boostJar, out string detectErr))
        {
            Console.Error.WriteLine("MPLAB X / Java / IPECMDBoost not found (needed for --probe).");
            Console.Error.WriteLine("Reason: " + detectErr);
            return 2;
        }

        Console.WriteLine($"Connected PKOB4: {serials.Count}  (probing with device token '{device}' -- this resets each board)");
        foreach (string s in serials)
        {
            // Serialize: clear any boost server/state so only one uses the port.
            KillStaleBoostJava(false, new List<string>());
            CleanBoostState(false, new List<string>());

            string args = $"-jar \"{boostJar}\" /P{device} /TP{ToolType} /TS{s} /OK /OL /OY{BoostPort}";
            var r = RunBoost(javaExe, args, timeoutSec, verbose);

            string detail;
            if (LooksWedged(r.Output))
                detail = "(PKOB4 wedged -- unplug/replug USB)";
            else if (TryParseDeviceInfo(r.Output, out string name, out string id))
                detail = $"{(name.Length > 0 ? name : "?")}   Device Id {(id.Length > 0 ? id : "?")}";
            else
                detail = $"(no match for token '{device}'{(r.TimedOut ? ", timed out" : "")})";

            Console.WriteLine($"  {s}   {detail}");
        }
        return 0;
    }

    // Enumerate C:\Program Files\Microchip\MPLABX\v* (newest first) and pick the
    // first install that has ipecmdboost.jar and a bundled java.exe.
    static bool TryDetectMplab(out string mplabRoot, out string javaExe, out string boostJar, out string err)
    {
        mplabRoot = javaExe = boostJar = "";
        err = "";

        var roots = new List<string>();
        foreach (var pf in new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files",
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)",
        })
        {
            var baseDir = Path.Combine(pf, "Microchip", "MPLABX");
            if (Directory.Exists(baseDir)) roots.Add(baseDir);
        }
        if (roots.Count == 0) { err = @"no C:\Program Files\Microchip\MPLABX directory"; return false; }

        var versions = new List<(Version v, string dir)>();
        foreach (var baseDir in roots)
            foreach (var dir in Directory.GetDirectories(baseDir, "v*"))
            {
                var name = Path.GetFileName(dir).TrimStart('v', 'V');
                if (Version.TryParse(name, out var v)) versions.Add((v, dir));
            }
        if (versions.Count == 0) { err = "no MPLABX vX.YY install folders found"; return false; }

        foreach (var (_, dir) in versions.OrderByDescending(t => t.v))
        {
            var jar = Path.Combine(dir, "mplab_platform", "mplab_ipe", "ipecmdboost.jar");
            if (!File.Exists(jar)) continue;
            // Prefer the java bundled with this MPLAB X (sys\java\...\bin\java.exe).
            var java = Directory.EnumerateFiles(dir, "java.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (java == null) continue;
            mplabRoot = dir; boostJar = jar; javaExe = java;
            return true;
        }
        err = "found MPLAB X but no install had both ipecmdboost.jar and a bundled java.exe";
        return false;
    }

    // Remove the stale boost server lock/ini for our port (the exit-7 fix).
    // Appends a short action string to 'report' for each file actually removed.
    static void CleanBoostState(bool verbose, List<string> report)
    {
        var ipeState = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mchp_ipe");
        foreach (var ext in new[] { "lock", "ini" })
        {
            var f = Path.Combine(ipeState, $"{BoostPort}.{ext}");
            if (!File.Exists(f)) continue;

            // The file can briefly stay locked by a just-killed boost server, so
            // retry the delete a few times before giving up.
            bool deleted = false;
            string? lastErr = null;
            for (int t = 0; t < 5 && !deleted; t++)
            {
                try { File.Delete(f); deleted = true; }
                catch (Exception ex) { lastErr = ex.Message; Thread.Sleep(100); }
            }

            if (deleted)
            {
                report.Add($"removed {BoostPort}.{ext}");
                if (verbose) Console.WriteLine("  removed stale " + Path.GetFileName(f));
            }
            else if (verbose)
            {
                Console.WriteLine($"  (could not remove {Path.GetFileName(f)}: {lastErr})");
            }
        }
    }

    // Kill stuck boost-server java. Primary match: command line references
    // ipecmdboost.jar. Fallback: a java.exe bundled inside MPLAB X (...\MPLABX\
    // ...\sys\java\...) whose command line we cannot read -- Win32_Process
    // CommandLine is often empty for these without elevation, which is exactly
    // why the precise match used to miss the stale server and the reset then
    // timed out. We never touch a java.exe outside MPLAB X, and never one whose
    // (readable) command line shows it is something other than boost.
    // Appends a short action string to 'report' for each process killed.
    static void KillStaleBoostJava(bool verbose, List<string> report)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name = 'java.exe'");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                var cmd = mo["CommandLine"]?.ToString() ?? "";
                var exe = mo["ExecutablePath"]?.ToString() ?? "";

                bool isBoost = cmd.IndexOf("ipecmdboost.jar", StringComparison.OrdinalIgnoreCase) >= 0;
                // Non-elevated fallback: cmdline hidden, but it is an MPLAB X
                // bundled JRE -- treat as a (likely boost) server to clear.
                bool maybeBoost = string.IsNullOrEmpty(cmd)
                    && exe.IndexOf(@"\MPLABX\", StringComparison.OrdinalIgnoreCase) >= 0
                    && exe.IndexOf(@"\sys\java\", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isBoost && !maybeBoost) continue;

                int pid = Convert.ToInt32(mo["ProcessId"]);
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill(entireProcessTree: true);
                    string how = isBoost ? "" : " (MPLABX-bundled, cmdline hidden)";
                    report.Add($"killed boost java PID={pid}");
                    if (verbose) Console.WriteLine($"  killed stale boost java PID={pid}{how}");
                }
                catch { /* already gone / no access */ }
            }
        }
        catch (Exception ex) { if (verbose) Console.WriteLine("  (stale-java scan skipped: " + ex.Message + ")"); }
    }

    // ------------------------------------------------ java health: scan / check / clean

    // Read-only scan of the boost-server java processes (same match rule as
    // KillStaleBoostJava), with each one's age in seconds. Instant, side-effect free.
    readonly record struct BoostJavaProc(int Pid, double AgeSec, bool CmdVisible, bool IsBoostCmd);

    static List<BoostJavaProc> ScanBoostJava()
    {
        var list = new List<BoostJavaProc>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name = 'java.exe'");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                var cmd = mo["CommandLine"]?.ToString() ?? "";
                var exe = mo["ExecutablePath"]?.ToString() ?? "";

                bool isBoost = cmd.IndexOf("ipecmdboost.jar", StringComparison.OrdinalIgnoreCase) >= 0;
                bool maybeBoost = string.IsNullOrEmpty(cmd)
                    && exe.IndexOf(@"\MPLABX\", StringComparison.OrdinalIgnoreCase) >= 0
                    && exe.IndexOf(@"\sys\java\", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!isBoost && !maybeBoost) continue;

                int pid = Convert.ToInt32(mo["ProcessId"]);
                double age = -1;
                try { using var p = Process.GetProcessById(pid); age = (DateTime.Now - p.StartTime).TotalSeconds; }
                catch { /* StartTime can be inaccessible; report age unknown */ }

                list.Add(new BoostJavaProc(pid, age, !string.IsNullOrEmpty(cmd), isBoost));
            }
        }
        catch { /* WMI unavailable -> empty list */ }
        return list;
    }

    // True if a TCP listener is bound to the boost port (i.e. a boost server is up).
    static bool IsBoostPortListening()
    {
        try
        {
            foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                if (ep.Port == BoostPort) return true;
        }
        catch { /* not enumerable -> treat as not listening */ }
        return false;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool sort,
                                           int ipVersion, int tableClass, int reserved);

    // PID that OWNS the boost listening port (IPv4), or null. This is the authoritative
    // "who is the boost server" signal -- far more reliable than matching a java process
    // by its WMI CommandLine/ExecutablePath, which are EMPTY for an MPLAB X-owned java
    // when we are not elevated (the reason a stale boost java used to be missed, leaving
    // the port held and the next reset failing). Uses GetExtendedTcpTable
    // (TCP_TABLE_OWNER_PID_LISTENER); no child process, locale-independent.
    static int? GetBoostPortOwnerPid()
    {
        const int AF_INET = 2;
        const int TCP_TABLE_OWNER_PID_LISTENER = 3;
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size <= 0) return null;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                return null;
            int num = Marshal.ReadInt32(buf);
            IntPtr row = buf + 4;
            for (int i = 0; i < num; i++)   // MIB_TCPROW_OWNER_PID = 6 DWORDs (24 bytes)
            {
                int localPortRaw = Marshal.ReadInt32(row, 8);   // network byte order, low word
                int localPort = ((localPortRaw & 0xFF) << 8) | ((localPortRaw >> 8) & 0xFF);
                int pid = Marshal.ReadInt32(row, 20);
                if (localPort == BoostPort) return pid;
                row += 24;
            }
        }
        catch { /* fall through */ }
        finally { Marshal.FreeHGlobal(buf); }
        return null;
    }

    // Kill the process that owns the boost port, if it is a java (the boost server).
    // Owning our dedicated boost port IS the signal -- this catches the WMI-invisible
    // boost java that KillStaleBoostJava (cmdline/exe match) cannot see. We still
    // require ProcessName == java so we never kill some unrelated owner.
    static void KillBoostPortOwner(bool verbose, List<string> report)
    {
        int? pid = GetBoostPortOwnerPid();
        if (pid == null) return;
        try
        {
            using var p = Process.GetProcessById(pid.Value);
            if (!p.ProcessName.Equals("java", StringComparison.OrdinalIgnoreCase))
            {
                if (verbose) Console.WriteLine($"  port {BoostPort} owner PID={pid} is '{p.ProcessName}', not java -- not killing");
                return;
            }
            p.Kill(entireProcessTree: true);
            report.Add($"killed boost port {BoostPort} owner java PID={pid}");
            if (verbose) Console.WriteLine($"  killed boost port {BoostPort} owner java PID={pid}");
        }
        catch { /* already gone / no access */ }
    }

    static bool BoostStateFileExists(string ext)
    {
        var ipeState = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mchp_ipe");
        return File.Exists(Path.Combine(ipeState, $"{BoostPort}.{ext}"));
    }

    // --check-java / --doctor: fast, side-effect-free verdict on whether a stuck boost
    // server is the problem. A healthy boost run returns in ~2-3s, so a boost java that
    // has been alive much longer -- or a held port / leftover lock|ini -- is the
    // signature of a wedged server. Exit 0 = clean, 8 = stuck state detected.
    const double StuckAgeSec = 15;

    static int CheckJava(bool verbose)
    {
        var procs = ScanBoostJava();
        bool ini  = BoostStateFileExists("ini");
        bool lck  = BoostStateFileExists("lock");
        int? ownerPid = GetBoostPortOwnerPid();
        bool oldProc = procs.Any(p => p.AgeSec >= StuckAgeSec);

        Console.WriteLine("PKOB4 boost/java health check:");
        Console.WriteLine($"  boost java processes (cmdline-visible) : {procs.Count}");
        foreach (var p in procs)
        {
            string age = p.AgeSec < 0 ? "age=?" : $"age={p.AgeSec:F0}s";
            string src = p.IsBoostCmd ? "ipecmdboost.jar" : "MPLABX-bundled (cmdline hidden)";
            string flag = p.AgeSec >= StuckAgeSec ? $"  <-- older than {StuckAgeSec:F0}s (likely stuck)" : "";
            Console.WriteLine($"    PID={p.Pid} {age} {src}{flag}");
        }
        if (ownerPid != null)
        {
            string who = "java";
            double age = -1;
            try { using var op = Process.GetProcessById(ownerPid.Value); who = op.ProcessName; age = (DateTime.Now - op.StartTime).TotalSeconds; } catch { }
            string ageStr = age < 0 ? "age=?" : $"age={age:F0}s";
            Console.WriteLine($"  boost port {BoostPort} owner    : PID={ownerPid} ({who}, {ageStr})  <-- authoritative boost-server signal");
        }
        else
        {
            Console.WriteLine($"  boost port {BoostPort} owner    : none (port free)");
        }
        Console.WriteLine($"  stale {BoostPort}.ini   : {(ini ? "present" : "no")}");
        Console.WriteLine($"  stale {BoostPort}.lock  : {(lck ? "present" : "no")}");

        bool stuck = ownerPid != null || ini || lck || oldProc;
        if (stuck)
        {
            var why = new List<string>();
            if (ownerPid != null) why.Add($"port {BoostPort} held by PID={ownerPid}");
            if (oldProc) why.Add($"a boost java older than {StuckAgeSec:F0}s");
            if (ini)     why.Add($"{BoostPort}.ini present");
            if (lck)     why.Add($"{BoostPort}.lock present");
            Console.WriteLine("Verdict: STUCK boost state detected (" + string.Join(", ", why) + ").");
            Console.WriteLine("Fix: run 'reset_pkob4 --clean-java' (a normal reset also self-cleans before each attempt).");
            Console.WriteLine("If it persists after --clean-java, the PKOB4 itself is wedged: unplug/replug the USB cable.");
            return 8;
        }
        if (procs.Count > 0)
        {
            Console.WriteLine("Verdict: a boost java is present but looks transient (recent, no held port/lock).");
            Console.WriteLine("         Likely a reset in flight -- re-check in a moment.");
            return 0;
        }
        Console.WriteLine("Verdict: clean (no boost java, no stale lock/ini, port free).");
        return 0;
    }

    // --clean-java: kill stuck boost java + remove stale lock/ini, then report.
    static int CleanJava(bool verbose)
    {
        var report = new List<string>();
        KillStaleBoostJava(verbose, report);
        KillBoostPortOwner(verbose, report);   // catches the WMI-invisible boost java holding the port
        CleanBoostState(verbose, report);

        if (report.Count == 0)
            Console.WriteLine("Nothing to clean (no stale boost java / lock / ini).");
        else
            Console.WriteLine("Cleaned: " + string.Join("; ", report));

        // The port can take a moment to free after the server dies; re-check.
        if (IsBoostPortListening())
            Console.WriteLine($"WARNING: boost port {BoostPort} still listening after cleanup -- a server may be re-spawning. "
                + "If a reset still hangs, unplug/replug the PKOB4 USB cable.");
        return 0;
    }

    readonly record struct BoostResult(bool Ok, int ExitCode, bool TimedOut, string Output);

    static BoostResult RunBoost(string javaExe, string boostArgs, int timeoutSec, bool verbose)
    {
        var psi = new ProcessStartInfo
        {
            FileName = javaExe,
            Arguments = boostArgs,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        var sb = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (sb) sb.AppendLine(e.Data); if (verbose) Console.WriteLine("    " + e.Data); } };
        p.ErrorDataReceived  += (_, e) => { if (e.Data != null) { lock (sb) sb.AppendLine(e.Data); } };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { p.StandardInput.Close(); } catch { /* = "< NUL": child gets stdin EOF */ }

        bool timedOut = false;
        if (!p.WaitForExit(timeoutSec * 1000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            try { p.WaitForExit(3000); } catch { }
            timedOut = true;
        }
        else
        {
            p.WaitForExit(); // let async output handlers drain
        }

        string outText;
        lock (sb) outText = sb.ToString();

        // Make sure no detached boost server (a separate /OY<port> java daemon)
        // lingers after we return. Such a survivor would (a) hold the boost port
        // for the next run and (b) keep our redirected stdout pipe open, which can
        // make the *calling* shell appear to hang long after the reset finished.
        // Killing it here is the difference between "the tool always returns" and
        // "Java occasionally wedges the workflow".
        KillStaleBoostJava(false, new List<string>());

        if (timedOut)
            return new BoostResult(false, -1, true, outText);

        // Boost may leave a lingering JVM and exit non-zero even on success, so accept
        // either a clean exit code or the explicit success marker in the output.
        bool ok = p.ExitCode == 0
               || outText.Contains("Operation Succeeded", StringComparison.OrdinalIgnoreCase);
        return new BoostResult(ok, p.ExitCode, false, outText);
    }

    // True if boost output indicates the PKOB4 was unloaded/wedged mid-operation,
    // a state only a USB re-enumeration (unplug/replug) clears -- never retry into it.
    static bool LooksWedged(string output)
    {
        return output.IndexOf("unloaded while still busy", StringComparison.OrdinalIgnoreCase) >= 0
            || output.IndexOf("unplug and reconnect", StringComparison.OrdinalIgnoreCase) >= 0
            || output.IndexOf("terminated abruptly", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Pull the target device name / Device Id out of a boost connect's output.
    static bool TryParseDeviceInfo(string output, out string name, out string id)
    {
        name = "";
        id = "";
        var mName = System.Text.RegularExpressions.Regex.Match(output, @"Target device\s+(\S+)\s+found");
        if (mName.Success) name = mName.Groups[1].Value;
        var mId = System.Text.RegularExpressions.Regex.Match(output, @"Device Id\s*=\s*(0x[0-9A-Fa-f]+)");
        if (mId.Success) id = mId.Groups[1].Value;
        return name.Length > 0 || id.Length > 0;
    }
}
