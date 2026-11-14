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
echo ===== SIMULADOR DiagBIG 1.6.1 =====
echo  Eventos do Windows:
echo   1 - Disco: bad block (disk 7)            -^> HARDWARE
echo   2 - NTFS 55 (sistema de arquivos)        -^> HARDWARE/disco
echo  Linhas no .err do banco (faz copia antes):
echo   3 - Tabela quebrada (so tabela)          -^> TABELA(S)
echo   4 - Banco nao sobe (InnoDB aborta)       -^> ARQUIVOS DO BANCO
echo   5 - Disco cheio (errno 28 / 112)         -^> ESPACO, E/S
echo   6 - ibdata1 / redo log                   -^> ARQUIVOS DO BANCO
echo   7 - Erro de memoria (MAGIC_N)            -^> MOTOR DO BANCO
echo   8 - Arquivo em uso / acesso negado       -^> ESPACO, E/S, ACESSO
echo   9 - RESTAURAR o .err original
echo  Coleta:
echo   C - Rodar o DiagBIG (sem janela) e mostrar "ONDE ESTA O PROBLEMA"
echo   0 - Sair
set /p op=Escolha: 
if /i "%op%"=="0" exit /b 0
if /i "%op%"=="C" goto coletar
echo opcao %op% >> "%LOG%"
%PS% -Cenario %op% >> "%LOG%" 2>&1
echo (erro level %errorlevel%) >> "%LOG%"
powershell -NoProfile -Command "Get-Content '%LOG%' -Tail 8"
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
powershell -NoProfile -Command "$t=Get-Content -Encoding UTF8 -Raw '%RP%'; $i=$t.IndexOf('ONDE EST'); if($i -ge 0){ $r=$t.Substring($i); $j=$r.IndexOf('CAUSAS PROV'); if($j -gt 0){$r=$r.Substring(0,$j)}; $r } else { 'Secao ONDE ESTA O PROBLEMA nao apareceu (nada encontrado).' }"
goto menu
