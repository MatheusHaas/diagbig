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
echo ===== SIMULADOR DiagBIG 1.6.7 =====
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
