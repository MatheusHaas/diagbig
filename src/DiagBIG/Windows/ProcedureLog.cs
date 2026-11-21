using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DiagBIG.Core;

namespace DiagBIG.Windows
{
    /// <summary>
    /// Grava um TXT com tudo o que um procedimento (manutenção do banco, reparo do WMI) fez e o resultado, em DiagBIG_relatorios, ao lado do .exe
    /// (ou na Área de Trabalho). Nome "Procedimento_*": a limpeza dos relatórios antigos (DiagBIG_*.txt) não apaga esses arquivos.
    /// </summary>
    internal static class ProcedureLog
    {
        public static string Save(string procedure, string host, string body)
        {
            var dirs = new List<string>();
            try { dirs.Add(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)); } catch (Exception) { }
            try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch (Exception) { }
            string name = "Procedimento_" + procedure + "_" + host + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt";
            string head = "DIAGBIG " + Config.Version + " - procedimento: " + procedure + " - PC " + host + " - " +
                          DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " - usuário " + Environment.UserName + "\r\n" +
                          new string('=', 78) + "\r\n";
            foreach (string dir in dirs.Where(x => !string.IsNullOrEmpty(x)))
            {
                try
                {
                    string folder = Path.Combine(dir, Config.ReportsFolder);
                    Directory.CreateDirectory(folder);
                    string path = Path.Combine(folder, name);
                    File.WriteAllText(path, head + body, new UTF8Encoding(true));
                    return path;
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>Últimas linhas de um arquivo de texto (para anexar o fim do .err ao relatório do procedimento).</summary>
        public static string Tail(string path, int lines)
        {
            try
            {
                if (path == null || !File.Exists(path)) return "(arquivo não encontrado)";
                ErrReadResult r = ErrTailReader.ReadTail(path, 64 * 1024);
                string[] all = (r.Text ?? "").Replace("\r\n", "\n").Split('\n');
                return string.Join("\r\n", all.Skip(Math.Max(0, all.Length - lines)).ToArray());
            }
            catch (Exception ex) { return "(não consegui ler: " + ex.Message + ")"; }
        }
    }
}
