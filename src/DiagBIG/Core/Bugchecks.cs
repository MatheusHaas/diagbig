using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DiagBIG.Core
{
    /// <summary>Quem costuma causar o código de tela azul (só orientação para o suporte, não é veredito).</summary>
    public enum BugcheckSuspect { Ram, RamOuDriver, Disco, Driver, Hardware, Outro }

    public class BugcheckInfo
    {
        public long Code { get; set; }
        public string Name { get; set; }
        public BugcheckSuspect Suspect { get; set; }
    }

    /// <summary>Tabela dos códigos de tela azul mais comuns em PCs/servidores de cliente.</summary>
    public static class Bugchecks
    {
        static BugcheckInfo B(long code, string name, BugcheckSuspect s) { return new BugcheckInfo { Code = code, Name = name, Suspect = s }; }

        static readonly Dictionary<long, BugcheckInfo> Table = Build();

        static Dictionary<long, BugcheckInfo> Build()
        {
            var list = new[]
            {
                B(0x0A, "IRQL_NOT_LESS_OR_EQUAL", BugcheckSuspect.RamOuDriver),
                B(0x19, "BAD_POOL_HEADER", BugcheckSuspect.Ram),
                B(0x1A, "MEMORY_MANAGEMENT", BugcheckSuspect.Ram),
                B(0x1E, "KMODE_EXCEPTION_NOT_HANDLED", BugcheckSuspect.RamOuDriver),
                B(0x24, "NTFS_FILE_SYSTEM", BugcheckSuspect.Disco),
                B(0x3B, "SYSTEM_SERVICE_EXCEPTION", BugcheckSuspect.Driver),
                B(0x4E, "PFN_LIST_CORRUPT", BugcheckSuspect.Ram),
                B(0x50, "PAGE_FAULT_IN_NONPAGED_AREA", BugcheckSuspect.Ram),
                B(0x77, "KERNEL_STACK_INPAGE_ERROR", BugcheckSuspect.Disco),
                B(0x7A, "KERNEL_DATA_INPAGE_ERROR", BugcheckSuspect.Disco),
                B(0x7B, "INACCESSIBLE_BOOT_DEVICE", BugcheckSuspect.Disco),
                B(0x7E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", BugcheckSuspect.Driver),
                B(0x7F, "UNEXPECTED_KERNEL_MODE_TRAP", BugcheckSuspect.RamOuDriver),
                B(0x8E, "KERNEL_MODE_EXCEPTION_NOT_HANDLED", BugcheckSuspect.RamOuDriver),
                B(0x9C, "MACHINE_CHECK_EXCEPTION", BugcheckSuspect.Hardware),
                B(0xBE, "ATTEMPTED_WRITE_TO_READONLY_MEMORY", BugcheckSuspect.RamOuDriver),
                B(0xC2, "BAD_POOL_CALLER", BugcheckSuspect.Driver),
                B(0xC5, "DRIVER_CORRUPTED_EXPOOL", BugcheckSuspect.RamOuDriver),
                B(0xD1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL", BugcheckSuspect.Driver),
                B(0xEF, "CRITICAL_PROCESS_DIED", BugcheckSuspect.Outro),
                B(0xF4, "CRITICAL_OBJECT_TERMINATION", BugcheckSuspect.Disco),
                B(0x101, "CLOCK_WATCHDOG_TIMEOUT", BugcheckSuspect.Hardware),
                B(0x124, "WHEA_UNCORRECTABLE_ERROR", BugcheckSuspect.Hardware),
                B(0x133, "DPC_WATCHDOG_VIOLATION", BugcheckSuspect.Driver),
                B(0x139, "KERNEL_SECURITY_CHECK_FAILURE", BugcheckSuspect.RamOuDriver),
                B(0x154, "UNEXPECTED_STORE_EXCEPTION", BugcheckSuspect.Disco),
            };
            var d = new Dictionary<long, BugcheckInfo>();
            foreach (BugcheckInfo i in list) d[i.Code] = i;
            return d;
        }

        public static BugcheckInfo Lookup(long code)
        {
            BugcheckInfo i;
            return Table.TryGetValue(code, out i) ? i : null;
        }

        /// <summary>"MEMORY_MANAGEMENT — suspeita: RAM defeituosa". Vazio se o código não está na tabela.</summary>
        public static string Describe(long code)
        {
            BugcheckInfo i = Lookup(code);
            if (i == null) return "";
            return i.Name + " — suspeita: " + SuspectText(i.Suspect);
        }

        public static string SuspectText(BugcheckSuspect s)
        {
            switch (s)
            {
                case BugcheckSuspect.Ram: return "RAM defeituosa (ou driver que corrompe memória)";
                case BugcheckSuspect.RamOuDriver: return "driver ou RAM defeituosa";
                case BugcheckSuspect.Disco: return "disco/cabo/controladora";
                case BugcheckSuspect.Driver: return "driver com defeito";
                case BugcheckSuspect.Hardware: return "hardware (CPU, RAM, placa-mãe, temperatura)";
                default: return "causa variada, analisar o MEMORY.DMP";
            }
        }

        static readonly Regex KernelPowerCode = new Regex(@"BugcheckCode 0x([0-9A-Fa-f]+)", RegexOptions.Compiled);
        static readonly Regex Wer1001Code = new Regex(@"0x([0-9A-Fa-f]{8})\b", RegexOptions.Compiled);

        /// <summary>Código de tela azul de um evento Kernel-Power 41 (interpretação do DiagBIG) ou WER/BugCheck 1001.</summary>
        public static long? FromOccurrence(Occurrence o)
        {
            if (o == null || string.IsNullOrEmpty(o.Message)) return null;
            Match m = KernelPowerCode.Match(o.Message);
            if (!m.Success && PowerEvents.Classify(o.Source, o.EventId) == PowerKind.BlueScreen)
                m = Wer1001Code.Match(o.Message);
            if (!m.Success) return null;
            long v;
            return long.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) && v != 0 ? v : (long?)null;
        }
    }
}
