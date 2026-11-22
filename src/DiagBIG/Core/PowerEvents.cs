using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DiagBIG.Core
{
    public enum PowerKind { Boot, CleanShutdown, Requested, Unexpected, BlueScreen, Sleep, Wake, Other }

    /// <summary>Classifica e descreve os eventos de liga/desliga do log Sistema.</summary>
    public static class PowerEvents
    {
        public static PowerKind Classify(string provider, string eventId)
        {
            string p = provider ?? "";
            int id;
            int.TryParse(eventId, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
            if (Is(p, Config.EventLogProvider))
            {
                if (id == 6005) return PowerKind.Boot;
                if (id == 6006) return PowerKind.CleanShutdown;
                if (id == Config.UnexpectedShutdownId) return PowerKind.Unexpected;
            }
            if (Is(p, Config.KernelPowerProvider))
            {
                if (id == Config.KernelPowerId) return PowerKind.Unexpected;
                if (id == 42) return PowerKind.Sleep;
            }
            if (Is(p, "User32") && id == 1074) return PowerKind.Requested;
            if ((Is(p, "Microsoft-Windows-WER-SystemErrorReporting") || Is(p, "BugCheck")) && id == 1001) return PowerKind.BlueScreen;
            if (Is(p, "Microsoft-Windows-Power-Troubleshooter") && id == 1) return PowerKind.Wake;
            return PowerKind.Other;
        }

        /// <summary>Entra no bloco DESLIGAMENTOS INESPERADOS (e deixa o resumo em ATENÇÃO).</summary>
        public static bool IsUnexpected(PowerKind k) { return k == PowerKind.Unexpected || k == PowerKind.BlueScreen; }

        public static string Label(PowerKind k)
        {
            switch (k)
            {
                case PowerKind.Boot: return "LIGOU (Windows iniciou)";
                case PowerKind.CleanShutdown: return "DESLIGOU CORRETAMENTE";
                case PowerKind.Requested: return "DESLIGAR/REINICIAR SOLICITADO";
                case PowerKind.Unexpected: return "DESLIGAMENTO INESPERADO";
                case PowerKind.BlueScreen: return "TELA AZUL (BSOD)";
                case PowerKind.Sleep: return "ENTROU EM SUSPENSÃO/HIBERNAÇÃO";
                case PowerKind.Wake: return "VOLTOU DA SUSPENSÃO";
                default: return "OUTRO";
            }
        }

        /// <summary>
        /// Interpreta os dados do Kernel-Power 41: código de tela azul, botão de energia ou corte/travamento.
        /// </summary>
        public static string KernelPower41Cause(long bugcheckCode, long powerButtonTimestamp, bool sleepInProgress)
        {
            if (bugcheckCode != 0)
            {
                string desc = Bugchecks.Describe(bugcheckCode);
                return "TELA AZUL (BugcheckCode 0x" + bugcheckCode.ToString("X", CultureInfo.InvariantCulture) + ")" +
                       (desc.Length > 0 ? " " + desc : "");
            }
            if (powerButtonTimestamp != 0)
                return "BOTÃO DE ENERGIA pressionado/segurado para forçar o desligamento";
            if (sleepInProgress)
                return "caiu durante a entrada em suspensão/hibernação";
            return "sem tela azul e sem botão de energia: provável QUEDA/CORTE DE ENERGIA, reset ou travamento total";
        }

        static bool IsKp41(Occurrence o) { return Is(o.Source, Config.KernelPowerProvider) && o.EventId == Config.KernelPowerId.ToString(CultureInfo.InvariantCulture); }
        static bool Is6008(Occurrence o) { return Is(o.Source, Config.EventLogProvider) && o.EventId == Config.UnexpectedShutdownId.ToString(CultureInfo.InvariantCulture); }

        /// <summary>Eventos EventLog 6008 que têm um Kernel-Power 41 até 5 min de distância: são o mesmo incidente.</summary>
        public static bool IsDuplicate6008(Occurrence o, IList<Occurrence> all)
        {
            if (!Is6008(o) || o.Time == null) return false;
            return all.Any(k => IsKp41(k) && k.Time != null && Math.Abs((k.Time.Value - o.Time.Value).TotalMinutes) <= 5);
        }

        /// <summary>Lista sem os 6008 que repetem um Kernel-Power 41.</summary>
        public static List<Occurrence> MergeUnexpected(IEnumerable<Occurrence> events)
        {
            var all = events.ToList();
            return all.Where(o => !IsDuplicate6008(o, all)).ToList();
        }

        /// <summary>Incidentes de desligamento inesperado (6008, Kernel-Power 41 e tela azul no mesmo momento contam 1).</summary>
        public static int UnexpectedIncidents(IEnumerable<Occurrence> events)
        {
            var times = events.Where(o => IsUnexpected(Classify(o.Source, o.EventId))).Select(o => o.Time).ToList();
            int semData = times.Count(t => t == null);
            int n = semData;
            DateTime? last = null;
            foreach (DateTime t in times.Where(x => x != null).Select(x => x.Value).OrderBy(x => x))
            {
                if (last == null || (t - last.Value).TotalMinutes > 5) n++;
                last = t;
            }
            return n;
        }

        /// <summary>Linhas da linha do tempo, mais recente primeiro.</summary>
        public static List<string> Timeline(IEnumerable<Occurrence> events, int max)
        {
            var lines = new List<string>();
            foreach (Occurrence o in Grouping.Latest(events, max))
            {
                PowerKind k = Classify(o.Source, o.EventId);
                string detail = Detail(k, o.Message);
                lines.Add(string.Format(CultureInfo.InvariantCulture, "  {0}  {1,-30} {2} {3}",
                    ReportBuilder.DateTimeLong(o.Time), Label(k), Grouping.ShortSource(o.Source), o.EventId) +
                    (detail.Length > 0 ? ReportBuilder.NL + "                         " + detail : ""));
            }
            return lines;
        }

        /// <summary>
        /// Linha do tempo enxuta: eventos repetidos viram uma linha com a lista de horários.
        /// Desligamentos inesperados e telas azuis ficam de fora (já estão no bloco de desligamentos inesperados).
        /// </summary>
        public static List<string> TimelineGrouped(IEnumerable<Occurrence> events)
        {
            var lines = new List<string>();
            var groups = events
                .Select(o => new { o, k = Classify(o.Source, o.EventId) })
                .Where(x => !IsUnexpected(x.k))
                .GroupBy(x => new { x.k, Src = Grouping.ShortSource(x.o.Source), Id = x.o.EventId, Det = x.k == PowerKind.Requested ? "" : Detail(x.k, x.o.Message) })
                .OrderByDescending(g => g.Max(x => x.o.Time ?? DateTime.MinValue));
            foreach (var g in groups)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(string.Format(CultureInfo.InvariantCulture, "  {0,4}x  {1} ({2} {3})", g.Count(), Label(g.Key.k), g.Key.Src, g.Key.Id)).Append(ReportBuilder.NL);
                if (g.Key.Det.Length > 0) ReportBuilder.AppendWrapped(sb, g.Key.Det, "          ");
                ReportBuilder.AppendWrapped(sb, "horários: " + Grouping.TimesText(g.Select(x => x.o.Time), Config.MaxTimesPerGroup), "          ");
                if (g.Key.k == PowerKind.Requested)
                {
                    // 1074: quem pediu, por quê e o comentário (os mais recentes).
                    foreach (var x in g.OrderByDescending(y => y.o.Time ?? DateTime.MinValue).Take(Config.MaxTimesPerGroup))
                    {
                        string d = Describe1074(x.o.Message);
                        if (d.Length == 0) continue;
                        ReportBuilder.AppendWrapped(sb, (x.o.Time == null ? "(sem horário)" : ReportBuilder.DateTimeLong(x.o.Time)) + ": " + d, "          ");
                    }
                }
                lines.Add(sb.ToString().TrimEnd('\r', '\n'));
            }
            return lines;
        }

        static string Grab(string msg, string pattern)
        {
            var m = System.Text.RegularExpressions.Regex.Match(msg ?? "", pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        /// <summary>
        /// Resume a mensagem do User32 1074 (pt-BR ou inglês): processo, usuário, motivo, tipo e comentário.
        /// Se não reconhecer nada, devolve a mensagem em uma linha.
        /// </summary>
        public static string Describe1074(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "";
            const string stop = @"(?=\s*(?:C[oó]digo do motivo|Reason Code|Tipo de (?:desligamento|encerramento)|Shutdown Type|Coment[aá]rio|Comment)\s*:|$)";
            string proc = Grab(message, @"(?:processo|process)\s+(\S.*?)\s+\(");
            int cut = proc.LastIndexOfAny(new[] { '\\', '/' });
            if (cut >= 0) proc = proc.Substring(cut + 1);
            string user = Grab(message, @"(?:em nome d[eo]s?\s+(?:usu[aá]rio\s+)?|pel[oa]\s+usu[aá]rio\s+|on behalf of user\s+|by user\s+)(\S+)");
            string reason = Grab(message, @"(?:seguinte motivo|este motivo|following reason|this reason)\s*:\s*(.*?)" + stop);
            string type = Grab(message, @"(?:Tipo de (?:desligamento|encerramento)|Shutdown Type)\s*:\s*(.*?)" + stop);
            string comment = Grab(message, @"(?:Coment[aá]rio|Comment)\s*:\s*(.*)$");
            var parts = new List<string>();
            if (user.Length > 0) parts.Add("usuário " + user);
            if (proc.Length > 0) parts.Add("processo " + proc);
            if (type.Length > 0) parts.Add("tipo: " + ReportBuilder.OneLine(type, 40));
            if (reason.Length > 0) parts.Add("motivo: " + ReportBuilder.OneLine(reason, 100));
            if (comment.Length > 0) parts.Add("comentário: " + ReportBuilder.OneLine(comment, 100));
            return parts.Count > 0 ? string.Join("; ", parts.ToArray()) : ReportBuilder.OneLine(message, 200);
        }

        static string Detail(PowerKind k, string message)
        {
            switch (k)
            {
                case PowerKind.Boot:
                case PowerKind.CleanShutdown:
                case PowerKind.Sleep:
                case PowerKind.Wake:
                    return "";
                case PowerKind.BlueScreen:
                    {
                        long? code = Bugchecks.FromOccurrence(new Occurrence { Source = "Microsoft-Windows-WER-SystemErrorReporting", EventId = "1001", Message = message });
                        string desc = code == null ? "" : Bugchecks.Describe(code.Value);
                        return (desc.Length > 0 ? "0x" + code.Value.ToString("X", CultureInfo.InvariantCulture) + " " + desc + " | " : "") + ReportBuilder.OneLine(message, 220);
                    }
                default:
                    return ReportBuilder.OneLine(message, 220);
            }
        }

        /// <summary>Linha "LIGA/DESLIGA" do resumo.</summary>
        public static SummaryItem Summarize(IList<Occurrence> events, DateTime? lastBoot)
        {
            var kinds = events.Select(o => Classify(o.Source, o.EventId)).ToList();
            int boots = kinds.Count(k => k == PowerKind.Boot);
            int clean = kinds.Count(k => k == PowerKind.CleanShutdown);
            int requested = kinds.Count(k => k == PowerKind.Requested);
            int kp41 = events.Count(o => Is(o.Source, Config.KernelPowerProvider) && o.EventId == Config.KernelPowerId.ToString(CultureInfo.InvariantCulture));
            int ev6008 = events.Count(o => Is(o.Source, Config.EventLogProvider) && o.EventId == Config.UnexpectedShutdownId.ToString(CultureInfo.InvariantCulture));
            int bsod = kinds.Count(k => k == PowerKind.BlueScreen);
            int unexpected = Math.Max(kp41, ev6008); // 41 e 6008 costumam ser o mesmo incidente

            var parts = new List<string>
            {
                boots + (boots == 1 ? " inicialização" : " inicializações"),
                clean + (clean == 1 ? " desligamento correto" : " desligamentos corretos"),
                requested + " solicitado(s) (User32 1074)",
                unexpected + (unexpected == 1 ? " inesperado" : " inesperados"),
            };
            if (bsod > 0) parts.Add(bsod + " tela(s) azul(is)");
            string text = string.Join(", ", parts.ToArray());
            if (lastBoot != null) text += "; ligado desde " + ReportBuilder.DateTimeShort(lastBoot);

            return new SummaryItem
            {
                Label = "LIGA/DESLIGA",
                Status = unexpected > 0 || bsod > 0 ? CheckStatus.Atencao : CheckStatus.Ok,
                Text = text,
            };
        }

        static bool Is(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
