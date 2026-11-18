@echo off
rem Buduje cewka_audio.dll dla win-x64 przy uzyciu kompilatora Microsoft Visual C++.
rem Wymaga zainstalowanych Visual Studio Build Tools 2022 (skladnik "Desktop development with C++").
rem Wynik trafia do runtimes\win-x64\native w projekcie Cewka.Audio.

setlocal enabledelayedexpansion

set "SCRIPT_DIR=%~dp0"
set "OUT_DIR=%SCRIPT_DIR%..\src\Cewka.Audio\runtimes\win-x64\native"

rem Pliki posrednie we wlasnym katalogu (native\build jest poza gitem): wspolny TEMP
rem dzielilyby rownolegle budowy z innych kopii repozytorium.
set "OBJ_DIR=%SCRIPT_DIR%build\obj-win-x64"
if not exist "%OBJ_DIR%" mkdir "%OBJ_DIR%"

rem --- odnalezienie srodowiska kompilatora ---
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo [blad] Nie odnaleziono vswhere.exe. Zainstaluj Visual Studio Build Tools 2022.
    exit /b 1
)

rem Wynik przez plik tymczasowy, a nie przez "for /f": sciezka do vswhere zawiera
rem nawiasy (Program Files (x86)), ktore rozwalaja parser cmd wewnatrz bloku.
set "VS_PATH_FILE=%OBJ_DIR%\vs_path.txt"
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

rem Pliki .obj trafiaja do biezacego katalogu, czyli do OBJ_DIR.
pushd "%OBJ_DIR%"

set "WV=%SCRIPT_DIR%wavpack\src"

rem /O2   optymalizacja pod katem szybkosci
rem /MT   statyczne dolinkowanie biblioteki uruchomieniowej - brak zaleznosci od redystrybutabli
rem /GS i /guard:cf z /GUARD:CF  kontrola przepelnienia stosu i przeplywu sterowania;
rem       biblioteka dekoduje pliki z niezaufanych zrodel
rem NO_TAGS  WavPack bez odczytu znacznikow (czyta je warstwa zarzadzana); bez ENABLE_DSD,
rem       ENABLE_LEGACY, ENABLE_THREADS i OPT_ASM_*: tylko dekodowanie PCM wersji 4 i 5 w C
cl /nologo /O2 /MT /GS /W3 /guard:cf /D_CRT_SECURE_NO_WARNINGS /DNO_TAGS ^
   /I"%SCRIPT_DIR%wavpack\include" ^
   /LD "%SCRIPT_DIR%cewka_audio.c" ^
   "%WV%\common_utils.c" "%WV%\decorr_utils.c" "%WV%\entropy_utils.c" "%WV%\open_utils.c" ^
   "%WV%\read_words.c" "%WV%\unpack.c" "%WV%\unpack_floats.c" "%WV%\unpack_seek.c" "%WV%\unpack_utils.c" ^
   /Fe:"%OUT_DIR%\cewka_audio.dll" ^
   /link /INCREMENTAL:NO /GUARD:CF

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
