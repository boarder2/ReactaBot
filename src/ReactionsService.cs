using Dapper;
using System.Text;

public class ReactionsService(AppConfiguration _config, DbHelper _db, ILogger<ReactionsService> _logger)
{
	private const string TOO_MANY_RESULTS = "\n**Too many results for one message!**";
	private readonly int TOO_MANY_RESULTS_LENGTH = TOO_MANY_RESULTS.Length;
	public async Task UpdateMessageReactions(IMessage msg)
	{
		if (await _db.IsUserOptedOut(msg.Author.Id))
		{
			_logger.LogInformation("Skipping message {MessageId} due to user opt-out", msg.Id);
			return;
		}

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
			INSERT OR REPLACE INTO messages(id, guild_id, channel_id, timestamp, author, url, total_reactions) 
			VALUES(@Id, @GuildId, @ChannelId, @Timestamp, @Author, @Url, @TotalReactions);
			""";
			await connection.ExecuteAsync(messageSql, new
			{
				msg.Id,
				(msg.Channel as IGuildChannel)?.GuildId,
				ChannelId = msg.Channel.Id,
				msg.Timestamp,
				Author = msg.Author.Id,
				Url = msg.GetJumpUrl(),
				TotalReactions = totalReactions
			}, transaction);

			// Delete existing reactions
			var deleteSql = "DELETE FROM reactions WHERE message_id = @MessageId";
			await connection.ExecuteAsync(deleteSql, new { MessageId = msg.Id }, transaction);

			// Insert current reactions
			var insertSql = "INSERT INTO reactions(message_id, emoji, reaction_count, reaction_id) VALUES(@MessageId, @Emoji, @ReactionCount, @ReactionId)";
			foreach (var react in msg.Reactions)
			{
				var emote = react.Key as Emote;
				await connection.ExecuteAsync(insertSql, new
				{
					MessageId = msg.Id,
					Emoji = react.Key.Name,
					react.Value.ReactionCount,
					ReactionId = emote?.Id
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

	public async Task PrintTopReactions(DiscordSocketClient client, SocketSlashCommand command)
	{
		try
		{
			var date = DateTimeOffset.UtcNow;
			var limit = 10;
			ulong? userId = null;
			ulong guildId;
			ulong? channelId = null;

			// Parse standard options
			var dateOption = command.Data.Options.FirstOrDefault(x => x.Name == "date");
			var limitOption = command.Data.Options.FirstOrDefault(x => x.Name == "limit");
			var userOption = command.Data.Options.FirstOrDefault(x => x.Name == "user");
			var channelOption = command.Data.Options.FirstOrDefault(x => x.Name == "channel");

			// Handle DM vs Guild context
			if (command.GuildId.HasValue)
			{
				guildId = command.GuildId.Value;
			}
			else // DM context
			{
				if (channelOption == null)
				{
					await command.ModifyOriginalResponseAsync(msg =>
						msg.Content = "When using this command in DMs, you must specify a channel!");
					return;
				}

				channelId = ((ITextChannel)channelOption.Value).Id;
				guildId = ((ITextChannel)channelOption.Value).GuildId;
			}

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

			var topMessages = await _db.GetTopMessages(date, limit, guildId, channelId, userId);
			if (!topMessages.Any())
			{
				await command.ModifyOriginalResponseAsync(msg =>
					msg.Content = $"No messages found for {date:MMMM d, yyyy}");
				return;
			}

			var response = await FormatTopMessages(client, topMessages);
			await command.ModifyOriginalResponseAsync(msg =>
			{
				msg.Content = $"**Top {limit} messages for {date:MMMM d, yyyy}**\n" + response;
			});
		}
		catch (Exception ex)
		{
			command.LogAndRespondWithError(_logger, ex, "Failed to get top messages");
		}
	}

	public async Task<string> FormatTopMessages(DiscordSocketClient client,
		List<(string url, ulong authorId, int total, Dictionary<string, (int count, ulong? reactionId)> reactions)> messages)
	{
		const int maxPreviewLength = 100;
		const int minPreviewLength = 15;
		const int previewDecrement = 20;

		// Get all previews once
		var previewCache = new Dictionary<string, string>();
		foreach (var (url, _, _, _) in messages)
		{
			var preview = await client.GetMessagePreview(url, _logger, maxPreviewLength);
			previewCache[url] = preview;
		}

		// Try different preview lengths without additional API calls
		for (int previewLength = maxPreviewLength; previewLength >= minPreviewLength; previewLength -= previewDecrement)
		{
			var result = TryFormatMessagesWithCache(messages, previewCache, previewLength);
			if (result != null)
			{
				return result;
			}
		}

		// Final attempt with minimum length + TOO_MANY_RESULTS if needed
		return TryFormatMessagesWithCache(messages, previewCache, minPreviewLength, true) ?? "";
	}

	public async Task<List<string>> FormatTopMessagesMultiPart(
		DiscordSocketClient client,
		List<(string url, ulong authorId, int total, Dictionary<string, (int count, ulong? reactionId)> reactions)> messages,
		string header)
	{
		const int maxPreviewLength = 100;
		var result = new List<string>();
		var currentPart = new StringBuilder();
		var rank = 1;
		var partNumber = 1;

		// Get all previews once
		var previewCache = new Dictionary<string, string>();
		foreach (var (url, _, _, _) in messages)
		{
			var preview = await client.GetMessagePreview(url, _logger, maxPreviewLength);
			previewCache[url] = preview;
		}

		// Start first part with header
		currentPart.AppendLine($"{header} (Part 1/?)");

		foreach (var message in messages)
		{
			var messageText = FormatSingleMessageWithCache(message, previewCache, rank, maxPreviewLength);
			
			if (currentPart.Length + messageText.Length < 2000)
			{
				currentPart.Append(messageText);
			}
			else
			{
				result.Add(currentPart.ToString());
				currentPart.Clear();
				partNumber++;
				currentPart.AppendLine($"{header} (Part {partNumber}/?)");
				currentPart.Append(messageText);
			}
			rank++;
		}

		if (currentPart.Length > 0)
		{
			result.Add(currentPart.ToString());
		}

		// Update headers with correct total part count
		for (int i = 0; i < result.Count; i++)
		{
			result[i] = result[i].Replace("/?)", $"/{result.Count})");
		}

		return result;
	}

	private string? TryFormatMessagesWithCache(
		List<(string url, ulong authorId, int total, Dictionary<string, (int count, ulong? reactionId)> reactions)> messages,
		Dictionary<string, string> previewCache,
		int previewLength,
		bool isLastAttempt = false)
	{
		var sb = new StringBuilder();
		var rank = 1;

		foreach (var message in messages)
		{
			var messageText = FormatSingleMessageWithCache(message, previewCache, rank, previewLength);
			
			if (sb.Length + messageText.Length < (2000 - TOO_MANY_RESULTS_LENGTH))
			{
				sb.Append(messageText);
				rank++;
			}
			else
			{
				if (isLastAttempt)
				{
					sb.Append(TOO_MANY_RESULTS);
					return sb.ToString();
				}
				return null;
			}
		}

		return sb.ToString();
	}

	private string FormatSingleMessageWithCache(
		(string url, ulong authorId, int total, Dictionary<string, (int count, ulong? reactionId)> reactions) message,
		Dictionary<string, string> previewCache,
		int rank,
		int previewLength)
	{
		var (url, authorId, _, reactions) = message;
		var sb = new StringBuilder();
		
		var preview = previewCache[url];
		if (preview.Length > previewLength)
		{
			preview = preview[..previewLength] + "...";
		}
		
		sb.AppendLine($"#{rank}. {url}");
		sb.AppendLine($"<@{authorId}>: `{preview}`");
		sb.AppendLine(string.Join(" ", reactions.Select(r =>
			r.Value.reactionId.HasValue ?
			$"<:{r.Key}:{r.Value.reactionId}> {r.Value.count}" :
			$"{r.Key} {r.Value.count}"
		)));
		sb.AppendLine();

		return sb.ToString();
	}
}