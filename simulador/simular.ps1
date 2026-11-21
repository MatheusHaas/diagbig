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

function Evento($fonte, $id, $msg, $tipo = "Error") {
    Write-EventLog -LogName System -Source $fonte -EventId $id -EntryType $tipo -Message $msg
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

# 14: disk 153 (E/S repetida) citando "Disco 0": o relatorio deve mostrar qual disco (modelo e unidades).
function Disco153 {
    for ($i = 0; $i -lt 3; $i++) {
        Evento "disk" 153 "A operacao de ES no endereco de bloco logico 0xde94440 para o Disco 0 (nome PDO: \Device\00000041) foi repetida." "Warning"
    }
}

# 15: DISM digitado errado (opcao invalida): erro 87 / 0x80070057, que NAO indica disco.
function Dism-Errado {
    Write-Host "Rodando DISM com opcao errada (de proposito) ..."
    & dism.exe /Online /Cleanup-Image /RestoreHealt 2>&1 | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
}

# 16/17: volume NTFS pequeno (50 MB) em um VHD, com letra livre. O relatorio nao deve alertar POUCO ESPACO nele.
$VhdArq = "C:\diagbig-vhd50.vhd"
function Vhd-Criar {
    if (Test-Path $VhdArq) { Write-Host "O VHD de 50 MB ja existe: $VhdArq"; return }
    $letra = $null
    # Letras em uso inclui leitor de CD/DVD vazio (Test-Path dá falso nele); procura de R a Z.
    $usadas = @([System.IO.DriveInfo]::GetDrives() | ForEach-Object { $_.Name.Substring(0, 1).ToUpper() })
    foreach ($n in 82..90) { $l = [string][char]$n; if ($usadas -notcontains $l) { $letra = $l; break } }
    if (-not $letra) { Write-Host "Sem letra livre." -ForegroundColor Red; return }
    $dp = @("create vdisk file=""$VhdArq"" maximum=50 type=fixed", "select vdisk file=""$VhdArq""", "attach vdisk",
            "create partition primary", "format fs=ntfs quick label=DIAGBIG50", "assign letter=$letra")
    $tmp = Join-Path $env:TEMP "diagbig-dp.txt"
    Set-Content -Path $tmp -Value $dp -Encoding ASCII
    & diskpart.exe /s $tmp | ForEach-Object { if ($_ -match '\S') { Write-Host ("  diskpart: " + $_) } }
    Start-Sleep -Seconds 2
    if (Test-Path ($letra + ":\")) { Write-Host "Volume de 50 MB criado em ${letra}: (arquivo $VhdArq)" }
    else { Write-Host "ATENCAO: o VHD foi criado mas a letra ${letra}: NAO apareceu (veja as linhas do diskpart acima)." -ForegroundColor Red }
}
function Vhd-Remover {
    if (-not (Test-Path $VhdArq)) { Write-Host "Nada para remover."; return }
    $tmp = Join-Path $env:TEMP "diagbig-dp.txt"
    Set-Content -Path $tmp -Value @("select vdisk file=""$VhdArq""", "detach vdisk") -Encoding ASCII
    & diskpart.exe /s $tmp | Out-Null
    Remove-Item $VhdArq -Force -ErrorAction SilentlyContinue
    Write-Host "Volume de 50 MB removido."
}

# 18/19: faz o banco parecer recem-instalado (data de criacao do ibdata1 = agora) e volta o valor original.
function Achar-Ibdata {
    $e = Achar-Err
    if (-not $e) { return $null }
    $p = Join-Path (Split-Path $e) "ibdata1"
    if (Test-Path $p) { return $p }
    return $null
}
function Banco-Novo {
    $ib = Achar-Ibdata
    if (-not $ib) { Write-Host "NAO ACHEI o ibdata1." -ForegroundColor Red; return }
    Com-Servico {
        $bak = $ib + ".criacao-diagbig"
        if (-not (Test-Path $bak)) { Set-Content -Path $bak -Value (Get-Item $ib).CreationTime.ToString("o") -Encoding ASCII }
        (Get-Item $ib).CreationTime = Get-Date
        Write-Host ("ibdata1 agora com criacao em " + (Get-Item $ib).CreationTime)
    }
}
function Banco-Novo-Restaurar {
    $ib = Achar-Ibdata
    if (-not $ib -or -not (Test-Path ($ib + ".criacao-diagbig"))) { Write-Host "Nada para restaurar."; return }
    Com-Servico {
        $orig = [datetime]::Parse((Get-Content ($ib + ".criacao-diagbig") -Raw).Trim(), $null, [System.Globalization.DateTimeStyles]::RoundtripKind)
        (Get-Item $ib).CreationTime = $orig
        Remove-Item ($ib + ".criacao-diagbig") -Force
        Write-Host "Data de criacao do ibdata1 restaurada: $orig"
    }
}

# 23/24: enche o C: ate sobrar ~4 GB (alerta POUCO ESPACO, caso "N1 resolve") e depois apaga o arquivo.
$EncheArq = "C:\diagbig-enche.bin"
function Encher-C {
    if (Test-Path $EncheArq) { Write-Host "O arquivo de enchimento ja existe: $EncheArq"; return }
    $livre = [System.IO.DriveInfo]::new("C").AvailableFreeSpace
    $tam = $livre - 4GB
    if ($tam -le 0) { Write-Host "O C: ja tem menos de 4 GB livres; nada a fazer."; return }
    & fsutil.exe file createnew $EncheArq $tam | ForEach-Object { Write-Host ("  fsutil: " + $_) }
    Write-Host ("C: agora tem " + [math]::Round([System.IO.DriveInfo]::new("C").AvailableFreeSpace / 1GB, 1) + " GB livres")
}
function Encher-C-Remover {
    if (Test-Path $EncheArq) { Remove-Item $EncheArq -Force; Write-Host "Arquivo de enchimento removido." } else { Write-Host "Nada para remover." }
}

# ---- Botao "Manutencao do banco (force 3307)" do DiagBIG 1.7.0 ----
# 25: prepara o caso 2 (tabela quebrada no .err) + my.ini com doublewrite=OFF e flush=2, para clicar no botao.
function Botao-Preparar {
    Write-Host "Preparando: tabela quebrada no .err (cenario 3) e my.ini com doublewrite=OFF/flush=2 (cenario 10)."
    Linhas-Err @(
      "8 [ERROR] mysqld: Table '.\loja\movimento' is marked as crashed and should be repaired",
      "9 [ERROR] mysqld: Table '.\loja\movimento' is marked as crashed and should be repaired",
      "9 [ERROR] Got error 145 when reading table '.\loja\estoque'")
    Ini-Risco
    Write-Host "Pronto. Agora: DiagBIG.exe (opcao G) > 'Manutencao do banco (force 3307)' > botao 1; depois a opcao 27 mostra o estado." -ForegroundColor Green
}

# 27: mostra o estado do my.ini (linhas que o botao mexe, comentarios DiagBIG, arquivos de backup) e do servico.
function Botao-Estado {
    $f = Achar-Ini
    if (-not $f) { Write-Host "NAO ACHEI o my.ini." -ForegroundColor Red; return }
    Write-Host "my.ini: $f"
    foreach ($suf in @(".diagbig-original", ".bak-diagbig")) { Write-Host ("  " + $suf + ": " + $(if (Test-Path ($f + $suf)) { "EXISTE" } else { "nao existe" })) }
    Get-Content -Path $f -Encoding Default | Where-Object { $_ -match '^\s*(port|innodb_force_recovery|innodb_doublewrite|skip.innodb.doublewrite|innodb_flush_log_at_trx_commit)\s*(=|$)|DiagBIG' } |
        ForEach-Object { Write-Host ("  | " + $_) }
    $svc = Servico-Banco
    if ($svc) { Write-Host ("Servico " + $svc.Name + ": " + (Get-Service -Name $svc.Name).Status) }
}

# 26: desfaz TUDO do teste do botao: my.ini (o guardado pelo botao e depois o do simulador), .err, e deixa o servico rodando.
function Botao-Limpar {
    $f = Achar-Ini
    $svc = Servico-Banco
    if ($svc -and (Get-Service -Name $svc.Name).Status -ne 'Stopped') {
        Write-Host "Parando o servico $($svc.Name) ..."
        Stop-Service -Name $svc.Name -Force -ErrorAction SilentlyContinue
        try { (Get-Service -Name $svc.Name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) } catch { }
    }
    if ($f) {
        foreach ($suf in @(".diagbig-original", ".bak-diagbig")) {
            if (Test-Path ($f + $suf)) { Copy-Item ($f + $suf) $f -Force; Remove-Item ($f + $suf); Write-Host "Restaurado de $suf" }
        }
        $sobra = @(Get-Content -Path $f -Encoding Default | Where-Object { $_ -match '^\s*innodb_force_recovery\s*=' -or $_ -match '^\s*port\s*=\s*3307' })
        if ($sobra.Count -gt 0) { Write-Host "ATENCAO: o my.ini ainda tem force/porta 3307 (ja estava assim antes do teste?): $($sobra -join ' | ')" -ForegroundColor Yellow }
    } else { Write-Host "NAO ACHEI o my.ini." -ForegroundColor Red }
    $e = Achar-Err
    if ($e -and (Test-Path ($e + ".bak-diagbig"))) { Copy-Item ($e + ".bak-diagbig") $e -Force; Remove-Item ($e + ".bak-diagbig"); Write-Host "Restaurado o .err: $e" }
    if ($svc) {
        Write-Host "Subindo o servico $($svc.Name) ..."
        Start-Service -Name $svc.Name -ErrorAction SilentlyContinue
        try { (Get-Service -Name $svc.Name).WaitForStatus('Running', [TimeSpan]::FromSeconds(60)) } catch { }
        Write-Host ("Servico: " + (Get-Service -Name $svc.Name).Status)
    }
    Write-Host "Limpeza do teste do botao concluida." -ForegroundColor Green
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
 14 { Disco153 }
 15 { Dism-Errado }
 16 { Vhd-Criar }
 17 { Vhd-Remover }
 18 { Banco-Novo }
 19 { Banco-Novo-Restaurar }
 20 { Evento "disk" 7 "O dispositivo tem um bloco invalido (bad block)." }
 21 { Evento "BugCheck" 1001 "O computador foi reiniciado depois de uma verificacao de bug. A verificacao de bug foi: 0x000000d1 (0x0000000000000008, 0x0000000000000002, 0x0000000000000000, 0xfffff80012345678)." }
 22 { Evento "EventLog" 6008 ("O desligamento anterior do sistema em " + (Get-Date).AddMinutes(-20).ToString("HH:mm:ss") + " de " + (Get-Date).ToString("dd/MM/yyyy") + " foi inesperado.") }
 23 { Encher-C }
 24 { Encher-C-Remover }
 25 { Botao-Preparar }
 26 { Botao-Limpar }
 27 { Botao-Estado }
 default { Write-Host "Cenario invalido." }
}
