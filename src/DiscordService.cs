using System.Text;
using System.Text.Json;
using Dapper;

namespace reactabot;

public class DiscordService(ILogger<DiscordService> _logger, AppConfiguration _config, DbHelper _db) : IHostedService
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
			await UpdateMessageReactions(msg);
		};

		_client.ReactionRemoved += async (message, channel, reaction) =>
		{
			var msg = await message.GetOrDownloadAsync();
			await UpdateMessageReactions(msg);
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

	private async Task UpdateMessageReactions(IMessage msg)
	{
		using var connection = _db.GetConnection();
		await connection.OpenAsync();
		using var transaction = connection.BeginTransaction();

		try
		{
			if (!msg.Reactions.Any())
			{
				// If no reactions, delete everything for this message
				var deleteReactionsSql = "DELETE FROM reactions WHERE message_id = @MessageId";
				var deleteMessageSql = "DELETE FROM messages WHERE id = @MessageId";
				await connection.ExecuteAsync(deleteReactionsSql, new { MessageId = msg.Id }, transaction);
				await connection.ExecuteAsync(deleteMessageSql, new { MessageId = msg.Id }, transaction);
				transaction.Commit();
				_logger.LogInformation("Deleted message {MessageId} due to no reactions", msg.Id);
				return;
			}

			var totalReactions = msg.Reactions.Sum(r => r.Value.ReactionCount);

			// First ensure the message exists in DB
			var messageSql = """
			INSERT OR REPLACE INTO messages(id, timestamp, author, url, total_reactions) 
			VALUES(@Id, @Timestamp, @Author, @Url, @TotalReactions);
			""";
			await connection.ExecuteAsync(messageSql, new
			{
				msg.Timestamp,
				Author = msg.Author.Id,
				Url = msg.GetJumpUrl(),
				msg.Id,
				TotalReactions = totalReactions
			}, transaction);

			// Delete existing reactions
			var deleteSql = "DELETE FROM reactions WHERE message_id = @MessageId";
			await connection.ExecuteAsync(deleteSql, new { MessageId = msg.Id }, transaction);

			// Insert current reactions
			var insertSql = "INSERT INTO reactions(message_id, emoji, reaction_count) VALUES(@MessageId, @Emoji, @ReactionCount)";
			foreach (var react in msg.Reactions)
			{
				await connection.ExecuteAsync(insertSql, new
				{
					MessageId = msg.Id,
					Emoji = react.Key.Name,
					react.Value.ReactionCount
				}, transaction);
			}

			transaction.Commit();
			_logger.LogInformation("Updated reactions for message {MessageId}", msg.Id);
		}
		catch (Exception ex)
		{
			transaction.Rollback();
			_logger.LogError(ex, "Failed to update reactions for message {MessageId}", msg.Id);
		}
	}

	private async Task<string> GetMessagePreview(string url)
	{
		try
		{
			var messageLink = new Uri(url);
			var pathSegments = messageLink.AbsolutePath.Split('/').Where(s => !string.IsNullOrEmpty(s)).ToArray();

			if (pathSegments.Length >= 3 &&
				ulong.TryParse(pathSegments[2], out var channelId) &&
				ulong.TryParse(pathSegments[3], out var messageId))
			{
				var channel = await _client.GetChannelAsync(channelId) as ITextChannel;
				var originalMessage = channel != null ? await channel.GetMessageAsync(messageId) : null;

				if (originalMessage != null)
				{
					return originalMessage.Content.Length > 100
						? originalMessage.Content.Substring(0, 97) + "..."
						: originalMessage.Content;
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Error processing message URL: {url}", url);
		}
		return "(Message content unavailable)";
	}

	private async Task HandleSlashCommand(SocketSlashCommand command)
	{
		if (command.CommandName == "top")
		{
			await command.DeferAsync(); // Defer the response since it might take some time

			var date = DateTimeOffset.UtcNow;
			var limit = 10;
			ulong? userId = null;

			var dateOption = command.Data.Options.FirstOrDefault(x => x.Name == "date");
			var limitOption = command.Data.Options.FirstOrDefault(x => x.Name == "limit");
			var userOption = command.Data.Options.FirstOrDefault(x => x.Name == "user");

			if (dateOption != null && !DateTimeOffset.TryParse((string)dateOption.Value, out date))
			{
				await command.ModifyOriginalResponseAsync(msg => 
					msg.Content = "Invalid date format. Please use YYYY-MM-DD");
				return;
			}

			if (limitOption != null)
			{
				var longLimit = (long)limitOption.Value;
				limit = (int)Math.Clamp(longLimit, 1, 50);
				if (longLimit != limit)
				{
					await command.ModifyOriginalResponseAsync(msg => 
						msg.Content = "Number must be between 1 and 50");
					return;
				}
			}

			if (userOption != null)
			{
				userId = ((IUser)userOption.Value).Id;
			}

			var topMessages = await _db.GetTopMessages(date, limit, userId);
			if (!topMessages.Any())
			{
				await command.ModifyOriginalResponseAsync(msg => 
					msg.Content = $"No messages found for {date:MMMM d, yyyy}");
				return;
			}

			var response = new StringBuilder($"Top {limit} messages for {date:MMMM d, yyyy}:\n\n");

			var rank = 1;
			foreach (var (url, authorId, total, reactions) in topMessages)
			{
				response.AppendLine($"#{rank}. <@{authorId}> - {url}");
				var preview = await GetMessagePreview(url);
				response.AppendLine($"```\n{preview}\n```");
				response.AppendLine($"{string.Join(" ", reactions.Select(r => $"{r.Key} {r.Value}  "))} [{total}] total reactions");
				response.AppendLine();
				response.AppendLine();
				rank++;
			}

			await command.ModifyOriginalResponseAsync(msg => msg.Content = response.ToString());
		}
	}
}