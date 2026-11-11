# Zmiany

Każda wersja opisuje tylko to, co zmieniło się od poprzedniej.

## 1.0.0 (2026-11-11)

Scena pełnoekranowa z wizualizacjami, Cewka na telewizorze i radio internetowe.

### Scena pełnoekranowa

- F11, przycisk w pasku tytułu albo dwuklik w płytę otwiera scenę: rozmytą okładkę w tle, płytę, tytuł i postęp utworu. Przyciski chowają się same po kilku sekundach bez obsługi.
- Siedem wizualizacji pod klawiszem V: Płyta, Okładka, Noc, Widmo, Fala, Horyzont i Zorza. Cewka pamięta wybór, a każdą wizualizację poza Płytą można wyłączyć z cyklu.
- Klawisz Q pokazuje, co zagra jako następne, i rząd okładek z kolejki.
- Gdy gra muzyka, ekran się nie wygasza. Można to wyłączyć w ustawieniach.
- Wygląd sceny da się dopasować: położenie i wielkość napisów, wielkość płyty i okładki oraz plamy, poświatę i drobinki światła w barwach okładki.
- Wizualizacje reagujące na dźwięk mają ogranicznik błysków: jasność nie zmienia się szybciej, niż dopuszczają zalecenia dla osób wrażliwych na błyski. Przy ograniczonym ruchu w systemie zastępuje je Płyta.
- Zorza potrzebuje karty graficznej i zasilania z sieci. Bez nich scena pokazuje Płytę.
- Pierwsze kliknięcie przy schowanych przyciskach tylko je odsłania, więc nic nie włącza się przypadkiem.

### Telewizor

- Scenę można otworzyć na wybranym ekranie. Po odłączeniu tego ekranu scena się zamyka, a okno wraca na ekran główny.
- Telewizorom, które ucinają brzegi obrazu, pomaga margines zapamiętywany osobno dla każdego ekranu. Ramka „Dopasuj krawędzie” pozwala ustawić go strzałkami.
- Tekst sceny w trzech wielkościach: normalnej, dużej i bardzo dużej.
- Ochrona ekranów OLED: obraz powoli się przesuwa, stałe napisy przygasają po trzech minutach bez obsługi, a przy schowanych przyciskach płyta i tekst zamieniają się stronami przy zmianie utworu.

### Radio internetowe

- Ponad 700 stacji z podziałem na kraje i kategorie, z wyszukiwaniem, ulubionymi i ostatnio słuchanymi. Ctrl+1 i Ctrl+2 przełączają między plikami a radiem.
- Własne stacje, import list M3U i PLS oraz eksport ulubionych.
- Stacje w MP3, AAC i HLS. Cewka pokazuje tytuł granego utworu, po zerwaniu sieci łączy się ponownie, a gdy stacja nie gra, podaje przyczynę.
- Loga stacji są zapamiętywane na dysku. Na listach stacje z logo są wyżej.
- Radio nie używa ciasteczek, identyfikatorów ani telemetrii i nie sięga do sieci lokalnej. Połączenia radia i pobieranie logo można wyłączyć w ustawieniach.
- Klawisze multimedialne i panel multimediów systemu działają także z radiem.
- Czerwona kropka przy „Na żywo” pulsuje, gdy stacja gra.
- Po uruchomieniu w trybie radia może od razu zagrać ostatnia stacja.

### Pozostałe

- Nowa ikona: miedziana cewka głośnika.
- Trzy nowe języki: chiński uproszczony, japoński i portugalski brazylijski.
- Tytuły po chińsku, japońsku, arabsku, hebrajsku, tajsku i w piśmie dewanagari wyświetlają się poprawnie bez dodatkowych fontów w systemie.
- Opis formatu, np. „WAV 16 bit / 706 kbps / 44,1 kHz”, jest teraz dyskretnie za nazwą wykonawcy zamiast plakietki nad tytułem.
- Korektor i efekty mają własne okno, otwierane przyciskiem w pasku tytułu albo klawiszem Q. Okno główne jest dzięki temu prostsze.
- Ustawienia są podzielone na kategorie i mają wyszukiwarkę (Ctrl+F), która prowadzi wprost do znalezionej opcji. Zmiana języka działa od razu we wszystkich oknach.
- Okna ustawień i korektora otwierają się za pierwszym razem na środku okna głównego i zawsze mieszczą się na ekranie. Przesunięte wracają w zapamiętane miejsce.
- Miękka pauza: dźwięk cichnie łagodnie zamiast urywać się. Do włączenia w ustawieniach.
- Można wybrać tempo obrotu płyty, ruchu tła i opadania słupków widma.
- Kolejka wysuwa się po najechaniu na prawą krawędź okna i chowa po zjechaniu kursorem. Klawisz L przypina ją na stałe.
- Czarno-białe okładki dają szare tło zamiast przypadkowych barw.
- Znaki na przyciskach okna w stylu macOS są dokładnie na środku.
- Cewka działa na nowszej wersji .NET, z poprawkami bezpieczeństwa do listopada 2028. W Linuksie odtwarzanie przez PulseAudio nie zawiesza się przy starcie, a urządzenia nie znikają po ponownym uruchomieniu serwera dźwięku.

## 0.9.0 (2026-10-31)

Przegląd całego programu: dźwięk, okno, kolejka, klawiatura i teksty.

### Dźwięk

- Przewijanie nie trzeszczy i nie skacze głośnością, a pauza wycisza się płynnie.
- Ostatni utwór w kolejce gra do samego końca.
- Ogranicznik chroni to, co naprawdę trafia do głośników, a normalizacja nie podbija utworu ponad jego szczyt. Pliki Opus korzystają z zapisanego w nich poziomu głośności.
- Crossfeed i poszerzenie stereo zostały dostrojone, a poszerzenie nie rusza już basu. Efekty włączają się i zmieniają siłę bez trzasków.
- Losowanie gra każdy utwór raz, zanim zacznie od nowa.
- Głośność zmienia się tak, jak się ją słyszy, a wyciszenie (M albo kliknięcie w głośnik) nie gubi ustawionego poziomu. Ustawienia z 0.8.1 przechodzą bez zmian.
- Po odłączeniu urządzenia odtwarzanie staje z komunikatem i przyciskami „Spróbuj ponownie” oraz „Wybierz urządzenie”. Gdy wybranego urządzenia nie da się otworzyć, gra domyślne.
- Pliki wielokanałowe są miksowane według układu kanałów, Vorbis i Opus do 7.1. Cewka rozpoznaje też surowy AAC oraz FLAC w kontenerze Ogg.
- Nowa instalacja startuje z płaskim korektorem, a korektor ma przycisk „Wyzeruj”.

### Okno

- Pokazanie kolejki albo korektora nie zmienia rozmiaru okna. W wąskim oknie kolejka wysuwa się nad treść.
- W Windowsie działa przyciąganie okna do krawędzi ekranu i Win+strzałki.
- Przełączniki w pasku tytułu pokazują, czy obszar jest włączony.
- Drobny tekst jest czytelny na każdym tle z okładki.
- W pauzie animacje stają, a program zużywa około 0,2% rdzenia procesora zamiast około 15%. Animacje stają też, gdy system ma je wyłączone.
- Cyfry czasu mają stałą szerokość, więc licznik nie drga.
- Okno ustawień mieści się na małym ekranie i pamięta położenie.
- Okno zapamiętane na odłączonym monitorze otwiera się na ekranie głównym.

### Kolejka

- Kliknięcie zaznacza wiersz, a dwuklik albo Enter go odtwarza.
- Ctrl+Z cofa usunięcie, wyczyszczenie i zastąpienie kolejki.
- Kolejka i pozycja w utworze zapisują się na bieżąco, nie tylko przy zamknięciu programu.
- Plik dodany pod koniec ostatniego utworu zagra po nim, a plik albo folder otwarty w chwili zamykania programu nie przepada.
- Foldery dodają się bez zamrażania okna, a pliki układają się w naturalnej kolejności.
- Listy M3U z innych programów wczytują się poprawnie, także zapisane w starszym kodowaniu Windowsa.
- Gdy plik zostaje pominięty, komunikat podaje jego nazwę i przyczynę.

### Klawiatura i dostępność

- Nowe skróty: S (losowo), R (powtarzanie), M (wyciszenie) i Ctrl+Z. Pełna lista jest w ustawieniach, w zakładce O programie.
- Fadery i suwaki działają z klawiatury, a zaznaczona kontrolka jest dobrze widoczna.
- Czytnik ekranu odczytuje nazwę i stan każdej kontrolki, także granego wiersza kolejki.

### Poza tym

- Teksty poprawione we wszystkich 17 językach: jedno pojęcie ma jedną nazwę, podpowiedzi są krótsze, a komunikaty zrozumiałe.
- Paczki zawierają noty licencyjne składników.

## 0.8.1 (2026-08-18)

- W Linuksie plik otwarty z menedżera plików trafia zawsze do grającej Cewki, zamiast czasem uruchamiać drugą kopię programu.
- Kanał, przez który Cewka przekazuje pliki, jest w katalogu użytkownika, a nie we wspólnym katalogu tymczasowym.

## 0.8.0 (2026-08-18)

- Pięć nowych efektów, domyślnie wyłączonych: crossfeed, kompensacja głośności, wirtualny bas, kompresja dynamiki i poszerzenie stereo.
- Kolejka jest po prawej stronie okna, a efekty obok korektora. Oba obszary włączają klawisze Q i L albo przyciski w pasku tytułu.
- Sześć nowych par barw domyślnej okładki.
- Po instalacji pakietu Cewka jest w menu aplikacji.
- Fala nad paskiem postępu rysuje się poprawnie, a w wąskim oknie odtwarzacz nie nachodzi na kolejkę.

## 0.7.20 (2026-08-17)

- Cewka uruchamia się także w systemie bez zainstalowanych fontów.
- Pakiety przed wydaniem są próbnie instalowane w Ubuntu, Fedorze i Archu.
- Archiwum `.tar.gz` dla Linuksa, do uruchomienia bez instalacji.

## 0.7.11 (2026-08-17)

- Listy odtwarzania M3U.
- Siedemnaście języków interfejsu.
- Cewka sprawdza, czy jest nowsza wersja.
- Trzy intensywności barw tła i nowe pary barw domyślnej okładki.
- Plik otwarty z zewnątrz w miejsce kolejki gra on, a nie poprzedni utwór. Język „systemowy” działa.

## 0.6.0 (2026-08-17)

Pierwsze wydanie.

- MP3, FLAC, WAV, Ogg Vorbis i Opus, a AAC, M4A i ALAC przez kodeki systemu.
- Odtwarzanie bez przerw między utworami, kolejka z losowaniem i powtarzaniem.
- Korektor dziesięciopasmowy z przedwzmacniaczem i ogranicznikiem.
- Wyrównanie głośności według znaczników ReplayGain albo własnego pomiaru.
- Motyw jasny i ciemny, tło w barwach okładki, pięć języków.
- Panel multimediów systemu, klawisze multimedialne, jedna kopia programu i skojarzenia plików.
- Pakiety `.deb`, `.rpm` i `.pkg.tar.zst` oraz jeden plik `.exe` dla Windowsa.
