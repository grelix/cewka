#!/bin/bash
# Sprawdza, czy numer wersji zgadza sie wszedzie tam, gdzie musi byc wpisany.
#
# Zrodlem jest <Version> w Directory.Build.props. PKGBUILD musi niesc ten sam numer jako
# pkgver, bo makepkg nie czyta plikow projektu. Podany znacznik (np. v0.8.1) jest
# porownywany z ta sama wartoscia.
#
# Uzycie:  ./tools/sprawdz-wersje.sh [znacznik]
# Wypisuje wersje na standardowe wyjscie; przy niezgodnosci konczy sie bledem.

set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"

WERSJA="$(grep -oP '(?<=<Version>)[^<]+' "$REPO/Directory.Build.props" | head -1 || true)"
if [ -z "$WERSJA" ]; then
    echo "BLAD: nie udalo sie odczytac wersji z Directory.Build.props" >&2
    exit 1
fi

if grep -q -E '<(AssemblyVersion|FileVersion)>' "$REPO/Directory.Build.props"; then
    echo "BLAD: Directory.Build.props podaje AssemblyVersion lub FileVersion recznie." >&2
    echo "Obie wyprowadza SDK z <Version>; reczna wartosc rozjezdza sie przy podbiciu wersji." >&2
    exit 1
fi

PKGVER="$(sed -n 's/^pkgver=//p' "$REPO/packaging/arch/PKGBUILD" | head -1)"
if [ "$PKGVER" != "$WERSJA" ]; then
    echo "BLAD: pkgver=$PKGVER w packaging/arch/PKGBUILD, a Directory.Build.props podaje $WERSJA." >&2
    exit 1
fi

if [ $# -ge 1 ] && [ "${1#v}" != "$WERSJA" ]; then
    echo "BLAD: znacznik $1 nie odpowiada wersji $WERSJA z Directory.Build.props." >&2
    echo "Pakiety nazwane jedna wersja, a zawierajace inna, zdarzyly sie juz raz." >&2
    exit 1
fi

echo "$WERSJA"
