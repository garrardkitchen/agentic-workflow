namespace AgenticWorkflow.Shared.Services;

using AgenticWorkflow.Shared.Models;

public interface IPromptConfigStore
{
    Task<PromptConfig> GetAsync(CancellationToken ct = default);
    Task SaveAsync(PromptConfig config, CancellationToken ct = default);
}
