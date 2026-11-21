using System;
using System.Text.RegularExpressions;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    public class WmiRepoTests
    {
        [Theory]
        [InlineData("O repositório WMI é consistente.", WmiRepoState.Consistente)]
        [InlineData("WMI repository is consistent", WmiRepoState.Consistente)]
        [InlineData("O repositório WMI é inconsistente.", WmiRepoState.Inconsistente)]
        [InlineData("WMI repository is INCONSISTENT", WmiRepoState.Inconsistente)]
        [InlineData("", WmiRepoState.NaoVerificado)]
        [InlineData("Acesso negado", WmiRepoState.NaoVerificado)]
        public void Parse_LeASaidaDoVerifyRepository(string saida, WmiRepoState esperado)
        {
            Assert.Equal(esperado, WmiRepo.Parse(saida));
        }

        [Fact]
        public void OldName_TemDataEHora_EvitaColidirComOldRepositoryManual()
        {
            string n = WmiRepo.OldName(new DateTime(2026, 11, 21, 16, 5, 0));
            Assert.Equal("OLD_Repository_20261121_1605", n);
            Assert.NotEqual("OLD_Repository", n);
        }

        static string Flat(string s) { return Regex.Replace(s, @"\s+", " "); }

        static ReportData Run(WmiRepoState s, Action<ReportData> setup = null)
        {
            var d = new ReportData { CollectedAt = new DateTime(2026, 11, 21), ComputerName = "CAIXA01", WindowsVersion = "Windows 10 Pro", WmiRepo = s };
            d.Drives.Add("C: 50,0 GB livres de 200,0 GB (25%)");
            if (setup != null) setup(d);
            d.Diagnosis.AddRange(Diagnosis.Analyze(d));
            d.ActionPlan.AddRange(ActionPlan.Build(d, d.N1));
            return d;
        }

        [Fact]
        public void Inconsistente_AvisaNaTelaDoN1_SemMudarODestino()
        {
            var d = Run(WmiRepoState.Inconsistente);
            Assert.Equal(N1Case.Nada, d.N1Kind);
            Assert.Null(d.N1Dest);
            string t = Flat(ReportBuilder.BuildEssential(d));
            Assert.Contains("ATENÇÃO WMI", t);
            Assert.Contains("Reparar WMI (repositório)", t);
            Assert.Contains("não precisa reiniciar", t);
        }

        [Fact]
        public void Consistente_OuNaoVerificado_NaoAvisa()
        {
            Assert.DoesNotContain("ATENÇÃO WMI", ReportBuilder.BuildEssential(Run(WmiRepoState.Consistente)));
            Assert.DoesNotContain("ATENÇÃO WMI", ReportBuilder.BuildEssential(Run(WmiRepoState.NaoVerificado)));
        }
    }
}
