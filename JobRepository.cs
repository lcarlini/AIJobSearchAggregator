using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AIJobSearchAggregator;

public sealed record JobStats(int NewCount, int Saved, int Pipeline, int Hidden, int ArrivedToday, int Total);

public sealed class JobRepository
{
    private const string Columns = """
        id, source, external_id, title, company, company_url, location, regions, workplace,
        salary, seniority, employment, job_function, industry, stack_json, summary, description,
        posted_at, listed_ago, insight, apply_url, source_url, logo_url, status, note, applied_at,
        first_seen_at, last_seen_at, detail_fetched_at, relevant
        """;

    private readonly Database _db;
    private readonly object _gate = new();

    public JobRepository(Database db) => _db = db;

    public IReadOnlyList<JobDto> List(JobQuery query)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        var sql = new StringBuilder("SELECT ");
        sql.Append(Columns);
        sql.Append(" FROM jobs WHERE 1=1");

        if (!query.IncludeHidden && query.Status != "hidden")
            sql.Append(" AND status != 'hidden'");

        if (!string.IsNullOrWhiteSpace(query.Status) && query.Status != "all")
        {
            if (query.Status == "pipeline")
                sql.Append(" AND status IN ('applied','interview','offer')");
            else
            {
                sql.Append(" AND status = $status");
                Bind(command, "$status", query.Status);
            }
        }

        if (!string.IsNullOrWhiteSpace(query.Region) && query.Region != "all")
        {
            sql.Append(" AND instr(',' || regions || ',', ',' || $region || ',') > 0");
            Bind(command, "$region", query.Region);
        }

        if (!string.IsNullOrWhiteSpace(query.Workplace) && query.Workplace != "all")
        {
            sql.Append(" AND workplace = $workplace");
            Bind(command, "$workplace", query.Workplace);
        }

        if (query.OnlyRelevant)
            sql.Append(" AND (relevant IS NULL OR relevant = 1)");

        if (query.OnlySalary)
            sql.Append(" AND salary IS NOT NULL AND length(salary) > 0");

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            sql.Append("""
                 AND (
                    title LIKE $q OR company LIKE $q OR ifnull(location,'') LIKE $q
                    OR ifnull(summary,'') LIKE $q OR ifnull(stack_json,'') LIKE $q
                    OR ifnull(note,'') LIKE $q OR ifnull(description,'') LIKE $q
                 )
                """);
            Bind(command, "$q", Like(query.Text));
        }

        sql.Append("""
             ORDER BY CASE status WHEN 'new' THEN 0 ELSE 1 END,
                      COALESCE(posted_at, first_seen_at) DESC,
                      id DESC
             LIMIT 2000
            """);
        command.CommandText = sql.ToString();

        var jobs = new List<JobDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            jobs.Add(Map(reader) with { Description = null });
        return jobs;
    }

    public JobDto? Get(long id)
    {
        using var connection = _db.Open();
        return FindById(connection, id);
    }

    public JobDto? MarkSeen(long id)
    {
        lock (_gate)
        {
            using var connection = _db.Open();
            var current = FindById(connection, id);
            if (current == null || current.Status != "new")
                return current;

            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs SET status = 'seen' WHERE id = $id AND status = 'new';";
            Bind(command, "$id", id);
            command.ExecuteNonQuery();
            return FindById(connection, id);
        }
    }

    public JobDto? Update(long id, string? status, string? note)
    {
        lock (_gate)
        {
            using var connection = _db.Open();
            var current = FindById(connection, id);
            if (current == null)
                return null;

            var nextStatus = status ?? current.Status;
            if (!JobStatuses.All.Contains(nextStatus))
                throw new ArgumentException("Status inválido.");

            var nextNote = note ?? current.Note;
            var applied = current.AppliedAt;
            if (applied == null && JobStatuses.IsApplication(nextStatus))
                applied = DateTime.Now.ToString("yyyy-MM-dd");

            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE jobs
                SET status = $status, note = $note, applied_at = $applied
                WHERE id = $id;
                """;
            Bind(command, "$status", nextStatus);
            Bind(command, "$note", string.IsNullOrWhiteSpace(nextNote) ? null : nextNote.Trim());
            Bind(command, "$applied", applied);
            Bind(command, "$id", id);
            command.ExecuteNonQuery();
            return FindById(connection, id);
        }
    }

    public UpsertResult UpsertFromSearch(JobDraft draft)
    {
        lock (_gate)
        {
            using var connection = _db.Open();
            var current = FindByExternal(connection, draft.Source, draft.ExternalId);
            var now = Stamp();
            if (current == null)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO jobs (
                        source, external_id, title, company, company_url, location, regions, workplace,
                        salary, seniority, employment, job_function, industry, stack_json, summary, description,
                        posted_at, listed_ago, insight, apply_url, source_url, logo_url, status, note, applied_at,
                        first_seen_at, last_seen_at, detail_fetched_at, relevant
                    ) VALUES (
                        $source, $external, $title, $company, $companyUrl, $location, $regions, $workplace,
                        $salary, $seniority, $employment, $function, $industry, $stack, $summary, $description,
                        $posted, $listed, $insight, $apply, $sourceUrl, $logo, 'new', NULL, NULL,
                        $now, $now, NULL, $relevant
                    );
                    SELECT last_insert_rowid();
                    """;
                BindJob(insert, draft, RegionCatalog.Join([draft.RegionKey]), now);
                var id = Convert.ToInt64(insert.ExecuteScalar());
                return new UpsertResult(id, true);
            }

            var regions = MergeRegions(current.Regions, [draft.RegionKey]);
            var enriched = current.DetailFetchedAt != null;
            var workplace = MergeWorkplace(current.Workplace, draft.Workplace);
            var stack = enriched
                ? current.Stack
                : UnionStack(draft.Stack, current.Stack);
            var salary = enriched ? current.Salary : draft.Salary ?? current.Salary;
            var seniority = enriched ? current.Seniority : draft.Seniority ?? current.Seniority;
            var employment = enriched ? current.Employment : draft.Employment ?? current.Employment;
            var summary = enriched
                ? current.Summary
                : Longer(current.Summary, draft.Summary);
            var description = current.Description ?? draft.Description;
            var relevant = enriched
                ? current.Relevant ?? draft.Relevant
                : draft.Relevant == 1 || current.Relevant == 1 ? 1 : draft.Relevant ?? current.Relevant;

            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE jobs SET
                    title = $title,
                    company = $company,
                    company_url = COALESCE($companyUrl, company_url),
                    location = COALESCE($location, location),
                    regions = $regions,
                    workplace = $workplace,
                    salary = $salary,
                    seniority = $seniority,
                    employment = $employment,
                    job_function = COALESCE($function, job_function),
                    industry = COALESCE($industry, industry),
                    stack_json = $stack,
                    summary = $summary,
                    description = $description,
                    posted_at = COALESCE($posted, posted_at),
                    listed_ago = COALESCE($listed, listed_ago),
                    insight = COALESCE($insight, insight),
                    apply_url = $apply,
                    source_url = COALESCE($sourceUrl, source_url),
                    logo_url = COALESCE(logo_url, $logo),
                    last_seen_at = $now,
                    relevant = $relevant
                WHERE id = $id;
                """;
            Bind(update, "$title", draft.Title);
            Bind(update, "$company", draft.Company);
            Bind(update, "$companyUrl", draft.CompanyUrl);
            Bind(update, "$location", draft.Location ?? current.Location);
            Bind(update, "$regions", regions);
            Bind(update, "$workplace", workplace);
            Bind(update, "$salary", salary);
            Bind(update, "$seniority", seniority);
            Bind(update, "$employment", employment);
            Bind(update, "$function", draft.JobFunction ?? current.JobFunction);
            Bind(update, "$industry", draft.Industry ?? current.Industry);
            Bind(update, "$stack", JsonSerializer.Serialize(stack));
            Bind(update, "$summary", summary);
            Bind(update, "$description", description);
            Bind(update, "$posted", draft.PostedAt);
            Bind(update, "$listed", draft.ListedAgo);
            Bind(update, "$insight", draft.Insight);
            Bind(update, "$apply", PreferApply(draft.ApplyUrl, current.ApplyUrl));
            Bind(update, "$sourceUrl", draft.SourceUrl ?? current.SourceUrl);
            Bind(update, "$logo", draft.LogoUrl);
            Bind(update, "$now", now);
            Bind(update, "$relevant", relevant);
            Bind(update, "$id", current.Id);
            update.ExecuteNonQuery();
            return new UpsertResult(current.Id, false);
        }
    }

    public void SaveDetail(long id, JobDraft draft)
    {
        lock (_gate)
        {
            using var connection = _db.Open();
            var current = FindById(connection, id);
            if (current == null)
                return;

            var stack = draft.Stack.Count > 0 ? draft.Stack.ToArray() : current.Stack;
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE jobs SET
                    title = $title,
                    company = $company,
                    company_url = COALESCE($companyUrl, company_url),
                    location = COALESCE($location, location),
                    workplace = $workplace,
                    salary = $salary,
                    seniority = $seniority,
                    employment = $employment,
                    job_function = $function,
                    industry = $industry,
                    stack_json = $stack,
                    summary = $summary,
                    description = $description,
                    posted_at = COALESCE($posted, posted_at),
                    listed_ago = COALESCE($listed, listed_ago),
                    insight = $insight,
                    apply_url = $apply,
                    logo_url = COALESCE($logo, logo_url),
                    relevant = $relevant,
                    detail_fetched_at = $now
                WHERE id = $id;
                """;
            Bind(command, "$title", string.IsNullOrWhiteSpace(draft.Title) ? current.Title : draft.Title);
            Bind(command, "$company", string.IsNullOrWhiteSpace(draft.Company) ? current.Company : draft.Company);
            Bind(command, "$companyUrl", draft.CompanyUrl);
            Bind(command, "$location", draft.Location);
            Bind(command, "$workplace", string.IsNullOrWhiteSpace(draft.Workplace) ? current.Workplace : draft.Workplace);
            Bind(command, "$salary", draft.Salary ?? current.Salary);
            Bind(command, "$seniority", draft.Seniority ?? current.Seniority);
            Bind(command, "$employment", draft.Employment ?? current.Employment);
            Bind(command, "$function", draft.JobFunction ?? current.JobFunction);
            Bind(command, "$industry", draft.Industry ?? current.Industry);
            Bind(command, "$stack", JsonSerializer.Serialize(stack));
            Bind(command, "$summary", Longer(draft.Summary, current.Summary));
            Bind(command, "$description", draft.Description ?? current.Description);
            Bind(command, "$posted", draft.PostedAt);
            Bind(command, "$listed", draft.ListedAgo);
            Bind(command, "$insight", draft.Insight ?? current.Insight);
            Bind(command, "$apply", PreferApply(draft.ApplyUrl, current.ApplyUrl));
            Bind(command, "$logo", draft.LogoUrl);
            Bind(command, "$relevant", draft.Relevant ?? current.Relevant);
            Bind(command, "$now", Stamp());
            Bind(command, "$id", id);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<JobDto> NeedingDetail(int limit)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
            FROM jobs
            WHERE detail_fetched_at IS NULL AND status != 'hidden'
            ORDER BY COALESCE(posted_at, first_seen_at) DESC, id DESC
            LIMIT $limit;
            """;
        Bind(command, "$limit", limit);
        var jobs = new List<JobDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            jobs.Add(Map(reader));
        return jobs;
    }

    public JobStats Stats()
    {
        using var connection = _db.Open();
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        return new JobStats(
            Scalar(connection, "SELECT COUNT(*) FROM jobs WHERE status = 'new'"),
            Scalar(connection, "SELECT COUNT(*) FROM jobs WHERE status = 'saved'"),
            Scalar(connection, "SELECT COUNT(*) FROM jobs WHERE status IN ('applied','interview','offer')"),
            Scalar(connection, "SELECT COUNT(*) FROM jobs WHERE status = 'hidden'"),
            Scalar(connection, "SELECT COUNT(*) FROM jobs WHERE substr(first_seen_at, 1, 10) = $today AND status != 'hidden'", command => Bind(command, "$today", today)),
            Scalar(connection, "SELECT COUNT(*) FROM jobs"));
    }

    public IReadOnlyList<ProfileDto> Profiles()
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, keywords, enabled, recency, pages, regions_json FROM search_profiles ORDER BY id;";
        var profiles = new List<ProfileDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            profiles.Add(ReadProfile(reader));
        return profiles;
    }

    public ProfileDto CreateProfile(ProfileInput input)
    {
        var profile = Normalize(input);
        lock (_gate)
        {
            using var connection = _db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO search_profiles (name, keywords, enabled, recency, pages, regions_json)
                VALUES ($name, $keywords, $enabled, $recency, $pages, $regions);
                SELECT last_insert_rowid();
                """;
            BindProfile(command, profile);
            var id = Convert.ToInt64(command.ExecuteScalar());
            return profile with { Id = id };
        }
    }

    public ProfileDto? UpdateProfile(long id, ProfileInput input)
    {
        var profile = Normalize(input) with { Id = id };
        lock (_gate)
        {
            using var connection = _db.Open();
            using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT COUNT(*) FROM search_profiles WHERE id = $id;";
            Bind(exists, "$id", id);
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0)
                return null;

            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE search_profiles
                SET name = $name, keywords = $keywords, enabled = $enabled,
                    recency = $recency, pages = $pages, regions_json = $regions
                WHERE id = $id;
                """;
            BindProfile(command, profile);
            Bind(command, "$id", id);
            command.ExecuteNonQuery();
            return profile;
        }
    }

    public bool DeleteProfile(long id)
    {
        lock (_gate)
        {
            using var connection = _db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM search_profiles WHERE id = $id;";
            Bind(command, "$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public long StartRun()
    {
        lock (_gate)
        {
            using var connection = _db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO search_runs (started_at, status, found_count, new_count)
                VALUES ($now, 'running', 0, 0);
                SELECT last_insert_rowid();
                """;
            Bind(command, "$now", Stamp());
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    public void FinishRun(long id, string status, string message, int found, int created)
    {
        lock (_gate)
        {
            using var connection = _db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE search_runs
                SET finished_at = $now, status = $status, message = $message,
                    found_count = $found, new_count = $created
                WHERE id = $id;
                """;
            Bind(command, "$now", Stamp());
            Bind(command, "$status", status);
            Bind(command, "$message", message);
            Bind(command, "$found", found);
            Bind(command, "$created", created);
            Bind(command, "$id", id);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<RunDto> RecentRuns(int limit)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, started_at, finished_at, status, message, found_count, new_count
            FROM search_runs
            ORDER BY id DESC
            LIMIT $limit;
            """;
        Bind(command, "$limit", limit);
        var runs = new List<RunDto>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            runs.Add(new RunDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6)));
        }
        return runs;
    }

    private static ProfileDto Normalize(ProfileInput input)
    {
        var name = (input.Name ?? "").Trim();
        var keywords = (input.Keywords ?? "").Trim();
        if (name.Length == 0 || keywords.Length == 0)
            throw new ArgumentException("Dê um nome e pelo menos um termo. Separe termos com |.");
        if (name.Length > 80)
            throw new ArgumentException("O nome da busca ficou longo demais.");

        var recency = input.Recency is "day" or "week" or "month" ? input.Recency : "week";
        var pages = Math.Clamp(input.Pages <= 0 ? 40 : input.Pages, 1, 40);
        var regions = (input.Regions ?? [])
            .Where(key => RegionCatalog.Find(key) != null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (regions.Length == 0)
            throw new ArgumentException("Escolha ao menos uma região.");

        return new ProfileDto(0, name, keywords, input.Enabled, recency, pages, regions);
    }

    private static void BindProfile(SqliteCommand command, ProfileDto profile)
    {
        Bind(command, "$name", profile.Name);
        Bind(command, "$keywords", profile.Keywords);
        Bind(command, "$enabled", profile.Enabled ? 1 : 0);
        Bind(command, "$recency", profile.Recency);
        Bind(command, "$pages", profile.Pages);
        Bind(command, "$regions", JsonSerializer.Serialize(profile.Regions));
    }

    private static ProfileDto ReadProfile(SqliteDataReader reader)
    {
        var regions = JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [];
        return new ProfileDto(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3) != 0,
            reader.GetString(4),
            reader.GetInt32(5),
            regions);
    }

    private void BindJob(SqliteCommand command, JobDraft draft, string regions, string now)
    {
        Bind(command, "$source", draft.Source);
        Bind(command, "$external", draft.ExternalId);
        Bind(command, "$title", draft.Title);
        Bind(command, "$company", draft.Company);
        Bind(command, "$companyUrl", draft.CompanyUrl);
        Bind(command, "$location", draft.Location);
        Bind(command, "$regions", regions);
        Bind(command, "$workplace", string.IsNullOrWhiteSpace(draft.Workplace) ? "unknown" : draft.Workplace);
        Bind(command, "$salary", draft.Salary);
        Bind(command, "$seniority", draft.Seniority);
        Bind(command, "$employment", draft.Employment);
        Bind(command, "$function", draft.JobFunction);
        Bind(command, "$industry", draft.Industry);
        Bind(command, "$stack", JsonSerializer.Serialize(draft.Stack));
        Bind(command, "$summary", draft.Summary);
        Bind(command, "$description", draft.Description);
        Bind(command, "$posted", draft.PostedAt);
        Bind(command, "$listed", draft.ListedAgo);
        Bind(command, "$insight", draft.Insight);
        Bind(command, "$apply", draft.ApplyUrl);
        Bind(command, "$sourceUrl", draft.SourceUrl);
        Bind(command, "$logo", draft.LogoUrl);
        Bind(command, "$now", now);
        Bind(command, "$relevant", draft.Relevant);
    }

    private static JobDto? FindById(SqliteConnection connection, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM jobs WHERE id = $id;";
        Bind(command, "$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static JobDto? FindByExternal(SqliteConnection connection, string source, string externalId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM jobs WHERE source = $source AND external_id = $external;";
        Bind(command, "$source", source);
        Bind(command, "$external", externalId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static JobDto Map(SqliteDataReader reader)
    {
        string? Text(string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        int? NullableInt(string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
        }

        var stack = JsonSerializer.Deserialize<string[]>(Text("stack_json") ?? "[]") ?? [];
        var regions = (Text("regions") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var description = Text("description");
        return new JobDto(
            reader.GetInt64(reader.GetOrdinal("id")),
            Text("source") ?? "",
            Text("external_id") ?? "",
            Text("title") ?? "",
            Text("company") ?? "",
            Text("company_url"),
            Text("location"),
            regions,
            Text("workplace") ?? "unknown",
            Text("salary"),
            Text("seniority"),
            Text("employment"),
            Text("job_function"),
            Text("industry"),
            stack,
            Text("summary"),
            description,
            Text("posted_at"),
            Text("listed_ago"),
            Text("insight"),
            Text("apply_url"),
            Text("source_url"),
            Text("logo_url"),
            Text("status") ?? "new",
            Text("note"),
            Text("applied_at"),
            Text("first_seen_at") ?? "",
            Text("last_seen_at") ?? "",
            Text("detail_fetched_at"),
            !string.IsNullOrWhiteSpace(description),
            NullableInt("relevant"));
    }

    private static int Scalar(SqliteConnection connection, string sql, Action<SqliteCommand>? bind = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string MergeRegions(IEnumerable<string> current, IEnumerable<string> incoming) =>
        RegionCatalog.Join(current.Concat(incoming));

    private static string[] UnionStack(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Concat(right)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

    private static string MergeWorkplace(string current, string incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming) || incoming == "unknown")
            return string.IsNullOrWhiteSpace(current) ? "unknown" : current;
        if (string.IsNullOrWhiteSpace(current) || current == "unknown")
            return incoming;
        if (current == incoming)
            return current;
        if ((current == "remote" && incoming == "onsite") || (current == "onsite" && incoming == "remote"))
            return "hybrid";
        if (current == "hybrid" || incoming == "hybrid")
            return "hybrid";
        return incoming;
    }

    private static string? PreferApply(string? incoming, string? current) =>
        IsExternal(incoming) ? incoming : IsExternal(current) ? current : incoming ?? current;

    private static bool IsExternal(string? url) =>
        !string.IsNullOrWhiteSpace(url) && !url.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase);

    private static string? Longer(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return string.IsNullOrWhiteSpace(right) ? null : right;
        if (string.IsNullOrWhiteSpace(right))
            return left;
        return right.Length > left.Length ? right : left;
    }

    private static string Like(string text)
    {
        var cleaned = text.Trim().Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        return $"%{cleaned}%";
    }

    private static string Stamp() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

    private static void Bind(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
