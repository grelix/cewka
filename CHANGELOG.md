# Zmiany

Każda wersja opisuje tylko to, co zmieniło się od poprzedniej.

## 0.9.0 (2026-10-31)

Przegląd całego programu: dźwięk, okno, kolejka, obsługa z klawiatury i teksty.

### Dźwięk

- Przewijanie nie daje już trzasku ani skoku głośności, a pauza wycisza się płynnie.
- Ostatni utwór w kolejce gra do końca, bez uciętej końcówki.
- Ogranicznik działa za regulacją głośności, więc chroni to, co naprawdę trafia do głośników.
- Normalizacja nie podbija utworu ponad jego szczyt. Opus bierze wzmocnienie R128 z nagłówka.
- Crossfeed i poszerzenie stereo brzmią inaczej: crossfeed w zakresie nastaw bs2b, poszerzenie nie rusza basu. Efekty włączają się i zmieniają siłę bez trzasków.
- Odtwarzanie losowe gra każdy utwór raz, zanim zacznie od nowa.
- Głośność zmienia się w skali dopasowanej do słuchu, a wyciszenie (M albo klik w głośnik) jest osobnym stanem. Ustawienia z 0.8.1 przechodzą bez zmiany poziomu.
- Po odłączeniu urządzenia odtwarzanie staje z komunikatem. Gdy wybranego urządzenia nie da się otworzyć, gra domyślne. Komunikat ma przyciski „Spróbuj ponownie” i „Wybierz urządzenie”.
- Pliki wielokanałowe są miksowane według układu kanałów, Vorbis i Opus do 7.1. Surowy AAC i FLAC w Ogg są rozpoznawane po zawartości.
- Nowa instalacja startuje z płaskim korektorem, a korektor ma przycisk „Wyzeruj”.

### Okno

- Pokazanie i schowanie kolejki albo korektora nie zmienia rozmiaru okna. W oknie węższym niż 1040 px kolejka wysuwa się nad treść.
- W Windowsie działa przyciąganie okna do krawędzi ekranu i Win+strzałki.
- Przełączniki kolejki i korektora w pasku tytułu pokazują, czy są włączone.
- Drobny tekst jest czytelny na każdym tle z okładki.
- Animacje stają w pauzie i wtedy, gdy są wyłączone w systemie.
- Liczby i czasy są w kroju interfejsu, z cyframi tej samej szerokości. JetBrains Mono nie jest już dołączany.
- Okno ustawień mieści się na małym ekranie i pamięta położenie. Korektor i efekty są już tylko w oknie głównym.
- Okno zapisane na odłączonym monitorze otwiera się na głównym ekranie.

### Kolejka

- Kliknięcie zaznacza wiersz, odtwarza dwuklik albo Enter.
- Ctrl+Z cofa usunięcie, wyczyszczenie i zastąpienie kolejki listą.
- Kolejka i pozycja zapisują się na bieżąco, nie tylko przy zamknięciu programu.
- Plik dodany pod sam koniec ostatniego utworu gra po nim, a plik otwarty w chwili zamykania programu nie przepada. Folder, którego nie zdążono przejrzeć przed zamknięciem, dodaje się przy następnym uruchomieniu.
- Folder dodaje się bez zamrażania okna, a pliki układają się w porządku naturalnym.
- Wczytują się listy M3U z innych programów, także w stronie kodowej Windowsa i z adresami `file://`.
- Pominięty plik daje komunikat z nazwą i przyczyną.

### Klawiatura i dostępność

- Nowe skróty: S (losowo), R (powtarzanie), M (wyciszenie), Ctrl+Z. Pełna lista jest w ustawieniach, w zakładce O programie.
- Fadery i suwaki działają z klawiatury, a fokus ma widoczny pierścień. Przełącznik z fokusem zmienia się Spacją.
- Czytnik ekranu dostaje nazwę i stan każdej kontrolki, także granego wiersza kolejki.

### Teksty

- Teksty interfejsu poprawione we wszystkich językach: jedna nazwa dla jednego pojęcia, krótsze podpowiedzi, komunikaty bez tekstów wyjątków.

### Wydanie

- Archiwum, pakiety i wydanie dla Windows zawierają noty licencyjne składników (`THIRD-PARTY-NOTICES.txt`).

## 0.8.1 (2026-08-18)

- Otwarcie pliku z menedżera plików w Linuksie uruchamiało czasem drugą kopię programu zamiast oddać plik tej, która już gra.
- Kopia, która nie przejęła roli ani nie oddała plików, otwierała okno bez nasłuchu. Teraz ponawia próbę.
- Kanał przekazywania ścieżek jest w katalogu użytkownika, a nie w `/tmp`.

## 0.8.0 (2026-08-18)

- Pięć efektów, domyślnie wyłączonych: crossfeed, kompensacja głośności, bas wirtualny, kompresja dynamiki i poszerzenie stereo.
- Kolejka przeniesiona na prawą stronę okna, a efekty stanęły obok korektora. Oba obszary włącza się klawiszami Q i L albo przyciskami w pasku tytułu.
- Sześć nowych par barw domyślnej okładki.
- Program pojawia się w menu aplikacji po instalacji pakietu.
- Poprawiona fala nad paskiem postępu i blok odtwarzania nachodzący na kolejkę w węższym oknie.

## 0.7.20 (2026-08-17)

- Program nie startował w systemie bez zainstalowanych fontów.
- Skrypty budowania miały bit wykonywalności zgubiony w repozytorium.
- Wydania i pakiety powstają w potoku budowy, a pakiety są próbnie instalowane w Ubuntu, Fedorze i Archu.
- Archiwum `.tar.gz` dla Linuksa.

## 0.7.11 (2026-08-17)

- Listy odtwarzania M3U.
- Siedemnaście języków interfejsu.
- Sprawdzanie nowej wersji.
- Trzy intensywności barw tła i pary barw domyślnej okładki.
- Zastąpienie kolejki plikiem z zewnątrz odtwarzało poprzedni utwór, a język „systemowy” nie działał.

## 0.6.0 (2026-08-17)

Pierwsze wydanie.

- MP3, FLAC, WAV, Ogg Vorbis i Opus, a AAC, M4A i ALAC przez kodeki systemu.
- Odtwarzanie bez przerw między utworami, kolejka z losowaniem i powtarzaniem.
- Korektor dziesięciopasmowy z przedwzmacniaczem i ogranicznikiem.
- Wyrównanie głośności według ReplayGain albo własnego pomiaru EBU R128.
- Motyw jasny i ciemny, tło z barw okładki, pięć języków.
- Panel multimediów systemu, klawisze multimedialne, jedna kopia programu i skojarzenia plików.
- Pakiety `.deb`, `.rpm` i `.pkg.tar.zst` oraz jeden plik `.exe` dla Windowsa.
