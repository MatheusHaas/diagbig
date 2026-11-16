using System;
using System.Collections.Generic;
using System.Linq;

namespace DiagBIG.Core
{
    /// <summary>
    /// "O que fazer": passo a passo para o suporte. Duas rotas:
    /// disco com defeito grave (setor ruim, NTFS corrompido, SMART, E/S repetida em volume) = trocar o disco/servidor, sem reinstalar no mesmo;
    /// sem defeito grave de disco = tentar reparar o Windows (chkdsk, DISM, SFC) e só depois reinstalar.
    /// Indício dos logs, não prova.
    /// </summary>
    public static class ActionPlan
    {
        static IEnumerable<Occurrence> Occ(ReportData d, string tag)
        {
            return d.Sections.SelectMany(s => s.Blocks).Where(b => b.Tag == tag && b.Verified).SelectMany(b => b.Occurrences);
        }

        static bool Is(Occurrence o, string source, string id)
        {
            return string.Equals(Grouping.ShortSource(o.Source), source, StringComparison.OrdinalIgnoreCase) && o.EventId == id;
        }

        public static List<string> Build(ReportData d)
        {
            var plan = new List<string>();
            var disk = Occ(d, BlockTag.Disk).ToList();
            int badSectors = disk.Count(Hypotheses.IsBadSector);
            int ntfs = disk.Count(o => Is(o, "Ntfs", "55") || Is(o, "Ntfs", "98"));
            int retries = disk.Count(o => Is(o, "disk", "153"));
            bool smartBad = d.Summary.Any(s => s.Label == "SMART" && s.Status == CheckStatus.Atencao);

            var power = Occ(d, BlockTag.Power).ToList();
            int unexpected = power.Count(o => PowerEvents.IsUnexpected(PowerEvents.Classify(o.Source, o.EventId)));
            var bug = power.Select(Bugchecks.FromOccurrence).Where(c => c != null).Select(c => Bugchecks.Lookup(c.Value)).Where(i => i != null).ToList();
            bool ramSuspect = bug.Any(i => i.Suspect == BugcheckSuspect.Ram) || Occ(d, BlockTag.Hardware).Any(o => o.EventId == Config.MemDiagErrorId.ToString()) ||
                              Occ(d, BlockTag.Hardware).Any(o => Grouping.ShortSource(o.Source).StartsWith("WHEA", StringComparison.OrdinalIgnoreCase));
            bool driverBsod = bug.Any(i => i.Suspect == BugcheckSuspect.Driver);

            var dbAll = Occ(d, BlockTag.Db).Concat(Occ(d, BlockTag.DbEvents)).ToList();
            bool dbDamaged = dbAll.Any(o => ((o.Message ?? "") + (o.SummaryKey ?? "")).IndexOf("corrupt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            ((o.Message ?? "") + (o.SummaryKey ?? "")).IndexOf("Assertion failure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            ((o.Message ?? "") + (o.SummaryKey ?? "")).IndexOf("got exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            ((o.Message ?? "") + (o.SummaryKey ?? "")).IndexOf("init function returned error", StringComparison.OrdinalIgnoreCase) >= 0);

            var repair = Occ(d, BlockTag.Repair).ToList();
            var repairCodes = repair.Select(o => o.SummaryKey).Distinct().ToList();
            bool repairFailed = repair.Count > 0;
            bool repairDiskCode = repair.Any(o => o.SummaryKey != null && o.SummaryKey.StartsWith(RepairLogAnalyzer.DismKey, StringComparison.Ordinal) &&
                                                  RepairLogAnalyzer.IsDiskCode(o.SummaryKey.Substring(o.SummaryKey.LastIndexOf(' ') + 1)));
            bool dismFailed = repair.Any(o => o.SummaryKey != null && o.SummaryKey.StartsWith(RepairLogAnalyzer.DismKey, StringComparison.Ordinal));

            bool fatalDisk = badSectors > 0 || ntfs > 0 || smartBad || retries >= Config.DiskRetryThreshold ||
                             repairDiskCode || (repairFailed && disk.Count > 0);
            bool anyProblem = fatalDisk || repairFailed || dbDamaged || unexpected > 0 || ramSuspect || driverBsod || disk.Count > 0;
            if (!anyProblem) return plan;

            // Evidência de disco em texto.
            var ev = new List<string>();
            if (badSectors > 0) ev.Add(badSectors + "x setor defeituoso (bad block)");
            if (ntfs > 0) ev.Add(ntfs + "x NTFS corrompido (Ntfs 55/98)");
            if (smartBad) ev.Add("SMART em atenção");
            if (retries >= Config.DiskRetryThreshold) ev.Add(retries + "x disk 153 (E/S repetida)");
            if (repairFailed) ev.Add("DISM/SFC já falharam aqui (" + string.Join(", ", repairCodes.Select(c => c.Replace(RepairLogAnalyzer.DismKey + " ", "")).ToArray()) + ")");

            bool running = d.DbServiceRunning == true;
            bool forceRec = Hypotheses.ForceRecoveryOn(d);
            int n = 0;
            Func<string, string> step = t => (++n) + ". " + t;

            // Passo comum: salvar os dados antes de mexer em qualquer coisa.
            string save = dbDamaged || !running
                ? "SALVAR OS DADOS ANTES DE MEXER: parar o serviço do banco e copiar a pasta data inteira para OUTRO disco/máquina. " +
                  (running && forceRec ? "O banco já está em modo de recuperação: tirar o dump agora (mysqldump, porta trocada). " :
                   "Com o banco corrompido, tirar o dump subindo com innodb_force_recovery=1 (aumentar de 1 em 1 até 3; 4 ou mais pode perder dados). ") +
                  "Conferir que a cópia/dump abre antes de seguir."
                : "SALVAR OS DADOS ANTES DE MEXER: dump do banco (mysqldump) e cópia da pasta data e dos arquivos do sistema do cliente para OUTRO disco/máquina; conferir que abre.";

            if (fatalDisk)
            {
                plan.Add("VEREDITO: DISCO COM DEFEITO GRAVE (" + string.Join("; ", ev.ToArray()) + "). NÃO reinstalar nem restaurar neste disco/servidor: TROCAR o disco (ou o servidor).");
                plan.Add(step(save));
                plan.Add(step("NÃO rodar chkdsk /r, DISM nem SFC neste disco antes da cópia: forçam leitura e escrita num disco que está morrendo. SMART OK não descarta o disco; o setor ruim pode já ter sido remapeado."));
                plan.Add(step("Trocar o disco por um novo (SSD/HD de marca; no servidor, de preferência 2 discos em espelho/RAID 1 e cópia automática para fora do servidor) ou migrar para outro servidor."));
                plan.Add(step("No disco novo: instalar o Windows limpo, instalar o MariaDB (datadir novo), restaurar o dump e rodar mysqlcheck para conferir as tabelas."));
                if (ramSuspect)
                    plan.Add(step("Há sinal de RAM com defeito (tela azul de memória/WHEA/teste do Windows): testar a RAM (MemTest86 ou MemTest86+ em pen drive, várias passadas, pente a pente) ANTES de pôr o servidor em produção; senão corrompe de novo."));
                else
                    plan.Add(step("Testar a RAM de qualquer jeito (MemTest86+ em pen drive ou mdsched.exe > Estendido) antes de pôr em produção: disco e RAM ruins aparecem juntos."));
            }
            else
            {
                plan.Add("VEREDITO: sem defeito grave de disco nos logs. Dá para tentar reparar antes de reinstalar, nesta ordem (parar quando o problema sumir).");
                plan.Add(step(save));
                if (ramSuspect)
                    plan.Add(step("RAM primeiro: testar com MemTest86/MemTest86+ (pen drive, várias passadas, pente a pente) ou mdsched.exe > Estendido. Reparar Windows ou banco com RAM defeituosa não adianta: volta a corromper."));
                plan.Add(step("chkdsk C: /scan (e D: se o banco estiver lá); se achar erro, chkdsk /f na janela de manutenção. Se o chkdsk achar setor ruim ou voltar com erro, mudar para a rota de TROCAR O DISCO."));
                if (dismFailed)
                    plan.Add(step("O DISM já falhou aqui (" + string.Join(", ", repairCodes.ToArray()) + ") e os logs não mostram defeito de disco. Tentar UMA vez com origem local: montar o ISO da MESMA versão/build do Windows e rodar " +
                                  "DISM /Online /Cleanup-Image /RestoreHealth /Source:wim:X:\\sources\\install.wim:1 /LimitAccess (X: = letra do ISO; se for install.esd, trocar wim por esd), depois sfc /scannow e reiniciar. " +
                                  "Se falhar de novo: reinstalar o Windows limpo (não vale insistir). Se a corrupção voltar na instalação limpa, é hardware: trocar o disco."));
                else
                    plan.Add(step("Reparar o Windows: DISM /Online /Cleanup-Image /RestoreHealth e depois sfc /scannow, e reiniciar. Isso repara só arquivos do Windows (não o NTFS nem o banco); serve para tela azul de driver, travamento e serviço do Windows quebrado."));
                if (dbDamaged)
                    plan.Add(step("Banco corrompido (REPAIR/OPTIMIZE não resolvem quando é página do InnoDB, ibdata1 ou erro de memória): com o dump em mãos, reinstalar o MariaDB (datadir novo) e restaurar."));
                plan.Add(step("Se continuar caindo ou dando tela azul depois do DISM/SFC: reinstalar o Windows (de preferência instalação limpa) e o MariaDB, e restaurar o dump."));
                plan.Add(step("Se mesmo reinstalado voltar a cair ou corromper: é hardware (disco, RAM, fonte, placa); trocar o servidor/peças em vez de reinstalar de novo."));
            }

            if (unexpected > 0)
                plan.Add(step("Energia: " + unexpected + " desligamento(s) inesperado(s). Usar no-break (nobreak) com bateria boa no servidor; queda de energia corrompe banco."));
            plan.Add(step("my.ini: manter innodb_doublewrite ligado (padrão) e innodb_flush_log_at_trx_commit=1 em servidor sem no-break; tirar innodb_force_recovery depois do dump. Ver os AVISOS da seção do banco."));
            plan.Add(step("Depois de resolver, rodar o DiagBIG de novo após alguns dias para confirmar que as quedas e os erros pararam."));
            return plan;
        }
    }
}
