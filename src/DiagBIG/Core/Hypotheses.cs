using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DiagBIG.Core
{
    /// <summary>
    /// Cruza os blocos do relatório e lista as causas prováveis (RAM, disco, energia, driver).
    /// É orientação para o suporte: indício não é prova.
    /// </summary>
    public static class Hypotheses
    {
        static readonly string[] DbCorruptionTerms =
        {
            "corrupt", "wrong order", "wrong owned count", "marked as crashed", "Assertion failure",
            "Database page corruption", "Incorrect key file", "flag mismatch", "Summed data size",
        };
        /// <summary>Erro de memória do próprio banco (estrutura em memória corrompida, falta de memória).</summary>
        static readonly string[] DbMemoryErrorTerms = { "MAGIC_N", "Out of memory", "got signal 11" };
        static readonly string[] DbCrashTerms = { "got exception", "Assertion failure", "got signal" };

        /// <summary>Texto (em português e inglês) que indica setor defeituoso numa mensagem de evento de disco.</summary>
        static readonly string[] BadSectorTerms = { "bad block", "bad sector", "bloco inválido", "setor defeituoso", "setor danificado" };

        /// <summary>Evento de disco que é setor defeituoso: disk 7 (bad block) ou mensagem que cite bad block/sector.</summary>
        public static bool IsBadSector(Occurrence o)
        {
            if (o == null) return false;
            if (string.Equals(Grouping.ShortSource(o.Source), "disk", StringComparison.OrdinalIgnoreCase) && o.EventId == "7") return true;
            return ContainsAny(o.Message, null, BadSectorTerms);
        }

        static readonly string[] StartupFailureTerms = { "init function returned error", "Unknown/unsupported storage engine" };
        static readonly Regex CorruptFileRx = new Regex(@"'(?:[^']*[\\/])?([^\\/']+)[\\/]([^\\/']+)\.ibd'", RegexOptions.Compiled);
        static readonly Regex CorruptSpaceRx = new Regex(@"tablespace\s+([\w$]+)/([\w$#]+)", RegexOptions.Compiled);

        /// <summary>Tentativas de subir o banco que falharam (InnoDB aborta na inicialização), do .err e dos eventos do Windows.</summary>
        public static int StartupFailures(IEnumerable<Occurrence> errLines, IEnumerable<Occurrence> events)
        {
            Func<IEnumerable<Occurrence>, int> count = os => os == null ? 0 :
                os.Count(o => ContainsAny(o.Message, null, new[] { StartupFailureTerms[0] }));
            Func<IEnumerable<Occurrence>, int> countEngine = os => os == null ? 0 :
                os.Count(o => ContainsAny(o.Message, null, new[] { StartupFailureTerms[1] }));
            return new[] { count(errLines), countEngine(errLines), count(events), countEngine(events) }.Max();
        }

        /// <summary>Tabelas (banco.tabela) com página corrompida citadas no .err e nos eventos.</summary>
        public static List<string> CorruptTables(IEnumerable<Occurrence> lines)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Occurrence o in lines)
            {
                string msg = o.Message ?? "";
                if (msg.IndexOf("corrupt", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (Match m in CorruptFileRx.Matches(msg)) set.Add(m.Groups[1].Value + "." + m.Groups[2].Value);
                foreach (Match m in CorruptSpaceRx.Matches(msg)) set.Add(m.Groups[1].Value + "." + m.Groups[2].Value);
            }
            return set.ToList();
        }

        /// <summary>innodb_force_recovery ativo no my.ini (aviso gerado por DbConfigWarnings).</summary>
        public static bool ForceRecoveryOn(ReportData d)
        {
            return Blocks(d, BlockTag.Db).SelectMany(b => b.Warnings).Any(w => w.StartsWith("innodb_force_recovery=", StringComparison.OrdinalIgnoreCase));
        }

        static IEnumerable<ReportBlock> Blocks(ReportData d, string tag)
        {
            return d.Sections.SelectMany(s => s.Blocks).Where(b => b.Tag == tag && b.Verified);
        }

        static IEnumerable<Occurrence> Occ(ReportData d, string tag)
        {
            return Blocks(d, tag).SelectMany(b => b.Occurrences);
        }

        /// <summary>Bad block dos últimos Config.BadBlockRecentDays dias (período + histórico). Mais antigo só informa.</summary>
        static DateTime BadBlockLimit(ReportData d)
        {
            DateTime now = d.CollectedAt > new DateTime(2000, 1, 1) ? d.CollectedAt : DateTime.Now;
            return now.AddDays(-Config.BadBlockRecentDays);
        }

        public static List<Occurrence> RecentBadSectors(ReportData d)
        {
            DateTime limit = BadBlockLimit(d);
            return Occ(d, BlockTag.Disk).Concat(Occ(d, BlockTag.DiskOld)).Where(IsBadSector).Where(o => !o.Time.HasValue || o.Time.Value >= limit).ToList();
        }

        public static List<Occurrence> StaleBadSectors(ReportData d)
        {
            DateTime limit = BadBlockLimit(d);
            return Occ(d, BlockTag.Disk).Concat(Occ(d, BlockTag.DiskOld)).Where(IsBadSector).Where(o => o.Time.HasValue && o.Time.Value < limit).ToList();
        }

        /// <summary>"\Device\Harddisk0\DR0 (último em 12/09/2026)" por disco citado nos bad blocks.</summary>
        public static string BadSectorWhere(IList<Occurrence> bad, string singleDiskModel = null)
        {
            var parts = new List<string>();
            foreach (var g in bad.GroupBy(o => DiskTarget.DiskId(o.Message) ?? (string.IsNullOrEmpty(singleDiskModel) ? "disco não identificado no evento" : "o evento não cita o disco, mas o PC tem só um: " + singleDiskModel)))
            {
                DateTime? last = g.Where(o => o.Time.HasValue).Select(o => o.Time).OrderByDescending(t => t).FirstOrDefault();
                parts.Add(g.Key + " (" + g.Count() + "x" + (last.HasValue ? ", último em " + last.Value.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) : "") + ")");
            }
            return string.Join("; ", parts.ToArray());
        }

        /// <summary>Queda de energia, tela azul, hardware e disco achados no relatório (para reforçar os avisos do my.ini).</summary>
        public static List<string> RiskReasons(ReportData d)
        {
            var r = new List<string>();
            int unexpected = PowerEvents.UnexpectedIncidents(Occ(d, BlockTag.Power));
            if (unexpected > 0) r.Add(unexpected + " desligamento(s) inesperado(s)/tela azul");
            if (Occ(d, BlockTag.Hardware).Any()) r.Add("erros de hardware/memória");
            if (Occ(d, BlockTag.Disk).Any()) r.Add("eventos de disco");
            return r;
        }

        /// <summary>Unidades "POUCO ESPAÇO" que importam: a do banco e a do Windows (C:). Volume pequeno de recuperação/EFI não conta.</summary>
        public static List<string> LowSpaceDrives(ReportData d)
        {
            var res = new List<string>();
            foreach (string x in d.Drives)
            {
                if (x == null || !x.Contains("POUCO ESPAÇO") || x.Length < 2) continue;
                string letter = x.Substring(0, 2).ToUpperInvariant();
                if (letter == "C:" || d.DbDrives.Contains(letter)) res.Add(letter);
            }
            return res;
        }

        public static List<string> Analyze(ReportData d)
        {
            var result = new List<string>();

            // ---- telas azuis ----
            var codes = new List<long>();
            foreach (Occurrence o in Occ(d, BlockTag.Power))
            {
                long? c = Bugchecks.FromOccurrence(o);
                if (c != null && PowerEvents.Classify(o.Source, o.EventId) != PowerKind.BlueScreen) codes.Add(c.Value);
            }
            // Kernel-Power 41 e WER 1001 descrevem a mesma tela azul: conta pelos 41; se só houver WER, usa eles.
            if (codes.Count == 0)
                foreach (Occurrence o in Occ(d, BlockTag.Power))
                {
                    long? c = Bugchecks.FromOccurrence(o);
                    if (c != null) codes.Add(c.Value);
                }
            var infos = codes.Select(Bugchecks.Lookup).Where(i => i != null).ToList();
            int bsodRam = infos.Count(i => i.Suspect == BugcheckSuspect.Ram);
            int bsodRamDriver = infos.Count(i => i.Suspect == BugcheckSuspect.RamOuDriver || i.Suspect == BugcheckSuspect.Hardware);
            int bsodDisk = infos.Count(i => i.Suspect == BugcheckSuspect.Disco);
            int bsodDriver = infos.Count(i => i.Suspect == BugcheckSuspect.Driver);

            // ---- hardware / memória ----
            var hw = Occ(d, BlockTag.Hardware).ToList();
            int memTestFailed = hw.Count(o => o.EventId == Config.MemDiagErrorId.ToString());
            int whea = hw.Count(o => Grouping.ShortSource(o.Source).StartsWith("WHEA", StringComparison.OrdinalIgnoreCase));

            // ---- energia ----
            int powerLoss = Occ(d, BlockTag.Power).Count(o =>
                PowerEvents.Classify(o.Source, o.EventId) == PowerKind.Unexpected &&
                (o.Message ?? "").Contains("QUEDA/CORTE DE ENERGIA"));
            int powerButton = Occ(d, BlockTag.Power).Count(o => (o.Message ?? "").Contains("BOTÃO DE ENERGIA"));

            // ---- banco ----
            var dbLines = Occ(d, BlockTag.Db).ToList();
            int dbCorruption = dbLines.Count(o => ContainsAny(o.Message, o.SummaryKey, DbCorruptionTerms) && !ContainsAny(o.Message, o.SummaryKey, DbCrashTerms));
            int dbCrashes = dbLines.Count(o => ContainsAny(o.Message, o.SummaryKey, DbCrashTerms));
            dbCorruption += Occ(d, BlockTag.DbEvents).Count(o => ContainsAny(o.Message, null, DbCorruptionTerms));
            bool dbDamaged = dbCorruption > 0 || dbCrashes > 0;

            // ---- disco ----
            bool diskEvents = Occ(d, BlockTag.Disk).Any();
            bool smartBad = d.Summary.Any(s => s.Label == "SMART" && s.Status == CheckStatus.Atencao);
            var lowDrives = LowSpaceDrives(d);
            bool lowSpace = lowDrives.Count > 0;

            bool ramEvidence = bsodRam > 0 || memTestFailed > 0 || whea > 0;

            // 1) RAM (entra na lista depois do disco quando o disco tem evidência mais forte)
            var ramR = new List<string>();
            if (ramEvidence && dbDamaged)
            {
                var ev = RamEvidence(infos, bsodRam, bsodRamDriver, memTestFailed, whea);
                ramR.Add("Possível causa: RAM defeituosa (" + ev + "). Não está confirmada: a queda/corrupção do banco também pode vir de bug do MariaDB, disco, energia ou do my.ini. " +
                           "Testar a memória (MemTest86, várias passadas, ou mdsched.exe) e testar pente a pente ANTES de restaurar o banco; " +
                           "senão a base pode corromper de novo.");
            }
            else if (ramEvidence)
            {
                ramR.Add("Indícios de RAM com defeito (" + RamEvidence(infos, bsodRam, bsodRamDriver, memTestFailed, whea) +
                           "). Testar a memória (MemTest86 ou mdsched.exe) e testar pente a pente.");
            }
            else if (bsodRamDriver > 0)
            {
                ramR.Add("Tela azul de driver ou RAM (" + string.Join(", ", infos.Where(i => i.Suspect == BugcheckSuspect.RamOuDriver || i.Suspect == BugcheckSuspect.Hardware).Select(i => i.Name).Distinct().ToArray()) +
                           "): rodar o teste de memória e atualizar drivers (placa de vídeo, rede, chipset).");
            }

            // 2) disco
            var diskR = new List<string>();
            int diskRetries = Occ(d, BlockTag.Disk).Count(o => string.Equals(Grouping.ShortSource(o.Source), "disk", StringComparison.OrdinalIgnoreCase) && o.EventId == "153");
            var recentBad = RecentBadSectors(d);
            int badSectors = recentBad.Count;
            if (badSectors > 0)
                diskR.Add("Disco com setor defeituoso (bad block, " + badSectors + " ocorrência(s) nos últimos " + Config.BadBlockRecentDays / 30 + " meses; " + BadSectorWhere(recentBad, d.SingleDiskModel) + "): acionar o técnico e/ou efetuar a troca do disco/servidor. " +
                           "Fazer backup agora. " + (smartBad ? "" : "O SMART não acusou falha, mas SMART OK não descarta o disco: o setor ruim pode já ter sido remapeado ou o disco estar atrás de RAID. ") +
                           "chkdsk /r não conserta disco que está morrendo.");
            int ntfsCorrupt = Occ(d, BlockTag.Disk).Count(o => string.Equals(Grouping.ShortSource(o.Source), "Ntfs", StringComparison.OrdinalIgnoreCase) && o.EventId == "55");
            if (ntfsCorrupt > 0)
                diskR.Add("Sistema de arquivos corrompido (Ntfs 55, " + ntfsCorrupt + "x): o NTFS achou erro na estrutura do volume (pode vir de desligamento brusco, energia, controladora ou disco ruim). " +
                           "Fazer backup ANTES de reparar, depois rodar chkdsk /scan e, na janela de manutenção, chkdsk /f (o /r também procura setor ruim e demora). " +
                           "SFC/DISM não reparam o NTFS. Se voltar depois do chkdsk, suspeitar do disco, cabo, controladora ou energia" +
                           (badSectors > 0 ? "; junto com o setor defeituoso acima, tratar como disco com falha." : "."));
            var oldDisk = Occ(d, BlockTag.DiskOld).ToList();
            if (oldDisk.Count > 0)
            {
                int oldBad = StaleBadSectors(d).Count;
                int oldNtfs = oldDisk.Count(o => string.Equals(Grouping.ShortSource(o.Source), "Ntfs", StringComparison.OrdinalIgnoreCase) && (o.EventId == "55" || o.EventId == "98"));
                diskR.Add("Histórico de disco ANTES do período (ainda no log): " + string.Join("; ", DiskTarget.OldSummary(oldDisk).ToArray()) + ". " +
                          (oldBad > 0 ? "Bad block com mais de " + Config.BadBlockRecentDays / 30 + " meses (" + BadSectorWhere(StaleBadSectors(d)) + "): NÃO conta para trocar o disco sozinho; se o banco já foi reinstalado e os dados saíram sem erro, só acompanhar (SMART e chkdsk /scan) e ver se volta. " : "") +
                          (oldNtfs > 0 ? "NTFS corrompido antigo: rodar chkdsk /scan agora para ver se o erro continua e se há mais de uma unidade afetada. " : "") +
                          "Se o evento não diz a unidade (volume ??), o relatório mostra se o PC tem um disco só.");
            }
            if (bsodDisk > 0 || diskEvents || smartBad)
            {
                var ev = new List<string>();
                if (bsodDisk > 0) ev.Add("tela azul de disco: " + string.Join(", ", infos.Where(i => i.Suspect == BugcheckSuspect.Disco).Select(i => i.Name).Distinct().ToArray()));
                if (diskRetries >= Config.DiskRetryThreshold) ev.Add("disk 153 (E/S repetida pelo Windows) " + diskRetries + "x: o disco está falhando ou lento demais");
                if (diskEvents) ev.Add("eventos de disco/NTFS");
                if (smartBad) ev.Add("SMART/confiabilidade em ATENÇÃO");
                diskR.Add("Disco, cabo ou controladora sob suspeita (" + string.Join("; ", ev.ToArray()) + "). Fazer backup agora e rodar chkdsk /scan e a ferramenta do fabricante do disco." +
                           (diskEvents && !smartBad && d.Summary.Any(x => x.Label == "SMART" && x.Status == CheckStatus.Ok) ? " SMART OK não descarta o disco quando há eventos de disco." : ""));
            }

            // Disco com evidência forte (setor ruim, NTFS 55, E/S repetida em volume) vem antes da RAM,
            // a não ser que a RAM tenha prova direta (WHEA ou teste de memória do Windows falhou).
            var repairFails = Occ(d, BlockTag.Repair).ToList();
            bool repairNotSource = repairFails.Count > 0 && !RepairLogAnalyzer.OnlyNonDisk(repairFails);
            if (repairFails.Count > 0)
            {
                var repairKeys = repairFails.Select(o => o.SummaryKey).Distinct().ToList();
                string what = repairNotSource ? "arquivos do Windows corrompidos que o reparo não conseguiu consertar. "
                                              : "o código do DISM só indica que faltou a origem do reparo ou que o comando foi digitado errado, não defeito de disco. ";
                string todo = diskEvents && repairNotSource
                    ? "Junto com os eventos de disco/NTFS, tratar como disco com falha: trocar o disco, não reinstalar no mesmo."
                    : (diskEvents ? "Os eventos de disco acima são tratados à parte. Para o Windows: refazer o DISM com a sintaxe certa e, se pedir origem, usar /Source com um ISO da mesma versão."
                                  : "Sem evento de disco nos logs: tentar DISM com /Source de um ISO da mesma versão; se falhar de novo, reinstalar o Windows limpo; se a corrupção voltar, suspeitar do disco/RAM.");
                diskR.Add("DISM/SFC já falharam neste PC (" + string.Join("; ", repairKeys.ToArray()) + "): " + what + todo);
            }
            bool diskStrong = badSectors > 0 || ntfsCorrupt > 0 || diskRetries >= Config.DiskRetryThreshold || (repairNotSource && diskEvents);
            if (diskStrong && memTestFailed == 0 && whea == 0) { result.AddRange(diskR); result.AddRange(ramR); }
            else { result.AddRange(ramR); result.AddRange(diskR); }

            // 3) driver
            if (bsodDriver > 0)
                result.Add("Tela azul típica de driver (" + string.Join(", ", infos.Where(i => i.Suspect == BugcheckSuspect.Driver).Select(i => i.Name).Distinct().ToArray()) +
                           "): abrir o C:\\Windows\\MEMORY.DMP no BlueScreenView/WinDbg para ver qual driver causou.");

            // 3b) Windows Server em avaliação com desligamentos inesperados
            if (OsRules.IsEvaluation(d.Os) && OsRules.IsServer(d.Os) &&
                Occ(d, BlockTag.Power).Any(o => PowerEvents.IsUnexpected(PowerEvents.Classify(o.Source, o.EventId))))
                result.Add("Windows Server em edição de AVALIAÇÃO com desligamentos inesperados: se o prazo da avaliação expirou, o Windows desliga sozinho de hora em hora " +
                           "(e derruba o banco). Conferir com \"slmgr /xpr\"; se expirou, ativar com licença definitiva.");

            // 4) energia
            if (powerLoss > 0)
                result.Add("Desligamento sem tela azul e sem botão (" + powerLoss + "x): provável queda de energia, fonte ou no-break. Verificar a energia/no-break; queda de energia corrompe banco.");
            if (powerButton > 0)
                result.Add("Desligamento forçado pelo botão de energia (" + powerButton + "x): alguém segurou o botão (PC travado?). Verificar o motivo do travamento.");

            // 4b) banco novo já corrompendo
            string recent = RecentInstall.Cause(d, dbDamaged);
            if (recent != null) result.Add(recent);

            // 5) banco sem causa de hardware
            if (dbDamaged && !ramEvidence && bsodDisk == 0 && !diskEvents && !smartBad && powerLoss == 0)
                result.Add("Banco corrompido/caindo sem causa de hardware nos logs do Windows. Mesmo assim suspeitar de RAM (teste de memória), " +
                           "disco (chkdsk) e desligamentos forçados; checar a versão do MariaDB/MySQL.");
            if (dbCrashes >= Config.CrashLoopThreshold)
                result.Add("Banco em LOOP DE CRASH (" + dbCrashes + " quedas no período): o serviço cai e reinicia sozinho. " +
                           "Tentar subir com innodb_force_recovery=1 ou 2, somente, com a porta 3307 para manutenção; se não resolver, acionar a equipe técnica. Não deixar o sistema gravando nele.");

            // 5a) banco que não sobe: InnoDB aborta na inicialização (vai para o topo da lista)
            var dbAll = dbLines.Concat(Occ(d, BlockTag.DbEvents)).ToList();
            int startupFails = StartupFailures(dbLines, Occ(d, BlockTag.DbEvents));
            if (startupFails > 0)
            {
                List<string> tables = CorruptTables(dbAll);
                string loop = startupFails >= Config.CrashLoopThreshold ? ", em LOOP: o serviço tenta subir e cai sem parar" : "";
                string tabs = tables.Count == 0 ? "" :
                    " Tabelas com página corrompida:" + Diagnosis.TableLines(tables, 15, "       ") + "\n      ";
                if (d.DbServiceRunning == true)
                    result.Insert(0, "BANCO FALHOU AO SUBIR " + startupFails + " vez(es) (o InnoDB abortou na inicialização) e AGORA O SERVIÇO ESTÁ RODANDO" +
                                     (ForceRecoveryOn(d) ? " em modo de recuperação (innodb_force_recovery no my.ini): o banco só lê, não grava" : "") + "." + tabs +
                                     " Salvar o que der em OUTRO disco usando a porta 3307 para manutenção (o sistema do cliente não pode gravar). " +
                                     "Force somente 1 ou 2; se precisar de mais, acionar a equipe técnica. A causa da corrupção (energia, RAM, disco) precisa ser tratada, senão volta.");
                else
                    result.Insert(0, "BANCO NÃO SOBE: o InnoDB aborta na inicialização (" + startupFails + " tentativa(s) falhou" + loop + ")." + tabs +
                                     " Tentar subir com innodb_force_recovery=1 ou 2, somente, com a porta 3307 para manutenção, e salvar o que der em OUTRO disco. " +
                                     "Se não subir, acionar a equipe técnica. A causa da corrupção (energia, RAM, disco) precisa ser tratada, senão volta.");
            }

            // 5b) erro de memória no banco: procedimento de reinstalação
            if (dbLines.Any(o => ContainsAny(o.Message, o.SummaryKey, DbMemoryErrorTerms)))
                result.Add("PROCEDIMENTO: erro de memória no banco (ex.: assertion ROW_PREBUILT_FETCH_MAGIC_N). Se persistir mesmo com innodb_force_recovery=2 e com OPTIMIZE completo, " +
                           "reinstalar o MariaDB com a equipe de serviços é uma opção: tirar o dump em manutenção (porta trocada), reinstalar (de preferência o último 10.4.x), restaurar o dump e conferir. " +
                           "REPAIR/OPTIMIZE não resolvem esse erro, porque o problema não está numa tabela.");

            // 6) configuração do banco (my.ini)
            foreach (string cw in Blocks(d, BlockTag.Db).SelectMany(b => b.Warnings))
                if (!result.Contains(cw)) result.Add(cw);

            // 7) espaço
            if (lowSpace)
                result.Add("Pouco espaço em disco na unidade " + string.Join(", ", lowDrives.ToArray()) + " (veja UNIDADES): liberar espaço; disco cheio derruba o banco e o Windows.");

            return result;
        }

        static string RamEvidence(List<BugcheckInfo> infos, int bsodRam, int bsodRamDriver, int memTestFailed, int whea)
        {
            var ev = new List<string>();
            if (bsodRam > 0) ev.Add("tela azul de memória: " + string.Join(", ", infos.Where(i => i.Suspect == BugcheckSuspect.Ram).Select(i => "0x" + i.Code.ToString("X") + " " + i.Name).Distinct().ToArray()));
            else if (bsodRamDriver > 0) ev.Add("tela azul de driver/RAM: " + string.Join(", ", infos.Where(i => i.Suspect == BugcheckSuspect.RamOuDriver || i.Suspect == BugcheckSuspect.Hardware).Select(i => i.Name).Distinct().ToArray()));
            if (whea > 0) ev.Add(whea + " erro(s) de hardware WHEA");
            if (memTestFailed > 0) ev.Add("Diagnóstico de Memória do Windows achou erros");
            return string.Join("; ", ev.ToArray());
        }

        static bool ContainsAny(string a, string b, string[] terms)
        {
            string text = (a ?? "") + "\n" + (b ?? "");
            return terms.Any(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
