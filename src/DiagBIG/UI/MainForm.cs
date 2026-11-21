using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using DiagBIG.Core;
using DiagBIG.Windows;

namespace DiagBIG.UI
{
    /// <summary>Janela única, montada em código (sem Designer/.resx).</summary>
    internal class MainForm : Form
    {
        readonly ComboBox period = new ComboBox();
        readonly Button collect = new Button();
        readonly Button help = new Button();
        readonly Label status = new Label();
        readonly TextBox output = new TextBox();
        readonly Button copyAll = new Button();
        readonly Button toggle = new Button();
        readonly Button copyScreen = new Button();
        readonly Button copySummary = new Button();
        readonly Button openTxt = new Button();
        readonly Button openFolder = new Button();
        readonly Button chkdsk = new Button();
        readonly Button memtest = new Button();
        readonly Button maint = new Button();
        readonly Label copied = new Label();
        readonly System.Windows.Forms.Timer copiedTimer = new System.Windows.Forms.Timer();

        CollectionResult last;
        bool running;
        bool showFull;

        public MainForm(int initialDays)
        {
            Text = "DiagBIG " + Config.Version + " — logs de disco e banco";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1000, 720);
            MinimumSize = new Size(640, 400);
            Font = new Font("Segoe UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            // --- topo ---
            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(4) };
            Image logo = LoadLogo();
            if (logo != null)
                top.Controls.Add(new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(32, 32), Margin = new Padding(3, 0, 6, 0) });
            top.Controls.Add(new Label { Text = "DiagBIG", AutoSize = true, Font = new Font("Segoe UI", 12F, FontStyle.Bold), Margin = new Padding(0, 5, 16, 3) });
            top.Controls.Add(new Label { Text = "Período:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
            period.DropDownStyle = ComboBoxStyle.DropDownList;
            period.Width = 90;
            foreach (int d in Config.PeriodOptions) period.Items.Add(d + " dias");
            period.SelectedIndex = Math.Max(0, Array.IndexOf(Config.PeriodOptions, initialDays));
            period.Margin = new Padding(3, 4, 3, 3);
            top.Controls.Add(period);
            Setup(collect, "Coletar novamente", (s, e) => StartCollect());
            top.Controls.Add(collect);
            Setup(help, "Ajuda (F1)", (s, e) => ShowHelp());
            top.Controls.Add(help);
            status.AutoSize = true;
            status.Margin = new Padding(12, 8, 3, 3);
            top.Controls.Add(status);

            // --- texto ---
            output.Multiline = true;
            output.ReadOnly = true;
            output.ScrollBars = ScrollBars.Both;
            output.WordWrap = false;
            output.MaxLength = 0;
            output.Dock = DockStyle.Fill;
            output.Font = new Font("Consolas", 9.5F);
            output.BackColor = SystemColors.Window;
            output.HideSelection = false;

            // --- rodapé ---
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(4) };
            Setup(toggle, "Mostrar relatório completo", (s, e) => { showFull = !showFull; ShowText(); });
            Setup(copyScreen, "Copiar tela", (s, e) => Copy(output.Text));
            Setup(copySummary, "Copiar resumo", (s, e) => Copy(last == null ? null : last.ResumoText));
            Setup(copyAll, "Copiar tudo", (s, e) => Copy(last == null ? null : last.FullText));
            Setup(openTxt, "Abrir TXT", (s, e) => OpenTxt());
            Setup(openFolder, "Abrir pasta", (s, e) => OpenFolder());
            Setup(chkdsk, "Verificar disco (chkdsk /scan)", (s, e) => RunChkdsk());
            Setup(memtest, "Testar memória (mdsched)", (s, e) => RunMemTest());
            Setup(maint, "Manutenção do banco (force 3307)", (s, e) => RunMaintenance());
            bottom.Controls.AddRange(new Control[] { toggle, copyScreen, copySummary, copyAll, openTxt, openFolder, chkdsk, memtest, maint });
            copied.Text = "Copiado!";
            copied.AutoSize = true;
            copied.ForeColor = Color.DarkGreen;
            copied.Font = new Font(Font, FontStyle.Bold);
            copied.Margin = new Padding(12, 9, 3, 3);
            copied.Visible = false;
            bottom.Controls.Add(copied);
            copiedTimer.Interval = 2000;
            copiedTimer.Tick += (s, e) => { copied.Visible = false; copiedTimer.Stop(); };

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(top, 0, 0);
            layout.Controls.Add(output, 0, 1);
            layout.Controls.Add(bottom, 0, 2);
            Controls.Add(layout);

            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.F1) { e.Handled = true; ShowHelp(); } };
            Shown += (s, e) => StartCollect();
        }

        /// <summary>Logo embutido no .exe (recurso DiagBIG.logo.jpeg). Sem logo, a janela funciona igual.</summary>
        static Image LoadLogo()
        {
            try
            {
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("DiagBIG.logo.jpeg"))
                {
                    if (st == null) return null;
                    using (Image img = Image.FromStream(st))
                        return new Bitmap(img); // cópia independente do stream
                }
            }
            catch (Exception) { return null; }
        }

        static void Setup(Button b, string text, EventHandler click)
        {
            b.Text = text;
            b.AutoSize = true;
            b.Padding = new Padding(6, 2, 6, 2);
            b.Click += click;
        }

        int SelectedDays()
        {
            int i = period.SelectedIndex;
            return i >= 0 && i < Config.PeriodOptions.Length ? Config.PeriodOptions[i] : Config.DefaultPeriodDays;
        }

        void SetBusy(bool busy)
        {
            running = busy;
            collect.Enabled = period.Enabled = chkdsk.Enabled = memtest.Enabled = maint.Enabled = !busy;
            copyAll.Enabled = copySummary.Enabled = copyScreen.Enabled = toggle.Enabled = !busy && last != null;
            openTxt.Enabled = openFolder.Enabled = !busy && last != null && last.SavedPath != null;
            UseWaitCursor = busy;
        }

        void StartCollect()
        {
            if (running) return;
            int days = SelectedDays();
            SetBusy(true);
            status.Text = "Coletando, aguarde...";
            output.Text = "Coletando, aguarde..." + ReportBuilder.NL + ReportBuilder.NL +
                          "Lendo log de eventos, serviços do banco e arquivos .err (últimos " + days + " dias).";

            var t = new Thread(() =>
            {
                CollectionResult r = null;
                Exception error = null;
                try { r = Collector.Run(days, msg => UI(() => status.Text = "Coletando, aguarde... " + msg)); }
                catch (Exception ex) { error = ex; }
                UI(() => Finish(r, error));
            });
            t.IsBackground = true;
            t.Start();
        }

        void UI(Action a)
        {
            try
            {
                if (IsDisposed) return;
                BeginInvoke(a);
            }
            catch (InvalidOperationException) { /* janela fechada */ }
        }

        void Finish(CollectionResult r, Exception error)
        {
            if (error != null)
            {
                output.Text = "Erro inesperado na coleta:" + ReportBuilder.NL + error;
                status.Text = "Erro na coleta.";
                SetBusy(false);
                return;
            }
            last = r;
            showFull = false;
            ShowText();
            status.Text = r.SavedPath != null ? "Pronto. TXT salvo em " + r.SavedPath : "Pronto, mas " + r.SaveError;
            SetBusy(false);
        }

        /// <summary>Mostra o essencial (padrão) ou o relatório completo; o TXT salvo é sempre o completo.</summary>
        void ShowText()
        {
            if (last == null) return;
            output.Text = showFull ? last.FullText : (last.EssentialText ?? last.FullText);
            output.SelectionStart = 0;
            output.SelectionLength = 0;
            output.ScrollToCaret();
            toggle.Text = showFull ? "Mostrar só o essencial" : "Mostrar relatório completo";
        }

        void Copy(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                // Com retentativas: em acesso remoto a área de transferência costuma estar ocupada.
                Clipboard.SetDataObject(text, true, 10, 100);
                copied.Text = "Copiado!";
                copied.ForeColor = Color.DarkGreen;
            }
            catch (Exception ex)
            {
                copied.Text = "Falha ao copiar: " + ex.Message;
                copied.ForeColor = Color.DarkRed;
            }
            copied.Visible = true;
            copiedTimer.Stop();
            copiedTimer.Start();
        }

        void ShowHelp()
        {
            using (var f = new HelpForm(Icon)) f.ShowDialog(this);
        }

        void RunChkdsk()
        {
            if (running) return;
            bool fatal = last != null && last.Data != null && last.Data.ActionPlan.Count > 0 && last.Data.ActionPlan[0].StartsWith("VEREDITO: DISCO COM DEFEITO GRAVE", StringComparison.Ordinal);
            if (fatal && MessageBox.Show(this,
                    "O relatório já indica DISCO COM DEFEITO GRAVE (veja O QUE FAZER). O chkdsk lê o disco inteiro e pode piorar um disco que está morrendo.\r\n\r\n" +
                    "Faça o backup antes. Continuar mesmo assim?", "DiagBIG", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            string dir;
            try { dir = last != null && last.SavedPath != null ? Path.GetDirectoryName(last.SavedPath) : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
            catch (Exception) { dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
            using (var f = new ChkdskForm(Icon, dir, last != null && last.Data != null ? last.Data.ComputerName : SystemInfo.HostName())) f.ShowDialog(this);
        }

        void RunMaintenance()
        {
            if (running) return;
            bool fatal = last != null && last.Data != null && last.Data.ActionPlan.Count > 0 && last.Data.ActionPlan[0].StartsWith("VEREDITO: DISCO COM DEFEITO GRAVE", StringComparison.Ordinal);
            if (MessageBox.Show(this,
                    (fatal ? "ATENÇÃO: o relatório indica DISCO COM DEFEITO GRAVE. Não é caso de reparo de tabela: é caso de trocar o disco (equipe técnica).\r\n\r\n" : "") +
                    "Este assistente é para o caso SÓ TABELAS (N2): ele ajusta o my.ini (force 1 ou 2, porta 3307, doublewrite ON, flush 1), reinicia o serviço do banco e observa a subida por 30 segundos.\r\n\r\n" +
                    "O sistema do cliente deixa de conectar enquanto estiver na porta 3307. O my.ini original é guardado e dá para restaurar. Abrir?", "DiagBIG",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            using (var f = new MaintenanceForm(Icon)) f.ShowDialog(this);
        }

        void RunMemTest()
        {
            if (running) return;
            if (MessageBox.Show(this,
                    "O teste de memória do Windows (mdsched) só roda ANTES do Windows iniciar: o computador precisa REINICIAR.\r\n\r\n" +
                    "• Se for um servidor em uso, combine antes: ele ficará fora do ar durante o teste (de 15 min a algumas horas no modo Estendido).\r\n" +
                    "• Salve o trabalho e avise os usuários.\r\n" +
                    "• Na janela seguinte, escolha \"Reiniciar agora\" ou \"na próxima inicialização\". Para pentes suspeitos, use F1 na tela do teste e escolha Estendido.\r\n" +
                    "• Depois que o Windows voltar, abra o DiagBIG de novo: o relatório mostra o resultado em MEMÓRIA RAM E HARDWARE.\r\n\r\n" +
                    "Abrir o teste de memória agora?", "DiagBIG", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            try { Process.Start(new ProcessStartInfo("mdsched.exe") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, "Não consegui abrir o mdsched.exe (rodar como administrador): " + ex.Message, "DiagBIG", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void OpenTxt()
        {
            if (last == null || last.SavedPath == null) return;
            try { Process.Start("notepad.exe", "\"" + last.SavedPath + "\""); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "DiagBIG", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void OpenFolder()
        {
            if (last == null || last.SavedPath == null) return;
            try { Process.Start("explorer.exe", "/select,\"" + last.SavedPath + "\""); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "DiagBIG", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) copiedTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
