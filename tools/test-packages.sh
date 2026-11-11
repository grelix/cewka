#!/bin/bash
# Próbna instalacja pakietów w kontenerach trzech rodzin dystrybucji.
#
#   .deb          ubuntu:24.04
#   .rpm          fedora:latest
#   .pkg.tar.zst  archlinux:latest
#
# W każdym kontenerze: instalacja, sprawdzenie plików, wpisu w menu i zależności,
# uruchomienie programu pod serwerem X bez ekranu, odinstalowanie bez pozostałości.
#
# Wymaga: docker
# Użycie:  ./tools/test-packages.sh [katalog-pakietów] [katalog-materiału]
#
# Pakiety muszą już istnieć; najpierw ./tools/publish-linux.sh i ./tools/build-packages.sh

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
PAKIETY="${1:-$REPO/artifacts/packages}"
MATERIAL="${2:-$REPO/artifacts/material}"

if ! command -v docker >/dev/null 2>&1; then
    echo "Brak polecenia docker." >&2
    exit 1
fi

WERSJA="$(grep -oP '(?<=<Version>)[^<]+' "$REPO/Directory.Build.props" | head -1)"
if [ -z "$WERSJA" ]; then
    echo "Nie udalo sie odczytac wersji z Directory.Build.props" >&2
    exit 1
fi

# Pakiety bieżącej wersji po pełnej nazwie; wzorzec z gwiazdką objąłby też starsze.
DEB="cewka_${WERSJA}_amd64.deb"
RPM="cewka-${WERSJA}-1.x86_64.rpm"
PACMAN="cewka-${WERSJA}-1-x86_64.pkg.tar.zst"
for PLIK in "$DEB" "$RPM" "$PACMAN"; do
    if [ ! -f "$PAKIETY/$PLIK" ]; then
        echo "Brak $PAKIETY/$PLIK — uruchom najpierw ./tools/build-packages.sh" >&2
        exit 1
    fi
done

UTWOR="$MATERIAL/probka-dluga.wav"
if [ ! -f "$UTWOR" ]; then
    echo "Brak $UTWOR — uruchom najpierw:" >&2
    echo "  dotnet run --project tools/Cewka.Snapshots -- --material $MATERIAL" >&2
    exit 1
fi

# ---------------------------------------------------------------------------
# Część wspólna dla trzech dystrybucji; z zewnątrz przychodzi tylko menedżer pakietów.
# ---------------------------------------------------------------------------
WSPOLNE='
echo "--- pliki ---"
test -x /usr/bin/cewka || { echo "BLAD: brak /usr/bin/cewka"; exit 1; }
test -f /usr/share/applications/cewka.desktop || { echo "BLAD: brak wpisu w menu"; exit 1; }
for R in 16 22 24 32 48 64 128 256; do
    test -f "/usr/share/icons/hicolor/${R}x${R}/apps/cewka.png" || { echo "BLAD: brak ikony ${R}"; exit 1; }
done
test -f /usr/share/icons/hicolor/scalable/apps/cewka.svg || { echo "BLAD: brak ikony skalowalnej"; exit 1; }
echo "plik wykonywalny: $(stat -c %s /usr/bin/cewka) B"

echo "--- wpis w menu ---"
desktop-file-validate /usr/share/applications/cewka.desktop || { echo "BLAD: wpis w menu niepoprawny"; exit 1; }
echo "cewka.desktop: poprawny"

echo "--- zaleznosci ---"
if ldd /usr/bin/cewka 2>/dev/null | grep -q "not found"; then
    echo "BLAD: brakujace biblioteki:"
    ldd /usr/bin/cewka | grep "not found"
    exit 1
fi
echo "zaleznosci pliku uruchomieniowego odnalezione (biblioteki z pakunku sprawdza uruchomienie)"

echo "--- uruchomienie ---"
# Kontener nie ma karty dźwiękowej, więc CEWKA_NULL_AUDIO=1 włącza puste wyjście silnika.
set +e
CEWKA_NULL_AUDIO=1 timeout 20 xvfb-run -a /usr/bin/cewka /material/probka-dluga.wav >/tmp/dziennik.txt 2>&1
KOD=$?
set -e
echo "kod wyjscia: ${KOD} (124 znaczy: dzialal az do konca limitu czasu)"
echo "--- poczatek dziennika ---"
head -15 /tmp/dziennik.txt || true
echo "--- koniec dziennika ---"
if [ "${KOD}" != "124" ]; then
    echo "BLAD: program nie dotrwal do konca limitu czasu"
    exit 1
fi

# Pułapka: program przechwytuje błędy wyjścia dźwięku, więc sam czas życia procesu nie dowodzi,
# że biblioteki się załadowały. Te wpisy w dzienniku oznaczają wadę pakietu.
if grep -E "DllNotFoundException|BadImageFormatException|EntryPointNotFoundException|Unhandled exception|GLIBC_[0-9.]+. not found" /tmp/dziennik.txt; then
    echo "BLAD: dziennik zdradza brakujaca lub niezgodna biblioteke"
    exit 1
fi
echo "dziennik bez bledow ladowania bibliotek"
'

POZOSTALOSCI='
echo "--- pozostalosci ---"
for P in /usr/bin/cewka /usr/share/applications/cewka.desktop /usr/share/icons/hicolor/128x128/apps/cewka.png /usr/share/icons/hicolor/scalable/apps/cewka.svg; do
    if [ -e "${P}" ]; then echo "BLAD: po odinstalowaniu zostalo ${P}"; exit 1; fi
done
echo "nic nie zostalo"
'

sprawdz() {
    local nazwa="$1" obraz="$2" przygotowanie="$3" instalacja="$4" usuniecie="$5"

    echo
    echo "==============================================================="
    echo " $nazwa  ($obraz)"
    echo "==============================================================="

    docker run --rm \
        -v "$PAKIETY:/pakiety:ro" \
        -v "$MATERIAL:/material:ro" \
        "$obraz" /bin/bash -c "
set -e
export DEBIAN_FRONTEND=noninteractive
echo '--- przygotowanie ---'
$przygotowanie
echo '--- instalacja ---'
$instalacja
$WSPOLNE
echo '--- odinstalowanie ---'
$usuniecie
$POZOSTALOSCI
echo
echo 'WYNIK: $nazwa przeszla'
"
}

sprawdz "Debian i Ubuntu" "ubuntu:24.04" \
    'apt-get update -qq >/dev/null && apt-get install -y -qq desktop-file-utils xvfb >/dev/null' \
    "apt-get install -y -qq /pakiety/$DEB" \
    'apt-get remove -y -qq cewka >/dev/null'

# Obraz Fedory bez numeru wydania, bo repozytoria wycofanego wydania trafiają do archiwum
# i instalacja zależności przestałaby działać.
sprawdz "Fedora" "fedora:latest" \
    'dnf install -y -q desktop-file-utils xorg-x11-server-Xvfb >/dev/null' \
    "dnf install -y -q /pakiety/$RPM" \
    'dnf remove -y -q cewka >/dev/null'

sprawdz "Arch" "archlinux:latest" \
    'pacman -Syu --noconfirm --needed desktop-file-utils xorg-server-xvfb >/dev/null' \
    "pacman -U --noconfirm /pakiety/$PACMAN" \
    'pacman -R --noconfirm cewka >/dev/null'

echo
echo "Wszystkie trzy proby przeszly."
