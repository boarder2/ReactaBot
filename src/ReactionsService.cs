using Dapper;
using System.Text;

public class ReactionsService(DbHelper _db, ILogger<ReactionsService> _logger)
{
	private const string REACTION_SEPARATOR = " "; // U+2003 EM SPACE (It's bigger than a regular space)

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
				limit = (int)Math.Clamp(longLimit, 1, 10);
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

			var response = await FormatTopMessagesAsEmbeds(client, topMessages);
			await command.ModifyOriginalResponseAsync(msg =>
			{
				msg.Embeds = response.First().ToArray();
			});
		}
		catch (Exception ex)
		{
			command.LogAndRespondWithError(_logger, ex, "Failed to get top messages");
		}
	}

	public async Task<List<List<Embed>>> FormatTopMessagesAsEmbeds(DiscordSocketClient client, List<(string url, ulong authorId, int total, Dictionary<string, (int count, ulong? reactionId)> reactions)> messages)
	{
		var embedGroups = new List<List<Embed>>();
		var currentGroup = new List<Embed>();
		var rank = 1;

		foreach (var msg in messages)
		{
			var dMessage = await client.GetMessageFromUrl(msg.url, _logger);
			var username = (dMessage.Author as IGuildUser)?.DisplayName ?? dMessage.Author.Username;
			var embed = new EmbedBuilder()
				.WithColor(rank == 1 ? Color.Gold : Color.Blue)
				.WithTitle($"#{rank++} in <#{dMessage.Channel.Id}>")
				// .WithAuthor(username, dMessage.Author.GetAvatarUrl() ?? dMessage.Author.GetDefaultAvatarUrl())
				.WithDescription(string.IsNullOrWhiteSpace(dMessage.Content) ? "(No message content)" : dMessage.Content)
				.WithFields(
					new EmbedFieldBuilder()
						.WithName("Reactions")
						.WithValue(string.Join(REACTION_SEPARATOR, msg.reactions.Select(r =>
							r.Value.reactionId.HasValue ?
							$"<:{r.Key.Split(":")[0]}:{r.Value.reactionId}> {r.Value.count}" :
							$"{r.Key.Split(":")[0]} {r.Value.count}"
						)))
						.WithIsInline(false),
					new EmbedFieldBuilder()
						.WithName("Author")
						.WithValue($"<@{msg.authorId}>")
						.WithIsInline(false)
				)
				.WithFooter($"Total reactions: {msg.total}", dMessage.Author.GetAvatarUrl() ?? dMessage.Author.GetDefaultAvatarUrl())
				.WithTimestamp(dMessage.Timestamp)
				.WithUrl(msg.url);

			if (currentGroup.Count >= 10)
			{
				embedGroups.Add(currentGroup);
				currentGroup = new List<Embed>();
			}

			currentGroup.Add(embed.Build());
		}

		if (currentGroup.Any())
		{
			embedGroups.Add(currentGroup);
		}

		return embedGroups;
	}
}