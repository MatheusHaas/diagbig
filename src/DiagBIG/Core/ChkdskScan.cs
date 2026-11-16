namespace DiagBIG.Core
{
    /// <summary>Interpreta o resultado do "chkdsk X: /scan" (verificação online, só leitura).</summary>
    public static class ChkdskScan
    {
        public static bool IsProblem(int exitCode) { return exitCode != 0; }

        public static string Verdict(string drive, int exitCode)
        {
            switch (exitCode)
            {
                case 0:
                    return drive + " SEM PROBLEMAS: o chkdsk /scan não achou erro no sistema de arquivos (não descarta disco com setor ruim).";
                case 1:
                case 2:
                    return drive + " ERROS ENCONTRADOS E TRATADOS (código " + exitCode + "): o sistema de arquivos tinha erro. Fazer backup e rodar o DiagBIG de novo; se voltar, suspeitar do disco.";
                case -1:
                    return drive + " NÃO FOI POSSÍVEL EXECUTAR o chkdsk (rodar como administrador).";
                default:
                    return drive + " ERROS ENCONTRADOS, NÃO CORRIGIDOS, ou não deu para verificar (código " + exitCode + "): fazer backup AGORA. " +
                           "chkdsk /f só offline, depois do backup. Se houver também setor defeituoso/Ntfs 55 no relatório, trocar o disco.";
            }
        }
    }
}
