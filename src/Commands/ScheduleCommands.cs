using Cronos;
using Discord.Interactions;
using System.Text;

[RequireContext(ContextType.Guild)]
[CommandContextType(InteractionContextType.Guild)]
public class ScheduleCommands(
	DbHelper db,
	ILogger<ScheduleCommands> logger) : InteractionModuleBase<SocketInteractionContext>
{
	[Group("schedule", "Manage scheduled reports")]
	[RequireContext(ContextType.Guild)]
	[CommandContextType(InteractionContextType.Guild)]
	public class ScheduleSubCommands(
		DbHelper _db,
		ILogger<ScheduleCommands> _logger) : InteractionModuleBase<SocketInteractionContext>
	{
		private const double MIN_INTERVAL_HOURS = 0.5;
		private const double MAX_INTERVAL_HOURS = 168; // 7 days

		[SlashCommand("channel", "Schedule recurring top messages report for a text channel")]
		[CommandContextType(InteractionContextType.Guild)]
		public async Task HandleChannelSchedule(
			[Summary("cron", "Cron expression for scheduling (e.g. '0 */4 * * *' for every 4 hours)")] string cron,
			[Summary("interval", "Time interval to analyze in hours (0.5-168)")] 
			[MinValue(MIN_INTERVAL_HOURS), MaxValue(MAX_INTERVAL_HOURS)]
			double interval,
			[Summary("channel", "Text channel to post results")]
			[ChannelTypes(ChannelType.Text)] ITextChannel channel,
			[Summary("count", "Number of messages to show (1-20)")]
			[MinValue(1), MaxValue(20)] int count)
		{
			await CreateSchedule(cron, interval, channel, count);
		}

		[SlashCommand("forum", "Schedule recurring top messages report in a forum")]
		[CommandContextType(InteractionContextType.Guild)]
		public async Task HandleForumSchedule(
			[Summary("cron", "Cron expression for scheduling (e.g. '0 */4 * * *' for every 4 hours)")] string cron,
			[Summary("interval", "Time interval to analyze in hours (0.5-168)")]
			[MinValue(MIN_INTERVAL_HOURS), MaxValue(MAX_INTERVAL_HOURS)]
			double interval,
			[Summary("forum", "Forum channel to create thread in")]
			[ChannelTypes(ChannelType.Forum)] IForumChannel forum,
			[Summary("count", "Number of messages to show (1-20)")]
			[MinValue(1), MaxValue(20)] int count,
			[Summary("threadtitle", "Thread title template. Variables: {date:format}, {count}, {interval}. Date format is optional.")]
			[MinLength(1), MaxLength(100)] string threadTitle)
		{
			await CreateSchedule(cron, interval, forum, count, threadTitle);
		}

		private async Task CreateSchedule(string cron, double interval, IChannel channel, int count, string threadTitle = "")
		{
			try
			{
				await DeferAsync(ephemeral: true);
				_logger.LogInformation("Creating schedule for guild {GuildId}, channel {ChannelId}, interval {Interval}h, count {Count}",
					Context.Guild.Id, channel.Id, interval, count);

				if (!IsValidInterval(interval))
				{
					await ModifyOriginalResponseAsync(x => x.Embed = Embeds.Error(
						$"Invalid interval. Must be between {MIN_INTERVAL_HOURS} and {MAX_INTERVAL_HOURS} hours."));
					return;
				}

				try
				{
					var expression = CronExpression.Parse(cron);
					var nextRun = expression.GetNextOccurrence(DateTime.UtcNow);
					if (!nextRun.HasValue)
					{
						await ModifyOriginalResponseAsync(x => x.Embed = Embeds.Error(
							"Invalid cron expression - could not determine next run time"));
						return;
					}

					var job = new ScheduledJob
					{
						CronExpression = cron,
						IntervalHours = interval,
						ChannelId = channel.Id,
						GuildId = Context.Guild.Id,
						Count = count,
						NextRun = nextRun.Value,
						CreatedAt = DateTime.UtcNow,
						IsForum = channel is IForumChannel,
						ThreadTitleTemplate = threadTitle
					};

					await _db.CreateScheduledJob(job);

					var description = $"Scheduled job created. Will post top {count} messages for the last `{interval:0.#}h` to {MentionChannel(channel)}\n" +
						$"Schedule: `{cron}`\n" +
						$"Next run: {nextRun.Value:yyyy-MM-dd HH:mm:ss} UTC";

					if (channel is IForumChannel && !string.IsNullOrEmpty(threadTitle))
					{
						description += $"\nThread Title: `{threadTitle}`";
					}

					_logger.LogInformation("Created schedule for guild {GuildId}, channel {ChannelId}, next run at {NextRun}",
						Context.Guild.Id, channel.Id, nextRun);

					await ModifyOriginalResponseAsync(x => x.Embed = Embeds.Success(description));
				}
				catch
				{
					await ModifyOriginalResponseAsync(x => x.Embed = Embeds.Error("Invalid cron expression"));
				}
			}
			catch (Exception ex)
			{
				await _logger.HandleError(ex, Context,
					async embed => await ModifyOriginalResponseAsync(x => x.Embed = embed),
					logMessage: "Error creating schedule for guild {GuildId}, channel {ChannelId}, interval {Interval}, count {Count}",
					logArgs: [Context.Guild.Id, channel.Id, interval, count]);
			}
		}

		private static string MentionChannel(IChannel channel) => $"<#{channel.Id}>";

		private bool IsValidInterval(double hours) =>
			hours >= MIN_INTERVAL_HOURS && hours <= MAX_INTERVAL_HOURS;
	}

	[SlashCommand("getschedules", "Get all scheduled reports for this server")]
	public async Task HandleGetSchedulesCommand()
	{
		try
		{
			await DeferAsync(ephemeral: true);
			logger.LogInformation("Getting schedules for guild {GuildId}", Context.Guild.Id);
			var schedules = await db.GetGuildSchedules(Context.Guild.Id);

			var (description, components) = schedules.Count > 0 ?
				FormatSchedulesWithButtons(schedules) :
				("No scheduled reports found for this server.", null);

			logger.LogInformation("Found {Count} schedules for guild {GuildId}", schedules.Count, Context.Guild.Id);

			await ModifyOriginalResponseAsync(x => {
				x.Embed = Embeds.Info(description, "Scheduled Reports");
				x.Components = components;
			});
		}
		catch (Exception ex)
		{
			await logger.HandleError(ex, Context,
				async embed => await ModifyOriginalResponseAsync(x => x.Embed = embed),
				logMessage: "Error getting schedules for guild {GuildId}",
				logArgs: [Context.Guild.Id]);
		}
	}

	[ComponentInteraction("remove-schedule:*")]
	public async Task HandleRemoveScheduleButton(string id)
	{
		try
		{
			logger.LogInformation("Removing schedule {ScheduleId} for guild {GuildId} via button", id, Context.Guild.Id);

			var schedule = await db.GetScheduleById(id);

			if ((schedule == null) || (schedule.GuildId != Context.Guild.Id))
			{
				await RespondAsync(ephemeral: true, embed: Embeds.Error("Schedule not found."));
				return;
			}

			await db.DeleteSchedule(id);

			var schedules = await db.GetGuildSchedules(Context.Guild.Id);
			var (description, components) = schedules.Count > 0 ?
				FormatSchedulesWithButtons(schedules) :
				("No scheduled reports found for this server.", null);

			logger.LogInformation("Removed schedule {ScheduleId} for guild {GuildId} via button", id, Context.Guild.Id);

			var component = Context.Interaction as SocketMessageComponent;
			await component!.UpdateAsync(msg =>
			{
				msg.Embed = Embeds.Info(description, "Scheduled Reports");
				msg.Components = components;
			});
		}
		catch (Exception ex)
		{
			await logger.HandleError(ex, Context,
				async embed => await ModifyOriginalResponseAsync(x => x.Embed = embed),
				logMessage: "Error removing schedule {ScheduleId} for guild {GuildId} via button",
				logArgs: [id, Context.Guild.Id]);
		}
	}

	private (string Description, MessageComponent Components) FormatSchedulesWithButtons(List<ScheduledJob> schedules)
	{
		var sb = new StringBuilder();
		var builder = new ComponentBuilder();
		var row = 0;

		foreach (var (job, index) in schedules.Select((j, i) => (j, i)))
		{
			sb.AppendLine($"**Schedule #{index + 1}**");
			sb.AppendLine($"Channel: <#{job.ChannelId}>");
			sb.AppendLine($"Schedule: `{job.CronExpression}`");
			sb.AppendLine($"Interval: {job.IntervalHours:0.#}h");
			sb.AppendLine($"Messages: {job.Count}");
			if (job.IsForum && !string.IsNullOrEmpty(job.ThreadTitleTemplate))
			{
				sb.AppendLine($"Title Template: `{job.ThreadTitleTemplate}`");
			}
			sb.AppendLine($"Next Run: {job.NextRun:yyyy-MM-dd HH:mm:ss} UTC");
			sb.AppendLine();

			builder.WithButton($"Remove Schedule #{index + 1}",
				$"remove-schedule:{job.Id}",
				ButtonStyle.Danger,
				row: row++);
		}

		return (sb.ToString(), builder.Build());
	}
}
