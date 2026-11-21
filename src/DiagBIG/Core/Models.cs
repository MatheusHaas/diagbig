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
        /// <summary>Identifica o bloco para a análise de causas (BlockTag).</summary>
        public string Tag { get; set; }
        /// <summary>Avisos do bloco (ex.: opções perigosas do my.ini), mostrados como AVISO e nas causas prováveis.</summary>
        public List<string> Warnings { get; } = new List<string>();

        public static ReportBlock NotVerified(string title, string reason, bool isEvents = true)
        {
            return new ReportBlock { Title = title, Verified = false, NotVerifiedReason = reason, IsEvents = isEvents };
        }
    }

    /// <summary>Etiquetas dos blocos usadas por Hypotheses.</summary>
    public static class BlockTag
    {
        public const string Disk = "disk";
        /// <summary>Eventos de disco/NTFS ANTES do período escolhido (ainda presentes no log): histórico.</summary>
        public const string DiskOld = "diskold";
        /// <summary>Hora do Windows alterada por mais de 1 dia (bateria da placa-mãe/CMOS ou relógio errado).</summary>
        public const string Clock = "clock";
        public const string Smart = "smart";
        public const string Power = "power";
        public const string Hardware = "hardware";
        public const string Repair = "repair";
        public const string Db = "db";
        public const string DbEvents = "dbevents";
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

    /// <summary>Encaminhamentos possíveis para o N1; a tela inicial mostra só o necessário de cada um.</summary>
    public enum N1Case { Nada, TrocarDisco, Tabelas, Reinstalar, ReparoWindows, Energia, N1Sozinho }

    public class ReportData
    {
        public string ComputerName { get; set; }
        public string WindowsVersion { get; set; }
        /// <summary>Dados do sistema operacional para a sessão SISTEMA OPERACIONAL (null = não lido).</summary>
        public OsInfo Os { get; set; }
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
        /// <summary>Letras das unidades onde ficam os datadirs do banco (ex.: "C:").</summary>
        public List<string> DbDrives { get; } = new List<string>();
        /// <summary>Criação do ibdata1/datadir do banco (null = não lido).</summary>
        public DateTime? DbDataCreated { get; set; }
        /// <summary>Data de instalação do Windows (null = não lida).</summary>
        public DateTime? OsInstalled { get; set; }
        /// <summary>Modelo do disco quando o PC tem um único disco físico (não USB); null se há mais de um.</summary>
        public string SingleDiskModel { get; set; }
        public List<LogCoverage> LogCoverages { get; } = new List<LogCoverage>();
        public List<Occurrence> LogClearedEvents { get; } = new List<Occurrence>();
        /// <summary>null = verificado. Preenchido = não deu pra verificar o ID 104.</summary>
        public string LogClearedError { get; set; }
        public List<ErrCoverage> ErrCoverages { get; } = new List<ErrCoverage>();
        public List<SummaryItem> Summary { get; } = new List<SummaryItem>();
        /// <summary>Causas prováveis (calculadas por Hypotheses a partir dos blocos).</summary>
        public List<string> Hypotheses { get; } = new List<string>();
        /// <summary>Onde está o problema (calculado por Diagnosis).</summary>
        public List<DiagItem> Diagnosis { get; } = new List<DiagItem>();
        /// <summary>O que fazer, passo a passo (calculado por ActionPlan): primeira linha é o veredito.</summary>
        public List<string> ActionPlan { get; } = new List<string>();
        /// <summary>Encaminhamento simples para o N1, no início do relatório (calculado por ActionPlan).</summary>
        public List<string> N1 { get; } = new List<string>();
        /// <summary>Qual encaminhamento o ActionPlan escolheu (define o que a tela mostra).</summary>
        public N1Case N1Kind { get; set; }
        /// <summary>Quem aciona: "EQUIPE TÉCNICA", "N2 SISTEMA", "TÉCNICO DA FARMÁCIA", "N1 (RESOLVE SOZINHO)" ou null (sem encaminhamento).</summary>
        public string N1Dest { get; set; }
        /// <summary>IPv4 das placas ativas, ex.: "192.168.0.10 (Ethernet)".</summary>
        public List<string> IpAddresses { get; } = new List<string>();
        /// <summary>true = algum serviço do banco está rodando agora; false = nenhum; null = sem serviço encontrado.</summary>
        public bool? DbServiceRunning { get; set; }
        /// <summary>Serviços de banco com inicialização DESATIVADA (podem ser restos de uma troca de servidor/banco).</summary>
        public List<string> DisabledDbServices { get; } = new List<string>();
        /// <summary>Resultado do winmgmt /verifyrepository (só leitura).</summary>
        public WmiRepoState WmiRepo { get; set; }
        public string WmiRepoNote { get; set; }
        public List<ReportSection> Sections { get; } = new List<ReportSection>();
    }
}
