using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DiagBIG.Core
{
    public class MyIniOptions
    {
        public string BaseDir { get; set; }
        public string DataDir { get; set; }
        /// <summary>Valor de log-error/log_error. Vazio = opção presente sem valor.</summary>
        public string LogError { get; set; }
        public bool LogErrorDefined { get; set; }
        /// <summary>innodb_doublewrite (ou skip-innodb-doublewrite = "OFF"). null = não definido (padrão ON).</summary>
        public string DoubleWrite { get; set; }
        /// <summary>innodb_flush_log_at_trx_commit. null = não definido (padrão 1).</summary>
        public string FlushLogAtTrxCommit { get; set; }
        /// <summary>innodb_force_recovery. null = não definido (padrão 0).</summary>
        public string ForceRecovery { get; set; }
    }

    /// <summary>Lê datadir, basedir e log-error do my.ini / my.cnf.</summary>
    public static class MyIniParser
    {
        // [mysqld], [mariadb], [mariadbd], [server] e variações com versão: [mysqld-5.5], [mariadb-10.4], [mysqld8.0]
        static readonly Regex ServerSection = new Regex(@"^\[\s*(mysqld|mariadbd?|server)(-?\d+(\.\d+)*)?\s*\]", RegexOptions.IgnoreCase);
        static readonly Regex AnySection = new Regex(@"^\[.*\]");

        public static MyIniOptions Parse(string text)
        {
            var opt = new MyIniOptions();
            if (string.IsNullOrEmpty(text)) return opt;
            bool inServer = false;

            foreach (string raw in text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '!') continue;
                if (AnySection.IsMatch(line)) { inServer = ServerSection.IsMatch(line); continue; }
                if (!inServer) continue;

                string key, value;
                int eq = line.IndexOf('=');
                if (eq < 0) { key = StripComment(line); value = null; }
                else { key = line.Substring(0, eq); value = CleanValue(line.Substring(eq + 1)); }

                key = key.Trim().ToLowerInvariant().Replace('-', '_');
                if (key.StartsWith("loose_", StringComparison.Ordinal)) key = key.Substring(6);

                switch (key)
                {
                    case "datadir": if (!string.IsNullOrEmpty(value)) opt.DataDir = WinPath.Normalize(value); break;
                    case "basedir": if (!string.IsNullOrEmpty(value)) opt.BaseDir = WinPath.Normalize(value); break;
                    case "innodb_doublewrite": opt.DoubleWrite = value ?? "ON"; break;
                    case "skip_innodb_doublewrite": opt.DoubleWrite = "OFF"; break;
                    case "innodb_flush_log_at_trx_commit": if (!string.IsNullOrEmpty(value)) opt.FlushLogAtTrxCommit = value; break;
                    case "innodb_force_recovery": if (!string.IsNullOrEmpty(value)) opt.ForceRecovery = value; break;
                    case "log_error":
                        opt.LogErrorDefined = true;
                        opt.LogError = string.IsNullOrEmpty(value) ? "" : WinPath.Normalize(value);
                        break;
                }
            }
            return opt;
        }

        /// <summary>Junta vários arquivos na ordem de leitura do MySQL: o que vem depois prevalece.</summary>
        public static MyIniOptions Merge(IEnumerable<MyIniOptions> inOrder)
        {
            var r = new MyIniOptions();
            foreach (MyIniOptions o in inOrder)
            {
                if (o == null) continue;
                if (!string.IsNullOrEmpty(o.BaseDir)) r.BaseDir = o.BaseDir;
                if (!string.IsNullOrEmpty(o.DataDir)) r.DataDir = o.DataDir;
                if (o.LogErrorDefined) { r.LogErrorDefined = true; r.LogError = o.LogError; }
                if (o.DoubleWrite != null) r.DoubleWrite = o.DoubleWrite;
                if (o.FlushLogAtTrxCommit != null) r.FlushLogAtTrxCommit = o.FlushLogAtTrxCommit;
                if (o.ForceRecovery != null) r.ForceRecovery = o.ForceRecovery;
            }
            return r;
        }

        static string StripComment(string s)
        {
            int hash = s.IndexOf('#');
            return hash >= 0 ? s.Substring(0, hash) : s;
        }

        static string CleanValue(string v)
        {
            v = v.Trim();
            if (v.Length > 0 && (v[0] == '"' || v[0] == '\''))
            {
                int close = v.IndexOf(v[0], 1);
                v = close > 0 ? v.Substring(1, close - 1) : v.Substring(1);
            }
            else
            {
                v = StripComment(v).Trim();
            }
            // No my.ini "\\" vale uma barra; WinPath.Normalize junta as duplicadas.
            return v.Trim();
        }
    }
}
