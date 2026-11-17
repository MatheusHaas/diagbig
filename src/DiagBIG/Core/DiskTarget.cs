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
        public static string Describe(string message, IDictionary<string, string> volumeLetters, string singleDiskNote)
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
            m = PortRx.Match(message);
            if (m.Success && parts.Count == 0) parts.Add("porta " + m.Groups[1].Value + " da controladora (\\Device\\RaidPort" + m.Groups[1].Value + "), a letra não vem no evento");
            if (parts.Count == 0 && UnknownRx.IsMatch(message)) parts.Add("NÃO INFORMADA pelo Windows (o evento diz \"volume ??\")");
            if (parts.Count == 0) return null;
            string text = "Unidade: " + string.Join("; ", parts.ToArray());
            if (!string.IsNullOrEmpty(singleDiskNote) && (parts[0].StartsWith("NÃO") || parts[0].Contains("a letra não vem")))
                text += ". " + singleDiskNote;
            return text;
        }

        /// <summary>Acrescenta a linha "Unidade: ..." no começo da mensagem (uma vez só).</summary>
        public static void Annotate(Occurrence o, IDictionary<string, string> volumeLetters, string singleDiskNote)
        {
            if (o == null || o.Message == null || o.Message.StartsWith("Unidade:", StringComparison.Ordinal)) return;
            string t = Describe(o.Message, volumeLetters, singleDiskNote);
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
