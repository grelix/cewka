#!/bin/sh
# Buduje wydanie dla systemu Linux: jeden plik wykonywalny, bez wymogu srodowiska .NET.
# Wynik trafia do artifacts/publish/linux-x64/Cewka
#
# Uzycie:  ./tools/publish-linux.sh

set -e

KATALOG="$(cd "$(dirname "$0")/.." && pwd)"

# Biblioteka natywna musi powstac przed publikacja - to ona jest pakowana do wyniku.
if [ ! -f "$KATALOG/src/Cewka.Audio/runtimes/linux-x64/native/libcewka_audio.so" ]; then
    echo "Brak biblioteki natywnej, budowanie..."
    "$KATALOG/native/build-linux.sh"
fi

dotnet publish "$KATALOG/src/Cewka.App" \
    --configuration Release \
    --runtime linux-x64 \
    -p:PublishSingleFile=true \
    --output "$KATALOG/artifacts/publish/linux-x64"

chmod +x "$KATALOG/artifacts/publish/linux-x64/Cewka"

echo
echo "Gotowe: $KATALOG/artifacts/publish/linux-x64/Cewka"
