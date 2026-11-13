namespace DiagBIG.Core
{
    /// <summary>
    /// Listas e limites editáveis. Tudo que o suporte pode querer ajustar fica aqui.
    /// </summary>
    public static class Config
    {
        public const string Version = "1.5.1";

        // ---------------- Período ----------------
        public static readonly int[] PeriodOptions = { 7, 30, 90 };
        public const int DefaultPeriodDays = 30;
        public const int MaxPeriodDays = 90;

        // ---------------- Parte 1: disco / NTFS (log Sistema) ----------------

        /// <summary>Provider entra se o nome CONTIVER algum destes (sem diferenciar maiúsculas). Níveis Crítico e Erro.</summary>
        /// Inclui drivers de controladora RAID de servidor: Dell PERC/LSI MegaRAID (megasas, percsas),
        /// HP Smart Array (HpCISSs, hpsamd), Microsemi/HPE (smartpqi), LSI SAS (lsi_sas), Adaptec (arcsas), Areca (arcmsr).
        public static readonly string[] DiskSources =
        {
            "disk", "ntfs", "volmgr", "atapi", "storahci", "stornvme", "iastor",
            "megasas", "percsas", "hpcisss", "hpsamd", "smartpqi", "lsi_sas", "arcsas", "arcmsr",
        };

        /// <summary>Avisos (nível 3) da fonte "disk" que indicam disco falhando.</summary>
        public const string DiskWarningSource = "disk";
        public static readonly int[] DiskWarningIds = { 51, 52, 153 };
        /// <summary>Avisos de qualquer fonte de disco/controladora: 129 = "Reset to device" (disco travando/timeout).</summary>
        public static readonly int[] StorageWarningIds = { 129 };

        /// <summary>volmgr 46/161/162: falha ao gravar o despejo de memória. Consequência de tela azul, não entra em DISCO.</summary>
        public const string DumpFailureSource = "volmgr";
        public static readonly int[] DumpFailureIds = { 46, 161, 162 };

        /// <summary>Contadores de confiabilidade (fallback do SMART): acima disso o disco entra em ATENÇÃO.</summary>
        public const int WearAttentionPercent = 80;
        public const int DiskTempAttentionC = 70;

        // ---------------- Detecção de RAID (só informativa) ----------------
        /// <summary>Driver de controladora que é sempre RAID.</summary>
        public static readonly string[] RaidDrivers = { "megasas", "percsas", "hpcisss", "hpsamd", "smartpqi", "arcmsr" };
        /// <summary>Driver que também serve a controladoras sem RAID: só conta se o nome tiver RaidNameKeywords.</summary>
        public static readonly string[] RaidDriversIfNamed = { "lsi_sas", "arcsas", "iastor" };
        public static readonly string[] RaidNameKeywords = { "RAID", "PERC", "Smart Array", "MegaRAID" };
        /// <summary>Modelo do disco (Win32_DiskDrive) que indica volume lógico de RAID.</summary>
        public static readonly string[] RaidDiskModelKeywords = { "PERC", "LOGICAL VOLUME", "MegaRAID", "RAID", "Smart Array" };

        // ---------------- Desligamentos inesperados (log Sistema) ----------------
        public const string KernelPowerProvider = "Microsoft-Windows-Kernel-Power";
        public const int KernelPowerId = 41;
        public const string EventLogProvider = "EventLog";
        public const int UnexpectedShutdownId = 6008;

        /// <summary>Evento de "log limpo".</summary>
        public const string LogClearedProvider = "Microsoft-Windows-Eventlog";
        public const int LogClearedId = 104;

        // ---------------- Hardware / memória (possíveis causadores) ----------------
        /// <summary>WHEA-Logger: erros de hardware (RAM, CPU, PCIe) detectados/corrigidos pela plataforma. Aviso, Erro e Crítico.</summary>
        public const string WheaProvider = "Microsoft-Windows-WHEA-Logger";
        /// <summary>Resultado do Diagnóstico de Memória do Windows (mdsched). 1102 = erros de hardware detectados.</summary>
        public const string MemDiagProvider = "Microsoft-Windows-MemoryDiagnostics-Results";
        public const int MemDiagErrorId = 1102;
        /// <summary>Resource-Exhaustion-Detector 2004: pouca memória virtual.</summary>
        public const string ResourceExhaustionProvider = "Microsoft-Windows-Resource-Exhaustion-Detector";
        public const int ResourceExhaustionId = 2004;
        /// <summary>Quantas quedas do banco no período para falar em "loop de crash".</summary>
        public const int CrashLoopThreshold = 5;

        // ---------------- Parte 2: banco MySQL/MariaDB ----------------

        /// <summary>Executáveis do servidor de banco (ImagePath do serviço / Application Error 1000).</summary>
        /// MySQL 4 usa mysqld-nt / mysqld-max-nt / mysqld-opt etc.
        public static readonly string[] DbProcessNames =
        {
            "mysqld.exe", "mariadbd.exe",
            "mysqld-nt.exe", "mysqld-max-nt.exe", "mysqld-max.exe", "mysqld-opt.exe",
        };

        /// <summary>Fontes do log Aplicativo consideradas do banco (contém, sem diferenciar maiúsculas).</summary>
        public static readonly string[] DbEventSources = { "mysql", "mariadb" };

        public const string AppErrorProvider = "Application Error";
        public const int AppErrorId = 1000;

        public const string ScmProvider = "Service Control Manager";
        public static readonly int[] ScmEventIds = { 7000, 7009, 7023, 7024, 7031, 7034 };

        /// <summary>
        /// Varredura de reserva na raiz de cada unidade: pasta cuja "data" tenha esta subpasta
        /// (schema de sistema presente em toda datadir MySQL/MariaDB), seja qual for o nome da pasta.
        /// </summary>
        public const string DataDirMarker = "mysql";

        /// <summary>Quanto do final do .err é lido.</summary>
        public const long ErrTailBytes = 10L * 1024 * 1024;

        /// <summary>Linhas do .err que contenham algum destes textos (sem diferenciar maiúsculas) viram ocorrência.</summary>
        public static readonly string[] ErrPatterns =
        {
            "[ERROR]", "[FATAL]", "corrupt", "corruption", "crashed", "marked as crashed",
            "Assertion failure", "got exception", "got signal", "hit a bug", "innodb_force_recovery",
            "Database page corruption", "Incorrect key file",
            "Got error 1", // pega "Got error 1", 126, 127, 134, 145...
            "Operating system error", "Out of memory", "Aborting", "crash recovery",
            "Can't open", "Unable to lock", "No space left", "Disk full",
        };

        /// <summary>Em linhas de crash, trazer também as linhas seguintes (stack/contexto).</summary>
        public static readonly string[] CrashPatterns = { "got exception", "Assertion failure", "got signal" };
        public const int CrashContextLines = 15;

        // ---------------- Saída ----------------
        public const int DetailedOccurrences = 30;
        public const int ResumoMaxChars = 3500;
        public const int MaxEventsPerQuery = 20000;
        /// <summary>Pasta (ao lado do .exe ou na Área de Trabalho) onde os TXT são gravados.</summary>
        public const string ReportsFolder = "DiagBIG_relatorios";
        /// <summary>Quantos relatórios manter por PC nessa pasta; os mais antigos são apagados.</summary>
        public const int KeepReports = 10;
        /// <summary>Linhas na linha do tempo de liga/desliga.</summary>
        public const int PowerTimelineMax = 60;
    }
}
