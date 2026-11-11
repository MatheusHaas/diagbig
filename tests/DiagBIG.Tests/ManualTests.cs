using System;
using System.IO;
using Xunit;

namespace DiagBIG.Tests
{
    public class ManualTests
    {
        /// <summary>O manual (botão Ajuda) precisa explicar todos os estados e itens do RESUMO.</summary>
        [Theory]
        [InlineData("OK")]
        [InlineData("ATENÇÃO")]
        [InlineData("NÃO VERIFICADO")]
        [InlineData("DISCO")]
        [InlineData("SMART")]
        [InlineData("DESLIGAMENTOS INESPERADOS")]
        [InlineData("LIGA/DESLIGA")]
        [InlineData("BANCO")]
        [InlineData("EVENTOS DO BANCO NO WINDOWS")]
        [InlineData("RAID")]
        [InlineData("Windows Server")]
        [InlineData("DiagBIG_relatorios")]
        [InlineData("--sem-janela")]
        public void ManualMentions(string term)
        {
            string manual = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "docs", "MANUAL.txt"));
            Assert.Contains(term, manual);
        }
    }
}
