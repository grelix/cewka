#!/bin/sh
# Buduje wydanie dla systemu Linux: jeden plik wykonywalny, bez wymogu srodowiska .NET.
# Wynik trafia do artifacts/publish/linux-x64/Cewka
#
# Uzycie:  ./tools/publish-linux.sh

set -e

KATALOG="$(cd "$(dirname "$0")/.." && pwd)"

# Biblioteka natywna musi powstac przed publikacja - to ona jest pakowana do wyniku.
# Katalog runtimes/ jest poza gitem i przetrwa aktualizacje zrodel, wiec biblioteka starsza
# od zrodla jest budowana od nowa; inaczej do wydania trafilaby poprzednia wersja.
SO="$KATALOG/src/Cewka.Audio/runtimes/linux-x64/native/libcewka_audio.so"
if [ ! -f "$SO" ] ||
   [ "$KATALOG/native/cewka_audio.c" -nt "$SO" ] ||
   [ "$KATALOG/native/miniaudio/miniaudio.h" -nt "$SO" ]; then
    echo "Biblioteka natywna nieaktualna lub brak, budowanie..."
    "$KATALOG/native/build-linux.sh"
fi

# Plik poprzedniej publikacji usuwany z gory: gdy ta publikacja sie przerwie, nie zostanie
# nic, co pakowanie mogloby wziac za jej wynik.
rm -f "$KATALOG/artifacts/publish/linux-x64/Cewka"

dotnet publish "$KATALOG/src/Cewka.App" \
    --configuration Release \
    --runtime linux-x64 \
    -p:PublishSingleFile=true \
    --output "$KATALOG/artifacts/publish/linux-x64"

chmod +x "$KATALOG/artifacts/publish/linux-x64/Cewka"

echo
echo "Gotowe: $KATALOG/artifacts/publish/linux-x64/Cewka"
