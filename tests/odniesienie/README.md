# Obrazy odniesienia

Pięć zrzutów, z którymi narzędzie `tools/Cewka.Snapshots` porównuje wygląd programu. Listę
porównywanych obrazów trzyma `Odniesienie.Sprawdzane` w `tools/Cewka.Snapshots/Odniesienie.cs`.

| Plik | Zawartość |
|------|-----------|
| `ciemny-1180.png` | okno główne 1200 × 680, motyw ciemny, korektor i kolejka widoczne |
| `jasny-1180.png` | to samo w motywie jasnym |
| `ciemny-zwiniety.png` | okno 1180 × 520, motyw ciemny, korektor i kolejka schowane |
| `ustawienia-wyglad-cala.png` | ustawienia, zakładka Wygląd w całości (820 × 1000) |
| `ustawienia-jezyk.png` | ustawienia, zakładka Język (820 × 640) |

Pozostałe zrzuty, które narzędzie rysuje, służą do oglądania i nie są porównywane.

## Porównanie

```bash
dotnet run --project tools/Cewka.Snapshots -c Release -- --material artifacts/material
dotnet run --project tools/Cewka.Snapshots -c Release -- \
    artifacts/zrzuty artifacts/material/probka-dluga.wav artifacts/material/probka-krotka.wav \
    --porownaj tests/odniesienie
```

Narzędzie samo przełącza silnik na wyjście puste (`AudioDevice.UseNullOutput`), więc nic nie gra
przez głośniki i karta dźwiękowa nie jest potrzebna. W CI kroki zrzutów mają dodatkowo ustawione
`CEWKA_NULL_AUDIO=1`.

Obrazy powstają i są porównywane w CI na Linuksie (`.github/workflows/tests.yml`). Rasteryzacja
tekstu w Windowsie daje inne piksele, więc lokalne porównanie w Windowsie się nie zgodzi.

## Tolerancja

Piksel uchodzi za ten sam, gdy żaden kanał barwy nie różni się o więcej niż 8. Zrzut przechodzi,
jeśli różnych pikseli jest najwyżej 200 i mieszczą się w prostokącie o boku najwyżej 24 px.
Przy różnicy większej narzędzie zapisuje obraz różnicowy w `artifacts/zrzuty/roznice`
(różnice na czerwono na przygaszonym odniesieniu) i kończy się kodem 1.

## Brak obrazu i zmiana wyglądu

Gdy obrazu odniesienia brakuje, narzędzie zapisuje w tym katalogu bieżący zrzut. Lokalnie przebieg
kończy się wtedy powodzeniem. W CI krok „Wymagaj kompletu odniesienia” kończy go błędem, a katalog
z nowymi obrazami trafia do artefaktu `odniesienie`.

Po zamierzonej zmianie wyglądu:

1. obejrzeć obrazy różnicowe w katalogu `roznice` artefaktu `zrzuty`,
2. skopiować z tego artefaktu zmienione zrzuty o nazwach z tabeli do tego katalogu,
3. zatwierdzić je razem ze zmianą wyglądu.
