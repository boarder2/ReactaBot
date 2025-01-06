using System.Text;
using Cronos;

namespace reactabot;

public class DiscordService(DiscordSocketClient _client, ILogger<DiscordService> _logger, AppConfiguration _config, DbHelper _db, ReactionsService _reactionService) : IHostedService
{
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
					.WithDescription("Get top reacted messages")
					.AddOption("date", ApplicationCommandOptionType.String, "Date in YYYY-MM-DD format - Defaults to today", isRequired: false, isAutocomplete:true)
					.AddOption("channel", ApplicationCommandOptionType.Channel, "Filter by channel", isRequired: false, isAutocomplete: true)
					.AddOption("user", ApplicationCommandOptionType.User, "Filter by user", isRequired: false, isAutocomplete: true)
					.AddOption("limit", ApplicationCommandOptionType.Integer, "Number of messages to show (1-50) - Defaults to 10", isRequired: false),
				new SlashCommandBuilder()
					.WithName("optout")
					.WithDescription("Opt out of having reactions to your reactions tracked by the bot"),
				new SlashCommandBuilder()
					.WithName("optin")
					.WithDescription("Opt back in to having reactions to your messages tracked by the bot"),
				new SlashCommandBuilder()
					.WithName("optstatus")
					.WithDescription("Check if your reactions to your messages are being tracked by the bot"),
				new SlashCommandBuilder()
					.WithName("schedule")
					.WithDescription("Schedule recurring top messages report")
					.AddOption("cron", ApplicationCommandOptionType.String, "Cron expression for scheduling (e.g. '0 */4 * * *')", isRequired: true)
					.AddOption("interval", ApplicationCommandOptionType.String, "Time interval to analyze (1h,4h,8h,12h,24h,2d,3d,5d,7d)", isRequired: true)
					.AddOption("channel", ApplicationCommandOptionType.Channel, "Channel to post results", isRequired: true)
					.AddOption("count", ApplicationCommandOptionType.Integer, "Number of messages to show (1-50)", isRequired: true),
				new SlashCommandBuilder()
					.WithName("getschedules")
					.WithDescription("Get all scheduled reports for this server"),
				new SlashCommandBuilder()
					.WithName("removeschedule")
					.WithDescription("Remove a scheduled report")
					.AddOption("id", ApplicationCommandOptionType.String, "The ID of the schedule to remove", isRequired: true)
			};

			try
			{
				foreach (var command in commands)
				{
					await _client.CreateGlobalApplicationCommandAsync(command.Build());
				}
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
				await command.DeferAsync();
				await _reactionService.PrintTopReactions(_client, command);
				break;
			case "optout":
				await _db.OptOutUser(command.User.Id);
				await command.RespondAsync("You have been opted out. Reactions to your messages will no longer be tracked. Any existing tracked messages have been removed.", ephemeral: true);
				break;
			case "optin":
				await _db.OptInUser(command.User.Id);
				await command.RespondAsync("You have been opted back in. Reactions to your messages will now be tracked.", ephemeral: true);
				break;
			case "optstatus":
				bool isOptedOut = await _db.IsUserOptedOut(command.User.Id);
				string status = isOptedOut 
					? "You are currently opted out. Reactions to your messages are not being tracked." 
					: "You are currently opted in. Reactions to your messages are being tracked.";
				await command.RespondAsync(status, ephemeral: true);
				break;
			case "schedule":
				await HandleScheduleCommand(command);
				break;
			case "getschedules":
				await HandleGetSchedulesCommand(command);
				break;
			case "removeschedule":
				await HandleRemoveScheduleCommand(command);
				break;
		}
	}

	private async Task HandleScheduleCommand(SocketSlashCommand command)
	{
		var cronExpr = (string)command.Data.Options.First(x => x.Name == "cron").Value;
		var interval = (string)command.Data.Options.First(x => x.Name == "interval").Value;
		var channel = (ITextChannel)command.Data.Options.First(x => x.Name == "channel").Value;
		var count = (long)command.Data.Options.First(x => x.Name == "count").Value;

		// Validate inputs
		if (!IsValidInterval(interval))
		{
			await command.RespondAsync("Invalid interval. Use: 1h,4h,8h,12h,24h,2d,3d,5d,7d", ephemeral: true);
			return;
		}

		try
		{
			CronExpression.Parse(cronExpr);
		}
		catch
		{
			await command.RespondAsync("Invalid cron expression", ephemeral: true);
			return;
		}

		if (count < 1 || count > 50)
		{
			await command.RespondAsync("Count must be between 1 and 50", ephemeral: true);
			return;
		}

		var expression = CronExpression.Parse(cronExpr);
		var nextRun = expression.GetNextOccurrence(DateTime.UtcNow);
		if (!nextRun.HasValue)
		{
			await command.RespondAsync("Invalid cron expression - could not determine next run time", ephemeral: true);
			return;
		}

		var job = new ScheduledJob
		{
			CronExpression = cronExpr,
			Interval = interval,
			ChannelId = channel.Id,
			GuildId = channel.GuildId,
			Count = (int)count,
			NextRun = nextRun.Value,
			CreatedAt = DateTime.UtcNow
		};

		await _db.CreateScheduledJob(job);
		await command.RespondAsync(
			$"Scheduled job created. Will post top {count} messages for the last {interval} to {channel.Mention}\n" +
			$"Schedule: {cronExpr}\nNext run: {nextRun.Value:yyyy-MM-dd HH:mm:ss} UTC", 
			ephemeral: true);
	}

	private async Task HandleGetSchedulesCommand(SocketSlashCommand command)
	{
		if (!command.GuildId.HasValue)
		{
			await command.RespondAsync("This command can only be used in servers!", ephemeral: true);
			return;
		}

		var schedules = await _db.GetGuildSchedules(command.GuildId.Value);
		if (!schedules.Any())
		{
			await command.RespondAsync("No scheduled reports found for this server.", ephemeral: true);
			return;
		}

		var response = FormatSchedules(schedules);
		await command.RespondAsync(response, ephemeral: true);
	}

	private async Task HandleRemoveScheduleCommand(SocketSlashCommand command)
	{
		if (!command.GuildId.HasValue)
		{
			await command.RespondAsync("This command can only be used in servers!", ephemeral: true);
			return;
		}

		var id = (string)command.Data.Options.First(x => x.Name == "id").Value;
		if (!Guid.TryParse(id, out _))
		{
			await command.RespondAsync("Invalid schedule ID format.", ephemeral: true);
			return;
		}

		var schedule = await _db.GetScheduleById(id);

		if (schedule == null)
		{
			await command.RespondAsync("Schedule not found.", ephemeral: true);
			return;
		}

		// Check if user has permission to delete this schedule
		var guildUser = command.User as SocketGuildUser;
		if (!guildUser.GuildPermissions.ManageMessages && !guildUser.GuildPermissions.Administrator)
		{
			await command.RespondAsync("You need the Manage Messages permission to remove schedules.", ephemeral: true);
			return;
		}

		if (schedule.GuildId != command.GuildId.Value)
		{
			await command.RespondAsync("That schedule belongs to a different server.", ephemeral: true);
			return;
		}

		await _db.DeleteSchedule(id);
		await command.RespondAsync($"Schedule {id} has been removed.", ephemeral: true);
	}

	private string FormatSchedules(List<ScheduledJob> schedules)
	{
		var sb = new StringBuilder("Scheduled Reports:\n\n");
		foreach (var job in schedules)
		{
			sb.AppendLine($"**ID: `{job.Id}`**");
			sb.AppendLine($"Channel: <#{job.ChannelId}>");
			sb.AppendLine($"Schedule: `{job.CronExpression}`");
			sb.AppendLine($"Interval: {job.Interval}");
			sb.AppendLine($"Messages: {job.Count}");
			sb.AppendLine($"Next Run: {job.NextRun:yyyy-MM-dd HH:mm:ss} UTC");
			sb.AppendLine();
		}
		return sb.ToString();
	}

	private bool IsValidInterval(string interval)
	{
		return new[] { "1h", "4h", "8h", "12h", "24h", "2d", "3d", "5d", "7d" }.Contains(interval);
	}
}