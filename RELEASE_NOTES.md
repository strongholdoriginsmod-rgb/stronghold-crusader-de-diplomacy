# Änderungen im regulären Entwicklungsstand

- Der reguläre Build benötigt APIShared 0.44.0. Diplomatie erhält
  Eingaben für Truppenbefehle über den gemeinsamen Broker und veröffentlicht
  geschützte Spielerpaare für den gemeinsamen nativen Zielwahlfilter.
- Ein Angriffsklick auf einen Vertragspartner kann die Waffenruhe beenden
  und den passenden Originalbefehl auslösen. Aktive Waffenruhen werden beim
  Laden eines Spielstands wiederhergestellt.
- Im lokalen Gefecht leitet Diplomatie „Verteidigen“ aus dem
  Verbündetenfenster einmal an Vanilla weiter. Die KI entscheidet weiterhin,
  ob sie den Auftrag annimmt. Das Rückmeldefenster folgt der Simulationsuhr
  der aktuellen Mission.
- Diplomatie beobachtet den normalen Einzelspieler-Save über APIShared.
  Speicherformat und vorhandene Save-Schutzprüfungen bleiben erhalten.
- Der Build erzeugt die Versionsanforderung im Plugin und das passende
  Manifest aus derselben Abhängigkeitsangabe.

Die gezielten Einzelspielerprüfungen dieses Stands stehen in
[VALIDIERUNG.md](VALIDIERUNG.md#regulärer-entwicklungsstand-mit-apishared-0440).

# Diplomatie 2.3.11-preview.1

Einzelspieler-Vorabversion für Stronghold Crusader Definitive Edition, Stand
8. November 2026. Die Mod-DLL ist byteidentisch mit dem geprüften lokalen
Spielbuild 2.3.11. Änderungen an der Verpackung erteilen keine neue native
oder Mehrspielerfreigabe.

## Enthaltene Funktionen

- Diplomatie im vorhandenen Spielmenü mit Fürstenauswahl, Detailansicht,
  Bündnisübersicht und Handelsfenster.
- Beziehungen mit Vertrauen, Dankbarkeit, Respekt, Groll, Zuverlässigkeit und
  gespeicherter Vorgeschichte; unterschiedliche Fürstenprofile.
- Verhandlungen über Waffenruhe, Handelsabkommen und Bündnisse, mehrteilige
  Warenforderungen, Fristen, Bündnispflichten und Wiedergutmachung.
- Lehnsverhältnisse mit benanntem Lehnsherrn und Vasallen, Tribut,
  Zahlungsfristen, Rückständen, Freilassung und Gleichstellungsregeln.
- Warenhandel mit 19 Warenarten und 38 originalen Warensymbolen. Angebote,
  Gegenangebote, Annahme, Ablehnung, Rückzug, eigene Mengen und drei
  Angebotsstufen. Bestände und Lagerkapazitäten werden tatsächlich geprüft.
- Wirtschaftlich begrenzte KI-Handelsinitiative und automatische Warenhilfe
  zwischen geeigneten verbündeten KI-Fürsten.
- Registrierung manueller Warenanfragen; Sperre nicht zuordenbarer
  Originalanfragen im bestätigten Einzelspieler vor dem ursprünglichen Effekt.
- Fürstenchat im vorhandenen HUD mit Direktadressen und bestätigten
  Teamempfängern. Antworten beruhen auf lokalen Regeln und Diplomatieereignissen.
- Erfassung manueller Verbündetenbefehle und gespeicherte Mod-Zustände
  einschließlich Handelsverlauf, Anfragen und Hilfszusagen.

## Tatsächliche Spielnachweise

Mit der enthaltenen DLL lieferte KI 2 drei Einheiten Obst an KI 3. Die echten
Bestände änderten sich von 77/30 auf 74/33. Beim Originalspeichern und beim
ersten Lesen im neuen Prozess waren sie 74/32; nach weiteren 350 Originalticks
73/32. Der authentifizierte Verlauf blieb erhalten, ohne erneute Mod-Buchung.

Der vollständige Warenhandelslauf des unmittelbaren Vorgängers änderte vier
Bestände von 89/0/187/31 auf 84/5/183/35. Nach vollständigem Prozessende und
Neustart waren diese Bestände unverändert; es gab keine erneute Mod-Buchung.
Dieser Handelslauf wurde mit der aktuellen DLL noch nicht vollständig wiederholt.

Der Fürstenchat wurde an das Original-HUD gebunden; 35 isolierte Fälle prüfen
Adressierung, Teams, ausgeschiedene Fürsten und Antworten aus dem Verlauf.
11 isolierte Fälle prüfen die Regeln für nicht zugeordnete Warenanfragen.
Beide Build-Profile haben null Fehler und null Warnungen.

## Grenzen dieser Vorabversion

- Der Starter aktiviert Warenhandel und KI-Warenhilfe nur innerhalb der
  isolierten, verifizierten Einzelspielerpartie. Vor der ersten Warenbuchung
  ist ein bestätigter Originalspielstand der aktuellen Partie erforderlich.
- Der autonome KI-Militärsender ist beim normalen Start ausgeschaltet.
  Sein Prüfpfad ist angebunden; ein erfolgreicher autonomer Versand mit
  Speichern und Kaltladen ist noch nicht nachgewiesen.
- Die vollständige Ersetzung sämtlicher automatischer Original-KI-Anfragen
  und die Zuordnung aller Rückmeldungen und Folgen sind noch offen.
- Der zusätzliche native Waffenruhe-Zielwahlfilter blieb im aktuellen Spiel
  wegen einer abweichenden Engine-Prüfsumme deaktiviert. Eine vollständige
  Durchsetzung aller Kampf- und Bewegungswege ist nicht abgenommen.
- Sämtliche Vertrags-, Lehns-, Tribut- und Langzeitverläufe sind noch nicht
  zusammenhängend im aktuellen Build abgenommen.
- Nativer Mehrspielerhandel ist nicht freigegeben. Eine Partie mit zwei
  tatsächlichen Steam-Teilnehmern wurde noch nicht geprüft.
- Diese Vorabversion erfordert die exakt gepinnten Entwicklungsabhängigkeiten.
  Eine beliebige Installation mit derselben angezeigten Versionsnummer genügt
  nicht. Bei Abweichungen bricht die Einrichtung vor dem Kopieren ab.

## Installation

1. `Stronghold-Crusader-Diplomatie-2.3.11-preview.1-Windows.zip` außerhalb der
   vorhandenen Spielinstallation entpacken. Python 3.11 oder neuer wird benötigt.
2. Eine eigene kompatible Spielinstallation mit SHCDE-SE 2.14.3,
   APIShared 0.25.0 im gepinnten Entwicklungsstand und Bugfixes and QoL 1.0.192
   bereitstellen. `references/preview-runtime.json` enthält die exakten
   Dateiprüfsummen. Spiel und Abhängigkeiten sind nicht im Download enthalten.
3. Im entpackten Ordner ausführen:

   ```powershell
   py -3 -B tools/preview.py prepare --game-source "<eigener kompatibler Spielordner>"
   ```

   Die Einrichtung liest den angegebenen Spielordner und erstellt eine eigene
   Kopie unter `runtime/game`. Sie übernimmt weder vorhandene Spielstände noch
   persönliche Einstellungen oder zusätzliche Mods.
4. `Diplomatie starten.cmd` verwenden und ein lokales Gefecht starten.
   Spielstände dieser Kopie liegen unter `runtime/profile`.

Der Quellcode ist separat als `Stronghold-Crusader-Diplomatie-2.3.11-preview.1-Source.zip`
verfügbar. `SHA256SUMS.txt` enthält die Prüfsummen beider Archive.

## Abhängigkeiten und Rechte

SHCDE-SE, APIShared, Bugfixes and QoL sowie sämtliche Originalspiel- und
Engine-Dateien werden nicht mitverteilt. Die XAML-Patches verweisen auf
Spielressourcen; sie enthalten keine exportierten Originalgrafiken.

APIShared: https://github.com/SHCDE-APIShared/APIShared

Bugfixes and QoL: https://github.com/Serpens66/Stronghold-Crusader-DE-Mods

Für den Mod-Quellcode wird mit dieser Vorabversion keine zusätzliche
Open-Source-Lizenz erteilt. Rechte an Drittkomponenten verbleiben bei deren
jeweiligen Rechteinhabern.
