using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;

namespace VoicePeakAssist
{
    internal abstract class Job { }

    internal sealed class WheelJob : Job
    {
        public Point Pt;
        public double Notches;   // 上 = 正
        public bool Fine;        // 小移動
        public bool Word;        // 単語全体
    }

    internal sealed class BulkJob : Job { }
    internal sealed class ExtremeJob : Job { public Point Pt; public bool Max; }
    internal sealed class ScrollJob : Job { public int Delta; }
    internal sealed class CalibrateJob : Job { }
    internal sealed class DiagJob : Job { }

    /// <summary>重い処理（画面の読み取りと入力の送信）を別スレッドで順番に実行する。</summary>
    internal sealed class Worker
    {
        readonly Settings st;
        readonly InputSender input;
        readonly Action<string, bool> notify;
        readonly Queue<Job> queue = new Queue<Job>();
        readonly object qLock = new object();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly Thread thread;
        volatile bool stop;
        public volatile bool BulkRunning;

        // 連続操作の状態
        double lastIntonX = double.NaN, lastIntonF; DateTime lastIntonTime;
        double lastLenX = double.NaN, lastLenCarry; DateTime lastLenTime;

        public Worker(Settings st, InputSender input, Action<string, bool> notify)
        {
            this.st = st; this.input = input; this.notify = notify; st0 = st;
            thread = new Thread(Loop) { IsBackground = true, Name = "VPAssistWorker" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Enqueue(Job j)
        {
            lock (qLock) { queue.Enqueue(j); }
            signal.Set();
        }

        public void Stop() { stop = true; signal.Set(); }

        void Loop()
        {
            while (!stop)
            {
                signal.WaitOne(500);
                while (!stop)
                {
                    Job j;
                    lock (qLock)
                    {
                        if (queue.Count == 0) break;
                        j = queue.Dequeue();
                        var wj = j as WheelJob;
                        if (wj != null)
                        {
                            // 同じ列への連続したホイールはまとめる
                            while (queue.Count > 0)
                            {
                                var n = queue.Peek() as WheelJob;
                                if (n == null || n.Fine != wj.Fine || n.Word != wj.Word || Math.Abs(n.Pt.X - wj.Pt.X) > 12) break;
                                queue.Dequeue();
                                wj = new WheelJob { Pt = n.Pt, Fine = wj.Fine, Word = wj.Word, Notches = wj.Notches + n.Notches };
                            }
                            j = wj;
                        }
                    }
                    try { Run(j); }
                    catch (Exception ex) { Log.Write("処理中のエラー: " + ex); }
                }
            }
        }

        void Run(Job j)
        {
            if (j is DiagJob || j is CalibrateJob) Thread.Sleep(3000); // トレイメニューから VOICEPEAK に戻る時間
            var w = VpWindow.FromForeground(st);
            if (j is DiagJob) { Diagnostics.Save(st, w); return; }
            if (w == null) { if (j is BulkJob || j is CalibrateJob) notify("VOICEPEAK が前面にありません。", true); return; }
            var pn = Panel.Detect(w, st);
            if (j is ScrollJob) { input.ScrollWithoutModifiers(((ScrollJob)j).Delta); return; }
            if (pn == null) return;
            if (j is WheelJob) Wheel(pn, (WheelJob)j);
            else if (j is ExtremeJob) Extreme(pn, (ExtremeJob)j);
            else if (j is BulkJob) { BulkRunning = true; try { Bulk(pn); } finally { BulkRunning = false; } }
            else if (j is CalibrateJob) CalibrateAtCursor(pn);
        }

        // ================= ホイール =================

        void Wheel(Panel pn, WheelJob j)
        {
            double sign = j.Notches * (st.InvertWheel ? -1 : 1);
            if (j.Word)
            {
                if (pn.Mode == PanelMode.Intonation) WordIntonation(pn, j.Pt, st.IntonationBigStep * sign);
                else if (pn.Mode == PanelMode.Length) WordLength(pn, j.Pt, st.LengthBigStep * sign);
                return;
            }
            if (pn.Mode == PanelMode.Intonation) Intonation(pn, j.Pt, (j.Fine ? st.IntonationSmallStep : st.IntonationBigStep) * sign);
            else if (pn.Mode == PanelMode.Length) Length(pn, j.Pt, (j.Fine ? st.LengthSmallStep : st.LengthBigStep) * sign);
        }

        void Intonation(Panel pn, Point pt, double stepPercent)
        {
            wordCacheL = int.MinValue;
            var f = pn.CaptureContent();
            if (f == null) return;
            var dots = Detect.Dots(f, pn);
            int i = Detect.Nearest(dots, d => d.X, pt.X, st.ColumnPitch * 0.5 * pn.S);
            if (i < 0) { Log.Debug("イントネーション: カーソルの近くに点がありません"); return; }
            var dot = dots[i];
            double top = pn.Y(st.IntonationTopY), bottom = pn.Y(st.IntonationBottomY);
            double fr;
            if (!double.IsNaN(lastIntonX) && Math.Abs(lastIntonX - dot.X) <= 3 && (DateTime.Now - lastIntonTime).TotalMilliseconds < 600)
                fr = lastIntonF;
            else
                fr = (bottom - dot.Y) / (bottom - top);
            fr = Clamp01(fr);
            double nf = Clamp01(fr + stepPercent / 100.0);
            double y = bottom - nf * (bottom - top);
            var p = new Point((int)Math.Round(dot.X), (int)Math.Round(y));
            ClickIntonation(pn, new List<Point> { p });
            lastIntonX = dot.X; lastIntonF = nf; lastIntonTime = DateTime.Now;
            Log.Debug(string.Format("イントネーション: 点{0} {1:0.0}% -> {2:0.0}%", dot, fr * 100, nf * 100));
        }

        /// <summary>長さボックスを読むための取り込み。カーソルが乗ったボックスは見た目が変わるので、先にホバーを外す。</summary>
        Frame CaptureLen(Panel pn)
        {
            if (st.Input == InputMethod.Message)
            {
                input.Hover(pn.Win, new Point(pn.Win.Client.Left + (int)Math.Round(6 * pn.S), pn.Bottom - (int)Math.Round(6 * pn.S)));
                Thread.Sleep(40);
            }
            return pn.CaptureContent();
        }

        bool Calibrated { get { return st.LengthPctPerPxRight != 0 && st.LengthPctPerPxLeft != 0; } }

        /// <summary>change% 変えるためのドラッグ量（右が正の px）。変えられなければ 0。</summary>
        int PlanDrag(double change, double carryPx, out double exactPx)
        {
            exactPx = 0;
            double nR = st.LengthPctPerPxRight != 0 ? change / st.LengthPctPerPxRight : -1;   // 右へ nR px
            double nL = st.LengthPctPerPxLeft != 0 ? change / st.LengthPctPerPxLeft : -1;     // 左へ nL px
            double best;
            if (nR > 0 && (nL <= 0 || nR <= nL)) best = nR;
            else if (nL > 0) best = -nL;
            else return 0;
            exactPx = best + carryPx;
            return (int)Math.Round(exactPx, MidpointRounding.AwayFromZero);
        }

        /// <summary>カーソル下の文字を最大値・最小値にする。</summary>
        void Extreme(Panel pn, ExtremeJob j)
        {
            wordCacheL = int.MinValue;
            if (pn.Mode == PanelMode.Intonation)
            {
                var f = pn.CaptureContent();
                if (f == null) return;
                var dots = Detect.Dots(f, pn);
                int i = Detect.Nearest(dots, d => d.X, j.Pt.X, st.ColumnPitch * 0.5 * pn.S);
                if (i < 0) return;
                double y = pn.Y(j.Max ? st.IntonationTopY : st.IntonationBottomY);
                ClickIntonation(pn, new List<Point> { new Point((int)Math.Round(dots[i].X), (int)Math.Round(y)) });
                lastIntonX = dots[i].X; lastIntonF = j.Max ? 1 : 0; lastIntonTime = DateTime.Now;
                Log.Write("イントネーション: " + dots[i] + " を" + (j.Max ? "最大" : "最小") + "に");
            }
            else if (pn.Mode == PanelMode.Length)
            {
                var f = CaptureLen(pn);
                if (f == null) return;
                var boxes = Detect.Boxes(f, pn);
                int i = Detect.Nearest(boxes, b => b.Center, j.Pt.X, st.LengthHitWidth * 0.5 * pn.S + 1);
                if (i < 0) { input.RestoreHover(pn.Win); return; }
                int v = j.Max ? 100 : -50;
                SetValue(pn, boxes[i], v);
                lastLenX = boxes[i].Center; lastLenValue = v; lastLenTime = DateTime.Now;
                Log.Write("長さ: " + boxes[i] + " を " + v + " に");
                input.RestoreHover(pn.Win);
            }
        }

        double lastLenValue;

        /// <summary>長さ：今の値を読み取り、ボックス下の数値をクリックして新しい値を入力する。</summary>
        void Length(Panel pn, Point pt, double stepPercent)
        {
            wordCacheL = int.MinValue;
            var f = CaptureLen(pn);
            if (f == null) return;
            var boxes = Detect.Boxes(f, pn);
            int i = Detect.Nearest(boxes, b => b.Center, pt.X, st.LengthHitWidth * 0.5 * pn.S + 1);
            if (i < 0) { Log.Write("長さ: カーソルの近くにボックスが見つかりません（" + boxes.Count + "個検出）"); input.RestoreHover(pn.Win); return; }
            var box = boxes[i];

            double cur; string src;
            if (!double.IsNaN(lastLenX) && Math.Abs(lastLenX - box.Center) <= 3 && (DateTime.Now - lastLenTime).TotalSeconds < 3
                && Math.Abs(lastLenValue - box.Value) <= 7)
            { cur = lastLenValue; src = "前回の入力"; }
            else cur = ReadValue(pn, box, out src);

            int target = (int)Math.Round(cur + stepPercent);
            if (target > 100) target = 100;
            if (target < -50) target = -50;
            if (target == (int)Math.Round(cur)) { input.RestoreHover(pn.Win); return; }
            SetValue(pn, box, target);
            lastLenX = box.Center; lastLenValue = target; lastLenTime = DateTime.Now;

            var after = FindBox(pn, box.Center);
            Log.Write(string.Format("長さ: {0} 今の値 {1}（{2}）→ {3} を入力 → 幅から {4}", box, cur, src, target,
                after.HasValue ? after.Value.Value.ToString("+0;-0;0") : "?"));
            input.RestoreHover(pn.Win);
        }

        /// <summary>ボックスの今の値（%）。ボックスの下の数値を読み、読めなければ幅から求める。</summary>
        double ReadValue(Panel pn, LenBox box, out string src)
        {
            // カーソルを乗せると数値（±0% も）が表示される
            input.Hover(pn.Win, new Point((int)Math.Round(box.Center), pn.Yi(st.BoxScanY)));
            Thread.Sleep(70);
            var f = pn.CaptureContent();
            var boxes = Detect.Boxes(f, pn);
            int i = Detect.Nearest(boxes, b => b.Center, box.Center, 8 * pn.S);
            double wv = i >= 0 ? boxes[i].Value : box.Value;
            int? lbl = null;
            try { lbl = KanaReader.ReadLabel(f, pn, box.Center); } catch (Exception ex) { Log.Write("数値の読み取りエラー: " + ex.Message); }
            if (lbl.HasValue && Math.Abs(lbl.Value - wv) <= 7) { src = "数値"; return lbl.Value; }
            src = lbl.HasValue ? "幅（数値 " + lbl.Value + " と食い違い）" : "幅";
            return wv;
        }

        /// <summary>ボックスの下の数値をクリックして value を入力し、Enter で確定する。</summary>
        void SetValue(Panel pn, LenBox box, int value)
        {
            // 操作している間は、手で押している Shift/Ctrl を無効にする
            input.BeginModifierBlock();
            try { SetValueCore(pn, box, value, true); }
            finally { input.EndModifierBlock(); }
        }

        void SetValueCore(Panel pn, LenBox box, int value, bool verify)
        {
            var w = pn.Win;
            int cx = (int)Math.Round(box.Center);
            if (verify)
            {
                input.Hover(w, new Point(cx, pn.Yi(st.BoxScanY)));
                Thread.Sleep(40);
            }
            input.Hover(w, new Point(cx, pn.Yi(st.LabelY)));
            Thread.Sleep(30);
            input.Click(w, new Point(cx, pn.Yi(st.LabelY)));
            Thread.Sleep((int)st.EditorOpenWaitMs);
            input.TypeText(w, value.ToString(System.Globalization.CultureInfo.InvariantCulture), true);
            if (!verify) { Thread.Sleep(30); return; }
            Thread.Sleep(80);
            // 確定したか確かめる（幅から求めた値が大きく違えば、空いている所をクリックして入力欄を閉じる）
            var chk = FindBox(pn, box.Center);
            if (chk.HasValue && Math.Abs(chk.Value.Value - value) > 6)
            {
                Log.Write(string.Format("長さ: {0} を入力後も幅が {1} のため、入力欄の外をクリックして確定します", value, chk.Value.Value));
                input.Click(w, new Point(pn.Win.Client.Left + (int)Math.Round(6 * pn.S), pn.Bottom - (int)Math.Round(6 * pn.S)));
                Thread.Sleep(80);
            }
        }

        static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        /// <summary>イントネーションの点をクリックする。手で押している修飾キーを外し、「押す・離す」をまとめて送る。</summary>
        void ClickIntonation(Panel pn, List<Point> points)
        {
            if (points.Count == 0) return;
            bool mods = Native.KeyDown(Native.VK_SHIFT) || Native.KeyDown(Native.VK_CONTROL) || Native.KeyDown(Native.VK_MENU);
            if (mods) { input.BeginModifierBlock(); Thread.Sleep(20); } // 離した信号が先に処理されるよう少し待つ
            try { input.ClickMany(pn.Win, points); }
            finally { if (mods) input.EndModifierBlock(); }
        }

        // ================= 単語全体の移動 =================

        // 連続操作のための単語ごとの値の記憶（列のX → 値）
        Dictionary<int, double> wordCache = new Dictionary<int, double>();
        int wordCacheL = int.MinValue, wordCacheR; PanelMode wordCacheMode; DateTime wordCacheTime;

        bool WordCacheValid(int l, int r, PanelMode m, double seconds)
        {
            return l == wordCacheL && r == wordCacheR && m == wordCacheMode && (DateTime.Now - wordCacheTime).TotalSeconds < seconds;
        }

        static int Key(double x) { return (int)Math.Round(x); }

        bool CacheGet(double x, out double v)
        {
            foreach (var kv in wordCache) if (Math.Abs(kv.Key - x) <= 3) { v = kv.Value; return true; }
            v = 0; return false;
        }

        /// <summary>イントネーション：単語の各点を同じ量ずつ上下させる（端に当たった点だけ止まる）。</summary>
        void WordIntonation(Panel pn, Point pt, double stepPercent)
        {
            var f = pn.CaptureContent();
            if (f == null) return;
            int L, R;
            if (!Detect.BlockAt(f, pn, pt.X, out L, out R)) { Log.Write("単語: 単語の範囲が見つかりません"); return; }
            var dots = Detect.Dots(f, pn).Where(d => d.X > L && d.X < R).OrderBy(d => d.X).ToList();
            if (dots.Count == 0) return;
            double top = pn.Y(st.IntonationTopY), bottom = pn.Y(st.IntonationBottomY);
            bool cached = WordCacheValid(L, R, PanelMode.Intonation, 0.8);
            var next = new Dictionary<int, double>();
            var parts = new List<string>();
            var clicks = new List<Point>();
            foreach (var d in dots)
            {
                double fr;
                if (!(cached && CacheGet(d.X, out fr))) fr = Clamp01((bottom - d.Y) / (bottom - top));
                double nf = Clamp01(fr + stepPercent / 100.0);
                next[Key(d.X)] = nf;
                if (Math.Abs(nf - fr) < 1e-6) continue;
                clicks.Add(new Point((int)Math.Round(d.X), (int)Math.Round(bottom - nf * (bottom - top))));
                parts.Add(string.Format("{0:0}→{1:0}", fr * 100, nf * 100));
            }
            ClickIntonation(pn, clicks);
            wordCache = next; wordCacheL = L; wordCacheR = R; wordCacheMode = PanelMode.Intonation; wordCacheTime = DateTime.Now;
            lastIntonX = double.NaN;
            Log.Write(string.Format("単語（イントネーション）: {0}-{1} {2:+0.#;-0.#}% [{3}]", L, R, stepPercent, string.Join(" ", parts)));
        }

        /// <summary>長さ：単語の各文字に同じ%を足す（端に当たった文字だけ止まる）。</summary>
        void WordLength(Panel pn, Point pt, double stepPercent)
        {
            var f = CaptureLen(pn);
            if (f == null) return;
            int L, R;
            if (!Detect.BlockAt(f, pn, pt.X, out L, out R)) { Log.Write("単語: 単語の範囲が見つかりません"); input.RestoreHover(pn.Win); return; }
            var boxes = Detect.Boxes(f, pn).Where(b => b.Center > L && b.Center < R).OrderBy(b => b.Center).ToList();
            if (boxes.Count == 0) { input.RestoreHover(pn.Win); return; }

            // 今の値：カーソルを単語に乗せると全文字の数値が出るので、1回の取り込みでまとめて読む
            var values = new Dictionary<int, double>();
            bool cached = WordCacheValid(L, R, PanelMode.Length, 3);
            Frame lf = null;
            foreach (var b in boxes)
            {
                double v;
                if (cached && CacheGet(b.Center, out v) && Math.Abs(v - b.Value) <= 7) { values[Key(b.Center)] = v; continue; }
                if (lf == null)
                {
                    input.Hover(pn.Win, new Point((int)Math.Round(boxes[0].Center), pn.Yi(st.BoxScanY)));
                    Thread.Sleep(70);
                    lf = pn.CaptureContent();
                }
                int? lbl = null;
                try { lbl = KanaReader.ReadLabel(lf, pn, b.Center); } catch (Exception ex) { Log.Write("数値の読み取りエラー: " + ex.Message); }
                values[Key(b.Center)] = (lbl.HasValue && Math.Abs(lbl.Value - b.Value) <= 7) ? lbl.Value : b.Value;
            }

            var next = new Dictionary<int, double>();
            var todo = new List<KeyValuePair<LenBox, int>>();
            var parts = new List<string>();
            foreach (var b in boxes)
            {
                double cur = values[Key(b.Center)];
                int target = (int)Math.Round(Math.Max(-50, Math.Min(100, cur + stepPercent)));
                next[Key(b.Center)] = target;
                if (target != (int)Math.Round(cur)) { todo.Add(new KeyValuePair<LenBox, int>(b, target)); parts.Add(cur + "→" + target); }
            }
            if (todo.Count > 0)
            {
                // 入力している間は Shift/Ctrl/Alt を無効にする（1文字ずつの確認は省き、最後にまとめて確かめる）
                input.BeginModifierBlock();
                try
                {
                    foreach (var t in todo) SetValueCore(pn, t.Key, t.Value, false);
                    Thread.Sleep(60);
                    var after = Detect.Boxes(CaptureLen(pn), pn);
                    foreach (var t in todo)
                    {
                        int i = Detect.Nearest(after, b => b.Center, t.Key.Center, 8 * pn.S);
                        if (i >= 0 && Math.Abs(after[i].Value - t.Value) > 6)
                        {
                            Log.Write(string.Format("単語（長さ）: {0} が {1} にならなかったため入力し直します", t.Key, t.Value));
                            SetValueCore(pn, t.Key, t.Value, true);
                        }
                    }
                }
                finally { input.EndModifierBlock(); }
            }
            wordCache = next; wordCacheL = L; wordCacheR = R; wordCacheMode = PanelMode.Length; wordCacheTime = DateTime.Now;
            lastLenX = double.NaN;
            Log.Write(string.Format("単語（長さ）: {0}-{1} {2:+0.#;-0.#}% [{3}]", L, R, stepPercent, string.Join(" ", parts)));
            input.RestoreHover(pn.Win);
        }

        int Detour(Panel pn) { return (int)Math.Round(st.DragDetour * pn.S); }

        /// <summary>ボックスの左半分をつかむ位置（左半分をつかむと右ドラッグで縮み、左ドラッグで伸びる）。</summary>
        static Point GrabLeft(Panel pn, LenBox b)
        {
            double off = Math.Max(2 * pn.S, b.Width * 0.25);
            int x = (int)Math.Round(b.Center - off);
            if (x < b.Left + 2) x = b.Left + 2;
            if (x >= b.Center) x = (int)Math.Floor(b.Center - 1);
            return new Point(x, pn.Yi(st0.BoxMidY));
        }


        // ================= 長さのドラッグ量の計測 =================

        void CalibrateAtCursor(Panel pn)
        {
            if (pn.Mode != PanelMode.Length && !SwitchToLength(pn)) return;
            Native.POINT cur; Native.GetCursorPos(out cur);
            var f = CaptureLen(pn);
            var boxes = Detect.Boxes(f, pn);
            if (boxes.Count == 0) { notify("長さボックスが見つかりません。", true); input.RestoreHover(pn.Win); return; }
            int i = Detect.Nearest(boxes, b => b.Center, cur.X, 1e9);
            Calibrate(pn, boxes[i]);
            input.RestoreHover(pn.Win);
        }

        double MeasureValue(Panel pn, double colX, out bool fromLabel)
        {
            fromLabel = false;
            Thread.Sleep(110);
            var f = CaptureLen(pn);
            var boxes = Detect.Boxes(f, pn);
            int i = Detect.Nearest(boxes, b => b.Center, colX, 8 * pn.S);
            if (i < 0) return double.NaN;
            int? lbl = null;
            try { lbl = KanaReader.ReadLabel(f, pn, boxes[i].Center); } catch (Exception ex) { Log.Write("ラベル読み取りエラー: " + ex.Message); }
            if (lbl.HasValue && Math.Abs(lbl.Value - boxes[i].Value) <= 6) { fromLabel = true; return lbl.Value; }
            return boxes[i].Value;
        }

        /// <summary>
        /// 長さボックスを右へ1pxドラッグしたときに変わる%（符号付き）を測る。
        /// VOICEPEAK は右ドラッグで縮む（負の値）が、向きも含めて実測する。
        /// </summary>
        /// <summary>
        /// ボックスの右半分をつかんでドラッグする位置。VOICEPEAK はつかんだ側の端を動かす
        /// （右半分をつかめば右ドラッグで伸び、左ドラッグで縮む）。中心ちょうどは向きが定まらないので避ける。
        /// </summary>
        static Point Grab(Panel pn, LenBox b)
        {
            double off = Math.Max(2 * pn.S, b.Width * 0.25);
            int x = (int)Math.Round(b.Center + off);
            if (x > b.Right - 2) x = b.Right - 2;
            if (x <= b.Center) x = (int)Math.Ceiling(b.Center + 1);
            return new Point(x, pn.Yi(st0.BoxMidY));
        }
        static Settings st0;

        LenBox? FindBox(Panel pn, double colX)
        {
            var f = CaptureLen(pn);
            var boxes = Detect.Boxes(f, pn);
            int i = Detect.Nearest(boxes, b => b.Center, colX, 8 * pn.S);
            if (i < 0) return null;
            return boxes[i];
        }

        /// <summary>ボックスの右半分をつかんで右／左へ動かしたときの1pxあたりの変化（%）を、向きごとに測る。</summary>
        bool Calibrate(Panel pn, LenBox box)
        {
            bool l0, l1, l2;
            double col = box.Center;
            double v0 = MeasureValue(pn, col, out l0);
            if (double.IsNaN(v0)) v0 = box.Value;
            double vStart = v0;
            int dir = (100 - v0) >= (v0 + 50) ? 1 : -1; // 余裕のある方へ
            int D = (int)Math.Round(8 * pn.S);
            var cur = FindBox(pn, col);
            if (cur == null) { notify("計測に失敗しました（ボックスを見失いました）。", true); return false; }
            var a = Grab(pn, cur.Value);
            input.DragHorizontal(pn.Win, a, dir * D, Detour(pn));
            double v1 = MeasureValue(pn, col, out l1);
            if (double.IsNaN(v1)) { notify("計測に失敗しました（ボックスを見失いました）。", true); return false; }
            var cb = FindBox(pn, col);
            if (cb == null) { notify("計測に失敗しました（ボックスを見失いました）。", true); return false; }
            var a2 = Grab(pn, cb.Value);
            input.DragHorizontal(pn.Win, a2, -dir * D, Detour(pn));
            double v2 = MeasureValue(pn, col, out l2);
            Log.Write(string.Format("長さ計測: {0}({1}) →{2:+0;-0}px→ {3}({4}) →{5:+0;-0}px→ {6}({7})",
                v0, l0 ? "ラベル" : "幅", dir * D, v1, l1 ? "ラベル" : "幅", -dir * D, v2, l2 ? "ラベル" : "幅"));
            if (double.IsNaN(v2)) { notify("計測に失敗しました（ボックスを見失いました）。", true); return false; }
            double k1 = (v1 - v0) / D;   // dir 方向へ1pxあたり
            double k2 = (v2 - v1) / D;   // 逆方向へ1pxあたり
            bool c1 = v1 >= 99 || v1 <= -49, c2 = v2 >= 99 || v2 <= -49;
            if (Math.Abs(k1) < 0.2 && Math.Abs(k2) < 0.2)
            {
                notify("計測に失敗しました。ドラッグしても長さが変わりませんでした。入力の送り方を切り替えて試してください。", true);
                return false;
            }
            if (c1 && !c2) k1 = -k2; // 上限・下限に当たった方は、もう一方の値で代用
            if (c2 && !c1) k2 = -k1;
            double kR = dir > 0 ? k1 : k2, kL = dir > 0 ? k2 : k1;
            if (kR <= 0 || kL >= 0)
                Log.Write(string.Format("[注意] 想定と違う向き: 右 {0:0.##}%/px 左 {1:0.##}%/px", kR, kL));
            st.LengthPctPerPxRight = Math.Round(kR, 3);
            st.LengthPctPerPxLeft = Math.Round(kL, 3);
            st.Save();
            // 元の値に戻す
            if (Math.Abs(v2 - vStart) >= 3)
            {
                double ex; int fix = PlanDrag(vStart - v2, 0, out ex);
                var cb2 = FindBox(pn, col);
                if (fix != 0 && cb2 != null) input.DragHorizontal(pn.Win, Grab(pn, cb2.Value), fix, Detour(pn));
            }
            Log.Write(string.Format("長さ計測結果: 右へ1pxで {0:+0.##;-0.##}%、左へ1pxで {1:+0.##;-0.##}%", kR, kL));
            notify(string.Format("計測しました：右へ1pxで{0:+0.#;-0.#}%、左へ1pxで{1:+0.#;-0.#}%", kR, kL), false);
            return true;
        }

        // ================= 一括 -50% =================

        bool SwitchToLength(Panel pn)
        {
            input.Click(pn.Win, pn.IconCenter(PanelMode.Length));
            for (int t = 0; t < 10; t++)
            {
                Thread.Sleep(80);
                var p2 = Panel.Detect(pn.Win, st);
                if (p2 != null && p2.Mode == PanelMode.Length) { pn.Mode = PanelMode.Length; return true; }
            }
            notify("「長さ」の表示に切り替えられませんでした。", true);
            return false;
        }

        void Bulk(Panel pn)
        {
            wordCacheL = int.MinValue;
            if (!pn.TopDetected) Log.Write("一括: パネル上端を検出できず、既定の位置を使います");
            if (pn.Mode != PanelMode.Length)
            {
                if (!SwitchToLength(pn)) return;
                Thread.Sleep(120);
            }
            bool ocrOk = WinOcr.Available;
            if (!ocrOk) { notify(WinOcr.Error, true); return; }

            // 左端までスクロール
            var f = pn.CaptureContent();
            var thumb = Detect.FindThumb(f, pn);
            if (thumb.Found && thumb.Left > pn.ContentLeft + 4 * pn.S)
            {
                var a = new Point(thumb.Center, thumb.Y);
                input.Drag(pn.Win, a, new Point(a.X - (thumb.Left - pn.ContentLeft) - (int)(40 * pn.S), a.Y), 4);
                Thread.Sleep(200);
            }

            int changed = 0, failed = 0;
            bool unknown = false;
            var changedChars = new List<char>();
            int target = (int)Math.Round(Math.Max(-50, Math.Min(100, st.VoicelessLength)));
            double targetW = st.BoxBaseWidth * pn.S * (1 + target / 100.0);
            // 幅がほぼ目標どおりの列は、すでに設定済みとして飛ばす（スクロールで重なった列の二重処理も防ぐ）
            Func<LenBox, bool> done = b => Math.Abs(b.Width - targetW) <= 1.0 * pn.S;

            for (int pass = 0; pass < 60; pass++)
            {
                f = CaptureLen(pn);
                var boxes = Detect.Boxes(f, pn);
                var cells = boxes.Select(b => Detect.Cell(f, pn, b.Center)).ToList();
                var need = new List<Detect.KanaCell>();
                for (int i = 0; i < boxes.Count; i++) if (!done(boxes[i])) need.Add(cells[i]);
                KanaReader.Read(f, pn, need);

                for (int i = 0; i < boxes.Count; i++)
                {
                    var c = cells[i];
                    if (done(boxes[i])) continue;
                    if (c.HasInk && c.Char == '\0' && !c.Wide && !c.Small) { unknown = true; continue; }
                    if (!KanaReader.IsTarget(c)) continue;
                    if (SetTo(pn, boxes[i], target)) { changed++; changedChars.Add(c.Char); }
                    else failed++;
                }

                // 次の範囲へスクロール
                thumb = Detect.FindThumb(pn.CaptureContent(), pn);
                if (!thumb.Found) break;
                int move = Math.Max((int)(10 * pn.S), (int)(thumb.Width * 0.8));
                var p0 = new Point(thumb.Center, thumb.Y);
                input.Drag(pn.Win, p0, new Point(p0.X + move, p0.Y), 4);
                Thread.Sleep(200);
                var t2 = Detect.FindThumb(pn.CaptureContent(), pn);
                if (!t2.Found || Math.Abs(t2.Left - thumb.Left) <= 1) break;
            }

            string msg = changed > 0
                ? string.Format("{0}文字の長さを{1:+0;-0;0}%にしました（{2}）。", changed, target, new string(changedChars.Take(30).ToArray()))
                : "対象の文字はありませんでした。";
            if (failed > 0) msg += string.Format(" {0}文字は変更できませんでした。", failed);
            if (unknown) msg += " 読み取れなかった文字があります。";
            input.RestoreHover(pn.Win);
            notify(msg, failed > 0 || unknown);
            if (unknown) Log.Unread("一括: " + msg); else Log.Write("一括: " + msg);
        }

        /// <summary>数値入力で value にする。-50 で入力が効かなかったときはドラッグで縮める。</summary>
        bool SetTo(Panel pn, LenBox box, int value)
        {
            SetValue(pn, box, value);
            var chk = FindBox(pn, box.Center);
            double expectW = st.BoxBaseWidth * pn.S * (1 + value / 100.0);
            if (chk.HasValue && Math.Abs(chk.Value.Width - expectW) <= 2 * pn.S) return true;
            if (value > -50) { Log.Write("一括: " + box + " を " + value + " にできませんでした"); return false; }
            int minWidthPx = (int)Math.Ceiling((st.BoxBaseWidth * 0.5 + 2.5) * pn.S);
            return ShrinkToMin(pn, chk.HasValue ? chk.Value : box, minWidthPx);
        }

        bool ShrinkToMin(Panel pn, LenBox box, int minWidthPx)
        {
            Log.Write("一括: 数値入力で -50% にならなかったため、ドラッグで縮めます " + box);
            double cx = box.Center;
            double v = box.Value;
            var cur = box;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int px;
                double ex;
                if (Calibrated) px = PlanDrag(-(v + 50 + 15), 0, out ex);
                else px = -(int)(200 * pn.S); // 右半分をつかんで左へ動かすと縮む
                if (Math.Abs(px) < (int)(30 * pn.S)) px = Math.Sign(px == 0 ? -1 : px) * (int)(30 * pn.S);
                var a = Grab(pn, cur);
                int endX = Math.Max(pn.Win.Client.Left + 2, Math.Min(pn.Win.Client.Right - 2, a.X + px));
                input.DragHorizontal(pn.Win, a, endX - a.X, Detour(pn));
                Thread.Sleep(60);
                var f = CaptureLen(pn);
                var boxes = Detect.Boxes(f, pn);
                int i = Detect.Nearest(boxes, b => b.Center, cx, 8 * pn.S);
                if (i < 0) return false;
                if (boxes[i].Width <= minWidthPx) return true;
                cur = boxes[i]; v = cur.Value; cx = cur.Center;
            }
            return false;
        }
    }
}
