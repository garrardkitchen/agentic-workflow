using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgenticWorkflow.Shared.Services;

using AgenticWorkflow.Shared.Models;

public sealed class JsonFilePromptConfigStore : IPromptConfigStore
{
    private readonly string _filePath;
    private readonly ILogger<JsonFilePromptConfigStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public JsonFilePromptConfigStore(string directory, ILogger<JsonFilePromptConfigStore> logger)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "prompt-config.json");
        _logger = logger;
    }

    public async Task<PromptConfig> GetAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            return new PromptConfig();

        await _lock.WaitAsync(ct);
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<PromptConfig>(stream, JsonOptions, ct) ?? new PromptConfig();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read prompt config from {Path}, returning defaults", _filePath);
            return new PromptConfig();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(PromptConfig config, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var stream = File.Create(_filePath);
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions, ct);
            _logger.LogInformation("Prompt config saved to {Path}", _filePath);
        }
        finally
        {
            _lock.Release();
        }
    }
}
