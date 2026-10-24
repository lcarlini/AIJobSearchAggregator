namespace AIJobSearchAggregator;

public sealed class JobDraft
{
    public string Source { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Company { get; set; } = "";
    public string? CompanyUrl { get; set; }
    public string? Location { get; set; }
    public string RegionKey { get; set; } = "";
    public bool RemoteHint { get; set; }
    public string Workplace { get; set; } = "unknown";
    public string? SalaryRaw { get; set; }
    public string? Salary { get; set; }
    public string? SeniorityRaw { get; set; }
    public string? Seniority { get; set; }
    public string? EmploymentRaw { get; set; }
    public string? Employment { get; set; }
    public string? JobFunction { get; set; }
    public string? Industry { get; set; }
    public List<string> Stack { get; set; } = [];
    public string? DescriptionHtml { get; set; }
    public string? Description { get; set; }
    public string? Summary { get; set; }
    public string? PostedAt { get; set; }
    public string? ListedAgo { get; set; }
    public string? Insight { get; set; }
    public string? ApplyUrl { get; set; }
    public string? SourceUrl { get; set; }
    public string? LogoUrl { get; set; }
    public bool DetailComplete { get; set; }
    public bool Unavailable { get; set; }
    public int? Relevant { get; set; }

    public static JobDraft From(JobDto job) => new()
    {
        Source = job.Source,
        ExternalId = job.ExternalId,
        Title = job.Title,
        Company = job.Company,
        CompanyUrl = job.CompanyUrl,
        Location = job.Location,
        RegionKey = job.Regions.FirstOrDefault() ?? "",
        RemoteHint = job.Regions.Contains("remoto"),
        Workplace = job.Workplace,
        Salary = job.Salary,
        Seniority = job.Seniority,
        Employment = job.Employment,
        JobFunction = job.JobFunction,
        Industry = job.Industry,
        Stack = job.Stack.ToList(),
        Description = job.Description,
        Summary = job.Summary,
        PostedAt = job.PostedAt,
        ListedAgo = job.ListedAgo,
        Insight = job.Insight,
        ApplyUrl = job.ApplyUrl,
        SourceUrl = job.SourceUrl,
        LogoUrl = job.LogoUrl,
        DetailComplete = job.DetailFetchedAt != null,
        Relevant = job.Relevant
    };
}

public sealed record JobDto(
    long Id,
    string Source,
    string ExternalId,
    string Title,
    string Company,
    string? CompanyUrl,
    string? Location,
    string[] Regions,
    string Workplace,
    string? Salary,
    string? Seniority,
    string? Employment,
    string? JobFunction,
    string? Industry,
    string[] Stack,
    string? Summary,
    string? Description,
    string? PostedAt,
    string? ListedAgo,
    string? Insight,
    string? ApplyUrl,
    string? SourceUrl,
    string? LogoUrl,
    string Status,
    string? Note,
    string? AppliedAt,
    string FirstSeenAt,
    string LastSeenAt,
    string? DetailFetchedAt,
    bool HasDescription,
    int? Relevant);

public sealed record ProfileDto(
    long Id,
    string Name,
    string Keywords,
    bool Enabled,
    string Recency,
    int Pages,
    string[] Regions);

public sealed record ProfileInput(
    string? Name,
    string? Keywords,
    bool Enabled,
    string? Recency,
    int Pages,
    string[]? Regions);

public sealed record JobPatch(string? Status, string? Note);

public sealed record RunDto(
    long Id,
    string StartedAt,
    string? FinishedAt,
    string Status,
    string? Message,
    int FoundCount,
    int NewCount);

public sealed record JobQuery(
    string? Text,
    string? Status,
    string? Region,
    string? Workplace,
    bool IncludeHidden,
    bool OnlyRelevant,
    bool OnlySalary);

public sealed record UpsertResult(long Id, bool IsNew);

public sealed record SearchRequest(
    string Keywords,
    Region Region,
    string Recency,
    int Pages);

public static class JobStatuses
{
    public static readonly HashSet<string> All =
    [
        "new", "seen", "saved", "applied", "interview", "offer", "rejected", "hidden"
    ];

    public static bool IsApplication(string status) =>
        status is "applied" or "interview" or "offer";
}

public sealed class SearchOptions
{
    public int DelayMilliseconds { get; set; } = 2800;
    public int MaxDetailsPerRun { get; set; } = 8;
    public int MaxPagesPerRun { get; set; } = 700;
    public int SkipPages { get; set; }
}
