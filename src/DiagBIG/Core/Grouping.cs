using System;
using System.Collections.Generic;
using System.Linq;

namespace DiagBIG.Core
{
    public class OccurrenceGroup
    {
        public string Key { get; set; }
        public int Count { get; set; }
        public DateTime? First { get; set; }
        public DateTime? Last { get; set; }
        public string Sample { get; set; }
    }

    public static class Grouping
    {
        public static List<OccurrenceGroup> Group(IEnumerable<Occurrence> items)
        {
            return items
                .GroupBy(o => o.GroupKey ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => new OccurrenceGroup
                {
                    Key = g.First().GroupKey ?? "",
                    Count = g.Count(),
                    First = g.Min(o => o.Time),
                    Last = g.Max(o => o.Time),
                    Sample = g.OrderByDescending(o => o.Time ?? DateTime.MinValue).First().Message,
                })
                .OrderByDescending(g => g.Count)
                .ThenByDescending(g => g.Last ?? DateTime.MinValue)
                .ToList();
        }

        /// <summary>Contagem por chave curta, maior primeiro.</summary>
        public static List<KeyValuePair<string, int>> CountBySummaryKey(IEnumerable<Occurrence> items)
        {
            return items
                .GroupBy(o => o.SummaryKey ?? o.GroupKey ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => new KeyValuePair<string, int>(g.First().SummaryKey ?? g.First().GroupKey ?? "", g.Count()))
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Mais recentes primeiro; sem data vão para o fim.</summary>
        public static List<Occurrence> Latest(IEnumerable<Occurrence> items, int max)
        {
            return items
                .Select((o, i) => new { o, i })
                .OrderByDescending(x => x.o.Time.HasValue)
                .ThenByDescending(x => x.o.Time ?? DateTime.MinValue)
                .ThenByDescending(x => x.i)
                .Take(max)
                .Select(x => x.o)
                .ToList();
        }

        /// <summary>"Microsoft-Windows-Ntfs" → "Ntfs".</summary>
        public static string ShortSource(string provider)
        {
            if (string.IsNullOrEmpty(provider)) return "?";
            const string prefix = "Microsoft-Windows-";
            return provider.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? provider.Substring(prefix.Length) : provider;
        }
    }
}
