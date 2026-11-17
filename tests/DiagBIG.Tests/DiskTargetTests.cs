using System;
using System.Collections.Generic;
using DiagBIG.Core;
using Xunit;

namespace DiagBIG.Tests
{
    public class DiskTargetTests
    {
        const string Single = "Este PC tem 1 disco físico (X): as unidades C:, E: estão nele.";

        [Fact]
        public void Ntfs55VolumeDesconhecido()
        {
            string t = DiskTarget.Describe("Corrupção detectada na estrutura do sistema de arquivos, no volume ??.", null, Single);
            Assert.Contains("NÃO INFORMADA", t);
            Assert.Contains("1 disco físico", t);
        }

        [Fact]
        public void Ntfs55ComLetra()
        {
            Assert.Equal("Unidade: D:", DiskTarget.Describe("Corrupção detectada no volume D:.", null, Single));
        }

        [Fact]
        public void VolumeMapeiaParaLetra()
        {
            var map = new Dictionary<string, string> { { "3", "C:" } };
            Assert.Contains("C:", DiskTarget.Describe(@"O driver detectou erro em \Device\HarddiskVolume3", map, null));
        }

        [Fact]
        public void DiscoFisicoEPorta()
        {
            Assert.Contains("disco físico nº 0", DiskTarget.Describe(@"O dispositivo \Device\Harddisk0\DR0 tem um bloco inválido.", null, Single));
            string p = DiskTarget.Describe(@"Redefinir para dispositivo, \Device\RaidPort0, emitido.", null, Single);
            Assert.Contains("RaidPort0", p);
            Assert.Contains("1 disco físico", p);
        }

        [Fact]
        public void SemPistaNaoMuda()
        {
            Assert.Null(DiskTarget.Describe("Algo aconteceu", null, Single));
            var o = new Occurrence { Message = "Algo aconteceu" };
            DiskTarget.Annotate(o, null, Single);
            Assert.Equal("Algo aconteceu", o.Message);
        }

        [Fact]
        public void ResumoAntigoAgrupaEData()
        {
            var l = new List<Occurrence>
            {
                new Occurrence { GroupKey = "Ntfs 55", Time = new DateTime(2026, 5, 27) },
                new Occurrence { GroupKey = "Ntfs 55", Time = new DateTime(2026, 10, 6) },
            };
            Assert.Equal("Ntfs 55: 2x, último em 06/10/2026", DiskTarget.OldSummary(l)[0]);
        }
    }
}
