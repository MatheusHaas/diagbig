using System;
using System.Collections.Generic;
using System.Linq;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    public class RepairTests
    {
        static readonly DateTime Since = new DateTime(2026, 10, 17);

        const string Dism =
            "2026-11-16 11:40:01, Info                  DISM   DISM Manager: PID=100 TID=200 Image session has been closed.\r\n" +
            "2026-11-16 11:41:10, Error                 DISM   DISM Package Manager: PID=100 TID=200 Failed to restore the image health. - CDISMPackageManager::RestoreHealth(hr:0x800f0915)\r\n" +
            "2026-11-16 11:41:10, Error                 DISM   DISM Package Manager: PID=100 TID=200 Failed finalizing. - CDISMPackageManager::Internal_Finalize(hr:0x800f0915)\r\n" +
            "2026-11-16 11:41:12, Error                 DISM   DISM.EXE: DISM Package Manager processed the command line but failed. HRESULT=800F0915\r\n" +
            "2025-01-01 10:00:00, Error                 DISM   DISM Package Manager: PID=1 old (hr:0x800f081f)\r\n";

        const string Cbs =
            "2026-11-16 11:50:00, Info                  CSI    00000a1b [SR] Verify complete\r\n" +
            "2026-11-16 11:50:05, Info                  CSI    00000a1c [SR] Repairing corrupted file [ml:1,l:2]\"\\SystemRoot\\x.dll\"\r\n" +
            "2026-11-16 11:50:09, Info                  CSI    00000a1d [SR] Cannot repair member file [l:6]\"a.dll\" of Microsoft-Windows-X\r\n" +
            "2026-11-16 11:50:09, Info                  CSI    00000a1e [SR] Cannot repair member file [l:6]\"b.dll\" of Microsoft-Windows-X\r\n";

        [Fact]
        public void Dism_OneFailurePerCodeAndMinute_IgnoresOld()
        {
            var r = RepairLogAnalyzer.Analyze(Dism, null, Since);
            Assert.Single(r.Failures);
            Assert.Equal("DISM falhou 0x800f0915", r.Failures[0].SummaryKey);
            Assert.Contains("conteúdo de reparo não encontrado", r.Failures[0].Message);
            Assert.NotNull(r.LastDismRun);
        }

        [Fact]
        public void Sfc_CannotRepair_CountedOncePerMinute_AndRepairedCounted()
        {
            var r = RepairLogAnalyzer.Analyze(null, Cbs, Since);
            Assert.Single(r.Failures);
            Assert.Equal(RepairLogAnalyzer.SfcKey, r.Failures[0].SummaryKey);
            Assert.Equal(1, r.SfcRepaired);
            Assert.NotNull(r.LastSfcVerify);
        }

        [Fact]
        public void Empty_NoFailures() => Assert.Empty(RepairLogAnalyzer.Analyze("", "", Since).Failures);

        [Fact]
        public void DiskCodes_AreRecognized()
        {
            Assert.True(RepairLogAnalyzer.IsDiskCode("0x80070570"));
            Assert.False(RepairLogAnalyzer.IsDiskCode("0x800f0915"));
        }

        static ReportData Data(params ReportBlock[] blocks)
        {
            var d = new ReportData();
            var s = new ReportSection { Title = "x" };
            s.Blocks.AddRange(blocks);
            d.Sections.Add(s);
            return d;
        }

        static ReportBlock RepairBlock(string text)
        {
            var b = new ReportBlock { Tag = BlockTag.Repair, IsEvents = false };
            b.Occurrences.AddRange(RepairLogAnalyzer.Analyze(text, null, Since).Failures);
            return b;
        }

        static ReportBlock DiskBlock()
        {
            var b = new ReportBlock { Tag = BlockTag.Disk, IsEvents = true };
            b.Occurrences.Add(new Occurrence { Time = DateTime.Now, Source = "disk", EventId = "153", Message = "E/S repetida", GroupKey = "disk 153", SummaryKey = "disk 153" });
            return b;
        }

        [Fact]
        public void DismFailed_WithDiskEvents_MeansReplaceDisk()
        {
            var d = Data(RepairBlock(Dism), DiskBlock());
            var plan = ActionPlan.Build(d);
            Assert.StartsWith("VEREDITO: DISCO COM DEFEITO GRAVE", plan[0]);
            Assert.Contains("DISM/SFC já falharam aqui", plan[0]);
            Assert.Contains(Hypotheses.Analyze(d), x => x.StartsWith("DISM/SFC já falharam") && x.Contains("trocar o disco"));
        }

        [Fact]
        public void DismFailed_WithoutDiskEvidence_TriesIsoSourceThenCleanInstall()
        {
            var d = Data(RepairBlock(Dism));
            var plan = ActionPlan.Build(d);
            Assert.StartsWith("VEREDITO: sem defeito grave de disco", plan[0]);
            Assert.Contains(plan, x => x.Contains("/Source:wim:") && x.Contains("/LimitAccess") && x.Contains("reinstalar o Windows limpo"));
        }

        [Fact]
        public void DismDiskCode_IsFatalEvenWithoutDiskEvents()
        {
            string log = "2026-11-16 11:41:10, Error                 DISM   DISM Package Manager: PID=1 failed (hr:0x80070570)\r\n";
            var plan = ActionPlan.Build(Data(RepairBlock(log)));
            Assert.StartsWith("VEREDITO: DISCO COM DEFEITO GRAVE", plan[0]);
        }

        [Fact]
        public void ChkdskVerdicts()
        {
            Assert.Contains("SEM PROBLEMAS", ChkdskScan.Verdict("C:", 0));
            Assert.Contains("NÃO CORRIGIDOS", ChkdskScan.Verdict("C:", 3));
            Assert.True(ChkdskScan.IsProblem(3));
            Assert.False(ChkdskScan.IsProblem(0));
        }
    }
}
