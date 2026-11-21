# Roda os 8 casos do N1 em sequencia na VM: prepara o cenario, coleta (--sem-janela --dias 7), confere "QUEM ACIONAR" e restaura.
param([string]$Diag, [string]$Pasta)
[Console]::OutputEncoding = [System.Text.Encoding]::GetEncoding(850)
$Sim = Join-Path $Pasta "simular.ps1"
$Saida = Join-Path $Pasta "casos-resultado.txt"
$linhas = New-Object System.Collections.ArrayList
function Say($t, $cor = $null) {
    [void]$linhas.Add($t)
    if ($cor) { Write-Host $t -ForegroundColor $cor } else { Write-Host $t }
}
function Pasta-Relatorios {
    foreach ($p in @((Join-Path $Pasta "..\DiagBIG_relatorios"), (Join-Path $Pasta "DiagBIG_relatorios"))) { if (Test-Path $p) { return $p } }
    return $null
}
function Limpar-Logs { wevtutil cl System 2>$null; wevtutil cl Application 2>$null }
function Cenario($n) { & $Sim -Cenario $n 2>&1 | ForEach-Object { Write-Host ("    " + $_) } }

$casos = @(
  @{ Nome = "1 NADA ENCONTRADO";          Setup = @();      Restaura = @();       Dest = "NINGU";                 Contem = "" },
  @{ Nome = "2 SO TABELAS";               Setup = @(3);     Restaura = @(9);      Dest = "N2 SISTEMA";            Contem = "acertasequenciador" },
  @{ Nome = "3 DISCO RUIM";               Setup = @(1);     Restaura = @();       Dest = "EQUIPE T";              Contem = "TROCAR" },
  @{ Nome = "4 BANCO ALEM DE TABELAS";    Setup = @(6);     Restaura = @(9);      Dest = "EQUIPE T";              Contem = "REINSTALAR" },
  @{ Nome = "5 REPARO DO WINDOWS";        Setup = @(21);    Restaura = @();       Dest = "N2 SISTEMA";            Contem = "reparo simples" },
  @{ Nome = "6 ENERGIA";                  Setup = @(22);    Restaura = @();       Dest = "T.CNICO DA FARM";       Contem = "no-break" },
  @{ Nome = "7a N1 RESOLVE: POUCO ESPACO"; Setup = @(23);   Restaura = @(24);     Dest = "N1 \(RESOLVE";          Contem = "liberar espa" },
  @{ Nome = "7b N1 RESOLVE: DATA/HORA";   Setup = @(12);    Restaura = @();       Dest = "N1 \(RESOLVE";          Contem = "data/hora" }
)
$res = @()
foreach ($c in $casos) {
    Say ""
    Say ("######## CASO " + $c.Nome + " ########") Cyan
    Limpar-Logs
    foreach ($n in $c.Setup) { Say ("  cenario " + $n); Cenario $n }
    Start-Sleep -Seconds 3
    $rel = Pasta-Relatorios
    if (-not $rel) { Say "  Pasta DiagBIG_relatorios nao encontrada." Red; break }
    Get-ChildItem $rel -Filter *.essencial -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Say "  coletando (pode levar 1 a 2 minutos)..."
    Start-Process -FilePath $Diag -ArgumentList "--sem-janela", "--dias", "7" -Wait -WindowStyle Hidden
    $ess = Get-ChildItem $rel -Filter *.essencial -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $obt = "(sem tela essencial: o DiagBIG.exe e' da versao 1.7.0?)"; $ok = $false
    if ($ess) {
        $txt = Get-Content -Encoding UTF8 -Raw $ess.FullName
        Say "  ----- o que a tela mostraria ao N1 -----"
        $txt.TrimEnd() -split "`r?`n" | ForEach-Object { Say ("  | " + $_) }
        if ($txt -match "QUEM ACIONAR\r?\n-+\r?\n\s+(.+)") { $obt = $Matches[1].Trim() }
        $ok = ($obt -match $c.Dest) -and ($c.Contem -eq "" -or $txt -match $c.Contem)
    }
    $res += [pscustomobject]@{ Caso = $c.Nome; Obtido = $obt; Resultado = $(if ($ok) { "OK" } else { "DIVERGENTE" }) }
    foreach ($n in $c.Restaura) { Say ("  restaurando: cenario " + $n); Cenario $n }
}
Say ""
Say "================ RESUMO DOS CASOS ================" Cyan
foreach ($r in $res) { Say ("  {0,-30} {1,-12} {2}" -f $r.Caso, $r.Resultado, $r.Obtido) $(if ($r.Resultado -eq "OK") { "Green" } else { "Red" }) }
Say ""
Say "Obs.: o relogio voltou ao normal, mas os eventos de data ficam no log; os logs Sistema/Aplicativo foram limpos antes de cada caso."
Set-Content -Path $Saida -Value $linhas -Encoding UTF8
Write-Host ("Salvo em " + $Saida)
