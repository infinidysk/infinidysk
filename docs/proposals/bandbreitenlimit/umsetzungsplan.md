# Umsetzungsplan: Bandbreitenbegrenzung für NzbDav

Voraussetzung: [konzept.md](./konzept.md) — dieses Dokument bricht das Konzept in konkrete, nacheinander umsetzbare Phasen herunter. Jede Phase ist für sich lauffähig/abnahmefähig.

> **Status: Phase 1 ist umgesetzt.** Entscheidung des Nutzers: In der MVP-Phase reicht ein einzelnes Gesamtlimit (Idee 1), der optionale "Streaming Reserve %"-Regler (Idee 3 / Phase 2) wird erst bei Bedarf nachgezogen. Einheit bleibt bei Mbit/s; als Hilfestellung wurde ein Link zu speedtest.net sowie der Umrechnungshinweis "1 MB/s = 8 Mbit/s" im Hilfetext ergänzt (statt eines zusätzlichen Umrechner-Links).
>
> **Abweichung von der ursprünglichen Skizze:** Statt der in Abschnitt 5.3 des Konzepts beschriebenen "Admission Control mit Größenschätzung + Credit-Back" wurde für Phase 1 die einfachere Variante umgesetzt: ein global geteilter Token-Bucket, der beim tatsächlichen `ReadAsync` jedes Streams verbraucht wird (siehe unten). Das ist deutlich weniger Code, korrekt für die *nachhaltige* Rate, lässt aber — wie in Konzept-Abschnitt 3.4 beschrieben — theoretisch einen einmaligen, begrenzten Burst zu (in der Größenordnung `max-download-connections × durchschnittliche Artikelgröße`, bei Standardwerten grob 10-20 MB), bevor die Drosselung greift. **Update:** Im Praxistest (siehe Phase 0) hat das keine spürbaren Auswirkungen gezeigt — die Ping-/Packet-Loss-Probleme sind mit der einfachen Variante bereits verschwunden. Die Admission-Control-Variante bleibt als möglicher Folgeschritt dokumentiert, ist aber aktuell nicht priorisiert.

## Phase 0 — Validierung der Kernannahme — ✅ erledigt (positiv)

Kernfrage war: *Reicht ein reines Lesetempo-Throttling, oder braucht es zwingend Admission Control beim Start neuer Segment-Downloads?*

Statt eines separaten Wegwerf-Prototyps wurde direkt die in Phase 1 gebaute Read-Level-Lösung (Docker-Image, echter Container) gegen die reale Leitung des Nutzers getestet: Datei gestreamt bei aktivem Limit, parallel genutzt von einer zweiten Person im selben Netz. **Ergebnis: Die zuvor auftretenden Ping-Spitzen/Packet-Loss (~500ms) sind nicht mehr aufgetreten.** Der in Konzept-Abschnitt 3.4 befürchtete Burst durch das Look-Ahead-Buffering (`article-buffer-size`) scheint in der Praxis kein spürbares Problem zu sein — die einfachere Read-Level-Drosselung aus Phase 1 reicht aus. Die aufwändigere Admission-Control-Variante (ursprünglich als möglicher nächster Schritt vorgesehen) ist damit **vorerst nicht nötig** und wird nur bei zukünftigem gegenteiligem Befund nachgezogen.

**Abnahmekriterium:** erfüllt — Nutzer bestätigt, dass die Ping-/Packet-Loss-Probleme beim gleichzeitigen Streaming nicht mehr auftreten.

## Phase 1 — MVP: ein globales Gesamtlimit (Idee 1-Schnitt, ohne Unterlimits) — ✅ umgesetzt

Ziel: kleinstmögliche Änderung, die das Kernproblem löst — ein Wert in Mbit/s, sofort wirksam, ohne Streaming/Queue-Unterscheidung.

### Backend

1. **`backend/Clients/Usenet/Throttling/TokenBucket.cs`** (neu): Async-Token-Bucket.
   - Kapazität = konfiguriertes Limit in Bytes/s (`mbps * 1_000_000 / 8`, dezimal wie ISPs), Burst-Kapazität fix auf 1 Sekunde des Ziel-Durchsatzes gedeckelt.
   - `Task ConsumeAsync(int byteCount, CancellationToken ct)` — wartet (per `Task.Delay`), bis genug Tokens vorhanden sind, bucht dann ab; re-checkt nach dem Delay (mehrere gleichzeitige Konsumenten können sich gegenseitig verzögern).
   - `void UpdateRate(double bytesPerSecond)` — Live-Rekonfiguration, analog zu `PrioritizedSemaphore.UpdateMaxAllowed`.
2. **`backend/Clients/Usenet/Throttling/ThrottledYencStream.cs`** (neu): Subklasse von `UsenetSharp.Streams.YencStream` (gleiches Muster wie das bestehende `CachedYencStream`, `backend/Streams/CachedYencStream.cs`). Wrappt einen inneren `YencStream`, verbraucht bei jedem `ReadAsync` die gelesene Byte-Anzahl aus dem `TokenBucket`, bevor die Kontrolle an den Aufrufer zurückgegeben wird. `GetYencHeadersAsync`/`Dispose`/`DisposeAsync` werden 1:1 an den inneren Stream durchgereicht.
3. **`ConfigManager.GetBandwidthLimitMbps()`** (`backend/Config/ConfigManager.cs`): neuer Getter, Default `"0"` = kein Limit, Muster identisch zu `GetMaxDownloadConnections()`.
4. **`DownloadingNntpClient`** (`backend/Clients/Usenet/DownloadingNntpClient.cs`):
   - Feld `TokenBucket? _bandwidthLimiter`, `null` wenn kein Limit konfiguriert (kompletter Fast-Path ohne jeden Overhead, wenn das Feature nicht genutzt wird).
   - Private Helper `ApplyThrottle(...)` (Overloads für `UsenetDecodedBodyResponse`/`UsenetDecodedArticleResponse`) ersetzen `response.Stream` per `with`-Expression durch einen `ThrottledYencStream`, sofern ein Limit aktiv ist.
   - Angewendet in den beiden "Leaf"-Methodenpaaren, die tatsächlich `base.DecodedBodyAsync`/`base.DecodedArticleAsync` aufrufen und einen echten Stream zurückbekommen: die `(segmentId, onConnectionReadyAgain, ct)`-Overloads (Queue-Pfad über `UnbufferedMultiSegmentStream`) und die `(segmentId, exclusiveConnection, ct)`-Overloads (Streaming-Pfad über `MultiSegmentStream`). Beide Pfade sind damit automatisch abgedeckt, ohne dass `MultiSegmentStream`/`UnbufferedMultiSegmentStream` selbst angefasst werden mussten.
   - `OnConfigChanged`-Handler um `usenet.bandwidth-limit-mbps` erweitert → `_bandwidthLimiter.UpdateRate(...)` bzw. Limiter neu erstellen/entfernen, wenn der Wert die Schwelle 0/>0 wechselt.
5. **Kein DB-Migrationsschritt nötig** — Config ist eine generische Key-Value-Tabelle (`ConfigManager.LoadConfig`); der neue Key wird beim ersten Speichern über `UpdateConfigController` angelegt.

### Frontend

6. **`frontend/app/routes/settings/route.tsx`**: `usenet.bandwidth-limit-mbps: ""` zum `defaultConfig`-Objekt hinzugefügt.
7. **`frontend/app/routes/settings/webdav/webdav.tsx`**: neues `Form.Group` nach dem bestehenden `article-buffer-size`-Feld:
   - Freitext-Input mit `Mbit/s`-Suffix (`InputGroup`, wie beim `%`-Suffix bei `streaming-priority`), Placeholder `"unlimited"`.
   - Hilfetext erklärt Scope (Import + Streaming, alle Provider zusammen), die Umrechnung "1 MB/s = 8 Mbit/s", und verlinkt speedtest.net zur Ermittlung der eigenen Leitungsgeschwindigkeit.
   - Validator `isValidBandwidthLimit` (leerer String ODER endliche Zahl > 0 — bewusst Dezimalwerte erlaubt, z. B. `2.5`, anders als `isPositiveInteger` bei den übrigen Feldern).
   - Einbindung in `isWebdavSettingsUpdated`/`isWebdavSettingsValid`.

### Verifikation

8. ✅ `dotnet build` — kompiliert ohne neue Fehler/Warnungen.
9. ✅ `npm run typecheck` — läuft sauber durch (nach `npm install`, da `node_modules` im Arbeitsverzeichnis zuvor unvollständig war).
10. ✅ Docker-Image gebaut und als echter Container gegen die reale Leitung des Nutzers getestet (siehe Phase 0) — Ping-/Packet-Loss-Probleme beim gleichzeitigen Streaming sind nicht mehr aufgetreten.
11. Committed & auf `origin/main` gepusht (Commit `eb0a6ac`).

**Abnahmekriterium:** ✅ erfüllt. Ein einzelner Mbit/s-Wert in der UI deckelt die Summe aus Queue- und Streaming-Downloads, wirkt sofort, Health-Checks bleiben unbeeinflusst — und löst das ursprünglich gemeldete Ping-/Packet-Loss-Problem in der Praxis.

## Phase 2 — Unterlimits / anteilige Reservierung (Idee 3 vervollständigen)

Baut auf Phase 1 auf, nur nötig falls Nutzer nach Phase-1-Erfahrung eine feinere Steuerung wünscht.

1. `ConfigManager.GetBandwidthStreamingReserve()` — neuer Prozent-Wert, Default z. B. 80.
2. `BandwidthLimiter` um zwei "virtuelle" Teil-Budgets erweitern, die aus demselben physischen Bucket ziehen, gewichtet nach Reserve-Prozentsatz bei Konkurrenz — Odds-Mechanik strukturell analog zu `PrioritizedSemaphore` (Abschnitt 3.2 im Konzept), aber für Byte-Budget statt Slot-Anzahl.
3. Priorität pro Aufruf wird — wie beim bestehenden Slot-Semaphore — aus `DownloadPriorityContext` gelesen (`cancellationToken.GetContext<DownloadPriorityContext>()`), kein neuer Kontext-Mechanismus nötig.
4. UI: "Advanced"-Bereich unterhalb des Bandbreitenfelds mit `Streaming Reserve %`-Input, exakt im Stil von `streaming-priority`.

**Abnahmekriterium:** Bei gleichzeitigem Queue-Import und Streaming bekommt Streaming den konfigurierten Anteil bevorzugt; ungenutztes Queue-Budget wird für Streaming nutzbar (und umgekehrt), keine Verhungerung der Queue bei Dauerstreaming.

## Phase 3 — Politur (optional, nach Nutzerfeedback)

- ✅ **Live-Anzeige der aktuellen Downloadrate.** Neuer Websocket-Topic `WebsocketTopic.BandwidthUsage` ("bwu", `backend/Websocket/WebsocketTopic.cs`), einmal pro Sekunde vom neuen `DownloadingNntpClient`-Timer befüllt (`TokenBucket.TotalBytesConsumed`-Delta über die letzte Sekunde), analog zum bestehenden `ConnectionPoolStats`-Muster. Läuft nur, solange ein Limit aktiv ist. Frontend (`webdav.tsx`, `useBandwidthUsage`-Hook) zeigt "Current usage: x.x / y.y Mbit/s" direkt unter dem Eingabefeld, sobald ein Limit gesetzt ist — der Nutzer kann den Wert so empirisch justieren, ohne Router/`iftop` zu bemühen.
- ✅ **Warnhinweis bei sehr niedrigen Limits.** Nicht-blockierender Hinweistext (`isLowBandwidthLimit`, Schwelle < 2 Mbit/s) unter dem Feld, dass sehr niedrige Limits zu ungleichmäßigem Durchsatz führen können, weil der Token-Bucket häufiger pausieren muss. Keine harte Validierungsgrenze — der Nutzer kann trotzdem speichern.
- Nicht umgesetzt (weiterhin nur bei Bedarf): zusätzliches reines Lesetempo-Throttling pro Stream für sehr große Einzelartikel — laut Praxistest in Phase 0 nicht nötig, da der einfache Read-Level-Ansatz das ursprüngliche Problem bereits löst.

## Nicht in diesem Umsetzungsplan enthalten

- Zeitplan-basierte Limits (laut Nutzer explizit nicht gewünscht).
- Begrenzung der lokalen WebDAV-Auslieferung ans LAN (laut Nutzer explizit nicht gewünscht).
- Pro-Provider-Limits (laut Nutzer explizit nicht gewünscht — globales Limit über alle Provider reicht).

Falls sich diese Anforderungen später doch ergeben, betreffen sie unabhängige Erweiterungen und keinen Umbau der hier vorgeschlagenen Grundarchitektur.
