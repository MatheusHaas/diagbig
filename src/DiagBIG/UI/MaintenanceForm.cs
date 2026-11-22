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
    /// Manutenção do banco para o caso "só tabelas": ajusta o my.ini (force 1 ou 2, porta de manutenção, doublewrite ON, flush 1), reinicia o
    /// serviço e observa a subida por 30 segundos. NÃO roda OPTIMIZE/ALTER nem os programas do sistema: isso continua com o N2.
    /// A porta de manutenção é a 3307, ou a próxima livre quando o cliente já usa a 3307 (ver MaintenancePort); se o serviço não sobe por
    /// porta em uso, tenta a seguinte. Aplicar o force exige DUAS confirmações (a segunda é digitar o nome do PC); o my.ini original fica
    /// guardado em "my.ini.diagbig-original" (botão Restaurar).
    /// </summary>
    internal class MaintenanceForm : Form
    {
        const int ObserveSeconds = 30;
        const int MaxPortRetries = 3;
        const string OriginalSuffix = ".diagbig-original";

        // porta do sistema do cliente e porta usada na manutenção (definidas a cada ação, em Run; lidas pelo Report).
        int clientPort = MaintenancePort.MySqlDefault, maintPort = MaintenancePort.Preferred;

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
            Text = "Manutenção do banco: force 1 ou 2 em porta de manutenção (3307 ou a próxima livre)";
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

            box.Multiline = true; box.ReadOnly = true; box.ScrollBars = ScrollBars.Vertical; box.WordWrap = true;
            box.Dock = DockStyle.Fill; box.Font = new Font("Consolas", 9.5F); box.BackColor = SystemColors.Window;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
            Btn(apply, "1) Aplicar force e testar subida (" + ObserveSeconds + " s)", (s, e) => Go(Mode.Apply));
            Btn(remove, "2) Tirar o force e testar (porta de manutenção)", (s, e) => Go(Mode.RemoveForce));
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
                   "Aplicar o force pede DUAS confirmações (a segunda é digitar o nome do PC). O my.ini original fica guardado ao lado dele como \"" + "my.ini" + OriginalSuffix + "\".\r\n" +
                   "A porta de manutenção é a " + MaintenancePort.Preferred + "; se o cliente já usa a " + MaintenancePort.Preferred + " (ou ela está ocupada), o assistente sobe para a próxima livre.\r\n\r\n");
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

        /// <summary>Segunda confirmação: a pessoa digita o nome do PC (ver ConfirmTypeForm).</summary>
        bool ConfirmTyped(string text, string word)
        {
            bool r = false;
            try { Invoke(new Action(() => r = ConfirmTypeForm.Ask(this, Icon, "DiagBIG — confirmação 2 de 2", text, word))); }
            catch (InvalidOperationException) { }
            return r;
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
                Encoding origEnc; bool origBom;
                string orig = File.Exists(original) ? ReadIni(original, out origEnc, out origBom) : null;

                // Porta: a do cliente nunca pode ser a de manutenção (senão o sistema do cliente continua conectando no banco com force).
                PortPlan plan = MaintenancePort.Plan(cur, orig, ListeningPorts());
                if (plan.MaintPort == 0)
                {
                    Append("Não achei porta livre entre " + MaintenancePort.Preferred + " e " + MaintenancePort.Last + " para a manutenção. Nada foi alterado.\r\n");
                    return;
                }
                clientPort = plan.ClientPort; maintPort = plan.MaintPort;
                Append("Porta do sistema do cliente: " + clientPort + " | porta de manutenção: " + maintPort + (plan.Reused ? " (já aplicada antes)" : "") + "\r\n");
                foreach (string n in plan.Notes) Append("  " + n + "\r\n");

                List<string> changes;
                newText = MyIniEditor.Apply(cur, mode == Mode.Apply ? MyIniEditor.MaintenanceWithForce(force, Port()) : MyIniEditor.RemoveForce(Port()), DateTime.Now, out changes);
                string what = changes.Count == 0 ? "(o my.ini já está assim; só reinicia o serviço)" : string.Join("\r\n", changes.Select(c => "  " + c).ToArray());
                string portInfo = "Porta: o sistema do cliente usa a " + clientPort + "; durante a manutenção o banco fica na " + maintPort + ".\r\n" +
                                  (plan.Notes.Count > 0 && !plan.Reused ? string.Join("\r\n", plan.Notes.ToArray()) + "\r\n" : "");
                bool twoSteps = mode == Mode.Apply;
                if (!Confirm((twoSteps ? "CONFIRMAÇÃO 1 DE 2\r\n\r\n" : "") + "Serviço: " + svcName + "\r\nmy.ini: " + ini + "\r\n\r\n" + portInfo + "\r\nAlterações:\r\n" + what + "\r\n\r\n" +
                             "O serviço será PARADO e iniciado de novo na porta " + maintPort + "; o sistema do cliente não conecta nesse período. Continuar?")) { Append("Cancelado" + (twoSteps ? " na confirmação 1 de 2" : "") + ".\r\n"); return; }
                Append("Confirmação 1" + (twoSteps ? " de 2" : "") + ": confirmada.\r\n");
                if (twoSteps)
                {
                    string pc = SystemInfo.HostName();
                    if (!ConfirmTyped("ÚLTIMA CONFIRMAÇÃO (2 DE 2)\r\n\r\nVai PARAR o serviço " + svcName + " e subir o banco com innodb_force_recovery=" + force + " na porta " + maintPort + ".\r\n" +
                                      "O sistema do cliente fica SEM conexão com o banco até o reparo terminar e a porta voltar para " + clientPort + ".\r\n\r\n" +
                                      "Só faça com o cliente fora do movimento, programas fechados e o N2 de acordo.", pc)) { Append("Cancelado na confirmação 2 de 2.\r\n"); return; }
                    Append("Confirmação 2 de 2: digitou o nome do PC (" + pc + ").\r\n");
                }
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

                for (int attempt = 0; ; attempt++)
                {
                    long errPos = 0;
                    try { if (t.ErrPath != null && File.Exists(t.ErrPath)) errPos = new FileInfo(t.ErrPath).Length; } catch (Exception) { }

                    Append("Iniciando o serviço" + (mode == Mode.Restore ? "" : " na porta " + maintPort) + " e observando por " + ObserveSeconds + " segundos...\r\n");
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

                    // Não subiu e o .err diz que a porta está ocupada: sobe mais um número e tenta de novo (o serviço já está parado).
                    if (!everRunning && mode != Mode.Restore && attempt < MaxPortRetries && PortInUse(t.ErrPath, errPos))
                    {
                        int next = NextFreePort(maintPort + 1);
                        if (next > 0)
                        {
                            Append("O .err indica que a porta " + maintPort + " está ocupada. Tentando a porta " + next + " (tentativa " + (attempt + 2) + " de " + (MaxPortRetries + 1) + ")...\r\n");
                            try
                            {
                                sc.Refresh();
                                if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)); }
                                List<string> again;
                                string text = MyIniEditor.Apply(ReadIni(ini, out enc, out bom), mode == Mode.Apply ? MyIniEditor.MaintenanceWithForce(force, next.ToString(CultureInfo.InvariantCulture)) : MyIniEditor.RemoveForce(next.ToString(CultureInfo.InvariantCulture)), DateTime.Now, out again);
                                WriteIni(ini, text, enc, bom);
                                foreach (string c in again) Append("  " + c + "\r\n");
                                maintPort = next;
                                continue;
                            }
                            catch (Exception ex) { Append("Não consegui trocar a porta: " + ex.Message + "\r\n"); }
                        }
                        else Append("Não achei outra porta livre até " + MaintenancePort.Last + ".\r\n");
                    }
                    Report(t, errPos, stable, everRunning, drops, mode);
                    break;
                }
            }
        }

        string Port() { return maintPort.ToString(CultureInfo.InvariantCulture); }

        /// <summary>Portas TCP em que algum programa já está escutando neste Windows.</summary>
        static HashSet<int> ListeningPorts()
        {
            var set = new HashSet<int>();
            try { foreach (System.Net.IPEndPoint ep in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()) set.Add(ep.Port); }
            catch (Exception) { }
            return set;
        }

        /// <summary>Primeira porta livre a partir de "from" (pula a do cliente e as que têm programa escutando); 0 = nenhuma.</summary>
        int NextFreePort(int from)
        {
            HashSet<int> busy = ListeningPorts();
            for (int p = from; p <= MaintenancePort.Last; p++)
                if (p != clientPort && !busy.Contains(p)) return p;
            return 0;
        }

        static readonly string[] PortInUseTerms = { "Address already in use", "Do you already have another mysqld server running on port", "Can't start server: Bind on TCP/IP port",
                                                   "Only one usage of each socket address", "WSAEADDRINUSE" };

        /// <summary>O .err gerado desde errPos fala de porta ocupada?</summary>
        static bool PortInUse(string errPath, long errPos)
        {
            if (errPath == null) return false;
            try
            {
                string txt = ReadErrFrom(errPath, errPos);
                foreach (string term in PortInUseTerms)
                    if (txt.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch (Exception) { }
            return false;
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
                Append("SUBIU E FICOU ESTÁVEL por " + ObserveSeconds + " s" + (mode == Mode.Apply ? " com force na porta " + maintPort + ". Próximo: o N2 faz OPTIMIZE/ALTER TABLE nas tabelas abaixo (ainda com force)." :
                       ". Deixar mais alguns minutos observando o .err; se operar normal, o N2 volta a porta para " + clientPort + " (a do sistema do cliente) e roda bigupdate, acertaestrutura e acertasequenciador.") + "\r\n");
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
