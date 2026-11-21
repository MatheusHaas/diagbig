using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DiagBIG.Core;
using DiagBIG.Windows;

namespace DiagBIG.UI
{
    /// <summary>
    /// Reparo do repositório do WMI (erros aleatórios do Windows, ex.: dclvendas.exe no bigupdate), sem reiniciar o Windows:
    /// net stop winmgmt /y, renomeia wbem\Repository para OLD_Repository_AAAAMMDD_HHMM, winmgmt /resetRepository, confere e religa os serviços que dependiam do WMI.
    /// A pasta antiga fica no disco (não é apagada). Sempre tenta religar o WMI e os dependentes, mesmo se algum passo falhar.
    /// </summary>
    internal class WmiRepairForm : Form
    {
        readonly TextBox box = new TextBox();
        readonly Button go = new Button();
        readonly Button copy = new Button();
        readonly Button close = new Button();
        readonly StringBuilder all = new StringBuilder();
        volatile bool busy;

        public WmiRepairForm(Icon icon)
        {
            Text = "Reparar WMI (repositório)";
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(860, 560);
            Font = new Font("Segoe UI", 9F);
            box.Multiline = true; box.ReadOnly = true; box.ScrollBars = ScrollBars.Both; box.WordWrap = false;
            box.Dock = DockStyle.Fill; box.Font = new Font("Consolas", 9.5F); box.BackColor = SystemColors.Window;
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
            go.Text = "Reparar o repositório WMI"; go.AutoSize = true; go.Padding = new Padding(6, 2, 6, 2); go.Click += (s, e) => Go();
            copy.Text = "Copiar"; copy.AutoSize = true;
            copy.Click += (s, e) => { try { Clipboard.SetDataObject(all.ToString(), true, 10, 100); } catch (Exception) { } };
            close.Text = "Fechar"; close.AutoSize = true; close.Click += (s, e) => { if (!busy) Close(); };
            bar.Controls.AddRange(new Control[] { go, copy, close });
            Controls.Add(box); Controls.Add(bar);
            FormClosing += (s, e) => { if (busy) e.Cancel = true; };
            Append("Reparo do repositório do WMI. Procedimento: parar o WMI (net stop winmgmt /y), renomear wbem\\Repository para " + WmiRepo.OldName(DateTime.Now) +
                   ", winmgmt /resetRepository, conferir e religar os serviços que dependiam do WMI.\r\n" +
                   "Não reinicia o Windows. Use fora do movimento: durante o reparo alguns serviços do Windows ficam parados por alguns minutos.\r\n" +
                   "Programas de terceiros (antivírus, agentes do fabricante) podem perder registros WMI próprios. A pasta antiga fica no disco (não é apagada).\r\n\r\n");
        }

        void Append(string t)
        {
            all.Append(t);
            try { if (!IsDisposed) BeginInvoke(new Action(() => box.AppendText(t))); } catch (InvalidOperationException) { }
        }

        void Go()
        {
            if (busy) return;
            if (MessageBox.Show(this,
                    "Isto PARA o WMI do Windows e recria o repositório dele (sem reiniciar o Windows).\r\n\r\n" +
                    "• Faça fora do movimento: serviços que dependem do WMI ficam parados por alguns minutos.\r\n" +
                    "• Programas de terceiros podem perder registros WMI próprios.\r\n" +
                    "• Não use em servidor sem combinar. É para balcões, caixas e terminais.\r\n" +
                    "• Não feche o programa durante o reparo.\r\n\r\nContinuar?", "DiagBIG",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            busy = true; go.Enabled = false; UseWaitCursor = true;
            var th = new Thread(() =>
            {
                int startAt = all.Length;
                try { Run(); }
                catch (Exception ex) { Append("\r\nERRO: " + ex.Message + "\r\n"); }
                try
                {
                    string saved = ProcedureLog.Save("reparar-wmi", SystemInfo.HostName(), all.ToString(startAt, all.Length - startAt));
                    Append("\r\n" + (saved != null ? "Relatório do procedimento salvo em " + saved : "Não consegui salvar o relatório do procedimento.") + "\r\n");
                }
                catch (Exception) { }
                finally { busy = false; try { BeginInvoke(new Action(() => { go.Enabled = true; UseWaitCursor = false; })); } catch (InvalidOperationException) { } }
            });
            th.IsBackground = true;
            th.Start();
        }

        static bool IsRunning(string name)
        {
            try { using (var sc = new ServiceController(name)) { sc.Refresh(); return sc.Status != ServiceControllerStatus.Stopped; } }
            catch (Exception) { return false; }
        }

        void Run()
        {
            string repo = WmiRepository.RepositoryDir();
            string raw; int code;
            Append("== 1) verificando o repositório antes ==\r\n");
            WmiRepoState before = WmiRepository.Verify(out raw, 60000);
            Append("  " + (raw ?? "").Trim().Replace("\r\n", "\r\n  ") + "\r\n  => " + before + "\r\n\r\n");
            if (before == WmiRepoState.Consistente)
                Append("O repositório já está consistente. Seguindo mesmo assim, como no procedimento manual.\r\n\r\n");

            if (!Directory.Exists(repo)) { Append("Pasta " + repo + " não encontrada. Nada a fazer.\r\n"); return; }
            long size = WmiRepository.DirSize(repo);
            string oldDir = Path.Combine(Path.GetDirectoryName(repo), WmiRepo.OldName(DateTime.Now));
            var deps = WmiRepository.RunningDependents();
            Append("Serviços dependentes rodando agora (serão religados): " + (deps.Count == 0 ? "nenhum" : string.Join(", ", deps.ToArray())) + "\r\n\r\n");

            bool renamed = false;
            try
            {
                Append("== 2) parando o WMI (net stop winmgmt /y) ==\r\n");
                StopWmi();
                if (IsRunning("winmgmt")) { Append("O WMI não parou. Nada foi alterado.\r\n"); return; }

                Append("== 3) renomeando o repositório ==\r\n");
                try { Directory.Move(repo, oldDir); renamed = true; Append("  " + repo + "  ->  " + oldDir + "  (" + (size / 1048576) + " MB; fica no disco)\r\n\r\n"); }
                catch (Exception ex) { Append("Não consegui renomear: " + ex.Message + "\r\nNada foi alterado no repositório; religando o WMI.\r\n"); return; }

                Append("== 4) parando de novo, se o WMI religou, e winmgmt /resetRepository ==\r\n");
                if (IsRunning("winmgmt")) StopWmi(); else Append("  o WMI continua parado.\r\n");
                string o = WmiRepository.Run("wbem\\winmgmt.exe", "/resetRepository", 300000, out code);
                Append("  " + o.Trim().Replace("\r\n", "\r\n  ") + "\r\n  (código " + code + ")\r\n\r\n");
            }
            finally
            {
                Append("== 5) religando o WMI e os serviços dependentes ==\r\n");
                StartService("winmgmt");
                foreach (string d in deps) StartService(d); // do menos para o mais dependente
            }

            Append("\r\n== 6) conferindo ==\r\n");
            WmiRepoState after = WmiRepository.Verify(out raw, 120000);
            Append("  " + (raw ?? "").Trim().Replace("\r\n", "\r\n  ") + "\r\n\r\n");
            Append("RESULTADO\r\n---------\r\n");
            if (!renamed) Append("NADA FOI ALTERADO (não deu para renomear o repositório). Tentar de novo ou acionar a equipe técnica.\r\n");
            else if (after == WmiRepoState.Consistente)
                Append("REPOSITÓRIO RECRIADO E CONSISTENTE. Pasta antiga: " + oldDir + " (pode apagar depois de uns dias). Agora rodar de novo bigupdate, acertaestrutura e acertasequenciador. Não precisa reiniciar o Windows" +
                       " (se algum programa ainda reclamar de WMI, reiniciar o Windows).\r\n");
            else Append("O repositório NÃO ficou consistente (" + after + "). Reiniciar o Windows e verificar de novo; se persistir, acionar a equipe técnica. Pasta antiga: " + oldDir + "\r\n");
        }

        void StopWmi()
        {
            int code;
            string o = WmiRepository.Run("net.exe", "stop winmgmt /y", 90000, out code);
            Append("  " + o.Trim().Replace("\r\n", "\r\n  ") + "\r\n");
        }

        void StartService(string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.Running) { Append("  " + name + ": já rodando\r\n"); return; }
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
                    Append("  " + name + ": iniciado\r\n");
                }
            }
            catch (Exception ex) { Append("  " + name + ": não iniciou (" + ex.Message + ")\r\n"); }
        }
    }
}
