using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace AIJobSearchAggregator;

public static partial class LinkedInHtml
{
    public static List<JobDraft> ParseCards(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var jobs = new List<JobDraft>();
        foreach (var card in document.QuerySelectorAll("div.base-search-card"))
        {
            var urn = card.GetAttribute("data-entity-urn") ?? "";
            var id = urn.Split(':').LastOrDefault() ?? "";
            if (id.Length == 0 || !id.All(char.IsDigit))
                continue;

            var title = Clean(card.QuerySelector(".base-search-card__title")?.TextContent);
            if (title.Length == 0)
                continue;

            var companyLink = card.QuerySelector(".base-search-card__subtitle a");
            var company = Clean(card.QuerySelector(".base-search-card__subtitle")?.TextContent);
            var location = Clean(card.QuerySelector(".job-search-card__location")?.TextContent);
            var time = card.QuerySelector("time");
            var logo = card.QuerySelector("img")?.GetAttribute("data-delayed-url")
                ?? card.QuerySelector("img")?.GetAttribute("src");
            var viewUrl = $"https://www.linkedin.com/jobs/view/{id}";

            jobs.Add(new JobDraft
            {
                ExternalId = id,
                Title = title,
                Company = company,
                CompanyUrl = StripQuery(companyLink?.GetAttribute("href")),
                Location = NullIfEmpty(location),
                PostedAt = NullIfEmpty(time?.GetAttribute("datetime")),
                ListedAgo = NullIfEmpty(Clean(time?.TextContent)),
                Insight = NullIfEmpty(Clean(card.QuerySelector(".job-posting-benefits__text")?.TextContent)),
                LogoUrl = UsefulLogo(logo),
                SourceUrl = viewUrl,
                ApplyUrl = viewUrl
            });
        }

        return jobs;
    }

    public static bool LooksBlocked(string html) =>
        html.Contains("authwall", StringComparison.OrdinalIgnoreCase)
        && !html.Contains("show-more-less-html__markup", StringComparison.OrdinalIgnoreCase);

    public static void FillDetail(JobDraft job, string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        var markup = document.QuerySelector(".show-more-less-html__markup");
        if (markup != null && string.IsNullOrWhiteSpace(job.DescriptionHtml))
            job.DescriptionHtml = markup.InnerHtml;

        var salary = Clean(document.QuerySelector(".compensation__salary, .compensation__salary-range, .salary")?.TextContent);
        if (salary.Length > 0)
            job.SalaryRaw = salary;

        foreach (var item in document.QuerySelectorAll(".description__job-criteria-item, li.description__job-criteria-item"))
        {
            var heading = Region.Fold(Clean(item.QuerySelector("h3")?.TextContent));
            var value = NullIfEmpty(Clean(item.QuerySelector("span")?.TextContent));
            if (value == null)
                continue;
            if (heading.Contains("senior", StringComparison.Ordinal) || heading.Contains("senioridade", StringComparison.Ordinal))
                job.SeniorityRaw = value;
            else if (heading.Contains("employ", StringComparison.Ordinal) || heading.Contains("emprego", StringComparison.Ordinal) || heading.Contains("tipo de", StringComparison.Ordinal))
                job.EmploymentRaw = value;
            else if (heading.Contains("function", StringComparison.Ordinal) || heading.Contains("funcao", StringComparison.Ordinal) || heading.Contains("funç", StringComparison.Ordinal))
                job.JobFunction = value;
            else if (heading.Contains("industr", StringComparison.Ordinal) || heading.Contains("setor", StringComparison.Ordinal))
                job.Industry = value;
        }

        var external = ExternalApply(html);
        if (external != null)
            job.ApplyUrl = external;

        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
            ReadJsonLd(job, script.TextContent);

        var detailLogo = document.QuerySelector("img.artdeco-entity-image")?.GetAttribute("data-delayed-url");
        job.LogoUrl ??= UsefulLogo(detailLogo);

        job.DetailComplete = !string.IsNullOrWhiteSpace(job.DescriptionHtml)
            || !string.IsNullOrWhiteSpace(job.SeniorityRaw)
            || !string.IsNullOrWhiteSpace(job.SalaryRaw);
    }

    private static void ReadJsonLd(JobDraft job, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            Walk(document.RootElement);
        }
        catch (JsonException)
        {
            return;
        }

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Walk(item);
                    return;
                case JsonValueKind.Object:
                    break;
                default:
                    return;
            }

            if (element.TryGetProperty("@graph", out var graph))
                Walk(graph);

            var type = element.TryGetProperty("@type", out var typeElement) ? JsonText(typeElement) : null;
            if (type == null || !type.Contains("JobPosting", StringComparison.OrdinalIgnoreCase))
                return;

            if (string.IsNullOrWhiteSpace(job.DescriptionHtml)
                && element.TryGetProperty("description", out var description))
                job.DescriptionHtml = JsonText(description);

            if (element.TryGetProperty("title", out var title) && string.IsNullOrWhiteSpace(job.Title))
                job.Title = JsonText(title) ?? job.Title;

            if (element.TryGetProperty("datePosted", out var posted) && string.IsNullOrWhiteSpace(job.PostedAt))
                job.PostedAt = JsonText(posted);

            if (element.TryGetProperty("employmentType", out var employment) && string.IsNullOrWhiteSpace(job.EmploymentRaw))
                job.EmploymentRaw = JsonText(employment);

            if (element.TryGetProperty("jobLocationType", out var locationType)
                && (JsonText(locationType)?.Contains("TELECOMMUTE", StringComparison.OrdinalIgnoreCase) ?? false))
                job.RemoteHint = true;

            if (string.IsNullOrWhiteSpace(job.SalaryRaw) && element.TryGetProperty("baseSalary", out var salary))
                job.SalaryRaw = ReadSalary(salary);

            if (string.IsNullOrWhiteSpace(job.Location) && element.TryGetProperty("jobLocation", out var place))
                job.Location = ReadPlace(place);
        }
    }

    private static string? ReadSalary(JsonElement salary)
    {
        if (salary.ValueKind == JsonValueKind.String)
            return NullIfEmpty(salary.GetString());
        if (salary.ValueKind != JsonValueKind.Object)
            return null;

        var currency = salary.TryGetProperty("currency", out var currencyElement) ? currencyElement.GetString() : "BRL";
        if (!salary.TryGetProperty("value", out var value))
            return null;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();
        if (value.ValueKind != JsonValueKind.Object)
            return null;

        decimal? min = ReadDecimal(value, "minValue") ?? ReadDecimal(value, "value");
        decimal? max = ReadDecimal(value, "maxValue");
        if (min == null && max == null)
            return null;

        var unit = TextDigest.UnitLabel(value.TryGetProperty("unitText", out var unitElement) ? unitElement.GetString() : null);
        var low = TextDigest.FormatMoney(currency, min ?? max!.Value);
        var high = max != null && min != null && max != min ? TextDigest.FormatMoney(currency, max.Value) : null;
        var range = high == null ? low : $"{low} – {high}";
        return unit == null ? range : $"{range} / {unit}";
    }

    private static string? ReadPlace(JsonElement place)
    {
        if (place.ValueKind == JsonValueKind.Array)
            place = place.EnumerateArray().FirstOrDefault();
        if (place.ValueKind != JsonValueKind.Object || !place.TryGetProperty("address", out var address))
            return null;

        var parts = new[] { "addressLocality", "addressRegion", "addressCountry" }
            .Select(name => address.TryGetProperty(name, out var part) ? JsonText(part) : null)
            .Where(part => !string.IsNullOrWhiteSpace(part));
        var text = string.Join(", ", parts);
        return NullIfEmpty(text);
    }

    private static string? JsonText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Array:
                var parts = element.EnumerateArray().Select(JsonText).Where(part => !string.IsNullOrWhiteSpace(part));
                var joined = string.Join(", ", parts);
                return joined.Length == 0 ? null : joined;
            case JsonValueKind.Object when element.TryGetProperty("name", out var name):
                return JsonText(name);
            default:
                return null;
        }
    }

    private static decimal? ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var number))
            return number;
        return null;
    }

    private static string? ExternalApply(string html)
    {
        var code = ApplyCode().Match(html);
        if (code.Success)
            return ExternalOrNull(WebUtility.HtmlDecode(code.Groups[1].Value));

        var link = ApplyLink().Match(html);
        if (!link.Success)
            link = ApplyLinkReversed().Match(html);
        return link.Success ? ExternalOrNull(WebUtility.HtmlDecode(link.Groups[1].Value)) : null;
    }

    private static string? ExternalOrNull(string url)
    {
        var clean = StripQuery(url);
        if (clean == null || clean.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase))
            return null;
        return clean;
    }

    private static string? StripQuery(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var value = WebUtility.HtmlDecode(url).Trim();
        var cut = value.IndexOfAny(['?', '#']);
        if (cut >= 0)
            value = value[..cut];
        return value.Length == 0 ? null : value;
    }

    private static string? UsefulLogo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Contains("ghost", StringComparison.OrdinalIgnoreCase))
            return null;
        return WebUtility.HtmlDecode(url);
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return Whitespace().Replace(value, " ").Trim();
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"id=[""']applyUrl[""'][^>]*>\s*(?:<!--\s*)?[""'](https?://[^""'\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ApplyCode();

    [GeneratedRegex(@"<a[^>]*class=[""'][^""']*apply-button[^""']*[""'][^>]*href=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ApplyLink();

    [GeneratedRegex(@"<a[^>]*href=[""']([^""']+)[""'][^>]*class=[""'][^""']*apply-button", RegexOptions.IgnoreCase)]
    private static partial Regex ApplyLinkReversed();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
