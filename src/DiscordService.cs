using System.Text;
using System.Text.Json;
using Dapper;

namespace shacknews_discord_auth_bot;

public class DiscordService(ILogger<DiscordService> _logger, AppConfiguration _config, DbHelper _db) : IHostedService
{
	private DiscordSocketClient _client;

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		_client = new DiscordSocketClient(new DiscordSocketConfig()
		{
			GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildIntegrations | GatewayIntents.DirectMessages | GatewayIntents.GuildMessages | GatewayIntents.MessageContent | GatewayIntents.GuildEmojis | GatewayIntents.GuildMessageReactions
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

		_client.MessageReceived += async (message) =>
		 {
			 if (message.Content.StartsWith("!top"))
			 {
				 await HandleTopCommand(message);
			 }
		 };

		_client.Ready += () =>
		{
			_logger.LogInformation($"Logged in as {_client.CurrentUser.Username}");
			return Task.CompletedTask;
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

	private async Task HandleTopCommand(SocketMessage message)
	{
		var args = message.Content.Split(' ');
		var date = DateTimeOffset.UtcNow;
		var limit = 10;

		if (args.Length > 1)
		{
			if (args.Length != 3)
			{
				await message.Channel.SendMessageAsync("Usage: `!top [Date Number]` (e.g., `!top 2024-01-25 10`)\nOr just `!top` for today's top 10");
				return;
			}

			if (!DateTimeOffset.TryParse(args[1], out date))
			{
				await message.Channel.SendMessageAsync("Invalid date format. Please use YYYY-MM-DD");
				return;
			}

			if (!int.TryParse(args[2], out limit) || limit < 1 || limit > 50)
			{
				await message.Channel.SendMessageAsync("Number must be between 1 and 50");
				return;
			}
		}

		var topMessages = await _db.GetTopMessages(date, limit);
		if (!topMessages.Any())
		{
			await message.Channel.SendMessageAsync($"No messages found for {date:MMMM d, yyyy}");
			return;
		}

		var response = new StringBuilder($"Top {limit} messages for {date:MMMM d, yyyy}:\n\n");

		foreach (var (url, authorId, total, reactions) in topMessages)
		{
			response.AppendLine($"<@{authorId}> - {url}");
			var preview = await GetMessagePreview(url);
			response.AppendLine($"> {preview}");
			response.AppendLine($"{string.Join(" ", reactions.Select(r => $"{r.Key} {r.Value}  "))}");
			response.AppendLine();
		}

		await message.Channel.SendMessageAsync(response.ToString());
	}
}