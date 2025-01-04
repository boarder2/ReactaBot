public static class DiscordSocketClientExtensions
{
	public static async Task<IMessage> GetMessageFromUrl(this DiscordSocketClient client, string url, ILogger logger = null)
	{
		try
		{
			var messageLink = new Uri(url);
			var pathSegments = messageLink.AbsolutePath.Split('/').Where(s => !string.IsNullOrEmpty(s)).ToArray();

			if (pathSegments.Length >= 3 &&
				ulong.TryParse(pathSegments[2], out var channelId) &&
				ulong.TryParse(pathSegments[3], out var messageId))
			{
				var channel = await client.GetChannelAsync(channelId) as ITextChannel;
				return channel != null ? await channel.GetMessageAsync(messageId) : null;
			}
			else
			{
				return null;
			}
		}
		catch (Exception ex)
		{
			logger?.LogError(ex, "Error processing message URL: {url}", url);
			throw;
		}
	}

	public static async Task<string> GetMessagePreview(this DiscordSocketClient client, string url, int truncateLength = 100, ILogger logger = null)
	{
		try
		{
			var message = await client.GetMessageFromUrl(url);
			if (message == null)
			{
				return "(Message content unavailable)";
			}

			var content = message.Content;
			if (content.Length > truncateLength)
			{
				content = content.Substring(0, truncateLength) + "...";
			}

			return content;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to get message preview");
			return "(Message content unavailable)";
		}
	}
}