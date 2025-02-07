using Discord.Interactions;

namespace reactabot;

public class DiscordService(
	DiscordSocketClient _client, 
	ILogger<DiscordService> _logger, 
	AppConfiguration _config, 
	ReactionsService _reactionService,
	InteractionService _interactionService,
	IServiceProvider _services) : IHostedService
{
	private bool _commandsRegistered;

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		_client.Log += Log;
		_client.InteractionCreated += HandleInteraction;

		// Add InteractionService error logging
		_interactionService.SlashCommandExecuted += SlashCommandExecuted;

		// Add handlers for reaction events
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

			if (!_commandsRegistered)
			{
				// Register modules and commands with the service provider
				await _interactionService.AddModuleAsync<OptCommand>(_services);
				await _interactionService.AddModuleAsync<ScheduleCommands>(_services);
				await _interactionService.AddModuleAsync<AdminCommands>(_services);
				await _interactionService.AddModuleAsync<TopCommands>(_services);

				try
				{
					await Task.Delay(2000); // Small delay to ensure Discord is fully ready
					var commands = await _interactionService.RegisterCommandsGloballyAsync(true); // Added true to overwrite existing commands
					_logger.LogInformation($"{commands.Count} Slash commands registered globally: {string.Join(',', commands.Select(x => x.Name))}");
					_commandsRegistered = true;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error registering slash commands");
					throw; // Rethrow to prevent the bot from starting with unregistered commands
				}
			}
		};

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

	private async Task HandleInteraction(SocketInteraction interaction)
	{
		try
		{
			var context = new SocketInteractionContext(_client, interaction);
			await _interactionService.ExecuteCommandAsync(context, _services);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error handling interaction");
			if (interaction.Type == InteractionType.ApplicationCommand)
			{
				if (interaction.HasResponded)
				{
					await interaction.ModifyOriginalResponseAsync(msg => 
						msg.Content = "An error occurred while processing the command.");
				}
				else
				{
					await interaction.RespondAsync("An error occurred while processing the command.", 
						ephemeral: true);
				}
			}
		}
	}

	private Task SlashCommandExecuted(SlashCommandInfo info, IInteractionContext context, IResult result)
	{
		if (!result.IsSuccess)
		{
			_logger.LogError("Slash command {CommandName} failed: {ErrorReason} error: {Error}", 
				info.Name, result.ErrorReason, result.Error);
		}
		return Task.CompletedTask;
	}
}