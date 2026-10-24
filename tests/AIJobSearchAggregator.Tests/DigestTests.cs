using AIJobSearchAggregator;

namespace AIJobSearchAggregator.Tests;

public class DigestTests
{
    [Fact]
    public void Salary_reads_brazilian_range()
    {
        var salary = TextDigest.FindSalary(null, "Faixa salarial de R$ 8.000 a R$ 12.000/mês, CLT.");
        Assert.Contains("8.000", salary);
        Assert.Contains("12.000", salary);
    }

    [Fact]
    public void Salary_reads_labeled_numbers_without_symbol()
    {
        var salary = TextDigest.FindSalary(null, "Salário: 8000 a 12000 mensais");
        Assert.Equal("R$ 8000 – R$ 12000", salary);
    }

    [Fact]
    public void Stack_keeps_csharp_and_does_not_call_javascript_java()
    {
        var stack = TextDigest.ExtractStack("Experiência com C# e ASP.NET Core, JavaScript e Azure.");
        Assert.Contains(".NET", stack);
        Assert.Contains("C#", stack);
        Assert.Contains("ASP.NET", stack);
        Assert.Contains("JavaScript", stack);
        Assert.Contains("Azure", stack);
        Assert.DoesNotContain("Java", stack);
    }

    [Fact]
    public void Seniority_prefers_pleno_senior_combo()
    {
        Assert.Equal("Pleno/Sênior", TextDigest.InferSeniority("Desenvolvedor .NET Pleno/Sênior", null));
        Assert.Equal("Pleno", TextDigest.InferSeniority("Desenvolvedor .NET Pleno", "Mid-Senior level"));
    }

    [Fact]
    public void Workplace_treats_negated_remote_as_onsite()
    {
        Assert.Equal("onsite", TextDigest.InferWorkplace("Dev .NET", "São Paulo, SP", "A vaga não é remoto.", false));
        Assert.Equal("remote", TextDigest.InferWorkplace("Dev .NET remoto", "Brasil", null, false));
        Assert.Equal("hybrid", TextDigest.InferWorkplace("Dev", "Sorocaba, SP", "Modelo híbrido", false));
    }

    [Fact]
    public void Employment_joins_contract_model()
    {
        Assert.Equal("Tempo integral · CLT ou PJ", TextDigest.InferEmployment("Full-time", "Aceitamos CLT ou PJ."));
    }

    [Theory]
    [InlineData("Itu, São Paulo, Brasil", true)]
    [InlineData("Itaquaquecetuba, São Paulo, Brasil", false)]
    [InlineData("Sorocaba, SP", true)]
    [InlineData("Campinas, São Paulo, Brasil", false)]
    public void Sorocaba_region_accepts_neighbors_only(string location, bool expected)
    {
        Assert.Equal(expected, RegionCatalog.Sorocaba.AcceptsLocation(location));
    }

    [Fact]
    public void Grande_sp_accepts_metro_and_rejects_campinas()
    {
        Assert.True(RegionCatalog.GrandeSp.AcceptsLocation("Itaquaquecetuba, São Paulo, Brasil"));
        Assert.True(RegionCatalog.GrandeSp.AcceptsLocation("São Bernardo do Campo, SP"));
        Assert.False(RegionCatalog.GrandeSp.AcceptsLocation("Campinas, São Paulo, Brasil"));
        Assert.True(RegionCatalog.Remoto.AcceptsLocation("Campinas, São Paulo, Brasil"));
    }

    [Fact]
    public void Card_parser_reads_public_listing()
    {
        const string html = """
            <li>
              <div class="base-search-card" data-entity-urn="urn:li:jobPosting:4480388919">
                <a class="base-card__full-link" href="https://br.linkedin.com/jobs/view/dev-4480388919?trk=1"></a>
                <img data-delayed-url="https://media.licdn.com/logo.png" />
                <h3 class="base-search-card__title">Desenvolvedor Backend .NET Sênior</h3>
                <h4 class="base-search-card__subtitle"><a href="https://br.linkedin.com/company/jobbol?trk=1">Jobbol</a></h4>
                <span class="job-search-card__location">São Paulo, SP</span>
                <span class="job-posting-benefits__text">Contratando agora</span>
                <time datetime="2026-10-17">Há 6 dias</time>
              </div>
            </li>
            """;

        var job = Assert.Single(LinkedInHtml.ParseCards(html));
        Assert.Equal("4480388919", job.ExternalId);
        Assert.Equal("Desenvolvedor Backend .NET Sênior", job.Title);
        Assert.Equal("Jobbol", job.Company);
        Assert.Equal("São Paulo, SP", job.Location);
        Assert.Equal("2026-10-17", job.PostedAt);
        Assert.Equal("Há 6 dias", job.ListedAgo);
        Assert.Equal("Contratando agora", job.Insight);
        Assert.Equal("https://www.linkedin.com/jobs/view/4480388919", job.SourceUrl);
        Assert.Equal("https://br.linkedin.com/company/jobbol", job.CompanyUrl);
    }

    [Fact]
    public void Detail_parser_reads_criteria_salary_and_external_apply()
    {
        const string html = """
            <div class="show-more-less-html__markup"><p>Atuar com C# e SQL Server. Faixa de R$ 10.000 a R$ 14.000.</p></div>
            <div class="compensation__salary">R$ 10.000,00/mês - R$ 14.000,00/mês</div>
            <ul>
              <li class="description__job-criteria-item">
                <h3 class="description__job-criteria-subheader">Nível de senioridade</h3>
                <span class="description__job-criteria-text">Não aplicável</span>
              </li>
              <li class="description__job-criteria-item">
                <h3 class="description__job-criteria-subheader">Tipo de emprego</h3>
                <span class="description__job-criteria-text">Tempo integral</span>
              </li>
              <li class="description__job-criteria-item">
                <h3 class="description__job-criteria-subheader">Função</h3>
                <span class="description__job-criteria-text">Engenharia e tecnologia da informação</span>
              </li>
            </ul>
            <code id="applyUrl"><!--"https://empresa.example/vagas/123"--></code>
            """;

        var job = new JobDraft { Title = "Dev .NET", ExternalId = "1", SourceUrl = "https://www.linkedin.com/jobs/view/1", ApplyUrl = "https://www.linkedin.com/jobs/view/1" };
        LinkedInHtml.FillDetail(job, html);
        TextDigest.Apply(job);

        Assert.Equal("R$ 10.000,00/mês - R$ 14.000,00/mês", job.Salary);
        Assert.Equal("https://empresa.example/vagas/123", job.ApplyUrl);
        Assert.Equal("Engenharia e tecnologia da informação", job.JobFunction);
        Assert.Contains("SQL Server", job.Stack);
        Assert.Contains("C#", job.Description);
        Assert.True(job.DetailComplete);
    }

    [Fact]
    public void Hidden_job_stays_hidden_when_the_search_sees_it_again()
    {
        var database = new Database(Path.Combine(Path.GetTempPath(), "vagas-tests", Guid.NewGuid().ToString("n"), "jobs.db"));
        database.Initialize();
        var repository = new JobRepository(database);
        var draft = new JobDraft
        {
            Source = "linkedin",
            ExternalId = "99",
            Title = "Dev .NET",
            Company = "Acme",
            Location = "Sorocaba, SP",
            RegionKey = "sorocaba",
            Workplace = "hybrid",
            SourceUrl = "https://www.linkedin.com/jobs/view/99",
            ApplyUrl = "https://www.linkedin.com/jobs/view/99",
            Relevant = 1
        };

        var first = repository.UpsertFromSearch(draft);
        Assert.True(first.IsNew);
        repository.Update(first.Id, "hidden", "não quero");

        draft.Title = "Dev .NET atualizado";
        var second = repository.UpsertFromSearch(draft);
        Assert.False(second.IsNew);

        var saved = repository.Get(first.Id);
        Assert.NotNull(saved);
        Assert.Equal("hidden", saved.Status);
        Assert.Equal("não quero", saved.Note);
        Assert.Equal("Dev .NET atualizado", saved.Title);
        Assert.Empty(repository.List(new JobQuery(null, "all", null, null, false, true, false)));
        Assert.Single(repository.List(new JobQuery(null, "hidden", null, null, true, true, false)));
    }
}
