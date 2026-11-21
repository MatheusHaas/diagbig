using System;
using System.Globalization;

namespace DiagBIG.Core
{
    public enum WmiRepoState { NaoVerificado, Consistente, Inconsistente }

    /// <summary>Repositório WMI (C:\Windows\System32\wbem\Repository): leitura da saída do "winmgmt /verifyrepository" e nome da pasta renomeada no reparo.</summary>
    public static class WmiRepo
    {
        /// <summary>"O repositório WMI é consistente / inconsistente" (pt-BR) ou "WMI repository is consistent / inconsistent" (en).</summary>
        public static WmiRepoState Parse(string output)
        {
            string o = (output ?? "").ToLowerInvariant();
            if (o.Contains("inconsistente") || o.Contains("inconsistent")) return WmiRepoState.Inconsistente;
            if (o.Contains("consistente") || o.Contains("consistent")) return WmiRepoState.Consistente;
            return WmiRepoState.NaoVerificado;
        }

        /// <summary>Nome da pasta antiga: não usa "OLD_Repository" fixo para não colidir com uma renomeação feita à mão.</summary>
        public static string OldName(DateTime now)
        {
            return "OLD_Repository_" + now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);
        }

        public static string SummaryText(WmiRepoState s, string note)
        {
            switch (s)
            {
                case WmiRepoState.Consistente: return "repositório consistente";
                case WmiRepoState.Inconsistente: return "repositório INCONSISTENTE: erros aleatórios no Windows/sistema (ex.: dclvendas.exe no bigupdate). Usar o botão \"Reparar WMI (repositório)\"";
                default: return string.IsNullOrEmpty(note) ? "não foi possível verificar" : note;
            }
        }

        public static string N1Line()
        {
            return "ATENÇÃO WMI: o repositório do WMI do Windows está INCONSISTENTE (winmgmt /verifyrepository). Pode causar erro aleatório no bigupdate e programas (ex.: dclvendas.exe). " +
                   "Usar o botão \"Reparar WMI (repositório)\" do DiagBIG, fora do movimento; não precisa reiniciar o Windows. Não muda quem acionar.";
        }
    }
}
