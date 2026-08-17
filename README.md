# Cewka

Minimalistyczny odtwarzacz muzyki dla Linuksa i Windowsa. Jeden plik, bez instalatora,
bez środowiska .NET.

![Licencja](https://img.shields.io/badge/licencja-MIT-blue)
![Platformy](https://img.shields.io/badge/platformy-Linux%20%7C%20Windows-lightgrey)
![Wersja](https://img.shields.io/badge/wersja-0.6.0-brightgreen)

*[English version](README.en.md)*

![Cewka — motyw ciemny](docs/obrazy/cewka-ciemny.png)

![Cewka — motyw jasny](docs/obrazy/cewka-jasny.png)

## Dlaczego to napisałem

Brakowało mi konfigurowalnego i ładnego odtwarzacza na moją Fedorę, więc zrobiłem go sam.
Chciałem czegoś, co po prostu gra pliki z dysku, ma korektor i wygląda dobrze — bez biblioteki,
bez konta, bez integracji z serwisami.

Kod pisałem przy wsparciu Claude Code, które pisało automatyczne testy i wyszukiwało błędy.

## Co potrafi Cewka

- **Formaty**: MP3, FLAC, WAV, Ogg Vorbis, Opus, a przez kodeki systemowe także AAC, M4A i ALAC
- **Odtwarzanie bezprzerwowe** i przewijanie bez trzasków
- **Korektor dziesięciopasmowy** z przedwzmacniaczem i miękkim limiterem na wyjściu
- **Wyrównanie głośności** według tagów ReplayGain albo własnej analizy EBU R128, z wyborem
  poziomu docelowego (−23, −18 albo −14 LUFS)
- **Tło z barw okładki**, animowane w rytm muzyki
- **Motyw jasny i ciemny**, domyślnie zgodny z systemem
- **Pięć języków**: polski, angielski, hiszpański, niemiecki i francuski
- **Integracja z pulpitem**: panel multimediów (MPRIS w Linuksie, nakładka w Windowsie),
  klawisze multimedialne, jedna instancja z przekazywaniem plików
- **Ustawienia**: urządzenie wyjściowe, rozmiar bufora, jakość konwersji częstotliwości,
  krok przewijania, przywracanie poprzedniej sesji

<img src="docs/obrazy/cewka-ustawienia.png" width="640" alt="Ustawienia">

## Instalacja

Pakiety są w [wydaniach](https://github.com/grelix/cewka/releases). Po instalacji program
pojawia się w menu aplikacji.

**Fedora, RHEL, openSUSE**

```bash
sudo dnf install ./cewka-0.6.0-1.x86_64.rpm
```

**Debian, Ubuntu, Linux Mint**

```bash
sudo apt install ./cewka_0.6.0_amd64.deb
```

**Arch, Manjaro**

```bash
sudo pacman -U cewka-0.6.0-1-x86_64.pkg.tar.zst
```

**Windows**

Wystarczy pobrać `Cewka.exe` i uruchomić. Instalator nie jest potrzebny.

### AAC, M4A i ALAC w Linuksie

Te trzy formaty obsługuje GStreamer, a dekoder AAC dostarcza osobny pakiet. Bez niego pozostałe
formaty działają normalnie.

```bash
sudo dnf install gstreamer1-libav        # Fedora (z RPM Fusion)
sudo apt install gstreamer1.0-libav      # Debian, Ubuntu
sudo pacman -S gst-libav                 # Arch
```

## Skróty klawiszowe

| Skrót | Działanie |
|-------|-----------|
| `Spacja` | Odtwarzanie lub pauza |
| `←` `→` | Przewijanie (krok w ustawieniach: 5, 10 albo 30 s) |
| `Ctrl` + `←` `→` | Poprzedni lub następny utwór |
| `↑` `↓` | Głośność |
| `M` | Wyciszenie |
| `Q` | Korektor i kolejka |
| `T` | Zmiana motywu |
| `F11` | Pełny ekran |
| `Ctrl+O` | Dodanie plików |
| `Ctrl+Shift+O` | Dodanie folderu |
| `Delete` | Usunięcie z kolejki |

Klawisze multimedialne klawiatury działają też wtedy, gdy okno nie jest aktywne.

## Budowanie ze źródeł

Potrzebny jest .NET SDK 9 oraz kompilator C — `gcc` w Linuksie albo Visual Studio Build Tools
w Windowsie.

```bash
./native/build-linux.sh
```

Potem aplikacja:

```bash
dotnet run --project src/Cewka.App -- ~/Muzyka/Album
```

Wydanie jednoplikowe i pakiety instalacyjne:

```bash
./tools/publish-linux.sh
./tools/build-packages.sh
```

W Windowsie odpowiednikami są `native\build-windows.cmd` i `tools\publish-windows.cmd`.

### Testy

```bash
dotnet test Cewka.sln
```

103 testy: przetwarzanie sygnału, logika modelu widoku, zgodność ustawień między wersjami
i kompletność plików językowych.

Zrzuty interfejsu można wyrenderować bez otwierania okna — przydaje się do porównywania
kolejnych wersji wyglądu:

```bash
dotnet run --project tools/Cewka.Snapshots -- artifacts/snapshots
```

## Znane ograniczenia

- Pakiet `.deb` jest sprawdzony instalacją w Ubuntu 24.04: program z niego gra i odinstalowuje się
  bez pozostałości. Pakiety `.rpm` i Arch sprawdzałem zawartością, zależnościami oraz tym, czy plik
  wykonywalny wychodzi z nich nietknięty — ale nie instalacją na docelowej dystrybucji.
- Pakiet Arch powstaje bez `makepkg`, więc nie ma pliku `.MTREE`. `pacman -U` go przyjmuje,
  choć może o tym wspomnieć. W repozytorium jest też [`PKGBUILD`](packaging/arch/PKGBUILD).
- Tryb ograniczania efektów na baterii nie był sprawdzony na laptopie.
- Ustawienie „małe opóźnienie" jest tylko podpowiedzią dla sterownika. W trybie współdzielonym
  WASAPI nie zawsze cokolwiek zmienia — okno ustawień pokazuje wtedy rozmiar, który urządzenie
  faktycznie przyjęło.

## Jak to jest zrobione

C# i [Avalonia UI](https://avaloniaui.net/), dźwięk przez [miniaudio](https://miniaud.io/)
z własną nakładką interoperacyjną. Dekodery: miniaudio dla MP3, FLAC i WAV,
[NVorbis](https://github.com/NVorbis/NVorbis) dla Ogg Vorbis,
[Concentus](https://github.com/lostromb/concentus) dla Opusa, a dla pozostałych formatów
Media Foundation w Windowsie i GStreamer w Linuksie.

## Licencja

MIT — treść w pliku [LICENSE](LICENSE). Osadzone fonty Cantarell i JetBrains Mono podlegają
licencji SIL Open Font License 1.1; szczegóły w [docs/licencje.md](docs/licencje.md).
