public class ScheduledJob
{
    public Guid Id { get; set; }
    public string CronExpression { get; set; }
    public double IntervalHours { get; set; } // Changed from string Interval
    public ulong ChannelId { get; set; }
    public ulong GuildId { get; set; }
    public int Count { get; set; }
    public DateTime NextRun { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsForum { get; set; }
    public string ThreadTitleTemplate { get; set; }
}
