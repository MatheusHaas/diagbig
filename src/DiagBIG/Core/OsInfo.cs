using System;
using System.Collections.Generic;
using System.Linq;

namespace DiagBIG.Core
{
    /// <summary>Dados brutos do sistema operacional (lidos no Windows; as regras abaixo são puras e testáveis).</summary>
    public class OsInfo
    {
        public string ProductName { get; set; }
        public string EditionId { get; set; }
        /// <summary>Registro: "Client", "Server" ou "Server Core".</summary>
        public string InstallationType { get; set; }
        /// <summary>Win32_OperatingSystem.ProductType: 1 estação, 2 controlador de domínio, 3 servidor.</summary>
        public int? ProductType { get; set; }
        /// <summary>Win32_ComputerSystem.DomainRole: 0/1 estação, 2/3 servidor, 4/5 controlador de domínio.</summary>
        public int? DomainRole { get; set; }
        /// <summary>Motivos de reinício pendente (vazio = nenhum).</summary>
        public List<string> PendingReboot { get; } = new List<string>();
        public bool PendingRebootChecked { get; set; }
        /// <summary>Inicialização rápida (HiberbootEnabled); null = não lido.</summary>
        public bool? FastStartup { get; set; }
    }

    public static class OsRules
    {
        public static bool IsServer(OsInfo o)
        {
            if (o == null) return false;
            if (o.ProductType == 2 || o.ProductType == 3) return true;
            if (o.ProductType == 1) return false;
            if (o.DomainRole >= 2) return true;
            return Has(o.ProductName, "Server");
        }

        public static bool IsEvaluation(OsInfo o)
        {
            return o != null && (Has(o.ProductName, "Evaluation") || Has(o.EditionId, "Evaluation"));
        }

        static bool Has(string s, string part) { return s != null && s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0; }

        /// <summary>Fim do suporte (atualizações de segurança) da versão, ou null se não conhecida (ex.: Windows 11, LTSC).</summary>
        public static DateTime? SupportEnd(string productName)
        {
            string p = productName ?? "";
            if (Has(p, "LTSC") || Has(p, "LTSB")) return null;
            if (Has(p, "Server 2025")) return new DateTime(2034, 11, 14);
            if (Has(p, "Server 2022")) return new DateTime(2031, 10, 14);
            if (Has(p, "Server 2019")) return new DateTime(2029, 1, 9);
            if (Has(p, "Server 2016")) return new DateTime(2027, 1, 12);
            if (Has(p, "Server 2012")) return new DateTime(2023, 10, 10);
            if (Has(p, "Server 2008")) return new DateTime(2020, 1, 14);
            if (Has(p, "Server 2003")) return new DateTime(2015, 7, 14);
            if (Has(p, "Windows 10")) return new DateTime(2025, 10, 14);
            if (Has(p, "Windows 8.1")) return new DateTime(2023, 1, 10);
            if (Has(p, "Windows 8")) return new DateTime(2016, 1, 12);
            if (Has(p, "Windows 7")) return new DateTime(2020, 1, 14);
            if (Has(p, "Vista")) return new DateTime(2017, 4, 11);
            if (Has(p, "Windows XP")) return new DateTime(2014, 4, 8);
            return null;
        }

        public static string TypeText(OsInfo o)
        {
            if (!IsServer(o)) return "estação de trabalho (Windows cliente)";
            string role = o.DomainRole == 4 || o.DomainRole == 5 || o.ProductType == 2 ? "controlador de domínio"
                        : o.DomainRole == 3 ? "servidor membro de domínio"
                        : o.DomainRole == 2 ? "servidor independente (fora de domínio)"
                        : "servidor";
            return "Windows Server (" + role + ")";
        }

        /// <summary>Linhas da sessão SISTEMA OPERACIONAL. Só entram os campos que valem para o tipo detectado.</summary>
        public static List<string> Lines(OsInfo o, DateTime now)
        {
            var l = new List<string>();
            if (o == null) return l;
            bool server = IsServer(o);
            Func<string, string, string> row = (k, v) => "  " + (k + " ").PadRight(16, '.') + ": " + v;

            l.Add(row("TIPO", TypeText(o)));
            if (!string.IsNullOrEmpty(o.ProductName)) l.Add(row("EDIÇÃO", o.ProductName));
            if (server && !string.IsNullOrEmpty(o.InstallationType))
                l.Add(row("INSTALAÇÃO", Has(o.InstallationType, "Core") ? "Server Core (sem interface gráfica)" : "com interface gráfica"));

            DateTime? end = SupportEnd(o.ProductName);
            if (end != null)
            {
                string d = end.Value.ToString("dd/MM/yyyy");
                l.Add(row("SUPORTE", end.Value < now
                    ? "AVISO: sem atualizações de segurança desde " + d + " (salvo ESU pago). Planejar a atualização do sistema."
                    : "atualizações de segurança até " + d));
            }

            if (IsEvaluation(o))
                l.Add(row("LICENÇA", "AVISO: edição de AVALIAÇÃO. Passado o prazo o Windows desliga sozinho de hora em hora. " +
                                    "Conferir o prazo com \"slmgr /xpr\"; em produção, usar licença definitiva."));

            if (o.PendingRebootChecked)
                l.Add(row("REINÍCIO PEND.", o.PendingReboot.Count == 0 ? "não"
                    : "SIM (" + string.Join(", ", o.PendingReboot.ToArray()) + "): reiniciar em horário combinado para concluir atualizações/instalações"));

            if (!server && o.FastStartup == true)
                l.Add(row("INIC. RÁPIDA", "ligada: \"Desligar\" não reinicia o Windows por completo (só \"Reiniciar\" reinicia); " +
                                         "o \"ligado desde\" e a linha do tempo podem não mostrar cada desligamento"));
            return l;
        }
    }
}
