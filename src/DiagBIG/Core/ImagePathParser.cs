using System;
using System.Text.RegularExpressions;

namespace DiagBIG.Core
{
    public class ImagePathInfo
    {
        public string ExePath { get; set; }
        public string Arguments { get; set; }
        /// <summary>Valor de --defaults-file (normalizado) ou null.</summary>
        public string DefaultsFile { get; set; }
    }

    /// <summary>Interpreta o ImagePath de um serviço (registro HKLM\SYSTEM\CurrentControlSet\Services\X).</summary>
    public static class ImagePathParser
    {
        // "--defaults-file=C:\a b\my.ini"   (aspas em volta do argumento inteiro)
        static readonly Regex DefQuotedArg = new Regex("\"--defaults[-_]file\\s*=\\s*([^\"]*)\"", RegexOptions.IgnoreCase);
        // --defaults-file="C:\a b\my.ini"   (aspas só no valor)
        static readonly Regex DefQuotedValue = new Regex("--defaults[-_]file\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        // --defaults-file=C:\x\my.ini
        static readonly Regex DefPlain = new Regex("--defaults[-_]file\\s*=\\s*([^\\s\"]+)", RegexOptions.IgnoreCase);

        public static ImagePathInfo Parse(string imagePath)
        {
            var info = new ImagePathInfo { Arguments = "" };
            if (string.IsNullOrWhiteSpace(imagePath)) return info;
            string s = imagePath.Trim();

            if (s[0] == '"')
            {
                int close = s.IndexOf('"', 1);
                if (close < 0) { info.ExePath = s.Substring(1); }
                else { info.ExePath = s.Substring(1, close - 1); info.Arguments = s.Substring(close + 1).Trim(); }
            }
            else
            {
                // Sem aspas: o caminho pode ter espaços; corta logo após ".exe".
                int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                while (exe >= 0 && exe + 4 < s.Length && !char.IsWhiteSpace(s[exe + 4]) && s[exe + 4] != '"')
                    exe = s.IndexOf(".exe", exe + 4, StringComparison.OrdinalIgnoreCase);
                if (exe >= 0)
                {
                    info.ExePath = s.Substring(0, exe + 4);
                    info.Arguments = s.Substring(exe + 4).Trim();
                }
                else
                {
                    int sp = s.IndexOf(' ');
                    info.ExePath = sp < 0 ? s : s.Substring(0, sp);
                    info.Arguments = sp < 0 ? "" : s.Substring(sp + 1).Trim();
                }
            }
            info.ExePath = WinPath.Normalize(info.ExePath);

            Match m = DefQuotedArg.Match(info.Arguments);
            if (!m.Success) m = DefQuotedValue.Match(info.Arguments);
            if (!m.Success) m = DefPlain.Match(info.Arguments);
            if (m.Success && m.Groups[1].Value.Trim().Length > 0)
                info.DefaultsFile = WinPath.Normalize(m.Groups[1].Value);

            return info;
        }

        /// <summary>true se o executável do ImagePath for mysqld.exe / mariadbd.exe (com ou sem ".exe").</summary>
        public static bool IsDatabaseImagePath(string imagePath)
        {
            string exe = Parse(imagePath).ExePath;
            if (string.IsNullOrEmpty(exe)) return false;
            string name = WinPath.GetFileName(exe);
            foreach (string p in Config.DbProcessNames)
            {
                if (string.Equals(name, p, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(name + ".exe", p, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
