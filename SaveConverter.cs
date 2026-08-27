// LEGO Dimensions save converter
// Xbox 360 (xenia) <-> PS3 (RPCS3) <-> PS4 (shadPS4) <-> Wii U (Cemu)
//
// Every platform stores the same 24-byte header followed by a payload:
//   u32 version   u32 sigA   u32 zero   u32 sigB (0x502C3F10)   u32 checksum   u32 length
// The header dwords use the console's native byte order. On the PowerPC consoles
// sigA is simply sigB + version (checked against versions 1, 12 and 13); the PS4
// uses its own constant instead.
//
// The payload is otherwise identical between platforms, with one exception: the
// PS4 main blob carries 8 extra zero bytes at payload offset 1080, shifting
// everything after it. Those are stripped on read and re-inserted on write, so
// the rest of the tool works on one canonical layout.
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
        new Regex(@"^(V2GAME|GAME|DLC|OPTSC|OPTS|FEOPTS)(\d{1,2})$", RegexOptions.IgnoreCase);

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

    public static Plat Detect(string dir, out List<string> notes) {
        notes = new List<string>();
        bool sawBE = false, sawLE = false, sawPad = false, sawSingle = false;
        uint ver = 0;
        foreach (string f in Directory.GetFiles(dir)) {
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
            if (m.Groups[2].Value.Length == 1 && bas != "DLC") sawSingle = true;
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
        bool zero = ZeroBased(src);
        bool hasV2 = File.Exists(Path.Combine(dir, zero ? "V2GAME0" : "V2GAME01"));
        foreach (string f in Directory.GetFiles(dir)) {
            string n = Path.GetFileName(f);
            Match m = AnyName.Match(n);
            if (!m.Success) continue;
            string bas = m.Groups[1].Value.ToUpperInvariant();
            bool isOpt = (bas == "OPTS" || bas == "OPTSC" || bas == "FEOPTS");
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
            } else r.Logical = "OPT:" + bas;

            if (src == Plat.PS4 && IsGameBlob(r.Logical)) {
                bool wasZero;
                r.Payload = StripGap(r.Payload, out wasZero);
                if (log != null && !wasZero)
                    log("WARNING: the 8 bytes at offset " + PS4_GAP_OFF + " of " + n +
                        " were not zero; removing them anyway.");
            }
            recs.Add(r);
        }
        return recs;
    }

    static byte[] Build(Plat tgt, string logical, uint version, uint checksum, byte[] payload) {
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
        if ((o == "OPTSC" || o == "FEOPTS") && tgt == Plat.X360) return null;   // not used on Xbox
        return o + one;
    }

    public static void Write(string outDir, Plat tgt, List<Rec> recs, Action<string> log) {
        Directory.CreateDirectory(outDir);
        string bak = Path.Combine(outDir, "_backup_");
        Rec main = null;
        foreach (Rec r in recs) if (r.Logical == "MAIN") main = r;
        if (main == null) throw new Exception("The source folder has no main save file (GAME / V2GAME).");

        if (tgt == Plat.PS4 && main.Version != 13)
            log("WARNING: the PS4 signature is only known for save version 13; this save is version "
                + main.Version + ", so the result may be rejected.");

        var plan = new List<Rec>();
        foreach (Rec r in recs) if (r.Logical != "LEGACY") plan.Add(r);

        // GAME is the first N bytes of V2GAME sharing its checksum, so it is rebuilt from
        // the main blob at the length this save revision uses.
        int lg = LegacyLen(main.Version);
        if (lg > 0 && tgt != Plat.PS3) {
            int n = Math.Min(lg, main.Payload.Length);
            var leg = new Rec();
            leg.Logical = "LEGACY"; leg.Version = main.Version; leg.Checksum = main.Checksum;
            leg.Payload = new byte[n];
            Array.Copy(main.Payload, 0, leg.Payload, 0, n);
            plan.Add(leg);
            log("GAME rebuilt from the main blob: " + n + " bytes of canonical payload");
        }

        int written = 0;
        foreach (Rec r in plan) {
            string name = OutName(tgt, r);
            if (name == null) { log("skipped " + r.Name + " (not used on this platform)"); continue; }
            string dst = Path.Combine(outDir, name);
            if (File.Exists(dst)) {
                Directory.CreateDirectory(bak);
                File.Copy(dst, Path.Combine(bak, name), true);
            }
            File.WriteAllBytes(dst, Build(tgt, r.Logical, r.Version, r.Checksum, r.Payload));
            written++;
        }
        log("files written: " + written + " (save version " + main.Version + " preserved)");
        if (Directory.Exists(bak)) log("existing files were copied to _backup_ first");
    }
}

class MainForm : Form {
    TextBox src = new TextBox(), dst = new TextBox(), log = new TextBox();
    ComboBox tgt = new ComboBox();
    Label det = new Label();
    CheckBox opts = new CheckBox();

    public MainForm() {
        Text = "LEGO Dimensions - Save Converter";
        ClientSize = new Size(660, 430);
        MinimumSize = new Size(560, 380);
        Font = new Font("Segoe UI", 9f);

        Add(new Label { Text = "Source save folder:", Left = 12, Top = 12, Width = 400 });
        src.SetBounds(12, 32, 540, 23); src.Anchor = AnchorLeft(); Add(src);
        var b1 = new Button { Text = "Browse...", Left = 560, Top = 31, Width = 84 };
        b1.Anchor = AnchorRight(); b1.Click += (s, e) => Pick(src); Add(b1);

        det.SetBounds(12, 60, 632, 20); det.ForeColor = Color.DimGray; Add(det);

        Add(new Label { Text = "Target platform:", Left = 12, Top = 88, Width = 130 });
        tgt.SetBounds(148, 85, 240, 23);
        tgt.DropDownStyle = ComboBoxStyle.DropDownList;
        tgt.Items.AddRange(new object[] {
            "Xbox 360 (xenia)", "PS3 (RPCS3)", "PS4 (shadPS4)", "Wii U (Cemu)" });
        tgt.SelectedIndex = 0; Add(tgt);

        opts.SetBounds(400, 86, 244, 22);
        opts.Text = "Convert options files too";
        Add(opts);

        Add(new Label { Text = "Output folder:", Left = 12, Top = 120, Width = 400 });
        dst.SetBounds(12, 140, 540, 23); dst.Anchor = AnchorLeft(); Add(dst);
        var b2 = new Button { Text = "Browse...", Left = 560, Top = 139, Width = 84 };
        b2.Anchor = AnchorRight(); b2.Click += (s, e) => Pick(dst); Add(b2);

        var go = new Button { Text = "Convert", Left = 12, Top = 172, Width = 160, Height = 30 };
        go.Click += (s, e) => Run(); Add(go);

        log.SetBounds(12, 212, 632, 200);
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
        det.Text = "";
        if (!Directory.Exists(src.Text)) return;
        try {
            List<string> notes;
            Plat p = Conv.Detect(src.Text, out notes);
            int n = Conv.Read(src.Text, p, false, null).Count;
            det.Text = "Detected: " + Human(p) + " - " + string.Join(", ", notes.ToArray()) + "; data files: " + n;
            det.ForeColor = Color.FromArgb(0, 110, 0);
        } catch (Exception ex) {
            det.Text = ex.Message; det.ForeColor = Color.Firebrick;
        }
    }

    static string Human(Plat p) {
        if (p == Plat.X360) return "Xbox 360";
        if (p == Plat.PS3) return "PS3";
        if (p == Plat.PS4) return "PS4";
        return "Wii U";
    }

    void Run() {
        log.Clear();
        try {
            if (!Directory.Exists(src.Text)) throw new Exception("Source folder not found.");
            if (string.IsNullOrEmpty(dst.Text)) throw new Exception("Choose an output folder.");
            if (Path.GetFullPath(src.Text).TrimEnd('\\').Equals(Path.GetFullPath(dst.Text).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
                throw new Exception("Source and output are the same folder - pick a different one.");

            List<string> notes;
            Plat s = Conv.Detect(src.Text, out notes);
            Plat t = (Plat)tgt.SelectedIndex;
            Say("Source: " + Human(s) + " (" + string.Join(", ", notes.ToArray()) + ")");
            Say("Target: " + Human(t));
            if (s == t) Say("NOTE: source and target are the same platform, so files are just rewritten.");

            List<Rec> recs = Conv.Read(src.Text, s, opts.Checked, Say);
            Say("Files read: " + recs.Count);
            if (s == Plat.PS4 && t != Plat.PS4) Say("Removed the 8-byte PS4 gap from the main save blob.");
            if (t == Plat.PS4 && s != Plat.PS4) Say("Inserted the 8-byte PS4 gap into the main save blob.");
            Conv.Write(dst.Text, t, recs, Say);

            Say("");
            Say("Done. Copy the result into a save slot the game itself created, so that");
            if (t == Plat.PS4) Say("the sce_sys folder (param.sfo, icon0.png) stays in place.");
            else if (t == Plat.PS3) Say("PARAM.SFO and ICON0.PNG stay in place.");
            else if (t == Plat.WiiU) Say("the meta folder (meta.xml, iconTex.tga) stays in place.");
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
