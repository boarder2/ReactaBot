public class ScheduledJob
{
    public Guid Id { get; set; }
    public string CronExpression { get; set; }
    public string Interval { get; set; }
    public ulong ChannelId { get; set; }
    public ulong GuildId { get; set; }
    public int Count { get; set; }
    public DateTime NextRun { get; set; }
    public DateTime CreatedAt { get; set; }
}
