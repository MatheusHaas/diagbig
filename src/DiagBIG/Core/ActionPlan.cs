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

        /// <summary>
        /// Roteiro do N2 para o caso "só tabelas" (sem numeração; quem chama numera). Banco sobe só com force 1 ou 2, sempre na porta 3307
        /// (o sistema do cliente não conecta), OPTIMIZE/ALTER só nas tabelas listadas e, depois, sem force ainda na 3307. Sem dump pelo N2.
        /// </summary>
        public static List<string> TableRoute(IList<string> tables)
        {
            var r = new List<string>();
            r.Add("CONFIRMAR QUE É SÓ TABELA: no .err mais recente só aparecem tabelas com \"corrupt\"/\"marked as crashed\"/reparo solicitado, sem erro de página do ibdata1, redo log (ib_logfile) ou memória do InnoDB; rodar chkdsk /scan na unidade do banco e ver se saiu limpo. Em banco grande NÃO usar mysqlcheck na base inteira (demora): ler o .err.");
            r.Add("Parar o serviço do MariaDB e, no my.ini [mysqld]: innodb_force_recovery=1 ou 2 (começar no 1; se não subir, 2; NUNCA 3 ou mais), port=3307 (o sistema do cliente não conecta; manutenção; se o cliente já usa a 3307 ou ela está ocupada, usar a próxima livre, ex.: 3308), innodb_doublewrite=ON (se estiver OFF, tirar a linha) e innodb_flush_log_at_trx_commit=1 (se estiver 2 ou 0). Anotar os valores de antes. Subir o serviço.");
            r.Add("Observar a subida: se o serviço sobe, opera alguns segundos e cai de novo (loop de subir e cair) com force 1 e 2, ou só sobe com force 3 ou mais, NÃO é só tabela: parar e acionar a EQUIPE TÉCNICA.");
            r.Add("Subiu: ler o .err recente E o novo que a subida gerou e listar as tabelas com \"corrupt\"/\"marked as crashed\"/reparo solicitado" + (tables != null && tables.Count > 0 ? " (já identificadas acima: " + tables.Count + ")" : "") + ". Se aparecer erro de página/ibdata1/redo log ou de memória, não é só tabela: EQUIPE TÉCNICA.");
            r.Add("Ainda com force e na 3307, SÓ nas tabelas listadas: OPTIMIZE TABLE <tabela>; ou ALTER TABLE <tabela> ENGINE=InnoDB; (uma por vez; o force deixa o banco só para reparo). Conferir no .err que o erro não se repete.");
            r.Add("Reparou: parar o serviço, TIRAR o innodb_force_recovery do my.ini, manter a porta 3307 e subir. Deixar alguns minutos observando o .err: se opera normal, siga; se cair ou entrar em loop, EQUIPE TÉCNICA (não insistir).");
            r.Add("Operou normal sem force: voltar a porta para a original do cliente (em geral 3306), reiniciar o serviço e rodar, nesta ordem, bigupdate.exe (confere MD5 dos arquivos da base atualizador), acertaestrutura.exe (compara com campos.txt e ajusta tabelas) e acertasequenciador.exe (ajusta a sequência dos índices). Só com o banco SEM force e sem OPTIMIZE pendente.");
            r.Add("Qualquer passo que falhar, o banco não subir sem force ou a corrupção voltar: EQUIPE TÉCNICA (dump, reinstalação ou troca de servidor).");
            return r;
        }

        public static string DisabledServiceText(IEnumerable<string> names)
        {
            return "SERVIÇO DO BANCO DESATIVADO (" + string.Join(", ", names.ToArray()) + "): confirmar se não consta troca de servidor recente (pode ser um serviço antigo que ficou) e NÃO ativar.";
        }

        public static List<string> Build(ReportData d, List<string> n1 = null)
        {
            var plan = BuildCore(d, n1);
            if (n1 != null && d.DisabledDbServices.Count > 0) n1.Add("ATENÇÃO " + DisabledServiceText(d.DisabledDbServices));
            if (d.WmiRepo == WmiRepoState.Inconsistente)
            {
                if (n1 != null) n1.Add(WmiRepo.N1Line());
                if (plan.Count > 0) plan.Add(plan.Count + ". WMI: o repositório do WMI está inconsistente. Depois do reparo do Windows (ou antes, se o erro for no bigupdate/dclvendas.exe), usar o botão \"Reparar WMI (repositório)\" fora do movimento; não precisa reiniciar. Depois rodar de novo bigupdate, acertaestrutura e acertasequenciador.");
            }
            return plan;
        }

        static List<string> BuildCore(ReportData d, List<string> n1)
        {
            var plan = new List<string>();
            var disk = Occ(d, BlockTag.Disk).ToList();
            var recentBad = Hypotheses.RecentBadSectors(d);
            int badSectors = recentBad.Count;
            int ntfs = disk.Count(o => Is(o, "Ntfs", "55") || Is(o, "Ntfs", "98"));
            int retries = disk.Count(o => Is(o, "disk", "153"));
            var oldDisk = Occ(d, BlockTag.DiskOld).ToList();
            int oldBad = recentBad.Count(o => oldDisk.Contains(o));
            int oldNtfs = oldDisk.Count(o => Is(o, "Ntfs", "55") || Is(o, "Ntfs", "98"));
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

            // DISM "origem não encontrada" (0x800f081f e parentes) é falta de ISO/internet, não prova de disco.
            bool repairNotSource = repairFailed && !RepairLogAnalyzer.OnlyNonDisk(repair);
            string recentCause = RecentInstall.Cause(d, dbDamaged);
            bool recentDamaged = recentCause != null;
            int? dbAge = RecentInstall.DbAgeDays(d);
            bool fatalDisk = badSectors > 0 || ntfs > 0 || smartBad || retries >= Config.DiskRetryThreshold ||
                             repairDiskCode || (repairNotSource && disk.Count > 0) || (recentDamaged && disk.Count > 0);
            var clockEv = Occ(d, BlockTag.Clock).ToList();
            bool clockBad = clockEv.Count > 0;
            DateTime? clockLast = clockEv.Where(o => o.Time.HasValue).Select(o => o.Time).OrderByDescending(t => t).FirstOrDefault();
            string clockWhen = clockLast.HasValue ? ", último em " + clockLast.Value.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) : "";
            var cfgWarn = d.Sections.SelectMany(s => s.Blocks).Where(b => b.Tag == BlockTag.Db && b.Verified).SelectMany(b => b.Warnings).ToList();
            bool dwOff = cfgWarn.Any(w => w.StartsWith("innodb_doublewrite=OFF", StringComparison.OrdinalIgnoreCase));
            string flushBad = null;
            foreach (string w in cfgWarn)
                if (w.StartsWith("innodb_flush_log_at_trx_commit=", StringComparison.OrdinalIgnoreCase)) flushBad = w.Substring("innodb_flush_log_at_trx_commit=".Length, 1);
            var risky = new List<string>();
            if (dwOff) risky.Add("innodb_doublewrite=ON (hoje OFF; ou remover a linha)");
            if (flushBad != null) risky.Add("innodb_flush_log_at_trx_commit=1 (hoje " + flushBad + ")");
            var letters = d.Drives.Where(x => x != null && x.Length >= 2 && char.IsLetter(x[0]) && x[1] == ':').Select(x => x.Substring(0, 2).ToUpperInvariant()).Distinct().ToList();
            string chkTargets = letters.Count > 0 ? string.Join(" e ", letters.ToArray()) : "C: (e D: se o banco estiver lá)";
            var hoje = new List<string>();
            if (dwOff) hoje.Add("innodb_doublewrite está OFF");
            if (flushBad != null) hoje.Add("innodb_flush_log_at_trx_commit está " + flushBad);
            string powerLine = null;
            if (risky.Count > 0)
                powerLine = "ATENÇÃO MY.INI" + (unexpected > 0 ? " (" + unexpected + " desligamento(s)/queda(s) de energia no período)" : "") +
                            ": estas variáveis DEVEM ficar protegidas: " + string.Join(" e ", risky.ToArray()) +
                            ". Ajustar e REINICIAR o serviço do MariaDB depois que o reparo for concluído com sucesso, para evitar novas perdas de dados.";
            else if (unexpected > 0)
                powerLine = "ATENÇÃO ENERGIA: " + unexpected + " desligamento(s)/queda(s) de energia no período. Conferir o no-break e, no my.ini, innodb_doublewrite=ON e innodb_flush_log_at_trx_commit=1 (reiniciar o serviço do MariaDB depois de ajustar).";
            string clockLine = clockBad
                ? "ATENÇÃO DATA/HORA: o Windows teve a data/hora alterada em mais de 1 dia " + clockEv.Count + "x" + clockWhen + ": provável bateria da placa-mãe (CR2032) fraca ou data errada na BIOS. Com a data errada o banco não sobe. Orientar o técnico da farmácia a conferir data/hora (Windows e BIOS) e, se voltar a errar, a trocar a bateria, ANTES de reparar o banco."
                : "Orientar o técnico da farmácia a conferir também a data/hora do Windows e a bateria da placa-mãe (CR2032): com a data errada o banco não sobe.";
            var lowDrives = Hypotheses.LowSpaceDrives(d);
            bool lowSpace = lowDrives.Count > 0;
            string lowText = string.Join(" e ", lowDrives.ToArray());
            bool noOther = !(fatalDisk || repairFailed || dbDamaged || unexpected > 0 || ramSuspect || driverBsod || disk.Count > 0);
            bool onlyN1 = (clockBad || lowSpace) && noOther;
            bool onlyPower = unexpected > 0 && !(fatalDisk || repairFailed || dbDamaged || ramSuspect || driverBsod || disk.Count > 0);
            bool anyProblem = fatalDisk || repairFailed || dbDamaged || unexpected > 0 || ramSuspect || driverBsod || disk.Count > 0 || clockBad || lowSpace;
            d.N1Kind = N1Case.Nada;
            d.N1Dest = null;
            if (!anyProblem)
            {
                if (n1 != null) n1.Add("Nenhum indício forte nos logs lidos. Sem encaminhamento para o N2 por este relatório.");
                return plan;
            }
            string after = Config.N2AfterHoursNote;

            if (n1 != null)
            {
                DiagItem tI = d.Diagnosis.FirstOrDefault(i => i.Area.StartsWith("TABELA")), sI = d.Diagnosis.FirstOrDefault(i => i.Area.StartsWith("ARQUIVOS")),
                         mI = d.Diagnosis.FirstOrDefault(i => i.Area.StartsWith("MOTOR"));
                bool tabOnly = tI != null && tI.Verdict == DiagVerdict.Provavel &&
                               (sI == null || sI.Verdict != DiagVerdict.Provavel) && (mI == null || mI.Verdict != DiagVerdict.Provavel);
                var why = new List<string>();
                if (badSectors > 0) why.Add("setor defeituoso no disco (" + Hypotheses.BadSectorWhere(recentBad, d.SingleDiskModel) + ")");
                if (ntfs > 0) why.Add("NTFS corrompido");
                if (smartBad) why.Add("SMART em atenção");
                if (retries >= Config.DiskRetryThreshold) why.Add("disco com E/S repetida");
                if (repairDiskCode) why.Add("reparo do Windows falhou por disco");
                if (recentDamaged && disk.Count > 0) why.Add("banco recém-instalado já corrompendo, com eventos de disco");
                if (fatalDisk)
                {
                    d.N1Kind = N1Case.TrocarDisco; d.N1Dest = "EQUIPE TÉCNICA";
                    n1.Add("AÇÃO: Acione a equipe técnica para TROCAR O SERVIDOR/DISCO e reinstalar. Não reinstalar neste disco.");
                    n1.Add("Motivo: " + (why.Count > 0 ? string.Join("; ", why.ToArray()) : "erros de disco repetidos") + ".");
                }
                else if (dbDamaged && tabOnly && !ramSuspect)
                {
                    d.N1Kind = N1Case.Tabelas; d.N1Dest = "N2 SISTEMA";
                    var tabs = Diagnosis.DamagedTables(dbAll);
                    n1.Add("AÇÃO: Acione o N2 para tratar as tabelas:");
                    foreach (string t in tabs.Take(15)) n1.Add("- " + t);
                    if (tabs.Count > 15) n1.Add("... e mais " + (tabs.Count - 15));
                    n1.Add("Em caso de não sucesso, acionar equipe técnica.");
                    n1.Add("Motivo: dano só em tabela(s) e nenhum erro de disco/RAM nos logs.");
                    if (after.Length > 0) n1.Add(after);
                    if (powerLine != null) n1.Add(powerLine);
                    if (clockBad) n1.Add(clockLine);
                }
                else if (dbDamaged)
                {
                    d.N1Kind = N1Case.Reinstalar; d.N1Dest = "EQUIPE TÉCNICA";
                    n1.Add("AÇÃO: Acione a equipe técnica para REINSTALAR NO MESMO TERMINAL.");
                    n1.Add("Motivo: dano além de tabelas (arquivos de sistema do banco, banco que não sobe ou erro de memória do banco), sem defeito de disco nos logs." +
                           (ramSuspect ? " HÁ SINAL DE RAM: testar a RAM antes." : ""));
                    if (powerLine != null) n1.Add(powerLine);
                    n1.Add(clockLine);
                }
                else if (onlyN1)
                {
                    d.N1Kind = N1Case.N1Sozinho; d.N1Dest = "N1 (RESOLVE SOZINHO)";
                    if (lowSpace)
                    {
                        n1.Add("AÇÃO: O N1 resolve: liberar espaço em " + lowText + " (esvaziar lixeira e pasta Temp, apagar dumps e backups antigos, Limpeza de Disco). Não é caso de N2 nem de equipe técnica.");
                        n1.Add("Motivo: pouco espaço livre em " + lowText + "; sem espaço o banco para de gravar e pode corromper. Sem dano de banco nem de disco nos logs.");
                    }
                    if (clockBad)
                    {
                        n1.Add((lowSpace ? "TAMBÉM: " : "AÇÃO: ") + "O N1 resolve: acertar a data/hora do Windows (e conferir a da BIOS). Se voltar a errar, chamar o técnico da farmácia para trocar a bateria da placa-mãe (CR2032).");
                        n1.Add("Motivo: data/hora do Windows alterada em mais de 1 dia " + clockEv.Count + "x" + clockWhen + "; com a data errada o banco não sobe. Sem dano de banco nem de disco nos logs.");
                    }
                    if (powerLine != null) n1.Add(powerLine);
                }
                else if (onlyPower)
                {
                    d.N1Kind = N1Case.Energia; d.N1Dest = "TÉCNICO DA FARMÁCIA";
                    n1.Add("AÇÃO: Acione o técnico da farmácia para verificar a energia: no-break (nobreak), fonte, tomada e a bateria da placa-mãe (CR2032).");
                    n1.Add("Motivo: " + unexpected + " desligamento(s) inesperado(s) sem tela azul de driver nem erro de disco/banco nos logs.");
                    if (powerLine != null) n1.Add(powerLine);
                    n1.Add(clockLine);
                }
                else
                {
                    d.N1Kind = N1Case.ReparoWindows; d.N1Dest = "N2 SISTEMA";
                    n1.Add("AÇÃO: Acione o N2 para reparo simples do Windows (DISM e SFC). Sem dano de banco nem de disco grave nos logs.");
                    var mot = new List<string>();
                    if (unexpected > 0) mot.Add(unexpected + " desligamento(s) inesperado(s)");
                    if (ramSuspect) mot.Add("sinal de RAM");
                    if (driverBsod) mot.Add("tela azul de driver");
                    if (mot.Count == 0) mot.Add("eventos de disco/Windows");
                    n1.Add("Motivo: " + string.Join("; ", mot.ToArray()) + ".");
                    if (after.Length > 0) n1.Add(after);
                    if (powerLine != null) n1.Add(powerLine);
                    n1.Add(clockLine);
                }
            }

            // Evidência de disco em texto.
            var ev = new List<string>();
            if (badSectors > 0) ev.Add(badSectors + "x setor defeituoso (bad block)" + (oldBad > 0 ? ", " + oldBad + " antes do período (bad block não some sozinho)" : ""));
            if (oldNtfs > 0) ev.Add(oldNtfs + "x NTFS corrompido ANTES do período (Ntfs 55/98; confirmar com chkdsk /scan se ainda há erro)");
            if (ntfs > 0) ev.Add(ntfs + "x NTFS corrompido (Ntfs 55/98)");
            if (smartBad) ev.Add("SMART em atenção");
            if (retries >= Config.DiskRetryThreshold) ev.Add(retries + "x disk 153 (E/S repetida)");
            if (recentDamaged && disk.Count > 0) ev.Add("banco instalado " + (dbAge.Value == 0 ? "hoje" : "há " + dbAge.Value + " dia(s)") + " já corrompendo, junto com " + disk.Count + " evento(s) de disco");
            if (repairNotSource) ev.Add("DISM/SFC já falharam aqui (" + string.Join(", ", repairCodes.Select(c => c.Replace(RepairLogAnalyzer.DismKey + " ", "")).ToArray()) + ")");

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

            if (onlyN1 || onlyPower)
            {
                plan.Add(onlyN1
                    ? "VEREDITO: sem dano de banco nem defeito de disco nos logs; é ajuste simples, que o N1 resolve."
                    : "VEREDITO: sem dano de banco nem defeito de disco nos logs; o que aparece é queda de energia: caso do técnico da farmácia.");
                if (lowSpace)
                    plan.Add(step("Liberar espaço em " + lowText + ": esvaziar a lixeira e a pasta Temp, apagar dumps e backups antigos de dentro do servidor (levar para outro disco), Limpeza de Disco. Manter pelo menos 15% livre."));
                if (clockBad)
                    plan.Add(step("Data/hora: o Windows teve a data alterada em mais de 1 dia " + clockEv.Count + "x" + clockWhen + ". Acertar data/hora (Windows e BIOS); se voltar a errar, chamar o técnico da farmácia para trocar a bateria da placa-mãe (CR2032) antes de subir o banco; com a data errada ele não sobe."));
                if (unexpected > 0)
                    plan.Add(step("Energia: " + unexpected + " desligamento(s) inesperado(s). Técnico da farmácia: conferir no-break (bateria boa), fonte, tomada/aterramento e a bateria da placa-mãe (CR2032); queda de energia corrompe banco."));
                plan.Add(step("my.ini: innodb_doublewrite DEVE ficar ON (padrão; tirar a linha OFF) e innodb_flush_log_at_trx_commit=1" + (hoje.Count > 0 ? ". Situação hoje: " + string.Join("; ", hoje.ToArray()) : "") +
                              ". Ajustar e REINICIAR o serviço do MariaDB. Ver os AVISOS da seção do banco."));
                return plan;
            }

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
                DiagItem tabItem0 = d.Diagnosis.FirstOrDefault(i => i.Area.StartsWith("TABELA")), sysItem0 = d.Diagnosis.FirstOrDefault(i => i.Area.StartsWith("ARQUIVOS")),
                         motItem0 = d.Diagnosis.FirstOrDefault(i => i.Area.StartsWith("MOTOR"));
                bool tablesRoute = dbDamaged && !ramSuspect && tabItem0 != null && tabItem0.Verdict == DiagVerdict.Provavel &&
                                   (sysItem0 == null || sysItem0.Verdict != DiagVerdict.Provavel) && (motItem0 == null || motItem0.Verdict != DiagVerdict.Provavel);
                if (tablesRoute)
                {
                    plan.Add("VEREDITO: dano só em tabela(s) e sem defeito de disco nem de RAM nos logs. Caso do N2: reparar com force 1 ou 2 na porta 3307, sem dump e sem reinstalar (se algo fugir disso, EQUIPE TÉCNICA).");
                    foreach (string t in TableRoute(Diagnosis.DamagedTables(dbAll))) plan.Add(step(t));
                    if (clockBad)
                        plan.Add(step("Data/hora: o Windows teve a data alterada em mais de 1 dia " + clockEv.Count + "x" + clockWhen + ". Acertar data/hora (Windows e BIOS) e, se voltar a errar, trocar a bateria da placa-mãe (CR2032); com a data errada o banco não sobe."));
                    if (unexpected > 0)
                        plan.Add(step("Energia: " + unexpected + " desligamento(s) inesperado(s). Usar no-break (nobreak) com bateria boa no servidor; queda de energia corrompe banco."));
                    return plan;
                }
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
                plan.Add(step("chkdsk /scan em " + chkTargets + "; se achar erro, chkdsk /f na janela de manutenção. Se o chkdsk achar setor ruim ou voltar com erro, mudar para a rota de TROCAR O DISCO."));
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

            if (clockBad && !fatalDisk)
                plan.Add(step("Data/hora: o Windows teve a data alterada em mais de 1 dia " + clockEv.Count + "x" + clockWhen + ". O técnico da farmácia deve acertar data/hora (Windows e BIOS) e, se voltar a errar, trocar a bateria da placa-mãe (CR2032) antes de subir o banco; com a data errada ele não sobe."));
            if (unexpected > 0)
                plan.Add(step("Energia: " + unexpected + " desligamento(s) inesperado(s). Usar no-break (nobreak) com bateria boa no servidor; queda de energia corrompe banco."));
            plan.Add(step("my.ini: innodb_doublewrite DEVE ficar ON (padrão; tirar a linha OFF) e innodb_flush_log_at_trx_commit=1" + (hoje.Count > 0 ? ". Situação hoje: " + string.Join("; ", hoje.ToArray()) : "") +
                          "; tirar innodb_force_recovery depois da manutenção. Ajustar e REINICIAR o serviço do MariaDB depois que o reparo for concluído com sucesso, para evitar novas perdas de dados. Ver os AVISOS da seção do banco."));
            return plan;
        }
    }
}
