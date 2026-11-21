using System;
using System.Collections.Generic;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    public class MyIniEditorTests
    {
        static readonly DateTime Now = new DateTime(2026, 11, 21, 10, 0, 0);

        static string Edit(string ini, IList<IniSet> sets, out List<string> ch) { return MyIniEditor.Apply(ini, sets, Now, out ch); }

        [Fact]
        public void ComForce_CorrigeDoublewriteEFlush_PortaEForce_SemMexerNoResto()
        {
            string ini = "[client]\r\nport=3306\r\n[mysqld]\r\ndatadir=C:/data\r\nport=3306\r\ninnodb_doublewrite=OFF\r\ninnodb_flush_log_at_trx_commit=2\r\n";
            List<string> ch;
            string r = Edit(ini, MyIniEditor.MaintenanceWithForce("1", "3307"), out ch);
            var o = MyIniParser.Parse(r);
            Assert.Equal("1", o.ForceRecovery);
            Assert.Equal("ON", o.DoubleWrite);
            Assert.Equal("1", o.FlushLogAtTrxCommit);
            Assert.Matches("(?s)\\[client\\].*\\r\\nport=3307", r);          // [client] acompanha a porta de manutenção
            Assert.Contains("port=3307", r);
            Assert.Contains("datadir=C:/data", r);
            Assert.Contains("# DiagBIG 21/11/2026 10:00: era innodb_doublewrite=OFF", r);
            Assert.Equal(5, ch.Count);
        }

        [Fact]
        public void SkipDoublewrite_ViraComentario_EDoublewriteFicaPadrao()
        {
            List<string> ch;
            string r = Edit("[mysqld]\nskip-innodb-doublewrite\n", MyIniEditor.MaintenanceWithForce("2", "3307"), out ch);
            Assert.Null(MyIniParser.Parse(r).DoubleWrite);
            Assert.Equal("2", MyIniParser.Parse(r).ForceRecovery);
            Assert.DoesNotContain("\r\n", r);                      // manteve quebra de linha \n
        }

        [Fact]
        public void SemSecaoMysqld_CriaSecao()
        {
            List<string> ch;
            string r = Edit("[client]\r\nport=3306\r\n", MyIniEditor.MaintenanceWithForce("1", "3307"), out ch);
            Assert.Contains("[mysqld]", r);
            Assert.Equal("1", MyIniParser.Parse(r).ForceRecovery);
        }

        [Fact]
        public void TirarForce_MantemPorta3307()
        {
            List<string> ch;
            string r = Edit("[mysqld]\r\nport=3307\r\ninnodb_force_recovery=2\r\n", MyIniEditor.RemoveForce("3307"), out ch);
            Assert.Null(MyIniParser.Parse(r).ForceRecovery);
            Assert.Contains("port=3307", r);
            Assert.Contains("removido", r);
        }

        [Fact]
        public void AplicarDuasVezes_NaoAcumulaComentarios()
        {
            List<string> ch;
            string r1 = Edit("[mysqld]\r\nport=3306\r\ninnodb_doublewrite=OFF\r\n", MyIniEditor.MaintenanceWithForce("1", "3307"), out ch);
            string r2 = Edit(r1, MyIniEditor.MaintenanceWithForce("2", "3307"), out ch);
            string r3 = Edit(r2, MyIniEditor.RemoveForce("3307"), out ch);
            foreach (string r in new[] { r2, r3 })
                Assert.True(r.Split(new[] { "# DiagBIG" }, StringSplitOptions.None).Length - 1 <= 2, r);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(r3, "(?m)^port="));
        }

        [Fact]
        public void JaCorreto_NaoMuda()
        {
            List<string> ch;
            string ini = "[mysqld]\r\nport=3307\r\ninnodb_force_recovery=1\r\ninnodb_doublewrite=ON\r\ninnodb_flush_log_at_trx_commit=1\r\n";
            string r = Edit(ini, MyIniEditor.MaintenanceWithForce("1", "3307"), out ch);
            Assert.Empty(ch);
            Assert.Equal(ini, r);
        }
    }
}

namespace DiagBIG.Tests
{
    public class MyIniEditorPadraoBigTests
    {
        [Fact]
        public void IniRealDoBig_ComForce_SoAcrescentaForce_ELegendaFicaIntacta()
        {
            string ini = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "amostras", "my_mariadb104_big_padrao.ini"));
            List<string> ch;
            string r = MyIniEditor.Apply(ini, MyIniEditor.MaintenanceWithForce("1", "3307"), new DateTime(2026, 11, 21), out ch);
            Assert.Equal("1", MyIniParser.Parse(r).ForceRecovery);
            Assert.Single(ch);                                         // doublewrite=ON, flush=1 e port=3307 já estavam certos
            var a = ini.Replace("\r\n", "\n").TrimEnd().Split((char)10); var b = r.Replace("\r\n", "\n").TrimEnd().Split((char)10);
            Assert.Equal(a.Length + 1, b.Length);                      // só a linha do force a mais
            Assert.Contains("# LEGENDA", r);
        }

        [Fact]
        public void IniComPorta3306NoClient_TambemTrocaClient()
        {
            List<string> ch;
            string r = MyIniEditor.Apply("[mysqld]\r\nport=3306\r\n[client]\r\nport=3306\r\n", MyIniEditor.MaintenanceWithForce("1", "3307"), DateTime.Now, out ch);
            Assert.Matches("(?m)^port=3307\\r?$", r);
            Assert.DoesNotMatch("(?m)^port=3306", r);
        }
    }
}
