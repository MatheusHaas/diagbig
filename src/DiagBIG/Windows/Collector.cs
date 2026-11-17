using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DiagBIG.Core;

namespace DiagBIG.Windows
{
    internal class CollectionResult
    {
        public ReportData Data;
        public string FullText;
        public string ResumoText;
        public string SavedPath;   // null = não conseguiu gravar
        public string SaveError;
    }

    /// <summary>Executa todas as partes da coleta. Cada parte que falha vira NÃO VERIFICADO e as outras seguem.</summary>
    internal static class Collector
    {
        public static CollectionResult Run(int days, Action<string> progress)
        {
            progress = progress ?? (_ => { });
            if (days < 1) days = Config.DefaultPeriodDays;
            if (days > Config.MaxPeriodDays) days = Config.MaxPeriodDays;

            DateTime now = DateTime.Now;
            var d = new ReportData
            {
                ComputerName = SystemInfo.HostName(),
                CollectedAt = now,
                PeriodDays = days,
                Since = now.AddDays(-days),
            };
            d.OutputFileName = ReportBuilder.FileName(d.ComputerName, now);

            progress("Lendo informações do sistema...");
            d.WindowsVersion = SystemInfo.WindowsVersion();
            try { d.Os = SystemInfo.ReadOs(); } catch (Exception) { }
            try { d.Drives.AddRange(SystemInfo.FixedDrives()); }
            catch (Exception ex) { d.DrivesError = ex.Message; }

            foreach (string log in new[] { "System", "Application" })
            {
                string name = log == "System" ? "Sistema" : "Aplicativo";
                try { d.LogCoverages.Add(new LogCoverage { LogName = name, Oldest = EventLogs.OldestEvent(log) }); }
                catch (Exception ex) { d.LogCoverages.Add(new LogCoverage { LogName = name, Error = ex.Message }); }
            }

            string time = EventLogs.TimeFilter(d.Since);
            try
            {
                bool capped;
                d.LogClearedEvents.AddRange(EventLogs.Query("System",
                    "*[System[Provider[@Name='" + Config.LogClearedProvider + "'] and EventID=" + Config.LogClearedId + " and " + time + "]]",
                    null, out capped));
            }
            catch (Exception ex) { d.LogClearedError = ex.Message; }

            // ---------- Parte 1: disco ----------
            progress("Lendo eventos de disco/NTFS...");
            var diskSection = new ReportSection { Title = "DISCO / NTFS" };
            int dumpFailures = 0;
            ReportBlock disk = RunEvents("EVENTOS DE DISCO/NTFS", () =>
            {
                string xpath = "*[System[((Level=1 or Level=2) or (Level=3 and " +
                               EventLogs.IdsFilter(Config.DiskWarningIds.Concat(Config.StorageWarningIds)) + ")) and " + time + "]]";
                bool capped;
                var list = EventLogs.Query("System", xpath, rec =>
                {
                    if (Storage.IsDumpFailure(rec.ProviderName, rec.Id)) { dumpFailures++; return false; }
                    return Storage.IsDiskEvent(rec.ProviderName, rec.Level ?? 0, rec.Id);
                }, out capped);
                return Tuple(list, capped);
            });
            disk.Tag = BlockTag.Disk;
            Dictionary<string, string> volumeLetters = null;
            string singleDisk = null;
            try { volumeLetters = SystemInfo.VolumeLetters(); } catch (Exception) { }
            try { singleDisk = SystemInfo.SingleDiskNote(); } catch (Exception) { }
            foreach (Occurrence o in disk.Occurrences) DiskTarget.Annotate(o, volumeLetters, singleDisk);
            if (singleDisk != null) disk.Info.Add(singleDisk);
            if (dumpFailures > 0)
                disk.Info.Add("Ignorados " + dumpFailures + " evento(s) volmgr " + string.Join("/", Config.DumpFailureIds.Select(i => i.ToString()).ToArray()) +
                              " (falha ao gravar o despejo de memória): consequência de tela azul, não defeito de disco.");
            disk.Info.Insert(0, "Log Sistema: Crítico/Erro de " + string.Join(", ", Config.DiskSources) +
                                "; avisos disk " + string.Join("/", Config.DiskWarningIds.Select(i => i.ToString()).ToArray()) +
                                "; aviso 129 (reset do dispositivo) dessas fontes.");
            diskSection.Blocks.Add(disk);
            d.Summary.Add(ReportBuilder.Summarize("DISCO", new[] { disk }));

            // Histórico: o mesmo filtro (Crítico/Erro de disk, Ntfs etc.), mas ANTES do período escolhido, enquanto o log ainda tiver.
            progress("Procurando eventos antigos de disco/NTFS...");
            DateTime sinceCopy = d.Since;
            ReportBlock diskOld = RunEvents("HISTÓRICO DE DISCO/NTFS ANTES DO PERÍODO (ainda no log do Windows)", () =>
            {
                bool capped;
                var list = EventLogs.Query("System", "*[System[(Level=1 or Level=2)]]", rec =>
                    rec.TimeCreated.HasValue && rec.TimeCreated.Value < sinceCopy &&
                    !Storage.IsDumpFailure(rec.ProviderName, rec.Id) && Storage.IsDiskEvent(rec.ProviderName, rec.Level ?? 0, rec.Id), out capped);
                return Tuple(list, capped);
            });
            diskOld.Tag = BlockTag.DiskOld;
            foreach (Occurrence o in diskOld.Occurrences) DiskTarget.Annotate(o, volumeLetters, singleDisk);
            diskOld.Info.Insert(0, "Erros e críticos de disco/NTFS (inclui bad block, disk 7, Ntfs 55) de antes de " + d.Since.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) +
                                   ", enquanto o log do Windows ainda os guarda. Não entram na contagem do período, mas bad block e NTFS corrompido antigos contam para o veredito.");
            if (diskOld.Occurrences.Count > 0) diskSection.Blocks.Add(diskOld);
            else if (!diskOld.Verified) diskSection.Blocks.Add(diskOld);
            if (diskOld.Occurrences.Count > 0)
                d.Summary.Add(new SummaryItem { Label = "DISCO (HISTÓRICO)", Status = CheckStatus.Atencao, Text = string.Join("; ", DiskTarget.OldSummary(diskOld.Occurrences).ToArray()) + " (antes do período; veja a seção DISCO / NTFS)" });

            progress("Verificando RAID e SMART...");
            try { d.Raid = SystemInfo.DetectRaid(); }
            catch (Exception ex) { d.RaidError = "falha ao consultar controladoras via WMI: " + ex.Message; }
            SummaryItem smartSummary;
            ReportBlock smartBlock = SystemInfo.Smart(d.Raid, out smartSummary);
            smartBlock.Tag = BlockTag.Smart;
            diskSection.Blocks.Add(smartBlock);
            d.Summary.Add(smartSummary);
            progress("Lendo dism.log e CBS.log...");
            ReportBlock repair = ReadRepairLogs(d.Since);
            diskSection.Blocks.Add(repair);
            if (!repair.InfoOnly)
                d.Summary.Add(ReportBuilder.Summarize("REPARO DO WINDOWS (DISM/SFC)", new[] { repair }));
            d.Sections.Add(diskSection);

            // ---------- Reinícios e desligamentos ----------
            progress("Lendo reinícios e desligamentos...");
            var powerSection = new ReportSection { Title = "REINÍCIOS E DESLIGAMENTOS" };
            List<Occurrence> power = null;
            ReportBlock shut = RunEvents("DESLIGAMENTOS INESPERADOS (KERNEL-POWER 41, EVENTLOG 6008, TELA AZUL)", () =>
            {
                bool capped;
                power = EventLogs.Query("System", PowerXPath(time), null, out capped);
                return Tuple(power.Where(o => PowerEvents.IsUnexpected(PowerEvents.Classify(o.Source, o.EventId))).ToList(), capped);
            });
            shut.Tag = BlockTag.Power;
            shut.Info.Add("Queda de energia / desligamento forçado é causa comum de banco corrompido.");
            shut.Info.Add("No Kernel-Power 41 vem a interpretação: tela azul, botão de energia ou provável queda de energia.");
            powerSection.Blocks.Add(shut);
            d.Summary.Add(ReportBuilder.Summarize("DESLIGAMENTOS INESPERADOS", new[] { shut }));

            try { d.LastBoot = SystemInfo.LastBoot(); } catch (Exception) { }
            var timeline = new ReportBlock { Title = "LIGA/DESLIGA AGRUPADO (MAIS RECENTE PRIMEIRO)", InfoOnly = true };
            if (power == null)
            {
                timeline.Verified = false;
                timeline.NotVerifiedReason = shut.NotVerifiedReason;
                d.Summary.Add(new SummaryItem { Label = "LIGA/DESLIGA", Status = CheckStatus.NaoVerificado, Text = shut.NotVerifiedReason });
            }
            else
            {
                List<string> lines = PowerEvents.TimelineGrouped(power);
                if (lines.Count == 0) timeline.Info.Add("Nenhum outro evento de liga/desliga no período.");
                timeline.Info.AddRange(lines);
                timeline.Info.Add("(desligamentos inesperados e telas azuis: veja o bloco acima)");
                timeline.Info.Add("");
                timeline.Info.Add("Obs.: no Windows 8/10/11 com Inicialização Rápida, \"Desligar\" vira hibernação e não gera");
                timeline.Info.Add("LIGOU/DESLIGOU CORRETAMENTE; aparecem SUSPENSÃO/VOLTOU. \"Reiniciar\" sempre gera.");
                d.Summary.Add(PowerEvents.Summarize(power, d.LastBoot));
            }
            powerSection.Blocks.Add(timeline);
            d.Sections.Add(powerSection);

            // ---------- Hardware / memória (possíveis causadores) ----------
            progress("Verificando memória RAM e erros de hardware...");
            var hwSection = new ReportSection { Title = "MEMÓRIA RAM E HARDWARE" };
            ReportBlock hw = RunEvents("ERROS DE HARDWARE (WHEA) E DIAGNÓSTICO DE MEMÓRIA", () =>
            {
                string xpath = "*[System[(Provider[@Name='" + Config.WheaProvider + "'] and (Level=1 or Level=2 or Level=3)) or " +
                               "(Provider[@Name='" + Config.MemDiagProvider + "'] and EventID=" + Config.MemDiagErrorId + ") or " +
                               "(Provider[@Name='" + Config.ResourceExhaustionProvider + "'] and EventID=" + Config.ResourceExhaustionId + ")" +
                               " and " + time + "]]";
                bool capped;
                return Tuple(EventLogs.Query("System", xpath, null, out capped), capped);
            });
            hw.Tag = BlockTag.Hardware;
            try { hw.Info.InsertRange(0, Memory.Describe(SystemInfo.MemoryModules())); }
            catch (Exception ex) { hw.Info.Insert(0, "Pentes de RAM indisponíveis: " + ex.Message); }
            hw.Info.Add("WHEA = erro de hardware (RAM, CPU, PCIe); Resource-Exhaustion 2004 = pouca memória virtual.");
            hw.Info.Add("O Windows só registra o teste de memória se ele rodou: se nunca rodou, executar mdsched.exe ou MemTest86 (botão Testar memória).");
            try
            {
                bool cap2;
                var last = EventLogs.Query("System", "*[System[Provider[@Name='" + Config.MemDiagProvider + "'] and (EventID=" + Config.MemDiagOkId + " or EventID=" + Config.MemDiagErrorId + ")]]", null, out cap2);
                if (last.Count == 0) hw.Info.Add("Último teste de memória do Windows: NUNCA RODOU neste log (use o botão Testar memória).");
                else
                {
                    Occurrence o = last[0];
                    string when = o.Time.HasValue ? o.Time.Value.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "?";
                    hw.Info.Add("Último teste de memória do Windows: " + when + " — " + (o.EventId == Config.MemDiagErrorId.ToString() ? "ERROS DE HARDWARE DETECTADOS (trocar/testar pente a pente)" : "sem erros (o modo padrão é curto; não descarta pente ruim)"));
                }
            }
            catch (Exception) { }
            hwSection.Blocks.Add(hw);
            d.Summary.Add(ReportBuilder.Summarize("MEMÓRIA/HARDWARE", new[] { hw }));

            // Relógio: data/hora do Windows alterada por mais de 1 dia (bateria da placa-mãe/CMOS fraca). Com data errada o banco não sobe.
            ReportBlock clock = RunEvents("RELÓGIO DO WINDOWS (DATA/HORA ALTERADA POR MAIS DE 1 DIA)", () =>
            {
                bool capped;
                var list = EventLogs.Query("System",
                    "*[System[Provider[@Name='" + Config.TimeChangeProvider + "'] and EventID=" + Config.TimeChangeId + " and " + time + "]]",
                    rec => EventLogs.TimeJumpDays(rec) >= Config.TimeChangeMinDays, out capped);
                return Tuple(list, capped);
            });
            clock.Tag = BlockTag.Clock;
            clock.Info.Add("Ajuste de NTP (segundos) é ignorado. Salto de dias ou anos após ligar o PC indica bateria da placa-mãe (CR2032) fraca ou data errada na BIOS.");
            clock.Info.Add("Com a data errada o banco não sobe (e certificados, licenças e agendamentos falham). Conferir data/hora no Windows e na BIOS.");
            hwSection.Blocks.Add(clock);
            d.Summary.Add(ReportBuilder.Summarize("RELÓGIO (DATA/HORA)", new[] { clock }));
            d.Sections.Add(hwSection);

            // ---------- Parte 2: banco ----------
            progress("Procurando serviços do banco...");
            var dbSection = new ReportSection { Title = "BANCO MYSQL/MARIADB (.ERR)" };
            List<DbService> services = new List<DbService>();
            string servicesError = null;
            try { services = DbDiscovery.FindServices(); }
            catch (Exception ex) { servicesError = "falha ao ler serviços no registro: " + ex.Message; }

            var targets = new List<DbTarget>();
            foreach (DbService s in services)
            {
                try { targets.Add(DbDiscovery.ResolveForService(s)); }
                catch (Exception ex) { targets.Add(new DbTarget { Service = s, Label = "BANCO (" + s.Name + ")", NotFoundReason = "falha ao localizar o .err: " + ex.Message }); }
            }
            if (services.Count == 0 || targets.Any(t => t.ErrPath == null))
            {
                progress("Varredura de reserva das pastas do banco...");
                try { targets.AddRange(DbDiscovery.Scan(targets.Where(t => t.ErrPath != null).Select(t => t.ErrPath).ToList())); }
                catch (Exception ex) { servicesError = (servicesError == null ? "" : servicesError + "; ") + "falha na varredura: " + ex.Message; }
            }

            if (targets.Count == 0)
            {
                string reason = (servicesError != null ? servicesError + "; " : "") +
                                "nenhum serviço com " + string.Join("/", Config.DbProcessNames) + " no registro e nenhum .err nas pastas padrão (varredura)";
                var b = ReportBlock.NotVerified(null, reason, false);
                dbSection.Blocks.Add(b);
                d.Summary.Add(ReportBuilder.Summarize("BANCO", new[] { b }));
            }
            foreach (DbTarget t in targets)
            {
                progress("Lendo " + (t.ErrPath ?? t.Label) + "...");
                ReportBlock b = AnalyzeTarget(t, d);
                b.Tag = BlockTag.Db;
                dbSection.Blocks.Add(b);
                d.Summary.Add(ReportBuilder.Summarize(t.Label, new[] { b }));
            }
            d.Sections.Add(dbSection);

            // ---------- Eventos do banco no Windows ----------
            progress("Lendo eventos do banco no Windows...");
            var terms = new List<string>(Config.DbEventSources);
            foreach (DbService s in services) { terms.Add(s.Name); if (!string.IsNullOrEmpty(s.DisplayName)) terms.Add(s.DisplayName); }

            var evSection = new ReportSection { Title = "EVENTOS DO BANCO NO WINDOWS" };
            ReportBlock app = RunEvents("LOG APLICATIVO: ERRO/CRÍTICO DE MYSQL/MARIADB E APPLICATION ERROR 1000", () =>
            {
                string xpath = "*[System[(Level=1 or Level=2) and " + time + "]]";
                bool capped;
                var list = EventLogs.Query("Application", xpath, rec =>
                {
                    string p = rec.ProviderName ?? "";
                    if (EventLogs.ContainsAny(p, Config.DbEventSources)) return true;
                    return string.Equals(p, Config.AppErrorProvider, StringComparison.OrdinalIgnoreCase) && rec.Id == Config.AppErrorId
                           && EventLogs.Mentions(rec, Config.DbProcessNames);
                }, out capped);
                return Tuple(list, capped);
            });
            ReportBlock scm = RunEvents("LOG SISTEMA: SERVICE CONTROL MANAGER " + string.Join("/", Config.ScmEventIds.Select(i => i.ToString()).ToArray()) + " CITANDO O BANCO", () =>
            {
                string xpath = "*[System[Provider[@Name='" + Config.ScmProvider + "'] and " + EventLogs.IdsFilter(Config.ScmEventIds) + " and " + time + "]]";
                bool capped;
                return Tuple(EventLogs.Query("System", xpath, rec => EventLogs.Mentions(rec, terms), out capped), capped);
            });
            app.Tag = BlockTag.DbEvents;
            evSection.Blocks.Add(app);
            evSection.Blocks.Add(scm);
            d.Summary.Add(ReportBuilder.Summarize("EVENTOS DO BANCO NO WINDOWS", new[] { app, scm }));
            d.Sections.Add(evSection);

            progress("Montando relatório...");
            d.DbServiceRunning = services.Count == 0 ? (bool?)null : services.Any(s => s.Status == "rodando");
            try { d.Hypotheses.AddRange(Hypotheses.Analyze(d)); } catch (Exception) { }
            try { d.Diagnosis.AddRange(Diagnosis.Analyze(d)); } catch (Exception) { }
            try { d.ActionPlan.AddRange(ActionPlan.Build(d, d.N1)); } catch (Exception) { }
            var r = new CollectionResult { Data = d };
            r.FullText = ReportBuilder.BuildFull(d);
            r.ResumoText = ReportBuilder.BuildResumo(d);
            string err;
            r.SavedPath = Save(r.FullText, d.OutputFileName, out err);
            r.SaveError = err;
            return r;
        }

        static ReportBlock AnalyzeTarget(DbTarget t, ReportData d)
        {
            var b = new ReportBlock { Title = t.Label, IsEvents = false };
            b.Info.AddRange(t.Info);
            if (t.Options != null)
            {
                b.Warnings.AddRange(DbConfigWarnings.Evaluate(t.Options, Hypotheses.RiskReasons(d)));
                if (b.Warnings.Count > 0)
                    b.SummaryNote = "my.ini com opção de risco (ver AVISO)";
            }
            if (t.ErrPath == null)
            {
                b.Verified = false;
                b.NotVerifiedReason = t.NotFoundReason ?? ".err não encontrado";
                return b;
            }
            b.Info.Add(".err usado ....: " + t.ErrPath);
            b.Info.Add("Descoberto ....: " + t.HowFound);
            try
            {
                ErrReadResult read = ErrTailReader.ReadTail(t.ErrPath, Config.ErrTailBytes);
                ErrAnalysis an = ErrLogAnalyzer.Analyze(read.Text, d.Since);
                b.Info.Add("Leitura .......: " + (read.Truncated
                    ? "últimos " + ReportBuilder.FormatBytes(read.BytesRead) + " de " + ReportBuilder.FormatBytes(read.FileSize)
                    : "arquivo inteiro (" + ReportBuilder.FormatBytes(read.FileSize) + ")") + ", " + read.EncodingName);
                b.Info.Add("Cobertura .....: " + (an.FirstDate == null
                    ? "nenhuma linha com data (todas as linhas relevantes foram incluídas)"
                    : "leitura do .err cobre desde " + ReportBuilder.DateTimeShort(an.FirstDate) + " até " + ReportBuilder.DateTimeShort(an.LastDate)));
                d.ErrCoverages.Add(new ErrCoverage { Label = t.Label, FirstDate = an.FirstDate, Truncated = read.Truncated, FileSize = read.FileSize, BytesRead = read.BytesRead });
                var notes = new List<string>();
                if (t.FallbackNote != null) notes.Add(t.FallbackNote);
                if (read.Truncated && an.FirstDate != null && an.FirstDate.Value > d.Since)
                    notes.Add(".err lido só desde " + ReportBuilder.Date(an.FirstDate));
                var others = DbDiscovery.OtherErrFiles(t.DataDir, t.ErrPath, d.Since);
                foreach (FileInfo f in others)
                    b.Info.Add("AVISO: outro .err alterado no período, NÃO lido: " + f.FullName + " (alterado em " +
                               ReportBuilder.DateTimeShort(f.LastWriteTime) + ", " + ReportBuilder.FormatBytes(f.Length) + ")");
                if (others.Count > 0)
                    notes.Add("outro .err alterado no período não lido: " + string.Join(", ", others.Select(f => f.Name).ToArray()));
                if (notes.Count > 0)
                    b.SummaryNote = string.Join("; ", (b.SummaryNote != null ? new[] { b.SummaryNote } : new string[0]).Concat(notes).ToArray());
                b.Occurrences.AddRange(an.Occurrences);
                int startupFails = Hypotheses.StartupFailures(an.Occurrences, null);
                if (startupFails > 0)
                    b.SummaryNote = string.Join("; ", (b.SummaryNote != null ? new[] { b.SummaryNote } : new string[0]).Concat(new[] { "BANCO NÃO SOBE (InnoDB aborta na inicialização, " + startupFails + "x)" }).ToArray());
            }
            catch (Exception ex)
            {
                b.Verified = false;
                b.NotVerifiedReason = "não foi possível ler " + t.ErrPath + ": " + ex.Message;
            }
            return b;
        }

        /// <summary>Eventos de liga/desliga do log Sistema (classificados em PowerEvents).</summary>
        static string PowerXPath(string time)
        {
            return "*[System[(" +
                   "(Provider[@Name='" + Config.EventLogProvider + "'] and (EventID=6005 or EventID=6006 or EventID=" + Config.UnexpectedShutdownId + ")) or " +
                   "(Provider[@Name='User32'] and EventID=1074) or " +
                   "(Provider[@Name='" + Config.KernelPowerProvider + "'] and (EventID=" + Config.KernelPowerId + " or EventID=42)) or " +
                   "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001) or " +
                   "(Provider[@Name='BugCheck'] and EventID=1001) or " +
                   "(Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1)" +
                   ") and " + time + "]]";
        }

        static KeyValuePair<List<Occurrence>, bool> Tuple(List<Occurrence> list, bool capped)
        {
            return new KeyValuePair<List<Occurrence>, bool>(list, capped);
        }

        /// <summary>DISM e SFC deixam o resultado em dism.log e CBS.log; se alguém já tentou reparar e falhou, aparece aqui.</summary>
        static ReportBlock ReadRepairLogs(DateTime since)
        {
            var b = new ReportBlock { Title = "REPARO DO WINDOWS (DISM/SFC JÁ EXECUTADOS)", IsEvents = false, Tag = BlockTag.Repair };
            try
            {
                string root = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
                string dismPath = Path.Combine(root, @"Logs\DISM\dism.log");
                string cbsPath = Path.Combine(root, @"Logs\CBS\CBS.log");
                string dism = null, cbs = null;
                if (File.Exists(dismPath)) dism = ReadLogTail(dismPath, 6L * 1024 * 1024, b);
                if (File.Exists(cbsPath)) cbs = ReadLogTail(cbsPath, 10L * 1024 * 1024, b);
                if (dism == null && cbs == null)
                {
                    b.InfoOnly = true;
                    b.Info.Add("dism.log e CBS.log não encontrados em " + root + @"\Logs: DISM/SFC nunca rodaram neste PC.");
                    return b;
                }
                RepairResult r = RepairLogAnalyzer.Analyze(dism, cbs, since);
                b.Occurrences.AddRange(r.Failures);
                b.Info.Add("DISM e SFC registram o resultado aqui. Só aparece o que alguém já rodou (DISM /RestoreHealth, sfc /scannow).");
                b.Info.Add("Último DISM no período: " + (r.LastDismRun == null ? "nenhum" : ReportBuilder.DateTimeLong(r.LastDismRun)) +
                           " | última verificação do SFC: " + (r.LastSfcVerify == null ? "nenhuma" : ReportBuilder.DateTimeLong(r.LastSfcVerify)) +
                           " | arquivos reparados pelo SFC: " + r.SfcRepaired);
                if (r.LastDismRun == null && r.LastSfcVerify == null) b.SummaryNote = "DISM/SFC não rodaram no período";
            }
            catch (Exception ex) { b.Verified = false; b.NotVerifiedReason = "falha ao ler dism.log/CBS.log: " + ex.Message; }
            return b;
        }

        static string ReadLogTail(string path, long maxBytes, ReportBlock b)
        {
            ErrReadResult r = ErrTailReader.ReadTail(path, maxBytes);
            if (r.Truncated)
                b.Info.Add("AVISO: " + Path.GetFileName(path) + ": lidos só os últimos " + ReportBuilder.FormatBytes(r.BytesRead) + " de " + ReportBuilder.FormatBytes(r.FileSize) + ".");
            return r.Text;
        }

        static ReportBlock RunEvents(string title, Func<KeyValuePair<List<Occurrence>, bool>> query)
        {
            var b = new ReportBlock { Title = title };
            try
            {
                var r = query();
                b.Occurrences.AddRange(r.Key);
                if (r.Value) b.Info.Add("AVISO: muitos eventos; lidos só os " + Config.MaxEventsPerQuery + " mais recentes da consulta.");
            }
            catch (EventLogNotFoundException ex) { b.Verified = false; b.NotVerifiedReason = "log não encontrado: " + ex.Message; }
            catch (UnauthorizedAccessException ex) { b.Verified = false; b.NotVerifiedReason = "sem permissão (rode como administrador): " + ex.Message; }
            catch (Exception ex) { b.Verified = false; b.NotVerifiedReason = "falha ao ler o log de eventos: " + ex.Message; }
            return b;
        }

        /// <summary>
        /// Grava em DiagBIG_relatorios ao lado do .exe; se não der, na Área de Trabalho. UTF-8 com BOM.
        /// Depois mantém só os últimos relatórios de cada PC nessa pasta.
        /// </summary>
        static string Save(string text, string fileName, out string error)
        {
            error = null;
            var dirs = new List<string>();
            try { dirs.Add(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)); } catch (Exception) { }
            try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch (Exception) { }
            var errors = new List<string>();
            foreach (string dir in dirs.Where(x => !string.IsNullOrEmpty(x)))
            {
                string folder = Path.Combine(dir, Config.ReportsFolder);
                string path = Path.Combine(folder, fileName);
                try
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(path, text, new UTF8Encoding(true));
                    CleanupOldReports(folder);
                    return path;
                }
                catch (Exception ex) { errors.Add(folder + ": " + ex.Message); }
            }
            error = "não foi possível gravar o TXT (" + string.Join("; ", errors.ToArray()) + ")";
            return null;
        }

        /// <summary>Apaga só relatórios do próprio DiagBIG além dos últimos Config.KeepReports por PC. Falha aqui não atrapalha a coleta.</summary>
        static void CleanupOldReports(string folder)
        {
            try
            {
                var names = Directory.GetFiles(folder, "DiagBIG_*.txt").Select(Path.GetFileName).ToList();
                foreach (string name in ReportBuilder.ReportsToDelete(names, Config.KeepReports))
                {
                    try { File.Delete(Path.Combine(folder, name)); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }
}
