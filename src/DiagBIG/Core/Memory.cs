using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DiagBIG.Core
{
    public class MemoryModule
    {
        public string Slot { get; set; }
        public long Capacity { get; set; }
        public int SpeedMHz { get; set; }
        public string Manufacturer { get; set; }
        public string PartNumber { get; set; }
    }

    /// <summary>Descrição dos pentes de RAM (Win32_PhysicalMemory) para o bloco de memória.</summary>
    public static class Memory
    {
        public static List<string> Describe(IList<MemoryModule> modules)
        {
            var lines = new List<string>();
            if (modules == null || modules.Count == 0)
            {
                lines.Add("Pentes de RAM: o Windows não informou (comum em VM).");
                return lines;
            }
            long total = modules.Sum(m => m.Capacity);
            lines.Add("Pentes de RAM: " + modules.Count + " (" + ReportBuilder.FormatBytes(total) + " no total)");
            foreach (MemoryModule m in modules)
                lines.Add("  " + (string.IsNullOrWhiteSpace(m.Slot) ? "slot ?" : m.Slot.Trim()) + ": " + ReportBuilder.FormatBytes(m.Capacity) +
                          (m.SpeedMHz > 0 ? " | " + m.SpeedMHz.ToString(CultureInfo.InvariantCulture) + " MHz" : "") +
                          (string.IsNullOrWhiteSpace(m.Manufacturer) ? "" : " | " + m.Manufacturer.Trim()) +
                          (string.IsNullOrWhiteSpace(m.PartNumber) ? "" : " | " + m.PartNumber.Trim()));
            if (IsMixed(modules))
                lines.Add("AVISO: pentes diferentes (capacidade, velocidade ou modelo). Misturar pentes é causa conhecida de instabilidade.");
            return lines;
        }

        public static bool IsMixed(IList<MemoryModule> modules)
        {
            if (modules == null || modules.Count < 2) return false;
            return modules.Select(m => m.Capacity).Distinct().Count() > 1
                || modules.Select(m => m.SpeedMHz).Where(s => s > 0).Distinct().Count() > 1
                || modules.Select(m => (m.PartNumber ?? "").Trim().ToUpperInvariant()).Where(p => p.Length > 0).Distinct().Count() > 1;
        }
    }
}
