using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VoicePeakAssist
{
    /// <summary>フックが参照する、前面の VOICEPEAK の最新状態。</summary>
    internal sealed class Snapshot
    {
        public IntPtr Hwnd;
        public Rectangle Content;
        public Rectangle Panel;
        public PanelMode Mode;
        public DateTime Time;
        public Rectangle[] Hit; // 長さ表示：ホイールが効く範囲（+50%のボックスの幅 × カタカナの欄〜ボックスの下端）

        public bool Accepts(Point p)
        {
            if (!Content.Contains(p)) return false;
            if (Mode != PanelMode.Length) return true;
            if (Hit == null) return false;
            foreach (var r in Hit) if (r.Contains(p)) return true;
            return false;
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        readonly Settings st;
        readonly KeyState keys = new KeyState();
        readonly InputSender input;
        readonly Worker worker;
        readonly NotifyIcon tray;
        readonly Control invoker;
        readonly System.Threading.Timer poller;
        volatile Snapshot snap;
        volatile bool panelActive;
        DateTime lastFullHit = DateTime.MinValue;
        int swallowVk = -1; // 止めたキー（離したときの信号も止める）

        IntPtr mouseHook = IntPtr.Zero, keyHook = IntPtr.Zero;
        readonly Native.LowLevelProc mouseProc, keyProc; // GC されないよう保持

        ToolStripMenuItem miEnabled, miInvert, miRequireClick, miNeutral, miMsg, miReal;

        public TrayApp()
        {
            st = Settings.Load();
            Log.Verbose = st.DebugLog;
            Log.Level = st.LogLevel;
            Log.Startup("起動 v" + Assembly.GetExecutingAssembly().GetName().Version + " / " + Environment.OSVersion);

            invoker = new Control();
            invoker.CreateControl();
            var h = invoker.Handle; // ハンドルを作っておく

            input = new InputSender(st, keys);
            worker = new Worker(st, input, Notify);
            Diagnostics.Notify = Notify;

            tray = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "VoicePeak 発音調整アシスト",
                Visible = true,
                ContextMenuStrip = BuildMenu()
            };

            tray.DoubleClick += (s, e) => ShowSettings();

            mouseProc = MouseHook;
            keyProc = KeyHook;
            IntPtr mod = Native.GetModuleHandle(null);
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
            keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, mod, 0);
            if (mouseHook == IntPtr.Zero || keyHook == IntPtr.Zero)
                Log.Write("フックの登録に失敗しました: " + Marshal.GetLastWin32Error());

            poller = new System.Threading.Timer(Poll, null, 200, 120);
            ShowBalloon("起動しました。トレイアイコンをダブルクリックすると設定画面が開きます。");
        }

        static Icon LoadIcon()
        {
            try
            {
                var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
                if (s != null) return new Icon(s, SystemInformation.SmallIconSize);
            }
            catch { }
            return SystemIcons.Application;
        }

        // ================= メニュー =================

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            miEnabled = new ToolStripMenuItem("有効", null, (s, e) => { st.Enabled = !st.Enabled; st.Save(); SyncMenu(); });
            miInvert = new ToolStripMenuItem("ホイールの向きを反転", null, (s, e) => { st.InvertWheel = !st.InvertWheel; st.Save(); SyncMenu(); });
            miRequireClick = new ToolStripMenuItem("パネルをクリックしてから有効にする", null, (s, e) => { st.RequirePanelClick = !st.RequirePanelClick; st.Save(); SyncMenu(); });
            var miInput = new ToolStripMenuItem("入力の送り方");
            miMsg = new ToolStripMenuItem("カーソルを動かさない（標準）", null, (s, e) => { st.Input = InputMethod.Message; st.Save(); SyncMenu(); });
            miReal = new ToolStripMenuItem("実際のマウス操作（一瞬カーソルが動く）", null, (s, e) => { st.Input = InputMethod.RealMouse; st.Save(); SyncMenu(); });
            miInput.DropDownItems.Add(miMsg);
            miInput.DropDownItems.Add(miReal);
            miNeutral = new ToolStripMenuItem("送信中は Shift/Ctrl を離したことにする", null, (s, e) => { st.NeutralizeModifiers = !st.NeutralizeModifiers; st.Save(); SyncMenu(); });

            var miSettings = new ToolStripMenuItem("設定...", null, (s, e) => ShowSettings());
            miSettings.Font = new Font(miSettings.Font, FontStyle.Bold);
            m.Items.Add(miSettings);
            m.Items.Add(miEnabled);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(miInvert);
            m.Items.Add(miRequireClick);
            m.Items.Add(miInput);
            m.Items.Add(miNeutral);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("診断画像を保存（3秒以内に VOICEPEAK を前面に）", null, (s, e) => worker.Enqueue(new DiagJob())));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("ログを開く", null, (s, e) => OpenFile(Log.FilePath)));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("終了", null, (s, e) => ExitThread()));
            m.Opening += (s, e) => SyncMenu();
            SyncMenu();
            return m;
        }

        void SyncMenu()
        {
            miEnabled.Checked = st.Enabled;
            miInvert.Checked = st.InvertWheel;
            miRequireClick.Checked = st.RequirePanelClick;
            miNeutral.Checked = st.NeutralizeModifiers;
            miMsg.Checked = st.Input == InputMethod.Message;
            miReal.Checked = st.Input == InputMethod.RealMouse;
            if (tray != null) tray.Text = st.Enabled ? "VoicePeak 発音調整アシスト" : "VoicePeak 発音調整アシスト（無効）";
        }

        SettingsForm settingsForm;

        void ShowSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
            settingsForm = new SettingsForm(st, () => { Log.Verbose = st.DebugLog; Log.Level = st.LogLevel; SyncMenu(); });
            settingsForm.Show();
            settingsForm.Activate();
        }

        void ReloadSettings()
        {
            var n = Settings.Load();
            foreach (var f in typeof(Settings).GetFields())
                if (!f.IsStatic && !f.IsInitOnly && !f.IsLiteral) f.SetValue(st, f.GetValue(n));
            Log.Verbose = st.DebugLog;
            Log.Level = st.LogLevel;
            SyncMenu();
            Notify("設定を読み込み直しました。", false);
        }

        static void OpenFile(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) System.IO.File.WriteAllText(path, "");
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch (Exception ex) { Log.Write("ファイルを開けません: " + ex.Message); }
        }

        /// <summary>起動時以外は通知を出さず、ログにだけ記録する。</summary>
        void Notify(string text, bool warn)
        {
            Log.Write((warn ? "[注意] " : "") + text);
        }

        void ShowBalloon(string text)
        {
            Log.Startup(text);
            try
            {
                invoker.BeginInvoke((Action)(() =>
                    tray.ShowBalloonTip(3000, "VoicePeak 発音調整アシスト", text, ToolTipIcon.Info)));
            }
            catch { }
        }

        // ================= 状態の監視 =================

        void Poll(object _)
        {
            try
            {
                var w = VpWindow.FromForeground(st);
                if (w == null) { snap = null; return; }
                var pn = Panel.Detect(w, st);
                if (pn == null) { snap = null; return; }
                Rectangle[] hit = null;
                if (pn.Mode == PanelMode.Length)
                {
                    var f = pn.CaptureContent();
                    if (f != null)
                    {
                        var boxes = Detect.Boxes(f, pn);
                        double half = st.LengthHitWidth / 2 * pn.S;
                        // 縦はカタカナ（発音）の欄からボックスの下端まで
                        int top = pn.Yi(st.HitTopY), bottom = pn.Yi(st.BoxBottomY);
                        hit = boxes.Select(b => Rectangle.FromLTRB((int)Math.Floor(b.Center - half), top, (int)Math.Ceiling(b.Center + half) + 1, bottom + 1)).ToArray();
                        // 一時的に見えなかった列（カーソルが乗って見た目が変わった等）は、直前の範囲を3秒間使う
                        var prev = snap;
                        Native.POINT cp; Native.GetCursorPos(out cp);
                        var cursor = new Point(cp.X, cp.Y);
                        bool recent = (DateTime.Now - lastFullHit).TotalSeconds < 3;
                        if (prev != null && prev.Hit != null && prev.Hwnd == w.Hwnd)
                        {
                            var merged = hit.ToList();
                            foreach (var r in prev.Hit)
                                if (!merged.Any(m => m.IntersectsWith(r)) && (recent || r.Contains(cursor))) merged.Add(r);
                            if (merged.Count == hit.Length) lastFullHit = DateTime.Now;
                            hit = merged.ToArray();
                        }
                        else lastFullHit = DateTime.Now;
                    }
                }
                snap = new Snapshot
                {
                    Hwnd = w.Hwnd,
                    Content = pn.ContentRect,
                    Panel = Rectangle.FromLTRB(w.Client.Left, pn.Top, w.Client.Right, w.Client.Bottom),
                    Mode = pn.Mode,
                    Time = DateTime.Now,
                    Hit = hit
                };
            }
            catch (Exception ex) { Log.Debug("監視エラー: " + ex.Message); }
        }

        bool ForegroundIs(IntPtr hwnd)
        {
            IntPtr fg = Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOT);
            return fg == hwnd;
        }

        // ================= フック =================

        IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    int msg = wParam.ToInt32();
                    var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    bool own = info.dwExtraInfo == Native.OwnExtraInfo;
                    var pt = new Point(info.pt.X, info.pt.Y);

                    if (!own && (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN))
                    {
                        var s = snap;
                        IntPtr root = Native.GetAncestor(Native.WindowFromPoint(info.pt), Native.GA_ROOT);
                        panelActive = s != null && root == s.Hwnd && s.Panel.Contains(pt);
                    }
                    else if (msg == Native.WM_MOUSEWHEEL && !own && st.Enabled)
                    {
                        bool ctrl = keys.Ctrl || Native.KeyDown(Native.VK_CONTROL);
                        bool shift = keys.Shift || Native.KeyDown(Native.VK_SHIFT);
                        bool alt = keys.Alt || Native.KeyDown(Native.VK_MENU);
                        int mods = (ctrl ? 1 : 0) + (shift ? 1 : 0) + (alt ? 1 : 0);
                        if (!keys.Win && mods <= 1)
                        {
                            string action = ctrl ? st.WheelCtrl : shift ? st.WheelShift : alt ? st.WheelAlt : st.WheelPlain;
                            var s = snap;
                            bool live = s != null && (DateTime.Now - s.Time).TotalMilliseconds < 1500 && ForegroundIs(s.Hwnd);
                            short delta = (short)((info.mouseData >> 16) & 0xFFFF);
                            if (action == Settings.Scroll)
                            {
                                // 修飾キー付きのホイールに横スクロールが割り当てられている：普通のホイールとして送り直す
                                if (mods == 1 && live && s.Content.Contains(pt))
                                {
                                    worker.Enqueue(new ScrollJob { Delta = delta });
                                    return new IntPtr(1);
                                }
                            }
                            else if (live && (s.Mode == PanelMode.Intonation || s.Mode == PanelMode.Length) &&
                                     s.Accepts(pt) &&
                                     (!st.RequirePanelClick || panelActive))
                            {
                                worker.Enqueue(new WheelJob { Pt = pt, Notches = delta / 120.0, Fine = action == Settings.Small, Word = action == Settings.Word });
                                return new IntPtr(1); // VOICEPEAK には渡さない
                            }
                        }
                    }
                }
                catch (Exception ex) { Log.Debug("マウスフック: " + ex.Message); }
            }
            return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        IntPtr KeyHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    int msg = wParam.ToInt32();
                    var info = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    bool own = info.dwExtraInfo == Native.OwnExtraInfo;
                    bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                    int vk = (int)info.vkCode;
                    if (!own)
                    {
                        keys.Set(vk, down);
                        if (vk == Native.VK_LSHIFT || vk == Native.VK_RSHIFT) keys.Set(Native.VK_SHIFT, down || keys.PhysicallyDown(vk == Native.VK_LSHIFT ? Native.VK_RSHIFT : Native.VK_LSHIFT));
                        if (vk == Native.VK_LCONTROL || vk == Native.VK_RCONTROL) keys.Set(Native.VK_CONTROL, down || keys.PhysicallyDown(vk == Native.VK_LCONTROL ? Native.VK_RCONTROL : Native.VK_LCONTROL));
                        if (vk == Native.VK_LMENU || vk == Native.VK_RMENU) keys.Set(Native.VK_MENU, down || keys.PhysicallyDown(vk == Native.VK_LMENU ? Native.VK_RMENU : Native.VK_LMENU));

                        // 数値を操作している間は、手で押している Shift/Ctrl の信号を止める
                        if (InputSender.BlockModifiers && (vk == Native.VK_LSHIFT || vk == Native.VK_RSHIFT || vk == Native.VK_SHIFT ||
                            vk == Native.VK_LCONTROL || vk == Native.VK_RCONTROL || vk == Native.VK_CONTROL ||
                            vk == Native.VK_LMENU || vk == Native.VK_RMENU || vk == Native.VK_MENU))
                            return new IntPtr(1);

                        if (down)
                        {
                            // 押しっぱなしの繰り返しは、最初の1回だけ処理して残りは止める
                            if (swallowVk == vk) return new IntPtr(1);
                            if (st.Enabled && !keys.Win && !KeyCombo.IsModifierKey((Keys)vk))
                            {
                                bool ctrl = keys.Ctrl, shift = keys.Shift, alt = keys.Alt;
                                if (st.BulkHotkeyEnabled && KeyCombo.Parse(st.BulkHotkey).Matches(vk, ctrl, shift, alt) && IsVoicePeakForeground())
                                {
                                    if (!worker.BulkRunning) worker.Enqueue(new BulkJob());
                                    swallowVk = vk;
                                    return new IntPtr(1);
                                }
                                bool isMax = KeyCombo.Parse(st.MaxKey).Matches(vk, ctrl, shift, alt);
                                bool isMin = !isMax && KeyCombo.Parse(st.MinKey).Matches(vk, ctrl, shift, alt);
                                if (isMax || isMin)
                                {
                                    // カーソルが発音調整パネルの文字の上にあるときだけ反応する
                                    var sn = snap;
                                    Native.POINT cp; Native.GetCursorPos(out cp);
                                    var cur = new Point(cp.X, cp.Y);
                                    if (sn != null && (DateTime.Now - sn.Time).TotalMilliseconds < 1500 && ForegroundIs(sn.Hwnd) &&
                                        (sn.Mode == PanelMode.Intonation || sn.Mode == PanelMode.Length) && sn.Accepts(cur))
                                    {
                                        worker.Enqueue(new ExtremeJob { Pt = cur, Max = isMax });
                                        swallowVk = vk;
                                        return new IntPtr(1);
                                    }
                                }
                            }
                        }
                        else if (swallowVk == vk)
                        {
                            swallowVk = -1;
                            return new IntPtr(1);
                        }
                    }
                }
                catch (Exception ex) { Log.Debug("キーフック: " + ex.Message); }
            }
            return Native.CallNextHookEx(keyHook, nCode, wParam, lParam);
        }

        bool IsVoicePeakForeground()
        {
            IntPtr fg = Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOT);
            if (fg == IntPtr.Zero) return false;
            uint pid; Native.GetWindowThreadProcessId(fg, out pid);
            return VpWindow.IsVoicePeakProcess(pid, fg, st);
        }

        protected override void ExitThreadCore()
        {
            try { poller.Dispose(); } catch { }
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
            worker.Stop();
            tray.Visible = false;
            tray.Dispose();
            Log.Write("終了");
            base.ExitThreadCore();
        }
    }

    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { try { Native.SetProcessDPIAware(); } catch { } }

            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("予期しないエラー: " + e.ExceptionObject);
            Application.ThreadException += (s, e) => Log.Write("予期しないエラー: " + e.Exception);

            bool created;
            using (var mtx = new Mutex(true, "VoicePeakAssist_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("VoicePeak 発音調整アシストはすでに起動しています（タスクトレイを確認してください）。",
                        "VoicePeak 発音調整アシスト", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }
}
