#!/bin/bash
# Buduje pakiety instalacyjne dla trzech rodzin dystrybucji z gotowego pliku wykonywalnego:
#
#   .deb          Debian, Ubuntu, Linux Mint, Pop!_OS
#   .rpm          Fedora, RHEL, openSUSE
#   .pkg.tar.zst  Arch, Manjaro, EndeavourOS
#
# Wymaga: dpkg-deb, rpmbuild, bsdtar, zstd
# Użycie:  ./tools/build-packages.sh
#
# Plik wykonywalny musi już istnieć; najpierw ./tools/publish-linux.sh

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
BINARKA="$REPO/artifacts/publish/linux-x64/Cewka"
WYNIK="$REPO/artifacts/packages"

if [ ! -f "$BINARKA" ]; then
    echo "Brak $BINARKA — uruchom najpierw ./tools/publish-linux.sh" >&2
    exit 1
fi

WERSJA="$(grep -oP '(?<=<Version>)[^<]+' "$REPO/Directory.Build.props" | head -1)"
if [ -z "$WERSJA" ]; then
    echo "Nie udalo sie odczytac wersji z Directory.Build.props" >&2
    exit 1
fi

# Czy plik wykonywalny jest z bieżącej wersji; po przerwanej publikacji zostaje poprzedni.
#
# Pułapka: pakunek jest skompresowany poza deps.json, gdzie leży klucz "Cewka/<wersja>"
# w cudzysłowach. Szukany jest dosłownie (-F), bo kropki w wyrażeniu pasowałyby do dowolnego
# bajtu, a w danych skompresowanych przypadkowe trafienie jest niemal pewne.
if ! grep -a -q -F "\"Cewka/$WERSJA\"" "$BINARKA"; then
    echo "Plik $BINARKA nie pochodzi z wersji $WERSJA." >&2
    echo "Pochodzi z innej kompilacji — uruchom najpierw ./tools/publish-linux.sh" >&2
    exit 1
fi

# Najnowsza wersja glibc wymagana przez bibliotekę natywną musi zgadzać się z zależnościami
# pakietu .deb, żeby nowszy obraz maszyny budującej nie podniósł wymagania po cichu.
GLIBC_MIN="2.34"
BIBLIOTEKA="$REPO/src/Cewka.Audio/runtimes/linux-x64/native/libcewka_audio.so"
if [ ! -f "$BIBLIOTEKA" ]; then
    echo "Brak $BIBLIOTEKA — uruchom najpierw ./tools/publish-linux.sh" >&2
    exit 1
fi
GLIBC_WYMAGANE="$(grep -a -o 'GLIBC_[0-9.]*[0-9]' "$BIBLIOTEKA" | sed 's/^GLIBC_//' | sort -V | tail -1)"
if [ -n "$GLIBC_WYMAGANE" ] &&
   [ "$(printf '%s\n%s\n' "$GLIBC_WYMAGANE" "$GLIBC_MIN" | sort -V | tail -1)" != "$GLIBC_MIN" ]; then
    echo "Biblioteka natywna wymaga glibc $GLIBC_WYMAGANE, a pakiet deklaruje $GLIBC_MIN." >&2
    echo "Nalezy podniesc GLIBC_MIN w tools/build-packages.sh." >&2
    exit 1
fi

OPIEKUN="Maciej Grela <51171746+grelix@users.noreply.github.com>"
STRONA="https://github.com/grelix/cewka"
SKROT="Minimalistyczny odtwarzacz plikow lokalnych"

# Pułapka: budowanie w systemie plików Linuksa, nie na /mnt/c, bo na NTFS nie da się ustawić
# właściciela ani praw dostępu i pakiet dostałby prawa 777.
PRACA="$(mktemp -d /tmp/cewka-pakiety.XXXXXX)"
trap 'rm -rf "$PRACA"' EXIT

echo "Cewka $WERSJA — budowanie pakietow"
echo "  katalog roboczy: $PRACA"
echo

# ---------------------------------------------------------------------------
# Wspólne drzewo plików zgodne z FHS, te same ścieżki we wszystkich pakietach.
# ---------------------------------------------------------------------------
przygotuj_drzewo() {
    local root="$1"

    install -Dm755 "$BINARKA" "$root/usr/bin/cewka"
    install -Dm644 "$REPO/packaging/linux/cewka.desktop" "$root/usr/share/applications/cewka.desktop"

    # Każdy rozmiar we własnym katalogu motywu, jak wymaga specyfikacja ikon freedesktop.
    local rozmiar
    for rozmiar in 48 64 128 256; do
        install -Dm644 "$REPO/packaging/linux/icons/cewka-$rozmiar.png" \
            "$root/usr/share/icons/hicolor/${rozmiar}x${rozmiar}/apps/cewka.png"
    done
}

# Odświeżenie baz pulpitu; bez tego wpis w menu pojawia się dopiero po przelogowaniu.
SKRYPT_PO_INSTALACJI='
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
fi
'

# Znacznik czasu z zewnątrz, żeby ten sam materiał dawał ten sam pakiet.
CZAS="${SOURCE_DATE_EPOCH:-$(date +%s)}"

# Pakiety poprzednich wersji są usuwane, bo próbna instalacja bierze pliki z tego katalogu.
mkdir -p "$WYNIK"
rm -f "$WYNIK"/cewka_*.deb "$WYNIK"/cewka-*.rpm "$WYNIK"/cewka-*.pkg.tar.zst

# ---------------------------------------------------------------------------
# 1. Pakiet .deb
# ---------------------------------------------------------------------------
echo "[1/3] .deb"
DEB="$PRACA/deb"
przygotuj_drzewo "$DEB"

install -Dm644 "$REPO/LICENSE" "$DEB/usr/share/doc/cewka/copyright"
mkdir -p "$DEB/DEBIAN"

ROZMIAR_KB="$(du -sk "$DEB/usr" | cut -f1)"

cat > "$DEB/DEBIAN/control" <<KONIEC
Package: cewka
Version: $WERSJA
Section: sound
Priority: optional
Architecture: amd64
Maintainer: $OPIEKUN
Installed-Size: $ROZMIAR_KB
Depends: libc6 (>= $GLIBC_MIN), libgcc-s1, libstdc++6, zlib1g, libx11-6, libxrandr2, libxi6, libfontconfig1
Recommends: gstreamer1.0-plugins-base, gstreamer1.0-plugins-good, gstreamer1.0-libav, libssl3t64 | libssl3
Homepage: $STRONA
Description: $SKROT
 Odtwarzacz plikow lokalnych z korektorem dziesieciopasmowym, wyrownaniem
 glosnosci wedlug EBU R128 oraz tlem skladanym z barw okladki. Nie wymaga
 zainstalowanego srodowiska .NET — cala zawartosc jest w jednym pliku.
 .
 Formaty MP3, FLAC, WAV, Ogg Vorbis i Opus obsluguje samodzielnie. AAC, M4A
 i ALAC dekoduje GStreamer: potok z gstreamer1.0-plugins-base, kontener M4A
 z gstreamer1.0-plugins-good, a sam dekoder wylacznie z gstreamer1.0-libav.
KONIEC

cat > "$DEB/DEBIAN/postinst" <<KONIEC
#!/bin/sh
set -e
$SKRYPT_PO_INSTALACJI
exit 0
KONIEC

cat > "$DEB/DEBIAN/postrm" <<KONIEC
#!/bin/sh
set -e
$SKRYPT_PO_INSTALACJI
exit 0
KONIEC

chmod 755 "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/postrm"

# --root-owner-group: pliki w pakiecie należą do root bez względu na to, kto buduje.
dpkg-deb --root-owner-group --build "$DEB" "$WYNIK/cewka_${WERSJA}_amd64.deb" >/dev/null
echo "      $(basename "$WYNIK/cewka_${WERSJA}_amd64.deb")"

# ---------------------------------------------------------------------------
# 2. Pakiet .rpm
# ---------------------------------------------------------------------------
echo "[2/3] .rpm"
RPM_ROOT="$PRACA/rpm"
mkdir -p "$RPM_ROOT"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}
DRZEWO_RPM="$PRACA/rpm-drzewo"
przygotuj_drzewo "$DRZEWO_RPM"

# Data wpisu w %changelog z tego samego znacznika czasu. Nazwy dni i miesięcy po angielsku,
# bo tylko takie przyjmuje rpmbuild.
DATA_ZMIANY="$(LC_ALL=C date -u -d "@$CZAS" '+%a %b %d %Y')"

cat > "$RPM_ROOT/SPECS/cewka.spec" <<KONIEC
# Pułapka: debuginfo i skrypty poinstalacyjne rpmbuilda muszą być wyłączone, bo domyślny
# "strip" uszkodziłby jednoplikowy pakunek .NET.
%global debug_package %{nil}
%global __os_install_post %{nil}
%global __strip /bin/true
# Bez tego rpm dokłada dowiązania w /usr/lib/.build-id, zbędne przy gotowym pliku wykonywalnym.
%global _build_id_links none

Name:           cewka
Version:        $WERSJA
Release:        1
Summary:        $SKROT
License:        MIT
URL:            $STRONA
Packager:       $OPIEKUN
BuildArch:      x86_64

Requires:       glibc >= $GLIBC_MIN, libgcc, libstdc++, zlib, libX11, libXrandr, libXi, fontconfig
Recommends:     gstreamer1-plugins-base
Recommends:     gstreamer1-plugins-good
# Dekoder libav nazywa się w Fedorze inaczej niż w RPM Fusion; wystarczy którykolwiek.
Recommends:     (gstreamer1-plugin-libav or gstreamer1-libav)
Recommends:     openssl-libs

%description
Odtwarzacz plikow lokalnych z korektorem dziesieciopasmowym, wyrownaniem
glosnosci wedlug EBU R128 oraz tlem skladanym z barw okladki. Nie wymaga
zainstalowanego srodowiska .NET — cala zawartosc jest w jednym pliku.

Formaty MP3, FLAC, WAV, Ogg Vorbis i Opus obsluguje samodzielnie. AAC, M4A
i ALAC dekoduje GStreamer: potok z gstreamer1-plugins-base, kontener M4A
z gstreamer1-plugins-good, a sam dekoder wylacznie z pakietu libav.

%install
cp -a $DRZEWO_RPM/. %{buildroot}/
install -Dm644 $REPO/LICENSE %{buildroot}/usr/share/licenses/cewka/LICENSE

%files
%license /usr/share/licenses/cewka/LICENSE
/usr/bin/cewka
/usr/share/applications/cewka.desktop
/usr/share/icons/hicolor/*/apps/cewka.png

%post
$SKRYPT_PO_INSTALACJI

%postun
$SKRYPT_PO_INSTALACJI

%changelog
* $DATA_ZMIANY $OPIEKUN - $WERSJA-1
- Wydanie $WERSJA
KONIEC

rpmbuild --define "_topdir $RPM_ROOT" -bb "$RPM_ROOT/SPECS/cewka.spec" >"$PRACA/rpmbuild.log" 2>&1 || {
    echo "rpmbuild nie powiodl sie:" >&2
    tail -25 "$PRACA/rpmbuild.log" >&2
    exit 1
}

find "$RPM_ROOT/RPMS" -name '*.rpm' -exec cp {} "$WYNIK/" \;
echo "      $(cd "$WYNIK" && ls cewka-${WERSJA}*.rpm)"

# ---------------------------------------------------------------------------
# 3. Pakiet .pkg.tar.zst dla pacmana
# ---------------------------------------------------------------------------
echo "[3/3] .pkg.tar.zst"
ARCH_ROOT="$PRACA/arch"
przygotuj_drzewo "$ARCH_ROOT"
install -Dm644 "$REPO/LICENSE" "$ARCH_ROOT/usr/share/licenses/cewka/LICENSE"

ROZMIAR_B="$(du -sb "$ARCH_ROOT" | cut -f1)"

cat > "$ARCH_ROOT/.PKGINFO" <<KONIEC
pkgname = cewka
pkgbase = cewka
pkgver = $WERSJA-1
pkgdesc = $SKROT
url = $STRONA
builddate = $CZAS
packager = $OPIEKUN
size = $ROZMIAR_B
arch = x86_64
license = MIT
depend = glibc
depend = gcc-libs
depend = zlib
depend = libx11
depend = libxrandr
depend = libxi
depend = fontconfig
optdepend = gst-plugins-base: potok dekodowania AAC, M4A i ALAC
optdepend = gst-plugins-good: rozpakowanie kontenera M4A
optdepend = gst-libav: dekoder AAC i ALAC
optdepend = openssl: sprawdzanie nowszej wersji przez HTTPS
KONIEC

# Skrypt instalacyjny pacmana odświeża bazy pulpitu jak w pozostałych pakietach.
cat > "$ARCH_ROOT/.INSTALL" <<KONIEC
post_install() {
$SKRYPT_PO_INSTALACJI
}

post_upgrade() {
    post_install
}

post_remove() {
    post_install
}
KONIEC

# Powtarzalność: jeden czas modyfikacji i posortowana lista wpisów. Pułapka: pacman czyta
# .PKGINFO jako pierwszy element archiwum.
find "$ARCH_ROOT" -exec touch -h -d "@$CZAS" {} +
(
    cd "$ARCH_ROOT"
    { printf '%s\n' .PKGINFO .INSTALL; find usr | LC_ALL=C sort; } > "$PRACA/arch-lista.txt"
    bsdtar --uid 0 --gid 0 --numeric-owner --no-recursion --no-fflags --no-xattrs --no-acls \
        -cf - -T "$PRACA/arch-lista.txt" |
        zstd -19 -q -o "$WYNIK/cewka-${WERSJA}-1-x86_64.pkg.tar.zst" -f
)
echo "      cewka-${WERSJA}-1-x86_64.pkg.tar.zst"

echo
echo "Gotowe. Pakiety w $WYNIK:"
ls -lh "$WYNIK" | tail -n +2 | awk '{ printf "  %-42s %s\n", $9, $5 }'
