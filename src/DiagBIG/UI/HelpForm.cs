using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using DiagBIG.Core;

namespace DiagBIG.UI
{
    /// <summary>Janela do botão Ajuda: mostra o manual embutido no .exe (docs/MANUAL.txt).</summary>
    internal class HelpForm : Form
    {
        public HelpForm(Icon icon)
        {
            Text = "DiagBIG " + Config.Version + " — Ajuda";
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(760, 680);
            MinimumSize = new Size(480, 320);
            Font = new Font("Segoe UI", 9F);
            ShowInTaskbar = false;
            KeyPreview = true;

            var text = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                MaxLength = 0,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10F),
                BackColor = SystemColors.Window,
                Text = LoadManual(),
            };

            var close = new Button { Text = "Fechar", AutoSize = true, Padding = new Padding(10, 2, 10, 2), Anchor = AnchorStyles.Right };
            close.Click += (s, e) => Close();
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
            bottom.Controls.Add(close);

            Controls.Add(text);
            Controls.Add(bottom);
            CancelButton = close;
            Shown += (s, e) => { text.SelectionStart = 0; text.SelectionLength = 0; close.Focus(); };
        }

        static string LoadManual()
        {
            try
            {
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("DiagBIG.manual.txt"))
                {
                    if (st == null) return "Manual não encontrado neste executável.";
                    using (var r = new StreamReader(st, Encoding.UTF8, true))
                        return r.ReadToEnd().Replace("\r\n", "\n").Replace("\n", "\r\n");
                }
            }
            catch (Exception ex) { return "Não foi possível abrir o manual: " + ex.Message; }
        }
    }
}
