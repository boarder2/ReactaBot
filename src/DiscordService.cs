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
			
			// Register the slash command
			var guildCommand = new SlashCommandBuilder()
				.WithName("top")
				.WithDescription("Get top reacted messages")
				.AddOption("date", ApplicationCommandOptionType.String, "Date in YYYY-MM-DD format - Defaults to today", isRequired: false)
				.AddOption("user", ApplicationCommandOptionType.User, "Filter by user", isRequired: false)
				.AddOption("limit", ApplicationCommandOptionType.Integer, "Number of messages to show (1-50) - Defaults to 10", isRequired: false);

			try
			{
				await _client.CreateGlobalApplicationCommandAsync(guildCommand.Build());
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error registering slash command");
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
		if (command.CommandName == "top")
		{
			await command.DeferAsync(); // Defer the response since it might take some time
			await _reactionService.PrintTopReactions(_client, command);
		}
	}
}