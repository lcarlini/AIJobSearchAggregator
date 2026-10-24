using Microsoft.Data.Sqlite;

namespace AIJobSearchAggregator;

public sealed class Database
{
    private readonly string _connectionString;

    public Database(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS jobs (
                id INTEGER PRIMARY KEY,
                source TEXT NOT NULL,
                external_id TEXT NOT NULL,
                title TEXT NOT NULL,
                company TEXT NOT NULL DEFAULT '',
                company_url TEXT,
                location TEXT,
                regions TEXT NOT NULL DEFAULT '',
                workplace TEXT NOT NULL DEFAULT 'unknown',
                salary TEXT,
                seniority TEXT,
                employment TEXT,
                job_function TEXT,
                industry TEXT,
                stack_json TEXT NOT NULL DEFAULT '[]',
                summary TEXT,
                description TEXT,
                posted_at TEXT,
                listed_ago TEXT,
                insight TEXT,
                apply_url TEXT,
                source_url TEXT,
                logo_url TEXT,
                status TEXT NOT NULL DEFAULT 'new',
                note TEXT,
                applied_at TEXT,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                detail_fetched_at TEXT,
                relevant INTEGER,
                UNIQUE(source, external_id)
            );

            CREATE TABLE IF NOT EXISTS search_profiles (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                keywords TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                recency TEXT NOT NULL DEFAULT 'week',
                pages INTEGER NOT NULL DEFAULT 2,
                regions_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS search_runs (
                id INTEGER PRIMARY KEY,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                status TEXT NOT NULL,
                message TEXT,
                found_count INTEGER NOT NULL DEFAULT 0,
                new_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_jobs_status ON jobs(status);
            CREATE INDEX IF NOT EXISTS ix_jobs_posted ON jobs(posted_at);
            """;
        command.ExecuteNonQuery();
        using var interrupted = connection.CreateCommand();
        interrupted.CommandText = """
            UPDATE search_runs
            SET status = 'interrupted',
                finished_at = COALESCE(finished_at, $now),
                message = 'O programa fechou no meio da busca.'
            WHERE status = 'running';
            """;
        interrupted.Parameters.AddWithValue("$now", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
        interrupted.ExecuteNonQuery();
        using var widen = connection.CreateCommand();
        widen.CommandText = "UPDATE search_profiles SET pages = 40 WHERE pages <= 15;";
        widen.ExecuteNonQuery();
        Seed(connection);
    }

    private static void Seed(SqliteConnection connection)
    {
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM search_profiles;";
        var existing = Convert.ToInt32(count.ExecuteScalar());
        if (existing > 0)
            return;

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO search_profiles (name, keywords, enabled, recency, pages, regions_json)
            VALUES ($name, $keywords, 1, 'week', 40, $regions);
            """;
        insert.Parameters.AddWithValue("$name", ".NET e C#");
        insert.Parameters.AddWithValue("$keywords", ".NET | C#");
        insert.Parameters.AddWithValue("$regions", """["grande-sp","sorocaba","remoto"]""");
        insert.ExecuteNonQuery();
    }
}
