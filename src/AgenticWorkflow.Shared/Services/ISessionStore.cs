namespace AgenticWorkflow.Shared.Services;

using AgenticWorkflow.Shared.Models;

public interface ISessionStore
{
    Task<SessionState> CreateAsync(string prompt, string? systemPrompt, CancellationToken ct = default);
    Task<SessionState?> GetAsync(string sessionId, CancellationToken ct = default);
    Task<SessionState> UpdateAsync(SessionState session, CancellationToken ct = default);
    Task<IReadOnlyList<SessionState>> ListAsync(CancellationToken ct = default);
}
