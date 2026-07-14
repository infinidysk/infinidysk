# Konzept: Bandbreitenbegrenzung für NzbDav

Status: Entwurf zur Diskussion
Bezug: Nutzeranfrage — Ping-/Packet-Loss-Probleme auf der Heimleitung, wenn NzbDav parallel zu anderer Nutzung (Streaming durch Mitbewohner) Daten von Usenet-Providern lädt.

## 1. Problem

NzbDav lädt Artikel (Segmente) von Usenet-Providern nach, sobald:

1. eine NZB in die Queue eingespielt wird (initialer Import),
2. eine Datei per WebDAV gestreamt wird (Sonarr/Radarr-Client, Plex/Jellyfin o.ä. über Rclone-Mount),
3. ein periodischer Health-Check ein Release überprüft.

Aktuell existiert **keinerlei Byte-Raten-Begrenzung** im Code (siehe Abschnitt 3.6). Die einzigen vorhandenen "Limits" sind:

- `usenet.max-download-connections` — eine Obergrenze für die Anzahl **gleichzeitiger** BODY/ARTICLE-Verbindungen (kein Byte/s-Limit).
- `usenet.streaming-priority` — eine Odds-basierte Priorisierung, welche Anfrage (Streaming vs. Queue) bei Kontention eher einen freien Verbindungs-Slot bekommt.

Wenn viele Verbindungen gleichzeitig mit voller Providergeschwindigkeit Daten ziehen, sättigt das die Downstream-Bandbreite der Heimleitung. Bei hoher Grundlatenz (im vorliegenden Fall ~500ms) führt das nicht nur zu einem eigenen Geschwindigkeitsproblem, sondern durch Bufferbloat zu **Ping-Anstieg und Packet-Loss für alle anderen Nutzer** im selben Netz — das eigentlich gemeldete Symptom.

## 2. Rahmenbedingungen (aus Rückfragen mit dem Nutzer geklärt)

| Frage | Entscheidung |
|---|---|
| Limitiert werden soll... | **nur der Download von den Usenet-Providern** (nicht die lokale Auslieferung per WebDAV im LAN — dort ist i.d.R. nicht die Engstelle) |
| Zeitsteuerung | **kein Zeitplan** — ein statischer Wert, jederzeit ohne Neustart änderbar |
| Struktur | **Ein Gesamtlimit, mit optionalen Unterlimits** pro Kategorie (Idee 3 des Nutzers) |
| Eingabeform | **Fester Wert in Mbit/s** (keine "% meiner Leitung"-Eingabe) |
| Scope | **Global über alle Provider** (nicht pro Provider einzeln) |
| Laufzeit | **Sofort wirksam**, kein Neustart nötig |

Diese Entscheidungen fließen direkt in den Vorschlag in Abschnitt 4 ein.

## 3. Bestandsaufnahme der aktuellen Architektur

### 3.1 Layered NNTP-Client-Stack

`backend/Clients/Usenet/UsenetStreamingClient.cs` baut pro Konfigurationsänderung folgende Kette auf:

```
BaseNntpClient                 (1 Instanz pro physischer TCP-Verbindung; wrappt UsenetSharp)
  → MultiConnectionNntpClient  (Connection-Pool pro Provider)
    → MultiProviderNntpClient  (Failover über alle konfigurierten Provider)
      → DownloadingNntpClient  (globales "max-download-connections"-Gate + Streaming/Queue-Priorität)
        → [ArticleCachingNntpClient]  (pro Queue-Item, Disk-Cache, nur im Queue-Pfad)
          → UsenetStreamingClient  (Singleton, von WebDAV-Reads und Queue gleichermaßen genutzt)
```

`DownloadingNntpClient` (`backend/Clients/Usenet/DownloadingNntpClient.cs:15`) ist der **einzige Punkt im gesamten Stack**, der BODY/ARTICLE-Anfragen aus Queue- und Streaming-Pfad gemeinsam gated — sowohl für Verbindungs-Slots (`PrioritizedSemaphore`) als auch für die Priorisierung. STAT/HEAD-Kommandos (Health-Checks) laufen **nicht** durch dieses Gate, sondern werden von der Basisklasse direkt durchgereicht.

### 3.2 Priorisierung: `PrioritizedSemaphore` als Vorbild

`backend/Clients/Usenet/Concurrency/PrioritizedSemaphore.cs` implementiert bereits genau das Muster, das wir für eine Bandbreitenaufteilung wiederverwenden können:

- zwei Warteschlangen (High = Streaming, Low = Queue/Hintergrund),
- bei Konkurrenz wird "gewürfelt" nach konfigurierbaren Odds (`usenet.streaming-priority`, Default 80 %), damit die Low-Priority-Queue nie komplett verhungert,
- `UpdateMaxAllowed`/`UpdatePriorityOdds` erlauben Live-Rekonfiguration ohne Neustart — das Muster für "sofort wirksame Änderung" ist im Code bereits etabliert.

Welche Priorität eine Anfrage hat, wird **nicht als Parameter durchgereicht**, sondern ambient über den `CancellationToken` transportiert: `BaseStoreStreamFile.GetReadableStreamAsync` (WebDAV-GET-Handler) setzt `DownloadPriorityContext { Priority = High }` auf den Token; fehlt der Kontext (Queue-Pfad), gilt implizit `Low`. Dieser Mechanismus ist bereits exakt das, was wir bräuchten, um Lese-Bytes einer "Streaming"- oder "Queue"-Kategorie zuzuordnen — ohne dass Aufrufer geändert werden müssen.

### 3.3 Wo Bytes tatsächlich vom Netz gelesen werden

`BaseNntpClient.DecodedBodyAsync`/`DecodedArticleAsync` (`backend/Clients/Usenet/BaseNntpClient.cs:88-138`) senden das BODY/ARTICLE-Kommando, lesen die Response-Header, und geben dann **sofort** einen `YencStream` zurück, der **lazy** on-demand aus dem zugrunde liegenden Netzwerk-Stream dekodiert (CLAUDE.md: "decodes yEnc on the fly"). D.h.: Die eigentlichen Artikel-Bytes werden erst gelesen, wenn ein Aufrufer `Stream.ReadAsync(...)` auf diesem Objekt aufruft — nicht schon beim Senden des Kommandos.

Zwei Konsument:innen dieses Streams existieren, mit unterschiedlichem Nebenläufigkeitsverhalten:

- **`UnbufferedMultiSegmentStream`** (`backend/Streams/UnbufferedMultiSegmentStream.cs`) — für den Queue-/Import-Pfad (`articleBufferSize: 0`). Strikt sequenziell: ein Segment nach dem anderen, keine Lookahead-Parallelität.
- **`MultiSegmentStream`** (`backend/Streams/MultiSegmentStream.cs`) — für den Streaming-Pfad. Ein Hintergrund-Loop (`DownloadSegments`, Zeile 47) holt sich für **bis zu `usenet.article-buffer-size` (Default 40)** kommende Segmente jeweils vorab eine "exklusive Verbindung" (`AcquireExclusiveConnectionAsync`) und sendet das BODY-Kommando — der zurückgegebene Stream wird aber erst gelesen, wenn er in `ReadAsync` (Zeile 87) "an der Reihe" ist.

### 3.4 Wichtige, nicht-offensichtliche Erkenntnis: Warum ein reines "Lesetempo drosseln" nicht reicht

`AcquireExclusiveConnectionAsync` (aufgerufen von `MultiSegmentStream.DownloadSegments`, Zeile 56) geht über `DownloadingNntpClient.AcquireExclusiveConnectionAsync` (`DownloadingNntpClient.cs:97-102`) und **belegt einen Slot der `maxDownloadConnections`-Semaphore für die gesamte Dauer der Artikel-Übertragung** — freigegeben wird er erst über den `onConnectionReadyAgain`-Callback, wenn die Verbindung wieder für ein neues Kommando bereit ist (d.h. nachdem der Body vollständig gelesen/verworfen wurde).

Das bedeutet: Der reale Parallelitätsgrad beim Streaming ist `min(article-buffer-size, max-download-connections)` — bei Standardwerten (40 bzw. ~15–20) also bis zu ~15–20 **gleichzeitig geöffnete** BODY-Downloads. Da Artikel typischerweise nur wenige hundert KB bis ~1 MB groß sind, und TCP den kompletten Artikel unabhängig vom App-seitigen `Read()`-Aufruf in den Kernel-Empfangspuffer schaufeln kann (besonders bei hoher Latenz, wo TCP-Fenster tendenziell größer autoskaliert werden — genau das Szenario mit ~500ms Ping!), **kann bereits ein einziger Wett-Lauf mehrerer gleichzeitig offener Verbindungen einen Burst von mehreren MB erzeugen, bevor eine reine "drossle das `Stream.ReadAsync()`"-Logik überhaupt greifen könnte.**

**Konsequenz für das Design:** Eine Drosselung, die nur den *Lesekonsum* des gerade aktiven Streams begrenzt, reicht für den Streaming-Pfad (mit Lookahead) *allein nicht zuverlässig aus*, um Bufferbloat zu verhindern — sie hilft aber sehr wohl für den Queue-Pfad (streng sequenziell, keine Parallelität). Für den Streaming-Pfad muss die Drosselung zusätzlich am **Start neuer Segment-Downloads** ansetzen (Admission Control), nicht nur am Lesetempo. Details dazu in Abschnitt 4.3.

### 3.5 Health-Checks sind bandbreitentechnisch vernachlässigbar

`HealthCheckService.PerformHealthCheck` (`backend/Services/HealthCheckService.cs:109-165`) nutzt ausschließlich `StatAsync`/`HeadAsync` (STAT/HEAD-Kommandos) — es werden **keine Artikel-Bodies heruntergeladen**. Ein eventueller Re-Download bei fehlenden Segmenten geschieht extern über Sonarr/Radarr, nicht in NzbDav selbst. Health-Checks müssen daher **nicht** von einem Bandbreitenlimit erfasst werden; sie belasten die Leitung ohnehin kaum.

→ Von den drei in der Aufgabenstellung genannten "Downloadarten" bleiben effektiv **zwei** bandbreitenrelevant: **Initialer NZB-Import (Queue)** und **Streaming (WebDAV-Reads)**.

### 3.6 Bestätigt: keine vorhandene Rate-Limiting-Infrastruktur

Eine Volltextsuche nach `bandwidth|throttle|ratelimit|tokenbucket|kbps|mbps` im gesamten Backend/Frontend ergab keine Treffer außer beschreibendem Hilfetext in der UI (`webdav.tsx`). Alles Bestehende ist verbindungs- oder fehlerbasiert (`ConnectionPool`, `PrioritizedSemaphore`, `ProviderCircuitBreaker`), nichts ist byteraten-basiert. Das ist ein Neubau, kein Ausbau einer bestehenden (deaktivierten) Funktion.

### 3.7 Konfigurations- und UI-Muster

- Backend: flache Key-Value-Konfiguration (`ConfigManager`, `backend/Config/ConfigManager.cs`), Werte als String in SQLite, mit typisierten Gettern (`GetMaxDownloadConnections()`, `GetStreamingPriority()`) und Default-Fallback inline. Änderungen über `UpdateConfigController` lösen `OnConfigChanged`-Event aus, auf das z.B. `DownloadingNntpClient` bereits reagiert (`DownloadingNntpClient.cs:29-42`) — **das ist exakt der Mechanismus für "sofort wirksam ohne Neustart"**, den wir brauchen, und er ist im Code bereits etabliert.
- Frontend: `frontend/app/routes/settings/webdav/webdav.tsx` enthält bereits die drei nächstverwandten Einstellungen (`max-download-connections`, `streaming-priority` als Prozent-Input, `article-buffer-size`) als einfache `Form.Control`/`InputGroup`-Elemente mit Validierungsfunktion. Das ist die direkte Vorlage für eine neue Einstellung.
- Es existiert bereits Live-Telemetrie über WebSocket (`WebsocketTopic.UsenetConnections`, `ConnectionPoolStats.cs`) für die aktuelle Verbindungsauslastung — ein guter Präzedenzfall, um später auch die aktuelle Downloadrate live in der UI anzuzeigen (siehe Abschnitt 6, "nice to have").

## 4. Bewertung der Nutzer-Ideen

**Idee 1 (ein fixes Gesamtlimit für alles):** Einfachste UX, aber vermischt Health-Checks (vernachlässigbar) mit den zwei tatsächlich relevanten Kategorien. Da die bestehende Prioritätslogik (`streaming-priority`) Streaming ohnehin gegenüber der Queue bevorzugt, wäre das der pragmatischste MVP-Schnitt.

**Idee 2 (getrennte feste Limits pro Kategorie):** Nutzer müsste selbst "richtig" aufteilen (z.B. wenn er gerade nicht streamt, bleibt das Queue-Limit ungenutzt liegen — verschwendete Kapazität). Höchster Konfigurationsaufwand, geringster Komfort.

**Idee 3 (Kombi: Gesamtlimit + anteilige Unterlimits):** Das vom Nutzer selbst bevorzugte Modell — und es lässt sich fast 1:1 auf das bestehende `PrioritizedSemaphore`-Muster (Odds-basierte Aufteilung mit Vermeidung von Verhungern) abbilden. Deshalb im Folgenden als Basis für den eigenen Vorschlag übernommen.

## 5. Vorschlag (Idee 4)

### 5.1 Grundmodell: ein globaler Byte-Token-Bucket mit zwei Teil-Budgets

Ein einziger konfigurierbarer Gesamtwert `usenet.bandwidth-limit-mbps` (Mbit/s, 0 bzw. leer = kein Limit) deckelt die **Summe aus Streaming- und Queue-Downloads über alle Provider**. Analog zu `usenet.streaming-priority` gibt es einen optionalen zweiten Wert `usenet.bandwidth-streaming-reserve` (Prozent, Default z. B. 80 — bewusst identisch zum bestehenden `streaming-priority`-Default, um das mentale Modell nicht unnötig zu verdoppeln), der beschreibt, welcher Anteil des Gesamtlimits für Streaming reserviert ist, wenn beide Kategorien gleichzeitig aktiv sind. Ungenutzte Kapazität einer Kategorie darf die andere mitnutzen ("Borrowing") — die Reservierung ist also kein hartes Sub-Limit, sondern nur eine Gewichtung bei Konkurrenz, exakt wie beim bestehenden Odds-Mechanismus in `PrioritizedSemaphore`.

Das deckt Idee 3 ab, ohne dass der Nutzer im Normalfall mehr als **einen** Wert eintragen muss (das zweite Feld ist "erweiterte Einstellung", optional, mit sinnvollem Default).

### 5.2 Technischer Ansatzpunkt: `DownloadingNntpClient`

`DownloadingNntpClient` ist bereits der einzige gemeinsame Choke-Point für Streaming- und Queue-BODY/ARTICLE-Reads, kennt bereits die Priorität über `DownloadPriorityContext`, und reagiert bereits auf `OnConfigChanged`. Der neue Bandbreiten-Limiter sollte dort als weiterer, dem bestehenden `_semaphore.WaitAsync(...)` gleichgestellter Check ergänzt werden — konkret in `AcquireExclusiveConnectionAsync` (Zeile 97-102), bevor das BODY/ARTICLE-Kommando abgeschickt wird. Das bedeutet:

- Kein Eingriff in `BaseNntpClient`, `MultiConnectionNntpClient`, `MultiProviderNntpClient` oder die beiden `MultiSegmentStream`-Varianten nötig.
- Beide Downloadarten (Queue über `UnbufferedMultiSegmentStream`, Streaming über `MultiSegmentStream`) profitieren automatisch, da beide über `DecodedBodyAsync`/`AcquireExclusiveConnectionAsync` dieser Klasse laufen.
- Health-Checks (STAT/HEAD) durchlaufen diesen Pfad nicht — bleiben also, wie gewünscht, unbelastet vom Limit.

### 5.3 Admission Control statt reinem Lese-Throttle

Aus dem in 3.4 beschriebenen Grund (Lookahead-Parallelität kann Bufferbloat verursachen, bevor ein reiner Lese-Throttle greift) sollte der Limiter **beim Start eines neuen Segment-Downloads** ansetzen, nicht (nur) beim Auslesen: Vor dem eigentlichen `BODY`-Kommando wird geprüft/gewartet, bis genug Budget im Token-Bucket vorhanden ist, und dieses Budget wird sofort (optimistisch, anhand eines gleitenden Durchschnitts vergangener Artikelgrößen) abgebucht; nach Abschluss des Downloads wird die Differenz zur tatsächlichen Größe korrigiert ("Credit-Back"). Das begrenzt direkt, wie viele Segmente pro Zeiteinheit *neu gestartet* werden dürfen — und damit die tatsächliche Auslastung der Leitung — unabhängig davon, wie groß `article-buffer-size` konfiguriert ist.

Ergänzend (nicht als Ersatz) kann zusätzlich das reine Lesetempo pro Stream gedrosselt werden, um auch sehr große Einzelartikel gleichmäßig statt in einem Schub auszuliefern. Dieser zweite Mechanismus ist aber der weniger kritische Teil und kann in einer späteren Phase ergänzt werden.

**Diese Erkenntnis sollte vor der Implementierung mit einem kleinen Prototyp empirisch verifiziert werden** (siehe Umsetzungsplan, Phase 1) — die tatsächliche Auswirkung von TCP-Fenstergrößen/Bufferbloat auf einer 500ms-Leitung lässt sich nur begrenzt am Schreibtisch vorhersagen.

### 5.4 UI

Neues Feld in der bestehenden `WebdavSettings`-Tab (oder alternativ eigener "Bandwidth"-Tab, falls das Team die Tab-Liste nicht weiter überladen möchte):

- `Bandwidth Limit` — Freitextfeld in Mbit/s, leer/0 = deaktiviert (analog zu den bestehenden Zahlenfeldern).
- Ausgeklappt unter "Advanced" (optional, mit Default): `Streaming Reserve %` — exakt im Stil des bestehenden `streaming-priority`-Feldes (InputGroup mit `%`-Suffix).

Das folgt 1:1 dem in `webdav.tsx` etablierten Muster (`Form.Group` + `Form.Control`/`InputGroup` + Validator + Hilfetext + Einbindung in `isWebdavSettingsUpdated`/`isWebdavSettingsValid`).

**Nice-to-have (spätere Phase):** aktuelle Downloadrate live anzeigen (z. B. als kleiner Graph/Zahl neben dem Limit-Feld), analog zur bestehenden Live-Verbindungsanzeige über `WebsocketTopic.UsenetConnections`. Hilft dem Nutzer, das richtige Limit empirisch zu finden, ohne den Router/eigene Messtools bemühen zu müssen.

## 6. Offene Punkte / Risiken

1. **Empirische Verifikation nötig** (siehe 5.3): Wirkt Admission-Control-Throttling auf `DownloadingNntpClient`-Ebene tatsächlich spürbar auf Ping/Packet-Loss? Sollte früh in der Umsetzung mit einem Wegwerf-Prototyp gegen die echte Leitung des Nutzers getestet werden, bevor die "schöne" Lösung gebaut wird.
2. **Einheit Mbit/s → Bytes/s:** Empfehlung, dezimal wie ISPs zu rechnen (1 Mbit/s = 125.000 Byte/s), da das dem mentalen Modell der meisten Nutzer (Router-Anzeige, Speedtest) entspricht.
3. **Konvertierungsverlust bei sehr niedrigen Limits:** Bei sehr kleinen Werten (z. B. 1 Mbit/s) könnte die "Admission Control" pro Segment zu granular/unruhig werden (viele kleine Wartezyklen). Sollte beim Prototyp beobachtet werden; ggf. Mindestwert/Warnhinweis in der UI.
4. **`UsenetSharp` ist eine externe NuGet-Abhängigkeit** — der rohe Socket-Layer ist nicht Teil dieses Repos und kann nicht verändert werden. Der Limiter muss oberhalb von `BaseNntpClient`/`YencStream` ansetzen; das ist mit dem Vorschlag in 5.2 der Fall.
5. **Zusammenspiel mit `usenet.max-download-connections`:** Beide Regler (Verbindungsanzahl und Bandbreite) bleiben unabhängig konfigurierbar. Es ist möglich, dass ein Nutzer beide "falsch" kombiniert (z. B. viele Verbindungen + niedriges Bandbreitenlimit) — das ist funktional unproblematisch (die Bandbreite bleibt gedeckelt), sollte aber in der Hilfetext-UI kurz erwähnt werden, um Verwirrung zu vermeiden.

## 7. Offene Rückfragen an den Nutzer — geklärt

- **Streaming-Reserve-Regler in Phase 1?** Nein — MVP liefert bewusst nur ein einzelnes Gesamtlimit (Idee 1). Die anteilige Odds-Aufteilung (Idee 3 / Phase 2) wird nur bei Bedarf nachgezogen.
- **Einheit im Freitextfeld?** Nur Mbit/s. Als Hilfestellung verlinkt das Feld auf speedtest.net und nennt die Umrechnung "1 MB/s = 8 Mbit/s" im Hilfetext, statt ein zusätzliches Umrechnungs-Tool zu verlinken.

→ Umsetzung von Phase 1 ist erfolgt, siehe [umsetzungsplan.md](./umsetzungsplan.md).
