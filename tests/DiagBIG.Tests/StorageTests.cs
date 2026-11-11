using System.Linq;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    public class StorageTests
    {
        static StorageController C(string name, string driver) => new StorageController { Name = name, DriverName = driver };

        [Theory]
        [InlineData("disk", 2, 7, true)]
        [InlineData("Ntfs", 2, 55, true)]
        [InlineData("percsas3i", 2, 11, true)]       // Dell PERC
        [InlineData("HpCISSs2", 1, 9, true)]         // HP Smart Array
        [InlineData("megasas2i", 3, 129, true)]      // aviso de reset atrás da controladora
        [InlineData("storahci", 3, 129, true)]
        [InlineData("disk", 3, 153, true)]
        [InlineData("disk", 3, 129, true)]
        [InlineData("storahci", 3, 153, false)]      // 51/52/153 só da fonte "disk"
        [InlineData("megasas", 4, 129, false)]       // informação não entra
        [InlineData("Service Control Manager", 2, 7000, false)]
        public void DiskEventRule(string provider, int level, int id, bool expected) =>
            Assert.Equal(expected, Storage.IsDiskEvent(provider, level, id));

        [Fact]
        public void DellPerc_IsRaid()
        {
            var r = Storage.DetectRaid(new[] { C("PERC H730 Mini", "percsas3i") }, new[] { "DELL PERC H730 Mini SCSI Disk Device" });
            Assert.True(r.Found);
            Assert.Equal("SIM — controladora PERC H730 Mini (driver percsas3i); disco lógico de RAID \"DELL PERC H730 Mini SCSI Disk Device\"",
                         Storage.RaidLine(r, null));
        }

        [Fact]
        public void HpSmartArray_ByDriverOrLogicalVolume()
        {
            Assert.True(Storage.DetectRaid(new[] { C("HPE Smart Array P408i-a SR Gen10", "smartpqi") }, null).Found);
            Assert.True(Storage.DetectRaid(null, new[] { "HP LOGICAL VOLUME SCSI Disk Device" }).Found);
        }

        [Fact]
        public void VmAndAhci_AreNotRaid()
        {
            var vm = Storage.DetectRaid(
                new[] { C("LSI Adapter, SAS 3000 series, 8-port with 1068", "lsi_sas"), C("Microsoft Storage Spaces Controller", "spaceport") },
                new[] { "VMware Virtual disk SCSI Disk Device" });
            Assert.False(vm.Found);
            Assert.StartsWith("NÃO — controladoras: LSI Adapter", Storage.RaidLine(vm, null));

            Assert.False(Storage.DetectRaid(new[] { C("Intel(R) Chipset SATA/PCIe RST Premium Controller", "iaStorAVC") }, new[] { "Samsung SSD 870 EVO" }).Found);
            Assert.True(Storage.DetectRaid(new[] { C("Intel(R) Desktop/Workstation/Server Express Chipset SATA RAID Controller", "iaStorA") }, null).Found);
            Assert.True(Storage.DetectRaid(new[] { C("LSI MegaRAID SAS 9260-8i", "lsi_sas2") }, null).Found);
        }

        [Fact]
        public void RaidLine_NotVerifiedAndEmpty()
        {
            Assert.Equal("NÃO VERIFICADO — WMI indisponível", Storage.RaidLine(null, "WMI indisponível"));
            Assert.Equal("NÃO — nenhuma controladora SCSI/RAID listada", Storage.RaidLine(Storage.DetectRaid(null, null), null));
        }
    }
}
