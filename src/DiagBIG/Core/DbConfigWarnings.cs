using System;
using System.Collections.Generic;
using System.Globalization;

namespace DiagBIG.Core
{
    /// <summary>
    /// Avisos sobre opções do my.ini que tiram a proteção do InnoDB contra queda de energia, tela azul e
    /// hardware com defeito, ou que deixam o banco em modo de recuperação.
    /// </summary>
    public static class DbConfigWarnings
    {
        public static bool IsOff(string v)
        {
            string x = (v ?? "").Trim().ToLowerInvariant();
            return x == "off" || x == "0" || x == "false" || x == "no";
        }

        /// <param name="riskReasons">O que o relatório achou de queda de energia/hardware (pode ser vazio).</param>
        public static List<string> Evaluate(MyIniOptions o, IList<string> riskReasons)
        {
            var w = new List<string>();
            if (o == null) return w;
            string risk = riskReasons != null && riskReasons.Count > 0
                ? " Neste PC o relatório já mostra: " + string.Join("; ", new List<string>(riskReasons).ToArray()) + "."
                : "";

            int force;
            if (o.ForceRecovery != null && int.TryParse(o.ForceRecovery.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out force) && force > 0)
                w.Add("innodb_force_recovery=" + force + " ESTÁ ATIVO no my.ini: o banco sobe em modo de recuperação e o InnoDB bloqueia INSERT/UPDATE/DELETE." +
                      (force >= 4 ? " Valor 4 ou maior pode danificar os dados de vez." : "") +
                      " Só deve ser usado para MANUTENÇÃO (tirar o dump) e com a PORTA do banco TROCADA (ex.: 3307 para outra), para o sistema do cliente não conectar nele enquanto estiver assim." +
                      " Depois: remover a linha, voltar a porta original, recriar o banco e restaurar o dump.");

            if (IsOff(o.DoubleWrite))
                w.Add("innodb_doublewrite=OFF no my.ini: sem essa proteção, queda de energia, tela azul, RAM/disco com defeito ou qualquer crash do banco " +
                      "pode deixar páginas do InnoDB corrompidas sem como reparar. Remover a linha (o padrão é ON) e reiniciar o serviço." + risk);

            string f = (o.FlushLogAtTrxCommit ?? "").Trim();
            if (f == "0" || f == "2")
                w.Add("innodb_flush_log_at_trx_commit=" + f + " no my.ini: numa queda de energia ou tela azul se perde até cerca de 1 s de transações já confirmadas" +
                      (f == "0" ? " (com 0, também se o banco cair sozinho)" : "") +
                      " e a recuperação do InnoDB pode falhar, principalmente com o doublewrite desligado. Em servidor sem no-break ou com energia instável usar 1." + risk);
            return w;
        }
    }
}
