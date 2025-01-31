﻿using Microsoft.Extensions.DependencyInjection;
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

builder.Services.AddSingleton(x =>
{
	return new DiscordSocketClient(new DiscordSocketConfig()
	{
		// AlwaysDownloadUsers = true,
		DefaultRetryMode = RetryMode.AlwaysRetry,
		GatewayIntents =
			// GatewayIntents.GuildMembers |
			GatewayIntents.GuildMessageReactions |
			GatewayIntents.GuildMessages |
			GatewayIntents.Guilds |
			GatewayIntents.MessageContent
	});
});

// Register as singleton so we can resolve it in health checks
builder.Services.AddSingleton<DiscordService>();
// Use the previously registered singleton - If we just add it here, it can't be resolved by the health check.
builder.Services.AddHostedService(provider => provider.GetRequiredService<DiscordService>());

builder.Logging.ClearProviders();
builder.Services.AddSerilog();

// builder.Services.AddSingleton<AuthService>();
builder.Services
	.AddSingleton<AppConfiguration>()
	.AddSingleton<DbHelper>()
	.AddScoped<ReactionsService>()
	.AddSingleton<IHealthCheckPublisher, Publisher>()
	.AddHostedService<SchedulerService>()
	.AddHealthChecks()
	.AddCheck<DiscordHealth>("Discord Health");

var app = builder.Build();
await app.RunAsync();