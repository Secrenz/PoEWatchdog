# PoE Watchdog

Kleines Windows-Tool, das per Ping überwacht, ob ein Gerät an einem PoE-Port erreichbar ist – und wenn nicht, den Port an einem Huawei S5731-S automatisch per SSH aus- und wieder einschaltet. Kein Rumlaufen mehr zum Schaltschrank, nur weil eine Kamera oder ein AP mal wieder hängt.

Ich hatte das Problem, dass bei uns regelmäßig ein paar PoE-Geräte (Kameras, APs) einfach eingefroren sind und nur ein Stromreset geholfen hat. Anstatt jedes Mal ins Web-UI vom Switch zu gehen und den Port manuell zu togglen, macht das jetzt dieses Tool im Hintergrund.

## Was es macht

- Pingt eine oder mehrere IPs in einstellbarem Intervall
- Wenn eine IP nach X Fehlversuchen in Folge nicht mehr antwortet, wird der zugehörige PoE-Port am Switch per SSH kurz aus- und wieder eingeschaltet
- Danach wartet es eine Weile ab, ob das Gerät wieder online kommt, und loggt das entsprechend
- Läuft dauerhaft im Tray, mehrere Überwachungen gleichzeitig sind möglich (verschiedene Switches/Ports)
- Es gibt eine Obergrenze für Resets pro Überwachung (Standard 5), damit es sich nicht in einer Endlosschleife aus Resets aufhängt, falls das Gerät wirklich kaputt ist – dann wird die Überwachung gestoppt und man bekommt eine Windows-Benachrichtigung
- Zusätzlich gibt's Buttons für manuelle Aktionen (Port aus/an, Status abfragen) für den Fall, dass man selbst eingreifen will
- Alles wird geloggt, sowohl im UI als auch als Datei auf der Platte (mit automatischer Rotation nach Größe und automatischem Löschen alter Logs)
- Autostart mit Windows ist optional, dann startet es minimiert im Tray und die konfigurierten Überwachungen laufen automatisch an

Aktuell ist die SSH-Befehlssequenz fest auf Huawei VRP (S5731-S) zugeschnitten. Für andere Switch-Hersteller müsste man die Kommandos in `PoEReset` anpassen.

## Voraussetzungen

- Windows (getestet unter Windows 10/11)
- .NET 8 Runtime, falls man die Build ohne Self-Contained-Publish nutzt. Die fertige Release-exe bringt alles selbst mit, dafür braucht man dann nichts extra zu installieren
- SSH-Zugang auf den Switch mit einem Benutzer, der PoE-Ports schalten darf

## Build

Projekt ist ein normales .NET-8-WinForms-Projekt, kein extra `Program.cs`, der Einstiegspunkt sitzt direkt in `MainForm.cs`.

```
dotnet build
```

Für eine einzelne, portable exe (Single-File, self-contained):

```
dotnet publish -c Release
```

Das Ergebnis liegt danach unter `bin\Release\net8.0-windows\win-x64\publish\`. Das ist die Datei, die man einfach so verteilen/kopieren kann, ohne dass auf dem Zielrechner .NET installiert sein muss.

## Einrichtung

1. Programm starten, über "Hinzufügen" eine neue Überwachung anlegen
2. Folgende Angaben pro Überwachung:
   - **Monitor-IP**: die IP, die gepingt werden soll (meistens das Gerät selbst)
   - **Switch-IP**: die Management-IP des Huawei-Switches
   - **Benutzer / Passwort**: SSH-Login für den Switch
   - **PoE-Port**: der Port, der bei Ausfall geschaltet wird (z. B. `0/0/1`)
   - Intervall, Anzahl Fehlversuche vor Reset, Timeout nach Reset, maximale Resets – alles einstellbar
3. Passwort kann optional gespeichert werden (wird über Windows DPAPI verschlüsselt abgelegt, landet also nicht im Klartext in der settings.json)
4. Überwachung starten – entweder einzeln oder alle zusammen

Einstellungen und Logs landen unter `%AppData%\PoEWatchdog`.

## Autostart

Über die Checkbox in der App wird ein Registry-Eintrag unter `HKCU\...\Run` gesetzt. Beim Systemstart öffnet sich das Programm dann direkt minimiert im Tray, ohne dass man es manuell starten muss – und je nach Einstellung starten die Überwachungen auch gleich automatisch mit.

## Warum kein direkter API-Aufruf statt SSH?

Weil die S5731-S in unserem Fall nur klassisches CLI über SSH als praktikablen Zugriffsweg bietet und ich keine Lust hatte, mich mit NETCONF für diesen einen Use-Case auseinanderzusetzen. SSH mit einer durchgehenden Shell-Session reicht hier völlig aus – wichtig ist nur, dass `system-view`, `interface` und `shutdown`/`undo shutdown` in derselben Session laufen, sonst verliert der Switch den Kontext zwischen den Befehlen.

## Bekannte Einschränkungen

- Nur für Huawei VRP-Switches getestet, andere Hersteller brauchen angepasste Befehle
- Windows-only (WinForms)
- Kein Multi-User-Konzept, das Tool ist für den Betrieb durch eine Person/ein Team auf einem Rechner gedacht

## Lizenz

Nutzung auf eigene Gefahr. Falls es jemandem hilft – gerne forken und anpassen.
