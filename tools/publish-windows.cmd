@echo off
rem Buduje wydanie dla systemu Windows: jeden plik wykonywalny, bez wymogu srodowiska .NET.
rem Wynik trafia do artifacts\publish\win-x64\Cewka.exe
rem
rem Uzycie:  tools\publish-windows.cmd

setlocal
set KATALOG=%~dp0..

rem Biblioteka natywna musi powstac przed publikacja - to ona jest pakowana do wyniku.
if not exist "%KATALOG%\src\Cewka.Audio\runtimes\win-x64\native\cewka_audio.dll" (
    echo Brak biblioteki natywnej, budowanie...
    call "%KATALOG%\native\build-windows.cmd" || exit /b 1
)

dotnet publish "%KATALOG%\src\Cewka.App" ^
    --configuration Release ^
    --runtime win-x64 ^
    -p:PublishSingleFile=true ^
    --output "%KATALOG%\artifacts\publish\win-x64" || exit /b 1

echo.
echo Gotowe: %KATALOG%\artifacts\publish\win-x64\Cewka.exe
endlocal
