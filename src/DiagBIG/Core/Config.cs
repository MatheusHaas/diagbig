namespace DiagBIG.Core
{
    /// <summary>
    /// Listas e limites editáveis. Tudo que o suporte pode querer ajustar fica aqui.
    /// </summary>
    public static class Config
    {
        public const string Version = "1.4.0";

        // ---------------- Período ----------------
        public static readonly int[] PeriodOptions = { 7, 30, 90 };
        public const int DefaultPeriodDays = 30;
        public const int MaxPeriodDays = 90;

        // ---------------- Parte 1: disco / NTFS (log Sistema) ----------------

        /// <summary>Provider entra se o nome CONTIVER algum destes (sem diferenciar maiúsculas). Níveis Crítico e Erro.</summary>
        public static readonly string[] DiskSources = { "disk", "ntfs", "volmgr", "atapi", "storahci", "stornvme", "iastor" };

        /// <summary>Avisos (nível 3) da fonte "disk" que indicam disco falhando.</summary>
        public const string DiskWarningSource = "disk";
        public static readonly int[] DiskWarningIds = { 51, 52, 153 };

        // ---------------- Desligamentos inesperados (log Sistema) ----------------
        public const string KernelPowerProvider = "Microsoft-Windows-Kernel-Power";
        public const int KernelPowerId = 41;
        public const string EventLogProvider = "EventLog";
        public const int UnexpectedShutdownId = 6008;

        /// <summary>Evento de "log limpo".</summary>
        public const string LogClearedProvider = "Microsoft-Windows-Eventlog";
        public const int LogClearedId = 104;

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
