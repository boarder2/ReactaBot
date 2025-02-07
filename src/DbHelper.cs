using Dapper;
using Microsoft.Data.Sqlite;
using System.Data;
using System.Runtime.CompilerServices;

public class DbHelper
{
	private readonly long CurrentVersion = 2; // Increment version number
	private bool _initialized;
	private readonly string DbFile;
	private readonly ILogger<DbHelper> _logger;

	public DbHelper(ILogger<DbHelper> logger, AppConfiguration config)
	{
		_logger = logger;

		// Add custom type handler for Guid
		SqlMapper.AddTypeHandler(new GuidTypeHandler());

		if (string.IsNullOrWhiteSpace(config.DbLocation))
		{
			DbFile = Path.Combine(Directory.GetCurrentDirectory(), "ReactionStats.db");
		}
		else
		{
			DbFile = config.DbLocation;
		}

		logger.LogInformation("DB Location {dbfile}", DbFile);
	}

	[MethodImpl(MethodImplOptions.Synchronized)]
	public SqliteConnection GetConnection()
	{
		if (!File.Exists(DbFile))
		{
			CreateDatabase(DbFile);
		}
		if (!_initialized)
		{
			UpgradeDatabase();
			_initialized = true;
		}
		return GetConnectionInternal(DbFile);
	}

	private SqliteConnection GetConnectionInternal(string fileLocation, bool ignoreMissingFile = false)
	{
		if (!ignoreMissingFile && !File.Exists(fileLocation))
		{
			throw new FileNotFoundException("Database file doesn't exist", fileLocation);
		}
		return new SqliteConnection("Data Source=" + fileLocation);
	}

	private void CreateDatabase(string fileLocation)
	{
		_logger.LogInformation("Creating database at {fileLocation}", fileLocation);
		File.Create(fileLocation).Dispose();
		using var connection = GetConnectionInternal(fileLocation, true);
		connection.Open();
		using var tx = connection.BeginTransaction();
		connection.Execute(
			 @"
						CREATE TABLE messages
						(
							id BIGINT PRIMARY KEY,
							guild_id BIGINT,
							channel_id BIGINT,
							author INTEGER NOT NULL,
							url VARCHAR(300) NOT NULL,
							timestamp INTEGER NOT NULL DEFAULT(datetime('now')),
							total_reactions BIGINT NOT NULL DEFAULT 0
						);
						CREATE INDEX messages_guild_id_author ON messages(guild_id, author);
						CREATE INDEX messages_guild_id_timestamp_total_reactions ON messages(guild_id, timestamp, total_reactions);
						CREATE INDEX messages_guild_id_channel_id ON messages(guild_id, channel_id);

						CREATE TABLE reactions
						(
							id INTEGER PRIMARY KEY,
							message_id BIGINT NOT NULL,
							reaction_count INTEGER NOT NULL DEFAULT 1,
							emoji VARCHAR(50) NOT NULL,
							reaction_id BIGINT NULL,
							FOREIGN KEY(message_id) REFERENCES messages(id)
						);

						CREATE TABLE IF NOT EXISTS opted_out_users (
							user_id BIGINT PRIMARY KEY,
							opted_out_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
						);
						
						CREATE TABLE scheduled_jobs (
							id TEXT PRIMARY KEY,
							cron_expression TEXT NOT NULL,
							interval_hours REAL NOT NULL,
							channel_id BIGINT NOT NULL,
							guild_id BIGINT NOT NULL,
							count INTEGER NOT NULL,
							next_run TIMESTAMP WITH TIME ZONE NOT NULL,
							created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
							is_forum BOOLEAN DEFAULT FALSE,
							thread_title_template TEXT
						);
						
						CREATE INDEX scheduled_jobs_guild_id_channel_id ON scheduled_jobs(guild_id, channel_id);
						CREATE INDEX scheduled_jobs_next_run ON scheduled_jobs(next_run);
						CREATE INDEX scheduled_jobs_id ON scheduled_jobs(id);

						PRAGMA user_version=" + CurrentVersion + ";", transaction: tx);
		tx.Commit();
	}

	private void UpgradeDatabase()
	{
		using var con = GetConnectionInternal(DbFile);
		con.Open();
		var dbVersion = con.QuerySingle<long>(@"PRAGMA user_version");
		if (dbVersion < CurrentVersion)
		{
			using var tx = con.BeginTransaction();
			for (long i = dbVersion + 1; i <= CurrentVersion; i++)
			{
				_logger.LogInformation("Upgrading databse to version {dbupgradeversion}", i);
				UpgradeDatabase(i, con, tx);
			}
			con.Execute($"PRAGMA user_version={CurrentVersion};", transaction: tx);
			tx.Commit();
		}
	}

	private void UpgradeDatabase(long dbVersion, SqliteConnection con, IDbTransaction tx)
	{
		switch (dbVersion)
		{
			case 1:
				con.Execute(@"
					ALTER TABLE scheduled_jobs ADD COLUMN is_forum BOOLEAN DEFAULT FALSE;
					ALTER TABLE scheduled_jobs ADD COLUMN thread_title_template TEXT;
				", transaction: tx);
				break;
			case 2:
				con.Execute(@"
					-- Add new column
					ALTER TABLE scheduled_jobs ADD COLUMN interval_hours REAL;
					
					-- Convert existing intervals to hours
					UPDATE scheduled_jobs 
					SET interval_hours = CASE interval
						WHEN '1h' THEN 1
						WHEN '4h' THEN 4
						WHEN '8h' THEN 8
						WHEN '12h' THEN 12
						WHEN '24h' THEN 24
						WHEN '2d' THEN 48
						WHEN '3d' THEN 72
						WHEN '5d' THEN 120
						WHEN '7d' THEN 168
						ELSE 24
					END;
					
					-- Drop old column (supported in SQLite 3.35.0+)
					ALTER TABLE scheduled_jobs DROP COLUMN interval;
				", transaction: tx);
				break;
		}
	}

	public async Task<Guid> CreateScheduledJob(ScheduledJob job)
	{
		job.Id = Guid.NewGuid();
		using var conn = GetConnection();
		await conn.ExecuteAsync(@"
				INSERT INTO scheduled_jobs (
					id, 
					cron_expression, 
					interval_hours, 
					channel_id, 
					guild_id, 
					count, 
					next_run, 
					created_at, 
					is_forum, 
					thread_title_template
				)
				VALUES (
					LOWER(@Id), 
					@CronExpression, 
					@IntervalHours, 
					@ChannelId, 
					@GuildId, 
					@Count, 
					@NextRun, 
					@CreatedAt, 
					@IsForum, 
					@ThreadTitleTemplate
				)",
			job);
		return job.Id;
	}

	public async Task<List<ScheduledJob>> GetDueJobs()
	{
		using var conn = GetConnection();
		return (await conn.QueryAsync<ScheduledJob>(@"
				SELECT 
					 CAST(id as TEXT) as Id,
					 cron_expression as CronExpression,
					 interval_hours as IntervalHours,
					 channel_id as ChannelId,
					 guild_id as GuildId,
					 count as Count,
					 next_run as NextRun,
					 created_at as CreatedAt,
					 is_forum as IsForum,
					 thread_title_template as ThreadTitleTemplate
				FROM scheduled_jobs 
				WHERE datetime(next_run) <= datetime('now')")).ToList();
	}

	public async Task UpdateJobNextRun(Guid jobId, DateTimeOffset nextRun)
	{
		using var conn = GetConnection();
		await conn.ExecuteAsync(
			"UPDATE scheduled_jobs SET next_run = @NextRun WHERE id = @JobId",
			new { JobId = jobId.ToString().ToLowerInvariant(), NextRun = nextRun });
	}

	public async Task<List<(string url, ulong authorId, int total, Dictionary<string, (int count, ulong? reactionId)> reactions)>> GetTopMessages(
		DateTimeOffset startDate, DateTimeOffset endDate, int limit, ulong guildId, ulong? channelId = null, ulong? userId = null)
	{
		using var connection = GetConnection();
		await connection.OpenAsync();

		var sql = """
			SELECT m.url, m.author, m.total_reactions, 
					GROUP_CONCAT(r.emoji || ':' || r.reaction_count || ':' || COALESCE(r.reaction_id, '')) as reactions
			FROM messages m
			LEFT JOIN reactions r ON m.id = r.message_id
			WHERE datetime(m.timestamp) BETWEEN datetime(@StartDate) AND datetime(@EndDate)
			AND m.guild_id = @GuildId
		""";

		if (channelId.HasValue)
		{
			sql += " AND m.channel_id = @ChannelId";
		}

		if (userId.HasValue)
		{
			sql += " AND m.author = @UserId";
		}

		sql += """
			GROUP BY m.id
			ORDER BY m.total_reactions DESC
			LIMIT @Limit
		""";

		var results = await connection.QueryAsync<(string url, ulong authorId, int total, string reactions)>(
			sql,
			new { StartDate = startDate, EndDate = endDate, Limit = limit, GuildId = guildId, UserId = userId, ChannelId = channelId }
		);

		return results.Select(r => (
			r.url,
			r.authorId,
			r.total,
			r.reactions?.Split(',')
				.Where(x => !string.IsNullOrEmpty(x))
				.Select(x => x.Split(':'))
				.ToDictionary(
					x => x[0] + ":" + (!string.IsNullOrEmpty(x[2]) ? x[2] : ""),
					x => (count: int.Parse(x[1]), reactionId: !string.IsNullOrEmpty(x[2]) ? (ulong?)ulong.Parse(x[2]) : null)
				) ?? new Dictionary<string, (int count, ulong? reactionId)>()
		)).ToList();
	}

	public Task<List<(string url, ulong authorId, int total, Dictionary<string, (int count, ulong? reactionId)> reactions)>> GetTopMessages(
		DateTimeOffset date, int limit, ulong guildId, ulong? channelId = null, ulong? userId = null)
	{
		return GetTopMessages(date.Date, date.Date.AddDays(1), limit, guildId, channelId, userId);
	}

	public async Task OptOutUser(ulong userId)
	{
		using var conn = GetConnection();
		await conn.OpenAsync();
		using var transaction = conn.BeginTransaction();

		try
		{
			// Delete existing reactions for user's messages
			await conn.ExecuteAsync(@"
				DELETE FROM reactions 
				WHERE message_id IN (
					SELECT id FROM messages WHERE author = @UserId
				)", new { UserId = userId }, transaction);

			// Delete user's messages
			await conn.ExecuteAsync(
				"DELETE FROM messages WHERE author = @UserId",
				new { UserId = userId },
				transaction);

			// Add user to opted out table
			await conn.ExecuteAsync(@"
				INSERT INTO opted_out_users (user_id)
				VALUES (@UserId)
				ON CONFLICT (user_id) DO NOTHING;",
				new { UserId = userId },
				transaction);

			transaction.Commit();
			_logger.LogInformation("User {UserId} opted out and their data was deleted", userId);
		}
		catch (Exception ex)
		{
			transaction.Rollback();
			_logger.LogError(ex, "Failed to opt out user {UserId}", userId);
			throw;
		}
	}

	public async Task<bool> IsUserOptedOut(ulong userId)
	{
		using var conn = GetConnection();
		return await conn.QuerySingleOrDefaultAsync<bool>(
			"SELECT EXISTS(SELECT 1 FROM opted_out_users WHERE user_id = @UserId)",
			new { UserId = userId });
	}

	public async Task OptInUser(ulong userId)
	{
		using var conn = GetConnection();
		await conn.ExecuteAsync(
			"DELETE FROM opted_out_users WHERE user_id = @UserId",
			new { UserId = userId });
	}

	public async Task<List<ScheduledJob>> GetGuildSchedules(ulong guildId)
	{
		using var conn = GetConnection();
		return (await conn.QueryAsync<ScheduledJob>(@"
				SELECT 
					 CAST(id as TEXT) as Id,
					 cron_expression as CronExpression,
					 interval_hours as IntervalHours,
					 channel_id as ChannelId,
					 guild_id as GuildId,
					 count as Count,
					 next_run as NextRun,
					 created_at as CreatedAt,
					 is_forum as IsForum,
					 thread_title_template as ThreadTitleTemplate
				FROM scheduled_jobs 
				WHERE guild_id = @GuildId
				ORDER BY next_run ASC",
			new { GuildId = guildId })).ToList();
	}

	public async Task<ScheduledJob> GetScheduleById(string id)
	{
		if (!Guid.TryParse(id, out var guid))
			return null;

		using var conn = GetConnection();
		return await conn.QuerySingleOrDefaultAsync<ScheduledJob>(@"
				SELECT 
					 CAST(id as TEXT) as Id,
					 cron_expression as CronExpression,
					 interval_hours as IntervalHours,
					 channel_id as ChannelId,
					 guild_id as GuildId,
					 count as Count,
					 next_run as NextRun,
					 created_at as CreatedAt,
					 is_forum as IsForum,
					 thread_title_template as ThreadTitleTemplate
				FROM scheduled_jobs 
				WHERE id = @Id",
			new { Id = guid.ToString().ToLowerInvariant() });
	}

	public async Task DeleteSchedule(string id)
	{
		if (!Guid.TryParse(id, out var guid))
			return;

		using var conn = GetConnection();
		await conn.ExecuteAsync(
			"DELETE FROM scheduled_jobs WHERE id = @Id",
			new { Id = guid.ToString().ToLowerInvariant() });
	}

	public async Task<int> DeleteMessages(ulong guildId, ulong? channelId = null, ulong? userId = null)
	{
		using var conn = GetConnection();
		await conn.OpenAsync();
		using var transaction = conn.BeginTransaction();

		try
		{
			var whereClause = new List<string> { "guild_id = @GuildId" };
			if (channelId.HasValue)
			{
				whereClause.Add("channel_id = @ChannelId");
			}
			if (userId.HasValue)
			{
				whereClause.Add("author = @UserId");
			}

			var sql = $@"
				DELETE FROM reactions 
				WHERE message_id IN (
					SELECT id FROM messages WHERE {string.Join(" AND ", whereClause)}
				);
				DELETE FROM messages WHERE {string.Join(" AND ", whereClause)};";

			var result = await conn.ExecuteAsync(sql, new { GuildId = guildId, ChannelId = channelId, UserId = userId }, transaction);
			transaction.Commit();
			return result;
		}
		catch (Exception ex)
		{
			transaction.Rollback();
			_logger.LogError(ex, "Failed to delete messages for guild {GuildId}, channel {ChannelId}, user {UserId}", guildId, channelId, userId);
			throw;
		}
	}

	// Add this class inside DbHelper
	private class GuidTypeHandler : SqlMapper.TypeHandler<Guid>
	{
		public override Guid Parse(object value)
		{
			return Guid.Parse((string)value);
		}

		public override void SetValue(IDbDataParameter parameter, Guid value)
		{
			parameter.Value = value.ToString().ToLowerInvariant();
		}
	}
}