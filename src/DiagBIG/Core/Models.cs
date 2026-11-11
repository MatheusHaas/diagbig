using System;
using System.Collections.Generic;

namespace DiagBIG.Core
{
    public enum CheckStatus { Ok, Atencao, NaoVerificado }

    /// <summary>Um evento do Visualizador de Eventos ou uma linha relevante do .err.</summary>
    public class Occurrence
    {
        public DateTime? Time { get; set; }
        public string Source { get; set; }
        public string EventId { get; set; }
        public string Level { get; set; }
        public string Message { get; set; }
        /// <summary>Chave do agrupamento detalhado (fonte + ID ou mensagem normalizada).</summary>
        public string GroupKey { get; set; }
        /// <summary>Chave curta usada na linha do RESUMO (ex.: "disk 7", "marked as crashed").</summary>
        public string SummaryKey { get; set; }
    }

    /// <summary>Um bloco dentro de uma seção do TXT (ex.: eventos de disco, SMART, um banco).</summary>
    public class ReportBlock
    {
        public string Title { get; set; }
        public List<string> Info { get; } = new List<string>();
        public List<Occurrence> Occurrences { get; } = new List<Occurrence>();
        public bool Verified { get; set; } = true;
        public string NotVerifiedReason { get; set; }
        /// <summary>Bloco só informativo (sem lista de ocorrências), ex.: SMART.</summary>
        public bool InfoOnly { get; set; }
        /// <summary>Observação acrescentada à linha do resumo.</summary>
        public string SummaryNote { get; set; }
        /// <summary>true = itens são eventos do Windows; false = linhas do .err.</summary>
        public bool IsEvents { get; set; } = true;

        public static ReportBlock NotVerified(string title, string reason, bool isEvents = true)
        {
            return new ReportBlock { Title = title, Verified = false, NotVerifiedReason = reason, IsEvents = isEvents };
        }
    }

    public class ReportSection
    {
        public string Title { get; set; }
        public List<ReportBlock> Blocks { get; } = new List<ReportBlock>();
    }

    public class SummaryItem
    {
        public string Label { get; set; }
        public CheckStatus Status { get; set; }
        public string Text { get; set; }
    }

    public class LogCoverage
    {
        public string LogName { get; set; }
        public DateTime? Oldest { get; set; }
        public string Error { get; set; }
    }

    public class ErrCoverage
    {
        public string Label { get; set; }
        public DateTime? FirstDate { get; set; }
        public bool Truncated { get; set; }
        public long FileSize { get; set; }
        public long BytesRead { get; set; }
    }

    public class ReportData
    {
        public string ComputerName { get; set; }
        public string WindowsVersion { get; set; }
        public DateTime CollectedAt { get; set; }
        public int PeriodDays { get; set; }
        public DateTime Since { get; set; }
        public string OutputFileName { get; set; }
        /// <summary>Último boot completo do Windows (null = não lido).</summary>
        public DateTime? LastBoot { get; set; }
        /// <summary>Controladora RAID encontrada? (null + RaidError = não verificado)</summary>
        public RaidInfo Raid { get; set; }
        public string RaidError { get; set; }
        public List<string> Drives { get; } = new List<string>();
        public string DrivesError { get; set; }
        public List<LogCoverage> LogCoverages { get; } = new List<LogCoverage>();
        public List<Occurrence> LogClearedEvents { get; } = new List<Occurrence>();
        /// <summary>null = verificado. Preenchido = não deu pra verificar o ID 104.</summary>
        public string LogClearedError { get; set; }
        public List<ErrCoverage> ErrCoverages { get; } = new List<ErrCoverage>();
        public List<SummaryItem> Summary { get; } = new List<SummaryItem>();
        public List<ReportSection> Sections { get; } = new List<ReportSection>();
    }
}
