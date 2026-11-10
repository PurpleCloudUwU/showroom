# Retro Showroom

WinUI-3-App für Windows, die Showroom-Bilder „Glasvitrine“ für GTA-V-/FiveM-Kleidung rendert.
Das abgesegnete `vitrine.py` ist unverändert in die App eingebaut (`src/RetroShowroom.Core/Resources/vitrine.py`,
als Ressource in `RetroShowroom.Core.dll`). Beim Rendern wird es in den Cache geschrieben und von Blender im Hintergrund ausgeführt.
Wer das Skript ändern will, ersetzt diese Datei und baut neu.

## Ablauf

1. **Einrichtung**: Die App prüft Blender (≥ 4.2), Sollumz, die Freemode-Skelette und die Grafikkarte.
2. **Einstellungen → Assets**: Logo (`retro_logo_glas.png`) und optional ein Referenzbild hinzufügen.
   Die Dateien werden unverändert nach `%LOCALAPPDATA%\RetroShowroom\Assets` kopiert.
3. **Rendern**: `.ydd` oder `.blend` reinziehen. Die passende `.ytd` aus demselben Ordner wird automatisch
   gefunden und nach PNG umgewandelt. Geschlecht und Typ kommen aus dem Dateinamen (`mp_m_`/`mp_f_`, `lowr` = Hose).
   Titel eingeben, Qualität wählen (Final, Groß, Schnelltest), „Rendern starten“.

Fertige Bilder landen standardmäßig in `Bilder\Retro Showrooms` als `TITEL_FINAL.png`.

## Sprache

Deutsch (Standard) oder Englisch, umschaltbar unter Einstellungen → Allgemein → Sprache (wirkt nach einem Neustart).
Jeder Text steht im Code als Paar `Loc.T("Deutsch", "English")`, in XAML als `{l:Tr De='…', En='…'}`.
Beim Veröffentlichen werden alle anderen Sprachordner des Windows App SDK entfernt (nur `de-DE` und `en-us` bleiben).

## Grafikkarte

`vitrine.py` stellt fest auf CPU. Die App gibt Blender vorher ein kleines Zusatzskript mit
(`grafikkarte_verwenden.py`, wird in den Cache geschrieben). Es wählt OptiX/CUDA/HIP/oneAPI und stellt
Cycles direkt vor dem Rendern auf GPU um. Szene, Samples und Licht bleiben gleich.
Scheitert der GPU-Lauf (z. B. Grafikspeicher voll), rendert die App automatisch auf der CPU weiter.
In den Einstellungen lässt sich „Grafikkarte verwenden“ ausschalten.

## Bauen

Voraussetzung: .NET SDK 9.

```
dotnet publish src\RetroShowroom\RetroShowroom.csproj -c Release -o dist\RetroShowroom
```

Ergebnis ist eine einzige Datei, `dist\RetroShowroom\RetroShowroom.exe` (rund 92 MB). Sie ist eigenständig, es muss
kein .NET- oder Windows-App-SDK-Runtime installiert sein. Beim ersten Start (und nach jedem Update) entpackt .NET die
nativen Teile nach `%TEMP%\.net\RetroShowroom`. Das dauert einmalig etwa 20 Sekunden, danach startet die App sofort. Zwischendateien liegen in `%TEMP%\RetroShowroomBuild`, weil der
XAML-Compiler keine langen Pfade kann.

## Projekte

- `src/RetroShowroom` – WinUI-3-Oberfläche (Rendern, Einrichtung, Einstellungen)
- `src/RetroShowroom.Core` – YTD-Dekoder, Blender-Aufruf, Einrichtungsprüfung, GPU-Skript
- `tools/CoreCheck` – Prüfprogramm für den Kern (`CoreCheck --gpu-test <ydd> <ytd> <ordner>`)
