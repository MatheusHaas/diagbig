using System;

namespace DiagBIG.Core
{
    /// <summary>
    /// Banco instalado há pouco tempo que já corrompe/cai: o dump antigo deixa de ser o suspeito
    /// (o datadir é novo) e sobem disco, RAM e controladora.
    /// </summary>
    public static class RecentInstall
    {
        public const int RecentDays = 7;

        /// <summary>Dias desde a criação do datadir novo (ibdata1), se for recente; senão null.</summary>
        public static int? DbAgeDays(ReportData d)
        {
            if (d == null || !d.DbDataCreated.HasValue) return null;
            double days = (d.CollectedAt - d.DbDataCreated.Value).TotalDays;
            if (days < 0 || days > RecentDays) return null;
            return (int)Math.Floor(days);
        }

        static string Ago(int days) { return days == 0 ? "hoje" : "há " + days + " dia(s)"; }

        static string Fmt(DateTime t) { return t.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture); }

        /// <summary>Linha informativa para o bloco do banco; null se não há data lida.</summary>
        public static string InfoLine(ReportData d)
        {
            if (d == null || !d.DbDataCreated.HasValue) return null;
            string s = "Banco criado em .: " + Fmt(d.DbDataCreated.Value) + " (ibdata1/datadir)";
            if (d.OsInstalled.HasValue) s += " | Windows instalado em " + Fmt(d.OsInstalled.Value);
            return s;
        }

        /// <summary>Banco recém-instalado E com dano: texto da causa; senão null.</summary>
        public static string Cause(ReportData d, bool dbDamaged)
        {
            int? age = DbAgeDays(d);
            if (age == null || !dbDamaged) return null;
            string os = d.OsInstalled.HasValue && (d.CollectedAt - d.OsInstalled.Value).TotalDays <= RecentDays
                ? " e o Windows foi instalado em " + Fmt(d.OsInstalled.Value) : "";
            return "BANCO NOVO JÁ CORROMPENDO: o datadir foi criado " + Ago(age.Value) + " (" + Fmt(d.DbDataCreated.Value) + ")" + os +
                   " e já há corrupção/queda. Se isso aconteceu logo depois de instalar limpo e restaurar o backup, o dump antigo e a instalação deixam de ser o suspeito: " +
                   "a suspeita passa a ser DISCO, RAM ou controladora/cabo. Testar a RAM (MemTest86) e trocar o disco, ou restaurar em OUTRO disco/servidor, em vez de reinstalar de novo.";
        }
    }
}
