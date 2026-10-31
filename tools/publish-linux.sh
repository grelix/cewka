#!/bin/sh
# Buduje wydanie dla Linuksa: jeden plik wykonywalny, bez wymogu środowiska .NET.
# Wynik trafia do artifacts/publish/linux-x64/Cewka
#
# Użycie:  ./tools/publish-linux.sh

set -e

KATALOG="$(cd "$(dirname "$0")/.." && pwd)"

# Biblioteka natywna powstaje przed publikacją, bo trafia do wyniku. Pułapka: runtimes/ jest
# poza gitem, więc biblioteka starsza od źródła albo skryptu budowy jest budowana od nowa.
SO="$KATALOG/src/Cewka.Audio/runtimes/linux-x64/native/libcewka_audio.so"
if [ ! -f "$SO" ] ||
   [ "$KATALOG/native/cewka_audio.c" -nt "$SO" ] ||
   [ "$KATALOG/native/build-linux.sh" -nt "$SO" ] ||
   [ "$KATALOG/native/miniaudio/miniaudio.h" -nt "$SO" ]; then
    echo "Biblioteka natywna nieaktualna lub brak, budowanie..."
    "$KATALOG/native/build-linux.sh"
fi

# Plik poprzedniej publikacji usuwany z góry, żeby po przerwanej publikacji pakowanie nie
# wzięło go za wynik.
rm -f "$KATALOG/artifacts/publish/linux-x64/Cewka"

dotnet publish "$KATALOG/src/Cewka.App" \
    --configuration Release \
    --runtime linux-x64 \
    -p:PublishSingleFile=true \
    --output "$KATALOG/artifacts/publish/linux-x64"

chmod +x "$KATALOG/artifacts/publish/linux-x64/Cewka"

echo
echo "Gotowe: $KATALOG/artifacts/publish/linux-x64/Cewka"
