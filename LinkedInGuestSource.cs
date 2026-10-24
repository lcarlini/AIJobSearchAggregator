using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;

namespace AIJobSearchAggregator;

public sealed class LinkedInGuestSource : IJobSource
{
    private readonly HttpClient _http;
    private readonly ActivityLog _log;
    private readonly SemaphoreSlim _pace = new(1, 1);
    private readonly int _delay;
    private DateTime _next = DateTime.UtcNow;

    public LinkedInGuestSource(IHttpClientFactory factory, IOptions<SearchOptions> options, ActivityLog log)
    {
        _http = factory.CreateClient("linkedin");
        _log = log;
        _delay = Math.Clamp(options.Value.DelayMilliseconds, 1500, 15000);
    }

    public string Key => "linkedin";
    public string DisplayName => "LinkedIn";

    public async Task<IReadOnlyList<JobDraft>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var accepted = new List<JobDraft>();
        var seen = new HashSet<string>();
        var skipped = 0;
        var skippedSamples = new List<string>();
        var places = request.Region.SearchLocations.Length > 0
            ? request.Region.SearchLocations
            : [request.Region.Location ?? ""];

        for (var placeIndex = 0; placeIndex < places.Length; placeIndex++)
        {
            var place = places[placeIndex];
            var pages = placeIndex == 0 ? request.Pages : 1;
            var distance = placeIndex == 0 ? request.Region.DistanceMiles : 10;
            var start = 0;
            var foundHere = 0;
            var failed = false;

            for (var page = 1; page <= pages; page++)
            {
                var fetched = await GetWithRetryAsync(BuildUrl(request, place, distance, start), cancellationToken);
                if (fetched.Status == 429 || fetched.Body == null)
                {
                    _log.Warn($"{request.Region.Name} · {request.Keywords}: a página {page} não chegou. O que já entrou desta consulta está salvo.");
                    failed = true;
                    break;
                }

                var cards = LinkedInHtml.ParseCards(fetched.Body);
                if (cards.Count == 0)
                {
                    _log.Info($"{request.Region.Name} · {request.Keywords}: o LinkedIn não devolveu mais anúncios.");
                    break;
                }

                var pageAccepted = 0;
                var fresh = 0;
                foreach (var card in cards)
                {
                    if (!seen.Add(card.ExternalId))
                        continue;
                    fresh++;
                    if (!request.Region.AcceptsLocation(card.Location))
                    {
                        skipped++;
                        if (skippedSamples.Count < 4 && !string.IsNullOrWhiteSpace(card.Location) && !skippedSamples.Contains(card.Location))
                            skippedSamples.Add(card.Location);
                        continue;
                    }

                    card.Source = Key;
                    card.RegionKey = request.Region.Key;
                    card.RemoteHint = request.Region.RemoteOnly;
                    TextDigest.Apply(card);
                    accepted.Add(card);
                    pageAccepted++;
                    foundHere++;
                }

                var where = string.IsNullOrWhiteSpace(place) ? request.Region.Name : place;
                _log.Info($"{DisplayName} · {where} · {request.Keywords} · página {page}: {cards.Count} anúncios, {pageAccepted} na região.");
                if (fresh == 0)
                {
                    _log.Info($"{where} · {request.Keywords}: a página repetiu anúncios já vistos. Parei.");
                    break;
                }
                if (cards.Count < 8)
                    break;
                if (page == pages)
                    _log.Warn($"{where} · {request.Keywords}: a lista ainda não acabou na página {page}. Suba o número de páginas da busca.");
                start += cards.Count;
            }

            if (foundHere == 0 && placeIndex == 0 && !failed)
                _log.Info($"{request.Region.Name} · {request.Keywords}: nenhum anúncio nessa combinação.");
        }

        if (skipped > 0)
        {
            var sample = skippedSamples.Count == 0 ? "" : $" ({string.Join(", ", skippedSamples)})";
            var noun = skipped == 1 ? "anúncio" : "anúncios";
            _log.Info($"{request.Region.Name}: {skipped} {noun} de fora da região foram ignorados{sample}.");
        }

        return accepted;
    }

    public async Task<bool> TryFillDetailAsync(JobDraft job, CancellationToken cancellationToken)
    {
        var guestUrl = $"https://www.linkedin.com/jobs-guest/jobs/api/jobPosting/{job.ExternalId}";
        var fetched = await GetWithRetryAsync(guestUrl, cancellationToken);
        if (fetched.Status == 404)
        {
            job.Unavailable = true;
            job.DetailComplete = true;
            job.Insight = string.IsNullOrWhiteSpace(job.Insight) ? "Anúncio indisponível" : job.Insight;
            TextDigest.Apply(job);
            return true;
        }

        if (fetched.Status != 200 || fetched.Body == null)
            return false;

        if (LinkedInHtml.LooksBlocked(fetched.Body))
        {
            _log.Warn($"{job.Title}: o LinkedIn mostrou uma tela de login. Tento de novo na próxima abertura.");
            return false;
        }

        LinkedInHtml.FillDetail(job, fetched.Body);
        if (!job.DetailComplete)
        {
            var view = await GetWithRetryAsync($"https://www.linkedin.com/jobs/view/{job.ExternalId}", cancellationToken);
            if (view.Status == 200 && view.Body != null && !LinkedInHtml.LooksBlocked(view.Body))
                LinkedInHtml.FillDetail(job, view.Body);
        }

        if (!job.DetailComplete)
            _log.Warn($"{job.Title}: a página pública não trouxe a descrição. O link para se candidatar fica salvo.");

        job.DetailComplete = true;
        TextDigest.Apply(job);
        return true;
    }

    private async Task<Fetch> GetWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        var fetched = new Fetch(null, 0);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            fetched = await GetAsync(url, cancellationToken);
            if (fetched.Status != 429)
                return fetched;
        }

        return fetched;
    }

    private async Task<Fetch> GetAsync(string url, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wait = _next - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);

            await _pace.WaitAsync(cancellationToken);
            try
            {
                if (DateTime.UtcNow < _next)
                    continue;

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Referrer = new Uri("https://www.linkedin.com/jobs/search");
                using var response = await _http.SendAsync(request, cancellationToken);
                var status = (int)response.StatusCode;
                _next = DateTime.UtcNow.AddMilliseconds(status == 429 ? 18000 : _delay);

                if (status == 429)
                {
                    _log.Warn("LinkedIn pediu uma pausa. Esperando para não tomar bloqueio.");
                    return new Fetch(null, 429);
                }

                if (status == 404)
                    return new Fetch(null, 404);

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (status is < 200 or >= 300)
                {
                    _log.Warn($"LinkedIn respondeu {status}.");
                    return new Fetch(null, status);
                }

                return new Fetch(body, 200);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _next = DateTime.UtcNow.AddMilliseconds(_delay);
                _log.Warn($"Falha de rede: {ex.Message}");
                return new Fetch(null, 0);
            }
            finally
            {
                _pace.Release();
            }
        }
    }

    private static string BuildUrl(SearchRequest request, string? location, int? distanceMiles, int start)
    {
        var parts = new List<string>
        {
            "keywords=" + Uri.EscapeDataString(request.Keywords),
            "start=" + start.ToString(CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(request.Region.GeoId))
            parts.Add("geoId=" + Uri.EscapeDataString(request.Region.GeoId));
        if (!string.IsNullOrWhiteSpace(location))
            parts.Add("location=" + Uri.EscapeDataString(location));
        if (distanceMiles is int miles)
            parts.Add("distance=" + miles.ToString(CultureInfo.InvariantCulture));
        if (request.Region.RemoteOnly)
            parts.Add("f_WT=2");

        var recency = request.Recency switch
        {
            "day" => "r86400",
            "week" => "r604800",
            "month" => "r2592000",
            _ => null
        };
        if (recency != null)
            parts.Add("f_TPR=" + recency);

        return "https://www.linkedin.com/jobs-guest/jobs/api/seeMoreJobPostings/search?" + string.Join('&', parts);
    }

    private readonly record struct Fetch(string? Body, int Status);
}
