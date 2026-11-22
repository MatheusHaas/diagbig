using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DiagBIG.Core;

namespace DiagBIG.UI
{
    /// <summary>Roda "chkdsk X: /scan" (só leitura) em cada disco fixo, mostra a saída e grava um TXT ao lado do relatório.</summary>
    internal class ChkdskForm : Form
    {
        readonly TextBox box = new TextBox();
        readonly Button cancel = new Button();
        readonly Button copy = new Button();
        readonly string saveDir;
        readonly string host;
        readonly StringBuilder all = new StringBuilder();
        Process current;
        volatile bool cancelled;
        bool done;

        public ChkdskForm(Icon icon, string saveDir, string host)
        {
            this.saveDir = saveDir; this.host = host;
            Text = "Verificar disco (chkdsk /scan, só leitura)";
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(860, 560);
            Font = new Font("Segoe UI", 9F);
            box.Multiline = true; box.ReadOnly = true; box.ScrollBars = ScrollBars.Vertical; box.WordWrap = true;
            box.Dock = DockStyle.Fill; box.Font = new Font("Consolas", 9.5F); box.BackColor = SystemColors.Window;
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
            cancel.Text = "Cancelar"; cancel.AutoSize = true; cancel.Click += (s, e) => { if (done) Close(); else Cancel(); };
            copy.Text = "Copiar"; copy.AutoSize = true; copy.Enabled = false;
            copy.Click += (s, e) => { try { Clipboard.SetDataObject(all.ToString(), true, 10, 100); } catch (Exception) { } };
            bar.Controls.Add(cancel); bar.Controls.Add(copy);
            Controls.Add(box); Controls.Add(bar);
            Shown += (s, e) => Start();
            FormClosing += (s, e) => { if (!done) Cancel(); };
        }

        void Cancel()
        {
            cancelled = true;
            try { if (current != null && !current.HasExited) current.Kill(); } catch (Exception) { }
        }

        void Append(string text)
        {
            all.Append(text);
            try { if (!IsDisposed) BeginInvoke(new Action(() => { box.AppendText(text); })); } catch (InvalidOperationException) { }
        }

        void Start()
        {
            var t = new Thread(Run) { IsBackground = true };
            t.Start();
        }

        void Run()
        {
            var results = new List<string>();
            try
            {
                var drives = DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady).Select(x => x.Name.Substring(0, 2)).ToList();
                Append("chkdsk /scan lê o disco e NÃO corrige nada. Pode demorar. Discos: " + string.Join(", ", drives.ToArray()) + "\r\n\r\n");
                int oem = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                foreach (string drive in drives)
                {
                    if (cancelled) break;
                    Append("===== chkdsk " + drive + " /scan =====\r\n");
                    int code = -1;
                    try
                    {
                        var psi = new ProcessStartInfo("chkdsk.exe", drive + " /scan")
                        {
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true,
                            StandardOutputEncoding = Encoding.GetEncoding(oem),
                        };
                        current = Process.Start(psi);
                        current.OutputDataReceived += (s, e) => { if (e.Data != null) Append(e.Data + "\r\n"); };
                        current.ErrorDataReceived += (s, e) => { if (e.Data != null) Append(e.Data + "\r\n"); };
                        current.BeginOutputReadLine(); current.BeginErrorReadLine();
                        current.WaitForExit();
                        code = cancelled ? -2 : current.ExitCode;
                    }
                    catch (Exception ex) { Append("Falha ao executar o chkdsk: " + ex.Message + "\r\n"); }
                    if (cancelled) { results.Add(drive + " CANCELADO."); break; }
                    results.Add(ChkdskScan.Verdict(drive, code));
                    Append("\r\n");
                }
            }
            catch (Exception ex) { Append("Erro: " + ex.Message + "\r\n"); }

            string summary = "RESULTADO\r\n---------\r\n" + string.Join("\r\n", results.ToArray()) + "\r\n";
            string head = "DIAGBIG — chkdsk /scan em " + host + " (" + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture) + ")\r\n\r\n";
            string full = head + summary + "\r\n" + all;
            string saved = null;
            try
            {
                string name = "DiagBIG_chkdsk_" + host + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".txt";
                saved = Path.Combine(saveDir, name);
                File.WriteAllText(saved, full, new UTF8Encoding(true));
            }
            catch (Exception) { saved = null; }
            Append("\r\n" + summary + (saved != null ? "TXT salvo em " + saved + "\r\n" : "Não consegui salvar o TXT.\r\n"));
            done = true;
            try
            {
                BeginInvoke(new Action(() => { cancel.Text = "Fechar"; copy.Enabled = true; }));
            }
            catch (InvalidOperationException) { }
        }
    }
}
