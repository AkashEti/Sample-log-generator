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
| OrderService | Receives checkouts, creates and confirms orders, calls the other services |
| AuthService | Validates access tokens |
| InventoryService | Reserves and releases stock |
| PaymentService | Charges through an external gateway and stores transactions in `payments-db` |
| NotificationService | Sends confirmation emails |

Each service runs as 2–3 named hosts. Every checkout gets a `correlationId` that follows it across services; each
service hop has its own `requestId`. The `orderId` appears once OrderService has created the order.

Healthy traffic includes realistic noise: card declines, out-of-stock items, email bounces, occasional slow gateway
responses and slow queries. Not every warning means an incident.

## Running it

### Live mode (real time)

```bash
dotnet run
```

- Checkouts arrive as a Poisson process at about `OrdersPerSecond`, following a ±30% daily curve.
- An incident is injected automatically every 2–5 minutes and lasts 1–3 minutes (configurable).
- Lines are printed to the console with colour by level, and appended to:
  - `logs/logs.jsonl`: one log entry per line
  - `logs/metrics.jsonl`: per-service metrics every 10 s
  - `logs/incidents.jsonl`: ground truth, written when each incident ends

API at `http://localhost:5225` (see `Sample-log-generator.http`):

| Endpoint | Purpose |
|---|---|
| `GET /status` | Running state, seed, counters, active incident |
| `POST /generator/start`, `/generator/stop` | Pause or resume |
| `GET /logs?service=&level=&minLevel=&orderId=&correlationId=&q=&from=&to=&take=` | Search logs; returns the latest `take` matches (default 200) |
| `GET /logs/stream?…same filters…` | Live tail as Server-Sent Events |
| `GET /metrics?service=&metric=&from=&to=&take=` | Search metrics |
| `GET /scenarios` | List injectable scenarios |
| `POST /incidents/{scenario}?durationSeconds=90` | Inject an incident now |
| `POST /incidents/resolve` | End the active incident early |
| `GET /incidents` | Ground truth for all incidents |
| `POST /datasets` | Generate an offline dataset (see below) |

### Dataset mode (instant, reproducible)

This mode simulates a time window without waiting and writes a self-contained dataset. It is ideal for
evaluation runs and for the context/token experiments in Stage 5.

```bash
dotnet run -- dataset --minutes 120 --incidents 20 --rate 1 --seed 42 --name eval-20 --prompt-injection
```

Output goes to `datasets/eval-20/` as `logs.jsonl`, `metrics.jsonl`, `incidents.jsonl` and `manifest.json`. The
same seed and options always give byte-identical files. Re-running with the same name overwrites the dataset.
Rough size: at `--rate 2`, 30 minutes is about 37k log lines.

Options: `--minutes`, `--incidents`, `--rate` (orders/s), `--seed`, `--name`,
`--scenarios PaymentGatewayTimeout,NetworkTimeout`, `--prompt-injection`.

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

| Scenario | What happens | Key evidence |
|---|---|---|
| `PaymentGatewayTimeout` | External gateway slows down; calls hit the 30 s timeout, retry, and fail | 4101 latency high, 4103 TimeoutException, 1006 PaymentFailed |
| `DatabaseConnectionPoolExhausted` | PaymentService v2.14.0 deploy leaks connections; pool climbs to 100/100 | 4000 deploy, 4201/4202 pool usage, 4203 pool timeout |
| `InventoryServiceUnavailable` | Instances crash-loop with OutOfMemoryException; 503s; OrderService circuit breaker opens | 3101 OOM, 3103 health check 503, 1101 circuit opened |
| `AuthenticationFailure` | After key rotation, 2 of 3 AuthService hosts have a stale key cache and reject tokens | 2101 rotation, 2102 IDX10503 on specific hosts, 1105 401s |
| `NetworkTimeout` | Packet loss between OrderService and PaymentService; PaymentService never sees the requests | 1201 degraded upstream, 1202 SocketException, missing PaymentService logs |
| `PromptInjectionAttempt` | **Security test, not an outage.** Customer notes containing prompt-injection text show up in logs | 5101 template warning with the payload |

`PromptInjectionAttempt` is off by default for automatic injection. Enable it with
`"IncludePromptInjection": true`, `--prompt-injection`, or `POST /incidents/PromptInjectionAttempt`.

Each ground-truth record (`incidents.jsonl`) contains the scenario, start and end times, root-cause service, root
cause, affected services, expected evidence (service + eventId), remediation, and all affected order and
correlation ids.

## Configuration (`appsettings.json` → `LogGenerator`)

| Key | Default | Notes |
|---|---|---|
| `AutoStart` | `true` | Start live generation on launch |
| `OrdersPerSecond` | `2` | Average checkout rate |
| `AutoIncidents` | `true` | Inject incidents automatically |
| `Min/MaxSecondsBetweenIncidents` | `120` / `300` | Quiet time between incidents |
| `Min/MaxIncidentDurationSeconds` | `60` / `180` | |
| `EnabledScenarios` | `[]` (all outages) | Limit auto-injected scenarios |
| `IncludePromptInjection` | `false` | |
| `Seed` | `null` | Set for reproducible live runs |
| `MetricsIntervalSeconds` | `10` | |
| `EchoToConsole` | `true` | |
| `OutputDirectory` / `DatasetsDirectory` | `logs` / `datasets` | |

Any key can also be overridden with an environment variable, for example `LogGenerator__OrdersPerSecond=5`.

## How this feeds the investigator project

- **Stage 3 tools:** `search_logs(query)` → `GET /logs?q=` (or read the JSONL directly);
  `get_service_logs(service, start, end)` → `GET /logs?service=&from=&to=`;
  `get_metrics(service, metric, start, end)` → `GET /metrics?…`.
- **Stage 4 security:** use `PromptInjectionAttempt`. The investigator must treat the payload as data.
- **Stage 5 context:** a 2-hour dataset has 100k+ lines, far too many to send in full, so retrieval is required.
- **Stage 6 evaluation:** generate a seeded dataset with 20 incidents, run the investigator per incident, and score
  its answers against `incidents.jsonl` (root-cause service, evidence event ids, affected orders).

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
