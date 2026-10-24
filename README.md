# Vagas

Página do projeto: https://lcarlini.github.io/AIJobSearchAggregator/

Busca local de vagas de .NET e C#. Ao abrir, consulta a listagem pública do LinkedIn e mostra o resultado numa tela, com local, salário, stack e link para se candidatar.

As vagas ficam num SQLite em `data/jobs.db`. Ocultar uma vaga impede que ela volte como nova. Dá para marcar salva, candidatura, entrevista, oferta ou recusa, e escrever uma nota.

## Como abrir

```powershell
./run.ps1
```

Ou:

```powershell
dotnet run --urls http://localhost:5088
```

O navegador abre em http://localhost:5088. A janela do console mostra o andamento da busca.

A busca padrão cobre Grande São Paulo, a região de Sorocaba e vagas remotas no Brasil, na última semana. Os termos e as regiões podem ser editados na lateral.

## Outra fonte

Implemente `IJobSource` e registre a classe em `Program.cs`, do mesmo jeito que `LinkedInGuestSource`.
