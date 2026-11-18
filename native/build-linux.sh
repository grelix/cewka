#!/usr/bin/env bash
# Buduje libcewka_audio.so dla linux-x64.
# Wymaga wyłącznie kompilatora C (gcc lub clang).
#
# Nagłówki ALSA i PulseAudio nie są potrzebne: miniaudio samo deklaruje ich symbole
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
# -DNO_TAGS      WavPack bez odczytu znaczników (czyta je warstwa zarządzana); bez ENABLE_DSD,
#                ENABLE_LEGACY, ENABLE_THREADS i OPT_ASM_*: tylko dekodowanie PCM wersji 4 i 5 w C
# -lpthread -lm -ldl  wymagane przez miniaudio
WV="${SCRIPT_DIR}/wavpack/src"
# shellcheck disable=SC2086
"${CC}" ${CFLAGS:-} -shared -fPIC -O2 -fvisibility=hidden -fstack-protector-strong \
    -DNO_TAGS -I"${SCRIPT_DIR}/wavpack/include" \
    -o "${OUT_DIR}/libcewka_audio.so" \
    "${SCRIPT_DIR}/cewka_audio.c" \
    "${WV}/common_utils.c" "${WV}/decorr_utils.c" "${WV}/entropy_utils.c" "${WV}/open_utils.c" \
    "${WV}/read_words.c" "${WV}/unpack.c" "${WV}/unpack_floats.c" "${WV}/unpack_seek.c" "${WV}/unpack_utils.c" \
    ${LDFLAGS:-} -Wl,-z,relro,-z,now,-z,noexecstack \
    -lpthread -lm -ldl

echo "[ok] Zbudowano: ${OUT_DIR}/libcewka_audio.so"
