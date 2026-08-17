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

| Font | Wersja | Zastosowanie | Licencja | Tekst licencji |
|------|--------|--------------|----------|----------------|
| Cantarell | 0.303 | Tekst interfejsu | SIL Open Font License 1.1 | `src/Cewka.App/Assets/Fonts/OFL-Cantarell.txt` |
| JetBrains Mono | 2.304 | Liczby, czasy, dane techniczne | SIL Open Font License 1.1 | `src/Cewka.App/Assets/Fonts/OFL-JetBrainsMono.txt` |

Licencja OFL zezwala na osadzanie i rozpowszechnianie fontów wraz z oprogramowaniem, także
komercyjnym, pod warunkiem dołączenia tekstu licencji i niesprzedawania samych fontów. Oba
warunki są spełnione.

Do wydania 0.6.0 osadzony był Cantarell w wydaniu oznaczonym wewnętrznie jako 1.004 — 388 glifów,
samo pismo łacińskie. Wydanie 0.303 z projektu GNOME ma 1223 glify i dlatego zastąpiło poprzednie:
bez niego rosyjski i ukraiński wyświetlałyby się jako puste prostokąty.

Zmierzone pokrycie bloków Unicode, bo ono rozstrzyga o doborze kolejnych języków:

| Blok | Pokrycie |
|------|----------|
| Latin-1 (U+00A0–00FF) | 96 z 96 |
| Latin rozszerzony A (U+0100–017F) | 124 z 128 |
| Latin rozszerzony B (U+0180–024F) | 78 z 208 |
| Greka (U+0370–03FF) | 74 z 144 |
| Cyrylica (U+0400–04FF) | 178 z 256 |
| Wietnamski (U+1E00–1EFF) | 166 z 256 |

Pokrycie cyrylicy sięga więc poza zakres podstawowy U+0400–U+045F i obejmuje między innymi
litery kazachskie. Sprawdzenie trzydziestu dwóch kandydatur — od niderlandzkiego przez greckiego
i serbskiego cyrylicą po kazachski i islandzki — nie wykazało ani jednego znaku, którego font
nie zawiera. Font przestał być ograniczeniem dla języków pisanych łaciną, cyrylicą i greką;
poza jego zasięgiem zostają pisma chińskie, japońskie, koreańskie, dewanagari, arabskie
i perskie.

Kompletność pokrycia sprawdza test, który czyta tablicę znaków tego właśnie pliku i porównuje ją
ze wszystkimi tekstami interfejsu. Osobny test pilnuje, żeby znaki z diakrytyką były zapisane
w postaci złożonej: font zawiera dwadzieścia dziewięć znaków łączących, więc zapis rozłożony
przeszedłby pierwsze sprawdzenie, a wyświetlił się źle — nie ma zakotwiczeń pozwalających ułożyć
dwa znaki diakrytyczne jeden nad drugim, czego wymaga wietnamski.

Języki poza zasięgiem tego kroju to pisma niełacińskie inne niż cyrylica i greka: chiński,
japoński, koreański, hindi, arabski, perski. Wymagałyby osadzenia Noto CJK albo Noto
odpowiedniego pisma — w przypadku CJK około 40 MB przed kompresją przy 47 MB obecnego pliku
wykonywalnego. Okrojenie fontu do znaków użytych w interfejsie byłoby tanie, ale rozwiązywałoby
połowę zadania: tytuły utworów pochodzą z plików użytkownika, więc chiński interfejs pokazywałby
chiński tytuł jako prostokąty.

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

Domyślna okładka przedstawia cewkę, od której wzięła się nazwa aplikacji, i pojawia się wtedy,
gdy plik nie zawiera osadzonej okładki. Do wydania 0.6.0 były to dwa pliki PNG, po jednym na
motyw; od 0.7.0 spiralę rysuje kod (`src/Cewka.App/Services/CoilCover.cs`), bo pięć par barw
w dwóch motywach to dziesięć plików, a przejście barwy wzdłuż zwoju trzeba by i tak było
wygenerować programem. Rysunek podlega licencji MIT razem z resztą repozytorium.

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
