using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;
using Serilog.Formatting.Compact;
using reactabot;
using reactabot.Health;

Log.Logger = new LoggerConfiguration()
					.Enrich.FromLogContext()
					.MinimumLevel.Verbose()
					.WriteTo.Console(new CompactJsonFormatter())
					.CreateLogger();

var builder = Host.CreateApplicationBuilder(args);

// Register as singleton so we can resolve it in health checks
builder.Services.AddSingleton<DiscordService>();
// Use the previously registered singleton - If we just add it here, it can't be resolved by the health check.
builder.Services.AddHostedService(provider => provider.GetRequiredService<DiscordService>());

builder.Logging.ClearProviders();
builder.Services.AddSerilog();

builder.Services.AddSingleton<HttpClient>(provider =>
{
	var handler = new HttpClientHandler();
	if (handler.SupportsAutomaticDecompression)
	{
		handler.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
	}
	var client = new HttpClient(handler);
	client.DefaultRequestHeaders.UserAgent.ParseAdd("Shacknews Discord Auth Bot");
	return client;
});

// builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<AppConfiguration>();
builder.Services.AddSingleton<DbHelper>();
builder.Services.AddHealthChecks()
	.AddCheck<DiscordHealth>("Discord Health");
builder.Services.AddSingleton<IHealthCheckPublisher, Publisher>();

var app = builder.Build();
await app.RunAsync();