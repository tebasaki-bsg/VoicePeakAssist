using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VoicePeakAssist
{
    internal struct OcrWord
    {
        public string Text;
        public RectangleF Rect;
    }

    /// <summary>
    /// Windows 標準の OCR（Windows.Media.Ocr）を .NET Framework から使う。
    /// 追加の参照なしでビルドできるよう、Windows Runtime の型はリフレクションで呼び出す。
    /// </summary>
    internal static class WinOcr
    {
        static bool initDone;
        static object engine;
        public static string Error = "";
        public static string LanguageTag = "";
        static Type tEngine, tSoftwareBitmap, tPixelFormat, tIBuffer, tOcrResult, tOcrLine, tOcrWord, tIAsyncOp;
        static MethodInfo miAsBuffer, miAsTaskGeneric, miCreateCopy, miRecognize;
        static readonly object Lock = new object();

        const string WinRt = ", Windows, ContentType=WindowsRuntime";

        static Type T(string name) { return Type.GetType(name + WinRt, true); }

        public static bool Available
        {
            get { Init(); return engine != null; }
        }

        public static int MaxDimension = 2600;

        static void Init()
        {
            lock (Lock)
            {
                if (initDone) return;
                initDone = true;
                try
                {
                    tEngine = T("Windows.Media.Ocr.OcrEngine");
                    tSoftwareBitmap = T("Windows.Graphics.Imaging.SoftwareBitmap");
                    tPixelFormat = T("Windows.Graphics.Imaging.BitmapPixelFormat");
                    tIBuffer = T("Windows.Storage.Streams.IBuffer");
                    tOcrResult = T("Windows.Media.Ocr.OcrResult");
                    tOcrLine = T("Windows.Media.Ocr.OcrLine");
                    tOcrWord = T("Windows.Media.Ocr.OcrWord");
                    tIAsyncOp = T("Windows.Foundation.IAsyncOperation`1");
                    var tLanguage = T("Windows.Globalization.Language");

                    var rt = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                    var tBufExt = rt.GetType("System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions", true);
                    miAsBuffer = tBufExt.GetMethods().First(m => m.Name == "AsBuffer" && m.GetParameters().Length == 1);
                    var tSysExt = rt.GetType("System.WindowsRuntimeSystemExtensions", true);
                    miAsTaskGeneric = tSysExt.GetMethods().First(m =>
                        m.Name == "AsTask" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType.IsGenericType &&
                        m.GetParameters()[0].ParameterType.GetGenericTypeDefinition().FullName == "Windows.Foundation.IAsyncOperation`1");

                    miCreateCopy = tSoftwareBitmap.GetMethod("CreateCopyFromBuffer", new[] { tIBuffer, tPixelFormat, typeof(int), typeof(int) });
                    miRecognize = tEngine.GetMethod("RecognizeAsync");

                    try
                    {
                        var lang = Activator.CreateInstance(tLanguage, new object[] { "ja" });
                        var isSup = tEngine.GetMethod("IsLanguageSupported");
                        bool sup = (bool)isSup.Invoke(null, new[] { lang });
                        if (sup) engine = tEngine.GetMethod("TryCreateFromLanguage").Invoke(null, new[] { lang });
                    }
                    catch (Exception ex) { Log.Write("OCR: 日本語の指定に失敗: " + ex.Message); }

                    if (engine == null)
                    {
                        engine = tEngine.GetMethod("TryCreateFromUserProfileLanguages").Invoke(null, null);
                        if (engine != null)
                        {
                            try
                            {
                                var l = tEngine.GetProperty("RecognizerLanguage").GetValue(engine);
                                LanguageTag = Convert.ToString(l.GetType().GetProperty("LanguageTag").GetValue(l));
                            }
                            catch { }
                            if (!LanguageTag.StartsWith("ja"))
                            {
                                Error = "日本語の文字認識（OCR）が使えません。Windowsの設定で日本語の言語パックを追加してください。";
                                engine = null;
                            }
                        }
                    }
                    else LanguageTag = "ja";

                    try { MaxDimension = Convert.ToInt32(tEngine.GetProperty("MaxImageDimension").GetValue(null)); } catch { }
                    if (engine == null && Error.Length == 0) Error = "日本語の文字認識（OCR）が使えません。";
                    Log.Write("OCR 初期化: " + (engine != null ? "OK (" + LanguageTag + ", 最大" + MaxDimension + "px)" : Error));
                }
                catch (Exception ex)
                {
                    engine = null;
                    Error = "文字認識（OCR）を初期化できませんでした: " + ex.Message;
                    Log.Write(Error + "\r\n" + ex);
                }
            }
        }

        /// <summary>画像を OCR して単語（文字列と位置）を返す。</summary>
        public static List<OcrWord> Recognize(Bitmap bmp)
        {
            Init();
            var res = new List<OcrWord>();
            if (engine == null) return res;
            lock (Lock)
            {
                int w = bmp.Width, h = bmp.Height;
                var bytes = new byte[w * h * 4];
                using (var b32 = bmp.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb))
                {
                    var data = b32.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * w * 4, w * 4);
                    b32.UnlockBits(data);
                }
                object buffer = miAsBuffer.Invoke(null, new object[] { bytes });
                object fmt = Enum.ToObject(tPixelFormat, 87); // Bgra8
                object sbmp = miCreateCopy.Invoke(null, new object[] { buffer, fmt, w, h });
                object op = miRecognize.Invoke(engine, new[] { sbmp });
                var asTask = miAsTaskGeneric.MakeGenericMethod(tOcrResult);
                var task = (Task)asTask.Invoke(null, new[] { op });
                if (!task.Wait(5000)) { Log.Write("OCR がタイムアウトしました"); return res; }
                object result = task.GetType().GetProperty("Result").GetValue(task);
                var lines = (IEnumerable)tOcrResult.GetProperty("Lines").GetValue(result);
                var pWords = tOcrLine.GetProperty("Words");
                var pText = tOcrWord.GetProperty("Text");
                var pRect = tOcrWord.GetProperty("BoundingRect");
                foreach (var line in lines)
                {
                    var words = (IEnumerable)pWords.GetValue(line);
                    foreach (var word in words)
                    {
                        string text = (string)pText.GetValue(word);
                        object r = pRect.GetValue(word);
                        var rt = r.GetType();
                        float x = Convert.ToSingle(rt.GetProperty("X").GetValue(r));
                        float y = Convert.ToSingle(rt.GetProperty("Y").GetValue(r));
                        float ww = Convert.ToSingle(rt.GetProperty("Width").GetValue(r));
                        float hh = Convert.ToSingle(rt.GetProperty("Height").GetValue(r));
                        res.Add(new OcrWord { Text = text, Rect = new RectangleF(x, y, ww, hh) });
                    }
                }
                try { var d = sbmp as IDisposable; if (d != null) d.Dispose(); } catch { }
            }
            return res;
        }
    }

    /// <summary>発音調整パネルのカタカナを読み取る。</summary>
    internal static class KanaReader
    {
        // 対象：か・さ・た・は・ぱ行の清音
        public static readonly string Targets = "カキクケコサシスセソタチツテトハヒフヘホパピプペポ";

        static readonly Dictionary<char, char> LookAlike = new Dictionary<char, char>
        {
            {'力','カ'}, {'夕','タ'}, {'卜','ト'}, {'八','ハ'}, {'千','チ'}, {'口','ロ'}, {'工','エ'},
            {'二','ニ'}, {'才','オ'}, {'一','ー'}, {'ー','ー'}, {'―','ー'}, {'－','ー'}, {'匕','ヒ'},
            {'七','セ'}, {'子','チ'}, {'于','チ'}, {'メ','メ'}, {'乂','メ'}, {'ﾉ','ノ'}, {'丿','ノ'},
            {'レ','レ'}, {'し','シ'}, {'へ','ヘ'}, {'べ','ベ'}, {'ぺ','ペ'}, {'り','リ'}, {'三','ミ'}, {'彡','ミ'}, {'≡','ミ'}
        };

        public static char Normalize(char c)
        {
            char m;
            if (LookAlike.TryGetValue(c, out m)) return m;
            if (c >= 'ぁ' && c <= 'ゖ') return (char)(c + 0x60); // ひらがな→カタカナ
            if (c >= 'ｦ' && c <= 'ﾝ')
            {
                string half = c.ToString().Normalize(System.Text.NormalizationForm.FormKC);
                if (half.Length == 1) return half[0];
            }
            return c;
        }

        /// <summary>各列の文字を読み取り、cells[i].Char に入れる（不明なら '\0'）。</summary>
        public static void Read(Frame f, Panel pn, List<Detect.KanaCell> cells)
        {
            var withInk = cells.Where(c => c.HasInk).ToList();
            if (withInk.Count == 0) return;
            double s = pn.S;
            const int glyphTarget = 44;             // OCR に渡す文字の高さ（px）
            double scale = glyphTarget / (15.0 * s);
            int slotW = (int)(glyphTarget * 2.0), slotH = (int)(glyphTarget * 2.2);
            int perBatch = Math.Max(1, Math.Min(24, (WinOcr.MaxDimension - slotW) / slotW));

            using (var src = f.ToBitmap())
            {
                for (int start = 0; start < withInk.Count; start += perBatch)
                {
                    var batch = withInk.Skip(start).Take(perBatch).ToList();
                    using (var strip = new Bitmap(slotW * (batch.Count + 1), slotH, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(strip))
                        {
                            g.Clear(Color.White);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            for (int i = 0; i < batch.Count; i++)
                            {
                                var ink = batch[i].Ink;
                                int m = (int)Math.Round(2 * s);
                                var srcRect = Rectangle.FromLTRB(ink.Left - m - f.X0, ink.Top - m - f.Y0, ink.Right + m - f.X0, ink.Bottom + m - f.Y0);
                                float dw = (float)(srcRect.Width * scale), dh = (float)(srcRect.Height * scale);
                                // 文字の縦位置は元の行に合わせる（小さい文字が小さく見えるように）
                                float rowTop = (float)(pn.Y(pn.St.KanaTop) - f.Y0);
                                float dy = (float)((srcRect.Top - rowTop) * scale) + slotH * 0.2f;
                                float dx = slotW * (i + 0.5f) + (slotW - dw) / 2f;
                                g.DrawImage(src, new RectangleF(dx, dy, dw, dh), srcRect, GraphicsUnit.Pixel);
                            }
                        }
                        // 灰色の文字をくっきりさせる
                        Contrast(strip);
                        List<OcrWord> words;
                        try { words = WinOcr.Recognize(strip); }
                        catch (Exception ex) { Log.Write("OCR エラー: " + ex); words = new List<OcrWord>(); }
                        foreach (var wd in words)
                        {
                            string t = (wd.Text ?? "").Replace(" ", "");
                            if (t.Length == 0) continue;
                            for (int k = 0; k < t.Length; k++)
                            {
                                double cx = wd.Rect.X + wd.Rect.Width * (k + 0.5) / t.Length;
                                int slot = (int)Math.Floor(cx / slotW - 0.5);
                                if (slot < 0 || slot >= batch.Count) continue;
                                batch[slot].Ocr += t[k];
                            }
                        }
                    }
                }
            }

            foreach (var c in withInk)
            {
                c.Char = '\0';
                c.Vec = Glyphs.FromFrame(f, c.Ink);
                string first = c.Ocr;
                if (c.Ocr.Length == 1)
                {
                    char ch = Normalize(c.Ocr[0]);
                    if (Glyphs.IsKatakana(ch)) { c.Char = ch; c.How = "OCR"; Glyphs.Learn(ch, c.Vec); }
                }
                if (c.Char == '\0' && !c.Wide)
                {
                    // 1文字だけ大きくしてもう一度読む
                    string again = "";
                    try { again = RecognizeSingle(f, pn, c); } catch (Exception ex) { Log.Write("OCR エラー: " + ex.Message); }
                    if (again.Length == 1)
                    {
                        char ch = Normalize(again[0]);
                        if (Glyphs.IsKatakana(ch)) { c.Char = ch; c.How = "OCR（単独）"; }
                    }
                    if (c.Char == '\0')
                    {
                        // 形の手本と比べる
                        double score, margin; char second;
                        char ch = Glyphs.Classify(c.Vec, c.Small, out score, out margin, out second);
                        if (ch != '\0' && score >= 0.80 && margin >= 0.04) { c.Char = ch; c.How = string.Format("形（{0:0.00}）", score); }
                        string line = string.Format("文字の読み取り: x={0:0} OCR=「{1}」単独OCR=「{2}」 形の候補={3}({4:0.00}) 次点={5}(差{6:0.00}) → {7}",
                            c.ColumnX, first, again, ch, score, second, margin, c.Char == '\0' ? "不明" : c.Char.ToString());
                        if (c.Char == '\0') Log.Unread(line); else Log.Write(line);
                    }
                }
                if (c.Char == 'ツ' && c.Small) c.Char = 'ッ';
                c.Ocr = c.Char != '\0' ? c.Char.ToString() : c.Ocr;
            }
        }

        /// <summary>1つの列の文字だけを大きくして OCR する。</summary>
        static string RecognizeSingle(Frame f, Panel pn, Detect.KanaCell c)
        {
            if (!WinOcr.Available) return "";
            double s = pn.S;
            double scale = 64 / (15.0 * s);
            int m = (int)Math.Round(2 * s);
            var srcRect = Rectangle.FromLTRB(c.Ink.Left - m - f.X0, c.Ink.Top - m - f.Y0, c.Ink.Right + m - f.X0, c.Ink.Bottom + m - f.Y0);
            using (var src = f.ToBitmap())
            using (var bmp = new Bitmap((int)(srcRect.Width * scale) + 120, (int)(srcRect.Height * scale) + 80, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new RectangleF(60, 40, (float)(srcRect.Width * scale), (float)(srcRect.Height * scale)), srcRect, GraphicsUnit.Pixel);
                }
                Contrast(bmp);
                var words = WinOcr.Recognize(bmp);
                return string.Concat(words.Select(w => w.Text ?? "")).Replace(" ", "");
            }
        }

        static void Contrast(Bitmap bmp)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var px = new byte[data.Stride * bmp.Height];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            for (int i = 0; i < px.Length; i += 4)
            {
                int gray = (px[i] * 11 + px[i + 1] * 59 + px[i + 2] * 30) / 100;
                // 128（文字の灰色）→ 0、235以上 → 255
                int v = (gray - 120) * 255 / (235 - 120);
                if (v < 0) v = 0; if (v > 255) v = 255;
                px[i] = px[i + 1] = px[i + 2] = (byte)v; px[i + 3] = 255;
            }
            Marshal.Copy(px, 0, data.Scan0, px.Length);
            bmp.UnlockBits(data);
        }

        /// <summary>対象か（拗音・小さい文字・不明は対象外）。</summary>
        public static bool IsTarget(Detect.KanaCell c)
        {
            if (!c.HasInk || c.Char == '\0') return false;
            if (c.Wide || c.Small) return false;
            return Targets.IndexOf(c.Char) >= 0;
        }

        /// <summary>長さラベル（+40% など）を読み取る。読めなければ null。</summary>
        public static int? ReadLabel(Frame f, Panel pn, double colX)
        {
            if (!WinOcr.Available) return null;
            double s = pn.S;
            int half = (int)Math.Round(pn.St.ColumnPitch * 0.48 * s);
            var r = Rectangle.FromLTRB((int)colX - half - f.X0, pn.Yi(pn.St.LabelTop) - f.Y0, (int)colX + half - f.X0, pn.Yi(pn.St.LabelBottom) - f.Y0);
            r.Intersect(new Rectangle(0, 0, f.W, f.H));
            if (r.Width <= 0 || r.Height <= 0) return null;
            // ラベルがあるか（白以外の画素）
            bool any = false;
            for (int y = r.Top; y < r.Bottom && !any; y++)
                for (int x = r.Left; x < r.Right; x++)
                    if (!f.IsWhite(x + f.X0, y + f.Y0)) { any = true; break; }
            if (!any) return 0; // 0% のときはラベルが出ない
            using (var src = f.ToBitmap())
            {
                double sc = 40.0 / (11.0 * s);
                int W = (int)(r.Width * sc) + 80, H = (int)(r.Height * sc) + 40;
                using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, new RectangleF(40, 20, (float)(r.Width * sc), (float)(r.Height * sc)), r, GraphicsUnit.Pixel);
                    }
                    LabelContrast(bmp);
                    var words = WinOcr.Recognize(bmp);
                    string t = string.Concat(words.Select(w => w.Text)).Replace(" ", "")
                        .Replace('−', '-').Replace('－', '-').Replace('ー', '-').Replace('＋', '+').Replace('O', '0').Replace('o', '0')
                        .Replace('l', '1').Replace('I', '1').Replace('％', '%');
                    var m = System.Text.RegularExpressions.Regex.Match(t, @"([+\-]?)(\d{1,3})");
                    if (!m.Success) return null;
                    int v = int.Parse(m.Groups[2].Value);
                    if (m.Groups[1].Value == "-") v = -v;
                    if (v < -50 || v > 100) return null;
                    return v;
                }
            }
        }

        static void LabelContrast(Bitmap bmp)
        {
            // ピンク色の文字 → 黒、白 → 白
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var px = new byte[data.Stride * bmp.Height];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            for (int i = 0; i < px.Length; i += 4)
            {
                int b = px[i], g = px[i + 1], r = px[i + 2];
                int mn = Math.Min(r, Math.Min(g, b));
                int v = (mn - 150) * 255 / (245 - 150);
                if (v < 0) v = 0; if (v > 255) v = 255;
                px[i] = px[i + 1] = px[i + 2] = (byte)v; px[i + 3] = 255;
            }
            Marshal.Copy(px, 0, data.Scan0, px.Length);
            bmp.UnlockBits(data);
        }
    }
}
