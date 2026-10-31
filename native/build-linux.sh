#!/usr/bin/env bash
# Buduje libcewka_audio.so dla linux-x64.
# Wymaga wyłącznie kompilatora C (gcc lub clang).
#
# Nagłówki ALSA, PulseAudio i PipeWire nie są potrzebne: miniaudio samo deklaruje ich symbole
# i ładuje biblioteki przez dlopen w czasie działania, a zaplecze wybiera przy otwarciu urządzenia.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="${SCRIPT_DIR}/../src/Cewka.Audio/runtimes/linux-x64/native"

CC="${CC:-gcc}"
if ! command -v "${CC}" >/dev/null 2>&1; then
    echo "[blad] Nie odnaleziono kompilatora '${CC}'. Ustaw zmienna CC lub zainstaluj gcc." >&2
    exit 1
fi

mkdir -p "${OUT_DIR}"

# -fPIC          kod niezależny od położenia, wymagany dla bibliotek współdzielonych
# -O2            optymalizacja
# -fvisibility   domyślnie ukryte symbole; eksportowane są tylko oznaczone w kodzie
# -fstack-protector-strong  kontrola przepełnienia stosu bez względu na domyślne ustawienia
#                kompilatora w dystrybucji; biblioteka dekoduje niezaufane pliki
# -z relro,now,noexecstack  tablice przemieszczeń tylko do odczytu, stos bez prawa wykonania
# CFLAGS i LDFLAGS   flagi dystrybucji (makepkg, dpkg); _FORTIFY_SOURCE przychodzi z nich
# -lpthread -lm -ldl  wymagane przez miniaudio
# shellcheck disable=SC2086
"${CC}" ${CFLAGS:-} -shared -fPIC -O2 -fvisibility=hidden -fstack-protector-strong \
    -o "${OUT_DIR}/libcewka_audio.so" \
    "${SCRIPT_DIR}/cewka_audio.c" \
    ${LDFLAGS:-} -Wl,-z,relro,-z,now,-z,noexecstack \
    -lpthread -lm -ldl

echo "[ok] Zbudowano: ${OUT_DIR}/libcewka_audio.so"
