using Dapper;
using System.Text;

public class ReactionsService(AppConfiguration _config, DbHelper _db, ILogger<ReactionsService> _logger)
{
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
				msg.Content = $"**Top {limit} messages for {date:MMMM d, yyyy}**";
				msg.Embeds = response;
			});
		}
		catch (Exception ex)
		{
			command.LogAndRespondWithError(_logger, ex, "Failed to get top messages");
		}
	}

	public async Task<Embed[]> FormatTopMessages(DiscordSocketClient client, List<(string url, ulong authorId, int total, Dictionary<string, int> reactions)> messages)
	{
		var embeds = new List<Embed>();
		var rank = 1;
		foreach (var (url, authorId, total, reactions) in messages)
		{
			var sb = new StringBuilder();
			var preview = await client.GetMessagePreview(url, _logger);
			sb.AppendLine($"<@{authorId}>: `{preview}`");
			sb.AppendLine($"{string.Join(" ", reactions.Select(r => $"{r.Key} {r.Value}"))}");
			embeds.Add(new EmbedBuilder()
				.WithTitle($"#{rank}. {url}")
				.WithDescription(sb.ToString())
				.Build());
			rank++;
		}
		return embeds.ToArray();
	}
}