using System.Linq;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    public class WrapLongTests
    {
        static string Flat(string s) { return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim(); }

        [Fact]
        public void LinhaCurta_NaoMuda()
        {
            string t = "  PC ............: big-soft-window\r\n  IP ............: 10.0.2.15\r\n";
            Assert.Equal(t, ReportBuilder.WrapLong(t));
        }

        [Fact]
        public void LinhaLonga_QuebraNaLarguraENaoPerdeTexto()
        {
            string l = "  INIC. RÁPIDA ...: ligada: \"Desligar\" não reinicia o Windows por completo (só \"Reiniciar\" reinicia); o \"ligado desde\" e a linha do tempo podem não mostrar cada desligamento";
            string r = ReportBuilder.WrapLong(l + "\r\n");
            string[] lines = r.Split(new[] { "\r\n" }, System.StringSplitOptions.RemoveEmptyEntries);
            Assert.True(lines.Length >= 2);
            Assert.All(lines, x => Assert.True(x.Length <= ReportBuilder.WrapWidth, x));
            Assert.Equal(Flat(l), Flat(r));
        }

        [Fact]
        public void ContinuacaoDeLinhaComRotulo_AlinhaDepoisDosDoisPontos()
        {
            string l = "  INIC. RÁPIDA ...: " + string.Join(" ", Enumerable.Repeat("palavra", 30));
            string[] lines = ReportBuilder.WrapLong(l).Split(new[] { "\r\n" }, System.StringSplitOptions.None);
            int hang = "  INIC. RÁPIDA ...: ".Length;
            Assert.StartsWith(new string(' ', hang) + "palavra", lines[1]);
        }

        [Fact]
        public void ItemNumerado_ContinuaAlinhadoDepoisDoNumero()
        {
            string l = "  3. " + string.Join(" ", Enumerable.Repeat("trocar o disco", 12));
            string[] lines = ReportBuilder.WrapLong(l).Split(new[] { "\r\n" }, System.StringSplitOptions.None);
            Assert.Matches(@"^ {5}\S", lines[1]);          // 5 espaços: o recuo de "  3. ", seguido de texto
        }

        [Fact]
        public void LinhaSemEspaco_CaminhoLongo_NaoCorta()
        {
            string l = "C:\\" + new string('x', 150);
            Assert.Equal(l, ReportBuilder.WrapLong(l));
        }

        [Fact]
        public void PreservaEspacosDeDentroDaLinhaQueNaoQuebra()
        {
            string l = "  C:  36,7 GB livres de 78,6 GB   (46%)";
            Assert.Equal(l, ReportBuilder.WrapLong(l));
        }

        [Fact]
        public void TextoVazioENulo()
        {
            Assert.Equal("", ReportBuilder.WrapLong(""));
            Assert.Null(ReportBuilder.WrapLong(null));
        }

        [Fact]
        public void RelatorioReal_NenhumaLinhaPassaDaLargura_ExcetoCaminhosSemEspaco()
        {
            string txt = string.Join("\r\n", Enumerable.Range(0, 5).Select(i =>
                "  [PROVÁVEL] ARQUIVOS DO BANCO: " + string.Join(" ", Enumerable.Repeat("texto longo de exemplo", 8)))) + "\r\n";
            string r = ReportBuilder.WrapLong(txt);
            Assert.All(r.Split(new[] { "\r\n" }, System.StringSplitOptions.None), x => Assert.True(x.Length <= ReportBuilder.WrapWidth, x));
            Assert.Equal(Flat(txt), Flat(r));
        }
    }
}
