# LEGO Dimensions Save Converter

Converts LEGO Dimensions save files between **Xbox 360 (xenia)**, **PS3 (RPCS3)**,
**PS4 (shadPS4)** and **Wii U (Cemu)** — any direction, all files at once.

Small Windows GUI, single executable, no dependencies and nothing to install.

> ## ⚠ No guarantee — back up first
>
> **This tool comes with no guarantee that the saves it produces are stable or free of
> corruption. Use it at your own risk, and always keep a backup of any save you care about
> before touching it.**
>
> The save format was reverse-engineered rather than documented. It is verified as far as
> it can be — a real Xbox 360 save survives a full round trip bit-identical — but the game's
> field types are only known for the parts that real saves happened to exercise. A field
> shape that has never been seen may still convert wrongly, and a wrong conversion can look
> fine for hours of play before something turns out to be missing or nonsensical.
>
> Convert into a **spare slot**, never over your only copy.

## Usage

1. Point **Source save folder** at a save directory. The platform and save version are
   detected automatically and shown right below the field.
2. Pick the **Target platform**.
3. Pick an **Output folder** (must be different from the source).
4. Press **Convert**.

### Convert the whole save, with every DLC file

Point the tool at the **complete** save folder — all thirty `DLC` files together with
`GAME` and `V2GAME`. Progress is spread across them: each `DLC` file is one world, and the
main blob only holds the parts common to all of them. Converting a partial folder, or
copying only some of the results into the target slot, produces a save that loads but is
missing worlds. There is nothing to gain by leaving files out.

### The target slot must already contain a save the game made

**Do not drop the converted folder in as a new save slot.** Start the game on the target
emulator, create a new save in a spare slot, quit, and then **replace the files inside that
slot** with the converted ones.

Every platform stores per-slot metadata that this tool never writes, and without it the slot
is invisible or empty no matter how correct the save files are:

| Platform | Metadata that must survive |
|---|---|
| Xbox 360 (xenia) | the `.header` file in `Headers\<profile>\` |
| PS3 (RPCS3) | `PARAM.SFO` and `ICON0.PNG` |
| PS4 (shadPS4) | the `sce_sys\` folder (`param.sfo`, `icon0.png`) |
| Wii U (Cemu) | the `meta\` folder (`meta.xml`, `iconTex.tga`) |

If files already exist in the output folder they are copied into a `_backup_` subfolder
before anything is overwritten. That is a convenience, not a backup strategy — keep your own
copy of the original slot as well.

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

Wii U numbers its files from zero, so `DLC0` is the same slot as `DLC01` elsewhere — which
also explains why Wii U is missing `DLC14` while the others are missing `DLC15`.

### The payload, and the PS4

Inside the payload, **every 32-bit field is little-endian on all four platforms** — even on
the big-endian consoles, where the header dwords above are big-endian. The save data has
its own fixed-endian serialiser, which is why PS3 → Xbox 360 works as a plain copy.

**64-bit fields are the exception.** They are written as two little-endian halves in the
console's own word order: low half first on the PS4, high half first on the PowerPC
consoles. So `0x00000000726DE363` is stored as

```
Xbox 360 / PS3 / Wii U:   00 00 00 00 63 e3 6d 72
PS4:                      63 e3 6d 72 00 00 00 00
```

Copy the payload across unchanged and every 64-bit value arrives byte-reversed. The game
does not reject the file — it loads it and reads those fields as garbage, so the save
appears to work while the progress stored in them is silently gone. This is why an earlier
version of this tool produced PS4 saves that loaded into the right level with the right
stud count, but showed the hub as never completed and every red brick locked.

Finding those fields means walking the save's record tree. The payload is a zero pad,
then a chunk header

```
u32 magic (0x4DEE7E53)   u32 count   u32 hash   u32 dataOffset
```

with the record stream four bytes past `magic + 16 + dataOffset`. A record is

```
u32 key   u32 size   size bytes of body   u32 key      <- the key repeats as a terminator
```

and a body is either more records or an array of 24-byte entries `{ u32 id, u32 tag, u64, u64 }`.
Only those two slots are 64-bit; everything else is 32-bit and is left alone.

Two more restrictions keep the swap honest:

- **A slot is only swapped when exactly one of its halves is zero**, which is what a 64-bit
  value below 2^32 looks like. Opaque byte arrays whose length happens to be a multiple of
  24 have two non-zero halves and are therefore never touched.
- **A few entry ids hold a 32-bit value, not a 64-bit one**, and swapping those multiplies
  the number by 2^32. The type can be read off a real Xbox 360 save, where a 64-bit value
  below 2^32 is stored high half first `(0, v)` while a 32-bit field is stored `(v, 0)`.
  Across two genuine saves 3365 ids read as 64-bit, exactly one read as 32-bit, and none
  read as both. That one is `0EEB6A85` — the total stud counter — and it is on a deny-list
  in `Endian64.NOT_64_BIT`. A PS4 save on its own cannot distinguish the two, which is why
  the list is needed rather than derived at run time.

The 8-byte PS4 difference is in the zero pad ahead of the chunk, not in the data: the chunk
magic sits at payload offset 1088 on the PS4 and 1080 everywhere else, and the distance from
the chunk to the end of the payload is identical on both.

`GAME01` is literally the **first N bytes of `V2GAME01`**, sharing its header and checksum
with only the length field adjusted — verified against real saves on both platforms, 0
differing bytes. The converter therefore writes it as a slice of the finished main file
rather than rebuilding it, since a rebuild would walk a truncated record stream and pick a
different set of 64-bit fields.

The header checksum is **not** recomputed, and does not need to be: the game does not verify
it. Confirmed by corrupting the `V2GAME01` checksum of a real save and loading it under
xenia — it loaded completely and normally.

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

- **A real Xbox 360 save written by the game, run through Xbox 360 → PS4 → Xbox 360, comes
  back bit-identical — all 32 files.** This is the strongest check available: the tool
  reproduces exactly what the game itself wrote.
- Round trip PS4 → Xbox 360 → PS4: all 32 files bit-identical.
- Round trip Wii U → Xbox 360 → Wii U: all 32 files bit-identical.
- A PS4 save converted to Xbox 360 was compared against a real xenia save of the same game:
  nine `DLC` files match byte for byte, and the rest differ only by the two players' actual
  progress. Before the 64-bit fix, the same files differed by 240–2600 bytes each.
- A PS4 save converted to Xbox 360 was loaded by the game under xenia and played: correct
  level, correct hub progress, correct red bricks, correct stud total.
- Output sizes match real xenia and RPCS3 saves byte for byte.
- A save converted from RPCS3 was loaded by the game under xenia, which then wrote
  `V2GAME01` back to it.

## Known limits

- Cross-version conversion is not attempted (see above); Wii U saves stay at version 12.
- Converting a non-version-13 save **to PS4** emits a warning: the PS4 signature constant
  is only known for version 13.
- The PS4 main blob is 8 bytes longer than the Xbox 360 one (308,053 vs 308,045). Lengths
  are preserved rather than trimmed.
- The record grammar and the 64-bit field types were reverse-engineered from a handful of
  real saves. Field shapes those saves never exercised are unverified — see the warning at
  the top.
- Record bodies that are neither nested records nor 24-byte entry arrays are left alone, so
  a 64-bit value living in one of them is carried across unswapped and comes out 2^32 times
  too large. The `38EFEFE9` record (a plain array of 64-bit slots) is known to be like this.
- Options files (`OPTS` / `ZOPTS` / `OPTSC` / `FEOPTS`) are skipped by default. Unlike the
  save data they are raw structs in the console's own byte order, with no chunk to walk, so
  they cannot be translated — and they hold settings, not progress. The checkbox only takes
  effect between platforms of the same byte order; across a boundary they are skipped with a
  note in the log rather than copied over and scrambling the target's settings.
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
