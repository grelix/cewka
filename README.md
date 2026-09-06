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

## Jak to jest zrobione

C# i [Avalonia UI](https://avaloniaui.net/), dźwięk przez [miniaudio](https://miniaud.io/)
z własną nakładką interoperacyjną. Dekodery: miniaudio dla MP3, FLAC i WAV,
[NVorbis](https://github.com/NVorbis/NVorbis) dla Ogg Vorbis,
[Concentus](https://github.com/lostromb/concentus) dla Opusa, a dla pozostałych formatów
Media Foundation w Windowsie i GStreamer w Linuksie.

## Licencja

MIT — treść w pliku [LICENSE](LICENSE). Osadzone fonty Cantarell i JetBrains Mono podlegają
licencji SIL Open Font License 1.1; szczegóły w [docs/licencje.md](docs/licencje.md).
