# Obrazy odniesienia

Pięć zrzutów interfejsu, z którymi porównywany jest wygląd przy każdej zmianie w gałęzi głównej.
Porównanie uruchamia narzędzie zrzutów:

```bash
dotnet run --project tools/Cewka.Snapshots -- artifacts/zrzuty utwor.wav drugi.wav --porownaj tests/odniesienie
```

Obrazy pokrywają układ okna głównego w obu motywach, stan zwinięty oraz dwie najbogatsze
zakładki ustawień. Nie ma tu kompletu kilkudziesięciu zrzutów, które narzędzie potrafi
wyrenderować — komplet rósłby w historii repozytorium przy każdej zmianie wyglądu, a te pięć
wystarcza, żeby przesunięcie układu nie przeszło niezauważone.

## Skąd się biorą

Pliku odniesienia nie da się przygotować na dowolnej maszynie. Rasteryzacja tekstu w Linuksie
różni się od tej w Windowsie na tyle, że obraz zrobiony na jednym systemie nie zgadza się
z obrazem z drugiego. Obrazy powstają więc na maszynie budującej w chmurze i tam też są
porównywane.

Gdy pliku brakuje, przebieg go tworzy, mówi o tym wprost i **nie** kończy się porażką — inaczej
pierwsze uruchomienie zawsze byłoby czerwone. Nowe obrazy trafiają wtedy do artefaktu
`odniesienie`, skąd należy je pobrać, obejrzeć i dodać do repozytorium. Dopóki tego nikt nie
zrobi, porównanie niczego nie pilnuje.

## Gdy wygląd zmienia się celowo

Zmiana wyglądu zatrzyma budowę — i tak ma być. Należy wtedy:

1. obejrzeć obraz różnicowy z artefaktu `zrzuty` (różnice zaznaczone na czerwono),
2. upewnić się, że zmiana jest tą zamierzoną,
3. zastąpić pliki w tym katalogu nowymi, z artefaktu `odniesienie`.

## Tolerancja

Dwa przebiegi na tym samym kodzie dają dziś obrazy identyczne co do piksela. Porównanie
dopuszcza mimo to 200 różniących się pikseli skupionych w prostokącie o boku najwyżej 24 —
jest to zapas na maszynę w chmurze, której obraz systemu bywa podmieniany, a nie na cokolwiek,
co widać. Przesunięcie układu daje różnicę rozlaną po całym oknie i przekracza oba progi naraz.
