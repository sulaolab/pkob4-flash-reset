# reset_pkob4

A small native Windows wrapper around the MPLAB X **IPECMDBoost** "release from
reset" operation, selecting a target by **PKOB4 serial number**. It hides the
Java/IPECMDBoost complexity behind a stable CLI and adds timeout, retry and safe
cleanup so the occasional Boost/JVM hang no longer wedges your workflow.

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

  --list           list connected PKOB4 serial numbers and exit (instant, no target contact)
  --probe          with --list: also connect to each board and print its device
                   token + Device Id (RESETS each board; confirms the --device token)
  --serial  <sn>   PKOB4 serial number (required for reset), e.g. 020085204RYN000318
  --device  <dev>  device token            (default 33AK512MPS512; SHORT form, no 'dsPIC' prefix)
  --timeout <sec>  per-attempt timeout      (default 15)
  --retry   <n>    retries after attempt 1  (default 1)
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
reset_pkob4 --serial 020085204RYN000318 --device 33AK512MPS512 --timeout 15 --retry 2
reset_pkob4 --serial 020085204RYN000318 --dry-run --verbose
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

Before each attempt it removes the stale boost state `%USERPROFILE%\.mchp_ipe\2012.lock`
and `2012.ini`, and kills only `java.exe` whose command line contains
`ipecmdboost.jar`. On timeout it kills the launched Java process tree and retries.

## Exit codes

```
0  success
1  invalid arguments
2  MPLAB X / Java / IPECMDBoost not found
3  Boost reset failed
4  timeout after retry
5  unexpected exception
6  PKOB4 wedged -- USB unplug/replug required (not retryable)
```

Exit 6 is raised when boost reports the tool was *"unloaded while still busy /
unplug and reconnect"*: a device-side state that no host cleanup or retry can
clear. The tool detects it, stops retrying into a hang, and tells you to
re-seat the USB cable. (It also kills any detached boost server after each run so
a lingering JVM never holds the port or the output pipe — the tool always
returns.)

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
  detects the hang, kills its launched Java, retries, and on final failure prints a
  clear message and advises the physical reset button. It cannot force a wedged
  Boost to complete.
- The stale-Java cleanup matches processes by command line. Reading another
  process's command line can require elevation; **run from an elevated shell** if a
  stuck Boost Java needs to be cleaned up and isn't being caught.
- Most reliable reset overall remains the **physical reset button** (pure MCLR, never
  disturbs the CDC). Use `reset_pkob4` for hands-off command-line resets.
