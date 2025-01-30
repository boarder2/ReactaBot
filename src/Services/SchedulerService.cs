using System.Net.Mime;
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
				using var _ = _logger.BeginScope(new Dictionary<string, object>
				{
					{ "JobId", job.Id },
					{ "GuildId", job.GuildId },
					{ "ChannelId", job.ChannelId },
					{ "CronExpression", job.CronExpression },
					{ "IntervalHours", job.IntervalHours },
					{ "Count", job.Count }
				});

				await ExecuteJob(job);
				try
				{
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
		if (job.IsForum)
		{
			var forumChannel = _client.GetChannel(job.ChannelId) as IForumChannel;
			if (forumChannel == null)
			{
				throw new Exception("Could not find forum channel");
			}

			var endDate = DateTimeOffset.UtcNow;
			var startDate = endDate.Subtract(TimeSpan.FromHours(job.IntervalHours));

			var messages = await _db.GetTopMessages(
				startDate,
				endDate,
				job.Count,
				job.GuildId);

			if (messages.Any())
			{
				var threadTitle = ProcessTitleTemplate(job.ThreadTitleTemplate, job.Count, job.IntervalHours);
				var header = $"Top {job.Count} messages for the last {job.IntervalHours:0.#}h (from {startDate:MMM dd HH:mm} to {endDate:MMM dd HH:mm} UTC)";
				var embedGroups = await _reactionsService.FormatTopMessagesAsEmbeds(_client, messages);

				var thread = await forumChannel.CreatePostAsync(
					threadTitle,
					text: header
				);

				// Post additional parts
				foreach (var embedGroup in embedGroups)
				{
					await thread.SendMessageAsync(embeds: embedGroup.ToArray());
				}

				_logger.LogInformation("Successfully created forum thread with {Count} embed groups", embedGroups.Count);
			}
			else
			{
				_logger.LogInformation("No messages found for forum job");
			}
		}
		else
		{
			var channel = _client.GetChannel(job.ChannelId) as ITextChannel;
			if (channel == null)
			{
				throw new Exception($"Could not find channel");
			}

			var endDate = DateTimeOffset.UtcNow;
			var startDate = endDate.Subtract(TimeSpan.FromHours(job.IntervalHours));

			var messages = await _db.GetTopMessages(
				startDate,
				endDate,
				job.Count,
				job.GuildId);

			if (messages.Any())
			{
				var header = $"Top {job.Count} messages for the last {job.IntervalHours:0.#}h (from {startDate:MMM dd HH:mm} to {endDate:MMM dd HH:mm} UTC)";
				var embedGroups = await _reactionsService.FormatTopMessagesAsEmbeds(_client, messages);
				
				await channel.SendMessageAsync(text: header);

				// Send all parts
				foreach (var embedGroup in embedGroups)
				{
					await channel.SendMessageAsync(embeds: embedGroup.ToArray());
				}
				_logger.LogInformation("Successfully executed job with {Count} embed groups", embedGroups.Count);
			}
			else
			{
				_logger.LogInformation("No messages found for job");
			}
		}
	}

	private string ProcessTitleTemplate(string template, int count, double intervalHours)
	{
		var now = DateTime.UtcNow;
		var result = System.Text.RegularExpressions.Regex.Replace(
			template,
			@"\{date(?::([^}]+))?\}",
			match =>
			{
				var format = match.Groups[1].Success ? match.Groups[1].Value : "G";
				try
				{
					return now.ToString(format);
				}
				catch (FormatException)
				{
					return now.ToString("G");
				}
			});
		result = result.Replace("{count}", count.ToString())
			.Replace("{interval}", $"{intervalHours:0.#}h");
		return result;
	}
}
