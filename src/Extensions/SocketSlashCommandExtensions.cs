using reactabot;

public static class SocketSlashCommandExtensions
{
   public static void LogAndRespondWithError<T>(this SocketSlashCommand command, ILogger<T> logger, Exception ex, string message)
   {
      var guid = logger.LogErrorWithGuid(ex, message);
      command.ModifyOriginalResponseAsync(msg => msg.Content = $"An error occurred. Please contact the bot owner with this error code: {guid}");
   }
}