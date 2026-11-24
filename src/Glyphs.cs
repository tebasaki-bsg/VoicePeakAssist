using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace VoicePeakAssist
{
    /// <summary>
    /// 文字の形のお手本照合。OCR で読めなかった列を、カタカナの手本と形を比べて判定する。
    /// 手本は (1) Windows のフォントで描いたカタカナ と (2) OCR で読めた VOICEPEAK 上の文字 から作る。
    /// </summary>
    internal static class Glyphs
    {
        const int N = 24;
        static readonly object Lock = new object();
        static Dictionary<char, List<float[]>> rendered;
        static readonly Dictionary<char, List<float[]>> learned = new Dictionary<char, List<float[]>>();
        static bool learnedLoaded;

        static string LearnedPath { get { return Path.Combine(Settings.Dir, "glyphs.dat"); } }

        public static IEnumerable<char> KanaSet()
        {
            for (char c = 'ァ'; c <= 'ヴ'; c++) yield return c;
            yield return 'ー';
        }

        const string SmallKana = "ァィゥェォッャュョヮヵヶ";
        public static bool IsSmallKana(char c) { return SmallKana.IndexOf(c) >= 0; }

        public static bool IsKatakana(char c) { return (c >= 'ァ' && c <= 'ヶ') || c == 'ー'; }

        // ---------- 形の数値化 ----------

        /// <summary>インクの範囲を縦横比を保って N×N に縮め、濃さ（0〜1）の配列にする。</summary>
        public static float[] FromFrame(Frame f, Rectangle ink)
        {
            int w = ink.Width, h = ink.Height;
            if (w <= 0 || h <= 0) return null;
            var g = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int gray = f.Gray(ink.Left + x, ink.Top + y);
                    g[y * w + x] = gray < 0 ? 0 : Math.Max(0f, Math.Min(1f, (235 - gray) / 110f));
                }
            return Resample(g, w, h);
        }

        static float[] FromBitmap(Bitmap bmp)
        {
            // 黒い文字（白地）からインクの範囲を求めて数値化
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new byte[data.Stride * bmp.Height];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            bmp.UnlockBits(data);
            int W = bmp.Width, H = bmp.Height, minX = W, minY = H, maxX = -1, maxY = -1;
            var dark = new float[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * data.Stride + x * 4;
                    int gray = (px[i] * 11 + px[i + 1] * 59 + px[i + 2] * 30) / 100;
                    float d = Math.Max(0f, Math.Min(1f, (235 - gray) / 110f));
                    dark[y * W + x] = d;
                    if (gray < 185) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
                }
            if (maxX < 0) return null;
            int w = maxX - minX + 1, h = maxY - minY + 1;
            var g = new float[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) g[y * w + x] = dark[(y + minY) * W + x + minX];
            return Resample(g, w, h);
        }

        static float[] Resample(float[] g, int w, int h)
        {
            var v = new float[N * N];
            double sc = (N - 4) / (double)Math.Max(w, h);
            double ow = w * sc, oh = h * sc;
            double ox = (N - ow) / 2, oy = (N - oh) / 2;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    double sx = (x + 0.5 - ox) / sc - 0.5, sy = (y + 0.5 - oy) / sc - 0.5;
                    if (sx < -0.5 || sy < -0.5 || sx > w - 0.5 || sy > h - 0.5) continue;
                    int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                    double fx = sx - x0, fy = sy - y0;
                    double a = Px(g, w, h, x0, y0), b = Px(g, w, h, x0 + 1, y0), c = Px(g, w, h, x0, y0 + 1), d = Px(g, w, h, x0 + 1, y0 + 1);
                    v[y * N + x] = (float)((a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy);
                }
            // 少しぼかして、線の太さや位置の小さな違いを吸収する
            var o = new float[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    double sum = 0, wsum = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= N || yy >= N) continue;
                            double wt = (dx == 0 && dy == 0) ? 2 : 1;
                            sum += v[yy * N + xx] * wt; wsum += wt;
                        }
                    o[y * N + x] = (float)(sum / wsum);
                }
            return o;
        }

        static double Px(float[] g, int w, int h, int x, int y)
        {
            if (x < 0) x = 0; if (y < 0) y = 0; if (x >= w) x = w - 1; if (y >= h) y = h - 1;
            return g[y * w + x];
        }

        static double Corr(float[] a, float[] b)
        {
            double ma = a.Average(), mb = b.Average();
            double sab = 0, saa = 0, sbb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                double x = a[i] - ma, y = b[i] - mb;
                sab += x * y; saa += x * x; sbb += y * y;
            }
            if (saa <= 0 || sbb <= 0) return 0;
            return sab / Math.Sqrt(saa * sbb);
        }

        // ---------- 手本 ----------

        static void EnsureRendered()
        {
            if (rendered != null) return;
            rendered = new Dictionary<char, List<float[]>>();
            var installed = new InstalledFontCollection().Families.Select(f => f.Name).ToList();
            var fonts = new[] { "Noto Sans JP", "Noto Sans CJK JP", "Source Han Sans JP", "源ノ角ゴシック JP", "Yu Gothic UI", "Yu Gothic", "游ゴシック", "Meiryo UI", "Meiryo", "メイリオ", "BIZ UDPGothic", "MS UI Gothic" }
                .Where(n => installed.Contains(n)).Distinct().Take(5).ToList();
            Log.Write("文字の手本に使うフォント: " + (fonts.Count > 0 ? string.Join(", ", fonts) : "（なし）"));
            foreach (var fn in fonts)
            {
                try
                {
                    using (var font = new Font(fn, 40f, FontStyle.Regular, GraphicsUnit.Pixel))
                        foreach (char ch in KanaSet())
                            using (var bmp = new Bitmap(80, 80, PixelFormat.Format32bppArgb))
                            {
                                using (var g = Graphics.FromImage(bmp))
                                {
                                    g.Clear(Color.White);
                                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                                    g.DrawString(ch.ToString(), font, Brushes.Black, 10, 5);
                                }
                                var v = FromBitmap(bmp);
                                if (v == null) continue;
                                List<float[]> list;
                                if (!rendered.TryGetValue(ch, out list)) rendered[ch] = list = new List<float[]>();
                                list.Add(v);
                            }
                }
                catch (Exception ex) { Log.Write("手本の作成に失敗: " + fn + " " + ex.Message); }
            }
        }

        static void EnsureLearned()
        {
            if (learnedLoaded) return;
            learnedLoaded = true;
            try
            {
                if (!File.Exists(LearnedPath)) return;
                foreach (var line in File.ReadAllLines(LearnedPath, Encoding.UTF8))
                {
                    var parts = line.Split('\t');
                    if (parts.Length != 2 || parts[0].Length != 1) continue;
                    var bytes = Convert.FromBase64String(parts[1]);
                    if (bytes.Length != N * N) continue;
                    var v = bytes.Select(b => b / 255f).ToArray();
                    List<float[]> list;
                    if (!learned.TryGetValue(parts[0][0], out list)) learned[parts[0][0]] = list = new List<float[]>();
                    list.Add(v);
                }
            }
            catch (Exception ex) { Log.Write("文字の手本ファイルを読めません: " + ex.Message); }
        }

        /// <summary>OCR で確実に読めた文字の形を手本として覚える（1文字につき最大4つ）。</summary>
        public static void Learn(char ch, float[] v)
        {
            if (v == null || !IsKatakana(ch)) return;
            lock (Lock)
            {
                EnsureLearned();
                List<float[]> list;
                if (!learned.TryGetValue(ch, out list)) learned[ch] = list = new List<float[]>();
                if (list.Any(t => Corr(t, v) > 0.97)) return; // ほぼ同じ形はもう持っている
                if (list.Count >= 4) list.RemoveAt(0);
                list.Add(v);
                try
                {
                    var sb = new StringBuilder();
                    foreach (var kv in learned)
                        foreach (var t in kv.Value)
                            sb.Append(kv.Key).Append('\t').Append(Convert.ToBase64String(t.Select(x => (byte)Math.Round(Math.Max(0, Math.Min(1, x)) * 255)).ToArray())).Append('\n');
                    File.WriteAllText(LearnedPath, sb.ToString(), Encoding.UTF8);
                }
                catch { }
            }
        }

        /// <summary>形が最も近いカタカナ。score = 一致度（-1〜1）、margin = 2番目の文字との差。</summary>
        public static char Classify(float[] v, bool small, out double score, out double margin, out char second)
        {
            score = 0; margin = 0; second = '\0';
            if (v == null) return '\0';
            lock (Lock)
            {
                EnsureRendered();
                EnsureLearned();
                var best = new Dictionary<char, double>();
                foreach (var src in new[] { rendered, learned })
                    foreach (var kv in src)
                    {
                        // 大きさは別に測っているので、同じ大きさの文字だけと比べる
                        if (IsSmallKana(kv.Key) != small) continue;
                        foreach (var t in kv.Value)
                        {
                            double c = Corr(v, t);
                            if (src == learned) c += 0.03; // VOICEPEAK 上で覚えた手本を少し優先
                            double cur;
                            if (!best.TryGetValue(kv.Key, out cur) || c > cur) best[kv.Key] = c;
                        }
                    }
                if (best.Count == 0) return '\0';
                var order = best.OrderByDescending(kv => kv.Value).ToList();
                score = order[0].Value;
                if (order.Count > 1) { margin = order[0].Value - order[1].Value; second = order[1].Key; }
                else margin = 1;
                return order[0].Key;
            }
        }
    }
}
