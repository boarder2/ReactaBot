using Dapper;
using Microsoft.Data.Sqlite;
using System.Data;
using System.Runtime.CompilerServices;

public class DbHelper
{
	private readonly long CurrentVersion = 0;
	private bool _initialized;
	private readonly string DbFile;
	private readonly ILogger<DbHelper> _logger;

	public DbHelper(ILogger<DbHelper> logger, AppConfiguration config)
	{
		_logger = logger;

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
							id INTEGER PRIMARY KEY,
							author INTEGER NOT NULL,
							url VARCHAR(300) NOT NULL,
							timestamp INTEGER NOT NULL DEFAULT(datetime('now')),
							total_reactions INTEGER NOT NULL DEFAULT 0
						);
						CREATE INDEX messages_author ON messages(author);
						CREATE INDEX messages_timestamp_total_reactions ON messages(timestamp, total_reactions);

						CREATE TABLE reactions
						(
							id INTEGER PRIMARY KEY,
							message_id INTEGER NOT NULL,
							reaction_count INTEGER NOT NULL DEFAULT 1,
							emoji VARCHAR(50) NOT NULL,
							FOREIGN KEY(message_id) REFERENCES messages(id)
						);

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
				break;
		}
	}

	public async Task<List<(string url, ulong authorId, int total, Dictionary<string, int> reactions)>> GetTopMessages(DateTimeOffset date, int limit, ulong? userId = null)
	{
		using var connection = GetConnection();
		await connection.OpenAsync();

		var sql = """
			SELECT m.url, m.author, m.total_reactions, 
				   GROUP_CONCAT(r.emoji || ':' || r.reaction_count) as reactions
			FROM messages m
			LEFT JOIN reactions r ON m.id = r.message_id
			WHERE DATE(m.timestamp) = DATE(@Date)
		""";

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
			new { Date = date.Date, Limit = limit, UserId = userId }
		);

		return results.Select(r => (
			r.url,
			r.authorId,
			r.total,
			r.reactions?.Split(',')
				.Where(x => !string.IsNullOrEmpty(x))
				.Select(x => x.Split(':'))
				.ToDictionary(x => x[0], x => int.Parse(x[1])) ?? new Dictionary<string, int>()
		)).ToList();
	}
}