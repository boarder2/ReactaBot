using Discord.Interactions;

public class OptCommand(DbHelper _db, ILogger<OptCommand> _logger) : InteractionModuleBase<SocketInteractionContext>
{
	[SlashCommand("opt", "Manage your message tracking preferences")]
	public async Task HandleOptCommand()
	{
		try
		{
			await DeferAsync(ephemeral: true);
			_logger.LogInformation("Checking opt status for user {UserId}", Context.User.Id);
			var isOptedOut = await _db.IsUserOptedOut(Context.User.Id);

			var builder = new ComponentBuilder()
				.WithButton(
					isOptedOut ? "Opt In" : "Opt Out",
					isOptedOut ? "opt-in" : "opt-out",
					isOptedOut ? ButtonStyle.Success : ButtonStyle.Danger
				);

			await ModifyOriginalResponseAsync(x => {
				x.Embed = CreateStatusEmbed(isOptedOut);
				x.Components = builder.Build();
			});
		}
		catch (Exception ex)
		{
			await _logger.HandleError(ex, Context,
				async embed => await ModifyOriginalResponseAsync(x => x.Embed = embed),
				logMessage: "Error checking opt status for user {UserId}",
				logArgs: [Context.User.Id]);
		}
	}

	[ComponentInteraction("opt-in")]
	public async Task HandleOptIn()
	{
		try
		{
			_logger.LogInformation("User {UserId} opting in", Context.User.Id);
			await _db.OptInUser(Context.User.Id);
			var builder = new ComponentBuilder()
				.WithButton("Opt Out", "opt-out", ButtonStyle.Danger);

			if (Context.Interaction is SocketMessageComponent component)
			{
				await component.UpdateAsync(properties => {
					properties.Embed = CreateStatusEmbed(false);
					properties.Components = builder.Build();
				});
			}

			_logger.LogInformation("User {UserId} opted in successfully", Context.User.Id);
		}
		catch (Exception ex)
		{
			await _logger.HandleError(ex, Context,
				async embed => await ModifyOriginalResponseAsync(x => x.Embed = embed),
				logMessage: "Error handling opt-in for user {UserId}",
				logArgs: [Context.User.Id]);
		}
	}

	[ComponentInteraction("opt-out")]
	public async Task HandleOptOut()
	{
		try
		{
			_logger.LogInformation("User {UserId} opting out", Context.User.Id);
			await _db.OptOutUser(Context.User.Id);
			var builder = new ComponentBuilder()
				.WithButton("Opt In", "opt-in", ButtonStyle.Success);

			if (Context.Interaction is SocketMessageComponent component)
			{
				await component.UpdateAsync(properties => {
					properties.Embed = CreateStatusEmbed(true);
					properties.Components = builder.Build();
				});
			}

			_logger.LogInformation("User {UserId} opted out successfully", Context.User.Id);
		}
		catch (Exception ex)
		{
			await _logger.HandleError(ex, Context,
				async embed => await ModifyOriginalResponseAsync(x => x.Embed = embed),
				logMessage: "Error handling opt-out for user {UserId}",
				logArgs: [Context.User.Id]);
		}
	}

	private Embed CreateStatusEmbed(bool isOptedOut)
	{
		var (emoji, status, description) = isOptedOut 
			? ("🔴", "Opted Out", "Your messages will not be included in top message reports.")
			: ("🟢", "Opted In", "Your messages will be included in top message reports.");

		return new EmbedBuilder()
			.WithTitle($"Message Tracking Status {emoji}")
			.WithDescription($"**Status: {status}**\n{description}\n\nUse the buttons below to change your preferences.")
			.WithColor(isOptedOut ? Color.Red : Color.Green)
			.Build();
	}
}
