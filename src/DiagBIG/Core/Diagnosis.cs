using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DiagBIG.Core
{
    public enum DiagVerdict { SemIndicio, Possivel, Provavel }

    /// <summary>Uma área onde o problema pode estar (tabela, arquivos do banco, disco, RAM...), com o que os logs mostram e o encaminhamento.</summary>
    public class DiagItem
    {
        public string Area { get; set; }
        public DiagVerdict Verdict { get; set; }
        public string Evidence { get; set; }
        public string Action { get; set; }
    }

    /// <summary>
    /// Separa "onde está o problema": só tabela(s), arquivos de sistema do banco (ibdata/redo/dicionário), erro de memória do próprio banco,
    /// disco cheio/E-S, hardware (RAM/disco/energia), Windows em avaliação, serviço/instalação/configuração.
    /// Só enxerga o que está nos logs: serve para direcionar o suporte, não para provar.
    /// </summary>
    public static class Diagnosis
    {
        static readonly string[] SystemFileTerms =
        {
            "ib_logfile", "redo log", "is in the future", "doesn't exist in engine", "Could not open or create the system tablespace",
            "Unsupported redo log", "not found in the InnoDB data dictionary", "Cannot find or open table", "ibdata1",
            "innodb_table_stats", "innodb_index_stats", "Cannot open datafile for read-write", "Table 'mysql.",
        };
        // Linhas normais de início/ajuste de arquivo que citam esses nomes sem ser erro.
        static readonly string[] SystemFileNoise = { "Setting file", "Resizing redo log", "Upgrading redo log", "Starting to delete and rewrite log files", "Renaming log file" };

        static readonly string[] DiskFullTerms = { "Disk full", "No space left", "errno: 28", "Operating system error number 112", "not enough space on the disk" };
        static readonly string[] IoErrorTerms =
        {
            "Operating system error number 23", "Operating system error number 21", "Operating system error number 1117", "Operating system error number 121",
            "Operating system error number 483", "I/O error", "Input/output error", "Operating system error number 1450", "Operating system error number 1006",
        };
        static readonly string[] FileLockTerms =
        {
            "Operating system error number 5 ", "Operating system error number 32", "Operating system error number 33",
            "Access is denied", "being used by another process", "Unable to lock",
        };
        static readonly string[] StartTerms =
        {
            "Address already in use", "Can't start server", "Bind on TCP/IP port", "Can't open shared library", "Unknown/unsupported storage engine",
            "Can't create/write to file", "Fatal error: Can't open and lock privilege tables",
        };
        static readonly string[] TableCorruptTerms = { "marked as crashed", "Incorrect key file", "Database page corruption", "flag mismatch", "wrong order", "wrong owned count", "Summed data size" };
        static readonly string[] CrashTerms = { "got exception", "Assertion failure", "got signal" };
        static readonly string[] MemoryTerms = { "MAGIC_N", "Out of memory", "got signal 11" };

        static readonly Regex CrashedTableRx = new Regex(@"[Tt]able:?\s+'(?:[^']*[\\/])?([^\\/'.]+)[\\/]([^\\/']+?)(?:\.MY[ID])?'", RegexOptions.Compiled);
        static readonly Regex SpaceZeroRx = new Regex(@"space=0\b", RegexOptions.Compiled);
        static readonly Regex SysTablespaceRx = new Regex(@"tablespace\s+(?:mysql/|ibdata1|innodb_system)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static bool Has(Occurrence o, string[] terms)
        {
            string t = (o.Message ?? "") + "\n" + (o.SummaryKey ?? "");
            return terms.Any(x => t.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static bool IsSystemFile(Occurrence o)
        {
            if (!Has(o, SystemFileTerms) && !SpaceZeroRx.IsMatch(o.Message ?? "") && !SysTablespaceRx.IsMatch(o.Message ?? "")) return false;
            string first = (o.Message ?? "").Split('\n')[0];
            return !SystemFileNoise.Any(n => first.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) ||
                   first.IndexOf("[ERROR]", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Tabelas (banco.tabela) marcadas como quebradas ou com página corrompida, de InnoDB e MyISAM.</summary>
        public static List<string> DamagedTables(IEnumerable<Occurrence> lines)
        {
            var set = new SortedSet<string>(Hypotheses.CorruptTables(lines), StringComparer.OrdinalIgnoreCase);
            foreach (Occurrence o in lines)
            {
                if (!Has(o, TableCorruptTerms) && (o.Message ?? "").IndexOf("Got error 1", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (Match m in CrashedTableRx.Matches((o.Message ?? "").Split('\n')[0]))
                    set.Add(m.Groups[1].Value + "." + m.Groups[2].Value);
            }
            return set.ToList();
        }

        static IEnumerable<ReportBlock> Blocks(ReportData d, string tag)
        {
            return d.Sections.SelectMany(s => s.Blocks).Where(b => b.Tag == tag && b.Verified);
        }

        static List<Occurrence> Occ(ReportData d, string tag)
        {
            return Blocks(d, tag).SelectMany(b => b.Occurrences).ToList();
        }

        /// <summary>Uma tabela por linha ("\n" vira quebra de linha no relatório).</summary>
        public static string TableLines(IEnumerable<string> items, int max, string indent)
        {
            var l = items.ToList();
            string r = string.Concat(l.Take(max).Select(t => "\n" + indent + "- " + t).ToArray());
            return r + (l.Count > max ? "\n" + indent + "... e mais " + (l.Count - max) : "");
        }

        static string Join(IEnumerable<string> items, int max)
        {
            var l = items.ToList();
            return string.Join(", ", l.Take(max).ToArray()) + (l.Count > max ? " e mais " + (l.Count - max) : "");
        }

        public static List<DiagItem> Analyze(ReportData d)
        {
            var items = new List<DiagItem>();
            var db = Occ(d, BlockTag.Db);
            var dbEv = Occ(d, BlockTag.DbEvents);
            var all = db.Concat(dbEv).ToList();
            bool anyDbData = all.Count > 0;

            // ---------- evidências de fora do banco ----------
            var power = Occ(d, BlockTag.Power);
            var hw = Occ(d, BlockTag.Hardware);
            var disk = Occ(d, BlockTag.Disk);
            int bsodRam = 0;
            foreach (Occurrence o in power)
            {
                long? c = Bugchecks.FromOccurrence(o);
                BugcheckInfo i = c == null ? null : Bugchecks.Lookup(c.Value);
                if (i != null && i.Suspect == BugcheckSuspect.Ram) bsodRam++;
            }
            int memTest = hw.Count(o => o.EventId == Config.MemDiagErrorId.ToString());
            int whea = hw.Count(o => Grouping.ShortSource(o.Source).StartsWith("WHEA", StringComparison.OrdinalIgnoreCase));
            int badSectors = Hypotheses.RecentBadSectors(d).Count;
            int ntfs55 = disk.Count(o => string.Equals(Grouping.ShortSource(o.Source), "Ntfs", StringComparison.OrdinalIgnoreCase) && o.EventId == "55");
            bool smartBad = d.Summary.Any(s => s.Label == "SMART" && s.Status == CheckStatus.Atencao);
            int powerLoss = power.Count(o => PowerEvents.Classify(o.Source, o.EventId) == PowerKind.Unexpected && (o.Message ?? "").Contains("QUEDA/CORTE DE ENERGIA"));
            int unexpected = PowerEvents.UnexpectedIncidents(power);

            // ---------- evidências do banco ----------
            List<string> tables = DamagedTables(all);
            int startupFails = Hypotheses.StartupFailures(db, dbEv);
            // Os eventos do Windows repetem as mesmas linhas do .err: contar pelo .err quando ele tem a evidência, senão pelos eventos.
            var sysFile = db.Where(IsSystemFile).ToList();
            if (sysFile.Count == 0) sysFile = dbEv.Where(IsSystemFile).ToList();
            bool memoryErr = db.Any(o => Has(o, MemoryTerms));
            int crashes = db.Count(o => Has(o, CrashTerms));
            int tableCorrupt = db.Count(o => Has(o, TableCorruptTerms));
            if (tableCorrupt == 0) tableCorrupt = dbEv.Count(o => Has(o, TableCorruptTerms));
            var diskFull = all.Where(o => Has(o, DiskFullTerms)).ToList();
            var ioErr = all.Where(o => Has(o, IoErrorTerms)).ToList();
            var locks = all.Where(o => Has(o, FileLockTerms)).ToList();
            var startErr = all.Where(o => Has(o, StartTerms)).ToList();
            var scm = dbEv.Where(o => (Grouping.ShortSource(o.Source) ?? "").IndexOf("Service Control", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var cfgWarnings = Blocks(d, BlockTag.Db).SelectMany(b => b.Warnings).ToList();

            bool hwEvidence = bsodRam > 0 || memTest > 0 || whea > 0 || badSectors > 0 || ntfs55 > 0 || smartBad || disk.Count > 0 || powerLoss > 0;
            bool dbDamage = tableCorrupt > 0 || crashes > 0 || startupFails > 0 || sysFile.Count > 0 || memoryErr;

            // 1) só tabela(s)
            {
                var it = new DiagItem { Area = "TABELA(S) ESPECÍFICA(S)" };
                bool tablesOnly = tables.Count > 0 && startupFails == 0 && sysFile.Count == 0 && !memoryErr;
                if (tablesOnly)
                {
                    it.Verdict = DiagVerdict.Provavel;
                    it.Evidence = tables.Count + " tabela(s) com erro e nada indicando arquivos de sistema do banco:" + TableLines(tables, 15, "        ");
                    it.Action = "O problema parece restrito às tabelas citadas; NÃO precisa reinstalar nem trocar o servidor por isso. MyISAM: REPAIR TABLE (ou myisamchk). " +
                                "InnoDB: subir com innodb_force_recovery=1 ou 2, somente, na porta 3307 de manutenção, e recriar a tabela ou restaurá-la do backup. Se não resolver, equipe técnica.";
                    if (hwEvidence) it.Action += " Mas há indício de hardware abaixo: tratar a causa, senão outras tabelas corrompem.";
                }
                else if (tables.Count > 0)
                {
                    it.Verdict = DiagVerdict.Possivel;
                    it.Evidence = tables.Count + " tabela(s) citada(s) com erro, mas há também sinais mais amplos (veja as linhas abaixo):" + TableLines(tables, 15, "        ");
                    it.Action = "Não tratar só as tabelas: o dano é maior que elas.";
                }
                else if (tableCorrupt > 0)
                {
                    it.Verdict = DiagVerdict.Possivel;
                    it.Evidence = tableCorrupt + " linha(s) de corrupção de tabela, sem o nome da tabela no trecho lido.";
                    it.Action = "Rodar CHECK TABLE nas tabelas do banco para achar quais estão quebradas.";
                }
                else { it.Verdict = DiagVerdict.SemIndicio; it.Evidence = "Nenhuma tabela citada com erro."; }
                items.Add(it);
            }

            // 2) arquivos de sistema do banco
            {
                var it = new DiagItem { Area = "ARQUIVOS DO BANCO (ibdata1, redo log, dicionário)" };
                if (sysFile.Count > 0)
                {
                    it.Verdict = DiagVerdict.Provavel;
                    it.Evidence = sysFile.Count + " linha(s) apontando para o tablespace de sistema, redo log ou dicionário, ex.: " + Short(sysFile[0]);
                    it.Action = "Não é tabela: não adianta REPAIR/OPTIMIZE. Tentar salvar o que der em outro disco subindo com innodb_force_recovery=1 ou 2, somente, na porta 3307 de manutenção. " +
                                "Reinstalar e restaurar é com a equipe técnica.";
                }
                else if (startupFails > 0)
                {
                    it.Verdict = DiagVerdict.Possivel;
                    it.Evidence = (d.DbServiceRunning == true ? "O banco falhou ao subir (" + startupFails + " tentativa(s)) e agora está rodando " : "O banco não sobe (" + startupFails + " tentativa(s)) ") + (tables.Count > 0 ? "e há tabela corrompida citada: pode ser só ela." : "sem tabela citada: o dano pode estar nos arquivos de sistema do InnoDB.");
                    it.Action = "Subir com innodb_force_recovery=1 ou 2, somente, na porta 3307 de manutenção; se subir assim, o dano é recuperável. Se não subir, acionar a equipe técnica.";
                }
                else { it.Verdict = DiagVerdict.SemIndicio; it.Evidence = "Nada apontando para ibdata1/redo log/dicionário."; }
                items.Add(it);
            }

            // 3) erro de memória do próprio banco / bug
            {
                var it = new DiagItem { Area = "MOTOR DO BANCO (bug ou erro de memória do MariaDB/MySQL)" };
                if (memoryErr)
                {
                    it.Verdict = DiagVerdict.Provavel;
                    it.Evidence = "Erro de memória/estrutura interna do banco (MAGIC_N, out of memory ou signal 11).";
                    it.Action = "REPAIR/OPTIMIZE não resolvem. Se persistir com innodb_force_recovery=2, reinstalar o MariaDB (último 10.4.x) com dump e restore. Se houver RAM suspeita abaixo, testar a RAM antes.";
                }
                else if (crashes > 0 && tableCorrupt == 0 && startupFails == 0 && sysFile.Count == 0)
                {
                    it.Verdict = hwEvidence ? DiagVerdict.Possivel : DiagVerdict.Provavel;
                    it.Evidence = crashes + " queda(s)/assertion do banco sem corrupção de tabela nos logs.";
                    it.Action = hwEvidence ? "Pode ser consequência do hardware abaixo; resolver o hardware primeiro."
                                           : "Sem hardware suspeito: provável bug da versão ou do my.ini. Atualizar para a última versão estável da mesma linha e revisar o my.ini (avisos abaixo).";
                }
                else { it.Verdict = DiagVerdict.SemIndicio; it.Evidence = "Sem assertion ou queda isolada do motor."; }
                items.Add(it);
            }

            // 4) disco cheio / E-S / arquivo bloqueado
            {
                var it = new DiagItem { Area = "ESPAÇO, E/S E ACESSO AOS ARQUIVOS" };
                var parts = new List<string>(); var acts = new List<string>();
                var lowDrives = Hypotheses.LowSpaceDrives(d);
                bool lowSpace = lowDrives.Count > 0;
                if (diskFull.Count > 0 || lowSpace) { parts.Add("disco cheio/pouco espaço" + (lowSpace ? " em " + string.Join(", ", lowDrives.ToArray()) : "") + " (" + diskFull.Count + " erro(s) do banco)"); acts.Add("liberar espaço no disco do banco; disco cheio quebra tabelas no meio da gravação (veja se as tabelas dos erros foram gravadas nessa hora)"); }
                if (ioErr.Count > 0) { parts.Add(ioErr.Count + " erro(s) de leitura/gravação no arquivo (E/S)"); acts.Add("erro de E/S do sistema operacional é disco/controladora/rede: tratar como problema de hardware"); }
                if (locks.Count > 0) { parts.Add(locks.Count + " erro(s) de acesso negado/arquivo em uso"); acts.Add("antivírus, backup ou outro processo mexendo na pasta data; excluir a pasta data do antivírus e do backup de arquivos abertos"); }
                if (parts.Count > 0) { it.Verdict = DiagVerdict.Provavel; it.Evidence = string.Join("; ", parts.ToArray()) + "."; it.Action = Sentences(acts); }
                else { it.Verdict = DiagVerdict.SemIndicio; it.Evidence = "Sem disco cheio, erro de E/S ou arquivo bloqueado nos logs do banco."; }
                items.Add(it);
            }

            // 5) hardware
            {
                var it = new DiagItem { Area = "HARDWARE (RAM, DISCO, ENERGIA)" };
                var parts = new List<string>(); var acts = new List<string>();
                var ramParts = new List<string>(); var ramActs = new List<string>();
                var diskParts = new List<string>(); var diskActs = new List<string>();
                if (bsodRam > 0 || memTest > 0 || whea > 0)
                {
                    var r = new List<string>();
                    if (bsodRam > 0) r.Add(bsodRam + " tela(s) azul(is) de memória");
                    if (whea > 0) r.Add(whea + " erro(s) WHEA");
                    if (memTest > 0) r.Add("teste de memória do Windows falhou");
                    ramParts.Add("RAM/plataforma (" + string.Join("; ", r.ToArray()) + ")");
                    ramActs.Add("testar a RAM (MemTest86, pente a pente) antes de restaurar o banco; se confirmar, trocar o pente");
                }
                if (badSectors > 0 || smartBad)
                {
                    var r = new List<string>();
                    if (badSectors > 0) r.Add(badSectors + " bad block");
                    if (smartBad) r.Add("SMART em atenção");
                    diskParts.Add("disco com defeito (" + string.Join("; ", r.ToArray()) + ")");
                    diskActs.Add("backup agora e trocar o disco/servidor");
                }
                else if (disk.Count > 0 || ntfs55 > 0) { diskParts.Add(disk.Count + " evento(s) de disco/NTFS" + (ntfs55 > 0 ? " (inclui NTFS 55)" : "")); diskActs.Add("backup, chkdsk /scan e ferramenta do fabricante; se voltar, suspeitar de disco, cabo ou controladora"); }
                // Disco com evidência forte vem antes da RAM, salvo prova direta de RAM (WHEA ou teste do Windows falhou).
                bool diskFirst = (badSectors > 0 || ntfs55 > 0 || disk.Count >= Config.DiskRetryThreshold) && memTest == 0 && whea == 0;
                if (diskFirst) { parts.AddRange(diskParts); acts.AddRange(diskActs); parts.AddRange(ramParts); acts.AddRange(ramActs); }
                else { parts.AddRange(ramParts); acts.AddRange(ramActs); parts.AddRange(diskParts); acts.AddRange(diskActs); }
                if (powerLoss > 0) { parts.Add(powerLoss + " queda(s) de energia"); acts.Add("verificar energia/no-break/fonte"); }
                int? dbAge = RecentInstall.DbAgeDays(d);
                if (dbAge.HasValue && dbDamage) { parts.Add("banco instalado " + (dbAge.Value == 0 ? "hoje" : "há " + dbAge.Value + " dia(s)") + " e já corrompendo (datadir novo: não é o dump antigo)"); acts.Add("testar a RAM e trocar o disco ou restaurar em outro disco/servidor"); }
                if (parts.Count > 0)
                {
                    it.Verdict = (bsodRam > 0 || memTest > 0 || badSectors > 0 || smartBad || (dbAge.HasValue && dbDamage && (disk.Count > 0 || ntfs55 > 0))) ? DiagVerdict.Provavel : DiagVerdict.Possivel;
                    it.Evidence = string.Join("; ", parts.ToArray()) + ".";
                    it.Action = Sentences(acts) + " Hardware com defeito corrompe o banco de novo mesmo depois de reinstalar ou restaurar.";
                }
                else { it.Verdict = DiagVerdict.SemIndicio; it.Evidence = "Nenhum erro de RAM, disco ou energia nos logs do Windows lidos (" + unexpected + " desligamento(s) inesperado(s))."; it.Action = "Isso não descarta hardware: o log só mostra o que o Windows registrou."; }
                items.Add(it);
            }

            // 6) Windows
            {
                var it = new DiagItem { Area = "WINDOWS (licença de avaliação)" };
                if (OsRules.IsEvaluation(d.Os) && OsRules.IsServer(d.Os))
                {
                    bool desl = unexpected > 0;
                    it.Verdict = desl ? DiagVerdict.Provavel : DiagVerdict.Possivel;
                    it.Evidence = "Windows Server em edição de AVALIAÇÃO" + (desl ? " com " + unexpected + " desligamento(s) inesperado(s)." : ".");
                    it.Action = "Conferir com \"slmgr /xpr\". Avaliação vencida desliga o servidor de hora em hora e derruba o banco: ativar com licença definitiva. Não é problema do banco.";
                }
                else { it.Verdict = DiagVerdict.SemIndicio; it.Evidence = "Windows não está em edição de avaliação."; }
                items.Add(it);
            }

            // 7) serviço, instalação, configuração
            {
                var it = new DiagItem { Area = "SERVIÇO, INSTALAÇÃO E CONFIGURAÇÃO (my.ini)" };
                var parts = new List<string>(); var acts = new List<string>();
                if (startErr.Count > 0) { parts.Add(startErr.Count + " erro(s) de instalação/inicialização (porta em uso, biblioteca ou engine ausente, permissão)"); acts.Add("conferir porta ocupada por outra instância, arquivos da instalação e permissão da pasta data; se a instalação estiver incompleta, reinstalar"); }
                if (scm.Count > 0 && startupFails == 0) { parts.Add(scm.Count + " falha(s) do serviço no Windows (Service Control Manager)"); acts.Add("ver a mensagem do serviço no bloco de eventos do banco e o .err"); }
                if (cfgWarnings.Count > 0) { parts.Add(cfgWarnings.Count + " aviso(s) de configuração do my.ini"); acts.Add("corrigir o my.ini conforme os avisos (não corrompem sozinhos, mas aumentam o dano numa queda)"); }
                if (parts.Count > 0) { it.Verdict = startErr.Count > 0 ? DiagVerdict.Provavel : DiagVerdict.Possivel; it.Evidence = string.Join("; ", parts.ToArray()) + "."; it.Action = Sentences(acts); }
                else { it.Verdict = DiagVerdict.SemIndicio; it.Evidence = "Sem erro de inicialização do serviço nem aviso de my.ini."; }
                items.Add(it);
            }

            if (!anyDbData && !items.Any(i => i.Verdict != DiagVerdict.SemIndicio)) items.Clear();
            return items;
        }

        /// <summary>Junta as ações em frases: primeira letra maiúscula e ponto no fim.</summary>
        static string Sentences(IEnumerable<string> acts)
        {
            return string.Join(" ", acts.Select(a => char.ToUpperInvariant(a[0]) + a.Substring(1).TrimEnd('.') + ".").ToArray());
        }

        static string Short(Occurrence o)
        {
            string s = (o.Message ?? "").Split('\n')[0].Trim();
            return s.Length > 140 ? s.Substring(0, 137) + "..." : s;
        }

        /// <summary>Resumo de uma linha com o encaminhamento mais provável (ou null se nada aparece).</summary>
        public static string Bottom(List<DiagItem> items)
        {
            if (items == null || items.Count == 0) return null;
            Func<string, DiagItem> by = a => items.FirstOrDefault(i => i.Area.StartsWith(a));
            DiagItem hw = by("HARDWARE"), win = by("WINDOWS"), tab = by("TABELA"), sys = by("ARQUIVOS"), mot = by("MOTOR"), esp = by("ESPAÇO");
            var parts = new List<string>();
            if (hw != null && hw.Verdict == DiagVerdict.Provavel) parts.Add("HARDWARE com defeito");
            if (win != null && win.Verdict != DiagVerdict.SemIndicio) parts.Add("licença de avaliação do Windows (não é o banco)");
            if (sys != null && sys.Verdict == DiagVerdict.Provavel) parts.Add("arquivos de sistema do banco danificados" + (hw != null && hw.Verdict == DiagVerdict.Provavel ? "" : ": acionar a equipe técnica (reinstalar)"));
            else if (mot != null && mot.Verdict == DiagVerdict.Provavel) parts.Add("motor do banco (bug/memória): acionar a equipe técnica (reinstalar)");
            if (tab != null && tab.Verdict == DiagVerdict.Provavel && (sys == null || sys.Verdict != DiagVerdict.Provavel)) parts.Add("só tabela(s): recuperar/recriar as tabelas, sem reinstalar" + (hw == null || hw.Verdict == DiagVerdict.SemIndicio ? " (N2 pode tentar: force_recovery até 2 + reparar/recriar a tabela + DISM/SFC, desde que sem erro de disco/RAM; senão equipe técnica)" : ""));
            if (esp != null && esp.Verdict == DiagVerdict.Provavel) parts.Add("espaço/E-S/arquivo bloqueado");
            return parts.Count == 0 ? "Os logs lidos não separam a causa; veja as linhas abaixo e as CAUSAS PROVÁVEIS." : string.Join(" + ", parts.ToArray()) + "." + (hw != null && hw.Verdict == DiagVerdict.Provavel ? " Tentar salvar o que tem no disco em outro disco ou acionar a equipe técnica." : "");
        }
    }
}
