# Entwickeln und beitragen

Der Zweig `main` enthält den vollständigen Quellstand der Vorabversion
2.3.11-preview.1. Die C#-Dateien, Oberflächen-Patches und Build-Werkzeuge
stimmen mit dem veröffentlichten Source-Archiv überein. README und diese
Entwickleranleitung ergänzen den direkten Einstieg in das Repository.

## Orientierung

| Verzeichnis | Inhalt |
| --- | --- |
| `src/Diplomacy`, `src/Ui` | Beziehungen, Verträge, Anfragen, Diplomatiefenster und Fürstenchat |
| `src/Trade`, `src/Economy`, `src/Aid` | Warenhandel, Bedarfsplanung und KI-Warenhilfe |
| `src/Engine`, `src/Host`, `src/World` | Spielschnittstellen, Rollen und Autorität |
| `src/Military` | Menschliche Verbündetenbefehle und begrenzter autonomer Prüfpfad |
| `src/Persistence`, `src/Networking` | Speichern, Wiederherstellen und Zustimmung |
| `src/Testing`, `src/Qa`, `src/Diagnostics` | Codegesteuerte Prüfung und Diagnose |
| `assets` | Mod-Metadaten, Konfiguration und XAML-Patches |
| `references` | Abhängigkeitsprüfsummen; keine fremden Binärdateien |
| `evidence` | Veröffentlichbare Zusammenfassungen vorhandener Spielnachweise |

## Lokale Entwicklungsumgebung

Python 3.11 oder neuer, ein passendes .NET-SDK, .NET-Framework-4.8.1-
Referenzassemblies und eine eigene kompatible Spielinstallation werden benötigt.
Die exakten Abhängigkeiten sind in `references/qa.json`, `references/release.json`
und `references/preview-runtime.json` festgehalten. APIShared muss zum gewählten
Build-Profil passen; die private Spielintegration verwendet den gepinnten
Entwicklungsstand.

```powershell
py -3 -B tools/local_game.py prepare --help
py -3 -B build.py --help
```

`prepare` verlangt ausdrücklich die Vorlage der Spielkopie, die gepinnte
APIShared-Entwicklungsfassung, das SDK und die Framework-Referenzen. Die
vorhandene Spielinstallation wird dabei nur gelesen. Der frühere Parameter --public-api entfällt beim QA-Treiber. Das öffentliche SDK wird direkt an build.py übergeben. Nach Einrichtung der
getrennten Kopie erfolgt der Build mit:

```powershell
py -3 -B tools/local_game.py index
py -3 -B tools/local_game.py build
py -3 -B tools/local_game.py install
py -3 -B tools/local_game.py check
```

`source-index.json` legt die ausgewählten C#-Dateien und deren Prüfsummen fest.
Nach Änderungen wird der Index vor dem Build erneuert. `build.py` baut mit
expliziten Abhängigkeiten und installiert oder startet selbst kein Spiel.

Der lokale Spieltreiber baut und installiert die gepinnte QA-Variante. Für den
regulären Build `build.py --profile release --build` mit dem öffentlichen
APIShared-SDK und einer expliziten Abhängigkeitssperre ausführen. Die Sperre
enthält die verifizierten Prüfsummen aller Referenzen einschließlich der
ausgewählten APIShared-DLL. `references/release.json` dokumentiert den
ursprünglichen Stand und enthält den Hash des früheren APIShared-SDKs.
Für eine isolierte Prüfung die reguläre DLL mit dem erzeugten `info.json`
und den bestehenden `assets/Patches` separat installieren.
Die APIShared-Abhängigkeit wird über das eigene Paket bereitgestellt.

Der reguläre Build benötigt APIShared 0.44.0 und nutzt dessen öffentliche
Broker für HUD, Spielende, Missionswechsel und Eingaben für Truppenbefehle.
Diplomatie veröffentlicht geschützte Spielerpaare als Regeln für die gemeinsame
native Zielwahl.
Die Rückmeldefrist für Verbündetenbefehle folgt der Simulationsuhr der
aktuellen Mission. Im lokalen Gefecht bindet Diplomatie „Verteidigen“ an den
aktuellen Verbündeten-HUD-Klick. Der originale HUD-Pfad bleibt erhalten;
seinen Auftrag sendet Diplomatie nach den Prüfungen genau einmal über den
gemeinsamen GameAction-Validator. Die Weiterleitung setzt das geprüfte Spiel-
und Extender-Paar voraus. Andere Befehle behalten ihren bisherigen Pfad.
GameAction-Regeln, die erst nach der Zurückstellung an die Reihe kämen,
sehen die Wiederholung auf dem Simulationspfad. Ein Veto dort verhindert
den Originalaufruf.

Der passive Nachweis eines lokalen Spielstands verwendet `ManagedSaveEvents`.
Er ordnet den unveränderten SaveData-Callback dem ursprünglichen Aufruf,
Zielpfad und der aktuellen Mission zu. Die getrennte Save-Schutzbrücke
bleibt für aktive Schutzpfade bestehen.

Der reguläre Build erzeugt das Paketmanifest im Ausgabeordner aus
`assets/release/info.json`. Verwende beim Paketieren das erzeugte Manifest,
damit Plugin und Paket dieselbe APIShared-Anforderung angeben.

## Änderungen prüfen

Spielprüfungen erfolgen headless und per Code. `tools/acceptance.py --help`
beschreibt die vorhandenen Abläufe mit getrennten Originalprozessen. Bei
Warenbuchungen gehören reale Bestände, Originalspeichern, vollständiges
Prozessende, Kaltladen und der Ausschluss einer erneuten Buchung zum Nachweis.

Eine bestandene statische Prüfung ersetzt keinen Spielnachweis. Normale native
Freigaben und Mehrspielerautorität dürfen nicht durch UI-Zustimmung, Testflags
oder erfundene Spielerrollen ersetzt werden. Der autonome KI-Militärsender
bleibt beim normalen Start ausgeschaltet. Offene Punkte stehen in
[RELEASE_NOTES.md](RELEASE_NOTES.md) und [VALIDIERUNG.md](VALIDIERUNG.md).

Spielinstallationen, Spielstände, private Profile, Laufzeitprotokolle,
Zugangsdaten und absolute persönliche Pfade gehören nicht in Beiträge.
`runtime`, `build`, Python-Caches und lokale Build-Dateien sind entsprechend
von der veröffentlichten Quellstruktur getrennt.
