# pkob4-flash-reset

Three small native Windows CLI tools to **flash**, **reset**, and read the
**Unique Device ID (UDID)** of a Microchip dsPIC33AK (Curiosity-class) board
through its on-board **PKOB4** debugger, selecting the target by **PKOB4 serial
number**.

## Why this exists

Iterating on firmware from an editor/terminal — not the MPLAB X IDE — runs into
three friction points that these tools remove:

1. **Opening MPLAB X just to flash or reset is slow.** You want a single,
   scriptable command in your build/edit loop, not an IDE round-trip.
2. **Picking the right board when several are plugged in.** PKOB4 tools index
   boards by connection order, which changes; these tools select strictly by
   **PKOB4 serial number**, so the board you mean is the board you get.
3. **Resetting a board whose console runs over the PKOB4 USB-CDC is awkward.**
   - `mdb Reset` resets the MCU but tends to drop/re-enumerate the CDC console.
   - `ipecmd -OL` hold/release keeps the console but does **not** reset a running MCU.
   - MPLAB X **IPECMDBoost** "release from reset" *does* reset and is the
     practically usable method — but its Java server occasionally **hangs** and
     wedges the workflow.

`reset_pkob4` wraps that working IPECMDBoost path and adds **warm/cold timeout
selection, early failure detection, and failure-only targeted cleanup**. A healthy
Boost server is kept alive for fast subsequent resets; a transient hang no longer
blocks you. `flash_pkob4` does the same hardening for the `mdb` programming step.
Both keep board selection by serial and run without opening the IDE.

Neither tool talks to the PKOB4 USB protocol directly, and `reset_pkob4` never
blindly kills unrelated `java.exe` — it only clears the targeted IPECMDBoost state
when recovery is needed. `flash_pkob4 --reset-after-flash` is just a safe
composition of flash success followed by `reset_pkob4`.

**Bottom line — built for automation, including AI agents.** Because each tool is
a single deterministic command (explicit serial, clear exit codes, timeout/retry,
targeted recovery, machine-readable output that reports any cleanup it performed), an
AI coding agent or CI script can drive the **build → flash → reset → observe**
loop on real hardware with no IDE interaction and no human babysitting. That makes
the hardware iteration loop dramatically faster and more reliable — the real
payoff of these wrappers.

| Tool | Role |
|---|---|
| [`flash_pkob4`](flash_pkob4/) | **Flash** — program a HEX via MPLAB X `mdb`; optional reset-after-flash. |
| [`reset_pkob4`](reset_pkob4/) | **Reset only** — release-from-reset via MPLAB X `IPECMDBoost`. Does not flash. |
| [`read_udid_pkob4`](read_udid_pkob4/) | **Read UDID only** — read the target's 128-bit Unique Device ID via MPLAB X `mdb`. Does not flash or reset (but connecting briefly drops the CDC console). |

`flash_pkob4` and `reset_pkob4` are designed to be used as a pair: `flash_pkob4`
programs the HEX, and can optionally call `reset_pkob4` after success. `read_udid_pkob4` is a
standalone query — it reports the per-die UDID (board-individual identity), which
is distinct from the PKOB4 serial (a debugger ID) and from DEVID (a part-type ID).

## Requirements

- **Windows x64**.
- **MPLAB X** installed (v6.x). The tools auto-detect the newest install under
  `C:\Program Files\Microchip\MPLABX\vX.YY` and reuse its bundled `mdb` /
  `ipecmdboost.jar` and Java runtime. (No separate Java/.NET install is needed to
  *run* a published single-file exe.)
- A board attached via **PKOB4**.
- To **build** from source: **.NET SDK 8+**.

## Build

Each tool is an independent .NET project that publishes to a self-contained
single-file `.exe` (no .NET install required on the target machine):

```sh
cd flash_pkob4   # or: cd reset_pkob4
dotnet publish -c Release
# -> bin/Release/net8.0/win-x64/publish/<tool>.exe
```

Build outputs (`bin/`, `obj/`, the published `.exe`) are intentionally **not**
committed — see `.gitignore`. Copy the published `.exe` wherever you keep your
bench tools.

## Usage

```sh
# Flash a HEX to one board (by PKOB4 serial), then reset it:
flash_pkob4 --serial 020085204RYN000318 --hex path/to/firmware.production.hex --reset-after-flash

# Read that board's Unique Device ID (UDID):
read_udid_pkob4 --serial 020085204RYN000318
# UDID1=00D7794B
# UDID2=56080004
# UDID3=00EA010F
# UDID4=FFFFFFFF
# UDID128=FFFFFFFF00EA010F5608000400D7794B
# Serial: 020085204RYN000318
```

> **How `read_udid_pkob4` reads the UDID.** MPLAB X `mdb` prints the four
> `UDIDn = 0x...` words automatically when it connects to the target, and the tool
> parses those. The documented `x /U4xw 0x7F2BE0` memory-read form does **not** work
> on the dsPIC33AK MP_DFP tested (it returns `0xFFFFFFFF`/`0x00000000` garbage — the
> `U` memory is not mapped for `x` in that pack), so the connect-time print is the
> reliable path. The values match the on-chip read (firmware reading
> `0x007F2BE0..EC`) exactly.

Common options (see each subfolder README for the full list and exit codes):

- `--serial <sn>` — PKOB4 serial (required); selects the board.
- `--device <token>` — device token (`flash_pkob4` default `dsPIC33AK512MPS512`,
  `reset_pkob4` default `33AK512MPS512`).
- `--timeout <sec>`, `--retry <n>`, `--verbose`, `--dry-run`.
- `flash_pkob4 --reset-after-flash` delegates to `reset_pkob4` only after a
  successful program operation; the reset token is derived from `--device` unless
  `--reset-device` is specified.
- `reset_pkob4` uses separate Boost warm/cold defaults: warm port-2012 Java present
  means a 5 second timeout, while a cold Boost start uses 60 seconds because the
  first output may not appear for 20+ seconds. Pass `--warm-timeout`,
  `--cold-timeout`, or legacy `--timeout` to override.

### Finding a board's PKOB4 serial (and device token)

Both tools take `--list` to enumerate the connected PKOB4 serials — instant and
side-effect free (a USB scan):

```sh
reset_pkob4 --list        # or: flash_pkob4 --list
# Connected PKOB4 serial(s): 1
#   020085204RYN000057
```

`reset_pkob4` additionally takes `--list --probe` to report each board's **device
token + Device Id** by briefly connecting to it:

```sh
reset_pkob4 --list --probe            # uses --device (default 33AK512MPS512)
# Connected PKOB4: 1  (probing with device token '33AK512MPS512' -- this resets each board)
#   020085204RYN000057   dsPIC33AK512MPS512   Device Id 0xa77c
```

`--probe` **resets each probed board** and briefly drops its USB-CDC console (it
has to connect to the target to read the device id), so it is opt-in. It confirms
the expected `--device` token rather than discovering an arbitrary unknown part. A
plain `--list` does neither — it only reads the USB serial. (You can also get the
serial from the MPLAB X tool list, flashing logs, or
`Get-PnpDevice -PresentOnly | ? { $_.InstanceId -match 'RYN' }`.) A PKOB4 serial
is not a secret — it is printed on the debugger.

### Notes

- A reset re-enumerates the PKOB4 USB, so a serial/CDC console (e.g. Tera Term)
  briefly drops and must reconnect after each reset — expected, not a fault.
- `reset_pkob4` keeps a healthy `IPECMDBoost` server alive for fast warm resets.
  It only performs cleanup after timeout or a decisive early failure, using official
  `/OQ`, then targeted port-owner Java kill and `2012.lock|ini` removal if needed.
- State escape hatches (no target contact): `reset_pkob4 --check-java` gives a
  fast verdict (warm/cold/stale), `reset_pkob4 --shutdown-boost` asks Boost to quit,
  and `reset_pkob4 --clean-java` performs emergency targeted cleanup.
- If the PKOB4 firmware itself gets wedged (boost reports *"unloaded while still
  busy / unplug and reconnect"*), `reset_pkob4` detects this, stops retrying into
  a hang, and tells you to unplug/replug the USB cable (exit code 6). Only a USB
  re-enumeration clears that device-side state.

## License

[MIT-0](LICENSE) (MIT No Attribution).

---

## 概要（日本語）

dsPIC33AK（Curiosity 系）ボードを、基板上の **PKOB4** デバッガ経由で
**書き込み（flash）** ／ **リセット（reset）** ／ **UDID（固有デバイス ID）読み出し** する
ための Windows 用 CLI ツール 3 本です。
**PKOB4 のシリアル番号でボードを選択**するため、複数枚を挿したまま狙った 1 枚だけを操作できます。
MPLAB X を開かずにコマンド一発で動きます。

**最大の狙いは自動化・AI エージェントでの利用**です。各ツールは「明示シリアル指定・明確な終了コード・
timeout/retry・ターゲットを絞った復旧・掃除内容を出力する機械可読な出力」を備えた決定的な単一コマンドなので、
AI コーディングエージェントや CI が **build → flash → reset → 観測** のループを実機上で
IDE 操作も人手の見張りもなしに回せます。これにより実機イテレーションが劇的に速く・確実になります
（このラッパーの本当の価値）。

- `flash_pkob4` … **書き込み**（MPLAB X `mdb` 経由）。必要なら成功後に `reset_pkob4` を呼べる。
- `reset_pkob4` … **リセット専用**（MPLAB X `IPECMDBoost` の release-from-reset）。書き込みはしない。
- `read_udid_pkob4` … **UDID 読み出し専用**（MPLAB X `mdb` 経由）。ダイ固有の 128bit UDID を表示
  （接続時に CDC コンソールは一瞬切れる）。`mdb` が接続時に出力する `UDIDn = 0x...` をパースする方式で、
  `x /U4xw` メモリ読みは対象 DFP では機能しない（FF/00 を返す）ため接続時出力を使う。値はファームの
  オンチップ読み出し（`0x007F2BE0..EC`）と完全一致。UDID は PKOB4 シリアル（デバッガ ID）や
  DEVID（型番 ID）とは別物で、基板の個体識別に使える。

**要件**：Windows x64、MPLAB X v6.x インストール済み（同梱の `mdb`/`ipecmdboost.jar`/Java を自動検出して利用）、
ソースからのビルドには .NET SDK 8+。

**ビルド**：各フォルダで `dotnet publish -c Release` → 自己完結の単一 exe が
`bin/Release/net8.0/win-x64/publish/` に生成されます。**ビルド生成物（`bin`/`obj`/exe）は
リポジトリに含めていません**（`.gitignore` で除外）。

**使い方**：`--serial <PKOB4 SN>` でボードを選択。`flash_pkob4 --serial <sn> --hex <hex>` で書き込み、
`--reset-after-flash` を付けると成功後にリセットまで実行します。各サブフォルダの README に全オプションと終了コードがあります。

**補足**：リセットは PKOB4 の USB 再列挙を伴うため、シリアルコンソール（Tera Term 等）は
一瞬切れて再接続が必要です（正常動作）。`reset_pkob4` は正常な Boost 常駐を温存し、
失敗時だけ official shutdown、port-owner Java kill、`2012.lock|ini` 削除を行います。
warm 既定 timeout は 5 秒、cold 既定 timeout は 60 秒です。
