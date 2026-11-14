using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DiagBIG.Core
{
    /// <summary>Monta o TXT (cabeçalho, RESUMO e seções) a partir dos dados coletados.</summary>
    public static class ReportBuilder
    {
        public const string NL = "\r\n"; // Bloco de Notas antigo não entende só \n
        static readonly string Sep = new string('=', 78);
        const string Dash = " — ";

        // ---------------- formatação ----------------

        public static string Date(DateTime? d) { return d == null ? "(sem data)" : d.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture); }
        public static string DateTimeShort(DateTime? d) { return d == null ? "(sem data)" : d.Value.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture); }
        public static string DateTimeLong(DateTime? d) { return d == null ? "(sem data)" : d.Value.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture); }

        public static string FormatBytes(long bytes)
        {
            string[] units = { "bytes", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            string n = u == 0 ? bytes.ToString(CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
            return n + " " + units[u];
        }

        public static string StatusText(CheckStatus s)
        {
            switch (s)
            {
                case CheckStatus.Ok: return "OK";
                case CheckStatus.Atencao: return "ATENÇÃO";
                default: return "NÃO VERIFICADO";
            }
        }

        public static string FileName(string computerName, DateTime when)
        {
            var sb = new StringBuilder();
            foreach (char c in computerName ?? "PC")
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return "DiagBIG_" + sb + "_" + when.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt";
        }

        static readonly System.Text.RegularExpressions.Regex ReportName =
            new System.Text.RegularExpressions.Regex(@"^DiagBIG_(.+)_(\d{8}_\d{6})\.txt$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Relatórios a apagar: por PC, tudo além dos "keep" mais recentes (pela data no nome).
        /// Nomes fora do padrão DiagBIG_&lt;PC&gt;_&lt;aaaaMMdd_HHmmss&gt;.txt nunca entram.
        /// </summary>
        public static List<string> ReportsToDelete(IEnumerable<string> fileNames, int keep)
        {
            return fileNames
                .Select(n => new { Name = n, M = ReportName.Match(n ?? "") })
                .Where(x => x.M.Success)
                .GroupBy(x => x.M.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g => g.OrderByDescending(x => x.M.Groups[2].Value, StringComparer.Ordinal).Skip(Math.Max(keep, 1)))
                .Select(x => x.Name)
                .ToList();
        }

        // ---------------- RESUMO ----------------

        /// <summary>
        /// Linha de resumo a partir de blocos. Regra: achou ocorrência → ATENÇÃO; senão, se alguma parte
        /// falhou → NÃO VERIFICADO (com motivo); só é OK quando tudo foi verificado e nada foi achado.
        /// </summary>
        public static SummaryItem Summarize(string label, IList<ReportBlock> blocks)
        {
            var occ = blocks.SelectMany(b => b.Occurrences).ToList();
            var failed = blocks.Where(b => !b.Verified).ToList();
            bool isEvents = blocks.Count == 0 || blocks.Any(b => b.IsEvents);
            string notes = string.Join("; ", blocks.Where(b => !string.IsNullOrEmpty(b.SummaryNote)).Select(b => b.SummaryNote).ToArray());
            var item = new SummaryItem { Label = label };

            if (blocks.Count == 0)
            {
                item.Status = CheckStatus.NaoVerificado;
                item.Text = "nada foi verificado";
                return item;
            }

            string failText = string.Join("; ", failed.Select(b => b.NotVerifiedReason ?? "falha sem detalhe").ToArray());
            if (occ.Count > 0)
            {
                item.Status = CheckStatus.Atencao;
                item.Text = CountText(occ, isEvents);
                if (failed.Count > 0) item.Text += "; parte NÃO VERIFICADA: " + failText;
            }
            else if (failed.Count > 0)
            {
                item.Status = CheckStatus.NaoVerificado;
                item.Text = failText;
            }
            else
            {
                item.Status = CheckStatus.Ok;
                item.Text = "nenhuma ocorrência no período";
            }
            if (notes.Length > 0) item.Text += " (" + notes + ")";
            return item;
        }

        /// <summary>"12 eventos (disk 7: 10x, Ntfs 55: 2x), último em 10/11/2026 14:32"</summary>
        public static string CountText(IList<Occurrence> occ, bool isEvents)
        {
            var keys = Grouping.CountBySummaryKey(occ);
            var parts = keys.Take(4).Select(k => k.Key + ": " + k.Value + "x").ToList();
            if (keys.Count > 4) parts.Add("+" + (keys.Count - 4) + " outros");
            DateTime? last = occ.Max(o => o.Time);
            string noun = isEvents ? (occ.Count == 1 ? "evento" : "eventos") : (occ.Count == 1 ? "ocorrência" : "ocorrências");
            string lastWord = isEvents ? "último em " : "última em ";
            return occ.Count + " " + noun + " (" + string.Join(", ", parts.ToArray()) + "), " + lastWord + DateTimeShort(last);
        }

        // ---------------- TXT ----------------

        public static string BuildHeader(ReportData d)
        {
            var sb = new StringBuilder();
            sb.Append(Sep).Append(NL);
            sb.Append("  DIAGBIG v").Append(Config.Version).Append(Dash).Append("LOGS DE DISCO E BANCO PARA O SUPORTE").Append(NL);
            sb.Append(Sep).Append(NL).Append(NL);

            Title(sb, "IDENTIFICAÇÃO");
            sb.Append("  PC ............: ").Append(d.ComputerName).Append(NL);
            sb.Append("  WINDOWS .......: ").Append(d.WindowsVersion).Append(NL);
            sb.Append("  COLETA ........: ").Append(DateTimeLong(d.CollectedAt)).Append(NL);
            sb.Append("  PERÍODO .......: últimos ").Append(d.PeriodDays).Append(" dias (desde ").Append(DateTimeShort(d.Since)).Append(')').Append(NL);
            if (d.Raid != null || d.RaidError != null)
                sb.Append("  RAID ..........: ").Append(Storage.RaidLine(d.Raid, d.RaidError)).Append(NL);
            if (d.LastBoot != null)
                sb.Append("  LIGADO DESDE ..: ").Append(DateTimeShort(d.LastBoot)).Append(" (último boot completo)").Append(NL);
            if (!string.IsNullOrEmpty(d.OutputFileName))
                sb.Append("  ARQUIVO .......: ").Append(d.OutputFileName).Append(NL);
            sb.Append(NL);

            var osLines = OsRules.Lines(d.Os, d.CollectedAt);
            if (osLines.Count > 0)
            {
                Title(sb, "SISTEMA OPERACIONAL");
                foreach (string ol in osLines) sb.Append(ol).Append(NL);
                sb.Append(NL);
            }

            Title(sb, "UNIDADES");
            if (d.DrivesError != null) sb.Append("  NÃO VERIFICADO: ").Append(d.DrivesError).Append(NL);
            foreach (string dr in d.Drives) sb.Append("  ").Append(dr).Append(NL);

            sb.Append(NL);
            Title(sb, "COBERTURA DOS LOGS");
            foreach (LogCoverage c in d.LogCoverages)
            {
                sb.Append("  Log ").Append(c.LogName).Append(": ");
                if (c.Error != null) sb.Append("NÃO VERIFICADO").Append(Dash).Append(c.Error);
                else if (c.Oldest == null) sb.Append("log vazio (nenhum evento)");
                else
                {
                    sb.Append("log cobre desde ").Append(Date(c.Oldest));
                    if (c.Oldest.Value > d.Since)
                        sb.Append(Dash).Append("AVISO: não cobre o período escolhido (eventos anteriores foram sobrescritos ou apagados)");
                }
                sb.Append(NL);
            }

            if (d.LogClearedError != null)
                sb.Append("  Limpeza de log (Eventlog 104): NÃO VERIFICADO").Append(Dash).Append(d.LogClearedError).Append(NL);
            else if (d.LogClearedEvents.Count == 0)
                sb.Append("  Limpeza de log (Eventlog 104): nenhuma no período").Append(NL);
            else
                foreach (Occurrence o in Grouping.Latest(d.LogClearedEvents, 10))
                    sb.Append("  AVISO: log de eventos LIMPO em ").Append(DateTimeShort(o.Time)).Append(Dash)
                      .Append(OneLine(o.Message, 120)).Append(NL);

            foreach (ErrCoverage e in d.ErrCoverages)
            {
                sb.Append("  ").Append(e.Label).Append(": ");
                string how = e.Truncated
                    ? "últimos " + FormatBytes(e.BytesRead) + " de " + FormatBytes(e.FileSize)
                    : "arquivo inteiro, " + FormatBytes(e.FileSize);
                if (e.FirstDate == null)
                    sb.Append("leitura do .err sem linhas datadas (").Append(how).Append(')');
                else
                {
                    sb.Append("leitura do .err cobre desde ").Append(Date(e.FirstDate)).Append(" (").Append(how).Append(')');
                    if (e.Truncated && e.FirstDate.Value > d.Since)
                        sb.Append(Dash).Append("AVISO: a leitura não alcança o início do período");
                }
                sb.Append(NL);
            }
            sb.Append(NL);
            return sb.ToString();
        }

        public static string BuildSummary(ReportData d)
        {
            var sb = new StringBuilder();
            Title(sb, "RESUMO");
            foreach (SummaryItem s in d.Summary)
                sb.Append("  ").Append(s.Label).Append(": ").Append(StatusText(s.Status)).Append(Dash).Append(s.Text).Append(NL);
            sb.Append(NL);
            return sb.ToString();
        }

        public static string BuildHypotheses(ReportData d)
        {
            var sb = new StringBuilder();
            Title(sb, "CAUSAS PROVÁVEIS (ANÁLISE AUTOMÁTICA, INDÍCIO NÃO É PROVA)");
            if (d.Hypotheses.Count == 0) sb.Append("  Nenhum indício forte de causa nos logs lidos.").Append(NL);
            else sb.Append("  Qualquer uma das causas abaixo pode ser a origem; nenhuma está confirmada. Descartar uma por vez.").Append(NL);
            int n = 0;
            foreach (string h in d.Hypotheses) sb.Append("  ").Append(++n).Append(". ").Append(h).Append(NL);
            sb.Append(NL);
            return sb.ToString();
        }

        public static string BuildSections(ReportData d)
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (ReportSection sec in d.Sections)
            {
                n++;
                sb.Append(Sep).Append(NL).Append("  ").Append(n).Append(". ").Append(sec.Title.ToUpperInvariant()).Append(NL).Append(Sep).Append(NL).Append(NL);
                if (sec.Blocks.Count == 0) sb.Append("Nada a mostrar.").Append(NL).Append(NL);
                foreach (ReportBlock b in sec.Blocks) AppendBlock(sb, b);
            }
            sb.Append(Sep).Append(NL).Append("  FIM DO RELATÓRIO").Append(NL).Append(Sep).Append(NL);
            return sb.ToString();
        }

        static void AppendBlock(StringBuilder sb, ReportBlock b)
        {
            if (!string.IsNullOrEmpty(b.Title))
                sb.Append(">>> ").Append(b.Title.ToUpperInvariant()).Append(NL);
            foreach (string i in b.Info) sb.Append(i).Append(NL);
            foreach (string w in b.Warnings) sb.Append("AVISO: ").Append(w).Append(NL);

            if (!b.Verified)
            {
                sb.Append("NÃO VERIFICADO: ").Append(b.NotVerifiedReason ?? "falha sem detalhe").Append(NL).Append(NL);
                return;
            }
            if (b.InfoOnly) { sb.Append(NL); return; }
            if (b.Occurrences.Count == 0)
            {
                sb.Append("Nenhuma ocorrência encontrada no período.").Append(NL).Append(NL);
                return;
            }

            var groups = Grouping.Group(b.Occurrences);
            sb.Append(NL).Append("  AGRUPAMENTO (").Append(b.Occurrences.Count).Append(" no total, ").Append(groups.Count).Append(groups.Count == 1 ? " tipo)" : " tipos)").Append(NL);
            foreach (OccurrenceGroup g in groups)
            {
                sb.Append(string.Format(CultureInfo.InvariantCulture, "  {0,5}x  ", g.Count)).Append(g.Key).Append(NL);
                sb.Append("          primeira: ").Append(DateTimeShort(g.First)).Append("   última: ").Append(DateTimeShort(g.Last)).Append(NL);
                if (b.IsEvents)
                    sb.Append("          ex.: ").Append(OneLine(g.Sample, 110)).Append(NL);
            }

            var latest = Grouping.Latest(b.Occurrences, Config.DetailedOccurrences);
            sb.Append(NL).Append("  ÚLTIMAS ").Append(latest.Count).Append(latest.Count == 1 ? " OCORRÊNCIA" : " OCORRÊNCIAS").Append(" (mais recente primeiro)").Append(NL);
            foreach (Occurrence o in latest)
            {
                sb.Append("[").Append(DateTimeLong(o.Time)).Append("] ");
                if (b.IsEvents)
                {
                    sb.Append(o.Source);
                    if (!string.IsNullOrEmpty(o.EventId)) sb.Append(" ID ").Append(o.EventId);
                    if (!string.IsNullOrEmpty(o.Level)) sb.Append(" (").Append(o.Level).Append(')');
                }
                else
                {
                    sb.Append("encontrado: \"").Append(o.SummaryKey).Append('"');
                }
                sb.Append(NL);
                foreach (string line in SplitLines(o.Message))
                    sb.Append("    ").Append(line).Append(NL);
            }
            sb.Append(NL);
        }

        static void Title(StringBuilder sb, string title)
        {
            sb.Append(title).Append(NL).Append(new string('-', title.Length)).Append(NL);
        }

        public static string BuildFull(ReportData d)
        {
            return BuildHeader(d) + BuildSummary(d) + BuildHypotheses(d) + BuildSections(d);
        }

        /// <summary>Cabeçalho + resumo, cortado em no máximo maxChars (para colar no caso).</summary>
        public static string BuildResumo(ReportData d, int maxChars = Config.ResumoMaxChars)
        {
            return Truncate((BuildHeader(d) + BuildSummary(d) + BuildHypotheses(d)).TrimEnd() + NL, maxChars);
        }

        public static string Truncate(string text, int maxChars)
        {
            if (text.Length <= maxChars) return text;
            string suffix = NL + "[... cortado em " + maxChars + " caracteres; veja o TXT completo]";
            int limit = maxChars - suffix.Length;
            if (limit < 0) return text.Substring(0, maxChars);
            int cut = text.LastIndexOf('\n', limit - 1);
            string head = cut > 0 ? text.Substring(0, cut).TrimEnd('\r') : text.Substring(0, limit);
            return head + suffix;
        }

        static IEnumerable<string> SplitLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return new[] { "(sem mensagem)" };
            return s.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n').Select(l => l.TrimEnd());
        }

        public static string OneLine(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string one = string.Join(" ", s.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            return one.Length > max ? one.Substring(0, max - 3) + "..." : one;
        }
    }
}
