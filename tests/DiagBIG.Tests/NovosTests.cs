using System;
using System.Collections.Generic;
using System.Linq;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    public class NovosTests
    {
        static readonly DateTime T = new DateTime(2026, 11, 18, 17, 18, 0);

        static Occurrence Ev(string src, int id, string msg, string key = null) => new Occurrence
        {
            Time = T, Source = src, EventId = id.ToString(), Message = msg, SummaryKey = key ?? (src + " " + id),
        };

        static ReportBlock Block(string tag, bool events, params Occurrence[] occ)
        {
            var b = new ReportBlock { Tag = tag, IsEvents = events };
            b.Occurrences.AddRange(occ);
            return b;
        }

        static ReportData Data(params ReportBlock[] blocks)
        {
            var d = new ReportData { CollectedAt = new DateTime(2026, 11, 18, 18, 33, 0) };
            var s = new ReportSection { Title = "x" };
            s.Blocks.AddRange(blocks);
            d.Sections.Add(s);
            return d;
        }

        static ReportData Caso(bool bancoNovo)
        {
            var d = Data(Block(BlockTag.Disk, true, Ev("disk", 153, "A operação de ES foi repetida.")),
                         Block(BlockTag.Repair, false, new Occurrence { Time = T, Source = "DISM", Message = "DISM falhou", SummaryKey = RepairLogAnalyzer.DismKey + " 0x800f081f" }),
                         Block(BlockTag.Db, false, new Occurrence { Time = T, Source = ".err", Message = "InnoDB: Database page corruption", SummaryKey = "corruption" }));
            if (bancoNovo) d.DbDataCreated = new DateTime(2026, 11, 18, 17, 10, 0);
            d.Diagnosis.AddRange(Diagnosis.Analyze(d));
            return d;
        }

        [Fact]
        public void DismOrigemNaoEncontrada_NaoEhProvaDeDisco()
        {
            var plan = ActionPlan.Build(Caso(false));
            Assert.DoesNotContain("DISCO COM DEFEITO GRAVE", plan[0]);
        }

        [Fact]
        public void BancoNovoJaCorrompendoComEventoDeDisco_MandaTrocarDisco()
        {
            var d = Caso(true);
            var plan = ActionPlan.Build(d);
            Assert.StartsWith("VEREDITO: DISCO COM DEFEITO GRAVE", plan[0]);
            Assert.Contains("banco instalado hoje", plan[0]);
            Assert.Contains(Hypotheses.Analyze(d), x => x.StartsWith("BANCO NOVO JÁ CORROMPENDO"));
        }

        [Fact]
        public void BancoAntigo_NaoAcionaRegraDeInstalacaoRecente()
        {
            var d = Caso(false);
            d.DbDataCreated = new DateTime(2026, 6, 1);
            Assert.Null(RecentInstall.DbAgeDays(d));
            Assert.Null(RecentInstall.Cause(d, true));
        }

        [Fact]
        public void VolumePequenoOuForaDoBanco_NaoContaComoPoucoEspaco()
        {
            var d = Data();
            d.Drives.Add("C: 784,3 GB livres de 953,1 GB (82%)");
            d.Drives.Add("E: 19,6 MB livres de 50,0 MB (39%)  <<< POUCO ESPAÇO");
            d.Drives.Add("F: 1 GB livres de 400 GB (1%)  <<< POUCO ESPAÇO");
            d.DbDrives.Add("C:");
            Assert.Empty(Hypotheses.LowSpaceDrives(d));
            d.DbDrives.Add("F:");
            Assert.Equal(new[] { "F:" }, Hypotheses.LowSpaceDrives(d).ToArray());
        }

        [Fact]
        public void EventoDisco0_MostraModeloEUnidades()
        {
            var map = new Dictionary<int, string> { { 0, "REAPER C, 953,9 GB, IDE; unidades: C:" } };
            string t = DiskTarget.Describe("A operação de ES no endereço de bloco lógico 0xde94440 para o Disco 0 (nome PDO: \\Device\\00000041) foi repetida.", null, null, map);
            Assert.Contains("DISCO FÍSICO 0 = REAPER C", t);
            var cited = DiskTarget.CitedDisks(new[] { new Occurrence { Message = "para o Disco 0 (nome PDO" } }, map);
            Assert.Contains("DISCO COM ERRO: Disco 0 = REAPER C", cited[0]);
        }
    }
}
