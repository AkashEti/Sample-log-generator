using Microsoft.Extensions.Options;
using SampleLogGenerator.Configuration;
using SampleLogGenerator.Hosting;
using SampleLogGenerator.Models;
using SampleLogGenerator.Output;
using SampleLogGenerator.Simulation;
using SampleLogGenerator.Simulation.Scenarios;

namespace SampleLogGenerator.Endpoints;

public static class GeneratorEndpoints
{
    public static void MapGeneratorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Ok(new
        {
            name = "Sample log generator",
            endpoints = new[]
            {
                "GET  /status",
                "POST /generator/start | /generator/stop",
                "GET  /logs?service=&level=&minLevel=&orderId=&correlationId=&q=&from=&to=&take=",
                "GET  /logs/stream (Server-Sent Events, same filters)",
                "GET  /metrics?service=&metric=&from=&to=&take=",
                "GET  /scenarios",
                "POST /incidents/{scenario}?durationSeconds=90&distractors=2",
                "POST /incidents/resolve",
                "GET  /incidents (ground truth)",
                "POST /datasets",
            },
        }));

        app.MapGet("/status", (SimulationHost host) => host.Status());

        var generator = app.MapGroup("/generator");
        generator.MapPost("/start", (SimulationHost host) => { host.Start(); return host.Status(); });
        generator.MapPost("/stop", (SimulationHost host) => { host.Stop(); return host.Status(); });

        app.MapGet("/logs", ([AsParameters] LogQuery query, SimulationHost host) => JsonlReader.SearchLogs(host.LogsPath, query));

        app.MapGet("/logs/stream", ([AsParameters] LogQuery query, LogBroadcaster broadcaster, CancellationToken ct) =>
            TypedResults.ServerSentEvents(broadcaster.Subscribe(query.Matches, ct), eventType: "log"));

        app.MapGet("/metrics", ([AsParameters] MetricQuery query, SimulationHost host) => JsonlReader.SearchMetrics(host.MetricsPath, query));

        app.MapGet("/scenarios", () => ScenarioCatalog.All.Values.Select(s => new
        {
            s.Scenario,
            s.Title,
            Variants = s.Variants.Select(v => new { v.Name, v.Shape }),
            s.RootCauseService,
            s.AffectedServices,
            s.IsSecurityTest,
        }));

        var incidents = app.MapGroup("/incidents");
        incidents.MapGet("/", (SimulationHost host) => host.Incidents());

        incidents.MapPost("/resolve", (SimulationHost host) =>
            host.ResolveIncident() is { } record ? Results.Ok(record) : Results.NotFound(new { error = "No active incident." }));

        incidents.MapPost("/{scenario}", (string scenario, int? durationSeconds, int? distractors, string? variant, string? profile, SimulationHost host) =>
        {
            if (!Enum.TryParse<IncidentScenario>(scenario, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return Results.BadRequest(new { error = $"Unknown scenario '{scenario}'.", valid = Enum.GetNames<IncidentScenario>() });
            if (durationSeconds is < 10 or > 3600)
                return Results.BadRequest(new { error = "durationSeconds must be between 10 and 3600." });
            if (distractors is < 0 or > 6)
                return Results.BadRequest(new { error = "distractors must be between 0 and 6." });
            EvaluationProfile? parsedProfile = null;
            if (profile is not null)
            {
                if (!Enum.TryParse<EvaluationProfile>(profile, ignoreCase: true, out var p) || !Enum.IsDefined(p))
                    return Results.BadRequest(new { error = $"Unknown profile '{profile}'.", valid = Enum.GetNames<EvaluationProfile>() });
                parsedProfile = p;
            }

            try
            {
                var plan = (parsedProfile is { } pp ? EvaluationMix.PlanFor(pp) : new IncidentPlan()) with
                {
                    Duration = durationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
                    Variant = variant,
                };
                if (variant is not null) plan = plan with { Shape = null };
                if (distractors is not null) plan = plan with { Distractors = distractors };
                return Results.Ok(host.TriggerIncident(parsed, plan));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/datasets", async (DatasetRequest? request, IOptions<LogGeneratorOptions> options, IHostEnvironment env) =>
        {
            try
            {
                var root = Path.Combine(env.ContentRootPath, options.Value.DatasetsDirectory);
                return Results.Ok(await Task.Run(() => DatasetGenerator.Generate(request ?? new DatasetRequest(), options.Value, root)));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
