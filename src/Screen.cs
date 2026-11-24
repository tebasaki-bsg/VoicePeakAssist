using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VoicePeakAssist
{
    /// <summary>画面の一部を取り込んだ画像（座標はスクリーン座標）。</summary>
    internal sealed class Frame
    {
        public readonly int X0, Y0, W, H;
        public readonly byte[] Px; // BGRA
        public Frame(int x0, int y0, int w, int h, byte[] px) { X0 = x0; Y0 = y0; W = w; H = h; Px = px; }

        /// <summary>テスト用：設定されていれば画面の代わりにこの画像から切り出す。</summary>
        public static Frame TestSource;

        public static Frame Capture(Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return null;
            if (TestSource != null) return TestSource.Crop(r);
            using (var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
                }
                return FromBitmap(bmp, r.Left, r.Top);
            }
        }

        public static Frame FromBitmap(Bitmap bmp, int x0, int y0)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var px = new byte[bmp.Width * bmp.Height * 4];
                for (int y = 0; y < bmp.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, px, y * bmp.Width * 4, bmp.Width * 4);
                return new Frame(x0, y0, bmp.Width, bmp.Height, px);
            }
            finally { bmp.UnlockBits(data); }
        }

        public Bitmap ToBitmap()
        {
            var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < H; y++)
                Marshal.Copy(Px, y * W * 4, data.Scan0 + y * data.Stride, W * 4);
            bmp.UnlockBits(data);
            return bmp;
        }

        public Frame Crop(Rectangle r)
        {
            var px = new byte[r.Width * r.Height * 4];
            for (int y = 0; y < r.Height; y++)
                for (int x = 0; x < r.Width; x++)
                {
                    int sx = r.Left + x - X0, sy = r.Top + y - Y0;
                    int di = (y * r.Width + x) * 4;
                    if (sx < 0 || sy < 0 || sx >= W || sy >= H) { px[di] = px[di + 1] = px[di + 2] = 0; px[di + 3] = 255; continue; }
                    Array.Copy(Px, (sy * W + sx) * 4, px, di, 4);
                }
            return new Frame(r.Left, r.Top, r.Width, r.Height, px);
        }

        public bool Contains(int x, int y) { return x >= X0 && y >= Y0 && x < X0 + W && y < Y0 + H; }

        public void Get(int x, int y, out int r, out int g, out int b)
        {
            if (!Contains(x, y)) { r = g = b = -1; return; }
            int i = ((y - Y0) * W + (x - X0)) * 4;
            b = Px[i]; g = Px[i + 1]; r = Px[i + 2];
        }

        public int Gray(int x, int y)
        {
            int r, g, b; Get(x, y, out r, out g, out b);
            if (r < 0) return -1;
            return (r * 30 + g * 59 + b * 11) / 100;
        }

        /// <summary>ほぼ白（ボックスの中・ブロックの背景）</summary>
        public bool IsWhite(int x, int y)
        {
            int r, g, b; Get(x, y, out r, out g, out b);
            return r >= 250 && g >= 250 && b >= 250;
        }

        /// <summary>パネルの地の灰色（ブロックの外）</summary>
        public bool IsPanelBg(int x, int y)
        {
            int r, g, b; Get(x, y, out r, out g, out b);
            if (r < 0) return false;
            int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            return mx - mn <= 4 && mn >= 236 && mx <= 249;
        }
    }

    internal enum PanelMode { Unknown, Accent, Intonation, Length }

    /// <summary>前面にある VOICEPEAK のウィンドウ。</summary>
    internal sealed class VpWindow
    {
        public IntPtr Hwnd;
        public uint Pid;
        public uint ThreadId;
        public Rectangle Client;   // スクリーン座標（物理ピクセル）
        public double Scale;       // 表示スケール（1.0 = 100%）
        public int Awareness;      // 0=非対応 1=システム 2=モニター毎
        public string ClassName;

        static readonly Dictionary<uint, bool> PidCache = new Dictionary<uint, bool>();

        public static VpWindow FromForeground(Settings st)
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            return FromHandle(h, st);
        }

        public static bool IsVoicePeakProcess(uint pid, IntPtr hwnd, Settings st)
        {
            bool ok;
            lock (PidCache)
            {
                if (PidCache.TryGetValue(pid, out ok)) return ok;
            }
            ok = false;
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                    ok = p.ProcessName.IndexOf(st.ProcessName, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { }
            if (!ok)
            {
                // プロセス名が違う場合に備えてタイトルでも判定
                string t = Native.GetText(hwnd);
                ok = t.StartsWith(st.WindowTitlePrefix, StringComparison.OrdinalIgnoreCase);
            }
            lock (PidCache)
            {
                if (PidCache.Count > 200) PidCache.Clear();
                PidCache[pid] = ok;
            }
            return ok;
        }

        public static VpWindow FromHandle(IntPtr h, Settings st)
        {
            h = Native.GetAncestor(h, Native.GA_ROOT);
            if (h == IntPtr.Zero || Native.IsIconic(h)) return null;
            uint pid;
            uint tid = Native.GetWindowThreadProcessId(h, out pid);
            if (!IsVoicePeakProcess(pid, h, st)) return null;

            var w = new VpWindow { Hwnd = h, Pid = pid, ThreadId = tid };
            Native.RECT rc;
            if (!Native.GetClientRect(h, out rc)) return null;
            var p = new Native.POINT(0, 0);
            Native.ClientToScreen(h, ref p);
            uint dpi = 96;
            try { dpi = Native.GetDpiForWindow(h); } catch { }
            if (dpi == 0) dpi = 96;
            w.Awareness = 2;
            try { w.Awareness = Native.GetAwarenessFromDpiAwarenessContext(Native.GetWindowDpiAwarenessContext(h)); } catch { }
            w.Scale = dpi / 96.0;
            int cw = rc.Right - rc.Left, chh = rc.Bottom - rc.Top;
            if (w.Awareness == 0)
            {
                // DPI 非対応アプリはクライアントサイズが論理値で返るので物理ピクセルに直す
                cw = (int)Math.Round(cw * w.Scale);
                chh = (int)Math.Round(chh * w.Scale);
            }
            w.Client = new Rectangle(p.X, p.Y, cw, chh);
            w.ClassName = Native.GetClass(h);
            if (w.Client.Width < 200 || w.Client.Height < 200) return null;
            return w;
        }
    }

    /// <summary>発音調整パネルの位置とモード。</summary>
    internal sealed class Panel
    {
        public VpWindow Win;
        public Settings St;
        public double S;          // 拡大率
        public int Top;           // パネル上端の線（スクリーンY）
        public bool TopDetected;
        public PanelMode Mode;
        public int[] IconDark = new int[3];

        public double Y(double off) { return Top + off * S; }
        public int Yi(double off) { return (int)Math.Round(Y(off)); }
        public int ContentLeft { get { return Win.Client.Left + (int)Math.Round(St.ContentLeft * S); } }
        public int ContentRight { get { return Win.Client.Right - 1; } }
        public int Bottom { get { return Win.Client.Bottom; } }

        public Rectangle ContentRect
        {
            get { return Rectangle.FromLTRB(ContentLeft, Top, ContentRight + 1, Bottom); }
        }

        public Point IconCenter(PanelMode m)
        {
            double y = m == PanelMode.Accent ? St.IconAccentY : m == PanelMode.Intonation ? St.IconIntonationY : St.IconLengthY;
            return new Point(Win.Client.Left + (int)Math.Round(St.IconX * S), Yi(y));
        }

        public bool InContent(Point p)
        {
            return p.X >= ContentLeft && p.X <= ContentRight && p.Y >= Top && p.Y < Bottom;
        }

        public bool InPanel(Point p)
        {
            return p.X >= Win.Client.Left && p.X <= ContentRight && p.Y >= Top && p.Y < Bottom;
        }

        /// <summary>
        /// 左下のアイコン列だけを取り込み、パネル上端・拡大率・モードを調べる（軽い）。
        /// VOICEPEAK は Windows の表示スケールどおりに拡大されないことがあるため、
        /// 拡大率は「ウィンドウ下端からパネル上端の線までの高さ」を実測して求める。
        /// </summary>
        public static Panel Detect(VpWindow w, Settings st)
        {
            const double MaxS = 2.6;
            int x0 = w.Client.Left;
            int stripW = (int)Math.Round(70 * MaxS);
            int y0 = Math.Max(w.Client.Top, w.Client.Bottom - (int)Math.Round((st.PanelHeight + 40) * MaxS));
            var f = Frame.Capture(new Rectangle(x0, y0, Math.Min(stripW, w.Client.Width), w.Client.Bottom - y0));
            if (f == null) return null;

            // 候補（パネル上端の線の位置と拡大率）
            var cands = new List<KeyValuePair<int, double>>(); // top, scale
            var detected = new List<bool>();
            if (st.UiScale > 0)
            {
                cands.Add(new KeyValuePair<int, double>(w.Client.Bottom - (int)Math.Round(st.PanelHeight * st.UiScale), st.UiScale));
                detected.Add(false);
            }
            // パネル上端の線：地(242)の上にある濃いめの灰色の1px線
            int sx = x0 + 8;
            for (int y = y0 + 1; y < w.Client.Bottom - (int)(st.PanelHeight * 0.7); y++)
            {
                int g0 = f.Gray(sx, y);
                if (g0 < 150 || g0 > 222) continue;
                int g1 = Math.Max(f.Gray(sx, y + 1), f.Gray(sx, y + 2));
                if (g1 < 247) continue;
                bool bgBelow = true;
                for (int k = 3; k < 9; k++)
                {
                    int gk = f.Gray(sx, y + k);
                    if (gk < 236 || gk > 254) { bgBelow = false; break; }
                }
                if (!bgBelow) continue;
                double sc = (w.Client.Bottom - y) / st.PanelHeight;
                if (st.UiScale > 0) { if (Math.Abs(sc - st.UiScale) < 0.05) { cands.Insert(0, new KeyValuePair<int, double>(y, st.UiScale)); detected.Insert(0, true); } continue; }
                if (sc < 0.75 || sc > MaxS) continue;
                double nice = Math.Round(sc * 4) / 4.0;
                if (Math.Abs(nice - sc) < 0.02) sc = nice; // 1px の誤差を吸収
                cands.Add(new KeyValuePair<int, double>(y, sc));
                detected.Add(true);
            }
            // 線が見つからないときの予備
            foreach (double sc in new[] { 1.0, w.Scale })
            {
                cands.Add(new KeyValuePair<int, double>(w.Client.Bottom - (int)Math.Round(st.PanelHeight * sc), sc));
                detected.Add(false);
            }

            Panel best = null;
            for (int i = 0; i < cands.Count; i++)
            {
                var pn = new Panel { Win = w, St = st, S = cands[i].Value, Top = cands[i].Key, TopDetected = detected[i] };
                pn.EvalMode(f);
                if (pn.Mode != PanelMode.Unknown) return pn;
                if (best == null) best = pn;
            }
            return best;
        }

        void EvalMode(Frame f)
        {
            // モード：選択中のアイコンは濃い灰色で塗られている
            var modes = new[] { PanelMode.Accent, PanelMode.Intonation, PanelMode.Length };
            int half = (int)Math.Round(18 * S);
            for (int i = 0; i < 3; i++)
            {
                var c = IconCenter(modes[i]);
                int cnt = 0;
                for (int y = c.Y - half; y <= c.Y + half; y++)
                    for (int x = c.X - half; x <= c.X + half; x++)
                    {
                        int r, g, b; f.Get(x, y, out r, out g, out b);
                        if (r < 0) continue;
                        if (r < 165 && g < 165 && b < 170 && Math.Abs(r - b) < 20) cnt++;
                    }
                IconDark[i] = cnt;
            }
            int area = (2 * half + 1) * (2 * half + 1);
            int bi = 0;
            for (int i = 1; i < 3; i++) if (IconDark[i] > IconDark[bi]) bi = i;
            int second = 0;
            for (int i = 0; i < 3; i++) if (i != bi) second = Math.Max(second, IconDark[i]);
            Mode = (IconDark[bi] > area * 0.35 && IconDark[bi] > second * 2) ? modes[bi] : PanelMode.Unknown;
        }

        public Frame CaptureContent()
        {
            return Frame.Capture(ContentRect);
        }
    }
}
