# Simulador de cenarios para testar o DiagBIG na VM. Roda como administrador.
param([int]$Cenario = 0, [string]$Err = "")

function Achar-Err {
    if ($Err -and (Test-Path $Err)) { return $Err }
    $c = @()
    foreach ($base in @("C:\Program Files", "C:\Program Files (x86)")) {
        $c += Get-ChildItem -Path $base -Directory -Filter "MariaDB*" -ErrorAction SilentlyContinue |
              ForEach-Object { Join-Path $_.FullName ("data\" + $env:COMPUTERNAME + ".err") }
        $c += Get-ChildItem -Path $base -Directory -Filter "MySQL*" -ErrorAction SilentlyContinue |
              ForEach-Object { Join-Path $_.FullName ("data\" + $env:COMPUTERNAME + ".err") }
    }
    $c += "C:\ProgramData\MySQL\MySQL Server 5.1\Data\$($env:COMPUTERNAME).err"
    $c += "C:\mysql\data\mysql.err"
    foreach ($p in $c) { if (Test-Path $p) { return $p } }
    return $null
}

function Evento($fonte, $id, $msg) {
    Write-EventLog -LogName System -Source $fonte -EventId $id -EntryType Error -Message $msg
    Write-Host "Evento gravado: $fonte $id"
}

function Servico-Banco {
    Get-WmiObject Win32_Service | Where-Object { $_.PathName -match 'mysqld|mariadbd' } | Select-Object -First 1
}

# O banco mantem o .err aberto: para o servico, executa o bloco e sobe o servico de novo.
function Com-Servico($bloco) {
    $svc = Servico-Banco
    $parou = $false
    if ($svc -and $svc.State -eq 'Running') {
        Write-Host "Parando o servico $($svc.Name) ..."
        Stop-Service -Name $svc.Name -Force -ErrorAction SilentlyContinue
        (Get-Service -Name $svc.Name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        $parou = $true
    }
    try { & $bloco } catch { Write-Host "ERRO: $($_.Exception.Message)" -ForegroundColor Red }
    if ($parou) {
        Write-Host "Subindo o servico $($svc.Name) ..."
        Start-Service -Name $svc.Name -ErrorAction SilentlyContinue
        Write-Host ("Servico: " + (Get-Service -Name $svc.Name).Status + "")
    }
}

function Linhas-Err($linhas) {
    $f = Achar-Err
    if (-not $f) { Write-Host "NAO ACHEI o .err. Passe o caminho: simular.ps1 -Err <arquivo>" -ForegroundColor Red; return }
    Com-Servico {
        $bak = $f + ".bak-diagbig"
        if (-not (Test-Path $bak)) { Copy-Item $f $bak; Write-Host "Copia de seguranca: $bak" }
        $t = (Get-Date).AddMinutes(-5).ToString("yyyy-MM-dd HH:mm:ss")
        $linhas | ForEach-Object { Add-Content -Path $f -Value ($t + " " + $_) -Encoding Default }
        Write-Host "Linhas gravadas em $f"
    }
}

# my.ini: acha pelo --defaults-file do servico; senao, ao lado do .err (pasta data).
function Achar-Ini {
    $svc = Servico-Banco
    if ($svc -and $svc.PathName -match '--defaults-file=("?)(.+?\.(ini|cnf))') {
        if (Test-Path $Matches[2]) { return $Matches[2] }
    }
    $e = Achar-Err
    if ($e) {
        $p = Join-Path (Split-Path $e) "my.ini"
        if (Test-Path $p) { return $p }
    }
    return $null
}

# Coloca innodb_doublewrite=OFF e innodb_flush_log_at_trx_commit=2 no [mysqld] (faz copia antes) e reinicia o servico.
function Ini-Risco {
    $f = Achar-Ini
    if (-not $f) { Write-Host "NAO ACHEI o my.ini." -ForegroundColor Red; return }
    Com-Servico {
        $bak = $f + ".bak-diagbig"
        if (-not (Test-Path $bak)) { Copy-Item $f $bak; Write-Host "Copia de seguranca: $bak" }
        $lin = @(Get-Content -Path $f -Encoding Default | Where-Object { $_ -notmatch '^\s*innodb_doublewrite\s*=' -and $_ -notmatch '^\s*innodb_flush_log_at_trx_commit\s*=' })
        $novo = New-Object System.Collections.ArrayList
        $ok = $false
        foreach ($l in $lin) {
            [void]$novo.Add($l)
            if (-not $ok -and $l -match '^\s*\[mysqld\]\s*$') {
                [void]$novo.Add("innodb_doublewrite=OFF")
                [void]$novo.Add("innodb_flush_log_at_trx_commit=2")
                $ok = $true
            }
        }
        if (-not $ok) { Write-Host "Sem [mysqld] no my.ini: nada alterado." -ForegroundColor Red; return }
        Set-Content -Path $f -Value $novo -Encoding Default
        Write-Host "my.ini alterado: $f"
    }
}

function Ini-Restaurar {
    $f = Achar-Ini
    if ($f -and (Test-Path ($f + ".bak-diagbig"))) {
        Com-Servico { Copy-Item ($f + ".bak-diagbig") $f -Force; Remove-Item ($f + ".bak-diagbig"); Write-Host "Restaurado: $f" }
    } else { Write-Host "Nada para restaurar." }
}

# Relogio: volta a data 3 dias e corrige em seguida. O Windows registra 2 eventos Kernel-General 1 (salto de 3 dias).
function Relogio-Saltar {
    Write-Host "Voltando o relogio 3 dias e corrigindo em seguida..."
    try { Stop-Service -Name w32time -Force -ErrorAction SilentlyContinue } catch { }
    Set-Date -Adjust (New-TimeSpan -Days -3) | Out-Null
    Start-Sleep -Seconds 3
    Set-Date -Adjust (New-TimeSpan -Days 3 -Seconds 3) | Out-Null
    try { Start-Service -Name w32time -ErrorAction SilentlyContinue } catch { }
    Write-Host ("Relogio de volta: " + (Get-Date))
}

switch ($Cenario) {
 1 { Evento "disk" 7 "O dispositivo \Device\Harddisk0\DR0 tem um bloco invalido (bad block)." }
 2 { Evento "Ntfs" 55 "Corrupcao detectada na estrutura do sistema de arquivos, no volume ??." }
 3 { Linhas-Err @(
      "8 [ERROR] mysqld: Table '.\loja\movimento' is marked as crashed and should be repaired",
      "9 [ERROR] mysqld: Table '.\loja\movimento' is marked as crashed and should be repaired",
      "9 [ERROR] Got error 145 when reading table '.\loja\estoque'") }
 4 { Linhas-Err @(
      "0 [ERROR] InnoDB: Plugin initialization aborted with error Data structure corruption",
      "0 [ERROR] Plugin 'InnoDB' init function returned error.",
      "0 [ERROR] Plugin 'InnoDB' registration as a STORAGE ENGINE failed.",
      "0 [ERROR] Aborting") }
 5 { Linhas-Err @(
      "15 [ERROR] mysqld: Disk full (C:\Program Files\MariaDB\data\loja\#sql-1a2c_f.MAI); waiting for someone to free some space... (errno: 28 ""No space left on device"")",
      "15 [ERROR] InnoDB: Write to file .\loja\log_vendas.ibd failed. Operating system error number 112.") }
 6 { Linhas-Err @(
      "0 [ERROR] InnoDB: Database page corruption on disk or a failed file read of tablespace ibdata1 page [page id: space=0, page number=7]. You may have to recover from a backup.",
      "0 [ERROR] InnoDB: The log sequence number 9999999 in the system tablespace is in the future",
      "0 [ERROR] Plugin 'InnoDB' init function returned error.") }
 7 { Linhas-Err @(
      "0 [ERROR] InnoDB: Assertion failure in file row0sel.cc line 3410",
      "InnoDB: Failing assertion: prebuilt->magic_n == ROW_PREBUILT_FETCH_MAGIC_N",
      "0 [ERROR] mysqld got exception 0x80000003 ;") }
 8 { Linhas-Err @(
      "11 [ERROR] InnoDB: Operating system error number 32 in a file operation.",
      "11 [ERROR] InnoDB: The error means that another program is using the file (Access is denied).") }
 9 { $f = Achar-Err
     if ($f -and (Test-Path ($f + ".bak-diagbig"))) { Com-Servico { Copy-Item ($f + ".bak-diagbig") $f -Force; Remove-Item ($f + ".bak-diagbig"); Write-Host "Restaurado: $f" } }
     else { Write-Host "Nada para restaurar." } }
 10 { Ini-Risco }
 11 { Ini-Restaurar }
 12 { Relogio-Saltar }
 13 { Evento "Ntfs" 55 "Corrupcao detectada na estrutura do sistema de arquivos, no volume D:." }
 default { Write-Host "Cenario invalido." }
}
