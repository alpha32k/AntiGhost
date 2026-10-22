// AntiGhost - Rust anti stream-sniping helper.
// Trigger down -> F13, trigger up -> (delay) -> F14. The trigger key is never blocked.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

static class AntiGhost
{
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const uint LLKHF_INJECTED = 0x10, KEYEVENTF_KEYUP = 2;

    delegate IntPtr HookProc(int code, IntPtr wp, IntPtr lp);
    [StructLayout(LayoutKind.Sequential)]
    struct KBD { public uint vk, scan, flags, time; public IntPtr extra; }

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc p, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);

    static Icon AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    static HookProc proc = Hook; // keep reference alive
    static IntPtr hook;
    static bool enabled = true, down;
    static byte trigger = (byte)'G', downKey = 0x7C /*F13*/, upKey = 0x7D /*F14*/;
    static int delay = 100;
    static int lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? 0 : 1; // 0 = Türkçe, 1 = English
    static readonly System.Collections.Generic.Dictionary<string, string[]> Tx = new System.Collections.Generic.Dictionary<string, string[]> {
        {"active", new[]{"Aktif", "Active"}}, {"inactive", new[]{"Pasif", "Inactive"}},
        {"settings", new[]{"Ayarlar...", "Settings..."}}, {"exit", new[]{"Çıkış", "Exit"}},
        {"tip_on", new[]{"AntiGhost - Aktif", "AntiGhost - Active"}}, {"tip_off", new[]{"AntiGhost - Kapalı", "AntiGhost - Off"}},
        {"lang", new[]{"Dil / Language", "Dil / Language"}},
        {"trigger", new[]{"Tetik tuşu (kutuya tıkla, tuşa bas)", "Trigger key (click the box, press a key)"}},
        {"delay", new[]{"Gecikme (ms, 0–999)", "Delay (ms, 0–999)"}},
        {"autostart", new[]{"Windows açılınca başlat", "Start with Windows"}},
        {"obs", new[]{"OBS kısayolu için: tıkla, 3 sn içinde OBS kutusuna tıkla", "For OBS hotkeys: click, then click the OBS box within 3 s"}},
        {"send13", new[]{"F13 gönder", "Send F13"}}, {"send14", new[]{"F14 gönder", "Send F14"}},
        {"save", new[]{"Kaydet", "Save"}}
    };
    static string T(string k) { return Tx[k][lang]; }
    static System.Windows.Forms.Timer timer;

    static byte ParseKey(string s, byte def)
    {
        s = s.Trim().ToUpperInvariant();
        if (s.Length >= 2 && s[0] == 'F') { int n; if (int.TryParse(s.Substring(1), out n) && n >= 1 && n <= 24) return (byte)(0x6F + n); }
        if (s.Length == 1) return (byte)s[0];
        if (s.StartsWith("0X")) { try { return Convert.ToByte(s.Substring(2), 16); } catch { } }
        return def;
    }

    static void LoadConfig()
    {
        string path = Path.ChangeExtension(Application.ExecutablePath, ".ini");
        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "; AntiGhost ayarlari\r\n; Tus: A-Z, 0-9, F1-F24 veya 0xHEX (sanal tus kodu)\r\ntrigger=G\r\ndown_key=F13\r\nup_key=F14\r\ndelay_ms=100\r\n");
                return;
            }
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith(";") || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1);
                if (k == "trigger") trigger = ParseKey(v, trigger);
                else if (k == "down_key") downKey = ParseKey(v, downKey);
                else if (k == "up_key") upKey = ParseKey(v, upKey);
                else if (k == "language") lang = v.Trim().ToLowerInvariant() == "en" ? 1 : 0;
                else if (k == "delay_ms") { int d; if (int.TryParse(v, out d) && d >= 0 && d <= 999) delay = d; }
            }
        }
        catch { }
    }

    static void Tap(byte vk)
    {
        byte sc = (byte)MapVirtualKey(vk, 0);
        keybd_event(vk, sc, 0, UIntPtr.Zero);
        keybd_event(vk, sc, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    static IntPtr Hook(int code, IntPtr wp, IntPtr lp)
    {
        if (code >= 0 && enabled)
        {
            KBD k = (KBD)Marshal.PtrToStructure(lp, typeof(KBD));
            if ((k.flags & LLKHF_INJECTED) == 0 && k.vk == trigger)
            {
                int m = wp.ToInt32();
                if (m == WM_KEYDOWN || m == WM_SYSKEYDOWN)
                {
                    if (!down) { down = true; timer.Stop(); Tap(downKey); }
                }
                else if (m == WM_KEYUP || m == WM_SYSKEYUP)
                {
                    if (down) { down = false; timer.Interval = Math.Max(1, delay); timer.Start(); }
                }
            }
        }
        return CallNextHookEx(hook, code, wp, lp); // never block
    }

    static string KeyToIni(byte vk)
    {
        if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9')) return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
        return "0x" + vk.ToString("X2");
    }

    static void SaveConfig()
    {
        string path = Path.ChangeExtension(Application.ExecutablePath, ".ini");
        try
        {
            File.WriteAllText(path, "; AntiGhost ayarlari\r\n; Tus: A-Z, 0-9, F1-F24 veya 0xHEX (sanal tus kodu)\r\n" +
                "trigger=" + KeyToIni(trigger) + "\r\ndown_key=" + KeyToIni(downKey) + "\r\nup_key=" + KeyToIni(upKey) + "\r\ndelay_ms=" + delay + "\r\nlanguage=" + (lang == 1 ? "en" : "tr") + "\r\n");
        }
        catch { }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static bool GetAutostart()
    {
        try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("AntiGhost") != null; }
        catch { return false; }
    }
    static void SetAutostart(bool on)
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (on) k.SetValue("AntiGhost", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("AntiGhost", false);
            }
        }
        catch { }
    }

    static readonly Color BG = ColorTranslator.FromHtml("#202124"), BG2 = ColorTranslator.FromHtml("#303134"),
        FG = ColorTranslator.FromHtml("#E8EAED"), MUTED = ColorTranslator.FromHtml("#9AA0A6"), ACCENT = ColorTranslator.FromHtml("#8AB4F8");

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return BG2; } }
        public override Color MenuItemBorder { get { return BG2; } }
        public override Color ToolStripDropDownBackground { get { return BG; } }
        public override Color ImageMarginGradientBegin { get { return BG; } }
        public override Color ImageMarginGradientMiddle { get { return BG; } }
        public override Color ImageMarginGradientEnd { get { return BG; } }
        public override Color MenuBorder { get { return BG2; } }
        public override Color SeparatorDark { get { return BG2; } }
        public override Color SeparatorLight { get { return BG2; } }
    }

    static Label MakeLabel(string t, int y)
    {
        return new Label { Text = t, Left = 20, Top = y, Width = 260, Height = 20, ForeColor = MUTED, BackColor = BG };
    }

    static NotifyIcon tray;
    static ToolStripMenuItem trayToggle, traySettings, trayExit;
    static CheckBox actBox;
    static Form settingsForm;

    static Button MakeButton(string t, int x, int y, int w, int h, bool accent)
    {
        var b = new Button { Text = t, Left = x, Top = y, Width = w, Height = h, FlatStyle = FlatStyle.Flat, BackColor = accent ? ACCENT : BG2, ForeColor = accent ? BG : FG };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    static void RelabelTray()
    {
        tray.Text = enabled ? T("tip_on") : T("tip_off");
        trayToggle.Text = enabled ? T("active") : T("inactive");
        traySettings.Text = T("settings");
        trayExit.Text = T("exit");
    }

    static void SetEnabled(bool on)
    {
        enabled = on; down = false; timer.Stop();
        if (trayToggle.Checked != on) trayToggle.Checked = on;
        if (actBox != null && !actBox.IsDisposed)
        {
            if (actBox.Checked != on) actBox.Checked = on;
            actBox.Text = on ? T("active") : T("inactive");
        }
        RelabelTray();
    }

    // Counts down on the button so the user can click into OBS's hotkey box, then sends the key.
    static void SendLater(Button b, byte vk, string textKey)
    {
        b.Enabled = false;
        int left = 3;
        b.Text = left + "…";
        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        t.Tick += delegate
        {
            if (--left > 0) { b.Text = left + "…"; return; }
            t.Stop(); t.Dispose();
            Tap(vk);
            if (!b.IsDisposed) { b.Text = T(textKey); b.Enabled = true; }
        };
        t.Start();
    }

    static void ShowSettings()
    {
        if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
        var f = new Form { Text = "AntiGhost", Icon = AppIcon, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen, MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(300, 392), TopMost = true, BackColor = BG, ForeColor = FG, Font = new Font("Segoe UI", 9.5f) };
        settingsForm = f;
        int on = 1;
        f.HandleCreated += delegate { DwmSetWindowAttribute(f.Handle, 20, ref on, 4); };
        byte newKey = trigger;

        var lLang = MakeLabel("", 14);
        var bTr = MakeButton("Türkçe", 20, 38, 126, 28, false);
        var bEn = MakeButton("English", 154, 38, 126, 28, false);

        var lKey = MakeLabel("", 76);
        var kb = new TextBox { Left = 20, Top = 100, Width = 260, ReadOnly = true, Text = ((Keys)trigger).ToString(), BackColor = BG2, ForeColor = FG, BorderStyle = BorderStyle.FixedSingle };
        kb.KeyDown += delegate(object s, KeyEventArgs e)
        {
            e.SuppressKeyPress = true; e.Handled = true;
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.None) return;
            newKey = (byte)e.KeyValue; kb.Text = e.KeyCode.ToString();
        };

        var lDelay = MakeLabel("", 138);
        var tb = new TextBox { Left = 20, Top = 162, Width = 260, Text = delay.ToString(), MaxLength = 3, BackColor = BG2, ForeColor = FG, BorderStyle = BorderStyle.FixedSingle };
        tb.KeyPress += delegate(object s, KeyPressEventArgs e) { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };

        var cb = new CheckBox { Left = 20, Top = 200, Width = 260, Checked = GetAutostart(), ForeColor = FG, BackColor = BG, FlatStyle = FlatStyle.Flat };
        var act = new CheckBox { Left = 20, Top = 226, Width = 260, Checked = enabled, ForeColor = FG, BackColor = BG, FlatStyle = FlatStyle.Flat };
        actBox = act;
        act.CheckedChanged += delegate { SetEnabled(act.Checked); };

        var lObs = MakeLabel("", 256);
        lObs.Height = 34;
        var b13 = MakeButton("", 20, 294, 126, 32, false);
        var b14 = MakeButton("", 154, 294, 126, 32, false);
        b13.Click += delegate { SendLater(b13, downKey, "send13"); };
        b14.Click += delegate { SendLater(b14, upKey, "send14"); };
        var ok = MakeButton("", 20, 338, 260, 36, true);
        ok.Font = new Font("Segoe UI Semibold", 10f);

        Action relabel = delegate
        {
            lLang.Text = T("lang"); lKey.Text = T("trigger"); lDelay.Text = T("delay");
            cb.Text = T("autostart"); act.Text = enabled ? T("active") : T("inactive");
            lObs.Text = T("obs");
            if (b13.Enabled) b13.Text = T("send13");
            if (b14.Enabled) b14.Text = T("send14");
            ok.Text = T("save");
        };
        Action pick = delegate { bTr.BackColor = lang == 0 ? ACCENT : BG2; bTr.ForeColor = lang == 0 ? BG : FG; bEn.BackColor = lang == 1 ? ACCENT : BG2; bEn.ForeColor = lang == 1 ? BG : FG; };
        Action<int> setLang = delegate(int l) { lang = l; pick(); relabel(); RelabelTray(); SaveConfig(); };
        bTr.Click += delegate { setLang(0); };
        bEn.Click += delegate { setLang(1); };
        pick();
        relabel();


        ok.Click += delegate
        {
            int d;
            if (int.TryParse(tb.Text, out d)) delay = Math.Min(d, 999);
            trigger = newKey; down = false; timer.Stop();
            SetAutostart(cb.Checked);
            SaveConfig();
            f.Close();
        };
        f.Controls.AddRange(new Control[] { lLang, bTr, bEn, lKey, kb, lDelay, tb, cb, act, lObs, b13, b14, ok });
        f.ClientSize = new Size(300, 408);
        var made = new LinkLabel { Text = "made by alpha32k", AutoSize = true, LinkColor = MUTED, ActiveLinkColor = ACCENT, VisitedLinkColor = MUTED, LinkBehavior = LinkBehavior.NeverUnderline, BackColor = BG, Font = new Font("Segoe UI", 8.5f), Cursor = Cursors.Hand };
        made.Left = (300 - made.PreferredWidth) / 2; made.Top = 380;
        made.MouseEnter += delegate { made.LinkColor = ACCENT; };
        made.MouseLeave += delegate { made.LinkColor = MUTED; };
        made.LinkClicked += delegate { try { Process.Start("https://github.com/alpha32k"); } catch { } };
        f.Controls.Add(made);
        f.AcceptButton = ok;
        f.Show();
    }

    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (var mtx = new Mutex(true, "AntiGhost_Single", out created))
        {
            if (!created) return;
            LoadConfig();
            Application.EnableVisualStyles();
            timer = new System.Windows.Forms.Timer();
            timer.Tick += delegate { timer.Stop(); if (!down) Tap(upKey); };

            tray = new NotifyIcon { Icon = AppIcon, Visible = true };
            var menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()), BackColor = BG, ForeColor = FG, Font = new Font("Segoe UI", 9.5f) };
            trayToggle = new ToolStripMenuItem { Checked = true, CheckOnClick = true };
            trayToggle.CheckedChanged += delegate { SetEnabled(trayToggle.Checked); };
            traySettings = new ToolStripMenuItem(); traySettings.Click += delegate { ShowSettings(); };
            trayExit = new ToolStripMenuItem(); trayExit.Click += delegate { tray.Visible = false; Application.Exit(); };
            menu.Items.AddRange(new ToolStripItem[] { trayToggle, traySettings, trayExit });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowSettings(); };
            RelabelTray();

            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName), 0);
            if (hook == IntPtr.Zero) { MessageBox.Show("Keyboard hook failed."); return; }
            if (Array.IndexOf(args, "/settings") >= 0) ShowSettings();
            Application.Run();
            UnhookWindowsHookEx(hook);
        }
    }
}
