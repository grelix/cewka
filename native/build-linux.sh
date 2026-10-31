#!/usr/bin/env bash
# Buduje libcewka_audio.so dla linux-x64.
# Wymaga wylacznie kompilatora C (gcc lub clang).
#
# Naglowki ALSA, PulseAudio ani PipeWire nie sa potrzebne: miniaudio deklaruje ich symbole
# samo i laduje biblioteki przez dlopen dopiero w czasie dzialania. Sprawdzone na czystej
# instalacji Ubuntu 24.04 w WSL - kompilacja przechodzi bez zadnego pakietu deweloperskiego,
# a wybor zaplecza (tam PulseAudio) nastepuje przy otwarciu urzadzenia.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="${SCRIPT_DIR}/../src/Cewka.Audio/runtimes/linux-x64/native"

CC="${CC:-gcc}"
if ! command -v "${CC}" >/dev/null 2>&1; then
    echo "[blad] Nie odnaleziono kompilatora '${CC}'. Ustaw zmienna CC lub zainstaluj gcc." >&2
    exit 1
fi

mkdir -p "${OUT_DIR}"

# -fPIC          kod niezalezny od polozenia, wymagany dla bibliotek wspoldzielonych
# -O2            optymalizacja
# -fvisibility   domyslnie ukryte symbole; eksportowane sa tylko oznaczone w kodzie
# -fstack-protector-strong, -z relro -z now  utwardzenie: biblioteka parsuje pliki z niezaufanych zrodel
# CFLAGS i LDFLAGS   flagi dystrybucji (makepkg, dpkg); _FORTIFY_SOURCE przychodzi z nich
# -lpthread -lm -ldl  wymagane przez miniaudio
# shellcheck disable=SC2086
"${CC}" ${CFLAGS:-} -shared -fPIC -O2 -fvisibility=hidden -fstack-protector-strong \
    -o "${OUT_DIR}/libcewka_audio.so" \
    "${SCRIPT_DIR}/cewka_audio.c" \
    ${LDFLAGS:-} -Wl,-z,relro,-z,now \
    -lpthread -lm -ldl

echo "[ok] Zbudowano: ${OUT_DIR}/libcewka_audio.so"
