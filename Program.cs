using System.Globalization;
using SampleLogGenerator.Configuration;
using SampleLogGenerator.Endpoints;
using SampleLogGenerator.Hosting;
using SampleLogGenerator.Output;

// Log messages format numbers with '.' regardless of the machine's locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var offline = args.Length > 0 && DatasetCli.Commands.Contains(args[0]);
var builder = WebApplication.CreateBuilder(offline ? [] : args);
var generatorSection = builder.Configuration.GetSection(LogGeneratorOptions.SectionName);

// Offline commands: dataset / validate / benchmark (see DatasetCli)
if (offline)
    return DatasetCli.Run(args[0], args[1..], generatorSection.Get<LogGeneratorOptions>() ?? new LogGeneratorOptions());

builder.Services.AddOpenApi();
builder.Services.Configure<LogGeneratorOptions>(generatorSection);
builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Apply(o.SerializerOptions));
builder.Services.AddSingleton<LogBroadcaster>();
builder.Services.AddSingleton<SimulationHost>();
builder.Services.AddHostedService<LiveLogService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGeneratorEndpoints();

app.Run();
return 0;
