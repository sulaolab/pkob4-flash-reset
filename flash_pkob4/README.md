# flash_pkob4

A small native Windows wrapper around MPLAB X `mdb.bat` programming, selecting a
target by **PKOB4 serial number**. It can be used as flash-only, or it can
automatically run `reset_pkob4` after a successful flash.

This tool programs via MPLAB X `mdb.bat`. The optional post-flash reset delegates
to `reset_pkob4`, so the reset behavior stays identical to the standalone reset
tool. It does not open MPLAB X and does not touch the PKOB4 USB protocol directly.

## Why

The fast manual workflow is:

1. Build the firmware once.
2. Write a tiny MDB script.
3. Select the PKOB4 by serial with `hwtool pkob4 -p <sn>...`.
4. Program the generated HEX.

`flash_pkob4` turns that workflow into one stable command with timeout, retry,
temporary-script cleanup and success-marker checking.

## Usage

```text
flash_pkob4 --serial <PKOB4_SERIAL> --hex <HEX_FILE> [options]
flash_pkob4 --list

  --list            list connected PKOB4 serial numbers and exit (instant, no target contact)
  --serial  <sn>    PKOB4 serial number (required), e.g. 020085204RYN000318
  --hex     <path>  HEX file to program (required)
  --device  <dev>   MDB device token       (default dsPIC33AK512MPS512)
  --reset-after-flash
                   after a successful flash, run reset_pkob4 for the same serial
  --reset-device <dev>
                   reset_pkob4 boost token (default derived from --device)
  --timeout <sec>   per-attempt timeout    (default 120)
  --retry   <n>     retries after attempt 1 (default 0)
  --verbose         print detected paths, MDB script, output and exit code
  --dry-run         print what would run, do not program
  -h, --help        usage
```

`--list` only reads PKOB4 USB serials. To also see each board's device token /
Device Id, use `reset_pkob4 --list --probe` (it connects to the target, which
resets it).

Examples:

```powershell
flash_pkob4 --serial 020085204RYN000318 --hex C:\path\firmware.X.production.hex
flash_pkob4 --serial 020085204RYN001164 --hex C:\path\firmware.X.production.hex --timeout 180 --retry 1 --verbose
flash_pkob4 --serial 020085204RYN001164 --hex C:\path\firmware.X.production.hex --reset-after-flash --verbose
flash_pkob4 --serial 020085204RYN000318 --hex C:\path\firmware.X.production.hex --dry-run --verbose
```

## What it runs

The tool writes a temporary MDB script like this:

```text
device dsPIC33AK512MPS512
hwtool pkob4 -p <sn>020085204RYN000318
program "C:\path\firmware.X.production.hex"
quit
```

Then it executes:

```text
"<MPLABX>\mplab_platform\bin\mdb.bat" "<temporary-script>"
```

The temporary script is deleted after each attempt.

With `--reset-after-flash`, the reset command is run only after programming
succeeds. When both published executables are in the same folder, `flash_pkob4`
uses the sibling `reset_pkob4.exe`. During source-tree development, it falls back
to `dotnet run --project reset_pkob4/reset_pkob4.csproj`.

The default reset device token is derived from the MDB token by removing a leading
`dsPIC` or `PIC` prefix:

```text
--device dsPIC33AK512MPS512  ->  --reset-device 33AK512MPS512
```

Pass `--reset-device` explicitly if that derived token is not correct for a
future device.

## Success Criteria

`flash_pkob4` treats programming as successful when MDB output includes target
detection and programming success markers:

```text
Target device dsPIC33AK512MPS512 found.
Programming/Verify complete
Program succeeded.
```

MDB may print a Java `ChronicleHashClosedException` during shutdown. If
`Program succeeded.` appears before that, programming has completed.

## Build

Requires the .NET SDK. From this folder:

```powershell
dotnet publish -c Release
```

Output:

```text
bin\Release\net8.0\win-x64\publish\flash_pkob4.exe
```

The output is a self-contained single-file Windows x64 executable.

## Notes

- Always pass `--serial` when more than one PKOB4 board is connected.
- `--reset-after-flash` is intentionally a post-success action. If programming
  fails, no reset is attempted.
- If the target board is in use by someone else, do not run this tool against
  that serial number.
