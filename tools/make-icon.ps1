# Buduje ikonę aplikacji: src/Cewka.App/Assets/cewka.ico
#
# Ikona rysowana jest proceduralnie, a nie skalowana z jednego obrazu, ponieważ spirala
# o pięciu zwojach zlewa się w plamę przy szesnastu pikselach. Każdy rozmiar dostaje
# liczbę zwojów i grubość kreski dobrane do siebie, dzięki czemu ikona pozostaje
# czytelna zarówno na pasku zadań, jak i w oknie właściwości pliku.
#
# Uruchamianie (Windows, PowerShell):  powershell -File tools/make-icon.ps1

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'

$repozytorium = Split-Path -Parent $PSScriptRoot
$wynik = Join-Path $repozytorium 'src\Cewka.App\Assets\cewka.ico'

# Barwy z motywu ciemnego: tło pulpitu i akcent interfejsu.
$tlo     = [System.Drawing.Color]::FromArgb(255, 22, 22, 26)
$akcent  = [System.Drawing.Color]::FromArgb(255, 127, 182, 239)
$srodek  = [System.Drawing.Color]::FromArgb(255, 246, 246, 250)

# Rozmiar, liczba zwojów spirali, promień środkowej kropki jako ułamek rozmiaru.
#
# Liczba zwojów jest dobrana tak, aby odstęp między nimi pozostawał wyraźnie większy od
# grubości kreski. Przy pięciu zwojach na szesnastu pikselach kreska wypełnia cały odstęp
# i spirala zlewa się w jednolitą plamę — dlatego małe rozmiary dostają jeden zwój,
# co przy tej skali czyta się po prostu jako pierścień z przerwą.
$warianty = @(
    @{ Rozmiar = 16;  Zwoje = 1.00; Kropka = 0.130 }
    @{ Rozmiar = 24;  Zwoje = 1.50; Kropka = 0.115 }
    @{ Rozmiar = 32;  Zwoje = 2.00; Kropka = 0.105 }
    @{ Rozmiar = 48;  Zwoje = 2.50; Kropka = 0.100 }
    @{ Rozmiar = 64;  Zwoje = 3.00; Kropka = 0.095 }
    @{ Rozmiar = 128; Zwoje = 3.50; Kropka = 0.090 }
    @{ Rozmiar = 256; Zwoje = 4.00; Kropka = 0.085 }
)

function New-SciezkaZaokraglona {
    param([single]$Rozmiar, [single]$Promien)

    $sciezka = New-Object System.Drawing.Drawing2D.GraphicsPath
    $srednica = $Promien * 2
    $sciezka.AddArc(0, 0, $srednica, $srednica, 180, 90)
    $sciezka.AddArc($Rozmiar - $srednica, 0, $srednica, $srednica, 270, 90)
    $sciezka.AddArc($Rozmiar - $srednica, $Rozmiar - $srednica, $srednica, $srednica, 0, 90)
    $sciezka.AddArc(0, $Rozmiar - $srednica, $srednica, $srednica, 90, 90)
    $sciezka.CloseFigure()
    return $sciezka
}

function New-Obraz {
    param([int]$Rozmiar, [double]$Zwoje, [double]$Kropka)

    $obraz = New-Object System.Drawing.Bitmap($Rozmiar, $Rozmiar, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($obraz)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    # Tło: zaokrąglony kwadrat, tak jak przystoi ikonie w systemie Windows 11.
    $ksztalt = New-SciezkaZaokraglona -Rozmiar $Rozmiar -Promien ([single]($Rozmiar * 0.22))
    $pedzel = New-Object System.Drawing.SolidBrush($tlo)
    $g.FillPath($pedzel, $ksztalt)
    $pedzel.Dispose()
    $ksztalt.Dispose()

    # Spirala Archimedesa od środka na zewnątrz.
    $srodekXY = $Rozmiar / 2.0
    $promienMax = $Rozmiar * 0.375
    $promienMin = $Rozmiar * $Kropka * 1.75
    $kroki = [int]([Math]::Max(64, $Rozmiar * 4))
    $kat = $Zwoje * 2 * [Math]::PI

    $punkty = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    for ($i = 0; $i -le $kroki; $i++) {
        $t = $i / [double]$kroki
        $r = $promienMin + ($promienMax - $promienMin) * $t
        $a = $kat * $t
        $punkty.Add((New-Object System.Drawing.PointF(
            [single]($srodekXY + $r * [Math]::Cos($a)),
            [single]($srodekXY + $r * [Math]::Sin($a)))))
    }

    # Grubość kreski wyprowadzona z odstępu między zwojami, nie z samego rozmiaru: dopiero
    # ta proporcja decyduje o tym, czy spirala pozostaje spiralą, czy zlewa się w krążek.
    $odstep = ($promienMax - $promienMin) / [Math]::Max(1.0, $Zwoje)
    $grubosc = [single]([Math]::Max(1.0, [Math]::Min($odstep * 0.34, $Rozmiar * 0.030)))
    $pioro = New-Object System.Drawing.Pen($akcent, $grubosc)
    $pioro.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pioro.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pioro.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    # Odcinki proste, a nie krzywa sklejana: przy tej gęstości punktów różnicy nie widać,
    # a interpolacja splajnem potrafi wyjść poza tor i zafalować przy małych rozmiarach.
    $g.DrawLines($pioro, $punkty.ToArray())
    $pioro.Dispose()

    # Kropka pośrodku - odpowiednik otworu płyty, jak na okładce domyślnej.
    $rKropki = [single]($Rozmiar * $Kropka)
    $pedzelSrodka = New-Object System.Drawing.SolidBrush($srodek)
    $g.FillEllipse($pedzelSrodka, $srodekXY - $rKropki, $srodekXY - $rKropki, $rKropki * 2, $rKropki * 2)
    $pedzelSrodka.Dispose()

    $g.Dispose()
    return $obraz
}

# Złożenie pliku ICO: nagłówek, tablica wpisów, a następnie obrazy zapisane jako PNG.
$obrazy = @()
foreach ($wariant in $warianty) {
    $obraz = New-Obraz -Rozmiar $wariant.Rozmiar -Zwoje $wariant.Zwoje -Kropka $wariant.Kropka
    $strumien = New-Object System.IO.MemoryStream
    $obraz.Save($strumien, [System.Drawing.Imaging.ImageFormat]::Png)
    $obrazy += , @{ Rozmiar = $wariant.Rozmiar; Dane = $strumien.ToArray() }
    $strumien.Dispose()
    $obraz.Dispose()
}

$plik = New-Object System.IO.MemoryStream
$zapis = New-Object System.IO.BinaryWriter($plik)

$zapis.Write([UInt16]0)                 # zarezerwowane
$zapis.Write([UInt16]1)                 # typ: ikona
$zapis.Write([UInt16]$obrazy.Count)

$przesuniecie = 6 + 16 * $obrazy.Count
foreach ($obraz in $obrazy) {
    # Rozmiar 256 zapisuje się jako zero - jeden bajt nie pomieściłby tej liczby.
    $bajtRozmiaru = if ($obraz.Rozmiar -ge 256) { 0 } else { $obraz.Rozmiar }

    $zapis.Write([Byte]$bajtRozmiaru)    # szerokość
    $zapis.Write([Byte]$bajtRozmiaru)    # wysokość
    $zapis.Write([Byte]0)                # liczba barw palety: brak palety
    $zapis.Write([Byte]0)                # zarezerwowane
    $zapis.Write([UInt16]1)              # płaszczyzny
    $zapis.Write([UInt16]32)             # bitów na piksel
    $zapis.Write([UInt32]$obraz.Dane.Length)
    $zapis.Write([UInt32]$przesuniecie)

    $przesuniecie += $obraz.Dane.Length
}

foreach ($obraz in $obrazy) { $zapis.Write($obraz.Dane) }

$zapis.Flush()
[System.IO.File]::WriteAllBytes($wynik, $plik.ToArray())
$zapis.Dispose()
$plik.Dispose()

# Ten sam znak jako PNG: ikona okna ładowana jest przez Avalonię, a dekodowanie formatu ICO
# zależy tam od zaplecza graficznego, więc pewniejszy jest zwykły obraz.
$png = Join-Path $repozytorium 'src\Cewka.App\Assets\cewka.png'
$obrazPng = New-Obraz -Rozmiar 256 -Zwoje 4.0 -Kropka 0.085
$obrazPng.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
$obrazPng.Dispose()

# Osobne pliki PNG dla pakietów linuksowych. Motywy ikon w systemie Linux trzymają każdy
# rozmiar w swoim katalogu, więc pojedynczy obraz 256 px nie wystarczy: pulpit skalowałby go
# w dół i spirala straciłaby to, po co każdy rozmiar rysowany jest osobno.
$katalogIkon = Join-Path $repozytorium 'packaging\linux\icons'
New-Item -ItemType Directory -Force $katalogIkon | Out-Null

$dlaLinuksa = $warianty | Where-Object { $_.Rozmiar -ge 48 }
foreach ($wariant in $dlaLinuksa) {
    $obraz = New-Obraz -Rozmiar $wariant.Rozmiar -Zwoje $wariant.Zwoje -Kropka $wariant.Kropka
    $obraz.Save((Join-Path $katalogIkon "cewka-$($wariant.Rozmiar).png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $obraz.Dispose()
}

$rozmiary = ($warianty | ForEach-Object { $_.Rozmiar }) -join ', '
$rozmiaryLinux = ($dlaLinuksa | ForEach-Object { $_.Rozmiar }) -join ', '
Write-Output "Zapisano $wynik ($rozmiary px) oraz $png"
Write-Output "Ikony dla pakietów linuksowych w $katalogIkon ($rozmiaryLinux px)"
