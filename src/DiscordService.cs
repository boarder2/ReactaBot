namespace reactabot;

public class DiscordService(ILogger<DiscordService> _logger, AppConfiguration _config, DbHelper _db, ReactionsService _reactionService) : IHostedService
{
	private DiscordSocketClient _client;

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		_client = new DiscordSocketClient(new DiscordSocketConfig()
		{
			GatewayIntents =
				GatewayIntents.DirectMessages |
				GatewayIntents.GuildEmojis |
				GatewayIntents.GuildIntegrations |
				GatewayIntents.GuildMessageReactions |
				GatewayIntents.GuildMessages |
				GatewayIntents.Guilds |
				GatewayIntents.MessageContent
		}
		);

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
					.WithDescription("Check if your reactions to your messages are being tracked by the bot")
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
		}
	}
}