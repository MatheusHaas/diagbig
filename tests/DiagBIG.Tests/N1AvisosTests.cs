using System;
using System.Linq;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    public class N1AvisosTests
    {
        static readonly DateTime Now = new DateTime(2026, 11, 17, 10, 0, 0);

        static ReportData Data(params ReportBlock[] blocks)
        {
            var d = new ReportData { CollectedAt = Now };
            var s = new ReportSection { Title = "x" };
            s.Blocks.AddRange(blocks);
            d.Sections.Add(s);
            return d;
        }

        static ReportBlock Block(string tag, params Occurrence[] occ)
        {
            var b = new ReportBlock { Tag = tag, IsEvents = true };
            b.Occurrences.AddRange(occ);
            return b;
        }

        static Occurrence BadBlock(DateTime t) => new Occurrence
        {
            Time = t, Source = "disk", EventId = "7", GroupKey = "disk 7", SummaryKey = "disk 7",
            Message = @"O dispositivo \Device\Harddisk0\DR0 tem um bloco inválido."
        };

        static Occurrence Power(DateTime t) => new Occurrence
        {
            Time = t, Source = "Microsoft-Windows-Kernel-Power", EventId = "41", GroupKey = "Kernel-Power 41", SummaryKey = "Kernel-Power 41",
            Message = "Interpretação: provável QUEDA/CORTE DE ENERGIA"
        };

        static ReportBlock DbWithWarnings(params string[] w)
        {
            var b = new ReportBlock { Tag = BlockTag.Db, IsEvents = false };
            b.Warnings.AddRange(w);
            b.Occurrences.Add(new Occurrence { Time = Now, Message = "[ERROR] InnoDB: Table 'loja/movimento' is marked as crashed", SummaryKey = "marked as crashed" });
            return b;
        }

        [Fact]
        public void BadBlockRecente_TrocaDisco_ComIdDoDisco()
        {
            var n1 = new System.Collections.Generic.List<string>();
            ActionPlan.Build(Data(Block(BlockTag.DiskOld, BadBlock(Now.AddDays(-40)))), n1);
            Assert.Contains("TROCAR O SERVIDOR/DISCO", n1[0]);
            Assert.Contains(@"\Device\Harddisk0\DR0", n1[1]);
        }

        [Fact]
        public void BadBlockComMaisDeTresMeses_NaoViraTrocaDeDisco()
        {
            var n1 = new System.Collections.Generic.List<string>();
            var d = Data(Block(BlockTag.DiskOld, BadBlock(Now.AddDays(-200))));
            ActionPlan.Build(d, n1);
            Assert.DoesNotContain(n1, x => x.Contains("TROCAR O SERVIDOR/DISCO"));
            Assert.Contains(Hypotheses.Analyze(d), x => x.Contains("NÃO conta para trocar o disco"));
        }

        [Fact]
        public void Item4_ComQuedaEVariaveis_AvisaDevemFicarProtegidas()
        {
            var n1 = new System.Collections.Generic.List<string>();
            ActionPlan.Build(Data(Block(BlockTag.Power, Power(Now.AddDays(-1))),
                                  DbWithWarnings("innodb_doublewrite=OFF no my.ini: x", "innodb_flush_log_at_trx_commit=2 no my.ini: y")), n1);
            string all = string.Join("\n", n1.ToArray());
            Assert.Contains("DEVEM ficar protegidas", all);
            Assert.Contains("innodb_doublewrite=ON", all);
            Assert.Contains("innodb_flush_log_at_trx_commit=1 (hoje 2)", all);
            Assert.Contains("REINICIAR o serviço do MariaDB", all);
        }

        [Fact]
        public void Item4_SemRelogioDetectado_MandaConferirBateria()
        {
            var n1 = new System.Collections.Generic.List<string>();
            ActionPlan.Build(Data(Block(BlockTag.Power, Power(Now.AddDays(-1)))), n1);
            Assert.Contains(n1, x => x.Contains("bateria da placa-mãe"));
        }

        [Fact]
        public void RelogioAlterado_EntraNoN1ENoPasso()
        {
            var clock = Block(BlockTag.Clock, new Occurrence { Time = Now.AddDays(-2), Source = "Microsoft-Windows-Kernel-General", EventId = "1", Message = "hora alterada" });
            var n1 = new System.Collections.Generic.List<string>();
            var plan = ActionPlan.Build(Data(clock), n1);
            Assert.Contains("data/hora do Windows", n1[0]);
            Assert.Contains(plan, x => x.Contains("Data/hora:") && x.Contains("CR2032"));
        }

        [Fact]
        public void Item2_TabelasComRelogioAlterado_AvisaData()
        {
            var clock = Block(BlockTag.Clock, new Occurrence { Time = Now.AddDays(-2), Source = "Microsoft-Windows-Kernel-General", EventId = "1", Message = "hora alterada" });
            var n1 = new System.Collections.Generic.List<string>();
            var d = Data(clock, DbWithWarnings());
            d.Diagnosis.AddRange(Diagnosis.Analyze(d));
            ActionPlan.Build(d, n1);
            Assert.Contains("tratar as tabelas", n1[0]);
            Assert.Contains(n1, x => x.StartsWith("ATENÇÃO DATA/HORA"));
        }
    }
}
