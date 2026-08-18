# Cewka

Minimalistyczny odtwarzacz muzyki dla Linuksa i Windowsa. Jeden plik, bez instalatora,
bez środowiska .NET.

![Licencja](https://img.shields.io/badge/licencja-MIT-blue)
![Platformy](https://img.shields.io/badge/platformy-Linux%20%7C%20Windows-lightgrey)
![Wersja](https://img.shields.io/badge/wersja-0.8.1-brightgreen)

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
- **Pięć efektów**: crossfeed dla słuchawek, kompensacja głośności przy cichym słuchaniu, bas
  wirtualny, ograniczanie dynamiki i poszerzenie bazy stereo. Każdy ma suwak siły od 0 do 10
  i każdy jest domyślnie wyłączony
- **Korektor i kolejkę pokazuje się niezależnie** — klawiszem `Q` i klawiszem `L`, albo
  przyciskami w pasku tytułu. Okno wraca potem do rozmiaru, jaki miało wcześniej
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
  krok przewijania, przywracanie poprzedniej sesji, barwa domyślnej okładki (jedenaście par
  albo losowanie co utwór), zachowanie przy otwarciu pliku z eksploratora

<img src="docs/obrazy/cewka-ustawienia.png" width="640" alt="Ustawienia">

## Instalacja

Pakiety są w [wydaniach](https://github.com/grelix/cewka/releases). Po instalacji program
pojawia się w menu aplikacji.

**Fedora, RHEL, openSUSE**

```bash
sudo dnf install ./cewka-0.8.1-1.x86_64.rpm
```

**Debian, Ubuntu, Linux Mint**

```bash
sudo apt install ./cewka_0.8.1_amd64.deb
```

**Arch, Manjaro**

```bash
sudo pacman -U cewka-0.8.1-1-x86_64.pkg.tar.zst
```

**Windows**

Wystarczy pobrać `Cewka.exe` i uruchomić. Instalator nie jest potrzebny.

Plik nie jest podpisany certyfikatem, więc przy pierwszym uruchomieniu Windows pokaże ostrzeżenie
SmartScreen. Pochodzenie pliku można potwierdzić sumą kontrolną — każde wydanie ma dołączony
`SHA256SUMS.txt`, a sumę pobranego pliku podaje `Get-FileHash Cewka.exe` w PowerShellu.

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
| `Q` | Korektor i efekty |
| `L` | Kolejka |
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

375 testów: przetwarzanie sygnału wraz z pięcioma efektami, logika modelu widoku, zgodność
ustawień między wersjami, kompletność plików językowych i mechanizm jednej działającej kopii.

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

Własna muzyka nie jest do tego potrzebna — narzędzie potrafi wytworzyć materiał samo. Dwa pliki
o różnej długości, policzone z sinusów, więc za każdym razem takie same:

```bash
dotnet run --project tools/Cewka.Snapshots -- --material artifacts/material
```

Narzędzie nie tylko rysuje. Sprawdza też kilka rzeczy, które łatwo zepsuć i trudno zauważyć:
czy nagłówki „Korektor" i „Efekty" stoją na jednej wysokości, czy okno wraca do poprzedniego
rozmiaru po schowaniu korektora albo kolejki, czy blok odtwarzania nie wchodzi na listę utworów
i czy otwarcie pliku z zewnątrz naprawdę odtwarza ten plik. Same sprawdziany, bez rysowania,
trwają sekundy:

```bash
dotnet run --project tools/Cewka.Snapshots -- artifacts/snapshots dluga.wav krotka.wav --sprawdzenia
```

Wygląd można też zestawić z [obrazami odniesienia](tests/odniesienie/README.md). Przy różnicy
powstaje obraz różnicowy z zaznaczonymi na czerwono pikselami:

```bash
dotnet run --project tools/Cewka.Snapshots -- artifacts/snapshots dluga.wav krotka.wav --porownaj tests/odniesienie
```

### Budowa w chmurze

Każda zmiana w gałęzi głównej uruchamia kompilację, testy i zrzuty z porównaniem — osobno
w Linuksie i w Windowsie. Przy tej samej okazji budowane są pakiety i instalowane próbnie
w kontenerach, żeby zepsute pakowanie wyszło od razu, a nie w chwili wydawania.

Wydanie powstaje z wypchnięcia znacznika `vX.Y.Z`. Potok sprawdza najpierw, czy znacznik zgadza
się z numerem wersji w `Directory.Build.props`, buduje wszystko od zera i zostawia **szkic**
wydania z plikami i sumami kontrolnymi. Opis piszę ręcznie i publikuję sam — to celowe.

Próbne instalacje można uruchomić także u siebie, jeśli jest dostępny docker:

```bash
./tools/test-packages.sh
```

## Znane ograniczenia

- Wszystkie trzy pakiety instalują się i odinstalowują w kontenerach Ubuntu 24.04, bieżącej Fedory
  i Archa, a program z każdego z nich uruchamia się pod serwerem X bez ekranu. Sprawdzane jest to
  przy każdej zmianie. Kontener nie jest jednak pulpitem i nie powie nic o tym, jak program
  wygląda ani jak brzmi.
- Zrzuty ekranu powyżej pochodzą z wersji 0.7.20 i pokazują jeszcze poprzedni układ okna,
  z kolejką w pasie dolnym.
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
