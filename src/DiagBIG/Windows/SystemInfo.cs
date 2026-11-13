using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Management;
using DiagBIG.Core;
using Microsoft.Win32;

namespace DiagBIG.Windows
{
    internal static class SystemInfo
    {
        public static string WindowsVersion()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false))
                {
                    string product = k.GetValue("ProductName") as string ?? "Windows";
                    string display = k.GetValue("DisplayVersion") as string ?? k.GetValue("ReleaseId") as string;
                    string build = k.GetValue("CurrentBuildNumber") as string ?? k.GetValue("CurrentBuild") as string;
                    object ubr = k.GetValue("UBR");
                    string sp = k.GetValue("CSDVersion") as string;
                    int b;
                    // O registro do Windows 11 ainda diz "Windows 10".
                    if (build != null && int.TryParse(build, out b) && b >= 22000)
                        product = product.Replace("Windows 10", "Windows 11");
                    string s = product;
                    if (!string.IsNullOrEmpty(display)) s += " " + display;
                    if (!string.IsNullOrEmpty(sp)) s += " " + sp;
                    if (build != null) s += " (build " + build + (ubr is int ? "." + ((int)ubr).ToString(CultureInfo.InvariantCulture) : "") + ")";
                    s += Environment.Is64BitOperatingSystem ? " 64 bits" : " 32 bits";
                    return s;
                }
            }
            catch (Exception)
            {
                return Environment.OSVersion.VersionString;
            }
        }

        /// <summary>Último boot completo (Win32_OperatingSystem.LastBootUpTime).</summary>
        public static DateTime? LastBoot()
        {
            using (var s = new ManagementObjectSearcher(@"root\cimv2", "SELECT LastBootUpTime FROM Win32_OperatingSystem", Options()))
                foreach (ManagementObject mo in s.Get())
                    using (mo)
                    {
                        string v = mo["LastBootUpTime"] as string;
                        if (!string.IsNullOrEmpty(v)) return ManagementDateTimeConverter.ToDateTime(v);
                    }
            return null;
        }

        public static List<string> FixedDrives()
        {
            var list = new List<string>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    long free = d.AvailableFreeSpace, total = d.TotalSize;
                    int pct = total > 0 ? (int)(free * 100 / total) : 0;
                    string s = d.Name.TrimEnd('\\') + " " + ReportBuilder.FormatBytes(free) + " livres de " + ReportBuilder.FormatBytes(total) + " (" + pct + "%)";
                    if (pct < 10 || free < 5L * 1024 * 1024 * 1024) s += "  <<< POUCO ESPAÇO";
                    list.Add(s);
                }
                catch (Exception ex) { list.Add(d.Name.TrimEnd('\\') + " erro ao ler: " + ex.Message); }
            }
            return list;
        }

        /// <summary>Status SMART via WMI (root\wmi). Nunca lança exceção.</summary>
        /// <summary>Controladoras (Win32_SCSIController) e modelos de disco (Win32_DiskDrive) → tem RAID?</summary>
        public static RaidInfo DetectRaid()
        {
            var ctrls = new List<StorageController>();
            using (var s = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name, DriverName FROM Win32_SCSIController", Options()))
                foreach (ManagementObject mo in s.Get())
                    using (mo)
                        ctrls.Add(new StorageController
                        {
                            Name = Convert.ToString(mo["Name"], CultureInfo.InvariantCulture),
                            DriverName = Convert.ToString(mo["DriverName"], CultureInfo.InvariantCulture),
                        });
            var models = new List<string>();
            using (var s = new ManagementObjectSearcher(@"root\cimv2", "SELECT Model FROM Win32_DiskDrive", Options()))
                foreach (ManagementObject mo in s.Get())
                    using (mo) models.Add(Convert.ToString(mo["Model"], CultureInfo.InvariantCulture));
            return Storage.DetectRaid(ctrls, models);
        }

        public static ReportBlock Smart(RaidInfo raid, out SummaryItem summary)
        {
            var block = new ReportBlock { Title = "SMART (PREVISÃO DE FALHA DO DISCO)", InfoOnly = true };
            summary = new SummaryItem { Label = "SMART" };

            // Modelos dos discos (só informativo).
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\cimv2", "SELECT Model, Size, Status, InterfaceType FROM Win32_DiskDrive", Options()))
                    foreach (ManagementObject mo in s.Get())
                        using (mo)
                        {
                            object size = mo["Size"];
                            block.Info.Add("Disco: " + mo["Model"] + " | " + mo["InterfaceType"] +
                                           (size != null ? " | " + ReportBuilder.FormatBytes(Convert.ToInt64(size, CultureInfo.InvariantCulture)) : "") +
                                           " | status: " + mo["Status"]);
                        }
            }
            catch (Exception ex) { block.Info.Add("Lista de discos indisponível: " + ex.Message); }

            int count = 0, failing = 0;
            var failingNames = new List<string>();
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, PredictFailure, Reason FROM MSStorageDriver_FailurePredictStatus", Options()))
                    foreach (ManagementObject mo in s.Get())
                        using (mo)
                        {
                            count++;
                            bool pf = mo["PredictFailure"] is bool && (bool)mo["PredictFailure"];
                            string inst = Convert.ToString(mo["InstanceName"], CultureInfo.InvariantCulture);
                            block.Info.Add("SMART: " + inst + " → PredictFailure=" + (pf ? "True  <<< ATENÇÃO" : "False") +
                                           (pf ? " (Reason=" + mo["Reason"] + ")" : ""));
                            if (pf) { failing++; failingNames.Add(inst); }
                        }
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotSupported || ex.ErrorCode == ManagementStatus.InvalidClass)
            {
                return SmartNotVerified(block, summary, "SMART não suportado neste PC (comum em VM, RAID e alguns NVMe)", raid);
            }
            catch (Exception ex)
            {
                return SmartNotVerified(block, summary, "falha ao consultar SMART via WMI: " + ex.Message, raid);
            }

            if (count == 0)
                return SmartNotVerified(block, summary, "nenhum disco informou SMART (comum em VM, RAID e alguns NVMe)", raid);

            if (failing > 0)
            {
                summary.Status = CheckStatus.Atencao;
                summary.Text = failing + " de " + count + " disco(s) com previsão de falha (PredictFailure=True): " + string.Join(", ", failingNames.ToArray());
            }
            else
            {
                summary.Status = CheckStatus.Ok;
                summary.Text = count + " disco(s) verificado(s), nenhum com previsão de falha";
            }
            block.Info.Add("Resultado: " + ReportBuilder.StatusText(summary.Status) + " — " + summary.Text);
            return block;
        }

        public static List<MemoryModule> MemoryModules()
        {
            var list = new List<MemoryModule>();
            using (var s = new ManagementObjectSearcher(@"root\cimv2", "SELECT BankLabel, DeviceLocator, Capacity, Speed, Manufacturer, PartNumber FROM Win32_PhysicalMemory", Options()))
                foreach (ManagementObject mo in s.Get())
                    using (mo)
                    {
                        object cap = mo["Capacity"], speed = mo["Speed"];
                        list.Add(new MemoryModule
                        {
                            Slot = Convert.ToString(mo["DeviceLocator"], CultureInfo.InvariantCulture) ?? Convert.ToString(mo["BankLabel"], CultureInfo.InvariantCulture),
                            Capacity = cap == null ? 0 : Convert.ToInt64(cap, CultureInfo.InvariantCulture),
                            SpeedMHz = speed == null ? 0 : Convert.ToInt32(speed, CultureInfo.InvariantCulture),
                            Manufacturer = Convert.ToString(mo["Manufacturer"], CultureInfo.InvariantCulture),
                            PartNumber = Convert.ToString(mo["PartNumber"], CultureInfo.InvariantCulture),
                        });
                    }
            return list;
        }

        static long? Num(object v) { return v == null ? (long?)null : Convert.ToInt64(v, CultureInfo.InvariantCulture); }

        static string BusTypeName(object v)
        {
            switch (v == null ? -1 : Convert.ToInt32(v, CultureInfo.InvariantCulture))
            {
                case 3: return "ATA"; case 7: return "USB"; case 8: return "RAID"; case 10: return "SAS";
                case 11: return "SATA"; case 16: return "Storage Spaces"; case 17: return "NVMe"; default: return "";
            }
        }

        /// <summary>
        /// Alternativa ao SMART clássico (root\Microsoft\Windows\Storage: MSFT_PhysicalDisk + contadores de confiabilidade,
        /// Windows 8/Server 2012 em diante). Funciona em muitos NVMe. Retorna null se não houver dados.
        /// </summary>
        static List<DiskHealth> ReliabilityCounters()
        {
            var list = new List<DiskHealth>();
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"), Options()))
                foreach (ManagementObject mo in s.Get())
                    using (mo)
                    {
                        var h = new DiskHealth
                        {
                            Name = Convert.ToString(mo["FriendlyName"], CultureInfo.InvariantCulture),
                            BusType = BusTypeName(mo["BusType"]),
                            HealthStatus = mo["HealthStatus"] == null ? (int?)null : Convert.ToInt32(mo["HealthStatus"], CultureInfo.InvariantCulture),
                        };
                        try
                        {
                            foreach (ManagementObject rc in mo.GetRelated("MSFT_StorageReliabilityCounter"))
                                using (rc)
                                {
                                    h.Temperature = (int?)Num(rc["Temperature"]);
                                    h.Wear = (int?)Num(rc["Wear"]);
                                    h.ReadErrorsUncorrected = Num(rc["ReadErrorsUncorrected"]);
                                    h.WriteErrorsUncorrected = Num(rc["WriteErrorsUncorrected"]);
                                    h.PowerOnHours = Num(rc["PowerOnHours"]);
                                    break;
                                }
                        }
                        catch (Exception) { /* sem contadores: ainda vale o HealthStatus */ }
                        list.Add(h);
                    }
            return list;
        }

        static ReportBlock SmartNotVerified(ReportBlock block, SummaryItem summary, string reason, RaidInfo raid)
        {
            if (raid == null || !raid.Found)
            {
                try
                {
                    List<DiskHealth> disks = ReliabilityCounters();
                    if (disks.Count > 0)
                    {
                        block.Info.Add("SMART clássico indisponível (" + reason + "); usando os contadores de confiabilidade do Windows:");
                        var lines = new List<string>();
                        SummaryItem r = Storage.EvaluateHealth(disks, lines);
                        block.Info.AddRange(lines);
                        summary.Status = r.Status;
                        summary.Text = r.Text;
                        block.Info.Add("Resultado: " + ReportBuilder.StatusText(r.Status) + " — " + r.Text);
                        return block;
                    }
                }
                catch (Exception ex) { block.Info.Add("Contadores de confiabilidade indisponíveis: " + ex.Message); }
            }
            summary.Status = CheckStatus.NaoVerificado;
            summary.Text = reason;
            block.Info.Add("Resultado: NÃO VERIFICADO — " + reason);
            if (raid != null && raid.Found)
            {
                summary.Text += "; há RAID: verificar os discos pela ferramenta da controladora";
                block.Info.Add("Há controladora RAID: o Windows não enxerga o SMART dos discos físicos atrás dela.");
                block.Info.Add("Verificar pela ferramenta da controladora (ex.: Dell OpenManage/iDRAC, HPE SSA/iLO, MegaRAID Storage Manager).");
            }
            return block;
        }

        static EnumerationOptions Options()
        {
            return new EnumerationOptions { Timeout = TimeSpan.FromSeconds(30), ReturnImmediately = true, Rewindable = false };
        }
    }
}
