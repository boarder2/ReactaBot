using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace reactabot.Health;

public class DiscordHealth(ILogger<DiscordHealth> _logger, DiscordService _discordService) : IHealthCheck
{
	public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
	{
		try
		{
			if (_discordService.GetConnectionState() == ConnectionState.Connected) return Task.FromResult(HealthCheckResult.Healthy());
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error performing Discord health check");
		}
		return Task.FromResult(HealthCheckResult.Unhealthy());
	}
}
