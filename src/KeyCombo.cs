using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace VoicePeakAssist
{
    /// <summary>キーの組み合わせ（例：Ctrl+G、Z、NumPad1）。設定ファイルには "Ctrl+Shift+NumPad1" の形で保存する。</summary>
    internal struct KeyCombo
    {
        public bool Ctrl, Shift, Alt;
        public int Vk; // 0 = 未設定

        public bool IsEmpty { get { return Vk == 0; } }

        public static KeyCombo Parse(string text)
        {
            var k = new KeyCombo();
            if (string.IsNullOrWhiteSpace(text)) return k;
            foreach (var raw in text.Split('+'))
            {
                var p = raw.Trim();
                if (p.Length == 0) continue;
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) k.Ctrl = true;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) k.Shift = true;
                else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) k.Alt = true;
                else
                {
                    Keys key;
                    if (p.Length == 1 && char.IsDigit(p[0])) key = Keys.D0 + (p[0] - '0');
                    else if (!Enum.TryParse(p, true, out key)) continue;
                    k.Vk = (int)(key & Keys.KeyCode);
                }
            }
            return k;
        }

        public override string ToString()
        {
            if (IsEmpty) return "";
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Shift) parts.Add("Shift");
            if (Alt) parts.Add("Alt");
            parts.Add(((Keys)Vk).ToString());
            return string.Join("+", parts);
        }

        /// <summary>画面に出す名前（例：Ctrl ＋ G、テンキー 1）。</summary>
        public string Display()
        {
            if (IsEmpty) return "（なし）";
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Shift) parts.Add("Shift");
            if (Alt) parts.Add("Alt");
            parts.Add(KeyName((Keys)Vk));
            return string.Join(" ＋ ", parts);
        }

        static string KeyName(Keys k)
        {
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "テンキー " + (k - Keys.NumPad0);
            switch (k)
            {
                case Keys.Multiply: return "テンキー *";
                case Keys.Add: return "テンキー +";
                case Keys.Subtract: return "テンキー -";
                case Keys.Divide: return "テンキー /";
                case Keys.Decimal: return "テンキー .";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return ";";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.Space: return "スペース";
                case Keys.Left: return "←";
                case Keys.Right: return "→";
                case Keys.Up: return "↑";
                case Keys.Down: return "↓";
                case Keys.PageUp: return "PageUp";
                case Keys.Next: return "PageDown";
            }
            return k.ToString();
        }

        public bool Matches(int vk, bool ctrl, bool shift, bool alt)
        {
            return !IsEmpty && vk == Vk && ctrl == Ctrl && shift == Shift && alt == Alt;
        }

        public bool SameAs(KeyCombo o)
        {
            return Vk == o.Vk && Ctrl == o.Ctrl && Shift == o.Shift && Alt == o.Alt;
        }

        /// <summary>修飾キーそのものか。</summary>
        public static bool IsModifierKey(Keys k)
        {
            return k == Keys.ShiftKey || k == Keys.ControlKey || k == Keys.Menu || k == Keys.LShiftKey || k == Keys.RShiftKey ||
                   k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.LMenu || k == Keys.RMenu || k == Keys.LWin || k == Keys.RWin;
        }
    }
}
