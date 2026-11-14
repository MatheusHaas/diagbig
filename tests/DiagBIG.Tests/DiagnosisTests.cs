using System;
using System.Linq;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    public class DiagnosisTests
    {
        static readonly DateTime T = new DateTime(2026, 11, 12, 7, 4, 0);

        static Occurrence Err(string msg) => new Occurrence { Time = T, Source = ".err", Message = msg, SummaryKey = msg };
        static Occurrence Ev(string src, int id, string msg) => new Occurrence { Time = T, Source = src, EventId = id.ToString(), Message = msg, SummaryKey = src + " " + id };

        static ReportData Data(params ReportBlock[] blocks)
        {
            var d = new ReportData();
            var s = new ReportSection { Title = "x" };
            s.Blocks.AddRange(blocks);
            d.Sections.Add(s);
            return d;
        }

        static ReportBlock Block(string tag, bool events, params Occurrence[] occ)
        {
            var b = new ReportBlock { Tag = tag, IsEvents = events };
            b.Occurrences.AddRange(occ);
            return b;
        }

        static DiagItem Item(ReportData d, string area) => Diagnosis.Analyze(d).First(i => i.Area.StartsWith(area));

        [Fact]
        public void OnlyTable_NoReinstall()
        {
            var d = Data(Block(BlockTag.Db, false, Err("[ERROR] mysqld: Table '.\\loja\\movimento' is marked as crashed and should be repaired")));
            Assert.Equal(DiagVerdict.Provavel, Item(d, "TABELA").Verdict);
            Assert.Contains("loja.movimento", Item(d, "TABELA").Evidence);
            Assert.Equal(DiagVerdict.SemIndicio, Item(d, "ARQUIVOS").Verdict);
            Assert.Contains("só tabela", Diagnosis.Bottom(Diagnosis.Analyze(d)));
        }

        [Fact]
        public void Myisam_KeyFileAndGotError_NamesTable()
        {
            var t = Diagnosis.DamagedTables(new[] { Err("[ERROR] Incorrect key file for table '.\\loja\\vendas.MYI'; try to repair it"), Err("[ERROR] Got error 145 when reading table '.\\loja\\estoque'") });
            Assert.Contains("loja.vendas", t);
            Assert.Contains("loja.estoque", t);
        }

        [Fact]
        public void StartupFailure_WithoutTable_PointsToSystemFiles()
        {
            var d = Data(Block(BlockTag.Db, false, Err("[ERROR] Plugin 'InnoDB' init function returned error.")));
            Assert.Equal(DiagVerdict.Possivel, Item(d, "ARQUIVOS").Verdict);
            Assert.Equal(DiagVerdict.SemIndicio, Item(d, "TABELA").Verdict);
        }

        [Fact]
        public void SystemTablespace_IsNotTable()
        {
            var d = Data(Block(BlockTag.Db, false,
                Err("[ERROR] InnoDB: Database page corruption on disk or a failed file read of tablespace ibdata1 page [page id: space=0, page number=7]"),
                Err("[ERROR] InnoDB: The log sequence number 123 in the future")));
            Assert.Equal(DiagVerdict.Provavel, Item(d, "ARQUIVOS").Verdict);
            Assert.Contains("reinstalar", Item(d, "ARQUIVOS").Action);
        }

        [Fact]
        public void NormalStartupNotes_AreNotSystemFileErrors()
        {
            var d = Data(Block(BlockTag.Db, false, Err("[Note] InnoDB: Setting file .\\ibdata1 size to 10 MB")));
            Assert.Equal(DiagVerdict.SemIndicio, Item(d, "ARQUIVOS").Verdict);
        }

        [Fact]
        public void MagicN_IsEngineNotTable()
        {
            var d = Data(Block(BlockTag.Db, false, Err("[ERROR] InnoDB: Assertion failure ... ROW_PREBUILT_FETCH_MAGIC_N")));
            Assert.Equal(DiagVerdict.Provavel, Item(d, "MOTOR").Verdict);
        }

        [Fact]
        public void DiskFull_And_FileLock()
        {
            var d = Data(Block(BlockTag.Db, false,
                Err("[ERROR] mysqld: Disk full (x.MAI); waiting (errno: 28 \"No space left on device\")"),
                Err("[ERROR] InnoDB: Operating system error number 32 in a file operation.")));
            var it = Item(d, "ESPAÇO");
            Assert.Equal(DiagVerdict.Provavel, it.Verdict);
            Assert.Contains("disco cheio", it.Evidence);
            Assert.Contains("antivírus", it.Action);
        }

        [Fact]
        public void BadSector_IsHardware_AndBeatsTableOnly()
        {
            var d = Data(
                Block(BlockTag.Db, false, Err("[ERROR] mysqld: Table '.\\loja\\movimento' is marked as crashed and should be repaired")),
                Block(BlockTag.Disk, true, Ev("disk", 7, "O dispositivo tem um bloco inválido.")));
            Assert.Equal(DiagVerdict.Provavel, Item(d, "HARDWARE").Verdict);
            Assert.Contains("HARDWARE", Diagnosis.Bottom(Diagnosis.Analyze(d)));
            Assert.Contains("hardware abaixo", Item(d, "TABELA").Action);
        }

        [Fact]
        public void EvaluationServer_WithShutdowns()
        {
            var d = Data(Block(BlockTag.Power, true, Ev("EventLog", 6008, "O desligamento anterior não era esperado")));
            d.Os = new OsInfo { ProductName = "Windows Server 2019 Standard Evaluation", ProductType = 3 };
            Assert.Equal(DiagVerdict.Provavel, Item(d, "WINDOWS").Verdict);
        }

        [Fact]
        public void Nothing_NoItems()
        {
            Assert.Empty(Diagnosis.Analyze(Data()));
        }

        [Fact]
        public void Sample_Mariadb104_Corruption_NamesTables()
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "amostras", "mariadb104_corrupcao.err");
            var an = ErrLogAnalyzer.Analyze(System.IO.File.ReadAllText(path), new DateTime(2026, 1, 1));
            var d = Data(Block(BlockTag.Db, false, an.Occurrences.ToArray()));
            var tab = Item(d, "TABELA");
            Assert.Contains("loja.clientes", tab.Evidence + Item(d, "ARQUIVOS").Evidence);
            Assert.Equal(DiagVerdict.Provavel, Item(d, "ESPAÇO").Verdict);
        }

        [Fact]
        public void Actions_AreSentences()
        {
            var d = Data(Block(BlockTag.Db, false,
                Err("[ERROR] mysqld: Disk full (x.MAI) (errno: 28 \"No space left on device\")"),
                Err("[ERROR] InnoDB: Operating system error number 32 in a file operation.")));
            Assert.Contains(". Antivírus", Item(d, "ESPAÇO").Action);
        }

        [Theory]
        [InlineData("Windows 10 Home", "26200", "Windows 11 Home")]
        [InlineData("Windows 10 Pro", "19045", "Windows 10 Pro")]
        [InlineData("Windows 10 Pro", "22000", "Windows 11 Pro")]
        [InlineData("Windows Server 2019 Standard", "17763", "Windows Server 2019 Standard")]
        [InlineData("Windows 10 Home", null, "Windows 10 Home")]
        public void Windows11_IsNotReportedAsWindows10(string product, string build, string expected)
        {
            Assert.Equal(expected, OsRules.NormalizeProductName(product, build));
        }

        [Fact]
        public void Windows11_HasNoFalseSupportWarning()
        {
            Assert.Null(OsRules.SupportEnd(OsRules.NormalizeProductName("Windows 10 Home", "26200")));
        }
    }
}
