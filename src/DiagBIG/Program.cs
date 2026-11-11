using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DiagBIG.Core;
using DiagBIG.UI;
using DiagBIG.Windows;

namespace DiagBIG
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        /// <summary>
        /// DiagBIG.exe                 abre a janela e já coleta (30 dias)
        /// DiagBIG.exe --dias 7        período inicial 7/30/90
        /// DiagBIG.exe --sem-janela    só gera o TXT e sai (código 0 = gravou, 1 = falhou)
        /// </summary>
        [STAThread]
        static int Main(string[] args)
        {
            bool noWindow = false;
            int days = Config.DefaultPeriodDays;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].Trim().ToLowerInvariant();
                if (a == "--sem-janela" || a == "/sem-janela") noWindow = true;
                else if (a.StartsWith("--dias=", StringComparison.Ordinal)) days = ParseDays(a.Substring(7));
                else if (a == "--dias" && i + 1 < args.Length) days = ParseDays(args[++i]);
            }

            if (noWindow)
            {
                CollectionResult r;
                try { r = Collector.Run(days, null); }
                catch (Exception ex) { Print("DiagBIG: erro na coleta: " + ex); return 1; }
                Print(r.SavedPath != null ? "DiagBIG: TXT gerado em " + r.SavedPath : "DiagBIG: " + r.SaveError);
                return r.SavedPath != null ? 0 : 1;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => MessageBox.Show(e.Exception.ToString(), "DiagBIG - erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Run(new MainForm(days));
            return 0;
        }

        static int ParseDays(string s)
        {
            int d;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out d)) return Config.DefaultPeriodDays;
            foreach (int opt in Config.PeriodOptions) if (d <= opt) return opt;
            return Config.MaxPeriodDays;
        }

        static void Print(string msg)
        {
            try
            {
                if (AttachConsole(-1)) Console.WriteLine(Environment.NewLine + msg);
            }
            catch (Exception) { }
        }
    }
}
