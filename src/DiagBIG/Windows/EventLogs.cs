using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using DiagBIG.Core;

namespace DiagBIG.Windows
{
    /// <summary>Consultas somente leitura ao Visualizador de Eventos, filtrando nível/período no próprio XPath.</summary>
    internal static class EventLogs
    {
        public static string TimeFilter(DateTime since)
        {
            long ms = (long)(DateTime.Now - since).TotalMilliseconds;
            if (ms < 0) ms = 0;
            return "TimeCreated[timediff(@SystemTime) <= " + ms.ToString(CultureInfo.InvariantCulture) + "]";
        }

        public static string IdsFilter(IEnumerable<int> ids)
        {
            return "(" + string.Join(" or ", ids.Select(i => "EventID=" + i.ToString(CultureInfo.InvariantCulture)).ToArray()) + ")";
        }

        /// <summary>Lê os eventos do XPath (mais recentes primeiro). filter decide no código o que fica.</summary>
        public static List<Occurrence> Query(string logName, string xpath, Func<EventRecord, bool> filter, out bool capped)
        {
            capped = false;
            var list = new List<Occurrence>();
            var query = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };
            using (var reader = new EventLogReader(query))
            {
                int read = 0;
                EventRecord rec;
                while ((rec = reader.ReadEvent()) != null)
                {
                    using (rec)
                    {
                        if (++read > Config.MaxEventsPerQuery) { capped = true; break; }
                        if (filter != null && !filter(rec)) continue;
                        list.Add(ToOccurrence(rec));
                    }
                }
            }
            return list;
        }

        /// <summary>Data do evento mais antigo ainda presente no log (null = log vazio).</summary>
        public static DateTime? OldestEvent(string logName)
        {
            var query = new EventLogQuery(logName, PathType.LogName) { ReverseDirection = false };
            using (var reader = new EventLogReader(query))
            using (EventRecord rec = reader.ReadEvent())
                return rec == null ? (DateTime?)null : rec.TimeCreated;
        }

        public static Occurrence ToOccurrence(EventRecord rec)
        {
            string src = Grouping.ShortSource(rec.ProviderName);
            string id = rec.Id.ToString(CultureInfo.InvariantCulture);
            string message = Message(rec);
            if (rec.Id == Config.KernelPowerId && string.Equals(rec.ProviderName, Config.KernelPowerProvider, StringComparison.OrdinalIgnoreCase))
                message = "Interpretação: " + KernelPower41(rec) + "\r\n" + message;
            return new Occurrence
            {
                Time = rec.TimeCreated,
                Source = rec.ProviderName,
                EventId = id,
                Level = LevelName(rec.Level),
                Message = message,
                GroupKey = src + " " + id,
                SummaryKey = src + " " + id,
            };
        }

        /// <summary>Kernel-Power 41: [0] BugcheckCode, [5] SleepInProgress, [6] PowerButtonTimestamp.</summary>
        static string KernelPower41(EventRecord rec)
        {
            try
            {
                var p = rec.Properties;
                return PowerEvents.KernelPower41Cause(
                    p.Count > 0 ? ToLong(p[0].Value) : 0,
                    p.Count > 6 ? ToLong(p[6].Value) : 0,
                    p.Count > 5 && ToLong(p[5].Value) != 0);
            }
            catch (Exception) { return "não foi possível ler os dados do evento"; }
        }

        static long ToLong(object v)
        {
            if (v == null) return 0;
            if (v is bool) return (bool)v ? 1 : 0;
            if (v is ulong) return unchecked((long)(ulong)v);
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        public static string Message(EventRecord rec)
        {
            string msg = null;
            try { msg = rec.FormatDescription(); } catch (Exception) { }
            if (!string.IsNullOrWhiteSpace(msg)) return msg.Trim();
            string props = PropertiesText(rec);
            return "(sem descrição formatada) " + (props.Length > 0 ? "dados: " + props : "");
        }

        public static string PropertiesText(EventRecord rec)
        {
            try
            {
                if (rec.Properties == null) return "";
                return string.Join(" | ", rec.Properties.Select(p => p.Value == null ? "" : Convert.ToString(p.Value, CultureInfo.InvariantCulture)).ToArray());
            }
            catch (Exception) { return ""; }
        }

        /// <summary>true se o texto dos dados do evento (ou, se preciso, a mensagem) contém algum dos termos.</summary>
        public static bool Mentions(EventRecord rec, IEnumerable<string> terms)
        {
            var list = terms.Where(t => !string.IsNullOrEmpty(t)).ToList();
            string props = PropertiesText(rec);
            if (list.Any(t => props.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            string msg = null;
            try { msg = rec.FormatDescription(); } catch (Exception) { }
            return msg != null && list.Any(t => msg.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static bool ContainsAny(string text, IEnumerable<string> terms)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return terms.Any(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static string LevelName(byte? level)
        {
            switch (level)
            {
                case 1: return "Crítico";
                case 2: return "Erro";
                case 3: return "Aviso";
                case 4: return "Informação";
                default: return null;
            }
        }
    }
}
