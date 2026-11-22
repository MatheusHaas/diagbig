using System;
using System.Drawing;
using System.Windows.Forms;

namespace DiagBIG.UI
{
    /// <summary>
    /// Segunda confirmação de uma ação perigosa: a pessoa precisa DIGITAR uma palavra (aqui, o nome do PC). Não é senha: é uma trava contra
    /// clique automático e contra estar na máquina errada (o suporte remoto alterna entre vários clientes). O botão só habilita quando o
    /// texto digitado é igual (sem diferenciar maiúsculas de minúsculas).
    /// </summary>
    internal class ConfirmTypeForm : Form
    {
        readonly TextBox input = new TextBox();
        readonly Button ok = new Button();

        ConfirmTypeForm(Icon icon, string title, string message, string word)
        {
            Text = title;
            if (icon != null) Icon = icon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(520, 250);

            var msg = new Label { Text = message, Left = 14, Top = 12, Width = 492, Height = 130, AutoSize = false };
            var ask = new Label { Text = "Para confirmar, digite o nome deste PC:  " + word, Left = 14, Top = 150, Width = 492, Height = 20, Font = new Font(Font, FontStyle.Bold) };
            input.Left = 14; input.Top = 176; input.Width = 492;
            ok.Text = "Confirmar"; ok.Left = 326; ok.Top = 210; ok.Width = 85; ok.Enabled = false; ok.DialogResult = DialogResult.OK;
            var cancel = new Button { Text = "Cancelar", Left = 421, Top = 210, Width = 85, DialogResult = DialogResult.Cancel };
            input.TextChanged += (s, e) => ok.Enabled = string.Equals(input.Text.Trim(), word.Trim(), StringComparison.OrdinalIgnoreCase);
            Controls.AddRange(new Control[] { msg, ask, input, ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
            Shown += (s, e) => input.Focus();
        }

        /// <summary>true = digitou a palavra certa e confirmou.</summary>
        public static bool Ask(IWin32Window owner, Icon icon, string title, string message, string word)
        {
            using (var f = new ConfirmTypeForm(icon, title, message, word))
                return f.ShowDialog(owner) == DialogResult.OK;
        }
    }
}
