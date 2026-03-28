using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgenticWorkflow.Shared.Services;

using AgenticWorkflow.Shared.Models;

public sealed class JsonFileSessionStore : ISessionStore
{
    private readonly string _directory;
    private readonly ILogger<JsonFileSessionStore> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JsonFileSessionStore(string directory, ILogger<JsonFileSessionStore> logger)
    {
        _directory = directory;
        _logger = logger;
        Directory.CreateDirectory(_directory);
    }

    public async Task<SessionState> CreateAsync(string prompt, string? systemPrompt, CancellationToken ct = default)
    {
        var session = new SessionState
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Prompt = prompt,
            SystemPrompt = systemPrompt
        };

        var semaphore = GetLock(session.Id);
        await semaphore.WaitAsync(ct);
        try
        {
            await SaveAsync(session, ct);
            _logger.LogInformation("Session {SessionId} created and saved to {Path}", session.Id, GetPath(session.Id));
            return session;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<SessionState?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        var path = GetPath(sessionId);
        if (!File.Exists(path)) return null;

        var semaphore = GetLock(sessionId);
        await semaphore.WaitAsync(ct);
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SessionState>(stream, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read session {SessionId} from {Path}", sessionId, path);
            return null;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<SessionState> UpdateAsync(SessionState session, CancellationToken ct = default)
    {
        var semaphore = GetLock(session.Id);
        await semaphore.WaitAsync(ct);
        try
        {
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync(session, ct);
            _logger.LogInformation("Session {SessionId} updated (status: {Status}) and saved to {Path}",
                session.Id, session.Status, GetPath(session.Id));
            return session;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<SessionState>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_directory))
            return [];

        var files = Directory.GetFiles(_directory, "*.json")
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name is not null
                    && !name.StartsWith("system-prompt", StringComparison.Ordinal)
                    && !name.StartsWith("agent-prompts", StringComparison.Ordinal)
                    && !name.StartsWith("prompt-config", StringComparison.Ordinal);
            })
            .OrderByDescending(File.GetLastWriteTimeUtc);

        var sessions = new List<SessionState>();
        foreach (var file in files)
        {
            try
            {
                await using var stream = File.OpenRead(file);
                var session = await JsonSerializer.DeserializeAsync<SessionState>(stream, JsonOptions, ct);
                if (session is not null) sessions.Add(session);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read session file {Path}", file);
            }
        }

        return sessions;
    }

    private async Task SaveAsync(SessionState session, CancellationToken ct)
    {
        var path = GetPath(session.Id);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, session, JsonOptions, ct);
    }

    private SemaphoreSlim GetLock(string sessionId) => _locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

    private static string SanitizeId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

        var sanitized = new string(sessionId.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        if (sanitized.Length == 0 || sanitized != sessionId)
            throw new ArgumentException($"Invalid session ID: '{sessionId}'", nameof(sessionId));

        return sanitized;
    }

    private string GetPath(string sessionId) => Path.Combine(_directory, $"{SanitizeId(sessionId)}.json");
}
