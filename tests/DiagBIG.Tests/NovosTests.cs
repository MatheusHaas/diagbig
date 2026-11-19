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

        [Fact]
        public void ResumoDeDesligamentos_ContaIncidentes_NaoEventos()
        {
            var b = new ReportBlock { Tag = BlockTag.Power, IsEvents = true };
            for (int i = 0; i < 3; i++)
            {
                DateTime t = new DateTime(2026, 11, 14, 4 + i, 0, 0);
                b.Occurrences.Add(new Occurrence { Time = t, Source = "Microsoft-Windows-Kernel-Power", EventId = "41", Message = "x", SummaryKey = "Kernel-Power 41" });
                b.Occurrences.Add(new Occurrence { Time = t.AddSeconds(30), Source = "EventLog", EventId = "6008", Message = "x", SummaryKey = "EventLog 6008" });
            }
            string txt = ReportBuilder.Summarize("DESLIGAMENTOS INESPERADOS", new[] { b }).Text;
            Assert.StartsWith("3 incidente(s)", txt);
            Assert.Contains("6 eventos somados", txt);
        }

        [Fact]
        public void DismParametroIncorreto_NaoEhProvaDeDisco()
        {
            var rep = Block(BlockTag.Repair, false, new Occurrence { Time = T, Source = "DISM", Message = "DISM falhou", SummaryKey = RepairLogAnalyzer.DismKey + " 0x80070057" });
            var d = Data(Block(BlockTag.Disk, true, Ev("disk", 153, "A operação de ES foi repetida.")), rep);
            Assert.DoesNotContain("DISCO COM DEFEITO GRAVE", ActionPlan.Build(d)[0]);
            Assert.Contains("não indica disco", RepairLogAnalyzer.Describe("0x80070057"));
            // a causa não pode dizer "sem evento de disco" quando há evento de disco
            var causa = Hypotheses.Analyze(d).First(x => x.StartsWith("DISM/SFC já falharam"));
            Assert.DoesNotContain("Sem evento de disco", causa);
            Assert.DoesNotContain("trocar o disco", causa);
        }

        [Fact]
        public void BadBlockSemDisco_ComDiscoUnico_CitaOModelo()
        {
            var bad = new List<Occurrence> { new Occurrence { Time = T, Message = "bloco inválido" } };
            Assert.Contains("o PC tem só um: VBOX HARDDISK", Hypotheses.BadSectorWhere(bad, "VBOX HARDDISK"));
            Assert.Contains("disco não identificado", Hypotheses.BadSectorWhere(bad, null));
        }

        [Fact]
        public void LigacaoParticaoUnidade_DaIndiceDoDiscoELetra()
        {
            int di; string letter;
            Assert.True(DiskTarget.ParsePartitionLink(
                "\\\\BIG-SOFT\\root\\cimv2:Win32_DiskPartition.DeviceID=\"Disk #1, Partition #2\"",
                "\\\\BIG-SOFT\\root\\cimv2:Win32_LogicalDisk.DeviceID=\"d:\"", out di, out letter));
            Assert.Equal(1, di);
            Assert.Equal("D:", letter);
            Assert.False(DiskTarget.ParsePartitionLink("lixo", "lixo", out di, out letter));
        }
    }
}
