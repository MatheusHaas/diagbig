using System.Collections.Generic;
using System.Linq;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    public class MaintenancePortTests
    {
        static string Ini(string port) { return "[mysqld]\r\ndatadir=C:/x\r\n" + (port == null ? "" : "port=" + port + "\r\n") + "[client]\r\nport=" + (port ?? "3306") + "\r\n"; }
        static readonly int[] None = new int[0];

        [Fact]
        public void ClienteNa3306_ManutencaoNa3307()
        {
            PortPlan p = MaintenancePort.Plan(Ini("3306"), null, None);
            Assert.Equal(3306, p.ClientPort);
            Assert.Equal(3307, p.MaintPort);
            Assert.False(p.Reused);
            Assert.Empty(p.Notes);
        }

        [Fact]
        public void SemPortNoMyIni_ClienteNaPadrao3306()
        {
            PortPlan p = MaintenancePort.Plan(Ini(null), null, None);
            Assert.Equal(3306, p.ClientPort);
            Assert.Equal(3307, p.MaintPort);
        }

        [Fact]
        public void ClienteJaNa3307_ManutencaoSobePara3308()
        {
            PortPlan p = MaintenancePort.Plan(Ini("3307"), null, None);
            Assert.Equal(3307, p.ClientPort);
            Assert.Equal(3308, p.MaintPort);
            Assert.Contains(p.Notes, n => n.Contains("3307") && n.Contains("cliente"));
        }

        [Fact]
        public void Porta3307OcupadaPorOutroPrograma_Sobe()
        {
            PortPlan p = MaintenancePort.Plan(Ini("3306"), null, new[] { 3307 });
            Assert.Equal(3308, p.MaintPort);
            Assert.Contains(p.Notes, n => n.Contains("3307") && n.Contains("em uso"));
        }

        [Fact]
        public void ClienteNa3307E3308Ocupada_Vai3309()
        {
            PortPlan p = MaintenancePort.Plan(Ini("3307"), null, new[] { 3308 });
            Assert.Equal(3309, p.MaintPort);
            Assert.Equal(2, p.Notes.Count);
        }

        [Fact]
        public void TodasOcupadas_SemPorta()
        {
            PortPlan p = MaintenancePort.Plan(Ini("3306"), null, Enumerable.Range(3307, 3399 - 3307 + 1).ToArray());
            Assert.Equal(0, p.MaintPort);
        }

        [Fact]
        public void PortaInvalida_CaiNaPadrao()
        {
            Assert.Equal(3306, MaintenancePort.PortOf("[mysqld]\r\nport=abc\r\n"));
            Assert.Equal(3306, MaintenancePort.PortOf("[mysqld]\r\nport=0\r\n"));
            Assert.Equal(3306, MaintenancePort.PortOf("[mysqld]\r\nport=70000\r\n"));
            Assert.Equal(3310, MaintenancePort.PortOf("[mysqld]\r\nport=3310\r\n"));
        }

        [Fact]
        public void PortaDoClientNaoContaComoPortaDoServidor()
        {
            Assert.Equal(3306, MaintenancePort.PortOf("[client]\r\nport=3399\r\n[mysqld]\r\ndatadir=C:/x\r\n"));
        }

        // ---- depois que o DiagBIG já mexeu no my.ini (force 1, depois force 2, depois tirar o force) ----

        const string Marcado = "[mysqld]\r\n# DiagBIG 22/11/2026 10:00: era port=3306\r\nport=3307\r\ninnodb_force_recovery=1\r\n";

        [Fact]
        public void Reaplicar_MantemAPortaDeManutencaoJaColocada()
        {
            PortPlan p = MaintenancePort.Plan(Marcado, Ini("3306"), None);
            Assert.True(p.Reused);
            Assert.Equal(3306, p.ClientPort);
            Assert.Equal(3307, p.MaintPort);
        }

        [Fact]
        public void Reaplicar_PortaDeManutencaoEmUsoPeloProprioBanco_NaoTrocaDePorta()
        {
            // o serviço ainda está escutando na porta de manutenção quando se confirma o force 2: não pode ser tratada como "ocupada".
            PortPlan p = MaintenancePort.Plan(Marcado, Ini("3306"), new[] { 3307 });
            Assert.True(p.Reused);
            Assert.Equal(3307, p.MaintPort);
        }

        [Fact]
        public void Reaplicar_ClienteEra3307_ManutencaoContinuaNa3308()
        {
            string marcado = "[mysqld]\r\n# DiagBIG 22/11/2026 10:00: era port=3307\r\nport=3308\r\ninnodb_force_recovery=1\r\n";
            PortPlan p = MaintenancePort.Plan(marcado, Ini("3307"), new[] { 3308 });
            Assert.True(p.Reused);
            Assert.Equal(3307, p.ClientPort);
            Assert.Equal(3308, p.MaintPort);
        }

        [Fact]
        public void MarcaMasPortaIgualAoOriginal_EscolheDeNovo()
        {
            string marcado = "[mysqld]\r\n# DiagBIG 22/11/2026 10:00: era innodb_doublewrite=OFF\r\nport=3306\r\n";
            PortPlan p = MaintenancePort.Plan(marcado, Ini("3306"), None);
            Assert.False(p.Reused);
            Assert.Equal(3307, p.MaintPort);
        }

        [Fact]
        public void MarcaSemBackupDoOriginal_TrataAPortaAtualComoDoCliente()
        {
            // sem my.ini.diagbig-original não dá para saber a porta de antes: o mais seguro é não reaproveitar nada.
            PortPlan p = MaintenancePort.Plan(Marcado, null, None);
            Assert.False(p.Reused);
            Assert.Equal(3307, p.ClientPort);
            Assert.Equal(3308, p.MaintPort);
        }

        [Fact]
        public void ParserLePort_EMergeDeixaOUltimoPrevalecer()
        {
            MyIniOptions a = MyIniParser.Parse("[mysqld]\r\nport=3306\r\n");
            MyIniOptions b = MyIniParser.Parse("[mysqld]\r\nport=3310\r\n");
            Assert.Equal("3306", a.Port);
            Assert.Equal("3310", MyIniParser.Merge(new[] { a, b }).Port);
            Assert.Null(MyIniParser.Parse("[mysqld]\r\ndatadir=C:/x\r\n").Port);
        }
    }
}
