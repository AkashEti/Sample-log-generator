using System.Text.Json;
using SampleLogGenerator.Hosting;
using SampleLogGenerator.Models;
using SampleLogGenerator.Output;
using SampleLogGenerator.Simulation;
using SampleLogGenerator.Simulation.Scenarios;

namespace SampleLogGenerator.Validation;

public sealed record ValidationCheck(string Name, bool Passed, string Detail);

public sealed record IncidentValidation(string IncidentId, IncidentScenario Scenario, bool Passed, IReadOnlyList<ValidationCheck> Checks);

public sealed record ValidationReport(
    string Dataset,
    bool Passed,
    int LogCount,
    int IncidentCount,
    int FailedChecks,
    IReadOnlyList<ValidationCheck> DatasetChecks,
    IReadOnlyList<IncidentValidation> Incidents);

/// <summary>
/// Health checks a dataset must pass before it is used as an evaluation corpus. It guards against generator defects
/// where the logs don't consistently contain the evidence the ground truth expects, which would penalise an
/// investigator unfairly.
/// </summary>
public static class DatasetValidator
{
    /// <summary>How long after the end (or the last failing request) incident effects and recovery may still appear.</summary>
    private static readonly TimeSpan Tail = TimeSpan.FromSeconds(60);

    /// <summary>Longest a request already in flight when the fix lands may keep failing (two 30 s gateway attempts).</summary>
    private static readonly TimeSpan InFlightDrain = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(2);

    public static ValidationReport Validate(string directory)
    {
        var input = DatasetLayout.InputDir(directory);
        var truth = DatasetLayout.GroundTruthDir(directory);
        var (logs, badLogs) = JsonlReader.ReadAllCounted<LogEntry>(Path.Combine(input, "logs.jsonl"));
        var (metrics, badMetrics) = JsonlReader.ReadAllCounted<MetricSample>(Path.Combine(input, "metrics.jsonl"));
        var (incidents, badIncidents) = JsonlReader.ReadAllCounted<IncidentRecord>(Path.Combine(truth, "incidents.jsonl"));
        var (standalone, badDistractors) = JsonlReader.ReadAllCounted<DistractorRecord>(Path.Combine(truth, "distractors.jsonl"));
        var index = new LogIndex(logs);

        var datasetChecks = new List<ValidationCheck>
        {
            // A record the current schema cannot read (corrupt, or from an older generator version) would otherwise go unchecked.
            Check("all-lines-parse", badLogs + badMetrics + badIncidents + badDistractors == 0,
                $"unreadable lines: {badLogs} logs, {badMetrics} metrics, {badIncidents} incidents, {badDistractors} distractors"),
            Check("logs-sorted", IsSorted(logs.Select(l => l.Timestamp)), "log timestamps are non-decreasing"),
            Check("metrics-sorted", IsSorted(metrics.Select(m => m.Timestamp)), "metric timestamps are non-decreasing"),
            TimeoutsWellFormed(logs),
            LateCompletionsFollowCallerTimeout(logs, index),
            TraceParentsExist(logs),
            StandaloneDistractorsPresent(standalone, index),
            UniqueIncidentIds(incidents),
            GroundTruthNotInLogs(logs, incidents),
        };

        var incidentReports = incidents.Select(i => ValidateIncident(i, index)).ToList();
        var failed = datasetChecks.Count(c => !c.Passed) + incidentReports.Sum(r => r.Checks.Count(c => !c.Passed));
        return new ValidationReport(Path.GetFileName(directory), failed == 0, logs.Count, incidents.Count, failed, datasetChecks, incidentReports);
    }

    public static void Write(ValidationReport report, string directory) =>
        File.WriteAllText(Path.Combine(DatasetLayout.GroundTruthDir(directory), "validation.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true }));

    private static IncidentValidation ValidateIncident(IncidentRecord incident, LogIndex index)
    {
        var start = incident.StartedAt;
        var end = incident.EndedAt ?? start;
        // Recovery starts when the fix lands, which can be before the incident is over (in-flight requests still failing).
        var recoveryAt = incident.Timeline.RecoveryStartedAt ?? end;
        var effectsUntil = Max(end, incident.Timeline.LastImpactAt ?? end) + Tail;
        var checks = new List<ValidationCheck>();

        // 1. Every expected evidence event exists (on the listed hosts, when given).
        var missing = incident.ExpectedEvidence
            .Where(e => !index.Find(e.Service, e.EventId, start, effectsUntil, e.Hosts).Any())
            .Select(Describe).ToList();
        checks.Add(Check("evidence-present", missing.Count == 0, missing.Count == 0 ? $"{incident.ExpectedEvidence.Count} events found" : "missing: " + string.Join(", ", missing)));

        // 2. Causal chain: recorded first-seen times are real, the root cause comes first, and the ordering rule holds.
        var chain = incident.CausalChain;
        var badFirstSeen = chain
            .Where(l => l.FirstSeenAt is not { } seen || !index.Find(l.Service, l.EventId, seen, seen).Any())
            .Select(l => $"{l.Service}/{l.EventId}").ToList();
        checks.Add(Check("chain-first-seen-matches-logs", badFirstSeen.Count == 0,
            badFirstSeen.Count == 0 ? $"{chain.Count} links" : "no log at recorded time for: " + string.Join(", ", badFirstSeen)));

        var rootFirst = chain.Count > 0 && !chain[0].Concurrent && chain.All(l => l.FirstSeenAt >= chain[0].FirstSeenAt);
        checks.Add(Check("chain-root-first", rootFirst,
            chain.Count == 0 ? "empty chain" : $"root {chain[0].Service}/{chain[0].EventId} at {chain[0].FirstSeenAt:HH:mm:ss.fff}"));

        var violations = new List<string>();
        CausalLink? anchor = null;
        foreach (var link in chain)
        {
            if (anchor is not null && link.FirstSeenAt < anchor.FirstSeenAt)
                violations.Add($"{link.Service}/{link.EventId} ({link.FirstSeenAt:HH:mm:ss.fff}) before {anchor.Service}/{anchor.EventId} ({anchor.FirstSeenAt:HH:mm:ss.fff})");
            if (!link.Concurrent) anchor = link;
        }
        checks.Add(Check("chain-order", violations.Count == 0, violations.Count == 0 ? "each link at or after its preceding ordered link" : string.Join("; ", violations)));

        // 3. Recovery never shows up before the fix lands, and never before the impact it recovers from.
        if (incident.Status == IncidentStatus.Resolved)
        {
            var problems = new List<string>();
            foreach (var r in incident.RecoveryEvidence)
            {
                if (index.Find(r.Service, r.EventId, start, recoveryAt.AddTicks(-1), r.Hosts).Any()) problems.Add($"{Describe(r)} before recovery start");
                if (!index.Find(r.Service, r.EventId, recoveryAt, recoveryAt + RecoveryWindow, r.Hosts).Any()) problems.Add($"{Describe(r)} missing after recovery start");
            }
            if (incident.Timeline.FirstImpactAt > recoveryAt) problems.Add("recovery starts before the first impact");
            checks.Add(Check("recovery-after-impact", problems.Count == 0,
                problems.Count == 0 ? $"{incident.RecoveryEvidence.Count} recovery events, all at or after {recoveryAt:HH:mm:ss.fff}" : string.Join("; ", problems)));
        }

        // 4. Absence conditions hold.
        var broken = new List<string>();
        foreach (var absence in incident.ExpectedAbsences)
        {
            var violated = absence.Kind switch
            {
                AbsenceKind.NoLogsForAffectedRequests => incident.AffectedCorrelationIds.Any(c => index.ByCorrelation(c).Any(l => l.Service == absence.Service)),
                AbsenceKind.NoEventOnHosts => index.Find(absence.Service, absence.EventId ?? -1, start, effectsUntil, absence.Hosts).Any(),
                _ => false,
            };
            if (violated) broken.Add($"{absence.Kind} {absence.Service}");
        }
        checks.Add(Check("absences-hold", broken.Count == 0,
            broken.Count == 0 ? $"{incident.ExpectedAbsences.Count} absence conditions" : "violated: " + string.Join(", ", broken)));

        // 5. Affected ids really appear in the logs.
        var unknownOrders = incident.AffectedOrderIds.Count(o => !index.HasOrder(o));
        var unknownRequests = incident.AffectedCorrelationIds.Count(c => !index.ByCorrelation(c).Any());
        checks.Add(Check("affected-ids-present", unknownOrders + unknownRequests == 0,
            $"{incident.AffectedOrderIds.Count} orders, {incident.AffectedCorrelationIds.Count} requests; missing {unknownOrders} orders, {unknownRequests} requests"));

        // 6. Distractors were emitted when recorded and never overlap the real evidence.
        var reserved = incident.ExpectedEvidence.Select(e => (e.Service, e.EventId))
            .Concat(incident.CausalChain.Select(l => (l.Service, l.EventId)))
            .Concat(incident.RecoveryEvidence.Select(e => (e.Service, e.EventId)))
            .ToHashSet();
        var distractorProblems = incident.Distractors
            .Where(d => !index.Find(d.Service, d.EventId, d.At, d.At).Any() || reserved.Contains((d.Service, d.EventId)))
            .Select(d => $"{d.Kind} {d.Service}/{d.EventId}").ToList();
        checks.Add(Check("distractors-present-and-independent", distractorProblems.Count == 0,
            distractorProblems.Count == 0 ? $"{incident.Distractors.Count} distractors" : "bad: " + string.Join(", ", distractorProblems)));

        // 7. Timeline: StartedAt <= RootCauseAt <= FirstImpactAt <= PeakImpactAt <= LastImpactAt <= EndedAt, RecoveryStartedAt <= EndedAt.
        var t = incident.Timeline;
        var timelineProblems = new List<string>();
        void Order(string earlierName, DateTime? earlier, string laterName, DateTime? later)
        {
            if (earlier is { } a && later is { } b && a > b) timelineProblems.Add($"{earlierName} {a:HH:mm:ss.fff} after {laterName} {b:HH:mm:ss.fff}");
        }
        if (t.RootCauseAt != chain.FirstOrDefault()?.FirstSeenAt) timelineProblems.Add("rootCauseAt is not the root link's first appearance");
        if ((t.FirstImpactAt is null) != (t.LastImpactAt is null) || (t.FirstImpactAt is null) != (t.PeakImpactAt is null))
            timelineProblems.Add("impact times are partially missing");
        Order("startedAt", start, "rootCauseAt", t.RootCauseAt);
        Order("startedAt", start, "firstImpactAt", t.FirstImpactAt);
        Order("rootCauseAt", t.RootCauseAt, "firstImpactAt", t.FirstImpactAt);
        Order("firstImpactAt", t.FirstImpactAt, "peakImpactAt", t.PeakImpactAt);
        Order("peakImpactAt", t.PeakImpactAt, "lastImpactAt", t.LastImpactAt);
        Order("lastImpactAt", t.LastImpactAt, "endedAt", incident.EndedAt);
        Order("recoveryStartedAt", t.RecoveryStartedAt, "endedAt", incident.EndedAt);
        if (incident.Status == IncidentStatus.Resolved)
        {
            if (t.RecoveryStartedAt is not { } recovery) timelineProblems.Add("resolved without recoveryStartedAt");
            else
            {
                var expectedEnd = t.LastImpactAt > recovery ? t.LastImpactAt.Value : recovery;
                if (incident.EndedAt != expectedEnd) timelineProblems.Add("endedAt is not the later of recoveryStartedAt and lastImpactAt");
                if (t.LastImpactAt - recovery > InFlightDrain) timelineProblems.Add($"impact continues more than {InFlightDrain.TotalSeconds:0} s after recovery started");
            }
        }
        checks.Add(Check("timeline-consistent", timelineProblems.Count == 0, timelineProblems.Count == 0
            ? $"root {t.RootCauseAt:HH:mm:ss.fff}, impact {t.FirstImpactAt:HH:mm:ss.fff}-{t.LastImpactAt:HH:mm:ss.fff} (peak {t.PeakImpactAt:HH:mm:ss.fff}), recovery {t.RecoveryStartedAt:HH:mm:ss.fff}, end {incident.EndedAt:HH:mm:ss.fff}"
            : string.Join("; ", timelineProblems)));

        // 8. The variant exists and the recorded difficulty follows from the incident's contents.
        var variant = ScenarioCatalog.All.TryGetValue(incident.Scenario, out var definition)
            ? definition.Variants.FirstOrDefault(v => v.Name == incident.Variant)
            : null;
        checks.Add(Check("variant-known", variant is not null && variant.Shape == incident.Shape,
            variant is null ? $"unknown variant '{incident.Variant}'" : $"{incident.Variant} ({incident.Shape})"));
        var expectedDifficulty = ActiveIncident.DifficultyOf(incident);
        checks.Add(Check("difficulty-consistent", expectedDifficulty == incident.Difficulty,
            $"recorded {incident.Difficulty}, derived {expectedDifficulty} from shape {incident.Shape}, {chain.Count} chain links, " +
            $"{incident.Distractors.Count} distractors, security test {incident.IsSecurityTest}"));

        var serviceHosts = incident.AffectedServices.SelectMany(Services.HostsFor).ToHashSet();
        var hostsOk = !incident.AffectedHosts.Intersect(incident.UnaffectedHosts).Any() &&
                      serviceHosts.SetEquals(incident.AffectedHosts.Concat(incident.UnaffectedHosts));
        checks.Add(Check("hosts-partition", hostsOk, $"{incident.AffectedHosts.Count} affected, {incident.UnaffectedHosts.Count} unaffected"));

        return new IncidentValidation(incident.IncidentId, incident.Scenario, checks.All(c => c.Passed), checks);
    }

    private static ValidationCheck TimeoutsWellFormed(List<LogEntry> logs)
    {
        var timeouts = logs.Where(l => l.Operation?.Outcome == OperationOutcomes.Timeout).ToList();
        var bad = timeouts.Count(l => l.Operation!.TimeoutAt != l.Timestamp || l.Operation.StartedAt > l.Timestamp);
        return Check("timeouts-well-formed", bad == 0, $"{timeouts.Count} timeouts logged at their timeoutAt, after their start; {bad} bad");
    }

    /// <summary>Work that finishes after its caller gave up must come after a matching caller-side timeout.</summary>
    private static ValidationCheck LateCompletionsFollowCallerTimeout(List<LogEntry> logs, LogIndex index)
    {
        var late = logs.Where(l => l.Operation?.Outcome is OperationOutcomes.LateSuccess or OperationOutcomes.LateFailure).ToList();
        var bad = late.Count(l =>
        {
            var op = l.Operation!;
            if (op.TimeoutAt is not { } timeoutAt || timeoutAt > l.Timestamp || l.CorrelationId is null) return true;
            return !index.ByCorrelation(l.CorrelationId).Any(c =>
                c.Operation is { Outcome: OperationOutcomes.Timeout } t && c.Timestamp == timeoutAt && t.Name == op.Name && t.Attempt == op.Attempt);
        });
        return Check("late-completions-follow-caller-timeout", bad == 0, $"{late.Count} late completions; {bad} without a matching earlier caller timeout");
    }

    private static ValidationCheck TraceParentsExist(List<LogEntry> logs)
    {
        var spans = logs.Where(l => l.TraceId is not null).GroupBy(l => l.TraceId!).ToDictionary(g => g.Key, g => g.Select(l => l.SpanId).ToHashSet());
        var children = logs.Where(l => l.ParentSpanId is not null).ToList();
        var orphans = children.Count(l => !spans[l.TraceId!].Contains(l.ParentSpanId));
        return Check("trace-parents-exist", orphans == 0, $"{children.Count} child-span log lines; {orphans} whose parent span never logged");
    }

    private static ValidationCheck StandaloneDistractorsPresent(List<DistractorRecord> distractors, LogIndex index)
    {
        var missing = distractors.Count(d => !index.Find(d.Service, d.EventId, d.At, d.At).Any());
        return Check("standalone-distractors-present", missing == 0, $"{distractors.Count} standalone distractors; {missing} missing");
    }

    private static ValidationCheck UniqueIncidentIds(List<IncidentRecord> incidents)
    {
        var duplicates = incidents.GroupBy(i => i.IncidentId).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        return Check("unique-incident-ids", duplicates.Count == 0,
            duplicates.Count == 0 ? $"{incidents.Count} incidents" : "duplicated: " + string.Join(", ", duplicates));
    }

    /// <summary>
    /// The investigator's input must not give the answer away: no incident id, scenario name or root-cause statement in
    /// any log line. (Prompt-injection payloads may claim a root cause; that is hostile data, not ground truth.)
    /// </summary>
    private static ValidationCheck GroundTruthNotInLogs(List<LogEntry> logs, List<IncidentRecord> incidents)
    {
        var secrets = incidents.Select(i => i.IncidentId)
            .Concat(incidents.Select(i => i.RootCause))
            .Concat(Enum.GetNames<IncidentScenario>())
            .Distinct()
            .ToList();
        var leaks = logs.Where(l => secrets.Any(secret => l.Message.Contains(secret, StringComparison.Ordinal) ||
                                                          (l.Exception?.Contains(secret, StringComparison.Ordinal) ?? false)))
            .Take(5)
            .Select(l => $"{l.Service}/{l.EventId} at {l.Timestamp:HH:mm:ss.fff}")
            .ToList();
        return Check("ground-truth-not-in-logs", leaks.Count == 0,
            leaks.Count == 0 ? $"no incident id, scenario name or root-cause text in {logs.Count} log lines" : "leaked in: " + string.Join(", ", leaks));
    }

    private static ValidationCheck Check(string name, bool passed, string detail) => new(name, passed, detail);

    private static string Describe(EvidenceHint e) =>
        e.Hosts is { Count: > 0 } hosts ? $"{e.Service}/{e.EventId} on {string.Join("|", hosts)}" : $"{e.Service}/{e.EventId}";

    private static bool IsSorted(IEnumerable<DateTime> values)
    {
        DateTime? previous = null;
        foreach (var v in values)
        {
            if (v < previous) return false;
            previous = v;
        }
        return true;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>Lookups over the logs by (service, event id), correlation id and order id.</summary>
    private sealed class LogIndex
    {
        private readonly Dictionary<(string, int), List<LogEntry>> _byEvent;
        private readonly Dictionary<string, List<LogEntry>> _byCorrelation;
        private readonly HashSet<string> _orders;

        public LogIndex(List<LogEntry> logs)
        {
            _byEvent = logs.GroupBy(l => (l.Service, l.EventId)).ToDictionary(g => g.Key, g => g.OrderBy(l => l.Timestamp).ToList());
            _byCorrelation = logs.Where(l => l.CorrelationId is not null).GroupBy(l => l.CorrelationId!).ToDictionary(g => g.Key, g => g.ToList());
            _orders = [.. logs.Where(l => l.OrderId is not null).Select(l => l.OrderId!)];
        }

        public IEnumerable<LogEntry> Find(string service, int eventId, DateTime from, DateTime to, IReadOnlyList<string>? hosts = null) =>
            _byEvent.TryGetValue((service, eventId), out var list)
                ? list.Where(l => l.Timestamp >= from && l.Timestamp <= to && (hosts is not { Count: > 0 } || hosts.Contains(l.Host)))
                : [];

        public IReadOnlyList<LogEntry> ByCorrelation(string correlationId) =>
            _byCorrelation.TryGetValue(correlationId, out var list) ? list : [];

        public bool HasOrder(string orderId) => _orders.Contains(orderId);
    }
}
