# LEGO Dimensions Save Converter

Converts LEGO Dimensions save files between **Xbox 360 (xenia)**, **PS3 (RPCS3)**,
**PS4 (shadPS4)** and **Wii U (Cemu)** — any direction, all files at once.

Small Windows GUI, single executable, no dependencies and nothing to install.

## Usage

1. Point **Source save folder** at a save directory. The platform and save version are
   detected automatically and shown right below the field.
2. Pick the **Target platform**.
3. Pick an **Output folder** (must be different from the source).
4. Press **Convert**.

Then copy the result into a save slot **the game itself created**, so the slot metadata
survives — `sce_sys/` on PS4, `PARAM.SFO` + `ICON0.PNG` on PS3, `meta/` on Wii U, the
`.header` file in `Headers\` on Xbox 360. The converter never writes that metadata itself.

If files already exist in the output folder they are copied into a `_backup_` subfolder
before anything is overwritten.

Where saves usually live:

| Emulator | Path |
|---|---|
| xenia | `content\<profile>\5752084B\00000001\savegame_1\` |
| RPCS3 | `dev_hdd0\home\00000001\savedata\BLES02105000\` (EU) or `BLUS31488\` (US) |
| shadPS4 | `savedata\CUSA01176\Slot00\` |
| Cemu | `<title id>\user\<account>\Slot1\` |

### Does the region matter?

**No.** Region only changes the *folder name* (the title ID), never the file format —
and the converter works on file contents, not on folder names. A save taken from an EU
PS3 copy (`BLES02105000`) was loaded successfully by an NTSC-U Xbox 360 copy. Just drop
the converted files into whichever regional folder your own copy uses.

## How it works

Every platform stores the same 24-byte header followed by an opaque payload:

| Offset | Field | Notes |
|---|---|---|
| 0 | `version` | save revision — 13 on Xbox 360/PS3/PS4, 12 on Wii U, 1 on pre-update saves |
| 4 | `sigA` | `sigB + version` on the PowerPC consoles; PS4 uses `0x81E359DF` |
| 8 | `zero` | always 0 |
| 12 | `sigB` | `0x502C3F10` everywhere |
| 16 | `checksum` | payload-derived, platform independent |
| 20 | `length` | payload length in bytes |

Only these things differ between platforms:

| | Xbox 360 | PS3 | PS4 | Wii U |
|---|---|---|---|---|
| Byte order | big-endian | big-endian | **little-endian** | big-endian |
| File size | exact | **padded to 1 KiB** | exact | exact |
| Numbering | `DLC01`..`DLC31` | `DLC01`..`DLC31` | `DLC01`..`DLC31` | **`DLC0`..`DLC30`** |
| Main blob | `V2GAME01` | `GAME01` | `V2GAME01` | `V2GAME0` |
| Options | `OPTS01` | `OPTS01` | `ZOPTS01` | `OPTS0` |

**The payload is carried across untouched**, which is what makes this a header rewrite
rather than a data translation, and means the checksum never needs recomputing. Verified
by comparing a PS3 and a PS4 `DLC01`: 272 differing bytes out of 234,817 (genuine progress
differences), versus 5,200 when dword-swapped.

Wii U numbers its files from zero, so `DLC0` is the same slot as `DLC01` elsewhere — which
also explains why Wii U is missing `DLC14` while the others are missing `DLC15`.

`GAME01` is literally the **first N bytes of `V2GAME01`** sharing its checksum — verified:
of 100,441 bytes exactly 3 differ, and those 3 are the length field. The converter rebuilds
it for the target rather than copying it across, so the pair is always consistent.

## Save versions

The **save version is preserved, not translated**. This tool moves a save between
platforms; it does not upgrade one save revision into another.

That matters for Wii U, which uses **version 12** while the other three use **version 13**.
The revisions really do differ — `DLC` payloads are 12 bytes shorter, `GAME` 36 bytes
shorter and `V2GAME` 764 bytes longer — so a Wii U save converted to Xbox 360 arrives as a
valid version 12 file rather than a version 13 one. The game is known to handle more than
one revision (version 1 saves sit happily beside version 13 ones), but **whether it accepts
a version 12 save on another platform is untested** — try it on a spare slot first.

## Verified

- Output sizes match real xenia and RPCS3 saves byte for byte.
- Round trip PS4 → Xbox 360 → PS4: all 32 files bit-identical.
- Round trip Wii U → Xbox 360 → Wii U: all 32 files bit-identical.
- A save converted from RPCS3 was loaded by the game under xenia, which then wrote
  `V2GAME01` back to it.

## Known limits

- Cross-version conversion is not attempted (see above); Wii U saves stay at version 12.
- Converting a non-version-13 save **to PS4** emits a warning: the PS4 signature constant
  is only known for version 13.
- The PS4 main blob is 8 bytes longer than the Xbox 360 one (308,053 vs 308,045). Lengths
  are preserved rather than trimmed.
- Options files are skipped by default — they differ per platform and carry no progress.
  There is a checkbox if you want them anyway.
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
