using System;
using System.Collections.Generic;
using System.Linq;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    /// <summary>Os 7 encaminhamentos do N1 e o que a tela inicial mostra em cada um.</summary>
    public class TelaEssencialTests
    {
        static readonly DateTime Now = new DateTime(2026, 11, 19, 10, 0, 0);

        static ReportBlock Block(string tag, params Occurrence[] occ)
        {
            var b = new ReportBlock { Tag = tag, IsEvents = tag != BlockTag.Db && tag != BlockTag.Repair };
            b.Occurrences.AddRange(occ);
            return b;
        }

        static Occurrence BadBlock() => new Occurrence { Time = Now, Source = "disk", EventId = "7", GroupKey = "disk 7", SummaryKey = "disk 7", Message = @"bloco inválido \Device\Harddisk0\DR0" };
        static Occurrence Power() => new Occurrence { Time = Now, Source = "Microsoft-Windows-Kernel-Power", EventId = "41", GroupKey = "Kernel-Power 41", SummaryKey = "Kernel-Power 41", Message = "queda de energia" };
        static Occurrence Clock() => new Occurrence { Time = Now, Source = "Microsoft-Windows-Kernel-General", EventId = "1", Message = "hora alterada" };
        static Occurrence Table() => new Occurrence { Time = Now, Message = "[ERROR] InnoDB: Table 'loja/movimento' is marked as crashed", SummaryKey = "marked as crashed" };
        static Occurrence Sys() => new Occurrence { Time = Now, Message = "InnoDB: Assertion failure in ibdata1", SummaryKey = "Assertion failure" };
        static Occurrence Dism() => new Occurrence { Time = Now, Source = "DISM", Message = "DISM falhou", SummaryKey = RepairLogAnalyzer.DismKey + " 0x800f081f" };

        static ReportData Run(Action<ReportData> setup, params ReportBlock[] blocks)
        {
            var d = new ReportData { CollectedAt = Now, ComputerName = "CAIXA01", WindowsVersion = "Windows 10 Pro" };
            var s = new ReportSection { Title = "x" };
            s.Blocks.AddRange(blocks);
            d.Sections.Add(s);
            d.Drives.Add("C: 50,0 GB livres de 200,0 GB (25%)");
            if (setup != null) setup(d);
            d.Diagnosis.AddRange(Diagnosis.Analyze(d));
            ActionPlan.Build(d, d.N1);
            return d;
        }

        [Fact]
        public void Caso1_Nada_SemDestino()
        {
            var d = Run(null);
            Assert.Equal(N1Case.Nada, d.N1Kind);
            Assert.Null(d.N1Dest);
            string t = ReportBuilder.BuildEssential(d);
            Assert.Contains("NINGUÉM POR ESTE RELATÓRIO", t);
            Assert.Contains("CAIXA01", t);
        }

        [Fact]
        public void Caso2_SoTabelas_N2Sistema_ComForceE3307_EAviso20h()
        {
            var d = Run(null, Block(BlockTag.Db, Table()));
            Assert.Equal(N1Case.Tabelas, d.N1Kind);
            Assert.Equal("N2 SISTEMA", d.N1Dest);
            string t = ReportBuilder.BuildEssential(d);
            Assert.Contains("innodb_force_recovery=1 ou 2", t);
            Assert.Contains("3307", t);
            Assert.Contains("Após as 20:00", t);
            Assert.Contains("movimento", t);
            Assert.Contains("innodb_doublewrite=ON", t);
            Assert.Contains("ALTER TABLE", t);
            Assert.Contains("TIRAR o innodb_force_recovery", t);
            Assert.Contains("acertasequenciador.exe", t);
        }

        [Fact]
        public void Caso2_PlanoCompleto_UsaRoteiroDoN2_SemDump()
        {
            var d = Run(null, Block(BlockTag.Db, Table()));
            string plan = string.Join("\n", ActionPlan.Build(d));
            Assert.Contains("Caso do N2", plan);
            Assert.Contains("bigupdate.exe", plan);
            Assert.DoesNotContain("TENTAR SALVAR OS DADOS", plan);
            Assert.DoesNotContain("mysqldump", plan);
        }

        [Fact]
        public void ServicoDesativado_AvisaNaTelaEEmQualquerCaso_SemMudarODestino()
        {
            var d = Run(x => x.DisabledDbServices.Add("MariaDB"), Block(BlockTag.Db, Table()));
            Assert.Equal(N1Case.Tabelas, d.N1Kind);
            string t = System.Text.RegularExpressions.Regex.Replace(ReportBuilder.BuildEssential(d), @"\s+", " ");
            Assert.Contains("SERVIÇO DO BANCO DESATIVADO (MariaDB)", t);
            Assert.Contains("troca de servidor recente", t);
            Assert.Contains("NÃO ativar", t);
            var n = Run(x => x.DisabledDbServices.Add("MySQL"));
            Assert.Equal(N1Case.Nada, n.N1Kind);
            Assert.Contains("SERVIÇO DO BANCO DESATIVADO (MySQL)", System.Text.RegularExpressions.Regex.Replace(ReportBuilder.BuildEssential(n), @"\s+", " "));
            Assert.DoesNotContain("DESATIVADO", ReportBuilder.BuildEssential(Run(null)));
        }

        [Fact]
        public void Caso3_DiscoRuim_EquipeTecnica_ComReinicios()
        {
            var d = Run(null, Block(BlockTag.Disk, BadBlock()), Block(BlockTag.Power, Power()));
            Assert.Equal(N1Case.TrocarDisco, d.N1Kind);
            Assert.Equal("EQUIPE TÉCNICA", d.N1Dest);
            Assert.DoesNotContain("Após as 20:00", ReportBuilder.BuildEssential(d));
        }

        [Fact]
        public void Caso4_BancoAlemDeTabelas_EquipeTecnica_Reinstalar()
        {
            var d = Run(null, Block(BlockTag.Db, Sys()));
            Assert.Equal(N1Case.Reinstalar, d.N1Kind);
            Assert.Equal("EQUIPE TÉCNICA", d.N1Dest);
        }

        [Fact]
        public void Caso5_ReparoWindows_N2Sistema_ComAviso20h()
        {
            var d = Run(null, Block(BlockTag.Repair, new Occurrence { Time = Now, Source = "DISM", Message = "DISM falhou", SummaryKey = RepairLogAnalyzer.DismKey + " 0x80070057" }));
            Assert.Equal(N1Case.ReparoWindows, d.N1Kind);
            Assert.Equal("N2 SISTEMA", d.N1Dest);
            string t = ReportBuilder.BuildEssential(d);
            Assert.Contains("Após as 20:00", t);
            Assert.Contains("sfc /scannow", t);
        }

        [Fact]
        public void Caso6_SoEnergia_TecnicoDaFarmacia()
        {
            var d = Run(null, Block(BlockTag.Power, Power()));
            Assert.Equal(N1Case.Energia, d.N1Kind);
            Assert.Equal("TÉCNICO DA FARMÁCIA", d.N1Dest);
            Assert.Contains(d.N1, x => x.Contains("no-break"));
            Assert.DoesNotContain(ActionPlan.Build(d).ToList(), x => x.Contains("DISM /Online"));
        }

        [Fact]
        public void Caso7_PoucoEspaco_N1Resolve()
        {
            var d = Run(x => { x.Drives.Clear(); x.Drives.Add("C: 5,0 GB livres de 200,0 GB (2%)  <<< POUCO ESPAÇO"); });
            Assert.Equal(N1Case.N1Sozinho, d.N1Kind);
            Assert.Contains("N1 (RESOLVE SOZINHO)", d.N1Dest);
            Assert.Contains("liberar espaço em C:", d.N1[0]);
        }

        [Fact]
        public void Caso7_SoRelogio_N1Resolve_EmantemTextoDoRelogio()
        {
            var d = Run(null, Block(BlockTag.Clock, Clock()));
            Assert.Equal(N1Case.N1Sozinho, d.N1Kind);
            Assert.Contains("data/hora do Windows", d.N1[0]);
            Assert.Contains("CR2032", d.N1[0]);
        }

        [Fact]
        public void TelaEssencial_NaoTrazSecoesDetalhadas_ETxtContinuaCompleto()
        {
            var d = Run(x => x.IpAddresses.Add("192.168.0.10 (Ethernet)"), Block(BlockTag.Disk, BadBlock()));
            string curta = ReportBuilder.BuildEssential(d), cheia = ReportBuilder.BuildFull(d);
            Assert.Contains("192.168.0.10", curta);
            Assert.Contains("192.168.0.10", cheia);
            Assert.DoesNotContain("COBERTURA DOS LOGS", curta);
            Assert.DoesNotContain("CAUSAS PROVÁVEIS", curta);
            Assert.Contains("CAUSAS PROVÁVEIS", cheia);
            Assert.True(curta.Length < cheia.Length);
        }

        [Fact]
        public void Aviso20h_PodeSerDesligadoPelaConfig_SemQuebrarOsCasos()
        {
            Assert.Contains("20:00", Config.N2AfterHoursNote);
        }
    }
}
