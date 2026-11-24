using System;
using System.Collections.Generic;
using System.Drawing;

namespace VoicePeakAssist
{
    internal struct Dot
    {
        public double X, Y; public int Count;
        public override string ToString() { return string.Format("({0:0.0},{1:0.0})", X, Y); }
    }

    internal struct LenBox
    {
        public int Left, Right;            // 枠線のX（スクリーン座標、両端含む）
        public double Center { get { return (Left + Right) / 2.0; } }
        public int Width { get { return Right - Left + 1; } }
        public double Value;               // 幅から推定した値（%）
        public override string ToString() { return string.Format("[{0}-{1} {2:+0;-0}%]", Left, Right, Value); }
    }

    internal struct Thumb
    {
        public int Left, Right, Y;
        public bool Found;
        public int Width { get { return Right - Left + 1; } }
        public int Center { get { return (Left + Right) / 2; } }
    }

    internal static class Detect
    {
        // ---------- イントネーションの点 ----------

        /// <summary>点の色を推定する：帯の中で彩度の高い色のうち最も多い色（点は6x6の単色）。</summary>
        static bool DotColor(Frame f, Panel pn, out int cr, out int cg, out int cb)
        {
            var hist = new Dictionary<int, int>();
            int y0 = pn.Yi(pn.St.DotBandTop), y1 = pn.Yi(pn.St.DotBandBottom);
            for (int y = y0; y <= y1; y++)
                for (int x = f.X0; x < f.X0 + f.W; x++)
                {
                    int r, g, b; f.Get(x, y, out r, out g, out b);
                    if (r < 0) continue;
                    int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                    if (mx - mn < 45) continue;
                    int key = (r << 16) | (g << 8) | b;
                    int c; hist.TryGetValue(key, out c); hist[key] = c + 1;
                }
            int bestKey = -1, bestCnt = 0;
            foreach (var kv in hist) if (kv.Value > bestCnt) { bestCnt = kv.Value; bestKey = kv.Key; }
            cr = (bestKey >> 16) & 255; cg = (bestKey >> 8) & 255; cb = bestKey & 255;
            return bestKey >= 0 && bestCnt >= 12 * pn.S * pn.S;
        }

        public static List<Dot> Dots(Frame f, Panel pn)
        {
            var res = new List<Dot>();
            int cr, cg, cb;
            if (!DotColor(f, pn, out cr, out cg, out cb)) return res;
            int y0 = pn.Yi(pn.St.DotBandTop), y1 = pn.Yi(pn.St.DotBandBottom);
            int w = f.W;
            var colCount = new int[w];
            var sumY = new double[w];
            for (int x = 0; x < w; x++)
                for (int y = y0; y <= y1; y++)
                {
                    int r, g, b; f.Get(f.X0 + x, y, out r, out g, out b);
                    if (r < 0) continue;
                    if (Math.Abs(r - cr) + Math.Abs(g - cg) + Math.Abs(b - cb) <= 24) { colCount[x]++; sumY[x] += y; }
                }
            int minCol = Math.Max(2, (int)Math.Round(3 * pn.S));
            int xx = 0;
            while (xx < w)
            {
                if (colCount[xx] < minCol) { xx++; continue; }
                int s = xx; double sx = 0, sy = 0; int n = 0;
                while (xx < w && colCount[xx] >= 1)
                {
                    sx += (f.X0 + xx) * (double)colCount[xx]; sy += sumY[xx]; n += colCount[xx]; xx++;
                }
                int width = xx - s;
                if (n >= 12 * pn.S * pn.S && width <= 14 * pn.S)
                    res.Add(new Dot { X = sx / n, Y = sy / n, Count = n });
            }
            return res;
        }

        // ---------- 長さボックス ----------

        static bool IsBorder(Frame f, int x, int y)
        {
            int r, g, b; f.Get(x, y, out r, out g, out b);
            if (r < 0) return false;
            if (r >= 250 && g >= 250 && b >= 250) return false; // 白
            if (f.IsPanelBg(x, y)) return false;
            return true;
        }

        static int Diff(Frame f, int x, int y, int r0, int g0, int b0)
        {
            int r, g, b; f.Get(x, y, out r, out g, out b);
            if (r < 0) return 999;
            return Math.Max(Math.Abs(r - r0), Math.Max(Math.Abs(g - g0), Math.Abs(b - b0)));
        }

        /// <summary>
        /// 長さボックスを探す。中が一様な明るい色（白。カーソルが乗ると色が変わることがある）で、
        /// 左右と上下を中と違う色の枠で囲まれた四角をボックスとみなす。
        /// </summary>
        public static List<LenBox> Boxes(Frame f, Panel pn)
        {
            var res = new List<LenBox>();
            // 中央はカーソルが乗ると「↔」が出るので、上寄りの高さで調べる
            int ym = pn.Yi(pn.St.BoxScanY);
            int yTop = pn.Yi(pn.St.BoxTopY), yBot = pn.Yi(pn.St.BoxBottomY);
            int minInterior = Math.Max(5, (int)Math.Round(8 * pn.S));
            int maxWidth = (int)Math.Round(pn.St.BoxBaseWidth * 2 * pn.S + 6 * pn.S);
            int xEnd = f.X0 + f.W - 1;
            int x = f.X0;
            while (x < xEnd)
            {
                int r, g, b; f.Get(x + 1, ym, out r, out g, out b);
                bool lightNext = (r + g + b) >= 675 && Math.Min(r, Math.Min(g, b)) >= 185;
                if (!lightNext || Diff(f, x, ym, r, g, b) < 8) { x++; continue; }
                // x = 左の枠、x+1 から中（色 r,g,b）
                int xr = x + 1;
                while (xr <= xEnd && Diff(f, xr, ym, r, g, b) <= 4) xr++;
                if (xr > xEnd) break;
                int interior = xr - x - 1;
                if (interior < minInterior || xr - x + 1 > maxWidth) { x = xr - 1 > x ? xr - 1 : x + 1; continue; }
                // 枠の外側が地の色だけ（ブロックの間）なら対象外
                if (f.IsPanelBg(x + 1, ym)) { x = xr; continue; }
                int mid = (x + xr) / 2;
                bool top = false, bottom = false;
                for (int dy = -2; dy <= 2 && !top; dy++) if (Diff(f, mid, yTop + dy, r, g, b) >= 8) top = true;
                for (int dy = -2; dy <= 2 && !bottom; dy++) if (Diff(f, mid, yBot + dy, r, g, b) >= 8) bottom = true;
                // 上下の枠の外は中と違う色ではない（文字やラベルに当たらないよう、枠のすぐ内側は中の色であること）
                bool inner = Diff(f, mid, yTop + (int)Math.Round(4 * pn.S), r, g, b) <= 6 && Diff(f, mid, yBot - (int)Math.Round(4 * pn.S), r, g, b) <= 6;
                if (top && bottom && inner)
                {
                    var bx = new LenBox { Left = x, Right = xr };
                    bx.Value = Math.Round((bx.Width / (pn.St.BoxBaseWidth * pn.S) - 1.0) * 100.0);
                    res.Add(bx);
                    x = xr; // 右の枠から次を探す（隣のボックスと枠が接している場合に備える）
                }
                else x = xr - 1 > x ? xr - 1 : x + 1;
            }
            return res;
        }

        public static int Nearest<T>(List<T> list, Func<T, double> xOf, double x, double maxDist)
        {
            int bi = -1; double bd = double.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                double d = Math.Abs(xOf(list[i]) - x);
                if (d < bd) { bd = d; bi = i; }
            }
            return bd <= maxDist ? bi : -1;
        }

        // ---------- 単語ブロック ----------

        /// <summary>
        /// 単語ブロックの範囲（左右の枠線のX）を返す。枠線は、カタカナの下から一番下まで縦に続く線として見分ける
        /// （文字・点・ボックスの線は途中で切れる）。
        /// </summary>
        public static List<KeyValuePair<int, int>> Blocks(Frame f, Panel pn)
        {
            int y0 = pn.Yi(58), y1 = pn.Yi(190);
            int h = y1 - y0;
            var lines = new List<int>();
            for (int x = f.X0; x < f.X0 + f.W; x++)
            {
                int cnt = 0;
                for (int y = y0; y < y1; y++)
                {
                    int r, g, b; f.Get(x, y, out r, out g, out b);
                    if (r < 0) continue;
                    int mn = Math.Min(r, Math.Min(g, b));
                    if (mn < 245 && !f.IsPanelBg(x, y)) cnt++;
                }
                if (cnt >= h * 0.9) lines.Add(x);
            }
            // 隣り合う列はまとめる
            var borders = new List<int>();
            foreach (int x in lines)
                if (borders.Count == 0 || x - borders[borders.Count - 1] > 2) borders.Add(x);
                else borders[borders.Count - 1] = x;
            // 枠線どうしの間が白い（ブロックの中）ならブロック
            var res = new List<KeyValuePair<int, int>>();
            int ym = pn.Yi(26);
            for (int i = 0; i + 1 < borders.Count; i++)
            {
                int l = borders[i], r = borders[i + 1];
                if (r - l < 20 * pn.S) continue;
                int mid = (l + r) / 2;
                if (f.IsPanelBg(mid, ym) || f.IsPanelBg(mid, pn.Yi(120))) continue;
                res.Add(new KeyValuePair<int, int>(l, r));
            }
            return res;
        }

        /// <summary>x を含む単語ブロック。なければ null。</summary>
        public static KeyValuePair<int, int>? BlockAt(List<KeyValuePair<int, int>> blocks, double x)
        {
            foreach (var b in blocks) if (x > b.Key && x < b.Value) return b;
            return null;
        }

        // ---------- 単語ブロック ----------

        /// <summary>
        /// 単語ブロックの左右の枠線（パネルを縦に貫く線）の X 座標を返す。
        /// 枠線以外（カタカナ・点・ボックス・曲線）は途中で途切れるので区別できる。
        /// </summary>
        public static List<int> BlockBorders(Frame f, Panel pn)
        {
            int y0 = pn.Yi(58), y1 = pn.Yi(190);
            int h = y1 - y0 + 1;
            var xs = new List<int>();
            for (int x = f.X0; x < f.X0 + f.W; x++)
            {
                int cnt = 0;
                for (int y = y0; y <= y1; y++)
                {
                    int r, g, b; f.Get(x, y, out r, out g, out b);
                    if (r < 0) continue;
                    int mn = Math.Min(r, Math.Min(g, b));
                    if (mn < 245 && !f.IsPanelBg(x, y)) cnt++;
                }
                if (cnt >= h * 0.9) xs.Add(x);
            }
            // 隣り合う列はまとめる
            var res = new List<int>();
            foreach (int x in xs)
                if (res.Count == 0 || x - res[res.Count - 1] > 2) res.Add(x);
                else res[res.Count - 1] = x;
            return res;
        }

        /// <summary>x を含む単語ブロックの範囲（枠線の X）。見つからなければ false。</summary>
        public static bool BlockAt(Frame f, Panel pn, double x, out int left, out int right)
        {
            var borders = BlockBorders(f, pn);
            left = f.X0; right = f.X0 + f.W - 1; // 画面の端で切れている場合は端まで
            bool hasL = false, hasR = false;
            foreach (int b in borders)
            {
                if (b < x && (!hasL || b > left)) { left = b; hasL = true; }
                if (b > x && (!hasR || b < right)) { right = b; hasR = true; }
            }
            return hasL || hasR;
        }

        // ---------- 横スクロールバー ----------

        public static Thumb FindThumb(Frame f, Panel pn)
        {
            var t = new Thumb();
            int y = pn.Yi(pn.St.ScrollThumbY);
            int minLen = (int)Math.Round(20 * pn.S);
            int x = f.X0, xEnd = f.X0 + f.W;
            while (x < xEnd)
            {
                int r, g, b; f.Get(x, y, out r, out g, out b);
                bool on = r >= 150 && r <= 205 && Math.Abs(r - g) <= 6 && Math.Abs(r - b) <= 6;
                if (!on) { x++; continue; }
                int s = x;
                while (x < xEnd)
                {
                    f.Get(x, y, out r, out g, out b);
                    if (!(r >= 150 && r <= 205 && Math.Abs(r - g) <= 6 && Math.Abs(r - b) <= 6)) break;
                    x++;
                }
                if (x - s >= minLen && (!t.Found || x - s > t.Width))
                {
                    t.Found = true; t.Left = s; t.Right = x - 1; t.Y = y;
                }
            }
            return t;
        }

        // ---------- カタカナの列 ----------

        internal sealed class KanaCell
        {
            public double ColumnX;
            public Rectangle Ink;      // 文字のインクの範囲（スクリーン座標）。空なら文字なし
            public bool HasInk { get { return Ink.Width > 0; } }
            public bool Small;         // 小さい文字（ッ など）
            public bool Wide;          // 2文字分の幅（拗音など）
            public string Ocr = "";    // OCR の結果
            public char Char;          // 確定した文字（'\0' = 不明）
            public float[] Vec;        // 文字の形（お手本照合用）
            public string How = "";    // どう判定したか
        }

        public static KanaCell Cell(Frame f, Panel pn, double colX)
        {
            var c = new KanaCell { ColumnX = colX };
            int half = (int)Math.Round(pn.St.ColumnPitch * 0.42 * pn.S);
            int x0 = (int)Math.Round(colX) - half, x1 = (int)Math.Round(colX) + half;
            int y0 = pn.Yi(pn.St.KanaTop), y1 = pn.Yi(pn.St.KanaBottom);
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int r, g, b; f.Get(x, y, out r, out g, out b);
                    if (r < 0) continue;
                    int gray = (r * 30 + g * 59 + b * 11) / 100;
                    int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                    if (gray < 185 && mx - mn < 40)
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
                }
            if (maxX < minX) return c;
            c.Ink = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            double s = pn.S;
            c.Small = c.Ink.Height <= 12.5 * s && c.Ink.Width <= 12.5 * s;
            c.Wide = c.Ink.Width > 21 * s;
            return c;
        }
    }
}
