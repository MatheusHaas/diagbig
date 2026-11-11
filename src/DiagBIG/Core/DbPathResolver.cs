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
        /// <summary>Outros nomes padrão a tentar, em ordem, se ErrPath não existir (ex.: mysql.err do MySQL 4).</summary>
        public List<string> AlternateErrPaths { get; } = new List<string>();
    }

    /// <summary>Regras de onde fica a datadir e o .err a partir do executável e do my.ini.</summary>
    public static class DbPathResolver
    {
        /// <summary>Pasta base = pai da pasta do executável (ex.: C:\mysql\bin\mysqld-nt.exe → C:\mysql).</summary>
        public static string BaseDirFromExe(string exePath)
        {
            string bin = WinPath.GetDirectoryName(WinPath.Normalize(exePath));
            return WinPath.GetDirectoryName(bin) ?? bin;
        }

        /// <summary>
        /// Onde procurar my.ini/my.cnf quando o ImagePath não tem --defaults-file, na ordem em que o
        /// MySQL lê no Windows (o último lido prevalece): pasta do Windows, C:\, pasta base e, por
        /// último, a data (onde o instalador do MariaDB grava). O MySQL 4 costuma usar C:\WINDOWS\my.ini.
        /// </summary>
        public static List<string> IniCandidates(string baseDir, string windowsDir = null)
        {
            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(windowsDir)) dirs.Add(WinPath.Normalize(windowsDir));
            dirs.Add(@"C:\");
            if (!string.IsNullOrEmpty(baseDir))
            {
                dirs.Add(baseDir);
                dirs.Add(WinPath.Combine(baseDir, "data"));
            }
            var list = new List<string>();
            foreach (string dir in dirs)
            {
                foreach (string name in new[] { "my.ini", "my.cnf" })
                {
                    string p = WinPath.Combine(dir, name);
                    if (!list.Exists(x => string.Equals(x, p, System.StringComparison.OrdinalIgnoreCase))) list.Add(p);
                }
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
                r.AlternateErrPaths.Add(WinPath.Combine(r.DataDir, "mysql.err")); // padrão do MySQL 4
                r.ErrRule = opt.LogErrorDefined
                    ? "log-error sem valor no my.ini → padrão datadir\\NOMEDOPC.err (MySQL 4: datadir\\mysql.err)"
                    : "log-error não definido → padrão datadir\\NOMEDOPC.err (MySQL 4: datadir\\mysql.err)";
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
