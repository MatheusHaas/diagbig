using System.Collections.Generic;

namespace DiagBIG.Core
{
    public class DbPaths
    {
        public string BaseDir { get; set; }
        public string DataDir { get; set; }
        /// <summary>Caminho esperado do .err segundo a configuração.</summary>
        public string ErrPath { get; set; }
        /// <summary>Como ErrPath foi obtido (texto para o TXT).</summary>
        public string ErrRule { get; set; }
    }

    /// <summary>Regras de onde fica a datadir e o .err a partir do executável e do my.ini.</summary>
    public static class DbPathResolver
    {
        /// <summary>Pasta base = pai da pasta do executável (ex.: C:\BIG_db\bin\mysqld.exe → C:\BIG_db).</summary>
        public static string BaseDirFromExe(string exePath)
        {
            string bin = WinPath.GetDirectoryName(WinPath.Normalize(exePath));
            return WinPath.GetDirectoryName(bin) ?? bin;
        }

        /// <summary>Onde procurar my.ini/my.cnf quando o ImagePath não tem --defaults-file.</summary>
        public static List<string> IniCandidates(string baseDir)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(baseDir)) return list;
            foreach (string dir in new[] { baseDir, WinPath.Combine(baseDir, "data") })
            {
                list.Add(WinPath.Combine(dir, "my.ini"));
                list.Add(WinPath.Combine(dir, "my.cnf"));
            }
            return list;
        }

        public static DbPaths Resolve(MyIniOptions opt, string baseDirFromExe, string computerName)
        {
            opt = opt ?? new MyIniOptions();
            var r = new DbPaths();
            r.BaseDir = !string.IsNullOrEmpty(opt.BaseDir) && WinPath.IsRooted(opt.BaseDir) ? opt.BaseDir : baseDirFromExe;

            if (string.IsNullOrEmpty(opt.DataDir)) r.DataDir = WinPath.Combine(r.BaseDir, "data");
            else r.DataDir = WinPath.IsRooted(opt.DataDir) ? opt.DataDir : WinPath.Combine(r.BaseDir, opt.DataDir);
            r.DataDir = r.DataDir == null ? null : r.DataDir.TrimEnd('\\');

            string le = opt.LogError;
            if (string.IsNullOrEmpty(le))
            {
                r.ErrPath = WinPath.Combine(r.DataDir, computerName + ".err");
                r.ErrRule = opt.LogErrorDefined
                    ? "log-error sem valor no my.ini → padrão datadir\\NOMEDOPC.err"
                    : "log-error não definido → padrão datadir\\NOMEDOPC.err";
                return r;
            }

            string path = WinPath.IsRooted(le) ? le : WinPath.Combine(r.DataDir, le);
            r.ErrRule = WinPath.IsRooted(le) ? "log-error do my.ini" : "log-error do my.ini (relativo à datadir)";
            if (!WinPath.HasExtension(path))
            {
                path += ".err";
                r.ErrRule += ", sem extensão → .err";
            }
            r.ErrPath = path;
            return r;
        }
    }
}
