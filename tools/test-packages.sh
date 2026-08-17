#!/bin/bash
# Probna instalacja pakietow w kontenerach trzech rodzin dystrybucji.
#
#   .deb          ubuntu:24.04
#   .rpm          fedora:41
#   .pkg.tar.zst  archlinux:latest
#
# Kazdy kontener: instalacja, sprawdzenie plikow i wpisu w menu, sprawdzenie zaleznosci,
# uruchomienie programu pod serwerem X bez ekranu, odinstalowanie bez pozostalosci.
#
# Wymaga: docker
# Uzycie:  ./tools/test-packages.sh [katalog-pakietow] [katalog-materialu]
#
# Pakiety musza juz istniec — najpierw ./tools/publish-linux.sh i ./tools/build-packages.sh

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
PAKIETY="${1:-$REPO/artifacts/packages}"
MATERIAL="${2:-$REPO/artifacts/material}"

if ! command -v docker >/dev/null 2>&1; then
    echo "Brak polecenia docker." >&2
    exit 1
fi

if [ -z "$(ls "$PAKIETY"/cewka* 2>/dev/null)" ]; then
    echo "W katalogu $PAKIETY nie ma pakietow — uruchom najpierw ./tools/build-packages.sh" >&2
    exit 1
fi

UTWOR="$MATERIAL/probka-dluga.wav"
if [ ! -f "$UTWOR" ]; then
    echo "Brak $UTWOR — uruchom najpierw:" >&2
    echo "  dotnet run --project tools/Cewka.Snapshots -- --material $MATERIAL" >&2
    exit 1
fi

# ---------------------------------------------------------------------------
# Czesc wspolna, identyczna dla wszystkich trzech dystrybucji. Rozni je wylacznie
# menedzer pakietow, wiec tylko on jest wstrzykiwany z zewnatrz.
# ---------------------------------------------------------------------------
WSPOLNE='
echo "--- pliki ---"
test -x /usr/bin/cewka || { echo "BLAD: brak /usr/bin/cewka"; exit 1; }
test -f /usr/share/applications/cewka.desktop || { echo "BLAD: brak wpisu w menu"; exit 1; }
for R in 48 64 128 256; do
    test -f "/usr/share/icons/hicolor/${R}x${R}/apps/cewka.png" || { echo "BLAD: brak ikony ${R}"; exit 1; }
done
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
echo "wszystkie biblioteki odnalezione"

echo "--- uruchomienie ---"
set +e
timeout 20 xvfb-run -a /usr/bin/cewka /material/probka-dluga.wav >/tmp/dziennik.txt 2>&1
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
'

POZOSTALOSCI='
echo "--- pozostalosci ---"
for P in /usr/bin/cewka /usr/share/applications/cewka.desktop /usr/share/icons/hicolor/128x128/apps/cewka.png; do
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
    'apt-get install -y -qq /pakiety/cewka_*_amd64.deb' \
    'apt-get remove -y -qq cewka >/dev/null'

# Obraz Fedory bez numeru wydania. Przypiety numer bylby czytelniejszy, ale wydania Fedory
# zyja okolo trzynastu miesiecy i po wycofaniu ich repozytoria wedruja do archiwum — proba
# zaczelaby sie wtedy wywracac na instalacji zaleznosci, a nie na tym, co ma sprawdzac.
sprawdz "Fedora" "fedora:latest" \
    'dnf install -y -q desktop-file-utils xorg-x11-server-Xvfb >/dev/null' \
    'dnf install -y -q /pakiety/cewka-*.x86_64.rpm' \
    'dnf remove -y -q cewka >/dev/null'

sprawdz "Arch" "archlinux:latest" \
    'pacman -Syu --noconfirm --needed desktop-file-utils xorg-server-xvfb >/dev/null' \
    'pacman -U --noconfirm /pakiety/cewka-*-x86_64.pkg.tar.zst' \
    'pacman -R --noconfirm cewka >/dev/null'

echo
echo "Wszystkie trzy proby przeszly."
