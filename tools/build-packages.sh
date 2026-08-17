#!/bin/bash
# Buduje pakiety instalacyjne dla trzech rodzin dystrybucji z gotowego pliku wykonywalnego:
#
#   .deb          Debian, Ubuntu, Linux Mint, Pop!_OS
#   .rpm          Fedora, RHEL, openSUSE
#   .pkg.tar.zst  Arch, Manjaro, EndeavourOS
#
# Wymaga: dpkg-deb, rpmbuild, bsdtar, zstd
# Uzycie:  ./tools/build-packages.sh
#
# Plik wykonywalny musi juz istniec — najpierw ./tools/publish-linux.sh

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

# Czy plik wykonywalny jest z TEJ wersji.
#
# Sprawdzenie samego istnienia pliku nie wystarcza i kosztowalo juz raz trzy pakiety nazwane
# nowa wersja, a zawierajace poprzednia: publikacja przerwala sie bledem, stary plik zostal
# na dysku, a pakowanie poszlo dalej jak gdyby nigdy nic. Numer wersji jest wpisany
# w metadane zestawu, wiec wystepuje w pliku doslownie; jego brak znaczy, ze plik pochodzi
# z innej kompilacji.
if ! grep -a -q "$WERSJA" "$BINARKA"; then
    echo "Plik $BINARKA nie zawiera numeru wersji $WERSJA." >&2
    echo "Pochodzi z innej kompilacji — uruchom najpierw ./tools/publish-linux.sh" >&2
    exit 1
fi

OPIEKUN="Maciej Grela <51171746+grelix@users.noreply.github.com>"
STRONA="https://github.com/grelix/cewka"
SKROT="Minimalistyczny odtwarzacz plikow lokalnych"

# Budowanie odbywa sie w systemie plikow Linuksa, a nie na /mnt/c. Na NTFS nie da sie ustawic
# wlasciciela ani praw dostepu, a pakiet z plikami nalezacymi do przypadkowego uzytkownika
# i z prawami 777 jest bledem, ktory wychodzi dopiero przy instalacji u kogos innego.
PRACA="$(mktemp -d /tmp/cewka-pakiety.XXXXXX)"
trap 'rm -rf "$PRACA"' EXIT

echo "Cewka $WERSJA — budowanie pakietow"
echo "  katalog roboczy: $PRACA"
echo

# ---------------------------------------------------------------------------
# Wspolne drzewo plikow, zgodne z FHS. Te same sciezki we wszystkich pakietach.
# ---------------------------------------------------------------------------
przygotuj_drzewo() {
    local root="$1"

    install -Dm755 "$BINARKA" "$root/usr/bin/cewka"
    install -Dm644 "$REPO/packaging/linux/cewka.desktop" "$root/usr/share/applications/cewka.desktop"

    # Kazdy rozmiar w swoim katalogu motywu — tego wymaga specyfikacja katalogu ikon
    # freedesktop i tylko wtedy pulpit bierze wariant narysowany dla danej wielkosci.
    local rozmiar
    for rozmiar in 48 64 128 256; do
        install -Dm644 "$REPO/packaging/linux/icons/cewka-$rozmiar.png" \
            "$root/usr/share/icons/hicolor/${rozmiar}x${rozmiar}/apps/cewka.png"
    done
}

# Odswiezenie baz pulpitu. Bez tego wpis w menu pojawia sie dopiero po przelogowaniu,
# a ikona bywa zastapiona znakiem zapytania.
SKRYPT_PO_INSTALACJI='
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
fi
if command -v update-mime-database >/dev/null 2>&1; then
    update-mime-database /usr/share/mime >/dev/null 2>&1 || true
fi
'

mkdir -p "$WYNIK"

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
Depends: libc6 (>= 2.34), libgcc-s1, libstdc++6, zlib1g, libx11-6, libxrandr2, libxi6, libfontconfig1
Recommends: gstreamer1.0-plugins-good, gstreamer1.0-libav
Homepage: $STRONA
Description: $SKROT
 Odtwarzacz plikow lokalnych z korektorem dziesieciopasmowym, wyrownaniem
 glosnosci wedlug EBU R128 oraz tlem skladanym z barw okladki. Nie wymaga
 zainstalowanego srodowiska .NET — cala zawartosc jest w jednym pliku.
 .
 Formaty MP3, FLAC, WAV, Ogg Vorbis i Opus obsluguje samodzielnie. Do AAC,
 M4A i ALAC potrzebuje GStreamera z pakietu gstreamer1.0-libav.
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

# --root-owner-group: pliki w pakiecie naleza do root, niezaleznie od tego, kto buduje.
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

cat > "$RPM_ROOT/SPECS/cewka.spec" <<KONIEC
# Plik wykonywalny jest gotowy i samodzielny: pakiet niczego nie kompiluje.
# Wylaczenie debuginfo i skryptow poinstalacyjnych rpmbuilda jest tu konieczne —
# domyslne "strip" naruszylo by strukture jednoplikowego pakietu .NET.
%global debug_package %{nil}
%global __os_install_post %{nil}
%global __strip /bin/true
# Bez tego rpm dokłada dowiazania w /usr/lib/.build-id, wyprowadzone z identyfikatora
# kompilacji. Przy pakiecie z gotowym binarium nie sluza niczemu poza zasmiecaniem listy plikow.
%global _build_id_links none

Name:           cewka
Version:        $WERSJA
Release:        1
Summary:        $SKROT
License:        MIT
URL:            $STRONA
Packager:       $OPIEKUN
BuildArch:      x86_64

Requires:       glibc, libgcc, libstdc++, zlib, libX11, libXrandr, libXi, fontconfig
Recommends:     gstreamer1-plugins-good

%description
Odtwarzacz plikow lokalnych z korektorem dziesieciopasmowym, wyrownaniem
glosnosci wedlug EBU R128 oraz tlem skladanym z barw okladki. Nie wymaga
zainstalowanego srodowiska .NET — cala zawartosc jest w jednym pliku.

Formaty MP3, FLAC, WAV, Ogg Vorbis i Opus obsluguje samodzielnie. Do AAC,
M4A i ALAC potrzebuje GStreamera z dekoderem libav.

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
* Mon Aug 17 2026 $OPIEKUN - $WERSJA-1
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
# Znacznik czasu przekazany z zewnatrz, zeby ten sam material dawal ten sam pakiet.
CZAS="${SOURCE_DATE_EPOCH:-$(date +%s)}"

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
optdepend = gst-plugins-good: obsluga AAC, M4A i ALAC
optdepend = gst-libav: obsluga AAC, M4A i ALAC
KONIEC

# Skrypt instalacyjny pacmana odswieza bazy pulpitu tak samo jak w pozostalych pakietach.
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

# Kolejnosc wpisow ma znaczenie: pacman czyta .PKGINFO jako pierwszy element archiwum.
(
    cd "$ARCH_ROOT"
    bsdtar --uid 0 --gid 0 --numeric-owner -cf - .PKGINFO .INSTALL usr |
        zstd -19 -q -o "$WYNIK/cewka-${WERSJA}-1-x86_64.pkg.tar.zst" -f
)
echo "      cewka-${WERSJA}-1-x86_64.pkg.tar.zst"

echo
echo "Gotowe. Pakiety w $WYNIK:"
ls -lh "$WYNIK" | tail -n +2 | awk '{ printf "  %-42s %s\n", $9, $5 }'
