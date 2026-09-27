# Sample Log Generator

A fake e-commerce backend that produces realistic, correlated logs and metrics in real time. It injects known incident
scenarios and records the ground truth for each one. It is the data source for the **Claude-Powered Incident
Investigator** practice project (Claude Developer Foundations prep).

Claude is not involved here. The generator creates incidents from known scenarios, so you can later compare Claude's
diagnosis against the ground truth:

```
Incident scenario ──► correlated logs + metrics ──► (your) Claude investigator ──► diagnosis
        │                                                                              │
        └──────────────── ground truth (incidents.jsonl) ◄──── compare in `incident eval` ┘
```

## Simulated system

| Service | Role |
|---|---|
| CatalogService | Category listings, search and product pages |
| CartService | Adds, removes and shows cart items |
| OrderService | Receives checkouts, creates and confirms orders, calls the other services |
| AuthService | Signs users in and out, validates access tokens |
| InventoryService | Reserves and releases stock |
| PaymentService | Charges through an external gateway and stores transactions in `payments-db` |
| NotificationService | Sends confirmation emails |

Each service runs as 2–3 named hosts. Every request gets a `correlationId` that follows it across services; each
service hop has its own `requestId`. The `orderId` appears once OrderService has created the order.

### Concurrent users

Traffic comes from a population of **30–100 concurrent visitors** (configurable). A new target size within the range
is picked every 2–6 minutes: visitors arrive, spread out, to fill the gap and leave on their own, so the live count
drifts up and down. About 45% of visitors sign in on arrival; guests sign in when they check out.

Each visitor is a session that moves between actions with 2–12 s of think time:

```
browse category ─┬─► view product ─┬─► add to cart ─┬─► checkout ──► leave
search ──────────┘        ▲        │                ├─► view cart ─► checkout / remove item
                          └────────┘ (keep browsing) └─► leave (cart abandoned)
```

Sessions last 4–30 actions, and about a quarter of them end in a checkout (≈15–20 checkouts a minute at the default
range). Every log line from a visitor carries their `sessionId` and, once signed in, their `userId`, so you can
follow one person's journey.

All sessions run on one simulation clock instead of OS threads. Their requests interleave in the logs exactly like
parallel traffic (40–50 different sessions log in a typical 10-second window), while seeded runs stay reproducible
and timestamps stay ordered. Incidents act on checkouts; browsing, search and cart traffic keep flowing around them,
as they would in production.

Healthy traffic includes realistic noise: card declines, out-of-stock items, email bounces, occasional slow gateway
responses and slow queries. Not every warning means an incident.

## Running it

### Live mode (real time)

```bash
dotnet run
```

- 30–100 simulated visitors browse, search, fill carts and check out concurrently (see above).
- An incident is injected automatically every 2–5 minutes and lasts 1–3 minutes (configurable).
- Lines are printed to the console with colour by level, and appended to:
  - `logs/logs.jsonl`: one log entry per line
  - `logs/metrics.jsonl`: per-service metrics every 10 s
  - `logs/incidents.jsonl`: ground truth, written when each incident ends

API at `http://localhost:5225` (see `Sample-log-generator.http`):

| Endpoint | Purpose |
|---|---|
| `GET /status` | Running state, seed, active and target users, counters, active incident |
| `POST /generator/start`, `/generator/stop` | Pause or resume |
| `GET /logs?service=&level=&minLevel=&orderId=&correlationId=&q=&from=&to=&take=` | Search logs; returns the latest `take` matches (default 200) |
| `GET /logs/stream?…same filters…` | Live tail as Server-Sent Events |
| `GET /metrics?service=&metric=&from=&to=&take=` | Search metrics |
| `GET /scenarios` | List injectable scenarios |
| `POST /incidents/{scenario}?durationSeconds=90&distractors=2` | Inject an incident now |
| `POST /incidents/resolve` | End the active incident early |
| `GET /incidents` | Ground truth for all incidents |
| `POST /datasets` | Generate an offline dataset (see below) |

### Dataset mode (instant, reproducible)

This mode simulates a time window without waiting and writes a self-contained dataset. It is ideal for
evaluation runs and for the context/token experiments in Stage 5.

```bash
dotnet run -- dataset --minutes 120 --incidents 20 --users 30-100 --seed 42 --name eval-20 --prompt-injection
```

Output goes to `datasets/eval-20/` as `logs.jsonl`, `metrics.jsonl`, `incidents.jsonl` and `manifest.json`. The
same seed and options always give byte-identical files. Re-running with the same name overwrites the dataset.
Rough size: with 30–100 users, 30 minutes is about 15–20k log lines.

Options: `--minutes`, `--incidents`, `--users 30-100` (a range, or one number for a fixed population), `--seed`, `--name`,
`--scenarios PaymentGatewayTimeout,NetworkTimeout`, `--prompt-injection`, `--distractors N`.

## Log format

```json
{"timestamp":"2026-09-26T16:54:48.218Z","level":"Error","service":"PaymentService","host":"payment-svc-6a1e7-2",
 "environment":"Production","eventId":4103,"message":"Payment gateway request timed out for order ORD-169208",
 "correlationId":"8c1d…","requestId":"req-4be0f2a91c3d","orderId":"ORD-169208","durationMs":30000,
 "exception":"System.TimeoutException: The operation has timed out after 30000 ms.\n   at …"}
```

Timestamps are UTC with fixed millisecond precision, so they sort correctly as strings. Null fields are omitted.
`eventId` is stable per message type; see `Simulation/Catalog.cs` (1xxx Order, 2xxx Auth, 3xxx Inventory,
4xxx Payment, 5xxx Notification).

Metrics per service: `event_count`, `error_count`, `error_rate_percent`, `latency_p95_ms`, `cpu_percent`,
`memory_mb`, plus `db_connections_active`, `db_connection_wait_ms`, `payment_gateway_latency_p95_ms` (Payment),
`healthy_instances` (Inventory) and `upstream_payment_latency_p95_ms` (Order). Error rate and latency are
computed from the emitted logs, so logs and metrics always agree.

## Incident scenarios

| Scenario | Level | What the logs show | Key evidence |
|---|---|---|---|
| `PaymentGatewayTimeout` | 1 Direct | Gateway latency climbs; calls hit the 30 s timeout, retry, and fail | 4101 latency high, 4103 TimeoutException, 1006 PaymentFailed |
| `InventoryServiceUnavailable` | 1 Direct | Memory nears the limit on each instance, then OutOfMemoryException crash loop; 503s; circuit breaker opens | 3100 memory high, 3101 OOM, 3103 health check 503, 1101 circuit opened |
| `DatabaseConnectionPoolExhausted` | 2 Correlated | A PaymentService deploy, then pool usage climbing on every instance, connections never returned, pool exhausted, payments fail | 4000 deploy, 4201/4202 pool usage, 4205 connection held, 4203 pool timeout |
| `AuthenticationFailure` | 3 Distributed | Key rotation; auth-1 refreshes its cache, auth-2/3 still cache the old kid (last refresh 45+ min) and fail with IDX10503 → 401 | 2101 rotation, 2105 per-host cache state, 2102 IDX10503 on auth-2/3 only, 1105 401s |
| `NetworkTimeout` | 3 Distributed | Only order-2/3 report packet loss and connect timeouts to PaymentService; order-1 is fine; PaymentService never sees the failed requests | 1201 degraded upstream (per host), 1202 SocketException, missing PaymentService logs |
| `PromptInjectionAttempt` | 5 Noisy/misleading | **Security test, not an outage.** Customer notes containing prompt-injection text show up in logs | 5101 template warning with the payload |

### Cascading failures

These four scenarios start with one fault that spreads hop by hop while the incident runs. Each moves through stages
at fixed points in the incident's duration. When a stage begins, its defining event is logged first, and only then do
requests show its effects, so cause always comes before effect in the logs. If an incident is resolved early, the
cascade stops at whatever stage it had reached.

| Scenario | Chain (root cause first) | What makes it hard |
|---|---|---|
| `GatewaySlowdownCascade` | gateway latency ↑ → each payment holds a DB connection while waiting → pool usage climbs → OrderService's 10 s calls time out, orders fail (some payments still authorize afterwards, needing refunds) → pool exhausted → every payment fails | Looks like `DatabaseConnectionPoolExhausted`, but gateway latency comes first, there is no deploy, and connections are held only as long as a gateway call takes |
| `InventoryLockRetryStorm` | stock recount job locks `stock_levels` → reservations wait on the lock → OrderService's 5 s timeouts → 3× retries (the timed-out work still finishes) → request surge → thread pool starvation → health checks time out → 503 / open circuit | The loudest symptoms look like a capacity problem or a crash; the origin is a lock held by a batch job |
| `NotificationBackpressure` | SMTP relay times out → all email workers blocked → `order-events` backlog grows → broker memory alarm blocks publishers → OrderService can't publish OrderConfirmed → paid orders stuck in PaymentCaptured | Back-pressure travels upstream: the errors are in OrderService, but the root cause is at the end of the flow |
| `AuthCacheFailoverCascade` | Redis failover → cold revocation cache, lookups go to auth-db → token validation slows → OrderService's 3 s timeouts → retries double the load → AuthService sheds load with 429 → checkouts rejected | Retries amplify the problem; the 429s look like a capacity or rate-limit issue |

All cascades are level 3 (Distributed) before distractors. They reuse the same timeout, retry and circuit-breaker
patterns as real systems, including work that finishes after the caller gave up: a late `Reserved`/`validated`
line, or a payment authorized after its order already failed.

`PromptInjectionAttempt` is off by default for automatic injection. Enable it with
`"IncludePromptInjection": true`, `--prompt-injection`, or `POST /incidents/PromptInjectionAttempt`.

### Logs show evidence, never the conclusion

Each scenario class holds its root cause, expected evidence and remediation as **evaluation-only ground truth**.
Emitted log lines show only what a real system would log: symptoms, state and events. No message says "stale
cache", "connection leak" or "rolled back". For example, the database fix shows up only as a redeploy of the previous
version and a healthy pool, and the auth failure has to be inferred from per-host cache state. Background noise
that would contradict an incident's evidence is suppressed while it runs (for example, generic "JWKS refreshed"
lines during `AuthenticationFailure`).

Partial failures stay partial, and some hosts keep working. That split is part of the evidence.

### Difficulty levels and distractors

| Level | Meaning |
|---|---|
| 1 Direct | One failing component, visible in its own logs |
| 2 Correlated | A cause in one place surfaces as failures elsewhere |
| 3 Distributed | Partial failure across hosts/services; the pattern must be pieced together |
| 4 Competing hypotheses | 1–2 distractors: plausible, unrelated events that must be ruled out |
| 5 Noisy/misleading | 3+ distractors, or untrusted/misleading content in the logs |

Distractors are emitted early in the incident window. They include an unrelated NotificationService deploy, a
GC pause, a feature-flag rollout, a TLS-certificate expiry warning, a client rate limit and a slow-query burst.
A distractor never reuses an event id from the scenario's real evidence. Turn them on with
`"DistractorsPerIncident": 2`, `--distractors 2`, or `POST /incidents/{scenario}?distractors=2`.

### Ground truth record

Each record in `incidents.jsonl` contains:
- the scenario and its difficulty;
- start and end times;
- the root-cause service and root cause;
- the affected services;
- the expected evidence (service + eventId). Only evidence that actually occurred is listed: with light traffic,
  a short stage may see no checkouts, and that link is then left out rather than expected;
- the causal chain: the propagation path in cause-to-effect order, root cause first (the root is always the first
  link to appear in the logs; later links can overlap in time);
- the distractors (service + eventId): blaming one of these is a wrong answer;
- the remediation;
- all affected order and correlation ids.

## Configuration (`appsettings.json` → `LogGenerator`)

| Key | Default | Notes |
|---|---|---|
| `AutoStart` | `true` | Start live generation on launch |
| `MinConcurrentUsers` / `MaxConcurrentUsers` | `30` / `100` | Range for the number of concurrent visitors |
| `AutoIncidents` | `true` | Inject incidents automatically |
| `Min/MaxSecondsBetweenIncidents` | `120` / `300` | Quiet time between incidents |
| `Min/MaxIncidentDurationSeconds` | `60` / `180` | |
| `EnabledScenarios` | `[]` (all outages) | Limit auto-injected scenarios |
| `DistractorsPerIncident` | `0` | Decoy events per incident (raises difficulty to 4/5) |
| `IncludePromptInjection` | `false` | |
| `Seed` | `null` | Set for reproducible live runs |
| `MetricsIntervalSeconds` | `10` | |
| `EchoToConsole` | `true` | |
| `OutputDirectory` / `DatasetsDirectory` | `logs` / `datasets` | |

Any key can also be overridden with an environment variable, for example `LogGenerator__MaxConcurrentUsers=200`.

## How this feeds the investigator project

- **Stage 3 tools:** `search_logs(query)` → `GET /logs?q=` (or read the JSONL directly);
  `get_service_logs(service, start, end)` → `GET /logs?service=&from=&to=`;
  `get_metrics(service, metric, start, end)` → `GET /metrics?…`.
- **Stage 4 security:** use `PromptInjectionAttempt`. The investigator must treat the payload as data.
- **Stage 5 context:** a 2-hour dataset has 100k+ lines, far too many to send in full, so retrieval is required.
- **Stage 6 evaluation:** generate a seeded dataset with 20 incidents, run the investigator per incident, and score
  its answers against `incidents.jsonl`. Score:
  - root cause (service);
  - affected services;
  - evidence cited (event ids);
  - whether it traced the causal chain back to its origin or stopped at the loudest symptom;
  - remediation;
  - whether it blamed a distractor.

  Report the results per difficulty level.

Keep `/incidents`, `/scenarios` and `incidents.jsonl` away from the investigator's tools. They are the answer key.

## Project layout

```
Program.cs                    host setup + `dataset` CLI switch
Configuration/                LogGeneratorOptions
Models/                       LogEntry, MetricSample, IncidentRecord, IncidentScenario
Simulation/LogGenerator.cs    clock-driven simulation engine (live and dataset modes share it)
Simulation/CommonFlows.cs     the healthy checkout path + business noise
Simulation/Scenarios/         one class per incident scenario
Simulation/MetricsAggregator  metrics derived from logs + scenario gauges
Output/                       JSONL writer/reader, JSON settings, query filters
Hosting/                      background ticker, SSE broadcaster, dataset generator/CLI
Endpoints/                    minimal API
```
