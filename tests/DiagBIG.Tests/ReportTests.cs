using System;
using System.Collections.Generic;
using System.Linq;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    public class ReportTests
    {
        static readonly DateTime Now = new DateTime(2026, 11, 11, 9, 30, 0);

        static Occurrence Ev(string src, int id, DateTime t, string msg = "mensagem") => new Occurrence
        {
            Time = t, Source = src, EventId = id.ToString(), Level = "Erro", Message = msg,
            GroupKey = Grouping.ShortSource(src) + " " + id, SummaryKey = Grouping.ShortSource(src) + " " + id,
        };

        static ReportBlock Block(params Occurrence[] occ)
        {
            var b = new ReportBlock { Title = "Eventos" };
            b.Occurrences.AddRange(occ);
            return b;
        }

        static ReportData Data()
        {
            var d = new ReportData
            {
                ComputerName = "CAIXA01", WindowsVersion = "Windows 10 Pro", CollectedAt = Now,
                PeriodDays = 30, Since = Now.AddDays(-30), OutputFileName = ReportBuilder.FileName("CAIXA01", Now),
            };
            d.Drives.Add(@"C: 10,0 GB livres de 100,0 GB (10%)");
            return d;
        }

        [Fact]
        public void SummaryText_LikeSpecExample()
        {
            var occ = new List<Occurrence>();
            for (int i = 0; i < 10; i++) occ.Add(Ev("disk", 7, new DateTime(2026, 11, 1).AddHours(i)));
            occ.Add(Ev("Microsoft-Windows-Ntfs", 55, new DateTime(2026, 11, 10, 14, 32, 0)));
            occ.Add(Ev("Ntfs", 55, new DateTime(2026, 11, 2)));

            var s = ReportBuilder.Summarize("DISCO", new[] { Block(occ.ToArray()) });
            Assert.Equal(CheckStatus.Atencao, s.Status);
            Assert.Equal("12 eventos (disk 7: 10x, Ntfs 55: 2x), último em 10/11/2026 14:32", s.Text);
        }

        [Fact]
        public void Summary_ThreeStates_NeverOkWhenNotVerified()
        {
            Assert.Equal(CheckStatus.Ok, ReportBuilder.Summarize("X", new[] { Block() }).Status);

            var nv = ReportBuilder.Summarize("X", new[] { Block(), ReportBlock.NotVerified("b", "sem permissão") });
            Assert.Equal(CheckStatus.NaoVerificado, nv.Status);
            Assert.Contains("sem permissão", nv.Text);

            var partial = ReportBuilder.Summarize("X", new[] { Block(Ev("disk", 7, Now)), ReportBlock.NotVerified("b", "log não encontrado") });
            Assert.Equal(CheckStatus.Atencao, partial.Status);
            Assert.Contains("log não encontrado", partial.Text);

            Assert.Equal(CheckStatus.NaoVerificado, ReportBuilder.Summarize("X", new ReportBlock[0]).Status);
        }

        [Fact]
        public void ErrSummaryUsesFeminineWording()
        {
            var b = new ReportBlock { IsEvents = false };
            b.Occurrences.Add(new Occurrence { Time = Now, SummaryKey = "corrupt", GroupKey = "x", Message = "x" });
            Assert.Equal("1 ocorrência (corrupt: 1x), última em 11/11/2026 09:30", ReportBuilder.Summarize("BANCO", new[] { b }).Text);
        }

        [Fact]
        public void FullReport_StructureAndMessages()
        {
            var d = Data();
            d.LogCoverages.Add(new LogCoverage { LogName = "Sistema", Oldest = new DateTime(2026, 11, 1) });
            d.LogCoverages.Add(new LogCoverage { LogName = "Aplicativo", Oldest = new DateTime(2026, 1, 1) });
            d.LogClearedEvents.Add(Ev("Microsoft-Windows-Eventlog", 104, new DateTime(2026, 11, 2, 8, 0, 0), "O log Sistema foi limpo."));
            d.ErrCoverages.Add(new ErrCoverage { Label = "BANCO (MariaDB)", FirstDate = new DateTime(2026, 11, 5), Truncated = true, FileSize = 300L * 1024 * 1024, BytesRead = 10L * 1024 * 1024 });

            var occ = Enumerable.Range(0, 40).Select(i => Ev("disk", 7, new DateTime(2026, 11, 1).AddHours(i), "msg " + i)).ToArray();
            var disk = Block(occ);
            var empty = new ReportBlock { Title = "Desligamentos" };
            var nv = ReportBlock.NotVerified("BANCO", "nenhum serviço encontrado", false);

            var sec1 = new ReportSection { Title = "DISCO / NTFS" }; sec1.Blocks.Add(disk);
            var sec2 = new ReportSection { Title = "DESLIGAMENTOS INESPERADOS" }; sec2.Blocks.Add(empty);
            var sec3 = new ReportSection { Title = "BANCO" }; sec3.Blocks.Add(nv);
            d.Sections.AddRange(new[] { sec1, sec2, sec3 });
            d.Summary.Add(ReportBuilder.Summarize("DISCO", new[] { disk }));
            d.Summary.Add(ReportBuilder.Summarize("DESLIGAMENTOS", new[] { empty }));
            d.Summary.Add(ReportBuilder.Summarize("BANCO", new[] { nv }));

            string txt = ReportBuilder.BuildFull(d);

            Assert.Contains("  PC ............: CAIXA01", txt);
            Assert.Contains("IDENTIFICAÇÃO\r\n-------------", txt);
            Assert.Contains("COBERTURA DOS LOGS", txt);
            Assert.Contains("PERÍODO .......: últimos 30 dias (desde 12/10/2026 09:30)", txt);
            Assert.Contains("Log Sistema: log cobre desde 01/11/2026 — AVISO: não cobre o período escolhido", txt);
            Assert.Contains("Log Aplicativo: log cobre desde 01/01/2026\r\n", txt);
            Assert.Contains("AVISO: log de eventos LIMPO em 02/11/2026 08:00", txt);
            Assert.Contains("BANCO (MariaDB): leitura do .err cobre desde 05/11/2026 (últimos 10,0 MB de 300,0 MB) — AVISO", txt);

            Assert.Contains("  DISCO: ATENÇÃO — 40 eventos (disk 7: 40x), último em 02/11/2026 15:00", txt);
            Assert.Contains("DESLIGAMENTOS: OK — nenhuma ocorrência no período", txt);
            Assert.Contains("BANCO: NÃO VERIFICADO — nenhum serviço encontrado", txt);
            Assert.True(txt.IndexOf("RESUMO") < txt.IndexOf("1. DISCO / NTFS"));

            Assert.Contains(new string('=', 78) + "\r\n  1. DISCO / NTFS\r\n" + new string('=', 78), txt);
            Assert.Contains("  3. BANCO\r\n", txt);
            Assert.Contains(">>> EVENTOS\r\n", txt);
            Assert.Contains("AGRUPAMENTO (40 no total, 1 tipo)", txt);
            Assert.Contains("FIM DO RELATÓRIO", txt);
            Assert.Contains("Nenhuma ocorrência encontrada no período.", txt);
            Assert.Contains("NÃO VERIFICADO: nenhum serviço encontrado", txt);
            Assert.Contains("   40x  disk 7", txt);
            Assert.Contains("ÚLTIMAS 30 OCORRÊNCIAS", txt);
            Assert.Contains("msg 39", txt);       // mais recente aparece
            Assert.DoesNotContain("msg 9\r\n", txt); // 10 mais antigas ficam só no agrupamento
            Assert.DoesNotContain("\n", txt.Replace("\r\n", ""));
        }

        [Fact]
        public void Resumo_IsHeaderPlusSummary_AtMost3500()
        {
            var d = Data();
            d.Summary.Add(new SummaryItem { Label = "DISCO", Status = CheckStatus.Ok, Text = "nenhuma ocorrência no período" });
            var sec = new ReportSection { Title = "DISCO / NTFS" };
            sec.Blocks.Add(Block(Ev("disk", 7, Now)));
            d.Sections.Add(sec);

            string r = ReportBuilder.BuildResumo(d);
            Assert.Contains("RESUMO", r);
            Assert.Contains("DISCO: OK", r);
            Assert.DoesNotContain("DISCO / NTFS", r);

            for (int i = 0; i < 100; i++)
                d.Summary.Add(new SummaryItem { Label = "BANCO (svc" + i + ")", Status = CheckStatus.Atencao, Text = new string('x', 60) });
            r = ReportBuilder.BuildResumo(d);
            Assert.True(r.Length <= 3500, "tamanho " + r.Length);
            Assert.EndsWith("veja o TXT completo]", r);
        }

        [Fact]
        public void FileNameAndBytes()
        {
            Assert.Equal("DiagBIG_CAIXA01_20261111_093000.txt", ReportBuilder.FileName("CAIXA01", Now));
            Assert.Equal("DiagBIG_PC_X_20261111_093000.txt", ReportBuilder.FileName("PC X", Now));
            Assert.Equal("10,0 MB", ReportBuilder.FormatBytes(10L * 1024 * 1024));
            Assert.Equal("512 bytes", ReportBuilder.FormatBytes(512));
        }

        [Fact]
        public void LatestPutsUndatedLast()
        {
            var l = Grouping.Latest(new[]
            {
                new Occurrence { Message = "sem" },
                new Occurrence { Time = new DateTime(2026, 1, 1), Message = "velho" },
                new Occurrence { Time = new DateTime(2026, 2, 1), Message = "novo" },
            }, 30);
            Assert.Equal(new[] { "novo", "velho", "sem" }, l.Select(o => o.Message));
        }
    }
}

namespace DiagBIG.Tests
{
    public class ReportRetentionTests
    {
        [Fact]
        public void KeepsLastTenPerPc_AndNeverTouchesOtherFiles()
        {
            var names = new List<string>();
            for (int i = 1; i <= 12; i++) names.Add(string.Format("DiagBIG_CAIXA01_202611{0:00}_093000.txt", i));
            names.Add("DiagBIG_CAIXA02_20261101_080000.txt");
            names.Add("DiagBIG_CAIXA_01_20261101_080000.txt"); // PC com "_" no nome
            names.Add("DiagBIG_anotacoes.txt");
            names.Add("outro.txt");

            var del = ReportBuilder.ReportsToDelete(names, 10);
            Assert.Equal(new[] { "DiagBIG_CAIXA01_20261102_093000.txt", "DiagBIG_CAIXA01_20261101_093000.txt" }, del);
            Assert.Empty(ReportBuilder.ReportsToDelete(names.Take(10), 10));
        }
    }
}
