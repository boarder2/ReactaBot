using System.IO;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace shacknews_discord_auth_bot.Health;

public class Publisher : IHealthCheckPublisher
{
	private readonly string _fileName;

	public Publisher() => _fileName = Environment.GetEnvironmentVariable("DOCKER_HEALTHCHECK_FILEPATH");

	public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
	{
		if (_fileName == null) return Task.CompletedTask;
		
		var fileExists = File.Exists(_fileName);
		if (report.Status == HealthStatus.Healthy)
		{
			if (!fileExists)
			{
				using var _ = File.Create(_fileName);
			}
			else
			{
				File.SetLastWriteTimeUtc(_fileName, DateTime.UtcNow);
			}
		}
		else if (fileExists)
		{
			File.Delete(_fileName);
		}

		return Task.CompletedTask;
	}
}