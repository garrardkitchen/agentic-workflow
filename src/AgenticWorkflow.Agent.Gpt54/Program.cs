using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgenticWorkflow.Shared.Models;
using GitHub.Copilot.SDK;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var agentName = builder.Configuration["Agent:Name"] ?? "Agent-Gpt54";
var modelName = builder.Configuration["Agent:Model"] ?? "gpt-5.4";
var defaultSystemPrompt = builder.Configuration["Agent:SystemPrompt"] ?? "You are a helpful assistant.";

// Register singleton CopilotClient — AutoStart is true by default
builder.Services.AddSingleton<CopilotClient>(_ => new CopilotClient());

var app = builder.Build();
app.MapDefaultEndpoints();

// ── Non-streaming endpoint (used by Gateway fan-out) ─────────────────

app.MapPost("/api/run", async (
    AgentRequest request,
    CopilotClient copilotClient,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();
    var effectiveSystemPrompt = request.SystemPromptOverride ?? defaultSystemPrompt;

    try
    {
        await using var session = await copilotClient.CreateSessionAsync(new SessionConfig
        {
            Model = modelName,
            Streaming = false,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = effectiveSystemPrompt
            }
        });

        var responseText = new StringBuilder();
        var done = new TaskCompletionSource();
        string? errorMessage = null;

        using var subscription = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    responseText.Append(msg.Data.Content);
                    break;
                case SessionIdleEvent:
                    done.TrySetResult();
                    break;
                case SessionErrorEvent err:
                    errorMessage = err.Data?.Message ?? "Unknown error";
                    done.TrySetResult();
                    break;
            }
        });

        await session.SendAsync(new MessageOptions { Prompt = request.Prompt });

        // Link request cancellation + 5-min hard timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        await using (cts.Token.Register(() => done.TrySetCanceled()))
        {
            try
            {
                await done.Task;
            }
            catch (TaskCanceledException)
            {
                sw.Stop();
                var reason = ct.IsCancellationRequested ? "Request cancelled by caller" : "Timed out after 5 minutes";
                logger.LogWarning("{AgentName} cancelled: {Reason} ({ElapsedMs}ms)", agentName, reason, sw.ElapsedMilliseconds);
                return Results.Ok(new AgentResult
                {
                    AgentName = agentName,
                    Model = modelName,
                    ResponseText = responseText.ToString(),
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Failed = true,
                    Error = reason
                });
            }
        }

        sw.Stop();

        if (errorMessage is not null)
        {
            logger.LogWarning("{AgentName} returned error: {Error}", agentName, errorMessage);
            return Results.Ok(new AgentResult
            {
                AgentName = agentName,
                Model = modelName,
                ResponseText = "",
                ElapsedMs = sw.ElapsedMilliseconds,
                Failed = true,
                Error = errorMessage
            });
        }

        logger.LogInformation("{AgentName} completed in {ElapsedMs}ms ({Length} chars)",
            agentName, sw.ElapsedMilliseconds, responseText.Length);

        return Results.Ok(new AgentResult
        {
            AgentName = agentName,
            Model = modelName,
            ResponseText = responseText.ToString(),
            ElapsedMs = sw.ElapsedMilliseconds
        });
    }
    catch (Exception ex)
    {
        sw.Stop();
        logger.LogError(ex, "{AgentName} failed", agentName);
        return Results.Ok(new AgentResult
        {
            AgentName = agentName,
            Model = modelName,
            ResponseText = "",
            ElapsedMs = sw.ElapsedMilliseconds,
            Failed = true,
            Error = ex.Message
        });
    }
});

// ── Streaming endpoint (SSE) ─────────────────────────────────────────

app.MapGet("/api/run-stream", async (
    string prompt,
    string? systemPrompt,
    CopilotClient copilotClient,
    ILogger<Program> logger,
    HttpContext httpContext) =>
{
    httpContext.Response.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";

    var ct = httpContext.RequestAborted;
    var sw = Stopwatch.StartNew();
    var effectiveSystemPrompt = systemPrompt ?? defaultSystemPrompt;
    using var sseLock = new SemaphoreSlim(1, 1);

    try
    {
        await using var session = await copilotClient.CreateSessionAsync(new SessionConfig
        {
            Model = modelName,
            Streaming = true,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = effectiveSystemPrompt
            }
        });

        var done = new TaskCompletionSource();

        using var subscription = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    var data = JsonSerializer.Serialize(new
                    {
                        text = delta.Data.DeltaContent,
                        agent = agentName,
                        model = modelName
                    });
                    _ = WriteSSE(httpContext, data, sseLock);
                    break;
                case SessionIdleEvent:
                    done.TrySetResult();
                    break;
                case SessionErrorEvent:
                    done.TrySetResult();
                    break;
            }
        });

        await session.SendAsync(new MessageOptions { Prompt = prompt });

        // Link client disconnect + 5-min hard timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        await using (cts.Token.Register(() => done.TrySetCanceled()))
        {
            try { await done.Task; }
            catch (TaskCanceledException) { /* timeout or client disconnect — fall through */ }
        }

        sw.Stop();
        var doneData = JsonSerializer.Serialize(new { done = true, agent = agentName, elapsedMs = sw.ElapsedMilliseconds });
        await WriteSSE(httpContext, doneData, sseLock);
    }
    catch (Exception ex)
    {
        sw.Stop();
        logger.LogError(ex, "{AgentName} streaming failed", agentName);
        var errorData = JsonSerializer.Serialize(new { error = ex.Message, agent = agentName });
        try { await WriteSSE(httpContext, errorData, sseLock); } catch { /* client disconnected */ }
    }
});

app.Run();

static async Task WriteSSE(HttpContext ctx, string data, SemaphoreSlim sseLock)
{
    await sseLock.WaitAsync();
    try
    {
        await ctx.Response.WriteAsync($"data: {data}\n\n");
        await ctx.Response.Body.FlushAsync();
    }
    catch { /* client disconnected — swallow */ }
    finally
    {
        sseLock.Release();
    }
}
