public class AppConfiguration
{
	public string DbLocation { get; } = "";
	public string Token { get; } = "";
   public AppConfiguration(IConfiguration config)
   {
      this.DbLocation = config.GetValue<string>("DB_LOCATION");
      this.Token = config.GetValue<string>("DISCORD_TOKEN");
   }
}