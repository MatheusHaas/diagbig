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
    /// Manutenção do banco para o caso "só tabelas": ajusta o my.ini (force 1 ou 2, porta 3307, doublewrite ON, flush 1), reinicia o serviço
    /// e observa a subida por 30 segundos. NÃO roda OPTIMIZE/ALTER nem os programas do sistema: isso continua com o N2. Cada ação pede confirmação
    /// e o my.ini original fica guardado em "my.ini.diagbig-original" (botão Restaurar).
    /// </summary>
    internal class MaintenanceForm : Form
    {
        const string MaintPort = "3307";
        const int ObserveSeconds = 30;
        const string OriginalSuffix = ".diagbig-original";

        readonly TextBox box = new TextBox();
        readonly ComboBox svcBox = new ComboBox();
        readonly ComboBox forceBox = new ComboBox();
        readonly Button apply = new Button();
        readonly Button remove = new Button();
        readonly Button restore = new Button();
        readonly Button copy = new Button();
        readonly StringBuilder all = new StringBuilder();
        readonly List<DbTarget> targets = new List<DbTarget>();
        volatile bool busy;

        public MaintenanceForm(Icon icon)
        {
            Text = "Manutenção do banco: force 1 ou 2 na porta " + MaintPort;
            if (icon != null) Icon = icon;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(900, 620);
            Font = new Font("Segoe UI", 9F);

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
            top.Controls.Add(new Label { Text = "Serviço:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
            svcBox.DropDownStyle = ComboBoxStyle.DropDownList; svcBox.Width = 220; svcBox.Margin = new Padding(3, 4, 12, 3);
            top.Controls.Add(svcBox);
            top.Controls.Add(new Label { Text = "innodb_force_recovery:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
            forceBox.DropDownStyle = ComboBoxStyle.DropDownList; forceBox.Width = 50; forceBox.Margin = new Padding(3, 4, 3, 3);
            forceBox.Items.AddRange(new object[] { "1", "2" }); forceBox.SelectedIndex = 0;
            top.Controls.Add(forceBox);

            box.Multiline = true; box.ReadOnly = true; box.ScrollBars = ScrollBars.Both; box.WordWrap = false;
            box.Dock = DockStyle.Fill; box.Font = new Font("Consolas", 9.5F); box.BackColor = SystemColors.Window;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
            Btn(apply, "1) Aplicar force e testar subida (" + ObserveSeconds + " s)", (s, e) => Go(Mode.Apply));
            Btn(remove, "2) Tirar o force e testar (porta " + MaintPort + ")", (s, e) => Go(Mode.RemoveForce));
            Btn(restore, "Restaurar my.ini original", (s, e) => Go(Mode.Restore));
            Btn(copy, "Copiar", (s, e) => { try { Clipboard.SetDataObject(all.ToString(), true, 10, 100); } catch (Exception) { } });
            var close = new Button { Text = "Fechar", AutoSize = true };
            close.Click += (s, e) => { if (!busy) Close(); };
            bar.Controls.AddRange(new Control[] { apply, remove, restore, copy, close });

            Controls.Add(box); Controls.Add(bar); Controls.Add(top);
            FormClosing += (s, e) => { if (busy) e.Cancel = true; };
            Shown += (s, e) => Discover();
        }

        static void Btn(Button b, string text, EventHandler click) { b.Text = text; b.AutoSize = true; b.Padding = new Padding(6, 2, 6, 2); b.Click += click; }

        enum Mode { Apply, RemoveForce, Restore }

        void Append(string text)
        {
            all.Append(text);
            try { if (!IsDisposed) BeginInvoke(new Action(() => box.AppendText(text))); } catch (InvalidOperationException) { }
        }

        void SetBusy(bool b)
        {
            busy = b;
            try { BeginInvoke(new Action(() => { apply.Enabled = remove.Enabled = restore.Enabled = svcBox.Enabled = forceBox.Enabled = !b; UseWaitCursor = b; })); }
            catch (InvalidOperationException) { }
        }

        void Discover()
        {
            Append("Este assistente SÓ mexe no my.ini e no serviço do banco. OPTIMIZE/ALTER TABLE e os programas (bigupdate, acertaestrutura, acertasequenciador) continuam com o N2.\r\n" +
                   "Cada ação pede confirmação. O my.ini original fica guardado ao lado dele como \"" + "my.ini" + OriginalSuffix + "\".\r\n\r\n");
            try
            {
                foreach (DbService svc in DbDiscovery.FindServices())
                {
                    DbTarget t = DbDiscovery.ResolveForService(svc);
                    targets.Add(t);
                    svcBox.Items.Add(svc.Name + " (" + svc.Status + (svc.StartType == "DESATIVADO" ? ", DESATIVADO" : "") + ")");
                }
            }
            catch (Exception ex) { Append("Não consegui listar os serviços do banco: " + ex.Message + "\r\n"); }
            if (targets.Count == 0) { Append("Nenhum serviço MySQL/MariaDB encontrado neste Windows.\r\n"); apply.Enabled = remove.Enabled = restore.Enabled = false; return; }
            svcBox.SelectedIndex = 0;
            svcBox.Enabled = targets.Count > 1;
            DbTarget first = targets[0];
            Append("Serviço: " + first.Service.Name + " | my.ini: " + (first.IniPath ?? "NÃO ENCONTRADO") + " | .err: " + (first.ErrPath ?? "não encontrado") + "\r\n");
            if (first.IniPath == null) { Append("Sem my.ini não dá para ajustar por aqui: o N2 cria o my.ini à mão.\r\n"); apply.Enabled = remove.Enabled = restore.Enabled = false; }
        }

        void Go(Mode mode)
        {
            if (busy || svcBox.SelectedIndex < 0) return;
            DbTarget t = targets[svcBox.SelectedIndex];
            string force = (string)forceBox.SelectedItem;
            busy = true;
            var th = new Thread(() =>
            {
                int startAt = all.Length;
                try { Run(t, mode, force); } catch (Exception ex) { Append("\r\nERRO: " + ex.Message + "\r\n"); }
                try { SaveLog(t, mode, startAt); } catch (Exception) { }
                SetBusy(false);
            });
            th.IsBackground = true;
            SetBusy(true);
            th.Start();
        }

        /// <summary>Grava o que esta ação fez e o estado final (linhas do my.ini que importam, serviço e fim do .err) para o suporte conferir depois.</summary>
        void SaveLog(DbTarget t, Mode mode, int startAt)
        {
            var sb = new StringBuilder();
            sb.Append(all.ToString(startAt, all.Length - startAt));
            sb.Append("\r\n==== ESTADO FINAL ====\r\n");
            try
            {
                sb.Append("my.ini: ").Append(t.IniPath).Append("\r\n");
                foreach (string l in File.ReadAllLines(t.IniPath))
                    if (System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*(port|innodb_force_recovery|innodb_doublewrite|skip.innodb.doublewrite|innodb_flush_log_at_trx_commit)\s*(=|$)|DiagBIG"))
                        sb.Append("  | ").Append(l).Append("\r\n");
                sb.Append("backup .diagbig-original: ").Append(File.Exists(t.IniPath + OriginalSuffix) ? "existe" : "não existe").Append("\r\n");
            }
            catch (Exception ex) { sb.Append("(não consegui ler o my.ini: ").Append(ex.Message).Append(")\r\n"); }
            try { using (var sc = new ServiceController(t.Service.Name)) { sc.Refresh(); sb.Append("serviço ").Append(t.Service.Name).Append(": ").Append(sc.Status).Append("\r\n"); } }
            catch (Exception ex) { sb.Append("(serviço: ").Append(ex.Message).Append(")\r\n"); }
            sb.Append("\n==== FIM DO .err (últimas 30 linhas) ").Append(t.ErrPath).Append(" ====\r\n").Append(ProcedureLog.Tail(t.ErrPath, 30)).Append("\r\n");
            string saved = ProcedureLog.Save("manutencao-banco-" + (mode == Mode.Apply ? "aplicar" : mode == Mode.RemoveForce ? "tirar-force" : "restaurar"), SystemInfo.HostName(), sb.ToString());
            Append("\r\n" + (saved != null ? "Relatório do procedimento salvo em " + saved : "Não consegui salvar o relatório do procedimento.") + "\r\n");
        }

        bool Confirm(string text)
        {
            DialogResult r = DialogResult.No;
            try { Invoke(new Action(() => r = MessageBox.Show(this, text, "DiagBIG", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2))); }
            catch (InvalidOperationException) { }
            return r == DialogResult.Yes;
        }

        static string ReadIni(string path, out Encoding enc, out bool bom)
        {
            byte[] b = File.ReadAllBytes(path);
            bom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
            int skip = bom ? 3 : 0;
            try { enc = new UTF8Encoding(false, true); return enc.GetString(b, skip, b.Length - skip); }
            catch (Exception) { enc = Encoding.GetEncoding(1252); return enc.GetString(b, skip, b.Length - skip); }
        }

        static void WriteIni(string path, string text, Encoding enc, bool bom)
        {
            byte[] body = (enc is UTF8Encoding ? new UTF8Encoding(false) : enc).GetBytes(text);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                if (bom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
                fs.Write(body, 0, body.Length);
            }
        }

        void Run(DbTarget t, Mode mode, string force)
        {
            string ini = t.IniPath, svcName = t.Service.Name, original = ini + OriginalSuffix;
            Append("\r\n================ " + (mode == Mode.Apply ? "APLICAR FORCE " + force : mode == Mode.RemoveForce ? "TIRAR O FORCE" : "RESTAURAR O MY.INI ORIGINAL") + " ================\r\n");

            if (ServiceDisabled(svcName))
            {
                Append("O serviço " + svcName + " está DESATIVADO: o DiagBIG NÃO ativa nem altera o serviço. Nada foi alterado.\r\n" +
                       "Confirmar se não consta troca de servidor recente: pode ser um serviço antigo que ficou depois de uma troca de banco. NÃO ativar.\r\n");
                return;
            }

            string newText = null; Encoding enc = null; bool bom = false;
            if (mode == Mode.Restore)
            {
                if (!File.Exists(original)) { Append("Não há " + original + ": nada para restaurar.\r\n"); return; }
                if (!Confirm("Restaurar o my.ini original (" + original + ") e reiniciar o serviço " + svcName + "?")) { Append("Cancelado.\r\n"); return; }
            }
            else
            {
                string cur = ReadIni(ini, out enc, out bom);
                List<string> changes;
                newText = MyIniEditor.Apply(cur, mode == Mode.Apply ? MyIniEditor.MaintenanceWithForce(force, MaintPort) : MyIniEditor.RemoveForce(MaintPort), DateTime.Now, out changes);
                string what = changes.Count == 0 ? "(o my.ini já está assim; só reinicia o serviço)" : string.Join("\r\n", changes.Select(c => "  " + c).ToArray());
                if (!Confirm("Serviço: " + svcName + "\r\nmy.ini: " + ini + "\r\n\r\nAlterações:\r\n" + what + "\r\n\r\n" +
                             "O serviço será PARADO e iniciado de novo na porta " + MaintPort + "; o sistema do cliente não conecta nesse período. Continuar?")) { Append("Cancelado.\r\n"); return; }
                foreach (string c in changes) Append("  " + c + "\r\n");
            }

            using (var sc = new ServiceController(svcName))
            {
                sc.Refresh();
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    Append("Parando o serviço " + svcName + "...\r\n");
                    try { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(90)); }
                    catch (Exception ex) { Append("Não consegui parar o serviço: " + ex.Message + ". Nada foi alterado no my.ini.\r\n"); return; }
                }
                if (mode == Mode.Restore) { try { File.Copy(ini, ini + ".diagbig-antes-de-restaurar", true); Append("Guardei o my.ini atual em " + ini + ".diagbig-antes-de-restaurar (caso alguém o tenha editado).\r\n"); } catch (Exception) { } File.Copy(original, ini, true); try { File.Delete(original); Append("my.ini original restaurado; o arquivo de backup foi apagado (o próximo teste guarda um novo).\r\n"); } catch (Exception) { } }
                else
                {
                    // O backup só nasce agora (serviço já parado, tudo pronto para gravar): se algo falhar antes, não sobra backup velho.
                    // Backup velho (o my.ini atual não tem marca do DiagBIG = não foi mexido por nós): é de outro atendimento, troca pelo atual.
                    if (mode == Mode.Apply && (!File.Exists(original) || !ReadIni(ini, out enc, out bom).Contains("# DiagBIG")))
                    { File.Copy(ini, original, true); Append("Backup do original: " + original + "\r\n"); }
                    WriteIni(ini, newText, enc, bom); Append("my.ini gravado.\r\n");
                }

                long errPos = 0;
                try { if (t.ErrPath != null && File.Exists(t.ErrPath)) errPos = new FileInfo(t.ErrPath).Length; } catch (Exception) { }

                Append("Iniciando o serviço e observando por " + ObserveSeconds + " segundos...\r\n");
                bool startFailed = false;
                try { sc.Start(); } catch (Exception ex) { Append("Falha ao iniciar: " + ex.Message + "\r\n"); startFailed = true; }

                DateTime end = DateTime.Now.AddSeconds(ObserveSeconds);
                bool everRunning = false; int drops = 0; string last = "";
                ServiceControllerStatus prev = ServiceControllerStatus.Stopped;
                while (DateTime.Now < end && !startFailed)
                {
                    Thread.Sleep(500);
                    ServiceControllerStatus st;
                    try { sc.Refresh(); st = sc.Status; } catch (Exception) { continue; }
                    if (st == ServiceControllerStatus.Running) everRunning = true;
                    if (everRunning && prev == ServiceControllerStatus.Running && st != ServiceControllerStatus.Running) drops++;
                    prev = st;
                    string line = st.ToString();
                    if (line != last) { Append("  " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " serviço " + line + "\r\n"); last = line; }
                    if (st == ServiceControllerStatus.Stopped && !everRunning && (end - DateTime.Now).TotalSeconds < ObserveSeconds - 8) break; // nunca subiu
                    if (drops >= 2) break;
                }
                bool stable = false;
                try { sc.Refresh(); stable = !startFailed && everRunning && drops == 0 && sc.Status == ServiceControllerStatus.Running; } catch (Exception) { }
                Report(t, errPos, stable, everRunning, drops, mode);
            }
        }

        static string ReadErrFrom(string path, long pos)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (pos > fs.Length) pos = 0; // .err novo/rotacionado
                fs.Position = pos;
                var ms = new MemoryStream(); fs.CopyTo(ms);
                string e; byte[] b = ms.ToArray();
                return ErrTailReader.Decode(b, 0, b.Length, out e);
            }
        }

        static bool ServiceDisabled(string name)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name, false))
                    return k != null && k.GetValue("Start") is int && (int)k.GetValue("Start") == 4;
            }
            catch (Exception) { return false; }
        }

        static readonly string[] NotOnlyTable = { "Assertion failure", "got exception", "got signal", "init function returned error", "Unable to read page", "redo log", "ib_logfile", "ibdata1", "Plugin 'InnoDB' init" };

        void Report(DbTarget t, long errPos, bool stable, bool everRunning, int drops, Mode mode)
        {
            var tables = new List<string>(); var other = new List<string>();
            if (t.ErrPath != null)
            {
                try
                {
                    string txt = ReadErrFrom(t.ErrPath, errPos);
                    ErrAnalysis a = ErrLogAnalyzer.Analyze(txt, DateTime.MinValue);
                    // Tabelas: .err recente inteiro (até 1 MB do fim), pois os erros de tabela costumam ser de antes desta subida.
                    long fl = new FileInfo(t.ErrPath).Length;
                    ErrAnalysis recent = ErrLogAnalyzer.Analyze(ReadErrFrom(t.ErrPath, Math.Max(0, fl - 1048576)), DateTime.MinValue);
                    tables = Diagnosis.DamagedTables(recent.Occurrences);
                    foreach (Occurrence o in a.Occurrences)
                        foreach (string term in NotOnlyTable)
                            if ((o.Message ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 && !other.Contains(term)) other.Add(term);
                    Append("\r\n.err desta subida: " + a.LineCount + " linha(s) lidas.\r\n");
                }
                catch (Exception ex) { Append("\r\nNão consegui ler o .err: " + ex.Message + "\r\n"); }
            }
            Append("\r\nRESULTADO\r\n---------\r\n");
            if (mode == Mode.Restore) Append(stable ? "my.ini original restaurado e o serviço subiu e ficou estável.\r\n" : "my.ini original restaurado, mas o serviço NÃO ficou estável: ver o .err.\r\n");
            else if (stable)
                Append("SUBIU E FICOU ESTÁVEL por " + ObserveSeconds + " s" + (mode == Mode.Apply ? " com force na porta " + MaintPort + ". Próximo: o N2 faz OPTIMIZE/ALTER TABLE nas tabelas abaixo (ainda com force)." :
                       ". Deixar mais alguns minutos observando o .err; se operar normal, o N2 volta a porta para 3306 e roda bigupdate, acertaestrutura e acertasequenciador.") + "\r\n");
            else if (!everRunning) Append("NÃO SUBIU. " + (mode == Mode.Apply ? "Tentar force 2 (se foi 1). Se não subir com 2: EQUIPE TÉCNICA (não usar 3 ou mais)." : "Voltar o force (botão 1) e repetir o reparo, ou EQUIPE TÉCNICA.") + "\r\n");
            else Append("SUBIU MAS CAIU (" + drops + " queda(s) em " + ObserveSeconds + " s): loop de subir e cair. " + (mode == Mode.Apply ? "Tentar force 2 (se foi 1); se continuar: não é só tabela, EQUIPE TÉCNICA." : "Voltar o force e repetir o reparo; se continuar, EQUIPE TÉCNICA.") + "\r\n");
            if (tables.Count > 0 && mode == Mode.Apply)
            {
                Append("Tabelas com corrupção no .err recente (" + tables.Count + "):\r\n");
                foreach (string tb in tables.Take(50)) Append("  " + tb + "\r\n");
                Append("N2: OPTIMIZE TABLE <tabela>; ou ALTER TABLE <tabela> ENGINE=InnoDB; só nelas.\r\n");
            }
            if (other.Count > 0)
                Append("ATENÇÃO: o .err desta subida cita " + string.Join(", ", other.ToArray()) + ": isso NÃO é só tabela. Não insistir: EQUIPE TÉCNICA.\r\n");
        }
    }
}
