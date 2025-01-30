using System.Text;
using Cronos;

namespace reactabot;

public class DiscordService(DiscordSocketClient _client, ILogger<DiscordService> _logger, AppConfiguration _config, DbHelper _db, ReactionsService _reactionService) : IHostedService
{
	private const double MIN_INTERVAL_HOURS = 0.5;
	private const double MAX_INTERVAL_HOURS = 168; // 7 days

	public async Task StartAsync(CancellationToken cancellationToken)
	{

		_client.Log += Log;
		_client.ReactionAdded += async (message, channel, reaction) =>
		{
			var msg = await message.GetOrDownloadAsync();
			await _reactionService.UpdateMessageReactions(msg);
		};

		_client.ReactionRemoved += async (message, channel, reaction) =>
		{
			var msg = await message.GetOrDownloadAsync();
			await _reactionService.UpdateMessageReactions(msg);
		};

		_client.Ready += async () =>
		{
			_logger.LogInformation($"Logged in as {_client.CurrentUser.Username}");

			// Register the slash commands
			var commands = new SlashCommandBuilder[]
			{
				new SlashCommandBuilder()
					.WithName("top")
					.WithContextTypes(InteractionContextType.Guild)
					.WithDescription("Get top reacted messages")
					.AddOption("date", ApplicationCommandOptionType.String, "Date in YYYY-MM-DD format - Defaults to today", isRequired: false, isAutocomplete:true)
					.AddOption("channel", ApplicationCommandOptionType.Channel, "Filter by channel", isRequired: false, isAutocomplete: true)
					.AddOption("user", ApplicationCommandOptionType.User, "Filter by user", isRequired: false, isAutocomplete: true)
					.AddOption("limit", ApplicationCommandOptionType.Integer, "Number of messages to show (1-10) - Defaults to 10", isRequired: false),
				new SlashCommandBuilder()
					.WithName("optout")
					.WithDescription("Opt out of having reactions to your messages tracked by the bot"),
				new SlashCommandBuilder()
					.WithName("optin")
					.WithDescription("Opt back in to having reactions to your messages tracked by the bot"),
				new SlashCommandBuilder()
					.WithName("optstatus")
					.WithDescription("Check if your reactions to your messages are being tracked by the bot"),
				new SlashCommandBuilder()
					.WithName("schedule")
					.WithContextTypes(InteractionContextType.Guild)
					.WithDescription("Manage scheduled reports")
					.AddOption(new SlashCommandOptionBuilder()
						.WithName("channel")
						.WithDescription("Schedule recurring top messages report for a channel")
						.WithType(ApplicationCommandOptionType.SubCommand)
						.AddOption("cron", ApplicationCommandOptionType.String, "Cron expression for scheduling (e.g. '0 */4 * * *')", isRequired: true)
						.AddOption(new SlashCommandOptionBuilder()
							.WithName("interval")
							.WithRequired(true)
							.WithType(ApplicationCommandOptionType.Number)
							.WithDescription($"Time interval to analyze in hours ({MIN_INTERVAL_HOURS}-{MAX_INTERVAL_HOURS})")
							.WithMinValue((double)MIN_INTERVAL_HOURS)
							.WithMaxValue((double)MAX_INTERVAL_HOURS))
						.AddOption("channel", ApplicationCommandOptionType.Channel, "Channel to post results", isRequired: true, channelTypes: new List<ChannelType> { ChannelType.Text })
						.AddOption("count", ApplicationCommandOptionType.Integer, "Number of messages to show (1-20)", isRequired: true))
					.AddOption(new SlashCommandOptionBuilder()
						.WithName("forum")
						.WithDescription("Schedule recurring top messages report in a forum")
						.WithType(ApplicationCommandOptionType.SubCommand)
						.AddOption("cron", ApplicationCommandOptionType.String, "Cron expression for scheduling (e.g. '0 */4 * * *')", isRequired: true)
						.AddOption(new SlashCommandOptionBuilder()
							.WithName("interval")
							.WithRequired(true)
							.WithType(ApplicationCommandOptionType.Number)
							.WithDescription($"Time interval to analyze in hours ({MIN_INTERVAL_HOURS}-{MAX_INTERVAL_HOURS})")
							.WithMinValue((double)MIN_INTERVAL_HOURS)
							.WithMaxValue((double)MAX_INTERVAL_HOURS))
						.AddOption("forum", ApplicationCommandOptionType.Channel, "Forum channel to post results", isRequired: true, channelTypes: new List<ChannelType> { ChannelType.Forum })
						.AddOption("count", ApplicationCommandOptionType.Integer, "Number of messages to show (1-20)", isRequired: true)
						.AddOption("title", ApplicationCommandOptionType.String, "Thread title template. Variables: {date:format}, {count}, {interval}. Date format is optional.", isRequired: true)),
				new SlashCommandBuilder()
					.WithName("getschedules")
					.WithContextTypes(InteractionContextType.Guild)
					.WithDescription("Get all scheduled reports for this server"),
				new SlashCommandBuilder()
					.WithName("removeschedule")
					.WithContextTypes(InteractionContextType.Guild)
					.WithDescription("Remove a scheduled report")
					.AddOption("id", ApplicationCommandOptionType.String, "The ID of the schedule to remove", isRequired: true),
				new SlashCommandBuilder()
					.WithName("delete")
					.WithContextTypes(InteractionContextType.Guild)
					.WithDescription("Delete stored reactions")
					.AddOption("channel", ApplicationCommandOptionType.Channel, "Delete reactions from this channel", isRequired: false)
					.AddOption("user", ApplicationCommandOptionType.User, "Delete reactions from this user", isRequired: false),
				new SlashCommandBuilder()
					.WithName("version")
					.WithDescription("Get the current version of the bot"),
			};

			try
			{
				await _client.BulkOverwriteGlobalApplicationCommandsAsync(commands.Select(x => x.Build()).ToArray());
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error registering slash commands");
			}
		};

		_client.SlashCommandExecuted += HandleSlashCommand;

		LogContext.Push(
			 new PropertyEnricher("BotVersion", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString())
		);

		_logger.LogInformation("Bot starting.");
		await _client.LoginAsync(TokenType.Bot, _config.Token);
		await _client.StartAsync();
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		await _client.LogoutAsync();
		await _client.StopAsync();
		_client.Dispose();
	}

	public ConnectionState GetConnectionState()
	{
		return _client?.ConnectionState ?? ConnectionState.Disconnected;
	}

	private Task Log(LogMessage msg)
	{
		_logger.LogInformation(msg.ToString());
		return Task.CompletedTask;
	}


	private async Task HandleSlashCommand(SocketSlashCommand command)
	{
		switch (command.CommandName)
		{
			case "top":
				await command.DeferAsync(ephemeral: true);
				await _reactionService.PrintTopReactions(_client, command);
				break;
			case "optout":
				await command.DeferAsync(ephemeral: true);
				await _db.OptOutUser(command.User.Id);
				await command.ModifyOriginalResponseAsync(x => x.Embed =
					new EmbedBuilder()
						.WithTitle("Opt Out Success")
						.WithColor(Color.LightOrange)
						.WithDescription("You have been opted out. Reactions to your messages will no longer be tracked. Any existing tracked messages have been removed.")
						.Build());
				break;
			case "optin":
				await command.DeferAsync(ephemeral: true);
				await _db.OptInUser(command.User.Id);
				await command.ModifyOriginalResponseAsync(x => x.Embed =
					new EmbedBuilder()
						.WithTitle("Opt In Success")
						.WithColor(Color.Green)
						.WithDescription("You have been opted back in. Reactions to your messages will now be tracked.")
						.Build());
				break;
			case "optstatus":
				await command.DeferAsync(ephemeral: true);
				bool isOptedOut = await _db.IsUserOptedOut(command.User.Id);
				string status = isOptedOut
					? "You are currently opted out. Reactions to your messages are not being tracked."
					: "You are currently opted in. Reactions to your messages are being tracked.";
				await command.ModifyOriginalResponseAsync(x => x.Embed =
					new EmbedBuilder()
						.WithTitle("Opt In Status")
						.WithColor(isOptedOut ? Color.LightOrange : Color.Green)
						.WithDescription(status)
						.Build());
				break;
			case "schedule":
				var subCommand = command.Data.Options.First().Name;
				switch (subCommand)
				{
					case "channel":
						await HandleScheduleChannelCommand(command);
						break;
					case "forum":
						await HandleScheduleForumCommand(command);
						break;
				}
				break;
			case "getschedules":
				await HandleGetSchedulesCommand(command);
				break;
			case "removeschedule":
				await HandleRemoveScheduleCommand(command);
				break;
			case "delete":
				await HandleDeleteCommand(command);
				break;
			case "version":
				await command.DeferAsync(ephemeral: true);
				var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
				await command.ModifyOriginalResponseAsync(x => x.Embed = SuccessEmbed(
					$"Bot Version: `{version}`", 
					"Version Information"));
				break;
		}
	}

	private async Task HandleScheduleChannelCommand(SocketSlashCommand command)
	{
		await command.DeferAsync(ephemeral: true);
		var options = command.Data.Options.First().Options;
		var cronExpr = (string)options.First(x => x.Name == "cron").Value;
		var interval = (double)options.First(x => x.Name == "interval").Value;
		var channel = (ITextChannel)options.First(x => x.Name == "channel").Value;
		var count = (long)options.First(x => x.Name == "count").Value;

		// Validate inputs
		if (!IsValidInterval(interval))
		{
			await command.ModifyOriginalResponseAsync(x => x.Embed = ErrorEmbed($"Invalid interval. Must be between {MIN_INTERVAL_HOURS} and {MAX_INTERVAL_HOURS} hours."));
			return;
		}

		try
		{
			CronExpression.Parse(cronExpr);
		}
		catch
		{
			await command.ModifyOriginalResponseAsync(x => x.Embed = ErrorEmbed("Invalid cron expression"));
			return;
		}

		if (count < 1 || count > 20)
		{
			await command.ModifyOriginalResponseAsync(x => x.Embed = ErrorEmbed("Count must be between 1 and 20"));
			return;
		}

		var expression = CronExpression.Parse(cronExpr);
		var nextRun = expression.GetNextOccurrence(DateTime.UtcNow);
		if (!nextRun.HasValue)
		{
			await command.ModifyOriginalResponseAsync(x => x.Embed = ErrorEmbed("Invalid cron expression - could not determine next run time"));
			return;
		}

		var job = new ScheduledJob
		{
			CronExpression = cronExpr,
			IntervalHours = interval,
			ChannelId = channel.Id,
			GuildId = channel.GuildId,
			Count = (int)count,
			NextRun = nextRun.Value,
			CreatedAt = DateTime.UtcNow
		};

		await _db.CreateScheduledJob(job);
		await command.ModifyOriginalResponseAsync(x => x.Embed = SuccessEmbed(
			$"Scheduled job created. Will post top {count} messages for the last `{interval}` to {channel.Mention}\n" +
			$"Schedule: `{cronExpr}`\nNext run: {nextRun.Value:yyyy-MM-dd HH:mm:ss} UTC"));
	}

	private async Task HandleScheduleForumCommand(SocketSlashCommand command)
	{
		await command.DeferAsync(ephemeral: true);
		var options = command.Data.Options.First().Options;
		var cronExpr = (string)options.First(x => x.Name == "cron").Value;
		var interval = (double)options.First(x => x.Name == "interval").Value;
		var forumChannel = (IForumChannel)options.First(x => x.Name == "forum").Value;
		var count = (long)options.First(x => x.Name == "count").Value;
		var titleTemplate = (string)options.First(x => x.Name == "title").Value;

		if (!IsValidInterval(interval))
		{
			await command.ModifyOriginalResponseAsync(x => x.Embed = ErrorEmbed($"Invalid interval. Must be between {MIN_INTERVAL_HOURS} and {MAX_INTERVAL_HOURS} hours."));
			return;
		}

		try
		{
			CronExpression.Parse(cronExpr);
		}
		catch
		{
			await command.ModifyOriginalResponseAsync(x => x.Embed = ErrorEmbed("Invalid cron expression"));
			return;
		}

		if (count < 1 || count > 20)
		{
			await command.ModifyOriginalResponseAsync(x => x.Embed = ErrorEmbed("Count must be between 1 and 20"));
			return;
		}

		var expression = CronExpression.Parse(cronExpr);
		var nextRun = expression.GetNextOccurrence(DateTime.UtcNow);
		if (!nextRun.HasValue)
		{
			await command.ModifyOriginalResponseAsync(x => x.Embed = ErrorEmbed("Invalid cron expression - could not determine next run time"));
			return;
		}

		var job = new ScheduledJob
		{
			CronExpression = cronExpr,
				IntervalHours = interval,
			ChannelId = forumChannel.Id,
			GuildId = forumChannel.GuildId,
			Count = (int)count,
			NextRun = nextRun.Value,
			CreatedAt = DateTime.UtcNow,
			IsForum = true,
			ThreadTitleTemplate = titleTemplate
		};

		await _db.CreateScheduledJob(job);
		await command.ModifyOriginalResponseAsync(x => x.Embed = SuccessEmbed(
			$"Scheduled forum job created. Will post top {count} messages for the last `{interval:0.#}h` to {forumChannel.Mention}\n" +
			$"Schedule: `{cronExpr}`\nTitle Template: `{titleTemplate}`\nNext run: {nextRun.Value:yyyy-MM-dd HH:mm:ss} UTC"));
	}

	private async Task HandleGetSchedulesCommand(SocketSlashCommand command)
	{
		await command.DeferAsync(ephemeral: true);
		var schedules = await _db.GetGuildSchedules(command.GuildId.Value);
		if (schedules.Count == 0)
		{
			await command.ModifyOriginalResponseAsync(x => x.Embed = SuccessEmbed("No scheduled reports found for this server."));
			return;
		}

		var response = FormatSchedules(schedules);

		var eb = new EmbedBuilder()
			.WithTitle("Scheduled Reports")
			.WithDescription(response)
			.WithColor(Color.Blue);

		await command.ModifyOriginalResponseAsync(x => x.Embeds = new[] { eb.Build() });
	}

	private async Task HandleRemoveScheduleCommand(SocketSlashCommand command)
	{
		await command.DeferAsync(ephemeral: true);

		var id = (string)command.Data.Options.First(x => x.Name == "id").Value;
		if (!Guid.TryParse(id, out _))
		{
			await command.ModifyOriginalResponseAsync(x => x.Embed = ErrorEmbed("Invalid schedule ID format."));
			return;
		}

		var schedule = await _db.GetScheduleById(id);

		if ((schedule == null) || (schedule.GuildId != command.GuildId.Value))
		{
			await command.ModifyOriginalResponseAsync(x => x.Embed = ErrorEmbed("Schedule not found."));
			return;
		}

		await _db.DeleteSchedule(id);
		await command.ModifyOriginalResponseAsync(x => x.Embed = SuccessEmbed($"Schedule {id} has been removed."));
	}

	private async Task HandleDeleteCommand(SocketSlashCommand command)
	{
		await command.DeferAsync(ephemeral: true);

		var channel = command.Data.Options.FirstOrDefault(x => x.Name == "channel")?.Value as ITextChannel;
		var user = command.Data.Options.FirstOrDefault(x => x.Name == "user")?.Value as SocketUser;

		if (channel == null && user == null)
		{
			await command.ModifyOriginalResponseAsync(x => x.Embed = ErrorEmbed("You must specify at least one of: channel or user"));
			return;
		}

		var count = await _db.DeleteMessages(command.GuildId.Value, channel?.Id, user?.Id);

		var response = new StringBuilder();
		if (channel != null) response.AppendLine($"\nChannel: {channel.Mention}");
		if (user != null) response.AppendLine($"\nUser: {user.Mention}");
		response.AppendLine($"\nTotal messages and reactions deleted: {count}");

		await command.ModifyOriginalResponseAsync(x => x.Embed = 
			new EmbedBuilder()
				.WithTitle("Deleted Reactions and Messages")
				.WithColor(Color.Red)
				.WithDescription(response.ToString())
				.Build());
	}

	private string FormatSchedules(List<ScheduledJob> schedules)
	{
		var sb = new StringBuilder();
		foreach (var job in schedules)
		{
			sb.AppendLine($"**ID: `{job.Id}`**");
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
		}
		return sb.ToString();
	}

	private bool IsValidInterval(double hours) => 
		hours >= MIN_INTERVAL_HOURS && hours <= MAX_INTERVAL_HOURS;


	private Embed ErrorEmbed(string message, string title = "Error")
	{
		return new EmbedBuilder()
			.WithTitle(title)
			.WithDescription(message)
			.WithColor(Color.Red)
			.Build();
	}

	private Embed SuccessEmbed(string message, string title = "Success")
	{
		return new EmbedBuilder()
			.WithTitle(title)
			.WithDescription(message)
			.WithColor(Color.Green)
			.Build();
	}
}