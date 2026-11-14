using System;
using System.Collections.Generic;
using System.Linq;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    public class CausasTests
    {
        static readonly DateTime T = new DateTime(2026, 11, 12, 7, 4, 0);

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
            var d = new ReportData();
            var s = new ReportSection { Title = "x" };
            s.Blocks.AddRange(blocks);
            d.Sections.Add(s);
            return d;
        }

        static Occurrence Kp41(long code) =>
            Ev("Microsoft-Windows-Kernel-Power", 41, "Interpretação: " + PowerEvents.KernelPower41Cause(code, 0, false) + "\r\nO sistema foi reiniciado");

        // ---------- tela azul ----------

        [Fact]
        public void Bugcheck_Decoded()
        {
            Assert.Equal("MEMORY_MANAGEMENT — suspeita: RAM defeituosa (ou driver que corrompe memória)", Bugchecks.Describe(0x1A));
            Assert.Equal("", Bugchecks.Describe(0xDEAD));
            Assert.Contains("MEMORY_MANAGEMENT", PowerEvents.KernelPower41Cause(0x1A, 0, false));
            Assert.StartsWith("TELA AZUL (BugcheckCode 0x1A)", PowerEvents.KernelPower41Cause(0x1A, 0, false));
        }

        [Fact]
        public void Bugcheck_FromEvents()
        {
            Assert.Equal(0x1A, Bugchecks.FromOccurrence(Kp41(0x1A)));
            var wer = Ev("Microsoft-Windows-WER-SystemErrorReporting", 1001,
                "O computador foi reinicializado após uma verificação de erro. Essa verificação foi: 0x0000001a (0x0000000000041792, 0xffffecf900683688).");
            Assert.Equal(0x1A, Bugchecks.FromOccurrence(wer));
            Assert.Null(Bugchecks.FromOccurrence(Ev("EventLog", 6008, "O desligamento às 06:29:23 0x00000001 não era esperado")));
        }

        [Fact]
        public void Timeline_BlueScreenShowsName()
        {
            var wer = Ev("Microsoft-Windows-WER-SystemErrorReporting", 1001, "Essa verificação foi: 0x0000001a (0x1, 0x2).");
            string line = string.Join("\n", PowerEvents.Timeline(new[] { wer }, 10));
            Assert.Contains("0x1A MEMORY_MANAGEMENT", line);
        }

        // ---------- volmgr 161 ----------

        [Theory]
        [InlineData(161)] [InlineData(162)] [InlineData(46)]
        public void DumpFailure_IsNotDiskEvent(int id)
        {
            Assert.True(Storage.IsDumpFailure("volmgr", id));
            Assert.False(Storage.IsDiskEvent("volmgr", 2, id));
        }

        [Fact]
        public void OtherVolmgr_StillDiskEvent() => Assert.True(Storage.IsDiskEvent("volmgr", 2, 150));

        // ---------- contadores de confiabilidade ----------

        [Fact]
        public void Reliability_HealthyNvme_IsOk()
        {
            var lines = new List<string>();
            var r = Storage.EvaluateHealth(new[] { new DiskHealth { Name = "ADATA SX8100NP", BusType = "NVMe", HealthStatus = 0, Temperature = 41, Wear = 3, PowerOnHours = 5000, ReadErrorsUncorrected = 0, WriteErrorsUncorrected = 0 } }, lines);
            Assert.Equal(CheckStatus.Ok, r.Status);
            Assert.Contains("contadores de confiabilidade", r.Text);
            Assert.Contains("NVMe", lines[0]);
            Assert.DoesNotContain("ATENÇÃO", lines[0]);
        }

        [Fact]
        public void Reliability_Problems_AreAtencao()
        {
            var lines = new List<string>();
            var r = Storage.EvaluateHealth(new[]
            {
                new DiskHealth { Name = "D1", HealthStatus = 1 },
                new DiskHealth { Name = "D2", HealthStatus = 0, Wear = 90 },
                new DiskHealth { Name = "D3", HealthStatus = 0, ReadErrorsUncorrected = 4 },
                new DiskHealth { Name = "D4", HealthStatus = 0, Temperature = 75 },
                new DiskHealth { Name = "D5", HealthStatus = 0, Temperature = 40 },
            }, lines);
            Assert.Equal(CheckStatus.Atencao, r.Status);
            Assert.StartsWith("4 de 5", r.Text);
            Assert.DoesNotContain("D5 (", r.Text);
        }

        // ---------- RAM ----------

        [Fact]
        public void Memory_Describe_AndMixed()
        {
            var gb = 1024L * 1024 * 1024;
            var same = new[]
            {
                new MemoryModule { Slot = "DIMM A1", Capacity = 8 * gb, SpeedMHz = 2666, Manufacturer = "Kingston", PartNumber = "KF" },
                new MemoryModule { Slot = "DIMM B1", Capacity = 8 * gb, SpeedMHz = 2666, Manufacturer = "Kingston", PartNumber = "KF" },
            };
            var lines = Memory.Describe(same);
            Assert.Contains("2 (16,0 GB no total)", lines[0]);
            Assert.DoesNotContain(lines, l => l.StartsWith("AVISO"));

            var mixed = new[] { same[0], new MemoryModule { Slot = "DIMM B1", Capacity = 4 * gb, SpeedMHz = 2400, PartNumber = "XX" } };
            Assert.Contains(Memory.Describe(mixed), l => l.StartsWith("AVISO: pentes diferentes"));
            Assert.Contains("não informou", Memory.Describe(null)[0]);
        }

        // ---------- causas prováveis ----------

        /// <summary>Caso real: BSOD 0x1A + 223 quedas do MariaDB com páginas corrompidas.</summary>
        [Fact]
        public void ClientCase_PointsToRam()
        {
            var power = Block(BlockTag.Power, true, Kp41(0x1A),
                Ev("Microsoft-Windows-WER-SystemErrorReporting", 1001, "Essa verificação foi: 0x0000001a (0x1)."));
            var dbLines = new List<Occurrence>();
            for (int i = 0; i < 6; i++)
            {
                dbLines.Add(Ev(".err", 0, "InnoDB: Assertion failure in file row0mysql.cc line 1031\r\nInnoDB: Failing assertion: magic2 == ROW_PREBUILT_FETCH_MAGIC_N", "Assertion failure"));
                dbLines.Add(Ev(".err", 0, "[ERROR] mysqld got exception 0x80000003 ;", "got exception"));
            }
            dbLines.Add(Ev(".err", 0, "InnoDB: Apparent corruption in space 1972 page 4", "[ERROR]"));
            var d = Data(power, Block(BlockTag.Db, false, dbLines.ToArray()), Block(BlockTag.Disk, true));

            List<string> h = Hypotheses.Analyze(d);
            Assert.Contains(h, x => x.StartsWith("Possível causa: RAM") && x.Contains("não está confirmada", StringComparison.OrdinalIgnoreCase) && x.Contains("0x1A MEMORY_MANAGEMENT") && x.Contains("ANTES de restaurar"));
            Assert.Contains(h, x => x.Contains("LOOP DE CRASH (12"));
            Assert.DoesNotContain(h, x => x.StartsWith("Disco"));
            Assert.Contains(h, x => x.StartsWith("PROCEDIMENTO: erro de memória") && x.Contains("equipe de serviços") &&
                                    x.Contains("force_recovery=2") && x.Contains("OPTIMIZE completo"));
        }

        /// <summary>Caso real (SERVER-CENTRAL): InnoDB não sobe por página corrompida, 41 tentativas.</summary>
        [Fact]
        public void InnodbStartupFailure_TopCauseWithTablesAndRecovery()
        {
            var lines = new List<Occurrence>();
            for (int i = 0; i < 6; i++)
            {
                lines.Add(Ev(".err", 0, "[ERROR] InnoDB: Failed to read file '.\\gerente\\comunicacao_fila_filial_028.ibd' at offset 151: Page read from tablespace is corrupted.", "[ERROR]"));
                lines.Add(Ev(".err", 0, "[ERROR] InnoDB: Database page corruption on disk or a failed file read of tablespace gerente/comunicacao_fila_filial_009 page [page id: space=#]", "corruption"));
                lines.Add(Ev(".err", 0, "[ERROR] Plugin 'InnoDB' init function returned error.", "[ERROR]"));
                lines.Add(Ev(".err", 0, "[ERROR] Unknown/unsupported storage engine: InnoDB", "[ERROR]"));
                lines.Add(Ev(".err", 0, "[ERROR] Aborting", "Aborting"));
            }
            var power = Block(BlockTag.Power, true, Ev("EventLog", 6008, "O desligamento não era esperado"));
            List<string> h = Hypotheses.Analyze(Data(Block(BlockTag.Db, false, lines.ToArray()), power));
            Assert.StartsWith("BANCO NÃO SOBE", h[0]);
            Assert.Contains("6 tentativa(s)", h[0]);
            Assert.Contains("LOOP", h[0]);
            Assert.Contains("gerente.comunicacao_fila_filial_009", h[0]);
            Assert.Contains("gerente.comunicacao_fila_filial_028", h[0]);
            Assert.Contains("innodb_force_recovery=1", h[0]);
            Assert.Contains("copiar a pasta data ANTES", h[0]);
            Assert.Equal(6, Hypotheses.StartupFailures(lines, null));
        }

        [Fact]
        public void NoStartupFailure_NoBancoNaoSobe()
        {
            var db = Block(BlockTag.Db, false, Ev(".err", 0, "[ERROR] Table './x/y' is marked as crashed", "marked as crashed"));
            Assert.DoesNotContain(Hypotheses.Analyze(Data(db)), x => x.StartsWith("BANCO NÃO SOBE"));
        }

        [Fact]
        public void OsSection_ServerEvaluation_ShowsOnlyServerFields()
        {
            var os = new OsInfo { ProductName = "Windows Server 2019 Datacenter Evaluation", InstallationType = "Server", ProductType = 3, DomainRole = 3, FastStartup = true, PendingRebootChecked = true };
            os.PendingReboot.Add("Windows Update");
            string text = string.Join("\n", OsRules.Lines(os, new DateTime(2026, 11, 13)).ToArray());
            Assert.Contains("servidor membro de domínio", text);
            Assert.Contains("INSTALAÇÃO", text);
            Assert.Contains("atualizações de segurança até 09/01/2029", text);
            Assert.Contains("AVALIAÇÃO", text);
            Assert.Contains("SIM (Windows Update)", text);
            Assert.DoesNotContain("INIC. RÁPIDA", text);
        }

        [Fact]
        public void OsSection_Workstation_ShowsFastStartupAndEndOfSupport()
        {
            var os = new OsInfo { ProductName = "Windows 7 Professional", ProductType = 1, FastStartup = true, PendingRebootChecked = true };
            string text = string.Join("\n", OsRules.Lines(os, new DateTime(2026, 11, 13)).ToArray());
            Assert.Contains("estação de trabalho", text);
            Assert.Contains("sem atualizações de segurança desde 14/01/2020", text);
            Assert.Contains("INIC. RÁPIDA", text);
            Assert.DoesNotContain("INSTALAÇÃO", text);
            Assert.DoesNotContain("AVALIAÇÃO", text);
            Assert.Contains("REINÍCIO PEND.", text);
            Assert.Null(OsRules.SupportEnd("Windows 10 Enterprise LTSC 2019"));
        }

        [Fact]
        public void EvaluationServer_WithUnexpectedShutdown_HasCause()
        {
            var d = Data(Block(BlockTag.Power, true, Kp41(0x0)));
            d.Os = new OsInfo { ProductName = "Windows Server 2019 Datacenter Evaluation", ProductType = 3 };
            Assert.Contains(Hypotheses.Analyze(d), x => x.StartsWith("Windows Server em edição de AVALIAÇÃO"));
            d.Os = new OsInfo { ProductName = "Windows Server 2019 Standard", ProductType = 3 };
            Assert.DoesNotContain(Hypotheses.Analyze(d), x => x.Contains("AVALIAÇÃO"));
        }

        [Fact]
        public void NoMemoryError_NoReinstallProcedure()
        {
            var db = Block(BlockTag.Db, false, Ev(".err", 0, "[ERROR] Table './x/y' is marked as crashed", "marked as crashed"));
            Assert.DoesNotContain(Hypotheses.Analyze(Data(db)), x => x.StartsWith("PROCEDIMENTO"));
        }

        [Fact]
        public void Wheadline_WheaAndMemTest_CountAsRam()
        {
            var hw = Block(BlockTag.Hardware, true,
                Ev("Microsoft-Windows-WHEA-Logger", 17, "Erro de hardware corrigido"),
                Ev("Microsoft-Windows-MemoryDiagnostics-Results", 1102, "erros"));
            List<string> h = Hypotheses.Analyze(Data(hw));
            Assert.Single(h);
            Assert.StartsWith("Indícios de RAM", h[0]);
            Assert.Contains("WHEA", h[0]);
            Assert.Contains("Diagnóstico de Memória", h[0]);
        }

        [Fact]
        public void PowerLoss_DiskBsod_Driver()
        {
            var power = Block(BlockTag.Power, true,
                Ev("Microsoft-Windows-Kernel-Power", 41, "Interpretação: " + PowerEvents.KernelPower41Cause(0, 0, false)),
                Kp41(0x7A), Kp41(0xD1));
            List<string> h = Hypotheses.Analyze(Data(power));
            Assert.Contains(h, x => x.Contains("queda de energia"));
            Assert.Contains(h, x => x.StartsWith("Disco") && x.Contains("KERNEL_DATA_INPAGE_ERROR"));
            Assert.Contains(h, x => x.Contains("DRIVER_IRQL_NOT_LESS_OR_EQUAL") && x.Contains("MEMORY.DMP"));
            Assert.DoesNotContain(h, x => x.Contains("RAM"));
        }

        [Fact]
        public void BadSector_SmartOk_AdvisesTechnicianOrReplacement()
        {
            var disk = Block(BlockTag.Disk, true, Ev("disk", 7, "O dispositivo \\Device\\Harddisk0\\DR0 tem um bloco inválido."));
            var d = Data(disk);
            d.Summary.Add(new SummaryItem { Label = "SMART", Status = CheckStatus.Ok, Text = "ok" });
            List<string> h = Hypotheses.Analyze(d);
            Assert.Contains(h, x => x.StartsWith("Disco com setor defeituoso") && x.Contains("acionar o técnico") && x.Contains("troca") && x.Contains("SMART OK não descarta"));
        }

        [Fact]
        public void Ntfs55_AdvisesBackupAndChkdsk()
        {
            var disk = Block(BlockTag.Disk, true, Ev("Microsoft-Windows-Ntfs", 55, "Foi encontrada uma estrutura corrompida"));
            List<string> h = Hypotheses.Analyze(Data(disk));
            Assert.Contains(h, x => x.StartsWith("Sistema de arquivos corrompido") && x.Contains("backup ANTES") && x.Contains("chkdsk /scan") && x.Contains("SFC/DISM não reparam"));
            Assert.DoesNotContain(h, x => x.StartsWith("Disco com setor defeituoso"));
        }

        [Fact]
        public void OtherDiskEvent_IsNotBadSector()
        {
            var disk = Block(BlockTag.Disk, true, Ev("disk", 153, "A operação de E/S foi repetida"));
            Assert.DoesNotContain(Hypotheses.Analyze(Data(disk)), x => x.StartsWith("Disco com setor defeituoso"));
        }

        [Fact]
        public void NothingFound_NoHypothesis()
        {
            var d = Data(Block(BlockTag.Power, true), Block(BlockTag.Db, false), Block(BlockTag.Disk, true));
            Assert.Empty(Hypotheses.Analyze(d));
            d.Drives.Add("C: 1,0 GB livres de 100,0 GB (1%)  <<< POUCO ESPAÇO");
            Assert.Single(Hypotheses.Analyze(d));
        }

        [Fact]
        public void NotVerifiedBlocks_AreIgnored()
        {
            var bad = ReportBlock.NotVerified("x", "sem permissão");
            bad.Tag = BlockTag.Power;
            Assert.Empty(Hypotheses.Analyze(Data(bad)));
        }

        [Fact]
        public void ReportShowsHypotheses()
        {
            var d = new ReportData { ComputerName = "X", CollectedAt = T, Since = T.AddDays(-30), PeriodDays = 30 };
            Assert.Contains("Nenhum indício forte", ReportBuilder.BuildFull(d));
            d.Hypotheses.Add("RAM DEFEITUOSA é a causa mais provável");
            string full = ReportBuilder.BuildFull(d);
            Assert.Contains("CAUSAS PROVÁVEIS", full);
            Assert.Contains("Qualquer uma das causas abaixo pode ser a origem", full);
            Assert.Contains("1. RAM DEFEITUOSA", full);
            Assert.Contains("1. RAM DEFEITUOSA", ReportBuilder.BuildResumo(d));
        }
    }
}

namespace DiagBIG.Tests
{
    public class DbConfigWarningsTests
    {
        static MyIniOptions Parse(string text) => MyIniParser.Parse("[mysqld]\r\n" + text);

        [Fact]
        public void ClientIni_WarnsAllThree()
        {
            string text = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "amostras", "my_mariadb104_doublewrite_off.ini"));
            var w = DbConfigWarnings.Evaluate(MyIniParser.Parse(text), new[] { "1 desligamento(s) inesperado(s)/tela azul" });
            Assert.Equal(3, w.Count);
            Assert.StartsWith("innodb_force_recovery=1 ESTÁ ATIVO", w[0]);
            Assert.Contains("restaurar o dump", w[0]);
            Assert.Contains("MANUTENÇÃO", w[0]);
            Assert.Contains("PORTA do banco TROCADA", w[0]);
            Assert.DoesNotContain("danificar os dados de vez", w[0]);
            Assert.StartsWith("innodb_doublewrite=OFF", w[1]);
            Assert.Contains("queda de energia, tela azul, RAM/disco", w[1]);
            Assert.Contains("Neste PC o relatório já mostra: 1 desligamento(s)", w[1]);
            Assert.StartsWith("innodb_flush_log_at_trx_commit=2", w[2]);
        }

        [Theory]
        [InlineData("innodb_doublewrite=0")]
        [InlineData("innodb_doublewrite = off")]
        [InlineData("innodb-doublewrite=FALSE")]
        [InlineData("skip-innodb-doublewrite")]
        public void DoublewriteOff_Variants(string line) =>
            Assert.Single(DbConfigWarnings.Evaluate(Parse(line), null), x => x.StartsWith("innodb_doublewrite=OFF"));

        [Theory]
        [InlineData("")]
        [InlineData("innodb_doublewrite=ON")]
        [InlineData("innodb_doublewrite")]
        [InlineData("innodb_flush_log_at_trx_commit=1")]
        [InlineData("innodb_force_recovery=0")]
        public void SafeSettings_NoWarning(string line) => Assert.Empty(DbConfigWarnings.Evaluate(Parse(line), null));

        [Fact]
        public void FlushZero_AndForce4()
        {
            var w = DbConfigWarnings.Evaluate(Parse("innodb_flush_log_at_trx_commit=0\r\ninnodb_force_recovery=4"), null);
            Assert.Equal(2, w.Count);
            Assert.Contains("danificar os dados de vez", w[0]);
            Assert.Contains("também se o banco cair sozinho", w[1]);
            Assert.DoesNotContain("Neste PC", w[1]);
        }

        [Fact]
        public void LaterFileWins_OnMerge()
        {
            var merged = MyIniParser.Merge(new[] { Parse("innodb_doublewrite=OFF"), Parse("innodb_doublewrite=ON") });
            Assert.Empty(DbConfigWarnings.Evaluate(merged, null));
            Assert.Null(MyIniParser.Parse("[client]\r\ninnodb_doublewrite=OFF").DoubleWrite); // fora de [mysqld]
        }

        [Fact]
        public void WarningsReachHypothesesAndReport()
        {
            var b = new ReportBlock { Tag = BlockTag.Db, IsEvents = false, Title = "BANCO" };
            b.Warnings.Add("innodb_doublewrite=OFF no my.ini: teste");
            var d = new ReportData();
            var sec = new ReportSection { Title = "x" };
            sec.Blocks.Add(b);
            d.Sections.Add(sec);
            Assert.Contains(Hypotheses.Analyze(d), x => x.StartsWith("innodb_doublewrite=OFF"));
            Assert.Contains("AVISO: innodb_doublewrite=OFF no my.ini: teste", ReportBuilder.BuildSections(d));
        }
    }
}
