namespace reactabot.Common;

public static class Embeds
{
	public static Embed Error(string message, string title = "Error") =>
		new EmbedBuilder()
			.WithTitle(title)
			.WithDescription(message)
			.WithColor(Color.Red)
			.Build();

	public static Embed Success(string message, string title = "Success") =>
		new EmbedBuilder()
			.WithTitle(title)
			.WithDescription(message)
			.WithColor(Color.Green)
			.Build();

	public static Embed Info(string message, string title) =>
		new EmbedBuilder()
			.WithTitle(title)
			.WithDescription(message)
			.WithColor(Color.Blue)
			.Build();
}
