using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DiagBIG.Core
{
    public class ErrAnalysis
    {
        public List<Occurrence> Occurrences { get; } = new List<Occurrence>();
        /// <summary>Data da primeira linha datada do trecho lido (até onde a leitura alcança).</summary>
        public DateTime? FirstDate { get; set; }
        public DateTime? LastDate { get; set; }
        public bool HasDates { get; set; }
        public int LineCount { get; set; }
    }

    /// <summary>Filtra as linhas relevantes do .err respeitando o período.</summary>
    public static class ErrLogAnalyzer
    {
        // MySQL 5.5: "141022 10:00:00" ou "141022  9:05:03"
        static readonly Regex Mysql55Date = new Regex(@"^(\d{2})(\d{2})(\d{2})\s+(\d{1,2}):(\d{2}):(\d{2})", RegexOptions.Compiled);
        // MariaDB "2024-01-01 10:00:00" / MySQL 8 "2024-01-01T10:00:00.123456Z" ou "...-03:00"
        static readonly Regex IsoDate = new Regex(
            @"^(\d{4})-(\d{2})-(\d{2})[ T]\s*(\d{1,2}):(\d{2}):(\d{2})(\.\d+)?(Z|[+-]\d{2}:?\d{2})?", RegexOptions.Compiled);

        static readonly Regex LevelTag = new Regex(@"\[(ERROR|FATAL|Warning|Note|System)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex LeadingThreadId = new Regex(@"^\s*\d+\s+(?=\[)", RegexOptions.Compiled);
        static readonly Regex Hex = new Regex(@"0x[0-9a-fA-F]+", RegexOptions.Compiled);
        static readonly Regex LongNumber = new Regex(@"\d{4,}", RegexOptions.Compiled);
        static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>Data/hora no começo da linha (hora local), ou null.</summary>
        public static DateTime? TryParseLineDate(string line)
        {
            if (string.IsNullOrEmpty(line) || !char.IsDigit(line[0])) return null;
            int dateLen;
            return TryParseLineDate(line, out dateLen);
        }

        static DateTime? TryParseLineDate(string line, out int length)
        {
            length = 0;
            try
            {
                Match m = IsoDate.Match(line);
                if (m.Success)
                {
                    var dt = new DateTime(Int(m, 1), Int(m, 2), Int(m, 3), Int(m, 4), Int(m, 5), Int(m, 6));
                    if (m.Groups[7].Success)
                    {
                        string frac = (m.Groups[7].Value.Substring(1) + "0000000").Substring(0, 7);
                        dt = dt.AddTicks(long.Parse(frac, CultureInfo.InvariantCulture));
                    }
                    length = m.Length;
                    string zone = m.Groups[8].Value;
                    if (zone.Length == 0) return dt; // já está em hora local
                    TimeSpan offset = TimeSpan.Zero;
                    if (zone != "Z")
                    {
                        string z = zone.Replace(":", "");
                        offset = new TimeSpan(int.Parse(z.Substring(1, 2), CultureInfo.InvariantCulture), int.Parse(z.Substring(3, 2), CultureInfo.InvariantCulture), 0);
                        if (z[0] == '-') offset = offset.Negate();
                    }
                    return new DateTimeOffset(dt, offset).ToLocalTime().DateTime;
                }
                m = Mysql55Date.Match(line);
                if (m.Success)
                {
                    length = m.Length;
                    return new DateTime(2000 + Int(m, 1), Int(m, 2), Int(m, 3), Int(m, 4), Int(m, 5), Int(m, 6));
                }
            }
            catch (ArgumentException) { }
            return null;
        }

        static int Int(Match m, int g) { return int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture); }

        /// <summary>Padrão mais específico (mais longo) contido na linha, ou null.</summary>
        public static string MatchPattern(string line, IList<string> patterns)
        {
            string best = null;
            foreach (string p in patterns)
                if (line.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0 && (best == null || p.Length > best.Length))
                    best = p;
            return best;
        }

        static bool ContainsAny(string line, IList<string> patterns)
        {
            foreach (string p in patterns)
                if (line.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Mensagem sem data, thread id e números longos, para agrupar linhas iguais.</summary>
        public static string Normalize(string line)
        {
            int len;
            string s = line ?? "";
            if (TryParseLineDate(s, out len) != null) s = s.Substring(len);
            s = LeadingThreadId.Replace(s, "");
            s = Hex.Replace(s, "0x#");
            s = LongNumber.Replace(s, "#");
            s = Spaces.Replace(s, " ").Trim();
            if (s.Length > 160) s = s.Substring(0, 157) + "...";
            return s;
        }

        public static ErrAnalysis Analyze(string text, DateTime since)
        {
            return Analyze(text, since, Config.ErrPatterns, Config.CrashPatterns, Config.CrashContextLines);
        }

        public static ErrAnalysis Analyze(string text, DateTime since, IList<string> patterns, IList<string> crashPatterns, int contextLines)
        {
            var result = new ErrAnalysis();
            string[] lines = (text ?? "").Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            result.LineCount = lines.Length;

            // Data de cada linha; sem data herda a da anterior.
            var dates = new DateTime?[lines.Length];
            var ownDate = new bool[lines.Length];
            DateTime? current = null;
            for (int i = 0; i < lines.Length; i++)
            {
                DateTime? d = TryParseLineDate(lines[i]);
                if (d != null)
                {
                    ownDate[i] = true;
                    current = d;
                    if (result.FirstDate == null) result.FirstDate = d;
                    result.LastDate = d;
                }
                dates[i] = current;
            }
            result.HasDates = result.FirstDate != null;

            int contextUntil = -1; // linhas sem data até aqui já foram anexadas como contexto de um crash
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Trim().Length == 0) continue;
                if (i <= contextUntil && !ownDate[i]) continue;
                string pattern = MatchPattern(line, patterns);
                if (pattern == null) continue;

                // Linhas antes da primeira data (início cortado) só entram se o arquivo não tiver data nenhuma.
                DateTime? d = dates[i];
                bool inPeriod = result.HasDates ? (d != null && d.Value >= since) : true;

                if (!inPeriod) continue;

                // Contexto do crash: só é anexado; as linhas seguintes continuam sendo analisadas normalmente.
                var msg = new StringBuilder(line.TrimEnd());
                if (ContainsAny(line, crashPatterns))
                {
                    int end = Math.Min(lines.Length - 1, i + contextLines);
                    // Para antes de uma linha datada bem depois do crash (já é outro assunto).
                    for (int j = i + 1; j <= end; j++)
                        if (ownDate[j] && d != null && dates[j] > d.Value.AddMinutes(1)) { end = j - 1; break; }
                    while (end > i && lines[end].Trim().Length == 0) end--;
                    for (int j = i + 1; j <= end; j++)
                        msg.Append("\r\n").Append(lines[j].TrimEnd());
                    contextUntil = end;
                }

                Match lv = LevelTag.Match(line);
                result.Occurrences.Add(new Occurrence
                {
                    Time = d,
                    Source = ".err",
                    Level = lv.Success ? lv.Groups[1].Value.ToUpperInvariant() : null,
                    Message = msg.ToString(),
                    GroupKey = Normalize(line),
                    SummaryKey = pattern,
                });
            }
            return result;
        }
    }
}
