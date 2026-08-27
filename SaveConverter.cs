// LEGO Dimensions save converter - Xbox 360 (xenia) <-> PS3 (RPCS3) <-> PS4 (shadPS4)
//
// Every platform stores the same 24-byte header followed by an opaque payload:
//   u32 version (13)   u32 sigA   u32 zero   u32 sigB (0x502C3F10)   u32 checksum   u32 length
// The header dwords use the console's native byte order; the payload itself is
// byte-identical across all three, which is what makes conversion a header rewrite
// rather than a data translation.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

public static class Fmt {
    public const uint SIG_B = 0x502C3F10;
    public const uint SIG_A_PPC = 0x502C3F1D;   // PS3 and Xbox 360
    public const uint SIG_A_PS4 = 0x81E359DF;
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

public enum Plat { X360, PS3, PS4 }

public class Rec {
    public string Name;         // original file name
    public string Logical;      // MAIN / LEGACY / DLCnn / OPT:<name>
    public uint Checksum;
    public byte[] Payload;
}

public static class Conv {
    public static bool IsBE(Plat p) { return p != Plat.PS4; }
    public static uint SigA(Plat p) { return p == Plat.PS4 ? Fmt.SIG_A_PS4 : Fmt.SIG_A_PPC; }
    public static bool Pads(Plat p) { return p == Plat.PS3; }          // PS3 rounds files up to 1 KiB
    public static int LegacyLen(Plat p) {                              // GAME01 is a prefix of V2GAME01
        if (p == Plat.X360) return 100417;
        if (p == Plat.PS4)  return 100425;
        return -1;                                                     // PS3 keeps only the full blob
    }

    static readonly Regex DataName = new Regex(@"^(V2GAME01|GAME01|DLC\d+)$", RegexOptions.IgnoreCase);
    static readonly Regex OptName  = new Regex(@"^(OPTS01|ZOPTS01|OPTSC01|FEOPTS01)$", RegexOptions.IgnoreCase);

    // A save file is recognised by its signature, read either way round.
    public static bool Probe(byte[] b, out bool be, out uint len) {
        be = false; len = 0;
        if (b.Length < Fmt.HDR) return false;
        foreach (bool cand in new[] { true, false }) {
            if (Fmt.Rd(b, 12, cand) == Fmt.SIG_B && Fmt.Rd(b, 0, cand) == 13) {
                be = cand; len = Fmt.Rd(b, 20, cand);
                return len <= b.Length - Fmt.HDR;
            }
        }
        return false;
    }

    public static Plat Detect(string dir, out List<string> notes) {
        notes = new List<string>();
        bool sawBE = false, sawLE = false, sawPad = false, sawV2 = false;
        foreach (string f in Directory.GetFiles(dir)) {
            string n = Path.GetFileName(f);
            if (!DataName.IsMatch(n) && !OptName.IsMatch(n)) continue;
            byte[] b = File.ReadAllBytes(f);
            bool be; uint len;
            if (!Probe(b, out be, out len)) continue;
            if (be) sawBE = true; else sawLE = true;
            if (b.Length > Fmt.HDR + len) sawPad = true;
            if (n.Equals("V2GAME01", StringComparison.OrdinalIgnoreCase)) sawV2 = true;
        }
        if (!sawBE && !sawLE) throw new Exception("No LEGO Dimensions save files found in this folder.");
        if (sawLE) { notes.Add("byte order: little-endian"); return Plat.PS4; }
        notes.Add("byte order: big-endian");
        if (sawPad) { notes.Add("files padded to a 1 KiB boundary"); return Plat.PS3; }
        if (sawV2)  notes.Add("V2GAME01 present");
        return Plat.X360;
    }

    public static List<Rec> Read(string dir, Plat src, bool withOptions) {
        var recs = new List<Rec>();
        bool hasV2 = File.Exists(Path.Combine(dir, "V2GAME01"));
        foreach (string f in Directory.GetFiles(dir)) {
            string n = Path.GetFileName(f);
            bool isData = DataName.IsMatch(n), isOpt = OptName.IsMatch(n);
            if (!isData && !(isOpt && withOptions)) continue;
            byte[] b = File.ReadAllBytes(f);
            bool be; uint len;
            if (!Probe(b, out be, out len)) continue;
            var r = new Rec();
            r.Name = n;
            r.Checksum = Fmt.Rd(b, 16, be);
            r.Payload = new byte[len];
            Array.Copy(b, Fmt.HDR, r.Payload, 0, (int)len);
            string up = n.ToUpperInvariant();
            if (up == "V2GAME01") r.Logical = "MAIN";
            else if (up == "GAME01") r.Logical = hasV2 ? "LEGACY" : "MAIN";  // PS3 keeps the full blob under this name
            else if (up.StartsWith("DLC")) r.Logical = up;
            else r.Logical = "OPT:" + up;
            recs.Add(r);
        }
        return recs;
    }

    static byte[] Build(Plat tgt, uint checksum, byte[] payload) {
        int exact = Fmt.HDR + payload.Length;
        int total = Pads(tgt) ? ((exact + 1023) / 1024) * 1024 : exact;
        byte[] o = new byte[total];
        bool be = IsBE(tgt);
        Fmt.Wr(o, 0, 13, be);
        Fmt.Wr(o, 4, SigA(tgt), be);
        Fmt.Wr(o, 8, 0, be);
        Fmt.Wr(o, 12, Fmt.SIG_B, be);
        Fmt.Wr(o, 16, checksum, be);
        Fmt.Wr(o, 20, (uint)payload.Length, be);
        Array.Copy(payload, 0, o, Fmt.HDR, payload.Length);
        return o;
    }

    static string OutName(Plat tgt, Rec r) {
        if (r.Logical == "MAIN")   return tgt == Plat.PS3 ? "GAME01" : "V2GAME01";
        if (r.Logical == "LEGACY") return "GAME01";
        if (r.Logical.StartsWith("DLC")) return r.Logical;
        string o = r.Logical.Substring(4);
        if (o == "OPTS01"  && tgt == Plat.PS4) return "ZOPTS01";
        if (o == "ZOPTS01" && tgt != Plat.PS4) return "OPTS01";
        if ((o == "OPTSC01" || o == "FEOPTS01") && tgt == Plat.X360) return null;  // not used on Xbox
        return o;
    }

    public static void Write(string outDir, Plat tgt, List<Rec> recs, Action<string> log) {
        Directory.CreateDirectory(outDir);
        string bak = Path.Combine(outDir, "_backup_");
        Rec main = null;
        foreach (Rec r in recs) if (r.Logical == "MAIN") main = r;
        if (main == null) throw new Exception("The source folder has no main save file (GAME01 / V2GAME01).");

        var plan = new List<Rec>();
        foreach (Rec r in recs) if (r.Logical != "LEGACY") plan.Add(r);

        // GAME01 is literally the first N bytes of the main blob sharing its checksum,
        // so it is rebuilt for the target rather than carried over at the source's length.
        int lg = LegacyLen(tgt);
        if (lg > 0) {
            int n = Math.Min(lg, main.Payload.Length);
            var leg = new Rec();
            leg.Logical = "LEGACY"; leg.Checksum = main.Checksum;
            leg.Payload = new byte[n];
            Array.Copy(main.Payload, 0, leg.Payload, 0, n);
            plan.Add(leg);
            log("GAME01 rebuilt from the main blob: " + n + " bytes of payload");
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
            byte[] data = Build(tgt, r.Checksum, r.Payload);
            File.WriteAllBytes(dst, data);
            written++;
        }
        log("files written: " + written);
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
        tgt.Items.AddRange(new object[] { "Xbox 360 (xenia)", "PS3 (RPCS3)", "PS4 (shadPS4)" });
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
            int n = Conv.Read(src.Text, p, false).Count;
            det.Text = "Detected: " + Human(p) + " - " + string.Join(", ", notes.ToArray()) + "; data files: " + n;
            det.ForeColor = Color.FromArgb(0, 110, 0);
        } catch (Exception ex) {
            det.Text = ex.Message; det.ForeColor = Color.Firebrick;
        }
    }

    static string Human(Plat p) {
        if (p == Plat.X360) return "Xbox 360";
        if (p == Plat.PS3) return "PS3";
        return "PS4";
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
            Plat t = tgt.SelectedIndex == 0 ? Plat.X360 : (tgt.SelectedIndex == 1 ? Plat.PS3 : Plat.PS4);
            Say("Source: " + Human(s) + " (" + string.Join(", ", notes.ToArray()) + ")");
            Say("Target: " + Human(t));
            if (s == t) Say("NOTE: source and target are the same platform, so files are just rewritten.");

            List<Rec> recs = Conv.Read(src.Text, s, opts.Checked);
            Say("Files read: " + recs.Count);
            Conv.Write(dst.Text, t, recs, Say);

            Say("");
            Say("Done. Copy the result into a save slot the game itself created, so that");
            if (t == Plat.PS4) Say("the sce_sys folder (param.sfo, icon0.png) stays in place.");
            else if (t == Plat.PS3) Say("PARAM.SFO and ICON0.PNG stay in place.");
            else Say("the .header file in the Headers folder stays in place.");
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
