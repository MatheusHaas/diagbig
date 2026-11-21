using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DiagBIG.Core
{
    /// <summary>Uma opção a ajustar no my.ini. Value null = tirar a opção (a linha vira comentário).</summary>
    public class IniSet
    {
        public string Key;
        public string Value;
        /// <summary>true = só mexe se a opção já existe e está diferente (não cria a linha).</summary>
        public bool OnlyIfPresent;
        /// <summary>true = a opção é da seção [client] (ex.: port, para o cliente mysql achar a porta de manutenção).</summary>
        public bool Client;
        public IniSet(string key, string value, bool onlyIfPresent = false, bool client = false) { Key = key; Value = value; OnlyIfPresent = onlyIfPresent; Client = client; }
    }

    /// <summary>
    /// Edita o texto do my.ini para a manutenção do banco (force_recovery, porta, doublewrite, flush). Só mexe nas seções do servidor
    /// ([mysqld], [mariadb], [server]...). A linha antiga fica como comentário "# DiagBIG: era ..." logo acima da nova.
    /// </summary>
    public static class MyIniEditor
    {
        static readonly Regex ServerSection = new Regex(@"^\[\s*(mysqld|mariadbd?|server)(-?\d+(\.\d+)*)?\s*\]", RegexOptions.IgnoreCase);
        static readonly Regex ClientSection = new Regex(@"^\[\s*client(-server|-mariadb)?\s*\]", RegexOptions.IgnoreCase);
        static readonly Regex AnySection = new Regex(@"^\[.*\]");

        /// <summary>Opções da manutenção com force: force 1 ou 2, porta 3307, doublewrite ON e flush 1 (só se estavam errados).</summary>
        public static List<IniSet> MaintenanceWithForce(string force, string port)
        {
            return new List<IniSet>
            {
                new IniSet("innodb_force_recovery", force),
                new IniSet("port", port),
                new IniSet("port", port, true, true),
                new IniSet("innodb_doublewrite", "ON", true),
                new IniSet("skip_innodb_doublewrite", null, true),
                new IniSet("innodb_flush_log_at_trx_commit", "1", true),
            };
        }

        /// <summary>Depois do reparo: tira o force e mantém a porta de manutenção.</summary>
        public static List<IniSet> RemoveForce(string port)
        {
            return new List<IniSet>
            {
                new IniSet("innodb_force_recovery", null, true),
                new IniSet("port", port),
                new IniSet("port", port, true, true),
            };
        }

        static string Norm(string key)
        {
            key = key.Trim().ToLowerInvariant().Replace('-', '_');
            return key.StartsWith("loose_", StringComparison.Ordinal) ? key.Substring(6) : key;
        }

        static string KeyOf(string line, out string value)
        {
            string t = line.Trim();
            value = null;
            if (t.Length == 0 || t[0] == '#' || t[0] == ';' || t[0] == '!' || t[0] == '[') return null;
            int eq = t.IndexOf('=');
            string key = eq < 0 ? t : t.Substring(0, eq);
            if (eq >= 0)
            {
                value = t.Substring(eq + 1).Trim();
                int c = value.IndexOf(" #", StringComparison.Ordinal);
                if (c >= 0) value = value.Substring(0, c).Trim();
            }
            return Norm(key);
        }

        /// <summary>Devolve o novo texto; "changes" descreve cada alteração (antes e depois) para mostrar na confirmação.</summary>
        public static string Apply(string text, IList<IniSet> sets, DateTime now, out List<string> changes)
        {
            changes = new List<string>();
            string nl = (text ?? "").Contains("\r\n") || string.IsNullOrEmpty(text) ? "\r\n" : "\n";
            var lines = new List<string>((text ?? "").Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None));
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            // Comentários de execuções anteriores saem: o my.ini original fica guardado à parte, então o arquivo não acumula histórico.
            lines.RemoveAll(l => l.TrimStart().StartsWith("# DiagBIG", StringComparison.Ordinal));
            string stamp = "# DiagBIG " + now.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);

            foreach (IniSet set in sets)
            {
                string want = Norm(set.Key);
                bool inServer = false, found = false;
                int firstServerHeader = -1;
                var done = new List<string>();
                for (int i = 0; i < lines.Count; i++)
                {
                    string line = lines[i].Trim();
                    if (AnySection.IsMatch(line))
                    {
                        inServer = set.Client ? ClientSection.IsMatch(line) : ServerSection.IsMatch(line);
                        if (inServer && firstServerHeader < 0) firstServerHeader = i;
                        continue;
                    }
                    if (!inServer) continue;
                    string val;
                    string k = KeyOf(lines[i], out val);
                    if (k != want) continue;
                    found = true;
                    if (set.Value == null)
                    {
                        lines[i] = "# DiagBIG: removido (" + stamp.Substring(10).Trim() + "): " + lines[i].Trim();
                        changes.Add(set.Key + ": " + lines[i].Substring(lines[i].IndexOf(": ", StringComparison.Ordinal) + 2) + "  ->  (removido)");
                    }
                    else if (!string.Equals(val, set.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        string before = lines[i].Trim();
                        lines[i] = want + "=" + set.Value;
                        lines.Insert(i, stamp + ": era " + before);
                        i++;
                        changes.Add(before + "  ->  " + want + "=" + set.Value);
                    }
                }
                if (found || set.Value == null || set.OnlyIfPresent) continue;
                // opção não existe: cria logo abaixo do cabeçalho do servidor (ou uma seção [mysqld] nova no fim).
                string add = want + "=" + set.Value;
                if (firstServerHeader >= 0) lines.Insert(firstServerHeader + 1, add);
                else { lines.Add("[mysqld]"); lines.Add(add); }
                changes.Add("(não definido)  ->  " + add);
            }
            return string.Join(nl, lines.ToArray()) + nl;
        }
    }
}
