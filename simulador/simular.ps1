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

function Linhas-Err($linhas) {
    $f = Achar-Err
    if (-not $f) { Write-Host "NAO ACHEI o .err. Passe o caminho: SIMULAR.bat" -ForegroundColor Red; return }
    $bak = $f + ".bak-diagbig"
    if (-not (Test-Path $bak)) { Copy-Item $f $bak; Write-Host "Copia de seguranca: $bak" }
    $t = (Get-Date).AddMinutes(-5).ToString("yyyy-MM-dd HH:mm:ss")
    $linhas | ForEach-Object { Add-Content -Path $f -Value ($t + " " + $_) -Encoding Default }
    Write-Host "Linhas gravadas em $f"
}

switch ($Cenario) {
 1 { Evento "disk" 7 "O dispositivo tem um bloco invalido (bad block)." }
 2 { Evento "Ntfs" 55 "Estrutura do sistema de arquivos corrompida." }
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
     if ($f -and (Test-Path ($f + ".bak-diagbig"))) { Copy-Item ($f + ".bak-diagbig") $f -Force; Remove-Item ($f + ".bak-diagbig"); Write-Host "Restaurado: $f" }
     else { Write-Host "Nada para restaurar." } }
 default { Write-Host "Cenario invalido." }
}
