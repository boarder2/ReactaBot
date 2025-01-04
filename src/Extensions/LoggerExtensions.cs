namespace reactabot;

public static class LoggerExtensions
{
	public static Guid LogErrorWithGuid<T>(this ILogger<T> logger, Exception exception, string message)
	{
		var guid = Guid.NewGuid();
		logger.LogError(exception, "{ErrorGuid} {message}", guid, message);
		return guid;
	}
}