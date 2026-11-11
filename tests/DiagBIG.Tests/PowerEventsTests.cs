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
    }
}
