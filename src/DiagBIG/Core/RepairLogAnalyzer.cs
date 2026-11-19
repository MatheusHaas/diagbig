using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DiagBIG.Core
{
    public class RepairResult
    {
        /// <summary>Falhas de DISM (uma por código e minuto) e do SFC (uma por minuto).</summary>
        public List<Occurrence> Failures { get; } = new List<Occurrence>();
        /// <summary>Quantos arquivos o SFC reparou no período (CBS.log "Repairing corrupted file").</summary>
        public int SfcRepaired { get; set; }
        /// <summary>Última vez que o SFC terminou uma verificação no período.</summary>
        public DateTime? LastSfcVerify { get; set; }
        public DateTime? LastDismRun { get; set; }
    }

    /// <summary>
    /// Lê o dism.log e o CBS.log (as saídas de DISM e SFC) e acha as falhas do período.
    /// Só vê o que o Windows registrou: quem nunca rodou DISM/SFC não tem nada ali.
    /// </summary>
    public static class RepairLogAnalyzer
    {
        public const string DismKey = "DISM falhou";
        public const string SfcKey = "SFC não reparou";

        static readonly Regex DismLine = new Regex(@"^(\d{4})-(\d{2})-(\d{2}) (\d{2}):(\d{2}):(\d{2}), (Error|Info|Warning)\s+DISM\b", RegexOptions.Compiled);
        static readonly Regex Hresult = new Regex(@"0x[0-9a-fA-F]{8}", RegexOptions.Compiled);
        static readonly Regex CbsSr = new Regex(
            @"^(\d{4})-(\d{2})-(\d{2}) (\d{2}):(\d{2}):(\d{2}), \w+\s+\w+\s+[0-9a-fA-F]+\s+\[SR\] (Cannot repair member file|Repairing corrupted file|Verify complete|Repair complete)",
            RegexOptions.Compiled);

        /// <summary>Códigos que mandam procurar o disco (CRC, E/S, arquivo corrompido e ilegível).</summary>
        static readonly string[] DiskCodes = { "0x80070017", "0x8007045d", "0x80070570", "0x80070571", "0x8007001f" };

        /// <summary>Códigos do DISM que não indicam defeito de disco: faltou a origem do reparo (sem internet/ISO) ou parâmetro incorreto (comando mal digitado).</summary>
        static readonly string[] SourceMissingCodes = { "0x800f081f", "0x800f0915", "0x800f0906", "0x800f0907", "0x800f0950", "0x80070057" };

        /// <summary>true se TODAS as falhas de reparo são DISM sem relação com disco (origem não encontrada, parâmetro incorreto).</summary>
        public static bool OnlyNonDisk(IEnumerable<Occurrence> repair)
        {
            bool any = false;
            foreach (Occurrence o in repair)
            {
                any = true;
                string k = o.SummaryKey ?? "";
                if (!k.StartsWith(DismKey, StringComparison.Ordinal)) return false;
                string code = k.Substring(k.LastIndexOf(' ') + 1);
                bool ok = false;
                foreach (string c in SourceMissingCodes) if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) ok = true;
                if (!ok) return false;
            }
            return any;
        }

        public static bool IsDiskCode(string code)
        {
            foreach (string c in DiskCodes) if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Significado em português dos códigos mais comuns do DISM.</summary>
        public static string Describe(string code)
        {
            switch ((code ?? "").ToLowerInvariant())
            {
                case "0x800f0915": return "conteúdo de reparo não encontrado em lugar nenhum (sem acesso ao Windows Update ou origem errada)";
                case "0x800f081f": return "arquivos de origem não encontrados (usar /Source com ISO da mesma versão)";
                case "0x800f0906": return "não conseguiu baixar os arquivos de reparo (internet ou Windows Update)";
                case "0x800f0907": return "política do sistema bloqueia a origem de reparo";
                case "0x800f0950": return "reparo exige acesso a origem que não está disponível";
                case "0x80070017": return "erro de CRC na leitura: DISCO";
                case "0x8007045d": return "erro de entrada/saída do dispositivo: DISCO";
                case "0x80070570": return "arquivo ou pasta corrompido e ilegível: DISCO/sistema de arquivos";
                case "0x80070571": return "o disco está corrompido e ilegível: DISCO";
                case "0x8007001f": return "dispositivo não funciona: DISCO";
                case "0x80070057": return "parâmetro incorreto (comando digitado errado ou opção inválida; rodar de novo como administrador e conferir a sintaxe); não indica disco";
                case "0x80070002": return "arquivo não encontrado";
                case "0x80070005": return "acesso negado (rodar como administrador)";
                case "0x800f0922": return "falha ao aplicar a atualização (pouco espaço ou componente quebrado)";
                default: return "";
            }
        }

        static DateTime? Date(Match m)
        {
            try
            {
                return new DateTime(Int(m, 1), Int(m, 2), Int(m, 3), Int(m, 4), Int(m, 5), Int(m, 6));
            }
            catch (ArgumentException) { return null; }
        }

        static int Int(Match m, int g) { return int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture); }

        public static RepairResult Analyze(string dismLog, string cbsLog, DateTime since)
        {
            var r = new RepairResult();
            var seen = new HashSet<string>();

            foreach (string raw in (dismLog ?? "").Split('\n'))
            {
                Match m = DismLine.Match(raw);
                if (!m.Success) continue;
                DateTime? t = Date(m);
                if (t == null || t.Value < since) continue;
                if (r.LastDismRun == null || t > r.LastDismRun) r.LastDismRun = t;
                if (m.Groups[7].Value != "Error") continue;
                Match h = Hresult.Match(raw);
                if (!h.Success) continue;
                string code = h.Value.ToLowerInvariant();
                string key = code + t.Value.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
                if (!seen.Add(key)) continue; // um erro vira várias linhas em cascata: conta uma por código e minuto
                string meaning = Describe(code);
                r.Failures.Add(new Occurrence
                {
                    Time = t,
                    Source = "dism.log",
                    Level = "ERROR",
                    Message = "DISM falhou com " + code + (meaning.Length > 0 ? " — " + meaning : ""),
                    GroupKey = "DISM falhou " + code + (meaning.Length > 0 ? " — " + meaning : ""),
                    SummaryKey = DismKey + " " + code,
                });
            }

            foreach (string raw in (cbsLog ?? "").Split('\n'))
            {
                Match m = CbsSr.Match(raw);
                if (!m.Success) continue;
                DateTime? t = Date(m);
                if (t == null || t.Value < since) continue;
                switch (m.Groups[7].Value)
                {
                    case "Cannot repair member file":
                        if (seen.Add("sfc" + t.Value.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)))
                            r.Failures.Add(new Occurrence
                            {
                                Time = t, Source = "CBS.log", Level = "ERROR",
                                Message = "SFC não conseguiu reparar arquivo(s) do Windows (CBS.log: Cannot repair member file)",
                                GroupKey = "SFC não conseguiu reparar arquivo(s) do Windows",
                                SummaryKey = SfcKey,
                            });
                        break;
                    case "Repairing corrupted file":
                        r.SfcRepaired++;
                        break;
                    case "Verify complete":
                    case "Repair complete":
                        if (r.LastSfcVerify == null || t > r.LastSfcVerify) r.LastSfcVerify = t;
                        break;
                }
            }
            return r;
        }
    }
}
