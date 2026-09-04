// LEGO Dimensions save converter
// Xbox 360 (xenia) <-> PS3 (RPCS3) <-> PS4 (shadPS4) <-> Wii U (Cemu)
//
// Every platform stores the same 24-byte header followed by a payload:
//   u32 version   u32 sigA   u32 zero   u32 sigB (0x502C3F10)   u32 checksum   u32 length
// The header dwords use the console's native byte order. On the PowerPC consoles
// sigA is simply sigB + version (checked against versions 1, 12 and 13); the PS4
// uses its own constant instead.
//
// Two things inside the payload are platform dependent.
//
// 1. The PS4 main blob carries 8 extra zero bytes in the zero pad ahead of the save
//    chunk, so its chunk magic sits at payload offset 1088 instead of 1080. Those are
//    stripped on read and re-inserted on write.
//
// 2. Every 32-bit field in the payload is little-endian on all four platforms, but a
//    64-bit field is written as two little-endian halves in the console's own word
//    order: low half first on the PS4, high half first on the PowerPC consoles. Copying
//    the payload across therefore leaves every 64-bit value byte-reversed, which the
//    game reads as garbage - progress stored in those fields is silently lost. The
//    halves are swapped on read/write for the PS4 side, using the record walk in
//    Endian64 to find them.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

public static class Fmt {
    public const uint SIG_B = 0x502C3F10;
    public const uint SIG_A_PS4_V13 = 0x81E359DF;   // PS4 does not follow the sigB + version rule
    public const int HDR = 24;

    public static uint Rd(byte[] b, int o, bool be) {
        return be ? (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3])
                  : (uint)((b[o + 3] << 24) | (b[o + 2] << 16) | (b[o + 1] << 8) | b[o]);
    }
    public static void Wr(byte[] b, int o, uint v, bool be) {
        if (be) { b[o] = (byte)(v >> 24); b[o+1] = (byte)(v >> 16); b[o+2] = (byte)(v >> 8); b[o+3] = (byte)v; }
        else    { b[o+3] = (byte)(v >> 24); b[o+2] = (byte)(v >> 16); b[o+1] = (byte)(v >> 8); b[o] = (byte)v; }
    }
}

public enum Plat { X360, PS3, PS4, WiiU }

// Finds the 64-bit fields inside a save payload so their two halves can be swapped when
// moving between the PS4 and the PowerPC consoles.
//
// The payload is a zero pad, then a chunk header
//     u32 magic (0x4DEE7E53)   u32 count   u32 hash   u32 dataOffset
// with the record stream starting four bytes past magic + 16 + dataOffset. A record is
//     u32 key   u32 size   size bytes of body   u32 key       (the key repeats as a terminator)
// and a body is either more records or an array of 24-byte entries
//     u32 id   float   u64   u64
// Only those two u64 slots are 64-bit; everything else is 32-bit and needs no swapping.
public static class Endian64 {
    const int MAX_DEPTH = 24;

    static uint W(byte[] b, int o) {
        return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }

    // Validates a run of records, and when hits is non-null also records the 64-bit slots.
    // Passing hits = null makes it a pure probe, which is how a body is classified as
    // "nested records" rather than "entry array".
    static bool Scan(byte[] b, int off, int len, List<int> hits, int depth) {
        if (depth > MAX_DEPTH) return false;
        int p = off, end = off + len, seen = 0;
        while (p < end) {
            if (p + 12 > end) return false;
            uint key = W(b, p), size = W(b, p + 4);
            if (size > (uint)(end - p - 12)) return false;
            int body = p + 8, blen = (int)size;
            if (W(b, body + blen) != key) return false;
            seen++;
            if (blen > 0) {
                if (Scan(b, body, blen, null, depth + 1)) {
                    if (hits != null) Scan(b, body, blen, hits, depth + 1);
                } else if (hits != null && blen % 24 == 0) {
                    Entries(b, body, blen, hits);
                }
            }
            p = body + blen + 4;
        }
        return seen > 0;
    }

    // Walks the top-level stream as far as the grammar holds; the tail is zero padding.
    static void Walk(byte[] b, int start, int end, List<int> hits) {
        int p = start;
        while (p + 12 <= end) {
            uint key = W(b, p), size = W(b, p + 4);
            if (size > (uint)(end - p - 12)) break;
            int body = p + 8, blen = (int)size;
            if (W(b, body + blen) != key) break;
            if (blen > 0) {
                if (Scan(b, body, blen, null, 1)) Scan(b, body, blen, hits, 1);
                else if (blen % 24 == 0) Entries(b, body, blen, hits);
            }
            p = body + blen + 4;
        }
    }

    // The stud counter is the one entry known to hold a 32-bit value where every other entry
    // holds a 64-bit one. Swapping it multiplies the player's stud total by 2^32. Derived by
    // reading the type off real Xbox 360 saves, where a 64-bit value below 2^32 is stored
    // high half first (0, v) and a 32-bit field is stored (v, 0): across two genuine saves
    // 3365 ids read as 64-bit, this one alone read as 32-bit, and none read as both.
    static readonly uint[] NOT_64_BIT = { 0x0EEB6A85 };

    static bool Is64(uint id) {
        foreach (uint x in NOT_64_BIT) if (x == id) return false;
        return true;
    }

    // Collects the 64-bit slots of an array of 24-byte { u32 id, u32 tag, u64, u64 } entries.
    // A slot only counts when exactly one half is zero, which is what a 64-bit value below
    // 2^32 looks like. That leaves opaque byte arrays whose length happens to be a multiple
    // of 24 untouched, since their halves are both non-zero.
    static void Entries(byte[] b, int off, int len, List<int> hits) {
        for (int e = off; e + 24 <= off + len; e += 24) {
            if (!Is64(W(b, e))) continue;
            for (int s = 8; s <= 16; s += 8) {
                uint d0 = W(b, e + s), d1 = W(b, e + s + 4);
                if ((d0 == 0) != (d1 == 0)) hits.Add(e + s);
            }
        }
    }

    // Swaps the halves of every 64-bit field in place. Returns how many were found, or -1
    // if the payload has no save chunk at all (the options files, which are raw structs).
    public static int SwapHalves(byte[] payload) {
        int magic = -1;
        for (int i = 0; i + 16 <= payload.Length; i++)
            if (payload[i] == 0x53 && payload[i + 1] == 0x7e && payload[i + 2] == 0xee && payload[i + 3] == 0x4d) {
                magic = i; break;
            }
        if (magic < 0) return -1;
        long data = (long)magic + 16 + W(payload, magic + 12);
        int start = (int)data + 4;
        if (data < 0 || start >= payload.Length) return -1;

        var hits = new List<int>();
        Walk(payload, start, payload.Length, hits);
        foreach (int o in hits) {
            if (o + 8 > payload.Length) continue;
            for (int k = 0; k < 4; k++) {
                byte t = payload[o + k]; payload[o + k] = payload[o + 4 + k]; payload[o + 4 + k] = t;
            }
        }
        return hits.Count;
    }
}

// Moves a save between revision 12 (Wii U) and revision 13 (Xbox 360, PS3, PS4).
//
// Leaving a save at the revision it came from is what makes a converted Wii U save come up
// as a new game on Xbox 360: the header still says revision 12, and the game does not take
// it for one of its own.
//
// The revisions differ far less than that suggests. The record stream is a dictionary keyed
// by record id, and a record simply being absent is normal: in one real Xbox 360 save DLC01
// carries 10CA236C while DLC02 and DLC03 do not, all three being revision 13 and all three
// the same length. What actually differs between the revisions is the payload length, which
// every real save of a revision matches exactly:
//
//                    revision 12    revision 13
//     GAME               100381         100417     a prefix of the main blob, cut by length
//     V2GAME             308809         308045
//     DLC                234805         234817
//
// Past the record stream's terminator dword (123456789) the payload is zero padding, so the
// length is met by trimming or extending that padding - never by cutting data.
//
// One record is added on the way up to 13: 5081EB33, six small counters in a 24-byte body,
// which the game writes into every save it makes (all zeros in a fresh one) but which a
// revision 12 save does not carry. It is put back with a zero body, which is what a save
// that never had it deserves, and the chunk header's count - which includes the stream
// terminator - moves with it. Coming back down it is removed only when its body is still
// all zeros: on a real Xbox 360 save those counters hold data, and data is never dropped.
public static class Revision {
    public const uint MAIN_ADD = 0x5081EB33;
    public const uint DLC_ADD  = 0x10CA236C;
    const uint AFTER = 0xD02558B0;      // the record the added one follows
    const int MAIN_BODY = 24;

    static uint W(byte[] b, int o) {
        return (uint)(b[o] | (b[o+1] << 8) | (b[o+2] << 16) | (b[o+3] << 24));
    }
    static void Wr(byte[] b, int o, uint v) {
        b[o] = (byte)v; b[o+1] = (byte)(v >> 8); b[o+2] = (byte)(v >> 16); b[o+3] = (byte)(v >> 24);
    }

    static int Chunk(byte[] p) {
        for (int i = 0; i + 16 <= p.Length; i++)
            if (p[i] == 0x53 && p[i+1] == 0x7e && p[i+2] == 0xee && p[i+3] == 0x4d) return i;
        return -1;
    }

    // Walks the top-level record stream looking for one key. Returns where that record
    // starts, and through past the offset just after it.
    static int Find(byte[] p, int start, uint key, out int past) {
        past = -1;
        int q = start;
        while (q + 12 <= p.Length) {
            uint k = W(p, q), size = W(p, q + 4);
            if (size > (uint)(p.Length - q - 12)) break;
            int end = q + 8 + (int)size + 4;
            if (W(p, q + 8 + (int)size) != k) break;
            if (k == key) { past = end; return q; }
            q = end;
        }
        return -1;
    }

    // The revision each platform's game writes.
    public static uint Native(Plat p) { return p == Plat.WiiU ? 12u : 13u; }

    // Canonical payload length per revision, measured on real saves. -1 where unknown.
    public static int Length(string logical, uint version) {
        bool dlc = logical.StartsWith("DLC:");
        if (version == 13) return dlc ? 234817 : 308045;
        if (version == 12) return dlc ? 234805 : 308809;
        return -1;
    }

    // The record revision 13 carries and revision 12 does not, in the main blob only.
    static bool HasCounters(string logical) { return logical == "MAIN" || logical == "LEGACY"; }

    // A blob has a record stream; the options files are raw structs and have none.
    static bool IsBlob(string logical) {
        return HasCounters(logical) || logical.StartsWith("DLC:");
    }

    // Trims the zero padding back to the length real saves of this revision have, or extends
    // it. Data is never cut: a tail that is not all zeros is left exactly as it is.
    static byte[] Fit(byte[] p, int want) {
        if (want <= 0 || p.Length == want) return p;
        if (p.Length > want)
            for (int i = want; i < p.Length; i++) if (p[i] != 0) return p;
        byte[] o = new byte[want];
        Array.Copy(p, 0, o, 0, Math.Min(want, p.Length));
        return o;
    }

    static byte[] Add(byte[] p, int at, uint key, int bodyLen) {
        byte[] o = new byte[p.Length + 12 + bodyLen];
        Array.Copy(p, 0, o, 0, at);
        Wr(o, at, key);
        Wr(o, at + 4, (uint)bodyLen);                       // the body itself stays zero
        Wr(o, at + 8 + bodyLen, key);                       // the key repeats as a terminator
        Array.Copy(p, at, o, at + 12 + bodyLen, p.Length - at);
        return o;
    }

    static byte[] Cut(byte[] p, int at, int len) {
        byte[] o = new byte[p.Length - len];
        Array.Copy(p, 0, o, 0, at);
        Array.Copy(p, at + len, o, at, p.Length - at - len);
        return o;
    }

    static bool AllZero(byte[] p, int off, int len) {
        for (int i = off; i < off + len; i++) if (p[i] != 0) return false;
        return true;
    }

    // Returns the payload at revision "to", or null when this file cannot be converted, with
    // note saying why.
    public static byte[] Retarget(byte[] payload, uint from, uint to, string logical, out string note) {
        note = null;
        if (from == to) return payload;
        if (!IsBlob(logical)) { note = "it is a raw struct with no record stream"; return null; }
        if ((from != 12 || to != 13) && (from != 13 || to != 12)) {
            note = "only revisions 12 and 13 are understood";
            return null;
        }
        int want = Length(logical, to);
        if (want < 0) { note = "revision " + to + " has no known length for it"; return null; }

        byte[] outp = payload;
        if (HasCounters(logical)) {
            int magic = Chunk(payload);
            if (magic < 0) { note = "it has no save chunk"; return null; }
            int start = magic + 16 + (int)W(payload, magic + 12) + 4;
            if (start >= payload.Length) { note = "its save chunk is malformed"; return null; }
            int past;
            int here = Find(payload, start, MAIN_ADD, out past);
            if (to == 13 && here < 0) {
                if (Find(payload, start, AFTER, out past) < 0) {
                    note = AFTER.ToString("X8") + " is missing"; return null;
                }
                outp = Add(payload, past, MAIN_ADD, MAIN_BODY);
                Wr(outp, magic + 4, W(outp, magic + 4) + 1);
            } else if (to == 12 && here >= 0 && AllZero(payload, here + 8, MAIN_BODY)) {
                outp = Cut(payload, here, past - here);
                Wr(outp, magic + 4, W(outp, magic + 4) - 1);
            }
        }
        byte[] fitted = Fit(outp, want);
        if (fitted.Length != want) {
            note = "trimming it to " + want + " bytes would cut data";
            return null;
        }
        return fitted;
    }
}

public class Rec {
    public string Name;         // original file name
    public string Logical;      // MAIN / LEGACY / DLC:<n> / OPT:<base>
    public uint Version;
    public uint Checksum;
    public byte[] Payload;      // always in canonical (non-PS4) layout
}

public static class Conv {
    public const int PS4_GAP_OFF = 1080;    // PS4 inserts 8 zero bytes here in GAME / V2GAME
    public const int PS4_GAP_LEN = 8;

    public static bool IsBE(Plat p) { return p != Plat.PS4; }
    public static bool Pads(Plat p) { return p == Plat.PS3; }   // PS3 rounds files up to 1 KiB
    public static bool ZeroBased(Plat p) { return p == Plat.WiiU; }

    public static uint SigA(Plat p, uint version) {
        if (p != Plat.PS4) return Fmt.SIG_B + version;
        return Fmt.SIG_A_PS4_V13;                               // only known for version 13
    }

    // Canonical GAME payload length per save revision; GAME is a prefix of V2GAME.
    public static int LegacyLen(uint version) {
        if (version == 13) return 100417;
        if (version == 12) return 100381;
        return -1;
    }

    static bool IsGameBlob(string logical) { return logical == "MAIN" || logical == "LEGACY"; }

    static byte[] StripGap(byte[] p, out bool wasZero) {
        wasZero = true;
        if (p.Length < PS4_GAP_OFF + PS4_GAP_LEN) return p;
        for (int i = 0; i < PS4_GAP_LEN; i++) if (p[PS4_GAP_OFF + i] != 0) wasZero = false;
        byte[] o = new byte[p.Length - PS4_GAP_LEN];
        Array.Copy(p, 0, o, 0, PS4_GAP_OFF);
        Array.Copy(p, PS4_GAP_OFF + PS4_GAP_LEN, o, PS4_GAP_OFF, p.Length - PS4_GAP_OFF - PS4_GAP_LEN);
        return o;
    }

    static byte[] InsertGap(byte[] p) {
        if (p.Length < PS4_GAP_OFF) return p;
        byte[] o = new byte[p.Length + PS4_GAP_LEN];
        Array.Copy(p, 0, o, 0, PS4_GAP_OFF);
        Array.Copy(p, PS4_GAP_OFF, o, PS4_GAP_OFF + PS4_GAP_LEN, p.Length - PS4_GAP_OFF);
        return o;
    }

    static readonly Regex AnyName =
        new Regex(@"^(V2GAME|GAME|DLC|OPTSC|ZOPTS|OPTS|FEOPTS|GLOBAL)(\d{1,2})$", RegexOptions.IgnoreCase);

    // A save file is recognised by sigB plus a sane version, read either way round.
    public static bool Probe(byte[] b, out bool be, out uint ver, out uint len) {
        be = false; ver = 0; len = 0;
        if (b.Length < Fmt.HDR) return false;
        foreach (bool cand in new[] { true, false }) {
            if (Fmt.Rd(b, 12, cand) != Fmt.SIG_B) continue;
            uint v = Fmt.Rd(b, 0, cand);
            if (v == 0 || v > 255) continue;
            uint n = Fmt.Rd(b, 20, cand);
            if (n > b.Length - Fmt.HDR) continue;
            be = cand; ver = v; len = n;
            return true;
        }
        return false;
    }

    // The files that make up one save. Cemu keeps the shared options file one folder up,
    // in an OPTIONS folder next to the slots, so it is picked up from there as well.
    public static List<string> SaveFiles(string dir) {
        var files = new List<string>(Directory.GetFiles(dir));
        string parent = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
        if (parent != null) {
            string opt = Path.Combine(parent, "OPTIONS");
            if (Directory.Exists(opt) && !Same(opt, dir)) files.AddRange(Directory.GetFiles(opt));
        }
        return files;
    }

    public static bool Same(string a, string b) {
        return Path.GetFullPath(a).TrimEnd('\\', '/')
            .Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    const int MAX_SEARCH_DEPTH = 10;

    static bool HasMain(string dir) {
        foreach (string f in Directory.GetFiles(dir)) {
            Match m = AnyName.Match(Path.GetFileName(f));
            if (!m.Success) continue;
            string bas = m.Groups[1].Value.ToUpperInvariant();
            if (bas != "GAME" && bas != "V2GAME") continue;
            bool be; uint v, len;
            try { if (Probe(File.ReadAllBytes(f), out be, out v, out len)) return true; }
            catch (IOException) { }
        }
        return false;
    }

    static void Descend(string dir, List<string> found, int depth) {
        try {
            if (HasMain(dir)) { found.Add(dir); return; }   // a slot has no slots inside it
            if (depth >= MAX_SEARCH_DEPTH) return;
            foreach (string sub in Directory.GetDirectories(dir)) {
                string n = Path.GetFileName(sub);
                // _backup_ holds what a previous run replaced, and meta only ever holds the
                // slot icon and its xml - neither is a save.
                if (n.StartsWith("_") || n.Equals("meta", StringComparison.OrdinalIgnoreCase)) continue;
                Descend(sub, found, depth + 1);
            }
        } catch (UnauthorizedAccessException) { }
    }

    // A Wii U save is not a flat folder: Cemu stores it as
    // save\00050000\<title id>\user\<account>\Slot<n>, so the folder the user picks is
    // normally an ancestor of the real one, and a dump can carry several slots. Every folder
    // below the chosen one that holds a main save file counts as a slot. The other three
    // platforms keep one flat folder, which this finds as the single slot.
    public static List<string> FindSlots(string root) {
        var found = new List<string>();
        Descend(root, found, 0);
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    public static string Name(Plat p) {
        if (p == Plat.X360) return "Xbox 360";
        if (p == Plat.PS3) return "PS3";
        if (p == Plat.PS4) return "PS4";
        return "Wii U";
    }

    // What to say when a folder holds more than one save: the full path of each, with what
    // it turned out to be, so the user can point the tool at exactly one of them.
    public static List<string> DescribeSlots(string root, List<string> slots) {
        var lines = new List<string>();
        lines.Add("Found " + slots.Count + " saves below " + root + ":");
        lines.Add("");
        foreach (string d in slots) {
            string what;
            try {
                List<string> notes;
                Plat p = Detect(d, out notes);
                what = Name(p) + " - " + string.Join(", ", notes.ToArray())
                     + "; data files: " + Read(d, p, false, null).Count;
            } catch (Exception ex) { what = ex.Message; }
            lines.Add("    " + d);
            lines.Add("        " + what);
        }
        lines.Add("");
        lines.Add("Point the Source save folder at exactly one of these and convert again.");
        return lines;
    }

    public static Plat Detect(string dir, out List<string> notes) {
        notes = new List<string>();
        bool sawBE = false, sawLE = false, sawPad = false, sawSingle = false;
        uint ver = 0;
        foreach (string f in SaveFiles(dir)) {
            string n = Path.GetFileName(f);
            Match m = AnyName.Match(n);
            if (!m.Success) continue;
            byte[] b = File.ReadAllBytes(f);
            bool be; uint v, len;
            if (!Probe(b, out be, out v, out len)) continue;
            if (be) sawBE = true; else sawLE = true;
            if (b.Length > Fmt.HDR + len) sawPad = true;
            ver = v;
            // GAME0 / V2GAME0 / OPTS0 are unambiguous: only Wii U numbers from zero.
            string bas = m.Groups[1].Value.ToUpperInvariant();
            if (m.Groups[2].Value.Length == 1 && bas != "DLC" && bas != "GLOBAL") sawSingle = true;
        }
        if (!sawBE && !sawLE) throw new Exception("No LEGO Dimensions save files found in this folder.");
        notes.Add("save version " + ver);
        if (sawLE) { notes.Add("little-endian"); return Plat.PS4; }
        notes.Add("big-endian");
        if (sawSingle) { notes.Add("zero-based file names"); return Plat.WiiU; }
        if (sawPad) { notes.Add("padded to a 1 KiB boundary"); return Plat.PS3; }
        return Plat.X360;
    }

    public static List<Rec> Read(string dir, Plat src, bool withOptions, Action<string> log) {
        var recs = new List<Rec>();
        int swapped = 0;
        bool zero = ZeroBased(src);
        bool hasV2 = File.Exists(Path.Combine(dir, zero ? "V2GAME0" : "V2GAME01"));
        foreach (string f in SaveFiles(dir)) {
            string n = Path.GetFileName(f);
            Match m = AnyName.Match(n);
            if (!m.Success) continue;
            string bas = m.Groups[1].Value.ToUpperInvariant();
            // ZOPTS is what the PS4 calls OPTS, so it counts as an options file too.
            bool isOpt = (bas == "OPTS" || bas == "ZOPTS" || bas == "OPTSC" || bas == "FEOPTS" || bas == "GLOBAL");
            if (isOpt && !withOptions) continue;
            byte[] b = File.ReadAllBytes(f);
            bool be; uint ver, len;
            if (!Probe(b, out be, out ver, out len)) continue;
            var r = new Rec();
            r.Name = n; r.Version = ver; r.Checksum = Fmt.Rd(b, 16, be);
            r.Payload = new byte[len];
            Array.Copy(b, Fmt.HDR, r.Payload, 0, (int)len);
            if (bas == "V2GAME") r.Logical = "MAIN";
            else if (bas == "GAME") r.Logical = hasV2 ? "LEGACY" : "MAIN";   // PS3 stores the full blob here
            else if (bas == "DLC") {
                int idx = int.Parse(m.Groups[2].Value);
                r.Logical = "DLC:" + (zero ? idx + 1 : idx);                 // canonical index is 1-based
            } else r.Logical = "OPT:" + (bas == "ZOPTS" ? "OPTS" : bas);   // PS4 calls OPTS01 ZOPTS01

            if (src == Plat.PS4 && IsGameBlob(r.Logical)) {
                bool wasZero;
                r.Payload = StripGap(r.Payload, out wasZero);
                if (log != null && !wasZero)
                    log("WARNING: the 8 bytes at offset " + PS4_GAP_OFF + " of " + n +
                        " were not zero; removing them anyway.");
            }
            // Bring the PS4 word order into the canonical (PowerPC) one.
            if (src == Plat.PS4) swapped += Endian64.SwapHalves(r.Payload);
            recs.Add(r);
        }
        if (log != null && swapped > 0)
            log("64-bit fields re-ordered out of PS4 word order: " + swapped);
        return recs;
    }

    static byte[] Build(Plat tgt, string logical, uint version, uint checksum, byte[] payload) {
        if (tgt == Plat.PS4) {
            payload = (byte[])payload.Clone();      // records are shared, never edit in place
            Endian64.SwapHalves(payload);
        }
        if (tgt == Plat.PS4 && IsGameBlob(logical)) payload = InsertGap(payload);
        int exact = Fmt.HDR + payload.Length;
        int total = Pads(tgt) ? ((exact + 1023) / 1024) * 1024 : exact;
        byte[] o = new byte[total];
        bool be = IsBE(tgt);
        Fmt.Wr(o, 0, version, be);
        Fmt.Wr(o, 4, SigA(tgt, version), be);
        Fmt.Wr(o, 8, 0, be);
        Fmt.Wr(o, 12, Fmt.SIG_B, be);
        Fmt.Wr(o, 16, checksum, be);
        Fmt.Wr(o, 20, (uint)payload.Length, be);
        Array.Copy(payload, 0, o, Fmt.HDR, payload.Length);
        return o;
    }

    static string Suffix(Plat tgt, int canonicalIndex) {
        if (ZeroBased(tgt)) return (canonicalIndex - 1).ToString();
        return canonicalIndex.ToString("00");
    }

    static string OutName(Plat tgt, Rec r) {
        string one = ZeroBased(tgt) ? "0" : "01";
        if (r.Logical == "MAIN")   return tgt == Plat.PS3 ? "GAME" + one : "V2GAME" + one;
        if (r.Logical == "LEGACY") return "GAME" + one;
        if (r.Logical.StartsWith("DLC:")) return "DLC" + Suffix(tgt, int.Parse(r.Logical.Substring(4)));
        string o = r.Logical.Substring(4);
        if (o == "OPTS" && tgt == Plat.PS4) return "ZOPTS" + one;   // PS4 spells it ZOPTS01
        // OPTSC / FEOPTS are absent from the Xbox saves seen so far, but that was only
        // ever an assumption - and OPTSC is a plausible home for unlocked cheats, i.e.
        // red bricks. Write them anyway: if the Xbox build ignores them, nothing is lost.
        return o + one;
    }

    // Wii U splits a save in two: the slot files live in Slot<n> and the shared options
    // file in an OPTIONS folder beside it. The other three platforms keep one flat folder.
    static string SubDir(Plat tgt, string logical) {
        if (tgt != Plat.WiiU) return "";
        return logical == "OPT:GLOBAL" ? "OPTIONS" : "Slot1";
    }

    // Writes one file, keeping a copy of whatever it replaces in _backup_.
    static void Emit(string outDir, string bak, string sub, string name, byte[] bytes) {
        string dir = sub.Length == 0 ? outDir : Path.Combine(outDir, sub);
        Directory.CreateDirectory(dir);
        string dst = Path.Combine(dir, name);
        if (File.Exists(dst)) {
            string bdir = sub.Length == 0 ? bak : Path.Combine(bak, sub);
            Directory.CreateDirectory(bdir);
            File.Copy(dst, Path.Combine(bdir, name), true);
        }
        File.WriteAllBytes(dst, bytes);
    }

    public static void Write(string outDir, Plat tgt, List<Rec> recs, Action<string> log) {
        Directory.CreateDirectory(outDir);
        string bak = Path.Combine(outDir, "_backup_");
        Rec main = null;
        foreach (Rec r in recs) if (r.Logical == "MAIN") main = r;
        if (main == null) throw new Exception("The source folder has no main save file (GAME / V2GAME).");

        // Each platform's game writes its own save revision - 12 on Wii U, 13 on the other
        // three - and it does not take a save of the other revision for one of its own: the
        // slot comes up as a new game. So the save is moved to the target's revision, which
        // is a real edit of the record stream, not just a number in the header.
        uint outVer = Revision.Native(tgt);
        if (outVer != main.Version) {
            if (Revision.Length("MAIN", main.Version) < 0 || Revision.Length("MAIN", outVer) < 0) {
                log("WARNING: this save is revision " + main.Version + " and " + Name(tgt)
                    + " writes revision " + outVer + ", but only revisions 12 and 13 are"
                    + " understood - it is written unchanged and the game may not accept it.");
                outVer = main.Version;
            } else log("converting the save from revision " + main.Version + " to revision "
                       + outVer + ", which is what " + Name(tgt) + " writes");
        }
        if (tgt == Plat.PS4 && outVer != 13)
            log("WARNING: the PS4 signature is only known for save version 13; this save is version "
                + outVer + ", so the result may be rejected.");

        var plan = new List<Rec>();
        foreach (Rec r in recs) if (r.Logical != "LEGACY") plan.Add(r);

        int written = 0, moved = 0;
        byte[] mainBytes = null;
        foreach (Rec r in plan) {
            string name = OutName(tgt, r);
            if (name == null) { log("skipped " + r.Name + " (not used on this platform)"); continue; }
            byte[] pay = r.Payload;
            if (outVer != r.Version) {
                string why;
                byte[] shifted = Revision.Retarget(pay, r.Version, outVer, r.Logical, out why);
                if (shifted == null) {
                    log("skipped " + r.Name + ": it cannot be moved to revision " + outVer
                        + " because " + why + ".");
                    continue;
                }
                pay = shifted;
                moved++;
            }
            byte[] bytes = Build(tgt, r.Logical, outVer, r.Checksum, pay);
            if (r.Logical == "MAIN") mainBytes = bytes;
            Emit(outDir, bak, SubDir(tgt, r.Logical), name, bytes);
            written++;
        }
        if (moved > 0) log("files rewritten at revision " + outVer + ": " + moved);

        // GAME is not a file of its own: in a real save it is byte for byte the first N
        // bytes of V2GAME, sharing its header and checksum, with only the length field
        // adjusted. Slicing the finished main file is the only way to guarantee that -
        // rebuilding it from the payload would walk a truncated record stream and pick a
        // different set of 64-bit fields to swap.
        int lg = LegacyLen(outVer);
        if (lg > 0 && tgt != Plat.PS3 && mainBytes != null) {
            int payLen = lg + (tgt == Plat.PS4 ? PS4_GAP_LEN : 0);   // the gap sits inside the prefix
            int total = Fmt.HDR + payLen;
            if (total <= mainBytes.Length) {
                byte[] leg = new byte[total];
                Array.Copy(mainBytes, 0, leg, 0, total);
                Fmt.Wr(leg, 20, (uint)payLen, IsBE(tgt));
                Emit(outDir, bak, SubDir(tgt, "MAIN"), ZeroBased(tgt) ? "GAME0" : "GAME01", leg);
                written++;
                log("GAME written as the first " + payLen + " payload bytes of the main file");
            } else log("WARNING: the main blob is shorter than a GAME file; GAME not written.");
        }
        log("files written: " + written + " (save revision " + outVer + ")");
        if (tgt == Plat.WiiU) log("laid out the Wii U way: slot files in Slot1, GLOBAL in OPTIONS");
        if (Directory.Exists(bak)) log("existing files were copied to _backup_ first");
    }
}

class MainForm : Form {
    TextBox src = new TextBox(), dst = new TextBox(), log = new TextBox();
    ComboBox tgt = new ComboBox();
    Label det = new Label();
    CheckBox opts = new CheckBox();
    List<string> slots = new List<string>();

    public MainForm() {
        Text = "LEGO Dimensions - Save Converter";
        // The icon is linked into the exe by the build, so the window can just borrow it
        // rather than carry a second copy as a managed resource.
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        ClientSize = new Size(660, 440);
        MinimumSize = new Size(560, 390);
        Font = new Font("Segoe UI", 9f);

        Add(new Label { Text = "Source save folder:", Left = 12, Top = 12, Width = 400 });
        src.SetBounds(12, 32, 540, 23); src.Anchor = AnchorLeft(); Add(src);
        var b1 = new Button { Text = "Browse...", Left = 560, Top = 31, Width = 84 };
        b1.Anchor = AnchorRight(); b1.Click += (s, e) => Pick(src); Add(b1);

        // Two lines: the folder the user picked is often not the folder the save is in,
        // and saying where it actually is takes a second line.
        det.SetBounds(12, 60, 632, 34); det.ForeColor = Color.DimGray; Add(det);

        Add(new Label { Text = "Target platform:", Left = 12, Top = 100, Width = 130 });
        tgt.SetBounds(148, 97, 240, 23);
        tgt.DropDownStyle = ComboBoxStyle.DropDownList;
        tgt.Items.AddRange(new object[] {
            "Xbox 360 (xenia)", "PS3 (RPCS3)", "PS4 (shadPS4)", "Wii U (Cemu)" });
        tgt.SelectedIndex = 0; Add(tgt);

        opts.SetBounds(400, 98, 244, 22);
        opts.Text = "Convert options files too";
        Add(opts);

        Add(new Label { Text = "Output folder:", Left = 12, Top = 132, Width = 400 });
        dst.SetBounds(12, 152, 540, 23); dst.Anchor = AnchorLeft(); Add(dst);
        var b2 = new Button { Text = "Browse...", Left = 560, Top = 151, Width = 84 };
        b2.Anchor = AnchorRight(); b2.Click += (s, e) => Pick(dst); Add(b2);

        var go = new Button { Text = "Convert", Left = 12, Top = 184, Width = 160, Height = 30 };
        go.Click += (s, e) => Run(); Add(go);

        log.SetBounds(12, 224, 632, 204);
        log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical;
        log.BackColor = Color.White;
        log.Anchor = AnchorLeft() | AnchorStyles.Bottom;
        Add(log);

        src.TextChanged += (s, e) => Sniff();
    }

    AnchorStyles AnchorLeft()  { return AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; }
    AnchorStyles AnchorRight() { return AnchorStyles.Top | AnchorStyles.Right; }
    void Add(Control c) { Controls.Add(c); }
    void Say(string s) { log.AppendText(s + Environment.NewLine); }

    void Pick(TextBox box) {
        var d = new FolderBrowserDialog();
        if (Directory.Exists(box.Text)) d.SelectedPath = box.Text;
        if (d.ShowDialog() == DialogResult.OK) box.Text = d.SelectedPath;
    }

    void Sniff() {
        slots.Clear();
        det.Text = "";
        if (!Directory.Exists(src.Text)) return;
        try {
            slots = Conv.FindSlots(src.Text);
        } catch (Exception ex) {
            Fail(ex.Message);
            return;
        }
        if (slots.Count == 0) {
            Fail("No LEGO Dimensions save files found in this folder or below it.");
            return;
        }
        // More than one save below the chosen folder is refused rather than guessed at:
        // converting the wrong one would quietly carry the wrong progress across.
        if (slots.Count > 1) {
            Fail("Found " + slots.Count + " saves below this folder - their paths are listed below."
                 + Environment.NewLine + "Point the tool at exactly one of them.");
            log.Clear();
            ListSlots(Say);
            return;
        }
        Describe();
    }

    // Refusing an ambiguous folder is only useful if it says what it found, with full paths
    // the user can paste straight back into the source box.
    void ListSlots(Action<string> emit) {
        foreach (string line in Conv.DescribeSlots(src.Text, slots)) emit(line);
    }

    string Slot() { return slots.Count == 1 ? slots[0] : null; }

    void Fail(string msg) { det.Text = msg; det.ForeColor = Color.Firebrick; }

    void Describe() {
        string dir = Slot();
        if (dir == null) return;
        try {
            List<string> notes;
            Plat p = Conv.Detect(dir, out notes);
            int n = Conv.Read(dir, p, false, null).Count;
            string where = Conv.Same(dir, src.Text)
                         ? "" : Environment.NewLine + "Save files found in: " + dir;
            det.Text = "Detected: " + Human(p) + " - " + string.Join(", ", notes.ToArray())
                     + "; data files: " + n + where;
            det.ForeColor = Color.FromArgb(0, 110, 0);
        } catch (Exception ex) {
            Fail(ex.Message);
        }
    }

    static string Human(Plat p) { return Conv.Name(p); }

    void Run() {
        log.Clear();
        try {
            if (!Directory.Exists(src.Text)) throw new Exception("Source folder not found.");
            if (string.IsNullOrEmpty(dst.Text)) throw new Exception("Choose an output folder.");
            if (slots.Count > 1) {
                ListSlots(Say);
                Say("");
                throw new Exception("this folder holds " + slots.Count
                    + " saves - pick one of the paths listed above, not the folder above them.");
            }
            string sdir = Slot();
            if (sdir == null) throw new Exception("No LEGO Dimensions save files found in this folder or below it.");
            if (Conv.Same(src.Text, dst.Text) || Conv.Same(sdir, dst.Text))
                throw new Exception("Source and output are the same folder - pick a different one.");
            if (!Conv.Same(sdir, src.Text)) Say("Save files found in: " + sdir);

            List<string> notes;
            Plat s = Conv.Detect(sdir, out notes);
            Plat t = (Plat)tgt.SelectedIndex;
            Say("Source: " + Human(s) + " (" + string.Join(", ", notes.ToArray()) + ")");
            Say("Target: " + Human(t));
            if (s == t) Say("NOTE: source and target are the same platform, so files are just rewritten.");

            // The options files are raw structs in the console's own byte order, with no
            // save chunk to walk, so they cannot be translated - and they hold settings,
            // not progress. Carrying them across a platform boundary unconverted would
            // just scramble the target's settings.
            bool wantOptions = opts.Checked;
            if (wantOptions && Conv.IsBE(s) != Conv.IsBE(t)) {
                Say("NOTE: options files are raw " + (Conv.IsBE(s) ? "big" : "little") +
                    "-endian structs and cannot be converted; skipping them.");
                wantOptions = false;
            }
            if (wantOptions && Revision.Native(s) != Revision.Native(t)) {
                Say("NOTE: options files are raw structs with no record stream, so they cannot"
                    + " be moved between save revisions; skipping them.");
                wantOptions = false;
            }

            List<Rec> recs = Conv.Read(sdir, s, wantOptions, Say);
            Say("Files read: " + recs.Count);
            if (s == Plat.PS4 && t != Plat.PS4) Say("Removed the 8-byte PS4 gap from the main save blob.");
            if (t == Plat.PS4 && s != Plat.PS4) Say("Inserted the 8-byte PS4 gap into the main save blob.");
            Conv.Write(dst.Text, t, recs, Say);

            Say("");
            Say("Done. Copy the result into a save slot the game itself created, so that");
            if (t == Plat.PS4) Say("the sce_sys folder (param.sfo, icon0.png) stays in place.");
            else if (t == Plat.PS3) Say("PARAM.SFO and ICON0.PNG stay in place.");
            else if (t == Plat.WiiU) Say("the meta folder (meta.xml, iconTex.tga) stays in place - copy Slot1 and OPTIONS into user\\<account>\\.");
            else Say("the .header file in Headers\\ stays in place - without it xenia shows a blank slot.");
            Say("Options files and slot metadata are left untouched.");
        } catch (Exception ex) {
            Say("ERROR: " + ex.Message);
        }
    }

    [STAThread]
    static void Main() {
        Application.EnableVisualStyles();
        Application.Run(new MainForm());
    }
}
