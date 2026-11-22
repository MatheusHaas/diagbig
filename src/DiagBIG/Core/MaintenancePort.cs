using System.Collections.Generic;
using System.Globalization;

namespace DiagBIG.Core
{
    /// <summary>Qual porta o sistema do cliente usa e qual porta o banco usa durante a manutenção.</summary>
    public class PortPlan
    {
        /// <summary>Porta do sistema do cliente (a de antes da manutenção; é a que volta no fim).</summary>
        public int ClientPort;
        /// <summary>Porta da manutenção. 0 = nenhuma porta livre foi achada.</summary>
        public int MaintPort;
        /// <summary>true = o my.ini já estava na porta de manutenção, colocada antes pelo próprio DiagBIG (force 1 e depois 2, tirar o force).</summary>
        public bool Reused;
        /// <summary>Por que cada porta anterior foi pulada (para mostrar na confirmação e gravar no relatório do procedimento).</summary>
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Escolhe a porta de manutenção. A manutenção com innodb_force_recovery só é segura se o sistema do cliente NÃO conseguir conectar,
    /// então a porta de manutenção precisa ser diferente da que o cliente usa. O padrão é 3307; se o cliente já usa a 3307 (ou outro
    /// programa está escutando nela), sobe para 3308, 3309... até a primeira livre.
    /// </summary>
    public static class MaintenancePort
    {
        public const int Preferred = 3307;
        public const int Last = 3399;
        public const int MySqlDefault = 3306;
        /// <summary>Marca que o MyIniEditor deixa no my.ini ("# DiagBIG dd/MM/aaaa HH:mm: era ...").</summary>
        const string Mark = "# DiagBIG";

        /// <summary>Porta da seção do servidor no texto do my.ini (3306 se não houver).</summary>
        public static int PortOf(string iniText) { return PortOf(MyIniParser.Parse(iniText)); }

        public static int PortOf(MyIniOptions o)
        {
            int p;
            if (o != null && o.Port != null && int.TryParse(o.Port.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out p) && p > 0 && p < 65536)
                return p;
            return MySqlDefault;
        }

        /// <param name="currentIni">my.ini como está agora.</param>
        /// <param name="originalIni">my.ini.diagbig-original (null = não existe).</param>
        /// <param name="busy">Portas TCP em escuta neste Windows (pode ser null).</param>
        public static PortPlan Plan(string currentIni, string originalIni, ICollection<int> busy)
        {
            var plan = new PortPlan();
            int cur = PortOf(currentIni);

            // O my.ini já foi mexido por nós (tem a marca e o backup do original existe): a porta do cliente é a do original e a de
            // manutenção é a que já está lá. Assim force 1 -> force 2 -> tirar o force não trocam de porta no meio do reparo.
            bool ours = originalIni != null && currentIni != null && currentIni.Contains(Mark);
            if (ours)
            {
                plan.ClientPort = PortOf(originalIni);
                if (cur != plan.ClientPort)
                {
                    plan.MaintPort = cur;
                    plan.Reused = true;
                    plan.Notes.Add("O my.ini já está na porta de manutenção " + cur + " (colocada antes pelo DiagBIG); a porta do cliente é " + plan.ClientPort + ".");
                    return plan;
                }
            }
            else plan.ClientPort = cur;

            for (int c = Preferred; c <= Last; c++)
            {
                if (c == plan.ClientPort) plan.Notes.Add("Porta " + c + " pulada: é a porta que o sistema do cliente usa.");
                else if (busy != null && busy.Contains(c)) plan.Notes.Add("Porta " + c + " pulada: já está em uso por outro programa.");
                else { plan.MaintPort = c; return plan; }
            }
            plan.MaintPort = 0;
            return plan;
        }
    }
}
