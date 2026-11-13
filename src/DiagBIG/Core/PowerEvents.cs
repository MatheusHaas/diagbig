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
