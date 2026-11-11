# Buduje ikonę aplikacji z rysunków w assets/ikona:
#
#   src/Cewka.App/Assets/cewka.ico     plik wykonywalny i okna w Windows, 16–256 px, wpisy PNG
#   src/Cewka.App/Assets/cewka.png     ikona okna w innych systemach, 256 px
#   packaging/linux/icons/cewka-*.png  katalogi rozmiarów motywu hicolor w pakietach Linuksa
#
# Każdy rozmiar jest renderowany osobno z wektora, a nie zmniejszany z jednego obrazu.
# Rozmiary do 32 px biorą uproszczony rysunek cewka-maly.svg: pięć cienkich zwojów
# i wyprowadzenia pełnego rysunku zlewają się przy tej skali w miedzianą plamę, dlatego
# mały rysunek ma trzy grubsze zwoje, szersze obrzeże membrany, wyraźniejszy obrys kafla
# i nie ma wyprowadzeń. Jego kafel generator dopasowuje do każdego rozmiaru tak, by krawędź
# wypadała na całych pikselach.
#
# Renderuje Microsoft Edge w trybie bez okna, z wyciszonym dźwiękiem i profilem w katalogu
# tymczasowym, więc otwarta przeglądarka nie jest naruszana. Każdy rozmiar dostaje świeży
# profil: drugie uruchomienie na tym samym profilu potrafi zawisnąć bez zrzutu.
#
# Uruchamianie (Windows, PowerShell):  powershell -File tools/make-icon.ps1
# Inna ścieżka przeglądarki:          powershell -File tools/make-icon.ps1 -Edge "D:\Edge\msedge.exe"

param([string]$Edge)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repozytorium = Split-Path -Parent $PSScriptRoot
$rysunekPelny = Join-Path $repozytorium 'assets\ikona\cewka.svg'
$rysunekMaly  = Join-Path $repozytorium 'assets\ikona\cewka-maly.svg'
$wynikIco     = Join-Path $repozytorium 'src\Cewka.App\Assets\cewka.ico'
$wynikPng     = Join-Path $repozytorium 'src\Cewka.App\Assets\cewka.png'
$katalogIkon  = Join-Path $repozytorium 'packaging\linux\icons'

# Windows wybiera z pliku ICO rozmiar najbliższy bieżącemu skalowaniu ekranu.
$rozmiaryIco = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
# Do tego rozmiaru włącznie obowiązuje rysunek uproszczony.
$granicaMalego = 32
# Standardowe katalogi rozmiarów motywu hicolor; 22 px biorą panele KDE i XFCE, większe
# od 256 px pokrywa ikona skalowalna.
$rozmiaryLinux = 16, 22, 24, 32, 48, 64, 128, 256

if (-not $Edge) {
    $kandydaci = @()
    if (${env:ProgramFiles(x86)}) { $kandydaci += Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe' }
    if ($env:ProgramFiles) { $kandydaci += Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe' }
    $Edge = $kandydaci | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Edge -or -not (Test-Path $Edge)) {
    throw 'Nie znaleziono msedge.exe. Ścieżkę można podać parametrem -Edge.'
}
foreach ($rysunek in $rysunekPelny, $rysunekMaly) {
    if (-not (Test-Path $rysunek)) { throw "Brak rysunku $rysunek" }
}

$robocze = Join-Path ([IO.Path]::GetTempPath()) ('cewka-ikona-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $robocze | Out-Null

# Krawędź kafla na całych pikselach: margines zaokrąglony w dół do pełnego piksela (co najmniej
# jednego), a viewBox skaluje rysunek równo w obu osiach, tak by kafel wypełnił resztę obrazu.
# Margines w ułamku piksela zostawia wzdłuż krawędzi półprzezroczysty pas.
function Get-RysunekNaPikselach {
    param([string]$Rysunek, [int]$Rozmiar)

    $liczby = [Globalization.CultureInfo]::InvariantCulture
    $svg = New-Object Xml.XmlDocument
    $svg.PreserveWhitespace = $true
    $svg.Load($Rysunek)
    $przestrzen = New-Object Xml.XmlNamespaceManager($svg.NameTable)
    $przestrzen.AddNamespace('s', 'http://www.w3.org/2000/svg')
    $kafel = $svg.SelectSingleNode("/s:svg/s:rect[@fill='url(#kafel)']", $przestrzen)
    if (-not $kafel) { throw "Brak kafla (rect z fill=url(#kafel)) w $Rysunek" }

    $widok = $svg.DocumentElement.GetAttribute('viewBox') -split '\s+' | ForEach-Object { [double]::Parse($_, $liczby) }
    $x, $y, $szerokosc, $wysokosc = 'x', 'y', 'width', 'height' | ForEach-Object { [double]::Parse($kafel.GetAttribute($_), $liczby) }
    $bok = $widok[2]
    if ($widok[0] -ne 0 -or $widok[1] -ne 0 -or $widok[3] -ne $bok -or $x -ne $y -or $szerokosc -ne $wysokosc -or 2 * $x + $szerokosc -ne $bok) {
        throw "Kafel w $Rysunek nie jest kwadratem na środku kwadratowego rysunku"
    }

    $margines = [Math]::Max(1, [Math]::Floor($Rozmiar * $x / $bok))
    $pikseliNaJednostke = ($Rozmiar - 2 * $margines) / $szerokosc
    $poczatek = $x - $margines / $pikseliNaJednostke
    $bokWidoku = $Rozmiar / $pikseliNaJednostke
    $svg.DocumentElement.SetAttribute('viewBox', [string]::Format($liczby, '{0:R} {0:R} {1:R} {1:R}', $poczatek, $bokWidoku))

    return @{ Svg = [Text.Encoding]::UTF8.GetBytes($svg.OuterXml); Margines = [int]$margines }
}

function Invoke-Render {
    param([byte[]]$Svg, [int]$Rozmiar, [int]$Margines)

    $html = Join-Path $robocze "ikona-$Rozmiar.html"
    $obraz = Join-Path $robocze "ikona-$Rozmiar.png"
    $profil = Join-Path $robocze "profil-$Rozmiar"

    # Rysunek jako adres data:, bo strona z pliku lokalnego nie powinna sięgać po inne pliki.
    $dane = [Convert]::ToBase64String($Svg)
    $tresc = '<!doctype html><html><head><meta charset="utf-8"><style>' +
        'html,body{margin:0;padding:0;background:transparent;overflow:hidden}' +
        "img{display:block;width:${Rozmiar}px;height:${Rozmiar}px}" +
        "</style></head><body><img alt=`"`" src=`"data:image/svg+xml;base64,$dane`"></body></html>"
    [IO.File]::WriteAllText($html, $tresc, (New-Object Text.UTF8Encoding($false)))

    # Skala 1 niezależnie od skalowania ekranu, inaczej zrzut miałby więcej pikseli niż ikona.
    $argumenty = @(
        '--headless=new', '--disable-gpu', '--mute-audio', '--hide-scrollbars',
        '--default-background-color=00000000', '--force-device-scale-factor=1',
        "--window-size=$Rozmiar,$Rozmiar",
        "--user-data-dir=`"$profil`"",
        '--no-first-run', '--no-default-browser-check', '--disable-extensions',
        '--disable-background-networking', '--disable-component-update', '--disable-sync',
        "--screenshot=`"$obraz`"",
        ([Uri]$html).AbsoluteUri
    )
    try {
        $proces = Start-Process -FilePath $Edge -ArgumentList $argumenty -PassThru `
            -RedirectStandardOutput (Join-Path $robocze 'edge-wyjscie.txt') `
            -RedirectStandardError (Join-Path $robocze 'edge-bledy.txt')
        if (-not $proces.WaitForExit(60000)) {
            $proces.Kill()
            throw "Edge nie skończył renderowania $Rozmiar px w ciągu minuty"
        }
        if (-not (Test-Path $obraz)) { throw "Edge nie zapisał obrazu $Rozmiar px" }

        Test-Obraz -Plik $obraz -Rozmiar $Rozmiar -Margines $Margines
        # Przecinek oddaje tablicę w całości; bez niego potok rozbiłby ją na pojedyncze bajty.
        return , [IO.File]::ReadAllBytes($obraz)
    }
    finally {
        # Profil ma kilkadziesiąt megabajtów, więc znika od razu, a nie po wszystkich rozmiarach.
        Remove-Katalog $profil
    }
}

# Edge zamyka procesy pomocnicze chwilę po głównym, więc katalog bywa jeszcze zajęty.
function Remove-Katalog {
    param([string]$Sciezka)

    $czas = [Diagnostics.Stopwatch]::StartNew()
    while (Test-Path $Sciezka) {
        try {
            Remove-Item -Recurse -Force $Sciezka -ErrorAction Stop
        }
        catch {
            if ($czas.Elapsed.TotalSeconds -ge 10) {
                Write-Warning "Nie usunięto katalogu $Sciezka"
                return
            }
            Start-Sleep -Milliseconds 250
        }
    }
}

# Dokładny rozmiar, kanał alfa, przezroczyste narożniki i kryjący środek, a przy podanym marginesie
# także ostra krawędź kafla w połowie każdego boku.
function Test-Obraz {
    param([string]$Plik, [int]$Rozmiar, [int]$Margines)

    $obraz = New-Object System.Drawing.Bitmap($Plik)
    try {
        if ($obraz.Width -ne $Rozmiar -or $obraz.Height -ne $Rozmiar) {
            throw "Obraz $Plik ma $($obraz.Width)x$($obraz.Height) px zamiast $Rozmiar px"
        }
        if (-not [System.Drawing.Image]::IsAlphaPixelFormat($obraz.PixelFormat)) {
            throw "Obraz $Plik nie ma kanału alfa"
        }
        $skraj = $Rozmiar - 1
        foreach ($punkt in @(@(0, 0), @($skraj, 0), @(0, $skraj), @($skraj, $skraj))) {
            if ($obraz.GetPixel($punkt[0], $punkt[1]).A -ne 0) {
                throw "Obraz $Plik ma nieprzezroczysty narożnik"
            }
        }
        $polowa = [int][Math]::Floor($Rozmiar / 2)
        if ($obraz.GetPixel($polowa, $polowa).A -ne 255) {
            throw "Obraz $Plik ma przezroczysty środek; rysunek się nie wyrenderował"
        }
        if ($Margines -gt 0) {
            foreach ($glebokosc in ($Margines - 1), $Margines) {
                $daleka = $skraj - $glebokosc
                foreach ($punkt in @(@($polowa, $glebokosc), @($polowa, $daleka), @($glebokosc, $polowa), @($daleka, $polowa))) {
                    $krycie = $obraz.GetPixel($punkt[0], $punkt[1]).A
                    $poza = $glebokosc -lt $Margines
                    if (($poza -and $krycie -gt 8) -or (-not $poza -and $krycie -lt 250)) {
                        throw "Obraz $Plik ma rozmytą krawędź kafla: krycie $krycie w ($($punkt -join ', '))"
                    }
                }
            }
        }
    }
    finally {
        $obraz.Dispose()
    }
}

try {
    $obrazy = [ordered]@{}
    foreach ($rozmiar in ($rozmiaryIco + $rozmiaryLinux | Sort-Object -Unique)) {
        if ($rozmiar -le $granicaMalego) {
            $rysunek = Get-RysunekNaPikselach -Rysunek $rysunekMaly -Rozmiar $rozmiar
            $png = Invoke-Render -Svg $rysunek.Svg -Rozmiar $rozmiar -Margines $rysunek.Margines
        }
        else {
            $png = Invoke-Render -Svg ([IO.File]::ReadAllBytes($rysunekPelny)) -Rozmiar $rozmiar
        }
        $obrazy[[string]$rozmiar] = [byte[]]$png
    }
}
finally {
    Remove-Katalog $robocze
}

# Złożenie pliku ICO: nagłówek, tablica wpisów, a następnie obrazy zapisane jako PNG.
$plik = New-Object System.IO.MemoryStream
$zapis = New-Object System.IO.BinaryWriter($plik)

$zapis.Write([UInt16]0)                 # zarezerwowane
$zapis.Write([UInt16]1)                 # typ: ikona
$zapis.Write([UInt16]$rozmiaryIco.Count)

$przesuniecie = 6 + 16 * $rozmiaryIco.Count
foreach ($rozmiar in $rozmiaryIco) {
    $dane = $obrazy[[string]$rozmiar]
    # Rozmiar 256 zapisuje się jako zero - jeden bajt nie pomieściłby tej liczby.
    $bajtRozmiaru = if ($rozmiar -ge 256) { 0 } else { $rozmiar }

    $zapis.Write([Byte]$bajtRozmiaru)    # szerokość
    $zapis.Write([Byte]$bajtRozmiaru)    # wysokość
    $zapis.Write([Byte]0)                # liczba barw palety: brak palety
    $zapis.Write([Byte]0)                # zarezerwowane
    $zapis.Write([UInt16]1)              # płaszczyzny
    $zapis.Write([UInt16]32)             # bitów na piksel
    $zapis.Write([UInt32]$dane.Length)
    $zapis.Write([UInt32]$przesuniecie)

    $przesuniecie += $dane.Length
}

foreach ($rozmiar in $rozmiaryIco) { $zapis.Write($obrazy[[string]$rozmiar]) }

$zapis.Flush()
[IO.File]::WriteAllBytes($wynikIco, $plik.ToArray())
$zapis.Dispose()
$plik.Dispose()

# Ikona okna poza Windows jako zwykły PNG: dekodowanie formatu ICO zależy tam od zaplecza graficznego.
[IO.File]::WriteAllBytes($wynikPng, $obrazy['256'])

# Każdy rozmiar we własnym pliku, bo motyw ikon Linuksa trzyma rozmiary w osobnych katalogach;
# bez nich pulpit zmniejszałby duży obraz i mały rysunek nie trafiłby na panel.
New-Item -ItemType Directory -Force $katalogIkon | Out-Null
foreach ($rozmiar in $rozmiaryLinux) {
    [IO.File]::WriteAllBytes((Join-Path $katalogIkon "cewka-$rozmiar.png"), $obrazy[[string]$rozmiar])
}

Write-Output "Zapisano $wynikIco ($($rozmiaryIco -join ', ') px) oraz $wynikPng"
Write-Output "Ikony dla pakietów linuksowych w $katalogIkon ($($rozmiaryLinux -join ', ') px)"
