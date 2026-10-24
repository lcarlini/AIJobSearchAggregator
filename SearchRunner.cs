using Microsoft.Extensions.Options;

namespace AIJobSearchAggregator;

public sealed class SearchRunner
{
    private readonly JobRepository _repository;
    private readonly IReadOnlyList<IJobSource> _sources;
    private readonly ActivityLog _log;
    private readonly SearchOptions _options;
    private readonly SemaphoreSlim _run = new(1, 1);
    private int _running;

    public SearchRunner(JobRepository repository, IEnumerable<IJobSource> sources, ActivityLog log, IOptions<SearchOptions> options)
    {
        _repository = repository;
        _sources = sources.ToArray();
        _log = log;
        _options = options.Value;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!await _run.WaitAsync(0, cancellationToken))
        {
            _log.Info("Já existe uma busca em andamento.");
            return;
        }

        Interlocked.Exchange(ref _running, 1);
        var runId = _repository.StartRun();
        var found = 0;
        var created = 0;
        try
        {
            var profiles = _repository.Profiles().Where(profile => profile.Enabled).ToArray();
            if (profiles.Length == 0)
            {
                _log.Warn("Nenhuma busca ativa. Crie uma na lateral da tela.");
                _repository.FinishRun(runId, "ok", "Nenhuma busca ativa.", 0, 0);
                return;
            }

            _log.Info("Busca começou. As vagas aparecem na tela conforme entram.");
            var pageBudget = Math.Clamp(_options.MaxPagesPerRun, 1, 800);
            var pagesUsed = 0;
            var stop = false;

            foreach (var profile in profiles)
            {
                var terms = profile.Keywords.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (terms.Length == 0)
                    continue;

                foreach (var source in _sources)
                {
                    foreach (var regionKey in profile.Regions)
                    {
                        var region = RegionCatalog.Find(regionKey);
                        if (region == null)
                            continue;

                        foreach (var term in terms)
                        {
                            if (pagesUsed >= pageBudget)
                            {
                                _log.Warn("Parei para não sobrecarregar o LinkedIn. O que já entrou está salvo.");
                                stop = true;
                                break;
                            }

                            var pages = Math.Min(profile.Pages, pageBudget - pagesUsed);
                            pagesUsed += pages;
                            IReadOnlyList<JobDraft> jobs;
                            try
                            {
                                jobs = await source.SearchAsync(new SearchRequest(term, region, profile.Recency, pages), cancellationToken);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                _log.Error($"{source.DisplayName} · {region.Name} · {term}: {ex.Message}");
                                continue;
                            }

                            foreach (var job in jobs)
                            {
                                var result = _repository.UpsertFromSearch(job);
                                found++;
                                if (!result.IsNew)
                                    continue;
                                created++;
                                _log.Info($"Nova: {job.Title} — {job.Company} ({job.Location ?? "local não informado"})");
                            }
                        }

                        if (stop)
                            break;
                    }

                    if (stop)
                        break;
                }

                if (stop)
                    break;
            }

            var limit = Math.Clamp(_options.MaxDetailsPerRun, 1, 20);
            var details = _repository.NeedingDetail(limit);
            if (details.Count > 0)
                _log.Info($"Lendo a descrição de {details.Count} vaga(s). O resto abre quando você clicar.");

            var sourceByKey = _sources.ToDictionary(source => source.Key);
            foreach (var job in details)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sourceByKey.TryGetValue(job.Source, out var source))
                    continue;

                var draft = JobDraft.From(job);
                try
                {
                    var filled = await source.TryFillDetailAsync(draft, cancellationToken);
                    if (filled)
                        _repository.SaveDetail(job.Id, draft);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn($"Não li {job.Title}: {ex.Message}");
                }
            }

            var message = created == 0
                ? $"Nenhuma vaga nova. {found} anúncios já estavam salvos."
                : $"{created} nova(s), {Math.Max(found - created, 0)} já conhecida(s).";
            _log.Info("Busca pronta. " + message);
            _repository.FinishRun(runId, "ok", message, found, created);
        }
        catch (OperationCanceledException)
        {
            _log.Warn("Busca interrompida.");
            _repository.FinishRun(runId, "canceled", "Interrompida.", found, created);
        }
        catch (Exception ex)
        {
            _log.Error("A busca parou: " + ex.Message);
            _repository.FinishRun(runId, "error", ex.Message, found, created);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            _run.Release();
        }
    }
}

public sealed class StartupSearch : BackgroundService
{
    private readonly SearchRunner _runner;
    private readonly ActivityLog _log;

    public StartupSearch(SearchRunner runner, ActivityLog log)
    {
        _runner = runner;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VAGAS_SKIP_STARTUP"), "1", StringComparison.Ordinal))
                return;

            await Task.Delay(700, stoppingToken);
            await _runner.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error("A busca inicial falhou: " + ex.Message);
        }
    }
}
