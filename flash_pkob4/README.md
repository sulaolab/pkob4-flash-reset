# flash_pkob4

A small native Windows wrapper around MPLAB X `mdb.bat` programming, selecting a
target by **PKOB4 serial number**. It is intended to pair with
`tools/reset_pkob4`: this tool flashes, and `reset_pkob4` resets.

This is **flash only**. It does not reset after programming, does not open MPLAB
X, and does not touch the PKOB4 USB protocol directly.

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
- This tool does not reset the MCU after flashing. Use `reset_pkob4` when a reset
  is needed.
- If the target board is in use by someone else, do not run this tool against
  that serial number.
