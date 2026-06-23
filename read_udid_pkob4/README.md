# read_udid_pkob4

A small native Windows wrapper around MPLAB X `mdb.bat` that reads a target
dsPIC33AK's **Unique Device ID (UDID)**, selecting the board by **PKOB4 serial
number**.

This is **read only**. It does not flash and does not reset, though connecting to
the target briefly drops/re-enumerates the PKOB4 USB-CDC console (any open serial
terminal, e.g. Tera Term, must reconnect). It does not open MPLAB X and does not
touch the PKOB4 USB protocol directly.

## What a UDID is (and is not)

The UDID is a 128-bit, factory-programmed, read-only value that is **unique per
physical die** — use it to tell one board apart from another. It is NOT:

- the **PKOB4 serial** (`--serial`), which identifies the debugger, not the target;
- the **DEVID**, which identifies the part type/variant (same for every part of a
  variant).

The on-chip words live at `0x007F2BE0` (UDID1, bits 31:0) .. `0x007F2BEC` (UDID4,
bits 127:96). Reference: the device Programming Specification, "Unique Device ID"
(Section 1.3).

## Usage

```text
read_udid_pkob4 --serial <PKOB4_SERIAL> [options]
read_udid_pkob4 --list

  --list            list connected PKOB4 serial numbers and exit (instant, no target contact)
  --serial  <sn>    PKOB4 serial number (required), e.g. 020085204RYN000318
  --device  <dev>   MDB device token       (default dsPIC33AK512MPS512)
  --timeout <sec>   per-attempt timeout    (default 60)
  --retry   <n>     retries after attempt 1 (default 1)
  --verbose         print detected paths, MDB script, output and exit code
  --dry-run         print what would run, do not read
  -h, --help        usage
```

Example:

```powershell
read_udid_pkob4 --serial 020085204RYN000318
# UDID1=00D7794B
# UDID2=56080004
# UDID3=00EA010F
# UDID4=FFFFFFFF
# UDID128=FFFFFFFF00EA010F5608000400D7794B
# Serial: 020085204RYN000318
```

`UDID128` is the four words concatenated **UDID4..UDID1** (high word first),
matching how firmware typically prints it on a boot banner. Two boards from the
same production lot often share UDID1/UDID2 (lot/wafer) and differ in UDID3 (die
X/Y position); compare the full 128-bit value.

## What it runs

The tool writes a temporary MDB script like this:

```text
Device dsPIC33AK512MPS512
Hwtool PKOB4 -p <sn>020085204RYN000318
Quit
```

Then it executes `"<MPLABX>\mplab_platform\bin\mdb.bat" "<temporary-script>"` and
parses the four `UDIDn = 0x........` lines that MDB prints during the
target-connect / device-id readout. The temporary script is deleted after each
attempt.

## How the UDID is read (important)

`mdb` prints the UDID **automatically on connect** — that is what this tool parses.
The documented `x /U4xw 0x7F2BE0` memory-read form does **not** work on the
dsPIC33AK MP_DFP tested (`dsPIC33AK-MP_DFP` 1.3.185): it returns `0xFFFFFFFF` /
`0x00000000` garbage, because the `U` memory is not mapped for the `x` command in
that pack. The connect-time print is therefore the reliable path, and its values
match the on-chip read (firmware reading `0x007F2BE0..EC`) exactly — cross-checked
on hardware against two independent firmwares.

## Success criteria

Success requires both `Target device <dev> found.` in the MDB output **and** all
four `UDIDn` words parsed. (A benign `SLF4J: Failed to load class ...` line appears
in MDB output and is explicitly ignored.) If the UDID is all-`0x00000000` or
all-`0xFFFFFFFF`, the tool prints the values with a `WARNING` (implausible read).

## Build

Requires the .NET SDK. From this folder:

```powershell
dotnet publish -c Release
# -> bin\Release\net8.0\win-x64\publish\read_udid_pkob4.exe
```

The output is a self-contained single-file Windows x64 executable.

## Exit codes

```text
0  success (UDID read and printed)
1  invalid arguments
2  MPLAB X / mdb.bat not found
3  read failed (target not found, or UDID not reported by mdb)
4  timeout after retry
5  unexpected exception
```

## Notes

- Always pass `--serial` when more than one PKOB4 board is connected.
- Requires MPLAB X IDE / IPE to not be holding the same PKOB4 (close it first).
- Connecting resets the target; an open CDC console drops and must reconnect.
- If the target board is in use by someone else, do not run this tool against that
  serial number.
