# Licencje składników

Cewka jest na licencji MIT (`LICENSE`). Poniżej składniki zewnętrzne, które trafiają do pliku
wykonywalnego albo pakietów, oraz te, z których program korzysta w systemie, nie dołączając ich.
Wersje odpowiadają plikom projektów (`*.csproj`, `Directory.Build.props`) i grafowi zależności
po przywróceniu pakietów.

## Pakiety NuGet dołączone bezpośrednio

| Pakiet | Wersja | Licencja | Zastosowanie |
|--------|--------|----------|--------------|
| Avalonia | 12.1.6 | MIT | interfejs |
| Avalonia.Desktop | 12.1.6 | MIT | warstwy systemowe Windows i X11 |
| Avalonia.Themes.Fluent | 12.1.6 | MIT | bazowe szablony kontrolek |
| NVorbis | 0.10.5 | MIT | dekodowanie Ogg Vorbis |
| Concentus | 2.2.2 | BSD, 3 klauzule (licencja Opus) | dekodowanie Opus |
| Concentus.OggFile | 1.0.7 | MIT | kontener Ogg dla Opusa |
| z440.atl.core (ATL.NET) | 7.19.0 | MIT | tagi, okładki, wartości ReplayGain |

## Zależności przechodnie w wydaniu

| Pakiet | Wersja | Licencja | Skąd |
|--------|--------|----------|------|
| Avalonia.Win32, Avalonia.X11, Avalonia.Native, Avalonia.Skia, Avalonia.HarfBuzz | 12.1.6 | MIT | Avalonia.Desktop |
| Avalonia.FreeDesktop, Avalonia.FreeDesktop.AtSpi | 12.1.6 | MIT | Avalonia.X11 |
| Avalonia.Remote.Protocol | 12.1.6 | MIT | Avalonia |
| MicroCom.Runtime | 0.11.6 | MIT | Avalonia |
| Tmds.DBus.Protocol | 0.94.1 | MIT | Avalonia.FreeDesktop |
| SkiaSharp z bibliotekami natywnymi | 3.119.4 | MIT | Avalonia.Skia |
| HarfBuzzSharp z bibliotekami natywnymi | 8.3.1.3 | MIT | Avalonia.Skia, Avalonia.HarfBuzz |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | BSD, 3 klauzule (ANGLE) | Avalonia.Win32, tylko Windows |
| Ude.NetStandard | 1.2.0 | MPL 1.1, do wyboru GPL 2.0+ lub LGPL 2.1+ | z440.atl.core |

Biblioteki natywne `libSkiaSharp` i `libHarfBuzzSharp` zawierają kod innych projektów (między
innymi Skia, HarfBuzz, FreeType, libpng, libjpeg-turbo, libwebp, expat, zlib i dane ICU). Ich noty
licencyjne pochodzą z pliku `THIRD-PARTY-NOTICES.txt` pakietów `SkiaSharp.NativeAssets.*`
i `HarfBuzzSharp.NativeAssets.*` (we wszystkich czterech ten sam tekst).

Ude.NetStandard to przeniesiony na C# wykrywacz kodowania znaków z Mozilli; ATL.NET używa go do
tagów zapisanych w nieznanym kodowaniu. Pakiet nie zawiera tekstu licencji (plik `COPYING`
odsyła do katalogu, którego w pakiecie nie ma). Licencję podaje README repozytorium
`yinyue200/ude`.

Pakiety `SkiaSharp.NativeAssets.*` i `HarfBuzzSharp.NativeAssets.*` dla macOS i WebAssembly
też są w grafie, ale wydania dla tych platform nie ma, więc ich biblioteki do niego nie trafiają.

Wydanie jest samodzielne (`SelfContained`), więc zawiera też środowisko uruchomieniowe .NET 10
z pakietu `Microsoft.NETCore.App.Runtime.<rid>` (MIT, z notami składników w jego
`THIRD-PARTY-NOTICES.TXT`). Program nie odwołuje się do `Microsoft.WindowsDesktop.App`, więc
środowisko WPF i Windows Forms do wydania nie trafia.

## Pakiety wyłącznie deweloperskie

Nie trafiają do wydania: Avalonia.BuildServices 11.3.2 (MIT, zależność czasu budowania Avalonii),
Avalonia.Headless 12.1.6 (MIT, testy i narzędzie zrzutów), xunit 2.9.3, xunit.runner.visualstudio
4.0.1 (Apache 2.0) i Microsoft.NET.Test.Sdk 18.11.0 (MIT).

## Warstwa natywna

| Składnik | Wersja | Licencja | Zastosowanie |
|----------|--------|----------|--------------|
| miniaudio | 0.11.26 | do wyboru domena publiczna (Unlicense) lub MIT-0 | wyjście dźwięku, dekodery MP3, FLAC i WAV, resampler |

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

Fonty są osadzone w pliku wykonywalnym. Wszystkie pochodzą z rodziny Noto (wspólny projekt), pobrane
9.11.2026 z oficjalnych repozytoriów: `notofonts/notofonts.github.io` (katalog `fonts/<Rodzina>/hinted/ttf/`)
i `notofonts/noto-cjk` (`Sans/SubsetOTF/`).

| Font | Wersja | Pliki | Zastosowanie | Licencja | Tekst licencji |
|------|--------|-------|--------------|----------|----------------|
| Noto Sans | 2.015 | Regular, Bold, Light (`.ttf`) | cały interfejs: łacina z rozszerzeniami (także wietnamski), greka, cyrylica; liczby i czasy z cyframi tabelarycznymi (cecha `tnum`) | SIL OFL 1.1 | `src/Cewka.App/Assets/Fonts/OFL-NotoSans.txt` |
| Noto Sans SC | 2.004 | Regular, Bold (`.otf`, podzbiór regionalny) | chiński uproszczony; znaki Han w pozostałych językach | SIL OFL 1.1 | `src/Cewka.App/Assets/Fonts/OFL-NotoSansCJK.txt` |
| Noto Sans JP | 2.004 | Regular, Bold (`.otf`, podzbiór regionalny) | japoński (w japońskim interfejsie ma pierwszeństwo przed SC) | SIL OFL 1.1 | `src/Cewka.App/Assets/Fonts/OFL-NotoSansCJK.txt` |
| Noto Sans Arabic | 2.013 | Regular, Bold (`.ttf`) | pismo zastępcze (tytuły i nazwy w metadanych) | SIL OFL 1.1 | `src/Cewka.App/Assets/Fonts/OFL-NotoSans.txt` |
| Noto Sans Hebrew | 3.001 | Regular, Bold (`.ttf`) | pismo zastępcze | SIL OFL 1.1 | `src/Cewka.App/Assets/Fonts/OFL-NotoSans.txt` |
| Noto Sans Devanagari | 2.007 | Regular, Bold (`.ttf`) | pismo zastępcze | SIL OFL 1.1 | `src/Cewka.App/Assets/Fonts/OFL-NotoSans.txt` |
| Noto Sans Thai | 2.002 | Regular, Bold (`.ttf`) | pismo zastępcze | SIL OFL 1.1 | `src/Cewka.App/Assets/Fonts/OFL-NotoSans.txt` |

Łańcuch rodzin i kolejność zastępowania opisuje `src/Cewka.App/Themes/UiFonts.cs`. Fonty CJK zajmują
około 26 MB; koreańskiego (Noto Sans KR, około 9,5 MB) nie dołączono, bo program nie ma interfejsu
po koreańsku — znaki Hangul w metadanych rysuje font systemowy. Test `LanguageFileTests` sprawdza, że
każdy znak w plikach językowych ma glif w łańcuchu fontów danego języka, że każda rodzina z łańcucha
leży w obu odmianach pod właściwą nazwą i że znaki z diakrytyką są zapisane w postaci złożonej.

## Zobowiązania przy rozpowszechnianiu

Większość licencji powyżej pozwala rozpowszechniać plik wykonywalny pod warunkiem, że trafią
z nim noty składników:

- MIT (Avalonia, NVorbis, Concentus.OggFile, ATL.NET, środowisko .NET i reszta pakietów MIT)
  i BSD z 3 klauzulami (Concentus, ANGLE): informacja o prawach autorskich i tekst licencji.
- Biblioteki natywne Skii i HarfBuzz: noty z ich pliku `THIRD-PARTY-NOTICES.txt`.
- Ude.NetStandard (MPL 1.1): informacja, gdzie można pobrać kod źródłowy pakietu.
- Font (SIL OFL 1.1): informacja o prawach autorskich i tekst licencji przy każdej kopii fontu.

Noty nie wymaga tylko miniaudio (Unlicense albo MIT-0); jego licencja i tak jest w pliku not.

Wszystko to zbiera plik `THIRD-PARTY-NOTICES.txt` w katalogu głównym repozytorium. Każdy
składnik ma w nim sekcję z nazwą, wersją, adresem projektu, informacją o prawach autorskich
i pełnym tekstem licencji, skopiowanym z pakietu albo, gdy pakiet go nie zawiera, z repozytorium
źródłowego. Noty bibliotek natywnych Skii i HarfBuzz oraz środowiska .NET są przytoczone w całości.
Plik leży w każdej paczce obok licencji Cewki:

| Paczka | Położenie |
|--------|-----------|
| wydanie dla Windows | zasób wydania obok `Cewka.exe`, ujęty w `SHA256SUMS.txt` |
| archiwum `.tar.gz` | `cewka-<wersja>/THIRD-PARTY-NOTICES.txt` |
| `.deb` | `/usr/share/doc/cewka/THIRD-PARTY-NOTICES.txt` |
| `.rpm` | `/usr/share/licenses/cewka/THIRD-PARTY-NOTICES.txt` (`%license`) |
| pacman (wydanie i `packaging/arch/PKGBUILD`) | `/usr/share/licenses/cewka/THIRD-PARTY-NOTICES.txt` |

Budowa pakietów w CI (`.github/workflows/packages.yml`) sprawdza, że plik jest w spisie plików
pakietów `.deb`, `.rpm`, pacmana i archiwum. Test `ThirdPartyNoticesTests` wymaga w nim wiersza
`Pakiet NuGet: <nazwa> <wersja>` dla każdego pakietu z projektów `src/*/*.csproj` i każdego
pakietu z grafu zależności w `src/Cewka.App/obj/project.assets.json` (cel `net10.0`) oraz wiersza
`Plik: <nazwa>` dla każdego pliku fontu w `src/Cewka.App/Assets/Fonts`. Nowy pakiet, także
przechodni, nowa wersja albo nowy font bez sekcji w pliku not zatrzymuje więc testy. Test pomija
pakiety spoza wydania wymienione wyżej: Avalonia.BuildServices
oraz `*.NativeAssets.macOS` i `*.NativeAssets.WebAssembly`. Środowiska .NET i miniaudio
nie ma w grafie pakietów, więc ich sekcje trzeba pilnować przy zmianie wersji.

Teksty OFL są też osadzone w pliku wykonywalnym razem z fontami, ale program ich nie pokazuje,
a sekcja „Licencje” w ustawieniach podaje tylko nazwy licencji.

## Grafika

Ikony interfejsu to geometrie w `src/Cewka.App/Themes/Icons.axaml`, narysowane dla tego projektu.
Okładkę domyślną (spirala w jednej z 11 par barw) rysuje kod w `src/Cewka.App/Services/CoilCover.cs`.
Ikona aplikacji, miedziana cewka głośnika, jest własnym rysunkiem projektu. Źródłem są dwa pliki
SVG w `assets/ikona/`: pełny rysunek `cewka.svg` i uproszczony `cewka-maly.svg` na 16–32 px.
Skrypt `tools/make-icon.ps1` renderuje z nich `src/Cewka.App/Assets/cewka.ico`, `cewka.png`
i rozmiary w `packaging/linux/icons/`, a pakiety Linuksa dostają też `cewka.svg` jako ikonę
skalowalną. Wszystko to jest na licencji MIT razem z resztą repozytorium.

Flagi przy językach w ustawieniach pochodzą z paczki flag-icons 7.6.0 (MIT,
https://github.com/lipis/flag-icons). Pliki SVG z wariantu 4×3 zamieniono raz na PNG 64 × 48
(`rsvg-convert`, potem zmniejszenie filtrem Lanczos), bo program nie ma dekodera wektorów. Leżą
w `src/Cewka.App/Assets/Flags/` pod kodami języków, z licencją `LICENSE-flag-icons.txt`, a nota
jest w `THIRD-PARTY-NOTICES.txt` (pozycja 16). Język angielski ma flagę Wielkiej Brytanii,
portugalski — Portugalii, portugalski brazylijski — Brazylii, chiński uproszczony — Chin, japoński —
Japonii (trzy ostatnie dodane 9.11.2026 tą samą drogą).

## Zrzuty ekranu

Zrzuty w README pokazują nagranie „Rhapsody in Blue” z 1924 roku z okładką pierwszego wydania nut
(Harms, Nowy Jork 1924). Okładka jest w domenie publicznej, źródło:
[Wikimedia Commons](https://commons.wikimedia.org/wiki/File:Rhapsody_in_Blue_cover.jpg).
