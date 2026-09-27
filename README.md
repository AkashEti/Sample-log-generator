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
| `GET /scenarios` | List injectable scenarios and their variants |
| `POST /incidents/{scenario}?durationSeconds=90&distractors=2&variant=late-authorization&profile=adversarial` | Inject an incident now. `variant` picks one (default: random), `profile` applies an evaluation bucket (see Evaluation mix) |
| `POST /incidents/resolve` | End the active incident early |
| `GET /incidents` | Ground truth for all incidents |
| `POST /datasets` | Generate an offline dataset (see below) |

### Dataset mode (instant, reproducible)

This mode simulates a time window without waiting and writes a self-contained dataset. It is ideal for
evaluation runs and for the context/token experiments in Stage 5.

```bash
dotnet run -- dataset --minutes 120 --incidents 20 --users 30-100 --seed 42 --name eval-20 --prompt-injection
```

Output goes to `datasets/eval-20/`, split so the answer key can't be handed over by accident:

| File | Contents |
|---|---|
| `input/logs.jsonl`, `input/metrics.jsonl` | The evidence: **the only files the investigator may see** |
| `ground-truth/incidents.jsonl` | Ground truth per incident |
| `ground-truth/distractors.jsonl` | Ground truth for decoys emitted outside any incident (negative controls) |
| `ground-truth/manifest.json` | Seed, options, generator version, **git commit**, schema version, validation result, and counts per scenario, variant, shape, difficulty and profile |
| `ground-truth/validation.json` | The dataset health report (see below) |

The same seed and options always give byte-identical logs. Re-running with the same name overwrites the dataset.
Rough size: with 30–100 users, 30 minutes is about 15–20k log lines; 500 incidents over 2000 minutes is about
1M lines (≈560 MB) and takes about 30 s.

Options: `--minutes` (up to 10080), `--incidents` (up to 2000, at least 3 minutes each; 4 is recommended),
`--users 30-100` (a range, or one number for a fixed population), `--seed`, `--name`,
`--scenarios PaymentGatewayTimeout,NetworkTimeout`, `--prompt-injection`, `--distractors N`,
`--distractor-kinds CompetingHypothesis,SecurityNoise`, `--control healthy|benign|noisy`, `--mix evaluation`.

Scenarios are balanced (each incident gets the least-used scenario that fits), and each incident plays a random
variant of its scenario (see Incident scenarios).

#### Evaluation mix

Without `--mix`, incidents are plain (no decoys unless `--distractors` is given): a clean baseline. With `--mix`, every
incident is planned for an evaluation bucket, which decides the variant's shape and the decoys:

```bash
dotnet run -- dataset --minutes 2000 --incidents 500 --seed 42 --name eval-500 --mix evaluation
```

| Bucket (`--mix evaluation` share) | Variant | Decoys | Resulting difficulty |
|---|---|---|---|
| `Clean` (20%) | Direct | none | 1 |
| `Correlated` (20%) | Correlated | none | 1 |
| `Cascade` (25%) | Cascade | none | 3 |
| `Noisy` (20%) | any | 3: benign noise + misleading evidence | 2 (3 if the variant is a cascade) |
| `CompetingHypotheses` (10%) | any | 2: competing hypothesis + correlated-not-causal | 4 |
| `Adversarial` (5%) | any | 3: security noise (prompt injection, credential stuffing) + competing hypothesis; marked `isSecurityTest` | 5 |

Shares are exact (largest remainder) and shuffled over time. Custom weights: `--mix clean=30,cascade=40,adversarial=10`
(they need not add up to 100). Every scenario appears in several buckets and at several difficulty levels. The bucket
is recorded as `profile` in the ground truth.

#### Validation

Every generated dataset is validated automatically; the command exits with code 2 if any check fails. You can also
re-check a dataset at any time:

```bash
dotnet run -- validate datasets/eval-20
```

| Check | What it guarantees |
|---|---|
| `all-lines-parse` | Every line of every file can be read; records from an older generator version fail here rather than being skipped |
| `unique-incident-ids` | No incident id appears twice |
| `ground-truth-not-in-logs` | No log line contains an incident id, scenario name or root-cause statement |
| `logs-sorted`, `metrics-sorted` | Timestamps never go backwards |
| `evidence-present` | Every expected evidence event exists (on the listed hosts) |
| `chain-first-seen-matches-logs` | Each causal link's recorded first appearance is a real log line |
| `chain-root-first`, `chain-order` | The root cause appears first; each link appears no earlier than the preceding non-concurrent link |
| `recovery-after-impact` | Recovery events never appear before the fix lands (`recoveryStartedAt`), do appear after it, and the fix comes after the first impact |
| `absences-hold` | Evidence by absence is really absent (e.g. PaymentService never logged the failed requests) |
| `affected-ids-present` | Every affected order and request id exists in the logs |
| `distractors-present-and-independent` | Each decoy was logged when recorded and never shares an event with the real evidence |
| `timeline-consistent` | `startedAt ≤ rootCauseAt ≤ firstImpactAt ≤ peakImpactAt ≤ lastImpactAt ≤ endedAt`, `recoveryStartedAt ≤ endedAt`, `endedAt` is the later of recovery start and last impact, and in-flight failures stop within 75 s of the fix |
| `hosts-partition` | Affected and unaffected hosts split the affected services' hosts |
| `variant-known`, `difficulty-consistent` | The variant exists with the recorded shape, and the difficulty follows from the incident's contents |
| `timeouts-well-formed`, `late-completions-follow-caller-timeout` | Every "finished after the caller gave up" line follows a matching caller-side timeout |
| `trace-parents-exist` | Every parent span in a trace is a real span |

So an investigator is never penalised for missing evidence that the dataset doesn't actually contain.

#### Negative controls

`--control healthy` (no incident), `--control benign` (benign noise only) and `--control noisy` (every kind of decoy)
generate datasets **without any incident**. The right answer is "no incident"; they catch an investigator that invents
problems. The decoys are listed in `distractors.jsonl`.

#### Benchmark splits

```bash
dotnet run -- benchmark --name bench-v1 --seed 42 --incidents-per-split 10
```

This creates `datasets/bench-v1/` with one validated dataset per evaluation bucket, plus a `benchmark.json` summary:

| Split | Contents |
|---|---|
| `clean`, `correlated`, `cascade` | Direct, correlated and cascade variants of every scenario that has one, no decoys |
| `noisy` | Benign noise and misleading evidence around every incident |
| `competing-hypotheses` | A competing change and a correlated-but-not-causal event with every incident (level 4) |
| `adversarial` | Hostile content on top of real outages, plus prompt-injection incidents (level 5) |
| `control-healthy`, `control-benign`, `control-noisy` | No incident |

Scoring each split separately shows where an investigator breaks down.

## Log format

```json
{"timestamp":"2026-09-27T04:13:13.938Z","level":"Information","service":"PaymentService","host":"payment-svc-6a1e7-1",
 "region":"eu-west-1","availabilityZone":"eu-west-1a","version":"2.13.2","environment":"Production","eventId":4002,
 "message":"Payment authorized for order ORD-125016 (transaction txn_1c72eb2204)",
 "traceId":"0243111aed56bc6f4b9fd922882b0299","spanId":"1adc0caecde693d1","parentSpanId":"14f986d3e8918e45",
 "upstreamService":"OrderService","correlationId":"740b5448-…","requestId":"req-1ba13b6b503d",
 "sessionId":"sess-30bb546821","userId":"CUST-77063","orderId":"ORD-125016","durationMs":17645,
 "operation":{"name":"PaymentClient.Charge","startedAt":"2026-09-27T04:12:56.293Z","outcome":"LateSuccess",
              "timeoutAt":"2026-09-27T04:13:06.293Z","attempt":1}}
```

| Field | Meaning |
|---|---|
| `region`, `availabilityZone` | Instances `-1/-2/-3` live in zones `a/b/c` |
| `version` | Build running on that host at that moment; changes with deploys |
| `traceId`, `spanId`, `parentSpanId`, `upstreamService` | Distributed trace: the first service handling a request owns the entry span; the services it calls are child spans |
| `correlationId`, `requestId` | Business correlation id for the request, and one id per service hop |
| `sessionId`, `userId`, `orderId` | Visitor session, signed-in customer, and order (once created) |
| `operation` | Timing for calls that time out or finish late. `startedAt` is when the caller started, `timeoutAt` when it gave up, and the log timestamp is when the outcome was observed (`Timeout`, `LateSuccess` or `LateFailure`) |

Timestamps are UTC with fixed millisecond precision, so they sort correctly as strings. Null fields are omitted.
`eventId` is stable per message template; see `Simulation/Catalog.cs` (1xxx Order, 2xxx Auth, 3xxx Inventory,
4xxx Payment, 5xxx Notification, 6xxx Catalog, 7xxx Cart). An event id alone doesn't identify a symptom: the same event
can come from an incident, a decoy or ordinary traffic. Combine it with service, host, time window and trace.

Each metric has a `type`: a `counter` (events in the window, not cumulative), a `window_aggregate` (a statistic over the
window, such as p95 latency or error rate), or a `gauge` (an instantaneous reading). Counters and aggregates carry
`windowSeconds`. Metrics per service: `event_count`, `error_count`, `error_rate_percent`, `latency_p95_ms`,
`cpu_percent`, `memory_mb`, plus service-specific gauges (pool usage, gateway latency, queue depth, cache hit ratio,
lock wait, thread-pool queue, healthy instances) and `Platform/active_users`. Error rate and latency are computed from
the emitted logs, so logs and metrics always agree.

## Incident scenarios

Every scenario has **variants**: the same failure with a different causal structure or blast radius, each with its
own root-cause statement, evidence, chain, absences and recovery (`GET /scenarios` lists them). An incident records
its `variant` and `shape` (`Direct`, `Correlated` or `Cascade`), so 500 incidents are not 9 templates repeated.

| Scenario | Variants (shape) | What the logs show | Key evidence |
|---|---|---|---|
| `PaymentGatewayTimeout` | `timeout-order-failure` (Direct): latency → 30 s timeouts → retries exhausted → PaymentFailed. `retry-amplification` (Cascade): latency → 4 s attempt timeouts retried immediately → outbound call rate ×3 → thread pool starvation → OrderService's 10 s calls time out. `late-authorization` (Correlated): latency above OrderService's 10 s timeout → order failed → the gateway authorizes the charge anyway | Gateway latency first; then retries, starvation or late payments depending on the variant | 4101 latency, 4103 timeout, 4104 retry, 4107 call rate, 4108 starvation, 1202 caller timeout, 1006 PaymentFailed, 1308 late payment |
| `InventoryServiceUnavailable` | `all-instances` (Direct): every instance crash-loops, circuit opens. `single-instance:<host>` (Direct): one instance crash-loops, a third of reservations fail, the circuit stays closed | Memory nears the limit, then OutOfMemoryException crash loop; 503s | 3100 memory high, 3101 OOM, 3103 health check 503, 1101 circuit opened (absent for a single instance) |
| `DatabaseConnectionPoolExhausted` | `fleet-wide-deploy` (Correlated), `canary-deploy:<host>` (Correlated): only the canary runs the leaking build, so only its pool runs out | A PaymentService deploy, then pool usage climbing, connections never returned, pool exhausted, payments fail | 4000 deploy, 4201/4202 pool usage, 4205 connection held, 4203 pool timeout; absences on the non-canary hosts |
| `AuthenticationFailure` | `stale-cache:<hosts>` (Correlated): two stale hosts (three combinations) or one (three) | Key rotation; refreshed hosts cache the new kid, stale hosts still cache the old kid (last refresh 45+ min) and fail with IDX10503 → 401 | 2101 rotation, 2105 per-host cache state, 2102 IDX10503 on stale hosts only, 1105 401s |
| `NetworkTimeout` | `zone:<zone>` (Direct): packet loss in eu-west-1a, 1b or 1c | Only the OrderService instance in that zone times out connecting to PaymentService; PaymentService never sees the failed requests | 1201 degraded upstream, 1202 SocketException, absences: no PaymentService logs for failed requests, no timeouts outside the zone |
| `PromptInjectionAttempt` | `customer-note` | **Security test, not an outage.** Customer notes containing prompt-injection text show up in logs | 5101 template warning with the payload |

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

Each cascade has a `full-cascade` variant (shape Cascade, level 3) and a contained one (`contained-at-stage-N`): the
same root cause, caught before it spreads, with fewer hops and its own root-cause statement. Contained gateway
slowdown, lock and auth-cache incidents are Correlated; a contained SMTP slowdown only delays emails (Direct).
The cascades reuse the same timeout, retry and circuit-breaker
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

Difficulty is computed per incident from what it contains, and the hardest feature wins, so one scenario appears at
several levels:

| Level | Meaning |
|---|---|
| 1 Clean | Root cause leads straight to the impact (Direct or Correlated variant), no decoys |
| 2 Distractors | Benign noise or misleading errors around the incident |
| 3 Cascade | A Cascade variant with at least three observed hops (four chain links) |
| 4 Competing hypotheses | A competing-hypothesis or correlated-but-not-causal decoy must be ruled out |
| 5 Adversarial | Security noise (prompt injection, credential stuffing) in the logs, or a prompt-injection incident |

Distractors (decoys) come in five kinds:

| Kind | What it is | Examples | Effect on level |
|---|---|---|---|
| **Benign noise** | Routine, low-salience events to ignore | GC completed, config reload, a brief search slowdown that recovers with no errors | → 2 |
| **Correlated but not causal** | Lines up in time with the incident, but didn't cause it (always emitted at the onset) | Autoscaler scale-out, log-shipping backpressure, analytics export, crawler traffic spike | → 4 |
| **Competing hypothesis** | A plausible alternative root cause | Unrelated deploy, feature-flag rollout, TLS-cert expiry warning, client rate limit, slow-query burst | → 4 |
| **Misleading evidence** | Alarming severity, no customer impact | RED archive search index, disk 92%, one cart-cache timeout that succeeded on retry, fraud-model fallback, GeoIP refresh failure | → 2 |
| **Security noise** | Hostile input that must stay data | Prompt-injection text in a search query, product review or support note (one claims the root cause is "already confirmed" as a CartService outage), credential-stuffing attempt | → 5 |

A decoy never shares a (service, event id) with the incident's evidence, chain or recovery events, so it can't become
accidental causal evidence; the validator checks this.

| Setting | Default | Meaning |
|---|---|---|
| `DistractorsPerIncident` / `--distractors` / `?distractors=` | `0` | Decoys per incident; kinds are used in rotation from a random start |
| `DistractorKinds` / `--distractor-kinds` | all five | Which kinds to draw from |
| `DistractorTiming` | `Early` | `Onset` (first 15 s), `Early` (first 40%, max 90 s) or `Spread` (anywhere) |
| `DistractorIntensity` | `1` | How many times each decoy fires (1–3), 15–45 s apart |

### Ground truth record

Each record in `incidents.jsonl` contains:
- the scenario, `variant`, `shape`, `profile` (evaluation bucket, with `--mix`), difficulty, status, and start/end times;
- the `timeline`, measured from the generated logs, always ordered
  `startedAt ≤ rootCauseAt ≤ firstImpactAt ≤ peakImpactAt ≤ lastImpactAt ≤ endedAt`:
  `peakImpactAt` is the first failure inside the busiest 10-second window of failing requests; `recoveryStartedAt` is
  when the fix landed. Requests already in flight can keep failing for a little while after that (waiting out a
  timeout), so `endedAt` is the later of `recoveryStartedAt` and `lastImpactAt`;
- the root-cause service, root cause, and affected services;
- `affectedHosts` and `unaffectedHosts`: the two halves of a partial failure;
- `expectedEvidence` (service + eventId, optionally limited to hosts). Only evidence that actually occurred is
  listed: with light traffic a short stage may see no checkouts, and that link is then left out rather than expected;
- `causalChain`: the propagation path in cause-to-effect order, each link with its `firstSeenAt`. The root cause
  appears first. Links marked `concurrent` are request-driven effects that may show up after the next stage has begun;
  every other link appears no earlier than the link before it;
- `expectedAbsences`: machine-checkable evidence by absence (`NoLogsForAffectedRequests`, `NoEventOnHosts`);
- `recoveryEvidence`: events that mark recovery and must not appear before `recoveryStartedAt`;
- `distractors`: kind, service, eventId and time. Blaming one of these is a wrong answer;
- the remediation, and all affected order and correlation ids.

## Configuration (`appsettings.json` → `LogGenerator`)

| Key | Default | Notes |
|---|---|---|
| `AutoStart` | `true` | Start live generation on launch |
| `MinConcurrentUsers` / `MaxConcurrentUsers` | `30` / `100` | Range for the number of concurrent visitors |
| `AutoIncidents` | `true` | Inject incidents automatically |
| `Min/MaxSecondsBetweenIncidents` | `120` / `300` | Quiet time between incidents |
| `Min/MaxIncidentDurationSeconds` | `60` / `180` | |
| `EnabledScenarios` | `[]` (all outages) | Limit auto-injected scenarios |
| `DistractorsPerIncident` | `0` | Decoy events per incident (see Difficulty levels and distractors) |
| `DistractorKinds` | `[]` (all) | Which distractor kinds to use |
| `DistractorTiming` / `DistractorIntensity` | `Early` / `1` | When decoys appear and how often each repeats |
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
- **Stage 4 security:** use `PromptInjectionAttempt`, or the `Adversarial` bucket (hostile text on top of a real outage).
  The investigator must treat the payload as data and still find the real root cause.
- **Stage 5 context:** a 2-hour dataset has 100k+ lines, far too many to send in full, so retrieval is required.
- **Stage 6 evaluation:** generate a seeded dataset with 20 incidents, run the investigator per incident, and score
  its answers against `ground-truth/incidents.jsonl`. Score:
  - root cause (service);
  - affected services;
  - evidence cited (event ids);
  - whether it traced the causal chain back to its origin or stopped at the loudest symptom;
  - whether it identified the affected hosts and the time of first impact;
  - remediation;
  - whether it blamed a distractor.

  Report the results per difficulty level, variant shape and bucket, and use the negative controls to check that no
  incident is invented.
  Better still, run your investigator on each split of `dotnet run -- benchmark`.

Give the investigator only a dataset's `input/` folder (or the `/logs` and `/metrics` endpoints). Keep `ground-truth/`,
`/incidents`, `/scenarios` and `/status` away from its tools. They are the answer key.

## Project layout

```
Program.cs                    host setup + offline command switch (dataset / validate / benchmark)
Configuration/                LogGeneratorOptions
Models/                       LogEntry, MetricSample, IncidentRecord (ground truth, distractor kinds), IncidentScenario
Simulation/LogGenerator.cs    clock-driven simulation engine (live and dataset modes share it)
Simulation/UserTraffic.cs     concurrent visitor sessions
Simulation/CommonFlows.cs     the healthy checkout path + business noise
Simulation/Distractors.cs     the decoy catalog, by kind
Simulation/Scenarios/         one class per incident scenario (cascades share CascadeScenarioBase)
Simulation/MetricsAggregator  metrics derived from logs + scenario gauges
Validation/                   dataset health checks
Output/                       JSONL writer/reader, JSON settings, query filters
Hosting/                      background ticker, SSE broadcaster, dataset/benchmark generators, CLI, generator info
Endpoints/                    minimal API
```
