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

        public static List<string> Build(ReportData d, List<string> n1 = null)
        {
            var plan = new List<string>();
            var disk = Occ(d, BlockTag.Disk).ToList();
            int badSectors = disk.Count(Hypotheses.IsBadSector);
            int ntfs = disk.Count(o => Is(o, "Ntfs", "55") || Is(o, "Ntfs", "98"));
            int retries = disk.Count(o => Is(o, "disk", "153"));
            bool smartBad = d.Summary.Any(s => s.Label == "SMART" && s.Status == CheckStatus.Atencao);

            var power = Occ(d, BlockTag.Power).ToList();
            int unexpected = PowerEvents.UnexpectedIncidents(power);
            var bug = power.Select(Bugchecks.FromOccurrence).Where(c => c != null).Select(c => Bugchecks.Lookup(c.Value)).Where(i => i != null).ToList();
            bool ramSuspect = bug.Any(i => i.Suspect == BugcheckSuspect.Ram) || Occ(d, BlockTag.Hardware).Any(o => o.EventId == Config.MemDiagErrorId.ToString()) ||
                              Occ(d, BlockTag.Hardware).Any(o => Grouping.ShortSource(o.Source).StartsWith("WHEA", StringComparison.OrdinalIgnoreCase));
            bool driverBsod = bug.Any(i => i.Suspect == BugcheckSuspect.Driver);

            var dbAll = Occ(d, BlockTag.Db).Concat(Occ(d, BlockTag.DbEvents)).ToList();
            bool dbDamaged = dbAll.Any(o => ((o.Message ?? "") + (o.SummaryKey ?? "")).IndexOf("corrupt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            ((o.Message ?? "") + (o.SummaryKey ?? "")).IndexOf("Assertion failure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            ((o.Message ?? "") + (o.SummaryKey ?? "")).IndexOf("got exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            ((o.Message ?? "") + (o.SummaryKey ?? "")).IndexOf("init function returned error", StringComparison.OrdinalIgnoreCase) >= 0);

            dbDamaged = dbDamaged || d.Diagnosis.Any(i => i.Verdict != DiagVerdict.SemIndicio &&
                                                          (i.Area.StartsWith("TABELA") || i.Area.StartsWith("ARQUIVOS") || i.Area.StartsWith("MOTOR")));

            var repair = Occ(d, BlockTag.Repair).ToList();
            var repairCodes = repair.Select(o => o.SummaryKey).Distinct().ToList();
            bool repairFailed = repair.Count > 0;
            bool repairDiskCode = repair.Any(o => o.SummaryKey != null && o.SummaryKey.StartsWith(RepairLogAnalyzer.DismKey, StringComparison.Ordinal) &&
                                                  RepairLogAnalyzer.IsDiskCode(o.SummaryKey.Substring(o.SummaryKey.LastIndexOf(' ') + 1)));
            bool dismFailed = repair.Any(o => o.SummaryKey != null && o.SummaryKey.StartsWith(RepairLogAnalyzer.DismKey, StringComparison.Ordinal));

            bool fatalDisk = badSectors > 0 || ntfs > 0 || smartBad || retries >= Config.DiskRetryThreshold ||
                             repairDiskCode || (repairFailed && disk.Count > 0);
            bool anyProblem = fatalDisk || repairFailed || dbDamaged || unexpected > 0 || ramSuspect || driverBsod || disk.Count > 0;
            if (!anyProblem)
            {
                if (n1 != null) n1.Add("Nenhum indício forte nos logs lidos. Sem encaminhamento para o N2 por este relatório.");
                return plan;
            }

            if (n1 != null)
            {
                DiagItem tI = d.Diagnosis.FirstOrDefault(i => i.Area.StartsWith("TABELA")), sI = d.Diagnosis.FirstOrDefault(i => i.Area.StartsWith("ARQUIVOS")),
                         mI = d.Diagnosis.FirstOrDefault(i => i.Area.StartsWith("MOTOR"));
                bool tabOnly = tI != null && tI.Verdict == DiagVerdict.Provavel &&
                               (sI == null || sI.Verdict != DiagVerdict.Provavel) && (mI == null || mI.Verdict != DiagVerdict.Provavel);
                var why = new List<string>();
                if (badSectors > 0) why.Add("setor defeituoso no disco");
                if (ntfs > 0) why.Add("NTFS corrompido");
                if (smartBad) why.Add("SMART em atenção");
                if (retries >= Config.DiskRetryThreshold) why.Add("disco com E/S repetida");
                if (repairDiskCode) why.Add("reparo do Windows falhou por disco");
                if (fatalDisk)
                {
                    n1.Add("AÇÃO: Acione a equipe técnica para TROCAR O SERVIDOR/DISCO e reinstalar. Não reinstalar neste disco.");
                    n1.Add("Motivo: " + (why.Count > 0 ? string.Join("; ", why.ToArray()) : "erros de disco repetidos") + ".");
                }
                else if (dbDamaged && tabOnly && !ramSuspect)
                {
                    var tabs = Diagnosis.DamagedTables(dbAll);
                    n1.Add("AÇÃO: Acione o N2 para tratar as tabelas:");
                    foreach (string t in tabs.Take(15)) n1.Add("- " + t);
                    if (tabs.Count > 15) n1.Add("... e mais " + (tabs.Count - 15));
                    n1.Add("Em caso de não sucesso, acionar equipe técnica.");
                    n1.Add("Motivo: dano só em tabela(s) e nenhum erro de disco/RAM nos logs.");
                }
                else if (dbDamaged)
                {
                    n1.Add("AÇÃO: Acione a equipe técnica para REINSTALAR NO MESMO TERMINAL.");
                    n1.Add("Motivo: dano além de tabelas (arquivos de sistema do banco, banco que não sobe ou erro de memória do banco), sem defeito de disco nos logs." +
                           (ramSuspect ? " HÁ SINAL DE RAM: testar a RAM antes." : ""));
                }
                else
                {
                    n1.Add("AÇÃO: Acione o N2 para reparo simples do Windows (DISM e SFC). Sem dano de banco nem de disco grave nos logs.");
                    n1.Add("Motivo: " + (unexpected > 0 ? unexpected + " desligamento(s) inesperado(s)" : ramSuspect ? "sinal de RAM" : driverBsod ? "tela azul de driver" : "eventos de disco/Windows") + ".");
                }
            }

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
                ? "TENTAR SALVAR OS DADOS EM OUTRO DISCO: subir o banco com innodb_force_recovery=1 ou 2, somente, usando a porta 3307 para manutenção (o sistema do cliente não conecta). " +
                  (running && forceRec ? "O banco já está em modo de recuperação: usar a porta 3307 e salvar o que der. " : "") +
                  "Se não subir, acionar a equipe técnica."
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
                    plan.Add(step("Testar a RAM de qualquer jeito (MemTest86+ em pen drive ou o botão Testar memória (mdsched) > Estendido) antes de pôr em produção: disco e RAM ruins aparecem juntos."));
            }
            else
            {
                plan.Add("VEREDITO: sem defeito grave de disco nos logs. Dá para tentar reparar antes de reinstalar, nesta ordem (parar quando o problema sumir).");
                plan.Add(step(save));
                DiagItem tabItem = d.Diagnosis.FirstOrDefault(i => i.Area.StartsWith("TABELA")), sysItem = d.Diagnosis.FirstOrDefault(i => i.Area.StartsWith("ARQUIVOS")),
                         motItem = d.Diagnosis.FirstOrDefault(i => i.Area.StartsWith("MOTOR"));
                bool onlyTables = tabItem != null && tabItem.Verdict == DiagVerdict.Provavel &&
                                  (sysItem == null || sysItem.Verdict != DiagVerdict.Provavel) && (motItem == null || motItem.Verdict != DiagVerdict.Provavel);
                if (onlyTables && !ramSuspect)
                    plan.Add(step("TENTATIVA DO N2 ANTES DE REINSTALAR (só vale sem erro de disco e de RAM, como nos logs acima): o dano parece restrito a tabela(s). " +
                                  "N2: subir o banco com innodb_force_recovery=1 ou 2 (no máximo 2), reparar ou recriar as tabelas com erro (OPTIMIZE, REPAIR TABLE ou DROP e recriar) e reparar o Windows com DISM + SFC. " +
                                  "Se não der certo, ou se precisar de force 3 ou mais: EQUIPE TÉCNICA (dump, reinstalação ou troca de servidor)."));
                if (ramSuspect)
                    plan.Add(step("RAM primeiro: testar com MemTest86/MemTest86+ (pen drive, várias passadas, pente a pente) ou o botão Testar memória (mdsched) > Estendido. Reparar Windows ou banco com RAM defeituosa não adianta: volta a corromper."));
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
            plan.Add(step("my.ini: manter innodb_doublewrite ligado (padrão) e innodb_flush_log_at_trx_commit=1 em servidor sem no-break; tirar innodb_force_recovery depois da manutenção. Ver os AVISOS da seção do banco."));
            return plan;
        }
    }
}
