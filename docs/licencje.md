# Licencje składników

Cewka jest na licencji MIT (`LICENSE`). Poniżej składniki zewnętrzne, które trafiają do pliku
wykonywalnego albo pakietów, oraz te, z których program korzysta w systemie, nie dołączając ich.
Wersje odpowiadają plikom projektów (`*.csproj`, `Directory.Build.props`) i grafowi zależności
po przywróceniu pakietów.

## Pakiety NuGet dołączone bezpośrednio

| Pakiet | Wersja | Licencja | Zastosowanie |
|--------|--------|----------|--------------|
| Avalonia | 12.1.1 | MIT | interfejs |
| Avalonia.Desktop | 12.1.1 | MIT | warstwy systemowe Windows i X11 |
| Avalonia.Themes.Fluent | 12.1.1 | MIT | bazowe szablony kontrolek |
| NVorbis | 0.10.5 | MIT | dekodowanie Ogg Vorbis |
| Concentus | 2.2.2 | BSD, 3 klauzule (licencja Opus) | dekodowanie Opus |
| Concentus.OggFile | 1.0.7 | MIT | kontener Ogg dla Opusa |
| z440.atl.core (ATL.NET) | 7.16.0 | MIT | tagi, okładki, wartości ReplayGain |

## Zależności przechodnie w wydaniu

| Pakiet | Wersja | Licencja | Skąd |
|--------|--------|----------|------|
| Avalonia.Win32, Avalonia.X11, Avalonia.Native, Avalonia.Skia, Avalonia.HarfBuzz | 12.1.1 | MIT | Avalonia.Desktop |
| Avalonia.FreeDesktop, Avalonia.FreeDesktop.AtSpi | 12.1.1 | MIT | Avalonia.X11 |
| Avalonia.Remote.Protocol | 12.1.1 | MIT | Avalonia |
| MicroCom.Runtime | 0.11.6 | MIT | Avalonia |
| Tmds.DBus.Protocol | 0.94.1 | MIT | Avalonia.FreeDesktop |
| SkiaSharp z bibliotekami natywnymi | 3.119.4 | MIT | Avalonia.Skia |
| HarfBuzzSharp z bibliotekami natywnymi | 8.3.1.3 | MIT | Avalonia.Skia, Avalonia.HarfBuzz |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | BSD, 3 klauzule (ANGLE) | Avalonia.Win32, tylko Windows |
| Ude.NetStandard | 1.2.0 | MPL 1.1, do wyboru GPL 2.0+ lub LGPL 2.1+ | z440.atl.core |

Biblioteki natywne `libSkiaSharp` i `libHarfBuzzSharp` zawierają kod innych projektów (między
innymi Skia, HarfBuzz, FreeType, libpng, libjpeg-turbo, libwebp, expat, zlib i dane ICU). Ich noty
licencyjne są w pliku `THIRD-PARTY-NOTICES.txt` pakietów `SkiaSharp.NativeAssets.*`
i `HarfBuzzSharp.NativeAssets.*`.

Ude.NetStandard to przeniesiony na C# wykrywacz kodowania znaków z Mozilli; ATL.NET używa go do
tagów zapisanych w nieznanym kodowaniu. Pakiet nie zawiera tekstu licencji (plik `COPYING`
odsyła do katalogu, którego w pakiecie nie ma). Licencję podaje README repozytorium
`yinyue200/ude`.

Pakiety `System.IO.Pipelines`, `System.Memory`, `System.Numerics.Vectors` i `System.ValueTuple`
pojawiają się w grafie zależności, ale w wydaniu zastępują je zestawy środowiska .NET.

Wydanie jest samodzielne (`SelfContained`), więc zawiera też środowisko uruchomieniowe .NET 9
(MIT, z własnymi notami w `THIRD-PARTY-NOTICES.txt` Microsoftu).

## Pakiety wyłącznie deweloperskie

Nie trafiają do wydania: Avalonia.BuildServices 11.3.2 (MIT, zależność czasu budowania Avalonii),
Avalonia.Headless 12.1.1 (MIT, testy i narzędzie zrzutów), xunit 2.9.2, xunit.runner.visualstudio
2.8.2 (Apache 2.0) i Microsoft.NET.Test.Sdk 17.12.0 (MIT).

## Warstwa natywna

| Składnik | Wersja | Licencja | Zastosowanie |
|----------|--------|----------|--------------|
| miniaudio | 0.11.25 | do wyboru domena publiczna (Unlicense) lub MIT-0 | wyjście dźwięku, dekodery MP3, FLAC i WAV, resampler |

Źródło miniaudio leży w `native/miniaudio/` razem z licencją. Kompiluje się razem z nakładką
`native/cewka_audio.c` do biblioteki `cewka_audio`, która trafia do wydania.

## Biblioteki systemowe

Program ich nie dołącza. Ładuje je w czasie działania, jeśli są w systemie:

- Windows: WASAPI (wyjście dźwięku przez miniaudio), Media Foundation (AAC, M4A, ALAC),
  `SystemMediaTransportControls` przez WinRT (panel multimediów), `advapi32` (skojarzenia plików).
- Linux: ALSA albo PulseAudio (wyjście dźwięku, ładowane przez miniaudio; PipeWire działa przez
  warstwę zgodności z PulseAudio), GStreamer z wtyczkami (AAC, M4A, ALAC), `libdbus-1` (MPRIS),
  OpenSSL (sprawdzanie nowej wersji przez HTTPS).

## Font

Font jest osadzony w pliku wykonywalnym.

| Font | Wersja | Pliki | Zastosowanie | Licencja | Tekst licencji |
|------|--------|-------|--------------|----------|----------------|
| Cantarell | 0.303 | Regular, Bold (`.otf`) | cały interfejs; liczby i czasy z cyframi tabelarycznymi (cecha `tnum`) | SIL OFL 1.1 | `src/Cewka.App/Assets/Fonts/OFL-Cantarell.txt` |

Cantarell 0.303 obejmuje alfabet łaciński z rozszerzeniami, grekę i cyrylicę, więc wystarcza dla
wszystkich 17 języków interfejsu. Test `LanguageFileTests` sprawdza, że każdy znak w plikach
językowych jest w foncie i że znaki z diakrytyką są zapisane w postaci złożonej. Pisma chińskie,
japońskie, koreańskie, arabskie i dewanagari wymagałyby osobnego fontu.

## Zobowiązania przy rozpowszechnianiu

Większość licencji powyżej pozwala rozpowszechniać plik wykonywalny pod warunkiem, że trafią
z nim noty składników:

- MIT (Avalonia, NVorbis, Concentus.OggFile, ATL.NET, środowisko .NET i reszta pakietów MIT)
  i BSD z 3 klauzulami (Concentus, ANGLE): informacja o prawach autorskich i tekst licencji.
- Biblioteki natywne Skii i HarfBuzz: noty z ich pliku `THIRD-PARTY-NOTICES.txt`.
- Ude.NetStandard (MPL 1.1): informacja, gdzie można pobrać kod źródłowy pakietu.
- Font (SIL OFL 1.1): informacja o prawach autorskich i tekst licencji przy każdej kopii fontu.

Noty nie wymaga tylko miniaudio (Unlicense albo MIT-0).

Wydanie dziś tego nie spełnia. Archiwum i pakiety dla Linuksa zawierają tylko `LICENSE` Cewki,
a plik wykonywalny dla Windows wychodzi bez żadnego pliku obok. Teksty OFL są osadzone
w pliku wykonywalnym razem z fontami, ale program ich nie pokazuje, a sekcja „Licencje”
w ustawieniach podaje tylko nazwy licencji. Do uzupełnienia jest jeden plik z notami
(np. `THIRD-PARTY-NOTICES.txt`) dołączany do archiwum, pakietów i wydania dla Windows albo
dostępny z okna ustawień.

## Grafika

Ikony interfejsu to geometrie w `src/Cewka.App/Themes/Icons.axaml`, narysowane dla tego projektu.
Okładkę domyślną (spirala w jednej z 11 par barw) rysuje kod w `src/Cewka.App/Services/CoilCover.cs`.
Ikonę aplikacji (`src/Cewka.App/Assets/cewka.ico`, `cewka.png` i rozmiary w
`packaging/linux/icons/`) generuje skrypt `tools/make-icon.ps1`. Wszystko to jest na licencji MIT
razem z resztą repozytorium.

## Zrzuty ekranu

Zrzuty w README pokazują nagranie „Rhapsody in Blue” z 1924 roku z okładką pierwszego wydania nut
(Harms, Nowy Jork 1924). Okładka jest w domenie publicznej, źródło:
[Wikimedia Commons](https://commons.wikimedia.org/wiki/File:Rhapsody_in_Blue_cover.jpg).
