@echo off
rem Buduje cewka_audio.dll dla win-x64 przy uzyciu kompilatora Microsoft Visual C++.
rem Wymaga zainstalowanych Visual Studio Build Tools 2022 (skladnik "Desktop development with C++").
rem Wynik trafia do runtimes\win-x64\native w projekcie Cewka.Audio.

setlocal enabledelayedexpansion

set "SCRIPT_DIR=%~dp0"
set "OUT_DIR=%SCRIPT_DIR%..\src\Cewka.Audio\runtimes\win-x64\native"

rem --- odnalezienie srodowiska kompilatora ---
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo [blad] Nie odnaleziono vswhere.exe. Zainstaluj Visual Studio Build Tools 2022.
    exit /b 1
)

rem Wynik przez plik tymczasowy, a nie przez "for /f": sciezka do vswhere zawiera
rem nawiasy (Program Files (x86)), ktore rozwalaja parser cmd wewnatrz bloku.
set "VS_PATH_FILE=%TEMP%\cewka_vs_path.txt"
"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath > "%VS_PATH_FILE%"
set "VS_PATH="
if exist "%VS_PATH_FILE%" set /p VS_PATH=<"%VS_PATH_FILE%"
del /q "%VS_PATH_FILE%" 2>nul

if not defined VS_PATH (
    echo [blad] Nie odnaleziono narzedzi C++. Doinstaluj skladnik "Desktop development with C++".
    exit /b 1
)

rem Skrypty vcvars firmy Microsoft wypisuja na wyjscie bledow wlasne komunikaty
rem diagnostyczne nawet przy powodzeniu; wyciszamy oba strumienie.
call "%VS_PATH%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>nul
if errorlevel 1 (
    echo [blad] Nie udalo sie przygotowac srodowiska kompilatora.
    exit /b 1
)

if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"

pushd "%SCRIPT_DIR%"

rem /O2   optymalizacja pod katem szybkosci
rem /MT   statyczne dolinkowanie biblioteki uruchomieniowej - brak zaleznosci od redystrybutabli
rem /GS-  wylaczenie kontroli bufora w kodzie krytycznym czasowo
cl /nologo /O2 /MT /GS- /W3 /D_CRT_SECURE_NO_WARNINGS ^
   /LD cewka_audio.c ^
   /Fe:"%OUT_DIR%\cewka_audio.dll" ^
   /Fo:"%TEMP%\cewka_audio.obj" ^
   /link /INCREMENTAL:NO

set "BUILD_RESULT=%ERRORLEVEL%"
popd

if not "%BUILD_RESULT%"=="0" (
    echo [blad] Kompilacja nie powiodla sie.
    exit /b %BUILD_RESULT%
)

del /q "%OUT_DIR%\cewka_audio.exp" 2>nul
del /q "%OUT_DIR%\cewka_audio.lib" 2>nul

echo [ok] Zbudowano: %OUT_DIR%\cewka_audio.dll
endlocal
