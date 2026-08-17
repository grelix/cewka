# Licencje składników

Cewka jest udostępniana na licencji MIT. Poniżej wykaz składników zewnętrznych wraz z ich
licencjami oraz zobowiązaniami, jakie z nich wynikają.

## Biblioteki

| Składnik | Wersja | Licencja | Uwagi |
|----------|--------|----------|-------|
| Avalonia | 12.1.1 | MIT | Framework interfejsu wraz z zapleczem Skia |
| Avalonia.Desktop | 12.1.1 | MIT | Warstwy systemowe Windows i X11 |
| Avalonia.Themes.Fluent | 12.1.1 | MIT | Baza szablonów kontrolek, mocno nadpisywana |
| Avalonia.Headless | 12.1.1 | MIT | Wyłącznie narzędzie deweloperskie, nie trafia do wydania |

Wszystkie są na licencji MIT, zgodnej z licencją projektu i niewymagającej niczego ponad
zachowanie noty o prawach autorskich.

## Fonty

Oba fonty osadzone są w pliku wykonywalnym, aby interfejs wyglądał identycznie niezależnie od
tego, co użytkownik ma zainstalowane w systemie.

| Font | Zastosowanie | Licencja | Tekst licencji |
|------|--------------|----------|----------------|
| Cantarell | Tekst interfejsu | SIL Open Font License 1.1 | `src/Cewka.App/Assets/Fonts/OFL-Cantarell.txt` |
| JetBrains Mono | Liczby, czasy, dane techniczne | SIL Open Font License 1.1 | `src/Cewka.App/Assets/Fonts/OFL-JetBrainsMono.txt` |

Licencja OFL zezwala na osadzanie i rozpowszechnianie fontów wraz z oprogramowaniem, także
komercyjnym, pod warunkiem dołączenia tekstu licencji i niesprzedawania samych fontów. Oba
warunki są spełnione.

## Warstwa audio

| Składnik | Wersja | Licencja | Zastosowanie |
|----------|--------|----------|--------------|
| miniaudio | 0.11.25 | MIT-0 lub domena publiczna | Wyjście audio, dekodery MP3, FLAC i WAV, resampler |
| NVorbis | 0.10.5 | MIT | Dekodowanie Ogg Vorbis |
| Concentus | 2.2.2 | MIT | Dekodowanie Opus |
| Concentus.OggFile | 1.0.7 | MIT | Kontener Ogg dla Opusa |
| ATL.NET (z440.atl.core) | 7.16.0 | MIT | Odczyt tagów, okładek i wartości ReplayGain |

Kod źródłowy miniaudio znajduje się w repozytorium (`native/miniaudio/miniaudio.h`) wraz
z treścią licencji. Jest to biblioteka jednonagłówkowa kompilowana razem z nakładką
`native/cewka_audio.c`; do wydania trafia jako biblioteka wspólna zbudowana skryptami
z tego samego katalogu.

Wybrałem ATL.NET zamiast popularniejszego TagLib# z powodu licencji: TagLib# jest na LGPL, co przy
publikacji jednoplikowej rodzi ten sam problem, przez który odpadł FFmpeg.

## Okładki domyślne i ikona

Pliki `src/Cewka.App/Assets/Covers/cover-dark.png` i `cover-light.png` powstały na potrzeby
tego projektu — przedstawiają cewkę, od której wzięła się nazwa aplikacji. Podlegają licencji
MIT razem z resztą repozytorium. Pojawiają się wtedy, gdy plik nie zawiera osadzonej okładki,
a wariant dobierany jest do aktywnego motywu.

Ikona aplikacji (`src/Cewka.App/Assets/cewka.ico`, `cewka.png` oraz osobne rozmiary dla pakietów
linuksowych w `packaging/linux/icons/`) przedstawia ten sam znak i również powstała na potrzeby
tego projektu. Nie jest plikiem zewnętrznym: rysuje ją skrypt `tools/make-icon.ps1`, więc można
ją odtworzyć z samego repozytorium.

## Interfejsy systemowe

Program korzysta z Media Foundation (dekodowanie AAC, M4A i ALAC) oraz z
`SystemMediaTransportControls` (panel multimediów) przez ich binarne interfejsy. Nie dołącza
przy tym żadnego kodu Microsoftu — identyfikatory interfejsów i kolejność metod odczytano
z metadanych zainstalowanych w systemie, a wywołania idą do bibliotek, które są częścią systemu
operacyjnego. Nie powstaje z tego żadne zobowiązanie licencyjne.

W Linuksie tę samą rolę pełnią GStreamer (dekodowanie) i MPRIS na szynie D-Bus (panel
multimediów), również wywoływane przez interfejsy systemowe, bez dołączania cudzego kodu.
