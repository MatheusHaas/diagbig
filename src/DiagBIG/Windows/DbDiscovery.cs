using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using DiagBIG.Core;
using Microsoft.Win32;

namespace DiagBIG.Windows
{
    internal class DbService
    {
        public string Name;
        public string DisplayName;
        public string ImagePath;
        public string StartType;
        public string Status;
    }

    /// <summary>Um .err a analisar (ou a falta dele, com o motivo).</summary>
    internal class DbTarget
    {
        public DbService Service;      // null = achado por varredura
        public string Label;           // "BANCO (MariaDB)"
        public string ErrPath;         // null = não encontrado
        public string HowFound;
        public string NotFoundReason;
        public string DataDir;
        /// <summary>Preenchido quando o .err esperado não existe e foi usado outro da datadir.</summary>
        public string FallbackNote;
        public readonly List<string> Info = new List<string>();
    }

    /// <summary>Localiza serviços MySQL/MariaDB e seus .err. Só leitura (registro e sistema de arquivos).</summary>
    internal static class DbDiscovery
    {
        const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

        public static List<DbService> FindServices()
        {
            var list = new List<DbService>();
            using (RegistryKey services = Registry.LocalMachine.OpenSubKey(ServicesKey, false))
            {
                if (services == null) throw new InvalidOperationException("chave HKLM\\" + ServicesKey + " não encontrada");
                foreach (string name in services.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey k = services.OpenSubKey(name, false))
                        {
                            string img = k == null ? null : k.GetValue("ImagePath") as string;
                            if (img == null || !ImagePathParser.IsDatabaseImagePath(img)) continue;
                            list.Add(new DbService
                            {
                                Name = name,
                                DisplayName = k.GetValue("DisplayName") as string,
                                ImagePath = img,
                                StartType = StartTypeName(k.GetValue("Start"), k.GetValue("DelayedAutostart")),
                                Status = ServiceStatus(name),
                            });
                        }
                    }
                    catch (Exception) { /* subchave sem permissão: ignora */ }
                }
            }
            return list;
        }

        static string StartTypeName(object start, object delayed)
        {
            int s = start is int ? (int)start : -1;
            switch (s)
            {
                case 0: return "boot";
                case 1: return "sistema";
                case 2: return delayed is int && (int)delayed == 1 ? "automático (atraso)" : "automático";
                case 3: return "manual";
                case 4: return "DESATIVADO";
                default: return "desconhecido";
            }
        }

        static string ServiceStatus(string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    switch (sc.Status)
                    {
                        case ServiceControllerStatus.Running: return "rodando";
                        case ServiceControllerStatus.Stopped: return "PARADO";
                        case ServiceControllerStatus.StartPending: return "iniciando";
                        case ServiceControllerStatus.StopPending: return "parando";
                        case ServiceControllerStatus.Paused: return "pausado";
                        default: return sc.Status.ToString();
                    }
                }
            }
            catch (Exception ex) { return "não foi possível ler (" + ex.Message + ")"; }
        }

        public static DbTarget ResolveForService(DbService svc)
        {
            var t = new DbTarget { Service = svc, Label = "BANCO (" + svc.Name + ")" };
            t.Info.Add("Serviço .......: " + svc.Name + (string.IsNullOrEmpty(svc.DisplayName) || svc.DisplayName == svc.Name ? "" : " (" + svc.DisplayName + ")"));
            t.Info.Add("Status ........: " + svc.Status + " | inicialização: " + svc.StartType);
            t.Info.Add("ImagePath .....: " + svc.ImagePath);

            ImagePathInfo ip = ImagePathParser.Parse(svc.ImagePath);
            string baseDir = DbPathResolver.BaseDirFromExe(ip.ExePath);

            // Com --defaults-file o MySQL lê só esse arquivo; sem ele, lê todos os padrões em ordem.
            var files = new List<string>();
            if (ip.DefaultsFile != null)
            {
                if (File.Exists(ip.DefaultsFile)) files.Add(ip.DefaultsFile);
                else t.Info.Add("AVISO: --defaults-file aponta para arquivo inexistente: " + ip.DefaultsFile);
            }
            bool fromDefaultsFile = files.Count > 0;
            if (!fromDefaultsFile)
            {
                string windir = null;
                try { windir = Environment.GetEnvironmentVariable("windir"); } catch (Exception) { }
                files.AddRange(DbPathResolver.IniCandidates(baseDir, windir).Where(File.Exists));
            }

            var parsed = new List<MyIniOptions>();
            var read = new List<string>();
            foreach (string f in files)
            {
                try { parsed.Add(MyIniParser.Parse(ReadSharedText(f))); read.Add(f); }
                catch (Exception ex) { t.Info.Add("AVISO: não foi possível ler " + f + ": " + ex.Message); }
            }
            MyIniOptions opt = MyIniParser.Merge(parsed);
            if (read.Count == 0) t.Info.Add("Config ........: nenhum my.ini/my.cnf encontrado (usando padrões)");
            else if (fromDefaultsFile) t.Info.Add("Config ........: " + read[0] + " (--defaults-file)");
            else t.Info.Add("Config ........: " + string.Join(" + ", read.ToArray()) +
                            (read.Count > 1 ? " (lidos nessa ordem; o último prevalece)" : ""));

            DbPaths paths = DbPathResolver.Resolve(opt, baseDir, Environment.MachineName);
            t.Info.Add("Datadir .......: " + paths.DataDir);
            t.DataDir = paths.DataDir;

            foreach (string candidate in new[] { paths.ErrPath }.Concat(paths.AlternateErrPaths))
            {
                if (!File.Exists(candidate)) continue;
                t.ErrPath = candidate;
                t.HowFound = "pelo serviço " + svc.Name + " (" + paths.ErrRule + ")";
                return t;
            }

            string newest = NewestErr(paths.DataDir);
            if (newest != null)
            {
                t.ErrPath = newest;
                t.HowFound = "pelo serviço " + svc.Name + " (esperado " + paths.ErrPath + " não existe; usado o .err mais recente da datadir)";
                string expected = WinPath.GetFileName(paths.ErrPath), used = WinPath.GetFileName(newest);
                t.FallbackNote = "lido " + used + " porque " + expected + " não existe";
                t.Info.Add("AVISO: " + expected + " não existe; foi lido " + used + " (o .err mais recente da datadir).");
                t.Info.Add("       Se o .err foi renomeado com o banco rodando, o banco continua gravando no arquivo");
                t.Info.Add("       renomeado até o serviço ser reiniciado; só então cria um " + expected + " novo.");
                return t;
            }

            t.NotFoundReason = ".err não encontrado (esperado " + paths.ErrPath + "; nenhum .err em " + paths.DataDir + ")";
            return t;
        }

        /// <summary>Varredura leve (sem recursão) das pastas padrão de instalação em todas as unidades fixas.</summary>
        public static List<DbTarget> Scan(ICollection<string> alreadyUsedErr)
        {
            var dataDirs = new List<string>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try { if (d.DriveType != DriveType.Fixed || !d.IsReady) continue; }
                catch (Exception) { continue; }
                string root = d.RootDirectory.FullName; // "C:\"
                foreach (string pf in new[] { "Program Files", "Program Files (x86)" })
                {
                    AddDataDirs(dataDirs, Path.Combine(root, pf), "MariaDB*");
                    AddDataDirs(dataDirs, Path.Combine(root, pf + @"\MySQL"), "MySQL Server*");
                }
                AddRootDataDirs(dataDirs, root);
            }
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrEmpty(programData))
                AddDataDirs(dataDirs, Path.Combine(programData, "MySQL"), "MySQL Server*");

            var used = new HashSet<string>(alreadyUsedErr, StringComparer.OrdinalIgnoreCase);
            var result = new List<DbTarget>();
            foreach (string data in dataDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string err = NewestErr(data);
                if (err == null || used.Contains(err)) continue;
                used.Add(err);
                var t = new DbTarget { Label = "BANCO (varredura: " + data + ")", ErrPath = err, HowFound = "por varredura (sem serviço)", DataDir = data };
                t.Info.Add("Datadir .......: " + data);
                result.Add(t);
            }
            return result;
        }

        /// <summary>Outros .err da datadir alterados desde "since" (ex.: old_PC.err renomeado), exceto o que foi lido.</summary>
        public static List<FileInfo> OtherErrFiles(string dataDir, string usedErr, DateTime since)
        {
            try
            {
                if (string.IsNullOrEmpty(dataDir) || !Directory.Exists(dataDir)) return new List<FileInfo>();
                return new DirectoryInfo(dataDir).GetFiles("*.err")
                    .Where(f => f.Name.EndsWith(".err", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(f.FullName, usedErr, StringComparison.OrdinalIgnoreCase)
                                && f.LastWriteTime >= since)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
            }
            catch (Exception) { return new List<FileInfo>(); }
        }

        /// <summary>Pastas na raiz da unidade (ex.: C:\MeuBanco) cuja \data tenha a subpasta de sistema "mysql".</summary>
        static void AddRootDataDirs(List<string> into, string root)
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    try
                    {
                        string data = Path.Combine(dir, "data");
                        if (Directory.Exists(Path.Combine(data, Config.DataDirMarker))) into.Add(data);
                    }
                    catch (Exception) { /* sem permissão: segue */ }
                }
            }
            catch (Exception) { }
        }

        static void AddDataDirs(List<string> into, string parent, string pattern)
        {
            try
            {
                if (!Directory.Exists(parent)) return;
                foreach (string dir in Directory.GetDirectories(parent, pattern))
                {
                    string data = Path.Combine(dir, "data");
                    if (Directory.Exists(data)) into.Add(data);
                }
            }
            catch (Exception) { /* sem permissão: segue */ }
        }

        public static string NewestErr(string dataDir)
        {
            try
            {
                if (string.IsNullOrEmpty(dataDir) || !Directory.Exists(dataDir)) return null;
                return new DirectoryInfo(dataDir).GetFiles("*.err")
                    .Where(f => f.Name.EndsWith(".err", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch (Exception) { return null; }
        }

        static string ReadSharedText(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var ms = new MemoryStream();
                fs.CopyTo(ms);
                byte[] b = ms.ToArray();
                int skip = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
                string enc;
                return ErrTailReader.Decode(b, skip, b.Length - skip, out enc);
            }
        }
    }
}
