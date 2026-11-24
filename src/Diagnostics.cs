using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

namespace VoicePeakAssist
{
    /// <summary>診断画像：ツールが画面をどう認識したかを書き込んだ画像を保存する。</summary>
    internal static class Diagnostics
    {
        public static Action<string, bool> Notify = delegate { };

        static VpWindow FindAny(Settings st)
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.ProcessName.IndexOf(st.ProcessName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    var w = VpWindow.FromHandle(p.MainWindowHandle, st);
                    if (w != null) return w;
                }
                catch { }
            }
            return null;
        }

        public static void Save(Settings st, VpWindow w)
        {
            bool front = w != null;
            if (w == null) w = FindAny(st);
            if (w == null) { Notify("VOICEPEAK のウィンドウが見つかりません。", true); return; }
            var info = new StringBuilder();
            info.AppendLine("ウィンドウ: class=" + w.ClassName + " pid=" + w.Pid + " client=" + w.Client + " scale=" + w.Scale + " awareness=" + w.Awareness + (front ? "" : "（前面ではありません）"));
            var pn = Panel.Detect(w, st);
            info.AppendLine("パネル上端: y=" + pn.Top + (pn.TopDetected ? "（検出）" : "（推定）") + " モード=" + pn.Mode + " アイコン濃さ=" + string.Join(",", pn.IconDark));

            var full = Frame.Capture(w.Client);
            var content = pn.CaptureContent();
            var dots = Detect.Dots(content, pn);
            var boxes = Detect.Boxes(content, pn);
            var thumb = Detect.FindThumb(content, pn);
            info.AppendLine("点: " + string.Join(" ", dots));
            info.AppendLine("長さボックス: " + string.Join(" ", boxes));
            info.AppendLine("スクロールつまみ: " + (thumb.Found ? thumb.Left + "-" + thumb.Right : "なし"));

            var cols = boxes.Count > 0 ? boxes.Select(b => b.Center).ToList() : dots.Select(d => d.X).ToList();
            var cells = cols.Select(x => Detect.Cell(content, pn, x)).ToList();
            string ocrNote = "";
            try
            {
                if (WinOcr.Available) KanaReader.Read(content, pn, cells);
                else ocrNote = WinOcr.Error;
            }
            catch (Exception ex) { ocrNote = "OCRエラー: " + ex.Message; }
            info.AppendLine("文字: " + string.Join(" ", cells.Select(c =>
                (c.Ocr.Length > 0 ? c.Ocr : "?") + (c.Small ? "(小)" : "") + (c.Wide ? "(幅広)" : "") + (KanaReader.IsTarget(c) ? "*" : ""))) + " " + ocrNote);

            using (var bmp = full.ToBitmap())
            {
                using (var g = Graphics.FromImage(bmp))
                using (var font = new Font("Meiryo UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    int ox = w.Client.Left, oy = w.Client.Top;
                    Func<double, float> X = v => (float)(v - ox);
                    Func<double, float> Y = v => (float)(v - oy);
                    var red = new Pen(Color.Red, 1);
                    g.DrawLine(red, 0, Y(pn.Top), bmp.Width, Y(pn.Top));
                    g.DrawLine(new Pen(Color.Orange, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }, X(pn.ContentLeft), Y(pn.Top), X(pn.ContentLeft), bmp.Height);
                    foreach (var m in new[] { PanelMode.Accent, PanelMode.Intonation, PanelMode.Length })
                    {
                        var c = pn.IconCenter(m);
                        float h = (float)(18 * pn.S);
                        g.DrawRectangle(m == pn.Mode ? new Pen(Color.Lime, 2) : red, X(c.X) - h, Y(c.Y) - h, 2 * h, 2 * h);
                    }
                    var blue = new Pen(Color.Blue, 2);
                    foreach (var d in dots) g.DrawEllipse(blue, X(d.X) - 6, Y(d.Y) - 6, 12, 12);
                    g.DrawLine(new Pen(Color.Blue, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot }, 0, Y(pn.Y(st.IntonationTopY)), bmp.Width, Y(pn.Y(st.IntonationTopY)));
                    g.DrawLine(new Pen(Color.Blue, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot }, 0, Y(pn.Y(st.IntonationBottomY)), bmp.Width, Y(pn.Y(st.IntonationBottomY)));
                    var green = new Pen(Color.Green, 2);
                    foreach (var b in boxes)
                    {
                        g.DrawRectangle(green, X(b.Left), Y(pn.Y(st.BoxTopY)), b.Width - 1, (float)((st.BoxBottomY - st.BoxTopY) * pn.S));
                        g.DrawString(string.Format("{0:+0;-0;0}", b.Value), font, Brushes.Green, X(b.Left), Y(pn.Y(st.BoxMidY)) - 6);
                    }
                    if (thumb.Found) g.DrawRectangle(new Pen(Color.Orange, 2), X(thumb.Left), Y(thumb.Y) - 4, thumb.Width, 8);
                    foreach (var c in cells)
                    {
                        if (c.HasInk) g.DrawRectangle(new Pen(KanaReader.IsTarget(c) ? Color.Magenta : Color.Gray, 1), X(c.Ink.Left), Y(c.Ink.Top), c.Ink.Width, c.Ink.Height);
                        string t = (c.Ocr.Length > 0 ? c.Ocr : "?") + (c.Small ? "小" : "") + (c.Wide ? "幅" : "");
                        g.FillRectangle(Brushes.White, X(c.ColumnX) - 12, Y(pn.Y(st.KanaTop)) - 13, 24, 12);
                        g.DrawString(t, font, KanaReader.IsTarget(c) ? Brushes.Magenta : Brushes.Black, X(c.ColumnX) - 11, Y(pn.Y(st.KanaTop)) - 13);
                    }
                }
                string dir = Path.Combine(Settings.Dir, "diag");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string png = Path.Combine(dir, "diag_" + stamp + ".png");
                string raw = Path.Combine(dir, "raw_" + stamp + ".png");
                bmp.Save(png, ImageFormat.Png);
                using (var rb = full.ToBitmap()) rb.Save(raw, ImageFormat.Png);
                File.WriteAllText(Path.Combine(dir, "diag_" + stamp + ".txt"), info.ToString(), new UTF8Encoding(true));
                Log.Write("診断:\r\n" + info);
                try { Process.Start("explorer.exe", "/select,\"" + png + "\""); } catch { }
                Notify("診断画像を保存しました（diag フォルダ）。", false);
            }
        }
    }
}
