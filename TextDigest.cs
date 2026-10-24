using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace AIJobSearchAggregator;

public static partial class TextDigest
{
    public static void Apply(JobDraft job)
    {
        var plain = ToPlain(job.DescriptionHtml);
        if (!string.IsNullOrWhiteSpace(plain))
            job.Description = plain;

        var salary = FindSalary(job.SalaryRaw, job.Description);
        if (!string.IsNullOrWhiteSpace(salary))
            job.Salary = salary;

        var stack = ExtractStack($"{job.Title}\n{job.Description}");
        if (stack.Count > 0)
            job.Stack = stack;

        job.Seniority = InferSeniority(job.Title, job.SeniorityRaw) ?? job.Seniority;
        job.Employment = InferEmployment(job.EmploymentRaw, job.Description) ?? job.Employment;
        job.Workplace = InferWorkplace(job.Title, job.Location, job.Description, job.RemoteHint);
        job.Summary = Summarize(job.Description);
        job.Relevant = Relevance(job);

        if (job.Description is { Length: > 20000 })
            job.Description = job.Description[..20000];
    }

    public static string ToPlain(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var text = BreakTag().Replace(html, "\n");
        text = ListTag().Replace(text, "\n• ");
        text = CloseBlock().Replace(text, "\n");
        text = AnyTag().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        text = TrailingSpace().Replace(text, "\n");
        text = MultiBlank().Replace(text, "\n\n");
        text = MultiSpace().Replace(text, " ");
        return text.Trim();
    }

    public static string Summarize(string? text, int max = 360)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var flat = MultiSpace().Replace(text.Replace('\n', ' '), " ").Trim();
        if (flat.Length <= max)
            return flat;

        var cut = flat.LastIndexOf('.', Math.Min(max, flat.Length - 1));
        if (cut < 140)
            cut = max;
        return flat[..cut].Trim() + "…";
    }

    public static string? FindSalary(string? structured, string? description)
    {
        var direct = Clean(structured);
        if (direct.Length > 0)
            return direct;

        if (string.IsNullOrWhiteSpace(description))
            return null;

        var range = BrlRange().Match(description);
        if (range.Success)
            return Clean(range.Value);

        var labeled = LabeledRange().Match(description);
        if (labeled.Success)
            return "R$ " + labeled.Groups[1].Value + " – R$ " + labeled.Groups[2].Value;

        var single = BrlSingle().Match(description);
        if (single.Success)
            return Clean(single.Value);

        var usd = UsdRange().Match(description);
        if (usd.Success)
            return Clean(usd.Value);

        if (Combinar().IsMatch(description))
            return "A combinar";

        return null;
    }

    public static List<string> ExtractStack(string? text)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return found;

        foreach (var (label, pattern) in StackPatterns)
        {
            if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                found.Add(label);
            if (found.Count == 12)
                break;
        }

        return found;
    }

    public static bool IsDotNet(string? title, IEnumerable<string>? stack, string? description)
    {
        var blob = $"{title}\n{string.Join(' ', stack ?? [])}\n{description}";
        return DotNetSignal().IsMatch(blob);
    }

    public static int? Relevance(JobDraft job)
    {
        if (IsDotNet(job.Title, job.Stack, job.Description))
            return 1;
        return job.DetailComplete ? 0 : null;
    }

    public static string? InferSeniority(string? title, string? criteria)
    {
        var fromTitle = MatchSeniority(title);
        if (fromTitle != null)
            return fromTitle;

        var raw = Clean(criteria);
        if (raw.Length == 0)
            return null;

        return MatchSeniority(raw) ?? raw;
    }

    public static string? InferEmployment(string? criteria, string? description)
    {
        string? contract = null;
        if (!string.IsNullOrWhiteSpace(description))
        {
            var clt = Clt().IsMatch(description);
            var pj = Pj().IsMatch(description);
            contract = clt && pj ? "CLT ou PJ" : clt ? "CLT" : pj ? "PJ" : null;
        }

        var raw = Clean(criteria);
        if (raw.Length == 0)
            return contract;

        var folded = Region.Fold(raw);
        foreach (var (pattern, label) in EmploymentLabels)
        {
            if (folded.Contains(Region.Fold(pattern), StringComparison.Ordinal))
                return JoinParts(label, contract);
        }

        return JoinParts(raw, contract);
    }

    public static string InferWorkplace(string? title, string? location, string? description, bool remoteHint)
    {
        var blob = $"{title}\n{location}\n{description}";
        var hybrid = Hybrid().IsMatch(blob);
        var onsite = Onsite().IsMatch(blob);
        var negated = RemoteNegated().IsMatch(blob);
        var remote = RemoteWord().IsMatch(blob);

        if (remote && negated)
            return hybrid ? "hybrid" : "onsite";
        if (hybrid)
            return "hybrid";
        if (remote)
            return "remote";
        if (onsite || negated)
            return "onsite";
        if (remoteHint)
            return "remote";
        return "unknown";
    }

    public static string FormatMoney(string? currency, decimal amount)
    {
        if (string.Equals(currency, "BRL", StringComparison.OrdinalIgnoreCase))
            return amount.ToString("C0", CultureInfo.GetCultureInfo("pt-BR"));
        if (string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase))
            return amount.ToString("C0", CultureInfo.GetCultureInfo("en-US"));
        return amount.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"));
    }

    public static string? UnitLabel(string? unit) => unit?.ToUpperInvariant() switch
    {
        "HOUR" => "hora",
        "DAY" => "dia",
        "WEEK" => "semana",
        "MONTH" => "mês",
        "YEAR" => "ano",
        _ => null
    };

    private static string? MatchSeniority(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (PlenoSenior().IsMatch(text))
            return "Pleno/Sênior";
        if (Intern().IsMatch(text))
            return "Estágio";
        if (Junior().IsMatch(text))
            return "Júnior";
        if (Pleno().IsMatch(text))
            return "Pleno";
        if (Senior().IsMatch(text))
            return "Sênior";
        if (Specialist().IsMatch(text))
            return "Especialista";
        if (Architect().IsMatch(text))
            return "Arquitetura";
        if (Lead().IsMatch(text))
            return "Lead";
        if (Coord().IsMatch(text))
            return "Coordenação";
        if (Manager().IsMatch(text))
            return "Gestão";
        return null;
    }

    private static string? JoinParts(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return string.IsNullOrWhiteSpace(right) ? null : right;
        if (string.IsNullOrWhiteSpace(right) || left.Contains(right, StringComparison.OrdinalIgnoreCase))
            return left;
        return $"{left} · {right}";
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return MultiSpace().Replace(value, " ").Trim();
    }

    private static readonly (string Pattern, string Label)[] EmploymentLabels =
    [
        ("full-time", "Tempo integral"),
        ("full time", "Tempo integral"),
        ("tempo integral", "Tempo integral"),
        ("part-time", "Meio período"),
        ("meio período", "Meio período"),
        ("meio periodo", "Meio período"),
        ("contract", "Contrato"),
        ("contrato", "Contrato"),
        ("temporary", "Temporário"),
        ("temporário", "Temporário"),
        ("temporario", "Temporário"),
        ("internship", "Estágio"),
        ("estágio", "Estágio"),
        ("estagio", "Estágio"),
        ("freelance", "Freelancer"),
        ("volunteer", "Voluntário")
    ];

    private static readonly (string Label, string Pattern)[] StackPatterns =
    [
        (".NET", @"(?<![A-Za-z0-9])\.NET\b|\bdotnet\b|\bdot\s*net\b|\bASP\.NET\b"),
        ("C#", @"\bC\s*#|\bcsharp\b|\bC\s*sharp\b"),
        ("ASP.NET", @"\bASP\.NET\b"),
        ("Blazor", @"\bBlazor\b"),
        ("Entity Framework", @"\bEntity Framework\b|\bEF\s*Core\b"),
        ("Dapper", @"\bDapper\b"),
        ("SQL Server", @"\bSQL Server\b|\bMSSQL\b"),
        ("Azure", @"\bAzure\b"),
        ("AWS", @"\bAWS\b|\bAmazon Web Services\b"),
        ("Docker", @"\bDocker\b"),
        ("Kubernetes", @"\bKubernetes\b|\bK8s\b"),
        ("React", @"\bReact(?:\.js)?\b"),
        ("Angular", @"\bAngular(?:JS)?\b"),
        ("Vue", @"\bVue(?:\.js)?\b"),
        ("TypeScript", @"\bTypeScript\b"),
        ("JavaScript", @"\bJavaScript\b|\bJS\b"),
        ("Node.js", @"\bNode(?:\.js)?\b"),
        ("Next.js", @"\bNext(?:\.js)?\b"),
        ("Python", @"\bPython\b"),
        ("Java", @"\bJava\b(?!Script)"),
        ("Go", @"\bGolang\b|\bGo\s+lang\b"),
        ("Kotlin", @"\bKotlin\b"),
        ("PHP", @"\bPHP\b"),
        ("Ruby", @"\bRuby\b"),
        ("Rust", @"\bRust\b"),
        ("PostgreSQL", @"\bPostgre(?:SQL)?\b|\bPostgres\b"),
        ("MySQL", @"\bMySQL\b"),
        ("MongoDB", @"\bMongo(?:DB)?\b"),
        ("Oracle", @"\bOracle\b"),
        ("Redis", @"\bRedis\b"),
        ("RabbitMQ", @"\bRabbitMQ\b"),
        ("Kafka", @"\bKafka\b"),
        ("gRPC", @"\bgRPC\b"),
        ("GraphQL", @"\bGraphQL\b"),
        ("REST", @"\bREST\b|\bRESTful\b"),
        ("Microservices", @"\bMicroservi[cç]os?\b|\bMicroservices\b"),
        ("WPF", @"\bWPF\b"),
        ("WinForms", @"\bWinForms\b|\bWindows Forms\b"),
        ("MAUI", @"\b\.NET MAUI\b|\bMAUI\b"),
        ("Xamarin", @"\bXamarin\b"),
        ("SignalR", @"\bSignalR\b"),
        ("LINQ", @"\bLINQ\b"),
        ("Azure DevOps", @"\bAzure DevOps\b"),
        ("Terraform", @"\bTerraform\b"),
        ("CI/CD", @"\bCI\s*/\s*CD\b"),
        ("DDD", @"\bDDD\b|\bDomain-Driven\b"),
        ("Clean Architecture", @"\bClean Architecture\b"),
        ("Power BI", @"\bPower\s*BI\b"),
        ("SAP", @"\bSAP\b"),
        ("Salesforce", @"\bSalesforce\b"),
        ("Delphi", @"\bDelphi\b"),
        ("Flutter", @"\bFlutter\b"),
        ("Spring", @"\bSpring\b")
    ];

    [GeneratedRegex(@"<\s*br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex(@"<\s*li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListTag();

    [GeneratedRegex(@"</\s*(p|div|li|h\d|tr|section|ul|ol)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex CloseBlock();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiBlank();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"R\$\s*\d{1,3}(?:\.\d{3})+(?:,\d{2})?(?:\s*/\s*(?:m[eê]s|ano|hora))?(?:\s*(?:a|até|e|-|–|—)\s*R\$\s*\d{1,3}(?:\.\d{3})+(?:,\d{2})?(?:\s*/\s*(?:m[eê]s|ano|hora))?)?", RegexOptions.IgnoreCase)]
    private static partial Regex BrlRange();

    [GeneratedRegex(@"R\$\s*\d{1,3}(?:\.\d{3})+(?:,\d{2})?(?:\s*/\s*(?:m[eê]s|ano|hora))?", RegexOptions.IgnoreCase)]
    private static partial Regex BrlSingle();

    [GeneratedRegex(@"(?:sal[aá]rio|remunera[cç][aã]o|faixa(?:\s+salarial)?|pretens[aã]o)\D{0,30}(\d{1,3}(?:\.\d{3})+|\d{4,6})\s*(?:a|até|-|–)\s*(\d{1,3}(?:\.\d{3})+|\d{4,6})", RegexOptions.IgnoreCase)]
    private static partial Regex LabeledRange();

    [GeneratedRegex(@"US\$\s*\d{1,3}(?:,\d{3})+(?:\.\d{2})?(?:\s*(?:-|–|a)\s*US\$\s*\d{1,3}(?:,\d{3})+(?:\.\d{2})?)?", RegexOptions.IgnoreCase)]
    private static partial Regex UsdRange();

    [GeneratedRegex(@"\ba combinar\b", RegexOptions.IgnoreCase)]
    private static partial Regex Combinar();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:\.NET|dotnet|dot\s*net|ASP\.NET|C\s*#|csharp|CSharp|Blazor)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DotNetSignal();

    [GeneratedRegex(@"pleno\s*/\s*s[eê]nior|s[eê]nior\s*/\s*pleno", RegexOptions.IgnoreCase)]
    private static partial Regex PlenoSenior();

    [GeneratedRegex(@"est[aá]gio|estagi[aá]ri[oa]|internship|\bintern\b", RegexOptions.IgnoreCase)]
    private static partial Regex Intern();

    [GeneratedRegex(@"j[uú]nior|\bjunior\b|\bjr\.?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Junior();

    [GeneratedRegex(@"\bpleno\b|\bmid[\s-]level\b", RegexOptions.IgnoreCase)]
    private static partial Regex Pleno();

    [GeneratedRegex(@"s[eê]nior|\bsenior\b|\bsr\.?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Senior();

    [GeneratedRegex(@"especialista|\bspecialist\b|\bstaff\b|\bprincipal\b", RegexOptions.IgnoreCase)]
    private static partial Regex Specialist();

    [GeneratedRegex(@"arquitet[oa]|\barchitect\b", RegexOptions.IgnoreCase)]
    private static partial Regex Architect();

    [GeneratedRegex(@"tech\s*lead|l[ií]der\s+t[eé]cnico|\blead\b", RegexOptions.IgnoreCase)]
    private static partial Regex Lead();

    [GeneratedRegex(@"coordenador[ae]?", RegexOptions.IgnoreCase)]
    private static partial Regex Coord();

    [GeneratedRegex(@"\bgerente\b|\bmanager\b|\bhead\b|\bdiretor[ae]?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Manager();

    [GeneratedRegex(@"\bCLT\b")]
    private static partial Regex Clt();

    [GeneratedRegex(@"\bPJ\b")]
    private static partial Regex Pj();

    [GeneratedRegex(@"h[ií]brido|\bhybrid\b", RegexOptions.IgnoreCase)]
    private static partial Regex Hybrid();

    [GeneratedRegex(@"\bpresencial\b|\bon-?site\b|no escrit[oó]rio", RegexOptions.IgnoreCase)]
    private static partial Regex Onsite();

    [GeneratedRegex(@"n[aã]o\s+(?:[eé]\s+)?(?:100%\s+)?remoto|sem\s+home[\s-]*office|n[aã]o\s+aceita\s+(?:home[\s-]*office|remoto)", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteNegated();

    [GeneratedRegex(@"100%\s*remoto|totalmente\s+remoto|trabalho\s+remoto|vaga\s+remota|home[\s-]*office|teletrabalho|\bremoto\b|\bremote\b", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteWord();
}
