# Herkunft der Report-Fixtures

Beide JSONL-Dateien enthalten reale, am **10. September 2026** aufgezeichnete Inputreports einer GamaKay TK75 TMR unter Windows. Sie sind keine synthetischen Daten. Die Tests lesen diese versionierten Dateien; die lokal ignorierten Originalaufnahmen unter `captures/` werden nicht benötigt.

Die Bereinigung übernimmt ausschließlich `schemaVersion`, `reportId`, `reportLength`, `hex` und `elapsedMs`. Alle HEX-Strings, die Reihenfolge der Reports und die Zahlendarstellung der relativen Empfangszeiten wurden unverändert aus den Originalen übernommen. Gerätepfade, Seriennummern, Containerkennungen und andere Gerätemetadaten sind nicht enthalten. Die Originaldateien wurden nicht verändert.

| Fixture | Quelle der Rohreports | Reports | SHA-256 der bereinigten Fixture |
| --- | --- | ---: | --- |
| `outside-sandbox-w.jsonl` | Gleichnamige erste lokale Aufnahme | 1341 | `072F0B7CDDCD3FBE26E94EA2E8616E08D28B09960E196FFFA4667A7450FFF12D` |
| `live-wasd-verification.jsonl` | Gleichnamige zweite lokale Aufnahme | 3557 | `7E062314F9002B2B6E4321F554A592BB949D4A12666ED77D4E43A27BDE264F10` |

Die erste Aufnahme enthält 766 Reports für W/Index 14 mit beobachteten Rohwerten 0 bis 343. Die weiteren Indizes bleiben im Parser unbenannt.

Die zweite Aufnahme korreliert die Herstellerzuordnung von W/A/S/D mit der selbst getakteten Bedienung des Nutzers. Sie enthält W: 733 Reports und Rohwerte 0 bis 369, A: 789 Reports und 0 bis 385, S: 657 Reports und 0 bis 385, D: 970 Reports und 0 bis 385. Der letzte beobachtete Wert jeder dieser Tasten und aller weiteren enthaltenen Indizes ist null. Der ursprüngliche Abschlussdatensatz meldet 3557 geparste, keine verworfenen Reports und keine Erfassungsfehler; er ist als Metadatum nicht Teil der Report-Fixture.

Die Aufnahmen sind **keine kontrollierte Kalibrierung**. Minima und Maxima sind beobachtete Werte, keine bestätigten Ruhe-/Bottom-out-Grenzen und keine Millimeter. Die relativen Empfangszeiten beschreiben die Ankunft beim Host, nicht eine gemessene Sensor-Abtastrate. Synthetische Grenz- und Fehlerfälle sind im Testskript ausdrücklich separat markiert.
