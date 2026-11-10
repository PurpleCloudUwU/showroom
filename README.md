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

## Updates

Die App aktualisiert sich selbst aus den Releases von <https://github.com/PurpleCloudUwU/showroom>.
Unter Einstellungen → Updates:

- **Nach Updates suchen**: bei jedem Start, täglich (Standard), wöchentlich, monatlich oder nie. „Jetzt suchen“ geht immer.
- **Updates automatisch installieren** (Standard: an): Eine neue Version wird im Hintergrund geladen und beim Schließen
  der App installiert. Aus = die App meldet die neue Version nur, geladen und installiert wird auf Knopfdruck.

Ablauf: Die App fragt `releases/latest` ab (ohne Angaben zum Rechner oder Nutzer) und vergleicht das Tag (`v1.2.0`) mit
der eigenen Version. Die `RetroShowroom.exe` des Releases wird nach `%LOCALAPPDATA%\RetroShowroom\Updates\<Version>` geladen
und gegen Größe und SHA-256 aus der GitHub-Antwort geprüft. Zum Installieren startet die App die neue .exe mit
`--update-anwenden`: Sie wartet ohne Fenster, bis die alte App beendet ist, ersetzt deren Datei und startet sie auf
Wunsch neu. Ein laufendes Rendern wird nie unterbrochen. Was der Installer tut, steht in `update.log` im Datenordner.
Liegt die App in einem Ordner ohne Schreibrechte, verweist sie stattdessen auf die Release-Seite.

**Neue Version veröffentlichen:**

1. `<Version>` in `src/RetroShowroom/RetroShowroom.csproj` erhöhen (z. B. `1.2.0`).
2. Bauen (siehe unten).
3. Auf GitHub ein Release mit dem Tag `v1.2.0` anlegen und `dist\RetroShowroom\RetroShowroom.exe` anhängen.
   Die Datei muss `RetroShowroom.exe` heißen, Entwürfe und Vorabversionen werden übersehen.

Version 1.0.0 hat noch keinen Updater, von dort muss einmal von Hand auf eine neuere Version gewechselt werden.

## Bauen

Voraussetzung: .NET SDK 9.

```
dotnet publish src\RetroShowroom\RetroShowroom.csproj -c Release -o dist\RetroShowroom
```

Ergebnis ist eine einzige Datei, `dist\RetroShowroom\RetroShowroom.exe` (rund 68 MB). Sie ist eigenständig, es muss
kein .NET- oder Windows-App-SDK-Runtime installiert sein. Beim ersten Start (und nach jedem Update) entpackt .NET die
App nach `%TEMP%\.net\RetroShowroom`. Das dauert einmalig etwa 10 Sekunden, danach startet die App in einer halben Sekunde.
Nach einem Update räumt die App die entpackten Teile älterer Versionen dort selbst weg. Zwischendateien liegen in
`%TEMP%\RetroShowroomBuild`, weil der XAML-Compiler keine langen Pfade kann.

Vom Windows App SDK sind nur die benötigten Teile eingebunden (WinUI, Foundation, InteractiveExperiences, DWrite,
Runtime, Base). Das Sammelpaket `Microsoft.WindowsAppSDK` brächte zusätzlich KI-, Such- und Widget-Bibliotheken mit
(onnxruntime, DirectML …), das machte die .exe rund 24 MB größer.

## Geschwindigkeit

- Die Einrichtungsprüfung startet Blender und dauert 3 bis 7 Sekunden. Ihr Ergebnis wird gemerkt (`einrichtung.json`),
  damit „Rendern starten“ nach dem Start der App sofort bereit ist. Die echte Prüfung läuft trotzdem jedes Mal im
  Hintergrund. Der gemerkte Stand gilt nur für dieselbe `blender.exe` (Pfad, Größe, Änderungsdatum).
- Textur-Umwandlung: BC7 wird auf allen Kernen dekodiert, die PNG in Bändern von 1 MB parallel komprimiert.
  `CoreCheck --bench` misst das mit erzeugten Testdaten und liest jede geschriebene PNG zur Kontrolle zurück.
- Die Blender-Ausgabe wird nur aufbereitet, solange sie aufgeklappt ist. Einstellungen werden gesammelt und nicht
  im UI-Thread geschrieben.

## Projekte

- `src/RetroShowroom` – WinUI-3-Oberfläche (Rendern, Einrichtung, Einstellungen)
- `src/RetroShowroom.Core` – YTD-Dekoder, Blender-Aufruf, Einrichtungsprüfung, GPU-Skript
- `tools/CoreCheck` – Prüfprogramm für den Kern:
  - `CoreCheck <ydd> <ytd> <ordner>` und `CoreCheck --gpu-test <ydd> <ytd> <ordner>`
  - `CoreCheck --update-test` prüft den Updater gegen einen Testserver auf localhost (ohne Internet)
  - `CoreCheck --update-live` fragt das echte Release bei GitHub ab (lädt nichts herunter)
  - `CoreCheck --bench [größe]` misst die Textur-Umwandlung
