using System;
using System.IO;
using System.Linq;
using System.Text;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    public class ErrDateTests
    {
        static DateTime Utc(int y, int mo, int d, int h, int mi, int s) =>
            new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.Zero).ToLocalTime().DateTime;

        [Fact]
        public void Mysql55() => Assert.Equal(new DateTime(2014, 10, 22, 10, 0, 0), ErrLogAnalyzer.TryParseLineDate("141022 10:00:00 [ERROR] x"));

        [Fact]
        public void Mysql55SingleDigitHour() => Assert.Equal(new DateTime(2014, 10, 20, 8, 1, 12), ErrLogAnalyzer.TryParseLineDate("141020  8:01:12 [Note] x"));

        [Fact]
        public void MariaDb() => Assert.Equal(new DateTime(2024, 1, 1, 10, 0, 0), ErrLogAnalyzer.TryParseLineDate("2024-01-01 10:00:00 0 [ERROR] x"));

        [Fact]
        public void Mysql8UtcIsConvertedToLocal()
        {
            DateTime? d = ErrLogAnalyzer.TryParseLineDate("2024-01-01T10:00:00.123456Z 0 [ERROR] [MY-1] x");
            Assert.Equal(Utc(2024, 1, 1, 10, 0, 0).AddTicks(1234560), d);
        }

        [Fact]
        public void Mysql8WithOffset() =>
            Assert.Equal(Utc(2024, 1, 1, 13, 0, 0), ErrLogAnalyzer.TryParseLineDate("2024-01-01T10:00:00.000000-03:00 0 [Note] x"));

        [Theory]
        [InlineData("InnoDB: Starting crash recovery.")]
        [InlineData("000000013F6E2C3B    mysqld.exe!my_mb_wc_utf8()")]
        [InlineData("141399 10:00:00 data inválida")]
        [InlineData("")]
        public void NotADate(string line) => Assert.Null(ErrLogAnalyzer.TryParseLineDate(line));
    }

    public class ErrAnalyzerTests
    {
        /// <summary>Caso real (SERVER-CENTRAL): InnoDB despeja a página corrompida colada nas linhas de log.</summary>
        [Fact]
        public void BinaryGarbage_IsCleanedAndBoilerplateIgnored()
        {
            string err =
                "2026-11-13 19:18:38 0 [ERROR] InnoDB: Database page corruption on disk or a failed file read of tablespace gerente/comunicacao_fila_filial_028 page [page id: space=1972, page number=4]\r\n" +
                "2026-11-13 19:18:38 0 [Note] InnoDB: It is also possible that your operating system has corrupted its own file cache and rebooting your computer removes the error.\r\n" +
                "(a) len 12; hex 2d00d45de71faac45bf0dcf0b5f80aa0bd45a7de721e; asc -   E\r\n" +
                "estoque_minimo\0\0produto_id=5 and filial_id=24 \0$ ! Z N 2026-11-13 19:18:38 0 [ERROR] InnoDB: Failed to read file '.\\gerente\\comunicacao_fila_filial_028.ibd' at offset 151: Page read from tablespace is corrupted.\r\n" +
                "2026-11-13 19:18:39 0 [ERROR] InnoDB: Failed to read file '.\\gerente\\comunicacao_fila_filial_028.ibd' at offset 151: Page read from tablespace is corrupted.\r\n";
            var a = ErrLogAnalyzer.Analyze(err, new DateTime(2026, 11, 1));
            Assert.DoesNotContain(a.Occurrences, o => o.Message.Contains("It is also possible"));
            Assert.DoesNotContain(a.Occurrences, o => o.Message.Contains("hex "));
            // A linha colada no lixo vira a mesma ocorrência da linha limpa (mesmo grupo).
            var fail = a.Occurrences.Where(o => o.Message.Contains("Failed to read file")).ToList();
            Assert.Equal(2, fail.Count);
            Assert.Single(fail.Select(o => o.GroupKey).Distinct());
            Assert.StartsWith("2026-11-13 19:18:38", fail[0].Message);
            Assert.Equal(new DateTime(2026, 11, 13, 19, 18, 38), fail[0].Time);
        }

        [Fact]
        public void Mysql51Sample_CrashWithContext_UndatedInheritsDate()
        {
            var a = ErrLogAnalyzer.Analyze(Samples.Text("mysql51_crash.err"), new DateTime(2014, 10, 21));
            Assert.Equal(new DateTime(2014, 10, 20, 8, 1, 12), a.FirstDate);

            var crashed = a.Occurrences.Where(o => o.SummaryKey == "marked as crashed").ToList();
            Assert.Equal(2, crashed.Count);
            Assert.Single(a.Occurrences, o => o.SummaryKey == "Got error 1");
            Assert.Single(a.Occurrences, o => o.SummaryKey == "Incorrect key file");

            var crash = Assert.Single(a.Occurrences, o => o.SummaryKey == "got exception");
            // ~15 linhas seguintes vão junto (Config.CrashContextLines).
            Assert.Contains("Thread pointer: 0x0", crash.Message);
            Assert.Equal(1 + Config.CrashContextLines, crash.Message.Split('\n').Length);
            Assert.Equal(new DateTime(2014, 10, 22, 10, 3, 15), crash.Time);

            // Texto explicativo do crash vai no contexto, não como ocorrência separada.
            Assert.DoesNotContain(a.Occurrences, o => o.Message.StartsWith("or one of the libraries"));

            // Linha sem data herda a anterior (141022 10:03:21).
            var rec = Assert.Single(a.Occurrences, o => o.SummaryKey == "crash recovery");
            Assert.Equal(new DateTime(2014, 10, 22, 10, 3, 21), rec.Time);
        }

        [Fact]
        public void Mysql4Sample_NoErrorTags()
        {
            var a = ErrLogAnalyzer.Analyze(Samples.Text("mysql4_tabela_crashed.err"), new DateTime(2005, 4, 1));
            Assert.Equal(new DateTime(2005, 3, 10, 14, 1, 23), a.FirstDate);
            Assert.Equal(new[] { "marked as crashed", "Got error 1", "Incorrect key file", "crash recovery" },
                         a.Occurrences.Select(o => o.SummaryKey).ToArray());
            Assert.Equal(new DateTime(2005, 4, 12, 9, 15, 2), a.Occurrences[0].Time);
            // "InnoDB: Starting crash recovery." sem data herda 050418 8:00:12.
            Assert.Equal(new DateTime(2005, 4, 18, 8, 0, 12), a.Occurrences[3].Time);
            Assert.Empty(ErrLogAnalyzer.Analyze(Samples.Text("mysql4_tabela_crashed.err"), new DateTime(2005, 5, 1)).Occurrences);
        }

        [Fact]
        public void PeriodIsRespected()
        {
            var a = ErrLogAnalyzer.Analyze(Samples.Text("mysql51_crash.err"), new DateTime(2014, 10, 22));
            Assert.DoesNotContain(a.Occurrences, o => o.SummaryKey == "marked as crashed");
            Assert.All(a.Occurrences, o => Assert.True(o.Time >= new DateTime(2014, 10, 22)));
            Assert.Empty(ErrLogAnalyzer.Analyze(Samples.Text("mysql51_crash.err"), new DateTime(2015, 1, 1)).Occurrences);
        }

        [Fact]
        public void MariaDbSample_CorruptionCrashedDiskFull()
        {
            var a = ErrLogAnalyzer.Analyze(Samples.Text("mariadb104_corrupcao.err"), new DateTime(2026, 10, 1));
            Assert.Contains(a.Occurrences, o => o.SummaryKey == "Database page corruption");
            Assert.Contains(a.Occurrences, o => o.SummaryKey == "innodb_force_recovery");
            Assert.Contains(a.Occurrences, o => o.SummaryKey == "Disk full" || o.SummaryKey == "No space left");
            Assert.Contains(a.Occurrences, o => o.SummaryKey == "Aborting");
            var assertion = Assert.Single(a.Occurrences, o => o.SummaryKey == "Assertion failure");
            Assert.Contains("Failing assertion", assertion.Message);
            // MariaDB escreve o crash no formato antigo (261108  2:13:51).
            var exc = Assert.Single(a.Occurrences, o => o.SummaryKey == "got exception");
            Assert.Equal(new DateTime(2026, 11, 8, 2, 13, 51), exc.Time);
            // Contexto para na linha datada bem depois do crash (reinício e dias seguintes).
            Assert.Contains("row_upd_clust_rec", exc.Message);
            Assert.DoesNotContain("Aborted connection", exc.Message);
            // "Aborted connection" é aviso comum, não deve entrar.
            Assert.DoesNotContain(a.Occurrences, o => o.GroupKey.Contains("Aborted connection"));

            var groups = Grouping.Group(a.Occurrences);
            var g = groups.First(x => x.Key.Contains("marked as crashed"));
            Assert.Equal(2, g.Count);
            Assert.Equal(new DateTime(2026, 11, 3, 16, 40, 12), g.First);
            Assert.Equal(new DateTime(2026, 11, 3, 16, 41, 30), g.Last);
        }

        [Fact]
        public void Mysql8Sample_DiskFullAndAssertion()
        {
            var a = ErrLogAnalyzer.Analyze(Samples.Text("mysql8_disco_cheio.err"), new DateTime(2026, 10, 1));
            Assert.Contains(a.Occurrences, o => o.Message.Contains("No space left"));
            Assert.Contains(a.Occurrences, o => o.SummaryKey == "Operating system error");
            Assert.Contains(a.Occurrences, o => o.SummaryKey == "Assertion failure");
            Assert.Contains(a.Occurrences, o => o.SummaryKey == "got exception");
            // A linha de crash recovery datada não pode ser "engolida" pelo contexto do crash.
            Assert.Contains(a.Occurrences, o => o.SummaryKey == "crash recovery");
            Assert.DoesNotContain(a.Occurrences, o => o.Level == "WARNING" || o.Level == "SYSTEM");
        }

        [Fact]
        public void LinesBeforeFirstDateAreIgnored_ButAllIncludedWhenFileHasNoDates()
        {
            string text = "[ERROR] cortado no início\n2026-11-01 10:00:00 0 [ERROR] dentro\n";
            var a = ErrLogAnalyzer.Analyze(text, new DateTime(2026, 10, 1));
            Assert.Single(a.Occurrences);

            var b = ErrLogAnalyzer.Analyze("[ERROR] sem data 1\nnada\nInnoDB: corruption\n", new DateTime(2026, 10, 1));
            Assert.Equal(2, b.Occurrences.Count);
            Assert.False(b.HasDates);
        }

        [Fact]
        public void NormalizeRemovesDateThreadAndLongNumbers()
        {
            Assert.Equal("[ERROR] Write failed at offset #, page 12 0x#",
                ErrLogAnalyzer.Normalize("2026-11-07 11:20:04 15 [ERROR] Write failed at offset 2097152, page 12 0xDEADBEEF"));
            Assert.Equal(ErrLogAnalyzer.Normalize("141021 14:22:07 [ERROR] x"), ErrLogAnalyzer.Normalize("2024-01-01T10:00:00.1Z 3 [ERROR] x"));
        }

        [Fact]
        public void MostSpecificPatternWins() =>
            Assert.Equal("marked as crashed", ErrLogAnalyzer.MatchPattern("[ERROR] Table x is marked as crashed", Config.ErrPatterns));
    }

    public class ErrReaderTests
    {
        static ErrReaderTests() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }

        [Fact]
        public void ReadsOnlyTailAndDropsPartialFirstLine()
        {
            string path = Path.GetTempFileName();
            try
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 200; i++) sb.Append("2026-11-01 10:00:00 0 [Note] linha ").Append(i.ToString("000")).Append("\r\n");
                File.WriteAllText(path, sb.ToString());
                var r = ErrTailReader.ReadTail(path, 1000);
                Assert.True(r.Truncated);
                Assert.Equal(new FileInfo(path).Length, r.FileSize);
                Assert.StartsWith("2026-11-01", r.Text);
                Assert.EndsWith("linha 199\r\n", r.Text);

                var whole = ErrTailReader.ReadTail(path, 10_000_000);
                Assert.False(whole.Truncated);
                Assert.Contains("linha 000", whole.Text);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ReadsWhileAnotherProcessHasItOpenForWriting()
        {
            string path = Path.GetTempFileName();
            try
            {
                using (var w = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    byte[] b = Encoding.UTF8.GetBytes("2026-11-01 10:00:00 0 [ERROR] aberto\n");
                    w.Write(b, 0, b.Length);
                    w.Flush();
                    Assert.Contains("aberto", ErrTailReader.ReadTail(path, 1000).Text);
                }
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Utf8AndWindows1252()
        {
            byte[] utf8 = Encoding.UTF8.GetBytes("Não foi possível");
            string enc;
            Assert.Equal("Não foi possível", ErrTailReader.Decode(utf8, 0, utf8.Length, out enc));
            Assert.Equal("UTF-8", enc);

            byte[] cp1252 = Encoding.GetEncoding(1252).GetBytes("Não foi possível");
            Assert.Equal("Não foi possível", ErrTailReader.Decode(cp1252, 0, cp1252.Length, out enc));
            Assert.Equal("Windows-1252", enc);
        }
    }
}
