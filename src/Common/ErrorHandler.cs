#nullable enable
using System.Runtime.CompilerServices;

namespace reactabot.Common;

public static class ErrorHandlerLoggerExtensions
{
	public static async Task HandleError<T>(
      this ILogger<T> logger,
		Exception ex, 
		IInteractionContext context,
		Func<Embed, Task> respondWithEmbed,
		string userMessage = "An error occurred while processing your request.",
		string? logMessage = null,
		object?[]? logArgs = null,
		[CallerMemberName] string? caller = null)
	{
		var errorId = Guid.NewGuid();
		
		if (logMessage != null && logArgs != null)
		{
			// Prepend errorId to the logArgs array
			var newArgs = new object[logArgs.Length + 1];
			newArgs[0] = errorId;
			logArgs.CopyTo(newArgs, 1);
			
			logger.LogError(ex, "Error ID {ErrorId} - " + logMessage, newArgs);
		}
		else
		{
			logger.LogError(ex, 
				"Error ID {ErrorId} in {Caller} - User: {UserId}, Guild: {GuildId}, Channel: {ChannelId}, Error: {Error}",
				errorId, caller, context.User.Id, context.Guild?.Id, context.Channel.Id, ex.Message);
		}

		await respondWithEmbed(Embeds.Error(
			$"{userMessage}\n\nError Reference: `{errorId}`",
			"Error"));
	}
}
