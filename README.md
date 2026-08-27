# LEGO Dimensions Save Converter

Converts LEGO Dimensions save files between **Xbox 360 (xenia)**, **PS3 (RPCS3)** and
**PS4 (shadPS4)** — any direction, all files at once.

Small Windows GUI, single executable, no dependencies and nothing to install.

## Usage

1. Point **Source save folder** at a save directory. The platform is detected automatically
   and shown right below the field.
2. Pick the **Target platform**.
3. Pick an **Output folder** (must be different from the source).
4. Press **Convert**.

Then copy the result into a save slot **the game itself created**, so the slot metadata
survives — `sce_sys/` on PS4, `PARAM.SFO` + `ICON0.PNG` on PS3, the `.header` file in
`Headers\` on Xbox 360. The converter never writes that metadata itself.

If files already exist in the output folder they are copied into a `_backup_` subfolder
before anything is overwritten.

Where saves usually live:

| Emulator | Path |
|---|---|
| xenia | `content\<profile>\5752084B\00000001\savegame_1\` |
| RPCS3 | `dev_hdd0\home\00000001\savedata\BLES02105000\` |
| shadPS4 | `savedata\CUSA01176\Slot00\` |

## How it works

Every platform stores the same 24-byte header followed by an opaque payload:

| Offset | Field | Notes |
|---|---|---|
| 0 | `version` | always 13 |
| 4 | `sigA` | `0x502C3F1D` on PS3/Xbox 360, `0x81E359DF` on PS4 |
| 8 | `zero` | always 0 |
| 12 | `sigB` | `0x502C3F10` everywhere |
| 16 | `checksum` | payload-derived, platform independent |
| 20 | `length` | payload length in bytes |

Only three things actually differ between platforms:

| | Xbox 360 | PS3 | PS4 |
|---|---|---|---|
| Byte order | big-endian | big-endian | **little-endian** |
| `sigA` | `0x502C3F1D` | `0x502C3F1D` | `0x81E359DF` |
| File size | exact | **padded up to a 1 KiB boundary** | exact |

**The payload itself is byte-identical across all three platforms**, which is what makes
this a header rewrite rather than a data translation. Verified by comparing a PS3 and a
PS4 `DLC01`: 272 differing bytes out of 234,817 (genuine progress differences), versus
5,200 when dword-swapped. So the checksum carries over untouched and never needs
recomputing.

File naming also differs. The main save blob is `V2GAME01` on Xbox 360 and PS4 but plain
`GAME01` on PS3; the small options file is `OPTS01` on Xbox 360/PS3 and `ZOPTS01` on PS4.
`GAME01` on Xbox 360 and PS4 is literally the **first N bytes of `V2GAME01`** sharing its
checksum — verified: of 100,441 bytes exactly 3 differ, and those 3 are the length field.
The converter therefore rebuilds it for the target platform instead of copying it across
at the source's length, so the pair is always consistent.

## Verified

- Output sizes match real xenia and RPCS3 saves byte for byte.
- Round trip PS4 → Xbox 360 → PS4 returns all 32 files bit-identical.
- A save converted from RPCS3 was loaded by the game under xenia, which then wrote
  `V2GAME01` back to it.

## Known limits

- The PS4 main blob is 8 bytes longer than the Xbox 360 one (308,053 vs 308,045). The
  converter preserves the source length rather than trimming. If a converted save fails
  to load, this is the first thing to suspect.
- Options files are skipped by default — they differ in size per platform and carry no
  progress. There is a checkbox if you want them anyway.
- Slot metadata is never generated; convert into an existing slot.

## Building

Needs nothing beyond what ships with Windows:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
  /target:winexe /codepage:65001 /optimize+ ^
  /out:DimensionsSaveConverter.exe ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
  SaveConverter.cs
```

## Related

- [Xenia-Seamless-Toypad-Build](https://github.com/NeverCookFirst/Xenia-Seamless-Toypad-Build)
- [RPCS3-Seamless-Toypad-Build](https://github.com/NeverCookFirst/RPCS3-Seamless-Toypad-Build)
- [shadPS4-Seamless-Toypad-Bridge](https://github.com/NeverCookFirst/shadPS4-Seamless-Toypad-Bridge)
- [DimensionsModLoader](https://github.com/NeverCookFirst/DimensionsModLoader)
