namespace AIJobSearchAggregator;

/// <summary>
/// Uma fonte de vagas. LinkedIn é a primeira. Para somar outra, implemente esta interface e registre em Program.cs.
/// </summary>
public interface IJobSource
{
    string Key { get; }
    string DisplayName { get; }
    Task<IReadOnlyList<JobDraft>> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
    Task<bool> TryFillDetailAsync(JobDraft job, CancellationToken cancellationToken);
}
