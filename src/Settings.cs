using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VoicePeakAssist
{
    internal enum InputMethod { Message, RealMouse }

    /// <summary>設定。exe と同じフォルダの VoicePeakAssist.ini に保存する。</summary>
    internal sealed class Settings
    {
        public bool Enabled = true;
        public double SettingsVersion = 0;
        // ホイールの割り当て：Big（大移動）/ Small（小移動）/ Scroll（横スクロール）
        public string WheelShift = "Small";
        public string WheelCtrl = "Scroll";
        public string WheelPlain = "Big";
        public string WheelAlt = "Word";
        public double IntonationBigStep = 10;    // イントネーションの大移動（%）
        public double IntonationSmallStep = 2;   // イントネーションの小移動（%）
        public double LengthBigStep = 10;        // 長さの大移動（%）
        public double LengthSmallStep = 2;       // 長さの小移動（%）
        public bool InvertWheel = false;         // true: 上で低く/短く
        public bool RequirePanelClick = false;   // パネルをクリックしてから有効
        public bool BulkHotkeyEnabled = true;
        public string BulkHotkey = "Ctrl+G";     // 無声音（カ/サ/タ/ハ/パ行）の長さを自動設定
        public double VoicelessLength = -35;     // 無声音の長さを自動設定するときの値（%、-50〜+100）
        public string MaxKey = "Z";              // カーソル下の文字を最大値に
        public string MinKey = "X";              // カーソル下の文字を最小値に
        public char BulkHotkeyKey = 'G';         // 旧版の設定（読み込み時に BulkHotkey へ移す）
        public InputMethod Input = InputMethod.Message;
        public bool NeutralizeModifiers = true;  // 送信中は Shift/Ctrl が離れているように見せる
        public double EditorOpenWaitMs = 20;    // 数値をクリックしてから入力欄が開くまで待つ時間（ms）
        public double KeyGapMs = 10;             // 入力欄へのキー入力の間隔（ms）
        public string ConfirmWith = "Enter";     // 数値入力を確定するキー（Enter / Esc）
        public double DragDetour = 10;            // 長さのドラッグで先に縦へ動かす量（px）。小さな動きがクリック扱いになるのを防ぐ
        public double LengthHitWidth = 52.5;      // 長さ表示でホイールが効く幅（+50%のボックスの幅）
        public double LengthPctPerPxRight = 0;   // ボックスの右半分をつかんで右へ1px動かしたときの変化（%。0 = 未計測）
        public double LengthPctPerPxLeft = 0;    // 同じく左へ1px動かしたときの変化（%。縮むならマイナス）
        public string ProcessName = "voicepeak";
        public string WindowTitlePrefix = "VOICEPEAK";
        public bool DebugLog = false;
        public LogLevel LogLevel = LogLevel.None; // ログの記録量（None / Min / Mid / Max）
        public double UiScale = 0;               // VOICEPEAK の画面の拡大率（0 = 自動で測る）

        // 画面の寸法（表示スケール100%、パネル上端の線からの距離）。VOICEPEAK の更新で変わったときの調整用
        public double PanelHeight = 214;
        public double ContentLeft = 76;
        public double IconX = 37;
        public double IconAccentY = 37;
        public double IconIntonationY = 92;
        public double IconLengthY = 147;
        public double HitTopY = 55;   // 長さ表示でホイールが効く範囲の上端（カタカナの欄の上端）
        public double KanaTop = 62;
        public double KanaBottom = 88;
        public double DotBandTop = 90;
        public double DotBandBottom = 188;
        public double IntonationTopY = 99.5;
        public double IntonationBottomY = 178.5;
        public double BoxTopY = 99;
        public double BoxMidY = 128;
        public double BoxScanY = 110;
        public double LabelY = 175;
        public double BoxBottomY = 157;
        public double BoxBaseWidth = 35;
        public double LabelTop = 166;
        public double LabelBottom = 184;
        public double ScrollThumbY = 205.5;
        public double ColumnPitch = 70;

        public static string Dir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        public static string FilePath
        {
            get { return Path.Combine(Dir, "VoicePeakAssist.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) { s.Save(); return s; }
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                foreach (var f in typeof(Settings).GetFields())
                {
                    if (f.IsStatic || f.IsInitOnly) continue;
                    string v;
                    if (!map.TryGetValue(f.Name, out v)) continue;
                    try
                    {
                        if (f.FieldType == typeof(bool)) f.SetValue(s, ParseBool(v));
                        else if (f.FieldType == typeof(double)) f.SetValue(s, double.Parse(v, CultureInfo.InvariantCulture));
                        else if (f.FieldType == typeof(string)) f.SetValue(s, v);
                        else if (f.FieldType == typeof(char)) { if (v.Length > 0) f.SetValue(s, char.ToUpperInvariant(v[0])); }
                        else if (f.FieldType == typeof(InputMethod)) f.SetValue(s, Enum.Parse(typeof(InputMethod), v, true));
                        else if (f.FieldType == typeof(LogLevel)) f.SetValue(s, Enum.Parse(typeof(LogLevel), v, true));
                    }
                    catch (Exception ex) { Log.Write("設定の読み込みに失敗: " + f.Name + " = " + v + " (" + ex.Message + ")"); }
                }
            }
            catch (Exception ex) { Log.Write("設定ファイルを読めませんでした: " + ex.Message); }
            if (s.SettingsVersion < 2)
            {
                // v0.13 で初期値を変えた項目
                s.EditorOpenWaitMs = 20;
                s.KeyGapMs = 5;
                s.SettingsVersion = 2;
                s.Save();
            }
            if (s.SettingsVersion < 3)
            {
                // v0.14 で初期の割り当てを変更（キーなし＝大移動、Shift＝小移動、Ctrl＝横スクロール）
                if (s.WheelShift == Big && s.WheelCtrl == Small && s.WheelPlain == Scroll)
                { s.WheelShift = Small; s.WheelCtrl = Scroll; s.WheelPlain = Big; }
                s.SettingsVersion = 3;
                s.Save();
            }
            if (s.SettingsVersion < 4)
            {
                if (s.BulkHotkeyKey != 'G' && char.IsLetterOrDigit(s.BulkHotkeyKey)) s.BulkHotkey = "Ctrl+" + char.ToUpperInvariant(s.BulkHotkeyKey);
                s.SettingsVersion = 4;
                s.Save();
            }
            if (s.SettingsVersion < 5)
            {
                if (s.KeyGapMs == 5) s.KeyGapMs = 10; // v0.17 で初期値を 10ms に変更
                s.SettingsVersion = 5;
                s.Save();
            }
            s.NormalizeWheel();
            return s;
        }

        public const string Big = "Big", Small = "Small", Scroll = "Scroll", Word = "Word";

        /// <summary>割り当てが重複・不正なら既定（キーなし=大、Shift=小、Ctrl=横スクロール、Alt=単語全体）に直す。</summary>
        public void NormalizeWheel()
        {
            var vals = new[] { WheelShift, WheelCtrl, WheelAlt, WheelPlain };
            var ok = new[] { Big, Small, Scroll, Word };
            bool valid = vals.All(v => ok.Contains(v)) && vals.Distinct().Count() == 4;
            if (!valid) { WheelShift = Small; WheelCtrl = Scroll; WheelAlt = Word; WheelPlain = Big; }
        }

        static bool ParseBool(string v)
        {
            v = v.ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("; VoicePeak 発音調整アシスト 設定ファイル");
                sb.AppendLine("; 値を書き換えたら、トレイアイコンのメニューから「設定を読み込み直す」を選んでください。");
                sb.AppendLine("; Input = Message（カーソルを動かさない） / RealMouse（実際のマウス操作。一瞬カーソルが動く）");
                sb.AppendLine("; BulkHotkey / MaxKey / MinKey = キー（例：Ctrl+G、Z、NumPad1、Shift+F2）。設定画面から登録できます");
                sb.AppendLine("; WheelShift / WheelCtrl / WheelAlt / WheelPlain = Big（大移動） / Small（小移動） / Scroll（横スクロール） / Word（単語全体）");
                sb.AppendLine("; LengthPctPerPxRight / LengthPctPerPxLeft = 長さボックスの右半分をつかんで右／左へ1px動かしたときに変わる%（0 = 未計測。初回に自動で測ります）");
                sb.AppendLine("; UiScale = VOICEPEAK の画面の拡大率（0 = 自動。うまく測れないときだけ 1 や 1.25 を指定）");
                sb.AppendLine("; Panel〜ColumnPitch は画面の寸法（拡大率1のときのpx）。通常は変更不要です。");
                sb.AppendLine();
                foreach (var f in typeof(Settings).GetFields())
                {
                    if (f.IsStatic || f.IsInitOnly) continue;
                    object v = f.GetValue(this);
                    string text;
                    if (v is double) text = ((double)v).ToString("0.####", CultureInfo.InvariantCulture);
                    else if (v is bool) text = ((bool)v) ? "true" : "false";
                    else text = Convert.ToString(v, CultureInfo.InvariantCulture);
                    sb.AppendLine(f.Name + " = " + text);
                }
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { Log.Write("設定を保存できませんでした: " + ex.Message); }
        }
    }

    /// <summary>ログの記録量。None=書かない / Min=起動時だけ / Mid=起動時＋読み取れなかった文字 / Max=すべて</summary>
    internal enum LogLevel { None = 0, Min = 1, Mid = 2, Max = 3 }

    internal static class Log
    {
        static readonly object Lock = new object();
        public static bool Verbose;
        public static LogLevel Level = LogLevel.None;

        public static string FilePath
        {
            get { return Path.Combine(Settings.Dir, "VoicePeakAssist.log"); }
        }

        /// <summary>一般の記録（ログレベル「最大」のときだけ書く）。</summary>
        public static void Write(string msg) { if (Level >= LogLevel.Max) Append(msg); }

        /// <summary>起動時の記録（「最小」以上で書く）。</summary>
        public static void Startup(string msg) { if (Level >= LogLevel.Min) Append(msg); }

        /// <summary>読み取れなかった文字の記録（「中」以上で書く）。</summary>
        public static void Unread(string msg) { if (Level >= LogLevel.Mid) Append(msg); }

        static void Append(string msg)
        {
            lock (Lock)
            {
                try
                {
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        var old = FilePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }

        public static void Debug(string msg)
        {
            if (Verbose) Write(msg);
        }
    }
}
