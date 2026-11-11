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
            if (level == 1 || level == 2) return ContainsAny(p, Config.DiskSources);
            if (level == 3)
            {
                if (string.Equals(p, Config.DiskWarningSource, StringComparison.OrdinalIgnoreCase) && Config.DiskWarningIds.Contains(id))
                    return true;
                return Config.StorageWarningIds.Contains(id) && ContainsAny(p, Config.DiskSources);
            }
            return false;
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
