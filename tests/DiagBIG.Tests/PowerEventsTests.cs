using System;
using System.Linq;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    public class PowerEventsTests
    {
        static Occurrence Ev(string src, int id, DateTime t, string msg = "") =>
            new Occurrence { Source = src, EventId = id.ToString(), Time = t, Message = msg };

        [Theory]
        [InlineData("EventLog", 6005, PowerKind.Boot)]
        [InlineData("EventLog", 6006, PowerKind.CleanShutdown)]
        [InlineData("EventLog", 6008, PowerKind.Unexpected)]
        [InlineData("Microsoft-Windows-Kernel-Power", 41, PowerKind.Unexpected)]
        [InlineData("Microsoft-Windows-Kernel-Power", 42, PowerKind.Sleep)]
        [InlineData("User32", 1074, PowerKind.Requested)]
        [InlineData("Microsoft-Windows-WER-SystemErrorReporting", 1001, PowerKind.BlueScreen)]
        [InlineData("BugCheck", 1001, PowerKind.BlueScreen)]
        [InlineData("Microsoft-Windows-Power-Troubleshooter", 1, PowerKind.Wake)]
        [InlineData("disk", 7, PowerKind.Other)]
        public void Classify(string src, int id, PowerKind expected) => Assert.Equal(expected, PowerEvents.Classify(src, id.ToString()));

        [Fact]
        public void KernelPower41Interpretation()
        {
            Assert.Contains("TELA AZUL (BugcheckCode 0x9F)", PowerEvents.KernelPower41Cause(0x9F, 0, false));
            Assert.Contains("BOTÃO DE ENERGIA", PowerEvents.KernelPower41Cause(0, 132456789012345678, false));
            Assert.Contains("QUEDA/CORTE DE ENERGIA", PowerEvents.KernelPower41Cause(0, 0, false));
            Assert.Contains("suspensão", PowerEvents.KernelPower41Cause(0, 0, true));
        }

        [Fact]
        public void SummaryCountsAndStatus()
        {
            var t = new DateTime(2026, 11, 1, 8, 0, 0);
            var ev = new[]
            {
                Ev("EventLog", 6005, t), Ev("EventLog", 6006, t.AddHours(10)),
                Ev("User32", 1074, t.AddHours(10), "O processo C:\\Windows\\system32\\winlogon.exe iniciou a reinicialização em nome do usuário LOJA\\caixa"),
                Ev("EventLog", 6005, t.AddDays(1)),
                Ev("Microsoft-Windows-Kernel-Power", 41, t.AddDays(2)), Ev("EventLog", 6008, t.AddDays(2)), Ev("EventLog", 6005, t.AddDays(2)),
            };
            var s = PowerEvents.Summarize(ev, new DateTime(2026, 11, 3, 8, 0, 0));
            Assert.Equal(CheckStatus.Atencao, s.Status);
            Assert.Equal("3 inicializações, 1 desligamento correto, 1 solicitado(s) (User32 1074), 1 inesperado; ligado desde 03/11/2026 08:00", s.Text);

            var ok = PowerEvents.Summarize(ev.Take(4).ToList(), null);
            Assert.Equal(CheckStatus.Ok, ok.Status);

            var lines = PowerEvents.Timeline(ev, 60);
            Assert.Equal(7, lines.Count);
            Assert.Contains("DESLIGAMENTO INESPERADO", lines[0] + lines[1] + lines[2]);
            Assert.Contains(lines, l => l.Contains("em nome do usuário LOJA\\caixa"));
        }

        [Fact]
        public void MergeUnexpected_Joins6008WithKernelPower41()
        {
            var t = new DateTime(2026, 11, 1, 8, 0, 0);
            var ev = new System.Collections.Generic.List<Occurrence>
            {
                Ev("Microsoft-Windows-Kernel-Power", 41, t), Ev("EventLog", 6008, t.AddMinutes(1)),
                Ev("EventLog", 6008, t.AddDays(1)),
            };
            Assert.Equal(2, PowerEvents.MergeUnexpected(ev).Count);
            Assert.Equal(2, PowerEvents.UnexpectedIncidents(ev));
        }

        [Fact]
        public void TimelineGrouped_CollapsesRepeatsWithTimes_AndSkipsUnexpected()
        {
            var t = new DateTime(2026, 11, 1, 8, 0, 0);
            var ev = new System.Collections.Generic.List<Occurrence>
            {
                Ev("EventLog", 6005, t), Ev("EventLog", 6005, t.AddHours(5)), Ev("EventLog", 6005, t.AddDays(1)),
                Ev("Microsoft-Windows-Kernel-Power", 41, t.AddDays(2)),
            };
            var lines = PowerEvents.TimelineGrouped(ev);
            Assert.Single(lines);
            Assert.Contains("3x  LIGOU", lines[0]);
            Assert.Contains("01/11 08:00, 13:00; 02/11 08:00", lines[0]);
        }

        const string Msg1074Pt = "O processo C:\\Windows\\system32\\shutdown.exe (BIG-SOFT) iniciou a reinicialização do computador BIG-SOFT em nome de BIG-SOFT\\tecnico pelo seguinte motivo: Sistema operacional: Recuperação (planejada)\r\n Código do motivo: 0x80020002\r\n Tipo de desligamento: reiniciar\r\n Comentário: teste DiagBIG";
        const string Msg1074En = "The process C:\\Windows\\system32\\shutdown.exe (HOST) has initiated the restart of computer HOST on behalf of user DOM\\ana for the following reason: Other (Planned)\n Reason Code: 0x80000000\n Shutdown Type: restart\n Comment: teste";

        [Fact]
        public void Describe1074_Portuguese_ExtractsUserReasonTypeComment()
        {
            string d = PowerEvents.Describe1074(Msg1074Pt);
            Assert.Contains("usuário BIG-SOFT\\tecnico", d);
            Assert.Contains("processo shutdown.exe", d);
            Assert.Contains("tipo: reiniciar", d);
            Assert.Contains("motivo: Sistema operacional: Recuperação (planejada)", d);
            Assert.Contains("comentário: teste DiagBIG", d);
            Assert.DoesNotContain("0x80020002", d);
        }

        [Fact]
        public void Describe1074_English_And_Fallback()
        {
            string d = PowerEvents.Describe1074(Msg1074En);
            Assert.Contains("usuário DOM\\ana", d);
            Assert.Contains("motivo: Other (Planned)", d);
            Assert.Contains("comentário: teste", d);
            Assert.Equal("texto qualquer", PowerEvents.Describe1074("texto qualquer"));
            Assert.Equal("", PowerEvents.Describe1074(""));
        }

        [Fact]
        public void TimelineGrouped_Requested_ShowsDetailPerEvent()
        {
            var t = new DateTime(2026, 11, 22, 4, 28, 0);
            var o = Ev("User32", 1074, t); o.Message = Msg1074Pt;
            var lines = PowerEvents.TimelineGrouped(new[] { o, Ev("EventLog", 6005, t) });
            string all = string.Join("\n", lines);
            Assert.Contains("DESLIGAR/REINICIAR SOLICITADO", all);
            Assert.Contains("comentário: teste DiagBIG", System.Text.RegularExpressions.Regex.Replace(all, @"\s+", " "));
            Assert.Contains("usuário BIG-SOFT", all);
        }

        [Fact]
        public void Describe1074_RealWindows11Message_PeloUsuario()
        {
            string m = "O processo C:\\WINDOWS\\system32\\shutdown.exe (BIG-SOFT-WINDOW) iniciou reiniciar do computador BIG-SOFT-WINDOW pelo usuário BIG-SOFT-WINDOW\\vboxuser por este motivo: Aplicativo: manutenção (planejada)\r\n  Código do motivo: 0x80040001\r\n  Tipo de encerramento: reiniciar\r\n  Comentário: teste DiagBIG 1.7.1";
            string d = PowerEvents.Describe1074(m);
            Assert.Equal("usuário BIG-SOFT-WINDOW\\vboxuser; processo shutdown.exe; tipo: reiniciar; motivo: Aplicativo: manutenção (planejada); comentário: teste DiagBIG 1.7.1", d);
        }
    }
}
