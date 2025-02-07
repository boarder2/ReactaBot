#nullable enable
using Discord.Interactions;
using System.Text;

public class AdminCommands(
	DbHelper db, 
	ILogger<AdminCommands> logger) : InteractionModuleBase<SocketInteractionContext>
{
	[RequireContext(ContextType.Guild)]
	[CommandContextType(InteractionContextType.Guild)]
	[SlashCommand("delete", "Delete stored reactions")]
	public async Task HandleDeleteCommand(
		ITextChannel? channel = null,
		SocketUser? user = null)
	{
		try
		{
			await DeferAsync(ephemeral: true);
			
			if (channel == null && user == null)
			{
				await ModifyOriginalResponseAsync(x => x.Embed = Embeds.Error("You must specify at least one of: channel or user"));
				return;
			}

			var count = await db.DeleteMessages(Context.Guild.Id, channel?.Id, user?.Id);

			var response = new StringBuilder();
			if (channel != null) response.AppendLine($"\nChannel: {channel.Mention}");
			if (user != null) response.AppendLine($"\nUser: {user.Mention}");
			response.AppendLine($"\nTotal messages and reactions deleted: {count}");

			await ModifyOriginalResponseAsync(x => x.Embed = Embeds.Info(response.ToString(), "Deleted Reactions and Messages"));
		}
		catch (Exception ex)
		{
			await logger.HandleError(ex, Context,
				async embed => await ModifyOriginalResponseAsync(x => x.Embed = embed),
				logMessage: "Error deleting messages for guild {GuildId}, channel {ChannelId}, user {UserId}",
				logArgs: [Context.Guild.Id, channel?.Id, user?.Id]);
		}
	}

	[SlashCommand("version", "Get the current version of the bot")]
	public async Task HandleVersionCommand()
	{
		try
		{
			await DeferAsync(ephemeral: true);
			
			var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
			await ModifyOriginalResponseAsync(x => x.Embed = Embeds.Info(
				$"Bot Version: `{version}`",
				"Version Information"));
		}
		catch (Exception ex)
		{
			await logger.HandleError(ex, Context,
				async embed => await ModifyOriginalResponseAsync(x => x.Embed = embed),
				logMessage: "Error handling version command for guild {GuildId}",
				logArgs: [Context.Guild.Id]);
		}
	}
}
