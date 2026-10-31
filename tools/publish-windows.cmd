@echo off
rem Buduje wydanie dla systemu Windows: jeden plik wykonywalny, bez wymogu srodowiska .NET.
rem Wynik trafia do artifacts\publish\win-x64\Cewka.exe
rem
rem Uzycie:  tools\publish-windows.cmd

setlocal
set KATALOG=%~dp0..

rem Biblioteka natywna musi powstac przed publikacja - to ona jest pakowana do wyniku.
rem Budowana zawsze od nowa: katalog runtimes\ jest poza gitem i po aktualizacji zrodel
rem zostalaby w nim poprzednia wersja, a cmd nie ma wygodnego porownania dat plikow.
call "%KATALOG%\native\build-windows.cmd" || exit /b 1

rem Plik poprzedniej publikacji usuwany z gory, zeby po przerwanej publikacji nie zostal
rem jako rzekomy wynik tej.
if exist "%KATALOG%\artifacts\publish\win-x64\Cewka.exe" del /q "%KATALOG%\artifacts\publish\win-x64\Cewka.exe"

dotnet publish "%KATALOG%\src\Cewka.App" ^
    --configuration Release ^
    --runtime win-x64 ^
    -p:PublishSingleFile=true ^
    --output "%KATALOG%\artifacts\publish\win-x64" || exit /b 1

echo.
echo Gotowe: %KATALOG%\artifacts\publish\win-x64\Cewka.exe
endlocal
