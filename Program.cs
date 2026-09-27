using System.Globalization;
using SampleLogGenerator.Configuration;
using SampleLogGenerator.Endpoints;
using SampleLogGenerator.Hosting;
using SampleLogGenerator.Output;

// Log messages format numbers with '.' regardless of the machine's locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args is ["dataset", ..] ? [] : args);
var generatorSection = builder.Configuration.GetSection(LogGeneratorOptions.SectionName);

// Offline mode: dotnet run -- dataset --minutes 60 --incidents 5 --seed 42
if (args is ["dataset", .. var datasetArgs])
    return DatasetCli.Run(datasetArgs, generatorSection.Get<LogGeneratorOptions>() ?? new LogGeneratorOptions());

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
