#nullable enable

using Cronos;
using Discord.Interactions;
using System.Text;

[RequireContext(ContextType.Guild)]
[CommandContextType(InteractionContextType.Guild)]
public class ScheduleCommands(
	DbHelper db,
	ILogger<ScheduleCommands> logger) : InteractionModuleBase<SocketInteractionContext>
{
	[SlashCommand("getschedules", "Get all scheduled reports for this server")]
	public async Task HandleGetSchedulesCommand()
	{
		try
		{
			await DeferAsync(ephemeral: true);
			logger.LogInformation("Getting schedules for guild {GuildId}", Context.Guild.Id);
			var schedules = await db.GetGuildSchedules(Context.Guild.Id);

			var scheduleInfo = schedules.Count > 0 ?
				await FormatSchedulesWithButtons(schedules) :
				new ScheduleFormatResult("No scheduled reports found for this server.", null);

			logger.LogInformation("Found {Count} schedules for guild {GuildId}", schedules.Count, Context.Guild.Id);

			await ModifyOriginalResponseAsync(x =>
			{
				x.Embed = Embeds.Info(scheduleInfo.Description, "Scheduled Reports");
				x.Components = scheduleInfo.Components;
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
			var scheduleInfo = schedules.Count > 0 ?
				await FormatSchedulesWithButtons(schedules) :
				new ScheduleFormatResult("No scheduled reports found for this server.", null);

			logger.LogInformation("Removed schedule {ScheduleId} for guild {GuildId} via button", id, Context.Guild.Id);

			var component = Context.Interaction as SocketMessageComponent;
			await component!.UpdateAsync(msg =>
			{
				msg.Embed = Embeds.Info(scheduleInfo.Description, "Scheduled Reports");
				msg.Components = scheduleInfo.Components;
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

	[ComponentInteraction("edit-schedule:*")]
	public async Task HandleEditScheduleButton(string id)
	{
		try
		{
			var schedule = await db.GetScheduleById(id);
			if (schedule == null || schedule.GuildId != Context.Guild.Id)
			{
				await RespondAsync(ephemeral: true, embed: Embeds.Error("Schedule not found."));
				return;
			}

			var filters = await db.GetScheduleChannels(id);
			var includedChannels = filters.Where(f => !f.isExcluded)
				.Select(f => new SelectMenuDefaultValue(f.channelId, SelectDefaultValueType.Channel))
				.ToList();
			var excludedChannels = filters.Where(f => f.isExcluded)
				.Select(f => new SelectMenuDefaultValue(f.channelId, SelectDefaultValueType.Channel))
				.ToList();

			var builder = new ComponentBuilder()
				.WithSelectMenu(new SelectMenuBuilder()
					.WithCustomId($"add-include:{id}")
					.WithPlaceholder("Select channels to include")
					.WithMinValues(0)
					.WithMaxValues(25)
					.WithChannelTypes(ChannelType.Text, ChannelType.Forum)
					.WithDefaultValues([.. includedChannels])
					.WithType(ComponentType.ChannelSelect), row: 0)
				.WithSelectMenu(new SelectMenuBuilder()
					.WithCustomId($"add-exclude:{id}")
					.WithPlaceholder("Select channels to exclude")
					.WithMinValues(0)
					.WithMaxValues(25)
					.WithChannelTypes(ChannelType.Text, ChannelType.Forum)
					.WithDefaultValues([.. excludedChannels])
					.WithType(ComponentType.ChannelSelect), row: 1)
				.WithButton("Remove Schedule", $"remove-schedule:{id}", ButtonStyle.Danger, row: 2);

			var formattedSchedule = await FormatSingleSchedule(schedule, null);
			await RespondAsync(
				embed: Embeds.Info(formattedSchedule, "Edit Schedule"),
				components: builder.Build(),
				ephemeral: true);
		}
		catch (Exception ex)
		{
			await logger.HandleError(ex, Context,
				async embed => await RespondAsync(embed: embed, ephemeral: true),
				logMessage: "Error showing edit menu for schedule {ScheduleId}",
				logArgs: [id]);
		}
	}

	[ComponentInteraction("include-channels:*")]
	public async Task HandleIncludeChannelsButton(string id)
	{
		try
		{
			var schedule = await db.GetScheduleById(id);
			if (schedule == null || schedule.GuildId != Context.Guild.Id)
			{
				await RespondAsync(ephemeral: true, embed: Embeds.Error("Schedule not found."));
				return;
			}

			// Get existing channel filters
			var filters = await db.GetScheduleChannels(id);
			var includedChannels = filters.Where(f => !f.isExcluded)
				.Select(f => new SelectMenuDefaultValue(f.channelId, SelectDefaultValueType.Channel))
				.ToList();

			var builder = new ComponentBuilder()
				.WithSelectMenu(new SelectMenuBuilder()
					.WithCustomId($"add-include:{id}")
					.WithPlaceholder("Select channels to include")
					.WithMinValues(0)
					.WithMaxValues(25)
					.WithChannelTypes(ChannelType.Text, ChannelType.Forum)
					.WithDefaultValues([.. includedChannels])
					.WithType(ComponentType.ChannelSelect));

			await RespondAsync(
				embed: Embeds.Info("Select channels to include in this schedule. Messages from these channels will be considered for top reaction reports.", "Include Channels"),
				components: builder.Build(),
				ephemeral: true);
		}
		catch (Exception ex)
		{
			await logger.HandleError(ex, Context,
				async embed => await RespondAsync(embed: embed, ephemeral: true),
				logMessage: "Error showing include channels menu for schedule {ScheduleId}",
				logArgs: [id]);
		}
	}

	[ComponentInteraction("exclude-channels:*")]
	public async Task HandleExcludeChannelsButton(string id)
	{
		try
		{
			var schedule = await db.GetScheduleById(id);
			if (schedule == null || schedule.GuildId != Context.Guild.Id)
			{
				await RespondAsync(ephemeral: true, embed: Embeds.Error("Schedule not found."));
				return;
			}

			// Get existing channel filters
			var filters = await db.GetScheduleChannels(id);
			var excludedChannels = filters.Where(f => f.isExcluded)
				.Select(f => new SelectMenuDefaultValue(f.channelId, SelectDefaultValueType.Channel))
				.ToList();

			var builder = new ComponentBuilder()
				.WithSelectMenu(new SelectMenuBuilder()
					.WithCustomId($"add-exclude:{id}")
					.WithPlaceholder("Select channels to exclude")
					.WithMinValues(0)
					.WithMaxValues(25)
					.WithChannelTypes(ChannelType.Text, ChannelType.Forum)
					.WithDefaultValues([.. excludedChannels])
					.WithType(ComponentType.ChannelSelect));

			await RespondAsync(
				embed: Embeds.Info("Select channels to exclude from this schedule. Messages from these channels will NOT be considered for top reaction reports.", "Exclude Channels"),
				components: builder.Build(),
				ephemeral: true);
		}
		catch (Exception ex)
		{
			await logger.HandleError(ex, Context,
				async embed => await RespondAsync(embed: embed, ephemeral: true),
				logMessage: "Error showing exclude channels menu for schedule {ScheduleId}",
				logArgs: [id]);
		}
	}

	[ComponentInteraction("add-include:*")]
	public async Task HandleAddIncludeChannels(string id, IChannel[] selectedChannels)
	{
		await HandleAddChannels(id, selectedChannels, false);
	}

	[ComponentInteraction("add-exclude:*")]
	public async Task HandleAddExcludeChannels(string id, IChannel[] selectedChannels)
	{
		await HandleAddChannels(id, selectedChannels, true);
	}

	private async Task HandleAddChannels(string scheduleId, IChannel[] selectedChannels, bool isExcluded)
	{
		try
		{
			await DeferAsync(ephemeral: true);
			var schedule = await db.GetScheduleById(scheduleId);
			if (schedule == null || schedule.GuildId != Context.Guild.Id)
			{
				await ModifyOriginalResponseAsync(x => x.Embed = Embeds.Error("Schedule not found."));
				return;
			}

			// Get existing filters of the same type (included or excluded)
			var existingFilters = await db.GetScheduleChannels(scheduleId);
			var existingChannels = existingFilters
				.Where(f => f.isExcluded == isExcluded)
				.Select(f => f.channelId)
				.ToList();

			// Determine which channels to remove (they exist in DB but not in new selection)
			var selectedChannelIds = selectedChannels.Select(c => c.Id).ToList();
			var channelsToRemove = existingChannels
				.Where(channelId => !selectedChannelIds.Contains(channelId))
				.ToList();

			if (channelsToRemove.Any())
			{
				await db.RemoveScheduleChannels(scheduleId, channelsToRemove);
			}

			// Add new channel selections
			var channelFilters = selectedChannels
				.Select(c => (channelId: c.Id, isExcluded))
				.ToList();

			await db.AddScheduleChannels(scheduleId, channelFilters);

			// Update both the original message and show the updated channel list in the response
			try
			{
				if (Context.Interaction is IComponentInteraction interaction && interaction.Message != null)
				{
					var schedules = await db.GetGuildSchedules(Context.Guild.Id);
					var scheduleInfo = await FormatSchedulesWithButtons(schedules);

					await interaction.Message.ModifyAsync(msg =>
					{
						msg.Embed = Embeds.Info(scheduleInfo.Description, "Scheduled Reports");
						msg.Components = scheduleInfo.Components;
					});

					// Since we're showing the format in both places, we'll just show a blank embed
					// for the response since the user can see the changes in the main message
					await ModifyOriginalResponseAsync(x => x.Embed = null);
				}
				else
				{
					// If we can't update the original message, show the updated info in the response
					var formattedSchedule = await FormatSingleSchedule(schedule, 1);
					await ModifyOriginalResponseAsync(x => x.Embed = Embeds.Info(
						formattedSchedule,
						"Updated Channel Filters"));
				}
			}
			catch (Discord.Net.HttpException ex) when (ex.Message.Contains("Unknown Message"))
			{
				// Message no longer exists, show the updated info in the response
				var formattedSchedule = await FormatSingleSchedule(schedule, 1);
				await ModifyOriginalResponseAsync(x => x.Embed = Embeds.Info(
					formattedSchedule,
					"Updated Channel Filters"));
			}
		}
		catch (Exception ex)
		{
			await logger.HandleError(ex, Context,
				async embed => await ModifyOriginalResponseAsync(x => x.Embed = embed),
				logMessage: "Error adding channel filters for schedule {ScheduleId}, isExcluded {IsExcluded}",
				logArgs: [scheduleId, isExcluded]);
		}
	}

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

	private async Task<string> FormatSingleSchedule(ScheduledJob job, int? scheduleNumber)
	{
		var sb = new StringBuilder();
		if (scheduleNumber.HasValue)
		{
			sb.AppendLine($"**Schedule #{scheduleNumber}**");
		}
		sb.AppendLine($"Channel: <#{job.ChannelId}>");
		sb.AppendLine($"Schedule: `{job.CronExpression}`");
		sb.AppendLine($"Interval: {job.IntervalHours:0.#}h");
		sb.AppendLine($"Messages: {job.Count}");
		if (job.IsForum && !string.IsNullOrEmpty(job.ThreadTitleTemplate))
		{
			sb.AppendLine($"Title Template: `{job.ThreadTitleTemplate}`");
		}
		sb.AppendLine($"Next Run: {job.NextRun:yyyy-MM-dd HH:mm:ss} UTC");

		// Add channel filters if any exist
		var filters = await db.GetScheduleChannels(job.Id.ToString());
		if (filters.Any())
		{
			var included = filters.Where(f => !f.isExcluded).ToList();
			var excluded = filters.Where(f => f.isExcluded).ToList();

			if (included.Any())
			{
				sb.AppendLine("\n📥 **Included Channels**:");
				foreach (var filter in included)
				{
					sb.AppendLine($"- <#{filter.channelId}>");
				}
			}

			if (excluded.Any())
			{
				sb.AppendLine("\n📤 **Excluded Channels**:");
				foreach (var filter in excluded)
				{
					sb.AppendLine($"- <#{filter.channelId}>");
				}
			}
		}
		sb.AppendLine();
		return sb.ToString();
	}

	private async Task<ScheduleFormatResult> FormatSchedulesWithButtons(List<ScheduledJob> schedules)
	{
		var sb = new StringBuilder();
		var builder = new ComponentBuilder();

		foreach (var (job, index) in schedules.Select((j, i) => (j, i)))
		{
			var scheduleNumber = index + 1;
			sb.Append(await FormatSingleSchedule(job, scheduleNumber));

			// Add edit button for this schedule
			builder.WithButton($"Edit Schedule #{scheduleNumber}", $"edit-schedule:{job.Id}", ButtonStyle.Primary);
		}

		return new ScheduleFormatResult(sb.ToString(), builder.Build());
	}

	private record ScheduleFormatResult(string Description, MessageComponent? Components);
}
