using System;
using System.Collections.Generic;
using System.Linq;

namespace DiagBIG.Core
{
    public class StorageController
    {
        public string Name { get; set; }
        public string DriverName { get; set; }
    }

    /// <summary>Saúde de um disco físico via contadores de confiabilidade do Windows (MSFT_PhysicalDisk / MSFT_StorageReliabilityCounter).</summary>
    public class DiskHealth
    {
        public string Name { get; set; }
        public string BusType { get; set; }
        /// <summary>0 = Saudável, 1 = Aviso, 2 = Não saudável, 5 = Desconhecido (null = não informado).</summary>
        public int? HealthStatus { get; set; }
        public int? Temperature { get; set; }
        /// <summary>% de desgaste (SSD/NVMe).</summary>
        public int? Wear { get; set; }
        public long? ReadErrorsUncorrected { get; set; }
        public long? WriteErrorsUncorrected { get; set; }
        public long? PowerOnHours { get; set; }
    }

    public class RaidInfo
    {
        public bool Found { get; set; }
        /// <summary>Por que achou RAID (controladora/driver, disco lógico).</summary>
        public List<string> Evidence { get; } = new List<string>();
        /// <summary>Controladoras vistas (para mostrar quando NÃO há RAID).</summary>
        public List<string> Controllers { get; } = new List<string>();
    }

    /// <summary>Regras de disco: quais eventos entram e se o PC/servidor tem controladora RAID.</summary>
    public static class Storage
    {
        /// <summary>
        /// Evento do log Sistema que entra em DISCO: Crítico/Erro de fonte de disco; aviso "disk" 51/52/153;
        /// aviso 129 (reset do dispositivo) de qualquer fonte de disco/controladora.
        /// </summary>
        public static bool IsDiskEvent(string provider, int level, int id)
        {
            string p = provider ?? "";
            if (IsDumpFailure(p, id)) return false;
            if (level == 1 || level == 2) return ContainsAny(p, Config.DiskSources);
            if (level == 3)
            {
                if (string.Equals(p, Config.DiskWarningSource, StringComparison.OrdinalIgnoreCase) && Config.DiskWarningIds.Contains(id))
                    return true;
                return Config.StorageWarningIds.Contains(id) && ContainsAny(p, Config.DiskSources);
            }
            return false;
        }

        /// <summary>
        /// volmgr 46/161/162 = "não conseguiu gravar o despejo de memória": consequência de tela azul,
        /// não defeito de disco. Ficam fora de DISCO (o resumo conta quantos foram ignorados).
        /// </summary>
        public static bool IsDumpFailure(string provider, int id)
        {
            return string.Equals(provider ?? "", Config.DumpFailureSource, StringComparison.OrdinalIgnoreCase) && Config.DumpFailureIds.Contains(id);
        }

        public static string HealthStatusText(int? status)
        {
            switch (status)
            {
                case 0: return "Saudável";
                case 1: return "Aviso";
                case 2: return "NÃO saudável";
                case 5: return "Desconhecido";
                default: return "não informado";
            }
        }

        /// <summary>Motivos para desconfiar do disco; vazio = nenhum.</summary>
        public static List<string> HealthProblems(DiskHealth h)
        {
            var p = new List<string>();
            if (h.HealthStatus == 1 || h.HealthStatus == 2) p.Add("saúde " + HealthStatusText(h.HealthStatus));
            if (h.Wear.HasValue && h.Wear.Value >= Config.WearAttentionPercent) p.Add("desgaste " + h.Wear + "%");
            if (h.ReadErrorsUncorrected.HasValue && h.ReadErrorsUncorrected.Value > 0) p.Add(h.ReadErrorsUncorrected + " erro(s) de leitura não corrigidos");
            if (h.WriteErrorsUncorrected.HasValue && h.WriteErrorsUncorrected.Value > 0) p.Add(h.WriteErrorsUncorrected + " erro(s) de escrita não corrigidos");
            if (h.Temperature.HasValue && h.Temperature.Value >= Config.DiskTempAttentionC) p.Add("temperatura " + h.Temperature + " °C");
            return p;
        }

        /// <summary>Linhas do bloco e resultado do resumo quando o SMART clássico não existe.</summary>
        public static SummaryItem EvaluateHealth(IList<DiskHealth> disks, List<string> lines)
        {
            var failing = new List<string>();
            foreach (DiskHealth h in disks)
            {
                List<string> problems = HealthProblems(h);
                lines.Add("Confiabilidade: " + h.Name + (string.IsNullOrEmpty(h.BusType) ? "" : " | " + h.BusType) +
                          " | saúde: " + HealthStatusText(h.HealthStatus) +
                          (h.Temperature.HasValue ? " | " + h.Temperature + " °C" : "") +
                          (h.Wear.HasValue ? " | desgaste " + h.Wear + "%" : "") +
                          (h.PowerOnHours.HasValue ? " | ligado há " + h.PowerOnHours + " h" : "") +
                          (h.ReadErrorsUncorrected.HasValue || h.WriteErrorsUncorrected.HasValue
                              ? " | erros não corrigidos: leitura " + (h.ReadErrorsUncorrected ?? 0) + ", escrita " + (h.WriteErrorsUncorrected ?? 0) : "") +
                          (problems.Count > 0 ? "  <<< ATENÇÃO: " + string.Join(", ", problems.ToArray()) : ""));
                if (problems.Count > 0) failing.Add(h.Name + " (" + string.Join(", ", problems.ToArray()) + ")");
            }
            var item = new SummaryItem { Label = "SMART" };
            if (failing.Count > 0)
            {
                item.Status = CheckStatus.Atencao;
                item.Text = failing.Count + " de " + disks.Count + " disco(s) com problema nos contadores de confiabilidade do Windows: " + string.Join("; ", failing.ToArray());
            }
            else
            {
                item.Status = CheckStatus.Ok;
                item.Text = disks.Count + " disco(s) verificado(s) pelos contadores de confiabilidade do Windows (SMART clássico indisponível), nenhum com problema";
            }
            return item;
        }

        public static RaidInfo DetectRaid(IEnumerable<StorageController> controllers, IEnumerable<string> diskModels)
        {
            var r = new RaidInfo();
            foreach (StorageController c in controllers ?? new StorageController[0])
            {
                string name = (c.Name ?? "").Trim(), driver = (c.DriverName ?? "").Trim();
                string label = name + (driver.Length > 0 ? " (driver " + driver + ")" : "");
                r.Controllers.Add(label);
                bool raidName = ContainsAny(name, Config.RaidNameKeywords);
                bool raidDriver = ContainsAny(driver, Config.RaidDrivers);
                // lsi_sas, arcsas e iaStor também servem a controladoras sem RAID (ex.: VMware, Intel RST em AHCI).
                bool ambiguous = ContainsAny(driver, Config.RaidDriversIfNamed);
                if (raidName || raidDriver || (ambiguous && raidName))
                    r.Evidence.Add("controladora " + label);
            }
            foreach (string m in diskModels ?? new string[0])
            {
                if (!string.IsNullOrEmpty(m) && ContainsAny(m, Config.RaidDiskModelKeywords))
                    r.Evidence.Add("disco lógico de RAID \"" + m.Trim() + "\"");
            }
            r.Found = r.Evidence.Count > 0;
            return r;
        }

        /// <summary>Texto da linha RAID do cabeçalho.</summary>
        public static string RaidLine(RaidInfo raid, string error)
        {
            if (error != null) return "NÃO VERIFICADO — " + error;
            if (raid == null) return "NÃO VERIFICADO — não consultado";
            if (raid.Found) return "SIM — " + string.Join("; ", raid.Evidence.Distinct().ToArray());
            return "NÃO" + (raid.Controllers.Count > 0
                ? " — controladoras: " + string.Join("; ", raid.Controllers.Distinct().ToArray())
                : " — nenhuma controladora SCSI/RAID listada");
        }

        static bool ContainsAny(string text, IEnumerable<string> terms)
        {
            return !string.IsNullOrEmpty(text) && terms.Any(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
