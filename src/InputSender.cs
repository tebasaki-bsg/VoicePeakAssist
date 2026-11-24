using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace VoicePeakAssist
{
    /// <summary>VOICEPEAK へクリック・ドラッグを送る。</summary>
    internal sealed class InputSender
    {
        readonly Settings st;
        readonly KeyState keys;

        public InputSender(Settings st, KeyState keys) { this.st = st; this.keys = keys; }

        public void Click(VpWindow w, Point p)
        {
            Drag(w, p, p, 0);
        }

        /// <summary>start から end へまっすぐドラッグ（steps = 途中の移動回数）。start==end ならクリック。</summary>
        public void Drag(VpWindow w, Point start, Point end, int steps)
        {
            var path = new List<Point> { start };
            for (int i = 1; i <= steps; i++)
                path.Add(new Point(start.X + (end.X - start.X) * i / (steps + 1), start.Y + (end.Y - start.Y) * i / (steps + 1)));
            if (end != start) path.Add(end);
            DragPath(w, path);
        }

        /// <summary>
        /// 横に dx だけドラッグする。VOICEPEAK は数px以内の動きをクリック扱いにするため、
        /// 先に縦へ detour px 動かしてドラッグと認識させてから横へ動かし、元の高さに戻して離す。
        /// </summary>
        public void DragHorizontal(VpWindow w, Point start, int dx, int detour)
        {
            var path = new List<Point> { start };
            int y2 = start.Y + detour;
            path.Add(new Point(start.X, start.Y + detour / 2));
            path.Add(new Point(start.X, y2));
            int steps = Math.Min(6, Math.Abs(dx) / 10);
            for (int i = 1; i <= steps; i++) path.Add(new Point(start.X + dx * i / (steps + 1), y2));
            path.Add(new Point(start.X + dx, y2));
            path.Add(new Point(start.X + dx, start.Y + detour / 2));
            path.Add(new Point(start.X + dx, start.Y));
            DragPath(w, path);
        }

        /// <summary>path[0] で押し、path の順に動かし、最後の点で離す。</summary>
        public void DragPath(VpWindow w, List<Point> path)
        {
            Log.Debug(string.Format("送信 {0}: {1}", st.Input, string.Join(" ", path.Select(p => "(" + p.X + "," + p.Y + ")"))));
            if (st.Input == InputMethod.Message) DragByMessage(w, path);
            else DragByRealMouse(path);
        }

        /// <summary>
        /// 複数の点をクリックする（イントネーション用）。メッセージ方式では「押す・離す」をまとめて VOICEPEAK の受付列に
        /// 先に積むので、押してから離すまでの間に実際のカーソル位置の信号が割り込んでドラッグ扱いになるのを防げる。
        /// 呼ぶ側で BeginModifierBlock しておくこと（手で押している修飾キーを外すため）。
        /// </summary>
        public void ClickMany(VpWindow w, List<Point> points)
        {
            if (points.Count == 0) return;
            if (st.Input != InputMethod.Message)
            {
                foreach (var p in points) Click(w, p);
                return;
            }
            IntPtr target = TargetWindow(w, points[0]);
            foreach (var p in points)
            {
                var c = ToClient(target, w, p);
                var lp = Native.MakeLParam(c.X, c.Y);
                Native.PostMessage(target, Native.WM_MOUSEMOVE, IntPtr.Zero, lp);
                Native.PostMessage(target, Native.WM_LBUTTONDOWN, new IntPtr(Native.MK_LBUTTON), lp);
                Native.PostMessage(target, Native.WM_LBUTTONUP, IntPtr.Zero, lp);
            }
            Native.POINT cur;
            if (Native.GetCursorPos(out cur))
            {
                var c = ToClient(target, w, new Point(cur.X, cur.Y));
                Native.PostMessage(target, Native.WM_MOUSEMOVE, IntPtr.Zero, Native.MakeLParam(c.X, c.Y));
            }
            Log.Debug("クリック（まとめて送信）: " + string.Join(" ", points.Select(p => "(" + p.X + "," + p.Y + ")")));
            Thread.Sleep(20 + 4 * points.Count); // 処理されるのを待つ
        }

        /// <summary>ボタンを押さずにカーソルが p にあると VOICEPEAK に伝える（メッセージ方式のときだけ）。</summary>
        public void Hover(VpWindow w, Point p)
        {
            if (st.Input != InputMethod.Message) return;
            IntPtr target = TargetWindow(w, p);
            Send(target, Native.WM_MOUSEMOVE, 0, ToClient(target, w, p));
        }

        /// <summary>実際のカーソル位置にホバー表示を戻す。</summary>
        public void RestoreHover(VpWindow w)
        {
            Native.POINT cur;
            if (Native.GetCursorPos(out cur)) Hover(w, new Point(cur.X, cur.Y));
        }

        // ================= メッセージ方式（カーソルを動かさない） =================

        static readonly int[] ModifierVks = {
            Native.VK_SHIFT, Native.VK_LSHIFT, Native.VK_RSHIFT,
            Native.VK_CONTROL, Native.VK_LCONTROL, Native.VK_RCONTROL,
            Native.VK_MENU, Native.VK_LMENU, Native.VK_RMENU
        };

        bool queueReady;

        /// <summary>action の間だけ、VOICEPEAK から Shift/Ctrl/Alt が離されているように見せる。</summary>
        void WithNeutralKeys(VpWindow w, Action action)
        {
            if (!queueReady)
            {
                // AttachThreadInput にはこのスレッドのメッセージキューが必要
                Native.MSG m; Native.PeekMessage(out m, IntPtr.Zero, 0, 0, 0);
                queueReady = true;
            }
            uint myTid = Native.GetCurrentThreadId();
            bool attached = false;
            byte[] saved = null;
            try
            {
                if (st.NeutralizeModifiers && AnyModifierDown())
                {
                    attached = Native.AttachThreadInput(myTid, w.ThreadId, true);
                    if (attached)
                    {
                        saved = new byte[256];
                        if (Native.GetKeyboardState(saved))
                        {
                            var mod = (byte[])saved.Clone();
                            foreach (int vk in ModifierVks) mod[vk] = (byte)(mod[vk] & 0x7F);
                            Native.SetKeyboardState(mod);
                        }
                        else saved = null;
                    }
                    else Log.Debug("AttachThreadInput に失敗しました");
                }
                action();
            }
            finally
            {
                if (saved != null) Native.SetKeyboardState(saved);
                if (attached) Native.AttachThreadInput(myTid, w.ThreadId, false);
            }
        }

        void DragByMessage(VpWindow w, List<Point> path)
        {
            IntPtr target = TargetWindow(w, path[0]);
            WithNeutralKeys(w, () =>
            {
                var pts = path.Select(p => ToClient(target, w, p)).ToList();
                Send(target, Native.WM_MOUSEMOVE, 0, pts[0]);
                Send(target, Native.WM_LBUTTONDOWN, Native.MK_LBUTTON, pts[0]);
                for (int i = 1; i < pts.Count; i++)
                    Send(target, Native.WM_MOUSEMOVE, Native.MK_LBUTTON, pts[i]);
                Send(target, Native.WM_LBUTTONUP, 0, pts[pts.Count - 1]);
                // ホバー表示を実際のカーソル位置に戻す
                Native.POINT cur;
                if (Native.GetCursorPos(out cur))
                    Send(target, Native.WM_MOUSEMOVE, 0, ToClient(target, w, new Point(cur.X, cur.Y)));
            });
        }

        // ================= 文字の入力 =================

        /// <summary>VOICEPEAK に文字列を打ち込み、enter なら Enter で確定する。</summary>
        public void TypeText(VpWindow w, string text, bool enter)
        {
            ReplaceText(text, enter);
        }

        /// <summary>
        /// 開いている入力欄の中身を text に置き換える：Ctrl+A → BackSpace → text → Enter。
        /// すべて本物のキーボード入力として1回ずつ送る（押している Shift/Ctrl はその間だけ離す）。
        /// 文字は KEYEVENTF_UNICODE で送るので、日本語入力がオンでも全角にならない。
        /// </summary>
        public void ReplaceText(string text, bool enter)
        {
            Log.Debug("入力: " + text + (enter ? "⏎" : ""));
            int gap = Math.Max(5, (int)st.KeyGapMs);
            var released = ReleaseModifiers();
            try
            {
                Thread.Sleep(gap);
                // Ctrl+A（全選択）
                SendInputs(new List<Native.INPUT> { Key(Native.VK_LCONTROL, false), Key('A', false), Key('A', true), Key(Native.VK_LCONTROL, true) });
                Thread.Sleep(gap);
                // BackSpace（削除）
                SendInputs(new List<Native.INPUT> { Key(0x08, false), Key(0x08, true) });
                Thread.Sleep(gap);
                foreach (char c in text)
                {
                    SendInputs(new List<Native.INPUT> { Unicode(c, false), Unicode(c, true) });
                    Thread.Sleep(gap);
                }
                if (enter)
                {
                    int vk = string.Equals(st.ConfirmWith, "Esc", StringComparison.OrdinalIgnoreCase) ? 0x1B : 0x0D;
                    SendInputs(new List<Native.INPUT> { Key(vk, false), Key(vk, true) });
                    Thread.Sleep(gap);
                }
            }
            finally { RepressModifiers(released); }
        }

        /// <summary>本物のキーボード入力として Enter を押す（押している Shift/Ctrl はその間だけ離す）。</summary>
        public void PressEnter()
        {
            var released = ReleaseModifiers();
            try
            {
                SendInputs(new List<Native.INPUT> { Key(0x0D, false), Key(0x0D, true) });
                Thread.Sleep(30);
            }
            finally { RepressModifiers(released); }
        }

        /// <summary>修飾キーを外した普通のホイールとして送る（横スクロールの割り当て用）。</summary>
        public void ScrollWithoutModifiers(int delta)
        {
            BeginModifierBlock();
            try
            {
                var inp = new Native.INPUT { type = Native.INPUT_MOUSE };
                inp.U.mi.mouseData = unchecked((uint)delta);
                inp.U.mi.dwFlags = 0x0800; // MOUSEEVENTF_WHEEL
                inp.U.mi.dwExtraInfo = Native.OwnExtraInfo;
                SendInputs(new List<Native.INPUT> { inp });
                Thread.Sleep(15);
            }
            finally { EndModifierBlock(); }
        }

        static int VkOf(char c)
        {
            if (c >= '0' && c <= '9') return c;
            if (c == '-') return 0xBD; // VK_OEM_MINUS
            if (c == '+') return 0xBB; // VK_OEM_PLUS
            if (c == '.') return 0xBE; // VK_OEM_PERIOD
            return 0;
        }

        static void SendKey(IntPtr hwnd, int vk, char c)
        {
            IntPtr r;
            uint scan = vk != 0 ? Native.MapVirtualKey((uint)vk, Native.MAPVK_VK_TO_VSC) : 0;
            int down = 1 | (int)(scan << 16);
            int up = unchecked((int)(0xC0000001 | (scan << 16)));
            if (vk != 0) Native.SendMessageTimeout(hwnd, Native.WM_KEYDOWN, new IntPtr(vk), new IntPtr(down), Native.SMTO_ABORTIFHUNG, 500, out r);
            Native.SendMessageTimeout(hwnd, 0x0102 /*WM_CHAR*/, new IntPtr(c), new IntPtr(down), Native.SMTO_ABORTIFHUNG, 500, out r);
            if (vk != 0) Native.SendMessageTimeout(hwnd, Native.WM_KEYUP, new IntPtr(vk), new IntPtr(up), Native.SMTO_ABORTIFHUNG, 500, out r);
        }

        static Native.INPUT Unicode(char c, bool up)
        {
            var inp = new Native.INPUT { type = Native.INPUT_KEYBOARD };
            inp.U.ki.wVk = 0;
            inp.U.ki.wScan = c;
            inp.U.ki.dwFlags = 0x0004 /*KEYEVENTF_UNICODE*/ | (up ? Native.KEYEVENTF_KEYUP : 0);
            inp.U.ki.dwExtraInfo = Native.OwnExtraInfo;
            return inp;
        }

        /// <summary>
        /// true の間、キーボードフックが手で押している Shift/Ctrl/Alt の信号（押しっぱなしの連続信号を含む）を止める。
        /// 押しっぱなしだと「押した」信号が繰り返し届き、送った Enter が Shift+Enter や Ctrl+Esc になってしまうため。
        /// </summary>
        public static volatile bool BlockModifiers;
        List<int> blockReleased;

        /// <summary>ここから EndModifierBlock までの間、Shift/Ctrl を無効にする。</summary>
        public void BeginModifierBlock()
        {
            if (BlockModifiers) return;
            BlockModifiers = true;
            Thread.Sleep(5);
            blockReleased = ReleaseModifiersRaw();
            Thread.Sleep(10);
        }

        public void EndModifierBlock()
        {
            if (!BlockModifiers) return;
            Thread.Sleep(10);
            var r = blockReleased; blockReleased = null;
            BlockModifiers = false;
            if (r != null) RepressModifiers(r);
        }

        List<int> ReleaseModifiers()
        {
            if (BlockModifiers) return new List<int>(); // すでに無効化中
            return ReleaseModifiersRaw();
        }

        List<int> ReleaseModifiersRaw()
        {
            var released = new List<int>();
            if (!st.NeutralizeModifiers && !BlockModifiers) return released;
            foreach (int vk in new[] { Native.VK_LSHIFT, Native.VK_RSHIFT, Native.VK_LCONTROL, Native.VK_RCONTROL, Native.VK_LMENU, Native.VK_RMENU })
                if (Native.KeyDown(vk)) released.Add(vk);
            if (released.Count > 0) SendInputs(released.Select(vk => Key(vk, true)).ToList());
            return released;
        }

        void RepressModifiers(List<int> released)
        {
            var downs = released.Where(vk => keys.PhysicallyDown(vk)).Select(vk => Key(vk, false)).ToList();
            if (downs.Count > 0) SendInputs(downs);
        }

        static bool AnyModifierDown()
        {
            return Native.KeyDown(Native.VK_SHIFT) || Native.KeyDown(Native.VK_CONTROL) || Native.KeyDown(Native.VK_MENU);
        }

        static IntPtr TargetWindow(VpWindow w, Point p)
        {
            IntPtr h = Native.WindowFromPoint(new Native.POINT(p.X, p.Y));
            if (h != IntPtr.Zero)
            {
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (pid == w.Pid) return h;
            }
            return w.Hwnd;
        }

        static Point ToClient(IntPtr target, VpWindow w, Point screen)
        {
            var p = new Native.POINT(screen.X, screen.Y);
            Native.ScreenToClient(target, ref p);
            if (w.Awareness == 0 && Math.Abs(w.Scale - 1.0) > 0.01)
            {
                // DPI 非対応アプリへ送る座標は論理座標
                var o = new Native.POINT(0, 0);
                Native.ClientToScreen(target, ref o);
                p.X = (int)Math.Round((screen.X - o.X) / w.Scale);
                p.Y = (int)Math.Round((screen.Y - o.Y) / w.Scale);
            }
            return new Point(p.X, p.Y);
        }

        static void Send(IntPtr hwnd, int msg, int wParam, Point clientPt)
        {
            IntPtr result;
            Native.SendMessageTimeout(hwnd, (uint)msg, new IntPtr(wParam), Native.MakeLParam(clientPt.X, clientPt.Y),
                Native.SMTO_ABORTIFHUNG | Native.SMTO_NORMAL, 500, out result);
        }

        // ================= 実際のマウス操作（一瞬カーソルが動く） =================

        void DragByRealMouse(List<Point> path)
        {
            Native.POINT saved;
            Native.GetCursorPos(out saved);
            var released = ReleaseModifiers();
            try
            {
                SendInputs(new List<Native.INPUT> { MouseAbs(path[0], Native.MOUSEEVENTF_MOVE) });
                Thread.Sleep(8);
                SendInputs(new List<Native.INPUT> { MouseAbs(path[0], Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_LEFTDOWN) });
                Thread.Sleep(8);
                for (int i = 1; i < path.Count; i++)
                {
                    SendInputs(new List<Native.INPUT> { MouseAbs(path[i], Native.MOUSEEVENTF_MOVE) });
                    Thread.Sleep(5);
                }
                SendInputs(new List<Native.INPUT> { MouseAbs(path[path.Count - 1], Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_LEFTUP) });
                Thread.Sleep(10);
                SendInputs(new List<Native.INPUT> { MouseAbs(new Point(saved.X, saved.Y), Native.MOUSEEVENTF_MOVE) });
            }
            finally { RepressModifiers(released); }
        }

        static Native.INPUT MouseAbs(Point p, uint flags)
        {
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN) - 1);
            int vh = Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN) - 1);
            var inp = new Native.INPUT { type = Native.INPUT_MOUSE };
            inp.U.mi.dx = (int)Math.Round((p.X - vx) * 65535.0 / vw);
            inp.U.mi.dy = (int)Math.Round((p.Y - vy) * 65535.0 / vh);
            inp.U.mi.dwFlags = flags | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
            inp.U.mi.dwExtraInfo = Native.OwnExtraInfo;
            return inp;
        }

        static Native.INPUT Key(int vk, bool up)
        {
            var inp = new Native.INPUT { type = Native.INPUT_KEYBOARD };
            inp.U.ki.wVk = (ushort)vk;
            inp.U.ki.wScan = (ushort)Native.MapVirtualKey((uint)vk, Native.MAPVK_VK_TO_VSC);
            uint fl = up ? Native.KEYEVENTF_KEYUP : 0;
            if (vk == Native.VK_RCONTROL || vk == Native.VK_RMENU) fl |= Native.KEYEVENTF_EXTENDEDKEY;
            inp.U.ki.dwFlags = fl;
            inp.U.ki.dwExtraInfo = Native.OwnExtraInfo;
            return inp;
        }

        static void SendInputs(List<Native.INPUT> list)
        {
            var arr = list.ToArray();
            Native.SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(Native.INPUT)));
        }
    }

    /// <summary>物理的に押されているキー（自分で送った入力を除く）を記録する。</summary>
    internal sealed class KeyState
    {
        readonly bool[] down = new bool[256];
        public void Set(int vk, bool isDown) { if (vk >= 0 && vk < 256) down[vk] = isDown; }
        public bool PhysicallyDown(int vk) { return vk >= 0 && vk < 256 && down[vk]; }
        public bool Shift { get { return down[Native.VK_LSHIFT] || down[Native.VK_RSHIFT] || down[Native.VK_SHIFT]; } }
        public bool Ctrl { get { return down[Native.VK_LCONTROL] || down[Native.VK_RCONTROL] || down[Native.VK_CONTROL]; } }
        public bool Alt { get { return down[Native.VK_LMENU] || down[Native.VK_RMENU] || down[Native.VK_MENU]; } }
        public bool Win { get { return down[Native.VK_LWIN] || down[Native.VK_RWIN]; } }
    }
}
