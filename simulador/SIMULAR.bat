@echo off
chcp 850 >nul
setlocal
net session >nul 2>&1
if errorlevel 1 (
  echo Execute como ADMINISTRADOR: botao direito no SIMULAR.bat, "Executar como administrador".
  pause
  exit /b 1
)
set LOG=%~dp0simulador-log.txt
echo ==== %date% %time% ==== >> "%LOG%"
ver >> "%LOG%"
whoami >> "%LOG%"
echo pasta: %~dp0 >> "%LOG%"
set PS=powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0simular.ps1"
set DIAG=%~dp0..\DiagBIG.exe
if not exist "%DIAG%" set DIAG=%~dp0DiagBIG.exe

:menu
echo.
echo ===== SIMULADOR DiagBIG 1.6.7 (teste5) =====
echo  Eventos do Windows:
echo   1 - Disco: bad block (disk 7, com ID Harddisk0\DR0)   -^> TROCAR DISCO
echo   2 - NTFS 55 com "volume ??" (sem unidade)               -^> Unidade NAO INFORMADA
echo  13 - NTFS 55 com "volume D:"                            -^> Unidade: D:
echo  Linhas no .err do banco (faz copia antes):
echo   3 - Tabela quebrada (so tabela)          -^> TABELA(S)
echo   4 - Banco nao sobe (InnoDB aborta)       -^> ARQUIVOS DO BANCO
echo   5 - Disco cheio (errno 28 / 112)         -^> ESPACO, E/S
echo   6 - ibdata1 / redo log                   -^> ARQUIVOS DO BANCO
echo   7 - Erro de memoria (MAGIC_N)            -^> MOTOR DO BANCO
echo   8 - Arquivo em uso / acesso negado       -^> ESPACO, E/S, ACESSO
echo   9 - RESTAURAR o .err original
echo  my.ini e relogio (1.6.6):
echo  10 - my.ini com innodb_doublewrite=OFF e flush=2 (reinicia o servico)
echo  11 - RESTAURAR o my.ini original
echo  12 - Relogio: volta a data 3 dias e corrige (evento Kernel-General 1)
echo  Correcoes pos-1.6.7 (teste5):
echo  14 - disk 153 citando "Disco 0" (3x)                   -^> DISCO COM ERRO: Disco 0 = modelo, unidades
echo  15 - DISM com opcao errada (0x80070057)                 -^> "nao indica disco"
echo  16 - Criar volume NTFS de 50 MB (VHD)                   -^> "volume pequeno", sem POUCO ESPACO
echo  17 - REMOVER o volume de 50 MB
echo  18 - Banco "recem-instalado" (ibdata1 criado agora)     -^> BANCO NOVO JA CORROMPENDO (com 6 e 14)
echo  19 - RESTAURAR a data de criacao do ibdata1
echo  20 - Bad block SEM \Device\Harddisk no texto           -^> "o PC tem so um: modelo"
echo   T - TODOS DE UMA VEZ: 14, 15, 16, 18, 6, 10, 12, coleta, mostra tudo e restaura (11, 9, 19, 17)
echo  Coleta e limpeza:
echo   C - Rodar o DiagBIG (sem janela) e mostrar N1, ONDE ESTA O PROBLEMA, relogio, unidade
echo   G - Rodar o DiagBIG COM janela (ver botoes Testar memoria e Verificar disco)
echo   L - LIMPAR o log Sistema (so em VM de teste: apaga os eventos simulados)
echo   0 - Sair
set /p op=Escolha: 
if /i "%op%"=="0" exit /b 0
if /i "%op%"=="C" goto coletar
if /i "%op%"=="G" goto janela
if /i "%op%"=="L" goto limpar
if /i "%op%"=="T" goto todos
echo opcao %op% >> "%LOG%"
%PS% -Cenario %op% >> "%LOG%" 2>&1
echo (erro level %errorlevel%) >> "%LOG%"
powershell -NoProfile -Command "Get-Content '%LOG%' -Tail 8"
goto menu

:janela
if not exist "%DIAG%" (echo DiagBIG.exe nao encontrado. & goto menu)
start "" "%DIAG%"
goto menu

:limpar
set /p ok=Isto APAGA todo o log Sistema desta VM. Digite SIM para confirmar: 
if /i not "%ok%"=="SIM" goto menu
wevtutil cl System
echo Log Sistema limpo. >> "%LOG%"
echo Log Sistema limpo.
goto menu

:coletar
if not exist "%DIAG%" (echo DiagBIG.exe nao encontrado. & goto menu)
echo coleta: %DIAG% >> "%LOG%"
"%DIAG%" --sem-janela >> "%LOG%" 2>&1
echo (DiagBIG saiu com %errorlevel%) >> "%LOG%"
for /f "delims=" %%f in ('dir /b /o-d "%~dp0..\DiagBIG_relatorios\*.txt" "%~dp0DiagBIG_relatorios\*.txt" 2^>nul') do (set REL=%%f & goto achou)
echo Relatorio nao encontrado em DiagBIG_relatorios.
goto menu
:achou
if exist "%~dp0..\DiagBIG_relatorios\%REL%" (set RP=%~dp0..\DiagBIG_relatorios\%REL%) else (set RP=%~dp0DiagBIG_relatorios\%REL%)
echo Relatorio: %RP%
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0mostrar.ps1" -Arquivo "%RP%"
goto menu

:todos
echo.
echo ==== TESTE COMPLETO (todos de uma vez) ==== >> "%LOG%"
echo O banco sera parado/iniciado varias vezes e o relogio mexido. Pode levar alguns minutos.
for %%c in (14 15 16 18 6 10 12) do (
  echo [cenario %%c]
  echo opcao %%c >> "%LOG%"
  %PS% -Cenario %%c >> "%LOG%" 2>&1
)
echo.
echo [coletando]
call :fazcoleta
echo.
echo [restaurando: .err, my.ini, data do ibdata1 e volume de 50 MB]
for %%c in (11 9 19 17) do (
  echo opcao %%c >> "%LOG%"
  %PS% -Cenario %%c >> "%LOG%" 2>&1
)
powershell -NoProfile -Command "Get-Content '%LOG%' -Tail 6"
echo.
echo Pronto. O relatorio esta em DiagBIG_relatorios (copie o TXT mais recente para o compartilhado).
echo Obs.: o relogio foi corrigido, mas os 2 eventos de data ficam no log do Windows.
goto menu

:fazcoleta
if not exist "%DIAG%" (echo DiagBIG.exe nao encontrado. & exit /b 1)
"%DIAG%" --sem-janela >> "%LOG%" 2>&1
set REL=
for /f "delims=" %%f in ('dir /b /o-d "%~dp0..\DiagBIG_relatorios\*.txt" "%~dp0DiagBIG_relatorios\*.txt" 2^>nul') do (if not defined REL set REL=%%f)
if not defined REL (echo Relatorio nao encontrado em DiagBIG_relatorios. & exit /b 1)
if exist "%~dp0..\DiagBIG_relatorios\%REL%" (set RP=%~dp0..\DiagBIG_relatorios\%REL%) else (set RP=%~dp0DiagBIG_relatorios\%REL%)
echo Relatorio: %RP%
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0mostrar.ps1" -Arquivo "%RP%"
exit /b 0
