using System;
using System.Text.RegularExpressions;

namespace DiagBIG.Core
{
    /// <summary>
    /// Manipulação de caminhos no formato Windows, independente do SO em que o código roda
    /// (System.IO.Path no Linux usa "/" e quebraria os testes).
    /// </summary>
    public static class WinPath
    {
        static readonly Regex DriveRooted = new Regex(@"^[A-Za-z]:\\", RegexOptions.Compiled);

        /// <summary>Tira aspas, troca / por \ e junta barras duplicadas (mantendo o \\ inicial de UNC).</summary>
        public static string Normalize(string path)
        {
            if (path == null) return null;
            string p = path.Trim();
            if (p.Length >= 2 && ((p[0] == '"' && p[p.Length - 1] == '"') || (p[0] == '\'' && p[p.Length - 1] == '\'')))
                p = p.Substring(1, p.Length - 2).Trim();
            p = p.Replace('/', '\\');
            bool unc = p.StartsWith(@"\\", StringComparison.Ordinal);
            while (p.IndexOf(@"\\", StringComparison.Ordinal) >= 0)
                p = p.Replace(@"\\", @"\");
            if (unc) p = @"\" + p;
            if (p.Length == 2 && p[1] == ':') p += @"\";
            return p;
        }

        public static bool IsRooted(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return DriveRooted.IsMatch(path) || path.StartsWith(@"\", StringComparison.Ordinal)
                   || (path.Length == 2 && path[1] == ':');
        }

        public static string Combine(string dir, string name)
        {
            if (string.IsNullOrEmpty(dir)) return name;
            if (string.IsNullOrEmpty(name)) return dir;
            if (IsRooted(name)) return name;
            while (name.StartsWith(@".\", StringComparison.Ordinal)) name = name.Substring(2);
            return dir.TrimEnd('\\') + @"\" + name.TrimStart('\\');
        }

        public static string GetDirectoryName(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.TrimEnd('\\');
            int i = p.LastIndexOf('\\');
            if (i < 0) return null;
            if (i == 2 && p[1] == ':') return p.Substring(0, 3); // C:\
            return p.Substring(0, i);
        }

        public static string GetFileName(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            int i = path.TrimEnd('\\').LastIndexOf('\\');
            return i < 0 ? path : path.Substring(i + 1);
        }

        public static bool HasExtension(string path)
        {
            string name = GetFileName(path) ?? "";
            int dot = name.LastIndexOf('.');
            return dot > 0 && dot < name.Length - 1;
        }
    }
}
