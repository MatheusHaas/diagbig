using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.ServiceProcess;
using System.Text;
using DiagBIG.Core;

namespace DiagBIG.Windows
{
    /// <summary>Repositório WMI: verificação (só leitura) e caminhos. O reparo fica em UI\WmiRepairForm.</summary>
    internal static class WmiRepository
    {
        /// <summary>System32 verdadeiro; num processo de 32 bits em Windows de 64, System32 é redirecionado para SysWOW64, então usa Sysnative.</summary>
        public static string System32()
        {
            string win = Environment.GetEnvironmentVariable("windir") ?? @"C:\Windows";
            return Path.Combine(win, !Environment.Is64BitProcess && Environment.Is64BitOperatingSystem ? "Sysnative" : "System32");
        }

        public static string RepositoryDir() { return Path.Combine(System32(), @"wbem\Repository"); }

        /// <summary>Roda um programa do sistema e devolve a saída (stdout+stderr). timeoutMs vencido = mata o processo e exitCode -1.</summary>
        public static string Run(string exe, string args, int timeoutMs, out int exitCode)
        {
            exitCode = -1;
            var sb = new StringBuilder();
            var psi = new ProcessStartInfo(Path.Combine(System32(), exe), args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
                StandardErrorEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            };
            using (Process p = Process.Start(psi))
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } return sb.ToString(); }
                p.WaitForExit();
                exitCode = p.ExitCode;
            }
            return sb.ToString();
        }

        /// <summary>winmgmt /verifyrepository: só lê. Pode demorar uns segundos; 20 s no máximo na coleta.</summary>
        public static WmiRepoState Verify(out string raw, int timeoutMs = 20000)
        {
            try
            {
                int code;
                raw = Run("wbem\\winmgmt.exe", "/verifyrepository", timeoutMs, out code);
                if (code == -1) { raw = "tempo esgotado (" + timeoutMs / 1000 + " s) ao verificar"; return WmiRepoState.NaoVerificado; }
                return WmiRepo.Parse(raw);
            }
            catch (Exception ex) { raw = ex.Message; return WmiRepoState.NaoVerificado; }
        }

        /// <summary>Tamanho de uma pasta em bytes (0 se não der para ler).</summary>
        public static long DirSize(string dir)
        {
            long n = 0;
            try { foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) { try { n += new FileInfo(f).Length; } catch (Exception) { } } }
            catch (Exception) { }
            return n;
        }

        /// <summary>Serviços que dependem do winmgmt e estão rodando (todos os níveis), do mais dependente para o menos.</summary>
        public static List<string> RunningDependents()
        {
            var found = new List<string>();
            try
            {
                using (var sc = new ServiceController("winmgmt")) Collect(sc, found);
            }
            catch (Exception) { }
            return found;
        }

        static void Collect(ServiceController s, List<string> into)
        {
            foreach (ServiceController d in s.DependentServices)
            {
                try
                {
                    if (d.Status != ServiceControllerStatus.Running || into.Contains(d.ServiceName)) continue;
                    into.Add(d.ServiceName);
                    Collect(d, into);
                }
                catch (Exception) { }
            }
        }
    }
}
