using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DiagBIG.Core
{
    /// <summary>
    /// Descobre QUAL unidade/disco um evento de disco cita. O Windows quase nunca escreve só "C:":
    /// Ntfs 55 pode vir com "volume ??", disk 7 cita \Device\Harddisk0\DR0, iaStorA 129 cita \Device\RaidPort0.
    /// </summary>
    public static class DiskTarget
    {
        static readonly Regex DriveRx = new Regex(@"(?:volume|volumen|volumes?)\s+([A-Za-z]:)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex UnknownRx = new Regex(@"(?:volume|volumen)\s+\?\?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex VolRx = new Regex(@"\\Device\\HarddiskVolume(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex PhysRx = new Regex(@"\\Device\\Harddisk(\d+)\\DR(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex PortRx = new Regex(@"\\Device\\(?:Raid)?Port(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Texto "Unidade: ..." para a mensagem do evento; null se o evento não é de disco/volume.
        /// volumeLetters: "\Device\HarddiskVolume3" (ou só "3") para "C:". singleDiskNote: ex. "o PC tem 1 disco físico (X): C:, E: estão nele".
        /// </summary>
        public static string Describe(string message, IDictionary<string, string> volumeLetters, string singleDiskNote, IDictionary<int, string> diskMap = null)
        {
            if (string.IsNullOrEmpty(message)) return null;
            var parts = new List<string>();
            Match m = DriveRx.Match(message);
            if (m.Success) parts.Add(m.Groups[1].Value.ToUpperInvariant());
            m = VolRx.Match(message);
            if (m.Success)
            {
                string letter = null;
                if (volumeLetters != null) volumeLetters.TryGetValue(m.Groups[1].Value, out letter);
                parts.Add(letter != null ? letter + " (\\Device\\HarddiskVolume" + m.Groups[1].Value + ")" : "volume \\Device\\HarddiskVolume" + m.Groups[1].Value + " (sem letra)");
            }
            m = PhysRx.Match(message);
            if (m.Success) parts.Add("disco físico nº " + m.Groups[1].Value + " (\\Device\\Harddisk" + m.Groups[1].Value + "\\DR" + m.Groups[2].Value + "), a letra não vem no evento");
            if (diskMap != null)
            {
                int dn = DiskNumber(message);
                string what;
                if (dn >= 0 && diskMap.TryGetValue(dn, out what))
                {
                    // troca o "disco físico nº N" genérico pelo modelo e as letras
                    parts.RemoveAll(x => x.StartsWith("disco físico nº", StringComparison.Ordinal));
                    parts.Add("DISCO FÍSICO " + dn + " = " + what);
                }
            }
            m = PortRx.Match(message);
            if (m.Success && parts.Count == 0) parts.Add("porta " + m.Groups[1].Value + " da controladora (\\Device\\RaidPort" + m.Groups[1].Value + "), a letra não vem no evento");
            if (parts.Count == 0 && UnknownRx.IsMatch(message)) parts.Add("NÃO INFORMADA pelo Windows (o evento diz \"volume ??\")");
            if (parts.Count == 0) return null;
            string text = "Unidade: " + string.Join("; ", parts.ToArray());
            if (!string.IsNullOrEmpty(singleDiskNote) && (parts[0].StartsWith("NÃO") || parts[0].Contains("a letra não vem")))
                text += ". " + singleDiskNote;
            return text;
        }

        static readonly Regex DiskNumRx = new Regex(@"(?:\bDisco|\bDisk)\s+(\d+)\b|\\Device\\Harddisk(\d+)\\DR\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Número do disco físico citado ("Disco 0", "Disk 1" ou \Device\Harddisk0\DR0); -1 se não cita.</summary>
        public static int DiskNumber(string message)
        {
            if (string.IsNullOrEmpty(message)) return -1;
            Match m = DiskNumRx.Match(message);
            if (!m.Success) return -1;
            string g = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            int n;
            return int.TryParse(g, out n) ? n : -1;
        }

        /// <summary>Linhas "DISCO COM ERRO: Disco 0 = ..." para cada disco citado nos eventos (com a contagem).</summary>
        public static List<string> CitedDisks(IEnumerable<Occurrence> events, IDictionary<int, string> diskMap)
        {
            var count = new SortedDictionary<int, int>();
            foreach (Occurrence o in events)
            {
                int n = DiskNumber(o.Message);
                if (n < 0) continue;
                int c; count.TryGetValue(n, out c); count[n] = c + 1;
            }
            var lines = new List<string>();
            foreach (var kv in count)
            {
                string what;
                if (diskMap == null || !diskMap.TryGetValue(kv.Key, out what)) what = "modelo não identificado";
                lines.Add("DISCO COM ERRO: Disco " + kv.Key + " = " + what + " (" + kv.Value + " evento(s) citam este disco)");
            }
            return lines;
        }

        static readonly Regex DiskIdRx = new Regex(@"\\Device\\Harddisk\d+\\DR\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>ID do disco que o evento cita (ex.: \Device\Harddisk0\DR0); null se não cita.</summary>
        public static string DiskId(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            Match m = DiskIdRx.Match(message);
            return m.Success ? m.Value : null;
        }

        /// <summary>Acrescenta a linha "Unidade: ..." no começo da mensagem (uma vez só).</summary>
        public static void Annotate(Occurrence o, IDictionary<string, string> volumeLetters, string singleDiskNote, IDictionary<int, string> diskMap = null)
        {
            if (o == null || o.Message == null || o.Message.StartsWith("Unidade:", StringComparison.Ordinal)) return;
            string t = Describe(o.Message, volumeLetters, singleDiskNote, diskMap);
            if (t != null) o.Message = t + "\r\n" + o.Message;
        }

        /// <summary>Agrupa eventos antigos: "Ntfs 55: 5x, último em 06/10/2026" para a linha de aviso.</summary>
        public static List<string> OldSummary(IList<Occurrence> old)
        {
            var lines = new List<string>();
            var byKey = new Dictionary<string, List<Occurrence>>();
            var order = new List<string>();
            foreach (Occurrence o in old)
            {
                string k = o.GroupKey ?? ((o.Source ?? "") + " " + o.EventId);
                if (!byKey.ContainsKey(k)) { byKey[k] = new List<Occurrence>(); order.Add(k); }
                byKey[k].Add(o);
            }
            foreach (string k in order)
            {
                DateTime? last = null;
                foreach (Occurrence o in byKey[k]) if (o.Time.HasValue && (!last.HasValue || o.Time.Value > last.Value)) last = o.Time;
                lines.Add(k + ": " + byKey[k].Count + "x" + (last.HasValue ? ", último em " + last.Value.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) : ""));
            }
            return lines;
        }
    }
}
