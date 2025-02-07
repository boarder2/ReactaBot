#nullable enable
using Discord.Interactions;

namespace reactabot;

public class TopCommands(
	DbHelper _db,
	ILogger<TopCommands> _logger,
	ReactionsService _reactionsService,
	DiscordSocketClient _client) : InteractionModuleBase<SocketInteractionContext>
{
	[RequireContext(ContextType.Guild)]
    [CommandContextType(InteractionContextType.Guild)]
	[SlashCommand("top", "Get top reacted messages")]
	public async Task HandleTopCommand(
		[Summary("start_date", "Start date (YYYY-MM-DD format). Defaults to today")]
		string? startDate = null,
		[Summary("end_date", "End date (YYYY-MM-DD format). Defaults to today")]
		string? endDate = null,
		[Summary("user", "Filter by user")] 
		IUser? user = null,
		[Summary("channel", "Filter by channel")] 
		ITextChannel? channel = null,
		[Summary("count", "Number of messages to show (1-10)")]
		[MinValue(1)]
		[MaxValue(10)]
		int count = 10)
	{
		try
		{
			await DeferAsync(ephemeral: true);

			var end = string.IsNullOrEmpty(endDate) ? 
				DateTimeOffset.UtcNow.Date : 
				DateTimeOffset.TryParse(endDate, out var parsedEnd) ? parsedEnd.Date : DateTimeOffset.UtcNow.Date;

			var start = string.IsNullOrEmpty(startDate) ? 
				end.Date : 
				DateTimeOffset.TryParse(startDate, out var parsedStart) ? parsedStart.Date : end.Date;

			// Validate date range
			var dayDifference = (end - start).TotalDays;
			if (dayDifference < 0)
			{
				await ModifyOriginalResponseAsync(msg =>
					msg.Embed = Embeds.Error("Start date must be before or equal to end date"));
				return;
			}
			if (dayDifference > 7)
			{
				await ModifyOriginalResponseAsync(msg =>
					msg.Embed = Embeds.Error("Date range cannot exceed 7 days"));
				return;
			}

			// Add one day to end date to include the full end date
			var queryEnd = end.AddDays(1);
			
			var messages = await _db.GetTopMessages(
				start,
				queryEnd,
				count,
				Context.Guild.Id,
				channel?.Id,
				user?.Id);

			if (!messages.Any())
			{
				var dateRange = start.Date == end.Date ? 
					$"on {start:MMM dd, yyyy}" : 
					$"between {start:MMM dd, yyyy} and {end:MMM dd, yyyy}";

				await ModifyOriginalResponseAsync(msg =>
					msg.Content = $"No messages found {dateRange}");
				return;
			}

			var header = $"Top {count} messages";
			if (start.Date == end.Date)
			{
				header += $" from {start:MMM dd, yyyy}";
			}
			else
			{
				header += $" from {start:MMM dd, yyyy} to {end:MMM dd, yyyy}";
			}
			if (user != null) header += $" by {user.Mention}";
			if (channel != null) header += $" in {channel.Mention}";

			var embedGroups = await _reactionsService.FormatTopMessagesAsEmbeds(_client, messages);

			 // Since this is an ephemeral response, we need to fit everything in one response
			// Discord allows up to 10 embeds per message, so take the first 10 embeds if we have more
			var embeds = embedGroups.SelectMany(g => g).Take(10).ToList();
			
			await ModifyOriginalResponseAsync(msg =>
			{
				msg.Content = header + (embedGroups.SelectMany(g => g).Count() > 10 ? "\n(Showing top 10 messages due to Discord limits)" : "");
				msg.Embeds = embeds.ToArray();
			});
		}
		catch (Exception ex)
		{
			await _logger.HandleError(ex, Context,
				async embed => await ModifyOriginalResponseAsync(x => x.Embed = embed),
				logMessage: "Error getting top messages for guild {GuildId}, user {UserId}, channel {ChannelId}, start {Start}, end {End}",
				logArgs: [Context.Guild.Id, user?.Id, channel?.Id, startDate, endDate]);
		}
	}
}