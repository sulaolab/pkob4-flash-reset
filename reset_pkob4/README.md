# reset_pkob4

A small native Windows wrapper around the MPLAB X **IPECMDBoost** "release from
reset" operation, selecting a target by **PKOB4 serial number**. It hides the
Java/IPECMDBoost complexity behind a stable CLI and adds warm/cold timeout
selection, early failure detection and failure-only cleanup so the occasional
Boost/JVM hang no longer wedges your workflow.

This is **reset only**. It does not flash, does not touch the PKOB4 USB protocol,
does not use `mdb Reset` or `ipecmd -OL` as the reset, and never blindly kills all
`java.exe`.

## Why

On a board where the printf console runs over the PKOB4 USB-CDC:

- `mdb Reset` resets the MCU but tends to drop/re-enumerate the CDC console.
- `ipecmd -OL` hold/release keeps the CDC alive but does **not** reset a running MCU.
- The Java **IPECMDBoost** "release from reset" *does* reset and is the practically
  usable method — but the Java server occasionally hangs.

`reset_pkob4` wraps that known-working Boost reset and makes it robust.

## Usage

```
reset_pkob4 --serial <PKOB4_SERIAL> [options]
reset_pkob4 --list [--probe] [--device <dev>]
reset_pkob4 --check-java | --shutdown-boost | --clean-java

  --check-java     side-effect-free boost warm/cold/stale state check
                   (exit 0 = usable warm/cold state, 8 = stale/problem state)
  --shutdown-boost ask IPECMDBoost to quit via /OQ /OY2012, then exit
  --clean-java     emergency cleanup: shutdown/kill boost java + remove lock/ini
  --list           list connected PKOB4 serial numbers and exit (instant, no target contact)
  --probe          with --list: also connect to each board and print its device
                   token + Device Id (RESETS each board; confirms the --device token)
  --serial  <sn>   PKOB4 serial number (required for reset), e.g. 020085204RYN000318
  --device  <dev>  device token            (default 33AK512MPS512; SHORT form, no 'dsPIC' prefix)
  --warm-timeout <s> timeout when boost is warm (default 5)
  --cold-timeout <s> timeout when boost starts cold (default 60)
  --timeout <sec>  legacy override: use one timeout for both warm and cold
  --retry   <n>    recovery retries after a warm failure (default 1)
  --verbose        print detected paths, the command and the exit code
  --dry-run        print what would run, do nothing
  -h, --help       usage
```

`--device` takes the **short** boost token (`33AK512MPS512`). If you pass the
MPLAB IDE/MDB form (`dsPIC33AK512MPS512`), the tool stops with exit 1 and suggests
the short form rather than running into a confusing boost failure.

Examples:

```powershell
reset_pkob4 --serial 020085204RYN000318
reset_pkob4 --serial 020085204RYN000318 --device 33AK512MPS512 --warm-timeout 5 --cold-timeout 60
reset_pkob4 --serial 020085204RYN000318 --dry-run --verbose
reset_pkob4 --list --probe
```

Find a board's PKOB4 serial with MPLAB X, or with mdb (`hwtool pkob4` lists tools),
or from a flash log line like `INFO: 020085204RYN000318 successfully reserved`.

## What it runs

It reproduces the verified command from `pkob4_reset_auto.ps1`, adding `/TS` for
tool selection:

```
"<bundled java.exe>" -jar "<ipecmdboost.jar>" /P<device> /TPPKOB4 /TS<serial> /OK /OL /OY2012
```

(stdin is closed = the script's `< NUL`, which avoids a Boost stdin wait.)

Normal successful resets do **not** clean up the boost server. The server is useful:
when java is already listening on port 2012, the reset is a warm operation and should
complete quickly. If no boost server owns that port, the tool treats the run as cold
and allows enough time for MPLAB X/IPECMDBoost to start.

Observed defaults on the current setup:

- warm boost reset: about 2.5 s total, so the default warm timeout is 5 s.
- cold boost reset after official shutdown: about 34 s total, so the default cold
  timeout is 60 s.

Cleanup is failure-only. If a warm run times out, or Boost prints a decisive failure
marker such as `Operation Failed`, `Unable to connect`, `Tool is busy` or `No tool
found`, the tool requests official shutdown (`/OQ /OY2012`), kills only the java
process that owns boost port 2012 if it is still alive, removes `2012.lock`/`2012.ini`,
then retries once as a cold run. Cold failures are cleaned up but are not repeatedly
retried by default.

For `--list --probe`, the default per-board timeout is 60 seconds instead of 15.
Probe performs a real target connection and may include Java / IPECMDBoost /
PKOB4 cold-start time; successful warm probes still return as soon as Boost
finishes, so the higher cap does not slow down healthy runs.

## Exit codes

```
0  success
1  invalid arguments
2  MPLAB X / Java / IPECMDBoost not found
3  Boost reset failed
4  timeout after recovery
5  unexpected exception
6  PKOB4 wedged -- USB unplug/replug required (not retryable)
8  --check-java only: a stale/problem boost state was detected
```

Exit 6 is raised when Boost reports the tool was *"unloaded while still busy /
unplug and reconnect"*: a device-side state that no host cleanup or retry can
clear. The tool detects it, stops retrying into a hang, and tells you to
re-seat the USB cable.

## Diagnosing / clearing a stuck boost server

A healthy warm boost run returns in ~2-3 s, while a cold boost start can be silent
for 20+ seconds and still succeed. These opt-in modes make the current state visible
without contacting the target:

```powershell
reset_pkob4 --check-java       # verdict only; exit 0 = usable warm/cold, 8 = stale/problem
reset_pkob4 --shutdown-boost   # official /OQ /OY2012 shutdown
reset_pkob4 --clean-java       # emergency: shutdown/kill boost java + remove lock/ini
```

`--check-java` reports the cmdline-visible boost javas (with age), **who owns port
2012** (PID + age — the authoritative signal), and `2012.lock|ini`, then prints a
one-line verdict. If a java process owns port 2012, that is a normal **warm** boost
state; the lock/ini files may also be present and should not be deleted.

Example of a healthy warm state:

```
  boost java processes (cmdline-visible) : 0
  boost port 2012 owner    : PID=54068 (java, age=921s)  <-- authoritative boost-server signal
  stale 2012.ini   : present
  stale 2012.lock  : present
Verdict: warm boost server available on port 2012.
```

Example of a stale state:

```
  boost port 2012 owner    : none (port free)
  stale 2012.ini   : present
  stale 2012.lock  : present
Verdict: STUCK boost state detected (2012.ini present without a listening server, 2012.lock present without a listening server).
```

`--clean-java` is the manual escape hatch. Normal successful resets leave the warm
server alive; failed resets run the same targeted recovery automatically.

## Build

Requires the .NET SDK (8.0+). From this folder:

```powershell
dotnet publish -c Release
```

Output (self-contained single file, no .NET install needed on the target PC):

```
bin\Release\net8.0\win-x64\publish\reset_pkob4.exe
```

## Notes / limitations

- **Serial targeting works**: Boost reports `Selected Programming Tool Sno: <serial>`,
  so the right board is chosen even with several PKOB4 connected.
- **Boost can still hang** (its known weakness, especially with multiple PKOB4
  connected, or when another MPLAB Java process holds the tool). `reset_pkob4`
  detects timeout or early failure, performs targeted recovery, retries a failed
  warm run once as cold, and on final failure prints a clear message. It cannot force
  a wedged Boost to complete.
- Targeted cleanup does not depend on reading command lines: it can kill whatever
  java owns boost port 2012 (by owning PID via `GetExtendedTcpTable`), so a stuck
  boost server is caught **without elevation**. Use `--check-java` to see exactly
  what is holding the port.
- Most reliable reset overall remains the **physical reset button** (pure MCLR, never
  disturbs the CDC). Use `reset_pkob4` for hands-off command-line resets.
