using System.Diagnostics;
using System.Net;
using System.Text;
using AIJobSearchAggregator;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    builder.WebHost.UseUrls("http://localhost:5088");

builder.Services.AddSingleton<ActivityLog>();
builder.Services.AddSingleton(services =>
{
    var env = services.GetRequiredService<IWebHostEnvironment>();
    var database = new Database(Path.Combine(env.ContentRootPath, "data", "jobs.db"));
    database.Initialize();
    return database;
});
builder.Services.AddSingleton<JobRepository>();
builder.Services.Configure<SearchOptions>(builder.Configuration.GetSection("Search"));
builder.Services.AddHttpClient("linkedin", client =>
{
    client.Timeout = TimeSpan.FromSeconds(40);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en;q=0.8");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.All
});
builder.Services.AddSingleton<LinkedInGuestSource>();
builder.Services.AddSingleton<IJobSource>(services => services.GetRequiredService<LinkedInGuestSource>());
builder.Services.AddSingleton<SearchRunner>();
builder.Services.AddHostedService<StartupSearch>();

var app = builder.Build();

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new { error = "Falha interna. Veja o console." });
}));

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var name = context.File.Name;
        if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
});

var api = app.MapGroup("/api");

api.MapGet("/meta", () => Results.Ok(new
{
    regions = RegionCatalog.All.Select(region => new
    {
        region.Key,
        region.Name,
        region.Summary,
        region.Cities,
        region.RemoteOnly
    }),
    statuses = new[]
    {
        new { key = "new", label = "Nova" },
        new { key = "seen", label = "Vista" },
        new { key = "saved", label = "Salva" },
        new { key = "applied", label = "Candidatado" },
        new { key = "interview", label = "Entrevista" },
        new { key = "offer", label = "Oferta" },
        new { key = "rejected", label = "Recusado" },
        new { key = "hidden", label = "Oculta" }
    }
}));

api.MapGet("/status", (SearchRunner runner, ActivityLog log, JobRepository repository) => Results.Ok(new
{
    running = runner.IsRunning,
    log = log.Snapshot(),
    runs = repository.RecentRuns(6),
    stats = repository.Stats()
}));

api.MapGet("/jobs", (JobRepository repository, string? q, string? status, string? region, string? workplace, bool? includeHidden, bool? onlyRelevant, bool? onlySalary) =>
    Results.Ok(repository.List(new JobQuery(
        q,
        status,
        region,
        workplace,
        includeHidden ?? false,
        onlyRelevant ?? true,
        onlySalary ?? false))));

api.MapGet("/jobs/{id:long}", (long id, bool? markSeen, JobRepository repository) =>
{
    var job = markSeen == true ? repository.MarkSeen(id) : repository.Get(id);
    return job == null ? Results.NotFound() : Results.Ok(job);
});

api.MapPatch("/jobs/{id:long}", (long id, JobPatch patch, JobRepository repository) =>
{
    try
    {
        var job = repository.Update(id, patch.Status, patch.Note);
        return job == null ? Results.NotFound() : Results.Ok(job);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/jobs/{id:long}/detail", async (long id, JobRepository repository, IEnumerable<IJobSource> sources, CancellationToken cancellationToken) =>
{
    var job = repository.Get(id);
    if (job == null)
        return Results.NotFound();

    var source = sources.FirstOrDefault(item => item.Key == job.Source);
    if (source == null)
        return Results.BadRequest(new { error = "Fonte não disponível." });

    var draft = JobDraft.From(job);
    var filled = await source.TryFillDetailAsync(draft, cancellationToken);
    if (!filled)
        return Results.Json(new { error = "O LinkedIn não respondeu agora. Tente de novo em instantes." }, statusCode: StatusCodes.Status503ServiceUnavailable);

    repository.SaveDetail(id, draft);
    return Results.Ok(repository.Get(id));
});

api.MapGet("/profiles", (JobRepository repository) => Results.Ok(repository.Profiles()));

api.MapPost("/profiles", (ProfileInput input, JobRepository repository) =>
{
    try
    {
        return Results.Ok(repository.CreateProfile(input));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPut("/profiles/{id:long}", (long id, ProfileInput input, JobRepository repository) =>
{
    try
    {
        var profile = repository.UpdateProfile(id, input);
        return profile == null ? Results.NotFound() : Results.Ok(profile);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapDelete("/profiles/{id:long}", (long id, JobRepository repository) =>
    repository.DeleteProfile(id) ? Results.NoContent() : Results.NotFound());

api.MapPost("/search", (SearchRunner runner, IHostApplicationLifetime lifetime) =>
{
    _ = runner.RunAsync(lifetime.ApplicationStopping);
    return Results.Accepted("/api/status");
});

app.MapFallbackToFile("index.html");

app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "http://localhost:5088",
            UseShellExecute = true
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Não abri o navegador ({ex.Message}). Acesse http://localhost:5088");
    }
});

Console.WriteLine();
Console.WriteLine("  Vagas");
Console.WriteLine("  http://localhost:5088");
Console.WriteLine("  A busca no LinkedIn começa junto com esta janela.");
Console.WriteLine();

app.Run();
