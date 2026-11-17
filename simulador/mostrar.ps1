param([string]$Arquivo)
[Console]::OutputEncoding = [System.Text.Encoding]::GetEncoding(850)
$t = Get-Content -Encoding UTF8 -Raw $Arquivo

function Trecho($ini, $fim) {
    $i = $t.IndexOf($ini)
    if ($i -lt 0) { return $null }
    $r = $t.Substring($i)
    if ($fim) { $j = $r.IndexOf($fim); if ($j -gt 0) { $r = $r.Substring(0, $j) } }
    return $r.TrimEnd()
}

Write-Host "================ PARA O N1 ================" -ForegroundColor Cyan
$n1 = Trecho "PARA O N1" "IDENTIFICA"
if ($n1) { $n1 } else { "(bloco PARA O N1 nao apareceu)" }

Write-Host ""
Write-Host "================ ONDE ESTA O PROBLEMA ================" -ForegroundColor Cyan
$on = Trecho "ONDE EST" "O QUE FAZER (PASSO"
if ($on) { $on } else { "(secao nao apareceu: nada encontrado)" }

Write-Host ""
Write-Host "================ LINHAS DO RESUMO (disco, historico, relogio) ================" -ForegroundColor Cyan
Select-String -Path $Arquivo -Encoding UTF8 -Pattern '^\s+(DISCO|DISCO \(HIST|REL.GIO|MEM.RIA/HARDWARE)' | ForEach-Object { $_.Line }

Write-Host ""
Write-Host "================ UNIDADE / DISCO NOS EVENTOS ================" -ForegroundColor Cyan
$u = Select-String -Path $Arquivo -Encoding UTF8 -Pattern 'Unidade:|Este PC tem 1 disco|Este PC tem' | ForEach-Object { $_.Line.Trim() } | Select-Object -Unique
if ($u) { $u } else { "(nenhuma linha Unidade:)" }

Write-Host ""
Write-Host "================ RELOGIO / ULTIMO TESTE DE MEMORIA ================" -ForegroundColor Cyan
Select-String -Path $Arquivo -Encoding UTF8 -Pattern 'ATEN..O DATA/HORA|ATEN..O MY.INI|Último teste de memória|RELÓGIO DO WINDOWS|Conferir também a data|Orientar o técnico' | ForEach-Object { $_.Line.Trim() }
