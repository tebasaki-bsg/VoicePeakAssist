using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VoicePeakAssist
{
    /// <summary>設定画面。</summary>
    internal sealed class SettingsForm : Form
    {
        readonly Settings st;
        readonly Action onApplied;

        CheckBox chkAutoScale;
        NumericUpDown numScale;
        readonly RadioButton[,] radios = new RadioButton[4, 4];
        readonly int[] assign = new int[4]; // 行（Shift/Ctrl/Alt/キーなし）→ 列（大/小/横スクロール/単語全体）
        NumericUpDown numIntBig, numIntSmall, numLenBig, numLenSmall, numEditorWait, numKeyGap;
        GroupBox grpAdvanced;
        TextBox txtVoiceless;
        ComboBox cmbLog;
        readonly TextBox[] keyBoxes = new TextBox[3];
        readonly KeyCombo[] keyCombos = new KeyCombo[3]; // 0=一括-50% 1=最大 2=最小
        Button btnAdvanced;

        static readonly string[] RowNames = { "Shift＋ホイール", "Ctrl＋ホイール", "Alt＋ホイール", "ホイール（キーなし）" };
        static readonly string[] ColNames = { "大移動", "小移動", "横スクロール", "単語全体" };
        static readonly string[] ColKeys = { Settings.Big, Settings.Small, Settings.Scroll, Settings.Word };

        public SettingsForm(Settings st, Action onApplied)
        {
            this.st = st;
            this.onApplied = onApplied;

            Text = "VoicePeak 発音調整アシスト - 設定";
            Font = new Font("Yu Gothic UI", 9f);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ShowInTaskbar = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var root = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10),
                Location = new Point(0, 0)
            };
            Controls.Add(root);

            root.Controls.Add(BuildScaleGroup());
            root.Controls.Add(BuildWheelGroup());
            root.Controls.Add(BuildStepGroup("イントネーション", out numIntBig, out numIntSmall, 1, 0.1m, 100m));
            root.Controls.Add(BuildStepGroup("長さ", out numLenBig, out numLenSmall, 0, 1m, 150m));
            root.Controls.Add(BuildVoicelessGroup());
            root.Controls.Add(BuildHotkeyGroup());

            btnAdvanced = new Button { Text = "詳細オプション ▼", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            btnAdvanced.Click += (s, e) => ToggleAdvanced();
            root.Controls.Add(btnAdvanced);
            grpAdvanced = BuildAdvancedGroup();
            grpAdvanced.Visible = false;
            root.Controls.Add(grpAdvanced);

            root.Controls.Add(BuildButtons());

            LoadValues();
        }

        // ---------- 各グループ ----------

        GroupBox Group(string title)
        {
            return new GroupBox
            {
                Text = title,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 6, 8, 8),
                Margin = new Padding(3, 3, 3, 6),
                MinimumSize = new Size(404, 0)
            };
        }

        static TableLayoutPanel Table(int cols)
        {
            return new TableLayoutPanel
            {
                ColumnCount = cols,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Location = new Point(8, 22),
                Padding = new Padding(0, 0, 0, 0)
            };
        }

        /// <summary>表と注記を縦に並べてグループに入れる（注記は表の列幅で折り返さないよう表の外に置く）。</summary>
        static void Body(GroupBox g, params Control[] items)
        {
            var f = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Location = new Point(8, 22),
                Margin = new Padding(0)
            };
            foreach (var c in items) f.Controls.Add(c);
            g.Controls.Add(f);
        }

        static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 4, 3, 0) };
        }

        static Label Lbl(string text, ContentAlignment align = ContentAlignment.MiddleLeft)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = align, Margin = new Padding(3, 6, 3, 3) };
        }

        static NumericUpDown Num(int decimals, decimal min, decimal max, decimal inc)
        {
            return new NumericUpDown { DecimalPlaces = decimals, Minimum = min, Maximum = max, Increment = inc, Width = 70, TextAlign = HorizontalAlignment.Right };
        }

        GroupBox BuildScaleGroup()
        {
            var g = Group("UIスケール（VOICEPEAK の画面の拡大率）");
            var t = Table(4);
            chkAutoScale = new CheckBox { Text = "自動で測る", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 5, 12, 3) };
            numScale = Num(0, 50, 300, 5);
            chkAutoScale.CheckedChanged += (s, e) => numScale.Enabled = !chkAutoScale.Checked;
            t.Controls.Add(chkAutoScale, 0, 0);
            t.Controls.Add(Lbl("手動:"), 1, 0);
            t.Controls.Add(numScale, 2, 0);
            t.Controls.Add(Lbl("%"), 3, 0);
            g.Controls.Add(t);
            return g;
        }

        GroupBox BuildWheelGroup()
        {
            var g = Group("ホイールの割り当て");
            var t = Table(5);
            t.Location = new Point(0, 0);
            t.Controls.Add(new Label { AutoSize = true }, 0, 0);
            for (int c = 0; c < 4; c++)
            {
                var h = new Label { Text = ColNames[c], AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(6, 3, 6, 3) };
                t.Controls.Add(h, c + 1, 0);
            }
            for (int r = 0; r < 4; r++)
            {
                t.Controls.Add(Lbl(RowNames[r]), 0, r + 1);
                for (int c = 0; c < 4; c++)
                {
                    // 行・列の両方で1つだけ選べるよう、自動チェックは使わず自分で管理する
                    var rb = new RadioButton { AutoCheck = false, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(3, 4, 3, 4) };
                    int rr = r, cc = c;
                    rb.Click += (s, e) => Assign(rr, cc);
                    radios[r, c] = rb;
                    t.Controls.Add(rb, c + 1, r + 1);
                }
            }
            Body(g, t);
            return g;
        }

        GroupBox BuildStepGroup(string title, out NumericUpDown big, out NumericUpDown small, int decimals, decimal min, decimal max)
        {
            var g = Group(title + "の移動間隔");
            var t = Table(6);
            big = Num(decimals, min, max, decimals > 0 ? 0.5m : 1m);
            small = Num(decimals, min, max, decimals > 0 ? 0.5m : 1m);
            t.Controls.Add(Lbl("大移動"), 0, 0);
            t.Controls.Add(big, 1, 0);
            t.Controls.Add(Lbl("%"), 2, 0);
            var l2 = Lbl("小移動"); l2.Margin = new Padding(20, 6, 3, 3);
            t.Controls.Add(l2, 3, 0);
            t.Controls.Add(small, 4, 0);
            t.Controls.Add(Lbl("%"), 5, 0);
            g.Controls.Add(t);
            return g;
        }

        GroupBox BuildVoicelessGroup()
        {
            var g = Group("無声音（カ/サ/タ/ハ/パ行）の長さ自動設定");
            var t = Table(4);
            t.Location = new Point(0, 0);
            txtVoiceless = new TextBox { Width = 70, TextAlign = HorizontalAlignment.Right, Anchor = AnchorStyles.Left };
            txtVoiceless.Leave += (s, e) =>
            {
                int v;
                if (TryParsePercent(txtVoiceless.Text, out v)) txtVoiceless.Text = FormatPercent(v);
            };
            t.Controls.Add(Lbl("設定する長さ"), 0, 0);
            t.Controls.Add(txtVoiceless, 1, 0);
            t.Controls.Add(Lbl("%"), 2, 0);
            t.Controls.Add(Lbl("（-50 ～ +100）"), 3, 0);
            Body(g, t);
            return g;
        }

        /// <summary>「-35」「+20」「35%」「+0」「-0」「0」などを受け付ける（-50〜+100）。</summary>
        static bool TryParsePercent(string text, out int v)
        {
            v = 0;
            if (text == null) return false;
            string t = text.Trim().Replace("％", "%").Replace("＋", "+").Replace("－", "-").Replace("−", "-").Replace("ー", "-").TrimEnd('%').Trim();
            // 全角数字も受け付ける
            var sb = new System.Text.StringBuilder();
            foreach (char c in t) sb.Append(c >= '０' && c <= '９' ? (char)('0' + (c - '０')) : c);
            t = sb.ToString();
            if (!System.Text.RegularExpressions.Regex.IsMatch(t, @"^[+\-]?\d{1,3}$")) return false;
            v = int.Parse(t.TrimStart('+'), System.Globalization.CultureInfo.InvariantCulture);
            if (t.StartsWith("-")) v = -Math.Abs(v);
            return v >= -50 && v <= 100;
        }

        static string FormatPercent(int v)
        {
            return v > 0 ? "+" + v : v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        static readonly string[] KeyRowNames = { "無声音（カ/サ/タ/ハ/パ行）の長さを自動設定", "数値を最大に", "数値を最小に" };

        GroupBox BuildHotkeyGroup()
        {
            var g = Group("キー操作");
            var t = Table(2);
            t.Location = new Point(0, 0);
            for (int i = 0; i < 3; i++)
            {
                t.Controls.Add(Lbl(KeyRowNames[i]), 0, i);
                var tb = new TextBox { ReadOnly = true, Width = 130, BackColor = SystemColors.Window, Cursor = Cursors.Hand, TextAlign = HorizontalAlignment.Center };
                int idx = i;
                tb.PreviewKeyDown += (s, e) => e.IsInputKey = true; // 矢印キーや Tab も登録できるように
                tb.KeyDown += (s, e) => CaptureKey(idx, e);
                // クリック（選択）したら元のキー表記を消して入力待ちにし、何も押さずに離れたら元に戻す
                tb.Enter += (s, e) => { tb.BackColor = Color.FromArgb(255, 248, 225); tb.Text = ""; };
                tb.Leave += (s, e) => { tb.BackColor = SystemColors.Window; RefreshKeys(); };
                keyBoxes[i] = tb;
                t.Controls.Add(tb, 1, i);
            }
            Body(g, t, Note("クリックしてキー入力で割り当て"));
            return g;
        }

        void CaptureKey(int idx, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (KeyCombo.IsModifierKey(e.KeyCode)) return; // 修飾キーだけでは登録しない
            var k = new KeyCombo { Ctrl = e.Control, Shift = e.Shift, Alt = e.Alt, Vk = (int)e.KeyCode };
            // 他の欄と同じなら入れ替える
            for (int i = 0; i < 3; i++)
                if (i != idx && keyCombos[i].SameAs(k)) keyCombos[i] = keyCombos[idx];
            keyCombos[idx] = k;
            RefreshKeys();
            if (keyBoxes[idx].Focused) keyBoxes[idx].Text = keyCombos[idx].Display();
        }

        void RefreshKeys()
        {
            for (int i = 0; i < 3; i++) keyBoxes[i].Text = keyCombos[i].Display();
        }

        void SetKeys(string bulk, string max, string min)
        {
            keyCombos[0] = KeyCombo.Parse(bulk);
            keyCombos[1] = KeyCombo.Parse(max);
            keyCombos[2] = KeyCombo.Parse(min);
            RefreshKeys();
        }

        GroupBox BuildAdvancedGroup()
        {
            var g = Group("詳細オプション");
            var t = Table(3);
            numEditorWait = Num(0, 0, 3000, 5);
            numKeyGap = Num(0, 0, 500, 1);
            t.Controls.Add(Lbl("入力欄が開くまでの待ち時間"), 0, 0);
            t.Controls.Add(numEditorWait, 1, 0);
            t.Controls.Add(Lbl("ms"), 2, 0);
            t.Controls.Add(Lbl("キー入力の間隔"), 0, 1);
            t.Controls.Add(numKeyGap, 1, 1);
            t.Controls.Add(Lbl("ms"), 2, 1);
            cmbLog = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70, Anchor = AnchorStyles.Left };
            cmbLog.Items.AddRange(new object[] { "なし", "最小", "中", "最大" });
            t.Controls.Add(Lbl("ログレベル"), 0, 2);
            t.Controls.Add(cmbLog, 1, 2);

            t.Location = new Point(0, 0);
            Body(g, t, Note("入力を取りこぼすときは、どちらかを大きくしてください。"));
            return g;
        }

        Control BuildButtons()
        {
            var p = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(3, 8, 3, 3),
                MinimumSize = new Size(416, 0)
            };
            // すべて「初期値に戻す」と同じ高さになるよう、自動サイズ＋最小幅で揃える
            var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(80, 0) };
            var cancel = new Button { Text = "キャンセル", AutoSize = true, MinimumSize = new Size(80, 0) };
            var apply = new Button { Text = "適用", AutoSize = true, MinimumSize = new Size(80, 0) };
            var reset = new Button { Text = "初期値に戻す", AutoSize = true, MinimumSize = new Size(80, 0) };
            ok.Click += (s, e) => { if (Apply()) Close(); };
            cancel.Click += (s, e) => Close();
            apply.Click += (s, e) => { Apply(); };
            reset.Click += (s, e) => ResetDefaults();
            p.Controls.Add(cancel);
            p.Controls.Add(ok);
            p.Controls.Add(apply);
            p.Controls.Add(reset);
            AcceptButton = ok;
            CancelButton = cancel;
            return p;
        }

        void ToggleAdvanced()
        {
            grpAdvanced.Visible = !grpAdvanced.Visible;
            btnAdvanced.Text = grpAdvanced.Visible ? "詳細オプション ▲" : "詳細オプション ▼";
        }

        // ---------- ホイールの割り当て ----------

        void Assign(int row, int col)
        {
            if (assign[row] == col) return;
            // 同じ操作を使っている行と入れ替える
            for (int r = 0; r < 4; r++)
                if (r != row && assign[r] == col) assign[r] = assign[row];
            assign[row] = col;
            RefreshRadios();
        }

        void RefreshRadios()
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    radios[r, c].Checked = assign[r] == c;
        }

        static int ColOf(string key)
        {
            int i = Array.IndexOf(ColKeys, key);
            return i < 0 ? 0 : i;
        }

        // ---------- 値の読み書き ----------

        static decimal Clamp(NumericUpDown n, double v)
        {
            decimal d = (decimal)v;
            if (d < n.Minimum) d = n.Minimum;
            if (d > n.Maximum) d = n.Maximum;
            return d;
        }

        void LoadValues()
        {
            chkAutoScale.Checked = st.UiScale <= 0;
            numScale.Value = Clamp(numScale, st.UiScale > 0 ? st.UiScale * 100 : 100);
            numScale.Enabled = !chkAutoScale.Checked;
            assign[0] = ColOf(st.WheelShift);
            assign[1] = ColOf(st.WheelCtrl);
            assign[2] = ColOf(st.WheelAlt);
            assign[3] = ColOf(st.WheelPlain);
            if (assign.Distinct().Count() != 4) { var d0 = new Settings(); assign[0] = ColOf(d0.WheelShift); assign[1] = ColOf(d0.WheelCtrl); assign[2] = ColOf(d0.WheelAlt); assign[3] = ColOf(d0.WheelPlain); }
            RefreshRadios();
            numIntBig.Value = Clamp(numIntBig, st.IntonationBigStep);
            numIntSmall.Value = Clamp(numIntSmall, st.IntonationSmallStep);
            numLenBig.Value = Clamp(numLenBig, st.LengthBigStep);
            numLenSmall.Value = Clamp(numLenSmall, st.LengthSmallStep);
            numEditorWait.Value = Clamp(numEditorWait, st.EditorOpenWaitMs);
            numKeyGap.Value = Clamp(numKeyGap, st.KeyGapMs);
            SetKeys(st.BulkHotkey, st.MaxKey, st.MinKey);
            txtVoiceless.Text = FormatPercent((int)Math.Round(st.VoicelessLength));
            cmbLog.SelectedIndex = (int)st.LogLevel;
        }

        void ResetDefaults()
        {
            var d = new Settings();
            chkAutoScale.Checked = true;
            numScale.Value = 100;
            assign[0] = ColOf(d.WheelShift); assign[1] = ColOf(d.WheelCtrl); assign[2] = ColOf(d.WheelAlt); assign[3] = ColOf(d.WheelPlain);
            RefreshRadios();
            numIntBig.Value = Clamp(numIntBig, d.IntonationBigStep);
            numIntSmall.Value = Clamp(numIntSmall, d.IntonationSmallStep);
            numLenBig.Value = Clamp(numLenBig, d.LengthBigStep);
            numLenSmall.Value = Clamp(numLenSmall, d.LengthSmallStep);
            numEditorWait.Value = Clamp(numEditorWait, d.EditorOpenWaitMs);
            numKeyGap.Value = Clamp(numKeyGap, d.KeyGapMs);
            SetKeys(d.BulkHotkey, d.MaxKey, d.MinKey);
            txtVoiceless.Text = FormatPercent((int)Math.Round(d.VoicelessLength));
            cmbLog.SelectedIndex = (int)d.LogLevel;
        }

        bool Apply()
        {
            int voiceless;
            if (!TryParsePercent(txtVoiceless.Text, out voiceless))
            {
                MessageBox.Show(this, "無声音の長さは -50 ～ +100 の整数で入力してください（例：-35、+20、0）。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtVoiceless.Focus();
                txtVoiceless.SelectAll();
                return false;
            }
            st.VoicelessLength = voiceless;
            if (cmbLog.SelectedIndex >= 0) st.LogLevel = (LogLevel)cmbLog.SelectedIndex;
            txtVoiceless.Text = FormatPercent(voiceless);
            st.UiScale = chkAutoScale.Checked ? 0 : (double)numScale.Value / 100.0;
            st.WheelShift = ColKeys[assign[0]];
            st.WheelCtrl = ColKeys[assign[1]];
            st.WheelAlt = ColKeys[assign[2]];
            st.WheelPlain = ColKeys[assign[3]];
            st.NormalizeWheel();
            st.IntonationBigStep = (double)numIntBig.Value;
            st.IntonationSmallStep = (double)numIntSmall.Value;
            st.LengthBigStep = (double)numLenBig.Value;
            st.LengthSmallStep = (double)numLenSmall.Value;
            st.EditorOpenWaitMs = (double)numEditorWait.Value;
            st.KeyGapMs = (double)numKeyGap.Value;
            st.BulkHotkey = keyCombos[0].ToString();
            st.MaxKey = keyCombos[1].ToString();
            st.MinKey = keyCombos[2].ToString();
            st.Save();
            Log.Write(string.Format("設定を変更: 拡大率={0} ホイール Shift={1} Ctrl={2} 通常={3} Alt={14} イントネーション 大{4}/小{5}% 長さ 大{6}/小{7}% 待ち{8}ms 間隔{9}ms 一括={10}（{13}%） 最大={11} 最小={12}",
                st.UiScale <= 0 ? "自動" : st.UiScale.ToString("0.##"), st.WheelShift, st.WheelCtrl, st.WheelPlain,
                st.IntonationBigStep, st.IntonationSmallStep, st.LengthBigStep, st.LengthSmallStep, st.EditorOpenWaitMs, st.KeyGapMs, st.BulkHotkey, st.MaxKey, st.MinKey, st.VoicelessLength, st.WheelAlt));
            if (onApplied != null) onApplied();
            return true;
        }
    }
}
