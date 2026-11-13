using System;
using System.Collections.Generic;
using System.Linq;

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

        static IEnumerable<ReportBlock> Blocks(ReportData d, string tag)
        {
            return d.Sections.SelectMany(s => s.Blocks).Where(b => b.Tag == tag && b.Verified);
        }

        static IEnumerable<Occurrence> Occ(ReportData d, string tag)
        {
            return Blocks(d, tag).SelectMany(b => b.Occurrences);
        }

        /// <summary>Queda de energia, tela azul, hardware e disco achados no relatório (para reforçar os avisos do my.ini).</summary>
        public static List<string> RiskReasons(ReportData d)
        {
            var r = new List<string>();
            int unexpected = Occ(d, BlockTag.Power).Count(o => PowerEvents.IsUnexpected(PowerEvents.Classify(o.Source, o.EventId)));
            if (unexpected > 0) r.Add(unexpected + " desligamento(s) inesperado(s)/tela azul");
            if (Occ(d, BlockTag.Hardware).Any()) r.Add("erros de hardware/memória");
            if (Occ(d, BlockTag.Disk).Any()) r.Add("eventos de disco");
            return r;
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
            bool lowSpace = d.Drives.Any(x => x.Contains("POUCO ESPAÇO"));

            bool ramEvidence = bsodRam > 0 || memTestFailed > 0 || whea > 0;

            // 1) RAM
            if (ramEvidence && dbDamaged)
            {
                var ev = RamEvidence(infos, bsodRam, bsodRamDriver, memTestFailed, whea);
                result.Add("Possível causa: RAM defeituosa (" + ev + "). Não está confirmada: a queda/corrupção do banco também pode vir de bug do MariaDB, disco, energia ou do my.ini. " +
                           "Testar a memória (MemTest86, várias passadas, ou mdsched.exe) e testar pente a pente ANTES de restaurar o banco; " +
                           "senão a base pode corromper de novo.");
            }
            else if (ramEvidence)
            {
                result.Add("Indícios de RAM com defeito (" + RamEvidence(infos, bsodRam, bsodRamDriver, memTestFailed, whea) +
                           "). Testar a memória (MemTest86 ou mdsched.exe) e testar pente a pente.");
            }
            else if (bsodRamDriver > 0)
            {
                result.Add("Tela azul de driver ou RAM (" + string.Join(", ", infos.Where(i => i.Suspect == BugcheckSuspect.RamOuDriver || i.Suspect == BugcheckSuspect.Hardware).Select(i => i.Name).Distinct().ToArray()) +
                           "): rodar o teste de memória e atualizar drivers (placa de vídeo, rede, chipset).");
            }

            // 2) disco
            int badSectors = Occ(d, BlockTag.Disk).Count(IsBadSector);
            if (badSectors > 0)
                result.Add("Disco com setor defeituoso (bad block, " + badSectors + " ocorrência(s)): acionar o técnico e/ou efetuar a troca do disco/servidor. " +
                           "Fazer backup agora. " + (smartBad ? "" : "O SMART não acusou falha, mas SMART OK não descarta o disco: o setor ruim pode já ter sido remapeado ou o disco estar atrás de RAID. ") +
                           "chkdsk /r não conserta disco que está morrendo.");
            int ntfsCorrupt = Occ(d, BlockTag.Disk).Count(o => string.Equals(Grouping.ShortSource(o.Source), "Ntfs", StringComparison.OrdinalIgnoreCase) && o.EventId == "55");
            if (ntfsCorrupt > 0)
                result.Add("Sistema de arquivos corrompido (Ntfs 55, " + ntfsCorrupt + "x): o NTFS achou erro na estrutura do volume (pode vir de desligamento brusco, energia, controladora ou disco ruim). " +
                           "Fazer backup ANTES de reparar, depois rodar chkdsk /scan e, na janela de manutenção, chkdsk /f (o /r também procura setor ruim e demora). " +
                           "SFC/DISM não reparam o NTFS. Se voltar depois do chkdsk, suspeitar do disco, cabo, controladora ou energia" +
                           (badSectors > 0 ? "; junto com o setor defeituoso acima, tratar como disco com falha." : "."));
            if (bsodDisk > 0 || diskEvents || smartBad)
            {
                var ev = new List<string>();
                if (bsodDisk > 0) ev.Add("tela azul de disco: " + string.Join(", ", infos.Where(i => i.Suspect == BugcheckSuspect.Disco).Select(i => i.Name).Distinct().ToArray()));
                if (diskEvents) ev.Add("eventos de disco/NTFS");
                if (smartBad) ev.Add("SMART/confiabilidade em ATENÇÃO");
                result.Add("Disco, cabo ou controladora sob suspeita (" + string.Join("; ", ev.ToArray()) + "). Fazer backup agora e rodar chkdsk /scan e a ferramenta do fabricante do disco." +
                           (diskEvents && !smartBad && d.Summary.Any(x => x.Label == "SMART" && x.Status == CheckStatus.Ok) ? " SMART OK não descarta o disco quando há eventos de disco." : ""));
            }

            // 3) driver
            if (bsodDriver > 0)
                result.Add("Tela azul típica de driver (" + string.Join(", ", infos.Where(i => i.Suspect == BugcheckSuspect.Driver).Select(i => i.Name).Distinct().ToArray()) +
                           "): abrir o C:\\Windows\\MEMORY.DMP no BlueScreenView/WinDbg para ver qual driver causou.");

            // 4) energia
            if (powerLoss > 0)
                result.Add("Desligamento sem tela azul e sem botão (" + powerLoss + "x): provável queda de energia, fonte ou no-break. Verificar a energia/no-break; queda de energia corrompe banco.");
            if (powerButton > 0)
                result.Add("Desligamento forçado pelo botão de energia (" + powerButton + "x): alguém segurou o botão (PC travado?). Verificar o motivo do travamento.");

            // 5) banco sem causa de hardware
            if (dbDamaged && !ramEvidence && bsodDisk == 0 && !diskEvents && !smartBad && powerLoss == 0)
                result.Add("Banco corrompido/caindo sem causa de hardware nos logs do Windows. Mesmo assim suspeitar de RAM (teste de memória), " +
                           "disco (chkdsk) e desligamentos forçados; checar a versão do MariaDB/MySQL.");
            if (dbCrashes >= Config.CrashLoopThreshold)
                result.Add("Banco em LOOP DE CRASH (" + dbCrashes + " quedas no período): o serviço cai e reinicia sozinho. " +
                           "Parar o serviço, tirar cópia da pasta data e só então recuperar (innodb_force_recovery + dump, ou backup). Não deixar o sistema gravando nele.");

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
                result.Add("Pouco espaço em disco (veja UNIDADES): liberar espaço; disco cheio derruba o banco e o Windows.");

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
