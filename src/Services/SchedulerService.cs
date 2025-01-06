using Cronos;

public class SchedulerService : IHostedService
{
	private readonly ILogger<SchedulerService> _logger;
	private readonly DbHelper _db;
	private readonly DiscordSocketClient _client;
	private readonly ReactionsService _reactionsService;
	private Timer _timer;
	private readonly SemaphoreSlim _lock = new(1, 1);
	private bool _isRunning;
	private const int CHECK_INTERVAL_MS = 15000; // 15 seconds

	public SchedulerService(
		 ILogger<SchedulerService> logger,
		 DbHelper db,
		 DiscordSocketClient client,
		 ReactionsService reactionsService)
	{
		_logger = logger;
		_db = db;
		_client = client;
		_reactionsService = reactionsService;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_timer = new Timer(CheckSchedulesAsync, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(CHECK_INTERVAL_MS));
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_timer?.Dispose();
		_lock.Dispose();
		return Task.CompletedTask;
	}

	//Pretty sure there's a better way to do this. This is what copilot is suggesting.
	// I think this can be simplified using a different timer. But it works for now. Revisit later.
	private void CheckSchedulesAsync(object state)
	{
		// If already running or can't acquire lock, skip this execution
		if (_isRunning || !_lock.Wait(0))
		{
			return;
		}

		try
		{
			_isRunning = true;
			_timer?.Change(Timeout.Infinite, Timeout.Infinite); // Pause the timer

			RunScheduleCheck().GetAwaiter().GetResult();
		}
		finally
		{
			_isRunning = false;
			_timer?.Change(CHECK_INTERVAL_MS, CHECK_INTERVAL_MS); // Resume the timer
			_lock.Release();
		}
	}

	private async Task RunScheduleCheck()
	{
		try
		{
			var jobs = await _db.GetDueJobs();
			foreach (var job in jobs)
			{
				try
				{
					await ExecuteJob(job);

					// Calculate and set next run time
					var expression = CronExpression.Parse(job.CronExpression);
					var nextRun = expression.GetNextOccurrence(DateTime.UtcNow);
					if (nextRun.HasValue)
					{
						await _db.UpdateJobNextRun(job.Id, nextRun.Value);
					}
					else
					{
						_logger.LogWarning("Could not determine next run time for job {JobId}", job.Id);
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error processing job {JobId}", job.Id);
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error checking scheduled jobs");
		}
	}

	private async Task ExecuteJob(ScheduledJob job)
	{
		try
		{
			var channel = _client.GetChannel(job.ChannelId) as ITextChannel;
			if (channel == null)
			{
				_logger.LogError("Could not find channel {ChannelId} for job {JobId}", job.ChannelId, job.Id);
				return;
			}

			var endDate = DateTimeOffset.UtcNow;
			var startDate = endDate.Subtract(ParseInterval(job.Interval));
			
			var messages = await _db.GetTopMessages(
				startDate, 
				endDate,
				job.Count, 
				job.GuildId);

			if (messages.Any())
			{
				var header = $"Top {job.Count} messages for the last {job.Interval} (from {startDate:MMM dd HH:mm} to {endDate:MMM dd HH:mm} UTC)";
				var response = await _reactionsService.FormatTopMessages(_client, messages, header);
				await channel.SendMessageAsync(response);
				_logger.LogInformation("Successfully executed job {JobId}", job.Id);
			}
			else
			{
				_logger.LogInformation("No messages found for job {JobId}", job.Id);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error executing scheduled job {JobId}", job.Id);
		}
	}

	private TimeSpan ParseInterval(string interval)
	{
		return interval switch
		{
			"1h" => TimeSpan.FromHours(1),
			"4h" => TimeSpan.FromHours(4),
			"8h" => TimeSpan.FromHours(8),
			"12h" => TimeSpan.FromHours(12),
			"24h" => TimeSpan.FromDays(1),
			"2d" => TimeSpan.FromDays(2),
			"3d" => TimeSpan.FromDays(3),
			"5d" => TimeSpan.FromDays(5),
			"7d" => TimeSpan.FromDays(7),
			_ => TimeSpan.FromDays(1)
		};
	}
}
