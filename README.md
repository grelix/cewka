# Cewka

Minimalistyczny odtwarzacz muzyki dla Linuksa i Windowsa. Jeden plik, bez instalatora,
bez środowiska .NET.

![Licencja](https://img.shields.io/badge/licencja-MIT-blue)
![Platformy](https://img.shields.io/badge/platformy-Linux%20%7C%20Windows-lightgrey)
![Wersja](https://img.shields.io/badge/wersja-0.7.11-brightgreen)

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
- **Listy odtwarzania** w formacie M3U — kolejkę można zapisać i wczytać, także w innym programie
- **Tło z barw okładki**, animowane w rytm muzyki; plamy oddychają niezależnie od siebie,
  a intensywność barw ma trzy stopnie
- **Motyw jasny i ciemny**, domyślnie zgodny z systemem
- **Siedemnaście języków**: polski, angielski, czeski, niemiecki, grecki, hiszpański,
  francuski, węgierski, indonezyjski, włoski, niderlandzki, portugalski, rumuński, rosyjski,
  turecki, ukraiński i wietnamski
- **Integracja z pulpitem**: panel multimediów (MPRIS w Linuksie, nakładka w Windowsie),
  klawisze multimedialne, jedna instancja z przekazywaniem plików
- **Sprawdzanie nowszego wydania** — na żądanie albo przy uruchomieniu, domyślnie wyłączone.
  To jedyne miejsce, w którym program łączy się z siecią: jedno pytanie do GitHuba o numer
  najnowszego wydania, nie częściej niż raz na dobę
- **Ustawienia**: urządzenie wyjściowe, rozmiar bufora, jakość konwersji częstotliwości,
  krok przewijania, przywracanie poprzedniej sesji, barwa domyślnej okładki (pięć par
  albo losowanie co utwór), zachowanie przy otwarciu pliku z eksploratora

<img src="docs/obrazy/cewka-ustawienia.png" width="640" alt="Ustawienia">

## Instalacja

Pakiety są w [wydaniach](https://github.com/grelix/cewka/releases). Po instalacji program
pojawia się w menu aplikacji.

**Fedora, RHEL, openSUSE**

```bash
sudo dnf install ./cewka-0.7.11-1.x86_64.rpm
```

**Debian, Ubuntu, Linux Mint**

```bash
sudo apt install ./cewka_0.7.11_amd64.deb
```

**Arch, Manjaro**

```bash
sudo pacman -U cewka-0.7.11-1-x86_64.pkg.tar.zst
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

326 testów: przetwarzanie sygnału, logika modelu widoku, zgodność ustawień między wersjami
i kompletność plików językowych.

Zrzuty interfejsu można wyrenderować bez otwierania okna — przydaje się do porównywania
kolejnych wersji wyglądu:

```bash
dotnet run --project tools/Cewka.Snapshots -- artifacts/snapshots
```

Podanie pliku dźwiękowego jako drugiego argumentu wpuszcza go do okna tak, jak zrobiłoby to
otwarcie z menedżera plików — z tytułem, okładką i czasem. Wszystkie animacje są przy zrzucie
zatrzymywane w ustalonym miejscu, więc dwa przebiegi na tym samym kodzie dają te same obrazy
i można je porównywać piksel w piksel:

```bash
dotnet run --project tools/Cewka.Snapshots -- artifacts/snapshots ~/Muzyka/Album/utwor.mp3
```

Własna muzyka nie jest do tego potrzebna. Narzędzie potrafi wytworzyć materiał samo — dwa pliki
o różnym czasie trwania, liczone z funkcji trygonometrycznych, więc za każdym razem identyczne
co do bajtu:

```bash
dotnet run --project tools/Cewka.Snapshots -- --material artifacts/material
```

Poza rysowaniem narzędzie wykonuje trzy sprawdziany zachowania: czy nagłówki „Korektor"
i „Kolejka" stoją na jednej wysokości, czy okno wraca do poprzedniego rozmiaru po schowaniu
panelu i czy zastąpienie kolejki plikiem z zewnątrz odtwarza właśnie ten plik. Same sprawdziany,
bez rysowania, trwają sekundy:

```bash
dotnet run --project tools/Cewka.Snapshots -- artifacts/snapshots dluga.wav krotka.wav --sprawdzenia
```

Wygląd można też zestawić z [obrazami odniesienia](tests/odniesienie/README.md). Przy różnicy
powstaje obraz różnicowy z zaznaczonymi na czerwono pikselami:

```bash
dotnet run --project tools/Cewka.Snapshots -- artifacts/snapshots dluga.wav krotka.wav --porownaj tests/odniesienie
```

### Budowa w chmurze

Każda zmiana w gałęzi głównej uruchamia kompilację warstwy natywnej, testy oraz zrzuty
z porównaniem — osobno w Linuksie i w Windowsie.

Wydania powstają z wypchnięcia znacznika `vX.Y.Z`. Potok sprawdza najpierw, czy znacznik zgadza
się z `<Version>` w `Directory.Build.props`, buduje pakiety, instaluje je próbnie w kontenerach
Ubuntu, Fedory i Archa, po czym tworzy **szkic** wydania z plikami i sumami kontrolnymi. Opis
merytoryczny pisze autor i on decyduje o publikacji.

Próbne instalacje można uruchomić także u siebie, jeśli jest dostępny docker:

```bash
./tools/test-packages.sh
```

## Znane ograniczenia

- Wszystkie trzy pakiety są sprawdzane instalacją przy każdej zmianie — w kontenerach Ubuntu 24.04,
  bieżącej Fedory i Archa. Próba obejmuje instalację, poprawność wpisu w menu, komplet ikon,
  rozwiązanie zależności, uruchomienie programu pod serwerem X bez ekranu oraz odinstalowanie bez
  pozostałości. Kontener to jednak nie pulpit: nie mówi nic o wyglądzie ani o dźwięku.
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
