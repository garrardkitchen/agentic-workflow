using System.Text;
using System.Text.Json;
using AgenticWorkflow.Shared.Models;
using AgenticWorkflow.Shared.Services;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Http.Resilience;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults but override HTTP resilience for LLM-bound calls
builder.AddServiceDefaults();

// Configure HTTP resilience for LLM-bound calls — 5-min timeouts, no retries
builder.Services.ConfigureHttpClientDefaults(http =>
{
    http.AddServiceDiscovery();
    http.AddStandardResilienceHandler(options =>
    {
        options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
        options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(5);
        options.Retry.MaxRetryAttempts = 1;
        options.Retry.ShouldHandle = _ => ValueTask.FromResult(false); // Effectively disable retries
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(10);
    });
});

// Session store
var sessionsDir = Path.Combine(builder.Environment.ContentRootPath, "..", "..", "sessions");
builder.Services.AddSingleton<ISessionStore>(sp =>
    new JsonFileSessionStore(sessionsDir, sp.GetRequiredService<ILogger<JsonFileSessionStore>>()));
builder.Services.AddSingleton<IPromptConfigStore>(sp =>
    new JsonFilePromptConfigStore(sessionsDir, sp.GetRequiredService<ILogger<JsonFilePromptConfigStore>>()));

// CopilotClient singleton for the evaluator agent
builder.Services.AddSingleton<CopilotClient>(_ => new CopilotClient());

// HTTP clients for agent APIs
foreach (var name in new[] { "agent-sonnet", "agent-codex", "agent-gpt54" })
{
    builder.Services.AddHttpClient(name, c =>
    {
        c.BaseAddress = new Uri($"https+http://{name}");
        c.Timeout = TimeSpan.FromMinutes(5);
    });
}

// CORS for frontend
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();
app.MapDefaultEndpoints();
app.UseCors();

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

// ── Prompt Config Endpoints ───────────────────────────────────────────

app.MapGet("/api/config/prompt", async (IPromptConfigStore store, CancellationToken ct) =>
    Results.Ok(await store.GetAsync(ct)));

app.MapPut("/api/config/prompt", async (PromptConfig config, IPromptConfigStore store, CancellationToken ct) =>
{
    await store.SaveAsync(config, ct);
    return Results.Ok(config);
});

// ── Session Endpoints ─────────────────────────────────────────────────

app.MapGet("/api/sessions", async (ISessionStore store, CancellationToken ct) =>
    Results.Ok(await store.ListAsync(ct)));

app.MapGet("/api/sessions/{id}", async (string id, ISessionStore store, CancellationToken ct) =>
{
    var session = await store.GetAsync(id, ct);
    return session is null ? Results.NotFound() : Results.Ok(session);
});

// ── Main Orchestration Endpoint (SSE Streaming) ──────────────────────

app.MapPost("/api/orchestrate", async (
    AgentRequest request,
    ISessionStore sessionStore,
    IPromptConfigStore promptStore,
    IHttpClientFactory httpClientFactory,
    CopilotClient copilotClient,
    ILogger<Program> logger,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    httpContext.Response.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";

    using var sseLock = new SemaphoreSlim(1, 1);
    SessionState? session = null;

    try
    {
    // Load prompt config
    var promptConfig = await promptStore.GetAsync(ct);
    var systemPrompt = request.SystemPromptOverride ?? promptConfig.DrivingSystemPrompt;

    // Create session
    session = await sessionStore.CreateAsync(request.Prompt, systemPrompt, ct);
    session.ChatHistory.Add(new ChatMessage { Role = "user", Content = request.Prompt });

    await SendEvent(httpContext, new StreamEvent
    {
        Type = "status",
        SessionId = session.Id,
        Content = "Session created",
        Status = SessionStatus.Created
    }, jsonOptions, sseLock);

    // ── Fan-out to 3 agents ──────────────────────────────────────────
    session.Status = SessionStatus.AgentsRunning;
    await sessionStore.UpdateAsync(session, ct);

    await SendEvent(httpContext, new StreamEvent
    {
        Type = "status",
        SessionId = session.Id,
        Content = "Agents running",
        Status = SessionStatus.AgentsRunning
    }, jsonOptions, sseLock);

    var agentNames = new[] { "agent-sonnet", "agent-codex", "agent-gpt54" };
    var agentTasks = agentNames.Select(async name =>
    {
        try
        {
            var client = httpClientFactory.CreateClient(name);
            var agentRequest = new AgentRequest
            {
                Prompt = request.Prompt,
                SessionId = session.Id,
                SystemPromptOverride = GetAgentPrompt(promptConfig, name, systemPrompt)
            };

            var response = await client.PostAsJsonAsync("/api/run", agentRequest, ct);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<AgentResult>(ct);

            if (result is not null)
            {
                await SendEvent(httpContext, new StreamEvent
                {
                    Type = "agent_complete",
                    SessionId = session.Id,
                    AgentName = result.AgentName,
                    Content = result.ResponseText,
                    AgentResult = result
                }, jsonOptions, sseLock);
            }

            return result ?? new AgentResult
            {
                AgentName = name,
                Model = "unknown",
                ResponseText = "",
                Failed = true,
                Error = "Null response"
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent {AgentName} failed", name);
            await SendEvent(httpContext, new StreamEvent
            {
                Type = "error",
                SessionId = session.Id,
                AgentName = name,
                Content = $"Agent {name} failed: {ex.Message}"
            }, jsonOptions, sseLock);

            return new AgentResult
            {
                AgentName = name,
                Model = "unknown",
                ResponseText = "",
                Failed = true,
                Error = ex.Message
            };
        }
    });

    var results = await Task.WhenAll(agentTasks);
    session.AgentResults = [.. results];

    // Check if all agents failed
    if (results.All(r => r.Failed))
    {
        session.Status = SessionStatus.Failed;
        session.ChatHistory.Add(new ChatMessage
        {
            Role = "system",
            Content = "All agents failed to produce a response."
        });
        await sessionStore.UpdateAsync(session, ct);

        await SendEvent(httpContext, new StreamEvent
        {
            Type = "error",
            SessionId = session.Id,
            Content = "All agents failed. Use the recover endpoint to retry.",
            Status = SessionStatus.Failed
        }, jsonOptions, sseLock);

        logger.LogError("Session {SessionId} failed — all agents returned errors", session.Id);
        return;
    }

    logger.LogInformation("Session {SessionId} — {SuccessCount}/{TotalCount} agents succeeded",
        session.Id, results.Count(r => !r.Failed), results.Length);

    // ── Evaluate ─────────────────────────────────────────────────────
    session.Status = SessionStatus.Evaluating;
    await sessionStore.UpdateAsync(session, ct);

    await SendEvent(httpContext, new StreamEvent
    {
        Type = "status",
        SessionId = session.Id,
        Content = "Evaluating responses",
        Status = SessionStatus.Evaluating
    }, jsonOptions, sseLock);

    var evaluation = await EvaluateResponsesAsync(results, request.Prompt, promptConfig.EvaluatorPrompt, copilotClient, logger);
    session.Evaluation = evaluation;

    // Add agent responses to chat history
    foreach (var r in results.Where(r => !r.Failed))
    {
        session.ChatHistory.Add(new ChatMessage
        {
            Role = "agent",
            Content = r.ResponseText,
            AgentName = r.AgentName
        });
    }

    session.ChatHistory.Add(new ChatMessage
    {
        Role = "evaluator",
        Content = $"Winner: {evaluation.Winner}\n\n{evaluation.Reasoning}"
    });

    // ── Awaiting Approval ────────────────────────────────────────────
    session.Status = SessionStatus.AwaitingApproval;
    await sessionStore.UpdateAsync(session, ct);

    await SendEvent(httpContext, new StreamEvent
    {
        Type = "evaluation",
        SessionId = session.Id,
        Content = evaluation.Reasoning,
        Evaluation = evaluation,
        Status = SessionStatus.AwaitingApproval
    }, jsonOptions, sseLock);

    logger.LogInformation("Session {SessionId} awaiting approval. Winner: {Winner}",
        session.Id, evaluation.Winner);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Session orchestration failed unexpectedly");
        try
        {
            if (session is not null)
            {
                session.Status = SessionStatus.Failed;
                session.ChatHistory.Add(new ChatMessage
                {
                    Role = "system",
                    Content = $"Orchestration failed: {ex.Message}"
                });
                await sessionStore.UpdateAsync(session, ct);
            }

            await SendEvent(httpContext, new StreamEvent
            {
                Type = "error",
                SessionId = session?.Id ?? "unknown",
                Content = $"Orchestration failed: {ex.Message}",
                Status = SessionStatus.Failed
            }, jsonOptions, sseLock);
        }
        catch { /* Best effort error reporting */ }
    }
});

// ── Decision Endpoint ────────────────────────────────────────────────

app.MapPost("/api/sessions/{id}/decide", async (
    string id,
    DecisionRequest decision,
    ISessionStore sessionStore,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var validDecisions = new[] { "accept", "decline", "restart" };
    if (!validDecisions.Contains(decision.Decision))
        return Results.BadRequest(new { error = $"Invalid decision '{decision.Decision}'. Must be one of: {string.Join(", ", validDecisions)}" });

    var session = await sessionStore.GetAsync(id, ct);
    if (session is null) return Results.NotFound();

    if (session.Status != SessionStatus.AwaitingApproval)
        return Results.BadRequest(new { error = $"Session is not awaiting approval (current status: {session.Status})" });

    session.Status = decision.Decision switch
    {
        "accept" => SessionStatus.Accepted,
        "decline" => SessionStatus.Declined,
        "restart" => SessionStatus.Restarted,
        _ => session.Status
    };

    session.ChatHistory.Add(new ChatMessage
    {
        Role = "system",
        Content = $"User decision: {decision.Decision}"
    });

    await sessionStore.UpdateAsync(session, ct);
    logger.LogInformation("Session {SessionId} decision: {Decision}", id, decision.Decision);

    return Results.Ok(session);
});

// ── Session Recovery Endpoint ────────────────────────────────────────

app.MapPost("/api/sessions/{id}/recover", async (
    string id,
    ISessionStore sessionStore,
    IPromptConfigStore promptStore,
    IHttpClientFactory httpClientFactory,
    ILogger<Program> logger,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    var session = await sessionStore.GetAsync(id, ct);
    if (session is null) return Results.NotFound();

    if (session.Status is not (SessionStatus.Failed or SessionStatus.Restarted))
        return Results.BadRequest(new { error = $"Session cannot be recovered (status: {session.Status})" });

    // Reset for re-processing
    session.Status = SessionStatus.Created;
    session.AgentResults.Clear();
    session.Evaluation = null;
    session.ChatHistory.Add(new ChatMessage
    {
        Role = "system",
        Content = "Session recovery initiated — re-running all agents."
    });
    await sessionStore.UpdateAsync(session, ct);

    logger.LogInformation("Session {SessionId} recovered and reset for re-processing", id);

    return Results.Ok(session);
});

app.Run();

// ── Helper Methods ───────────────────────────────────────────────────

static async Task SendEvent(HttpContext ctx, StreamEvent evt, JsonSerializerOptions opts, SemaphoreSlim sselock)
{
    await sselock.WaitAsync();
    try
    {
        var json = JsonSerializer.Serialize(evt, opts);
        await ctx.Response.WriteAsync($"data: {json}\n\n");
        await ctx.Response.Body.FlushAsync();
    }
    catch (Exception)
    {
        // Client disconnected — swallow to avoid breaking fan-out
    }
    finally
    {
        sselock.Release();
    }
}

static string GetAgentPrompt(PromptConfig config, string agentName, string defaultPrompt)
{
    return config.AgentPromptOverrides.TryGetValue(agentName, out var prompt) && !string.IsNullOrWhiteSpace(prompt)
        ? prompt
        : defaultPrompt;
}

static async Task<EvaluationResult> EvaluateResponsesAsync(
    AgentResult[] results, string originalPrompt, string evaluatorPrompt,
    CopilotClient copilotClient, ILogger logger)
{
    var validResults = results.Where(r => !r.Failed).ToList();
    if (validResults.Count == 0)
    {
        return new EvaluationResult
        {
            Winner = "none",
            Reasoning = "All agents failed to produce a response.",
            Scores = new Dictionary<string, AgentScore>(),
            AllResults = [.. results]
        };
    }

    // Build evaluation prompt with all agent responses
    var evalInput = new StringBuilder();
    evalInput.AppendLine($"## Original User Prompt\n{originalPrompt}\n");
    evalInput.AppendLine("## Agent Responses\n");
    foreach (var r in validResults)
    {
        evalInput.AppendLine($"### {r.AgentName} ({r.Model}) — {r.ElapsedMs}ms");
        evalInput.AppendLine(r.ResponseText);
        evalInput.AppendLine();
    }
    evalInput.AppendLine("Respond with ONLY valid JSON matching this schema:");
    evalInput.AppendLine("""{"winner":"<agent name>","reasoning":"<your analysis>","scores":{"<agent name>":{"accuracy":1-10,"completeness":1-10,"clarity":1-10,"relevance":1-10}}}""");

    try
    {
        await using var session = await copilotClient.CreateSessionAsync(new SessionConfig
        {
            Model = "claude-sonnet-4.6",
            Streaming = false,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = evaluatorPrompt
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
                case AssistantMessageDeltaEvent delta:
                    responseText.Append(delta.Data.DeltaContent);
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

        await session.SendAsync(new MessageOptions { Prompt = evalInput.ToString() });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using (cts.Token.Register(() => done.TrySetCanceled()))
        {
            try { await done.Task; }
            catch (TaskCanceledException) { /* timeout — use whatever we have */ }
        }

        if (errorMessage is not null)
        {
            logger.LogWarning("Evaluator returned error: {Error}", errorMessage);
            return FallbackEvaluation(results, validResults, $"Evaluator error: {errorMessage}");
        }

        // Parse the evaluator's JSON response
        var raw = responseText.ToString().Trim();
        logger.LogInformation("Evaluator raw response (first 200 chars): {Raw}", raw.Length > 200 ? raw[..200] : raw);

        // Strip markdown code fences — handle multiple formats
        raw = StripCodeFences(raw);
        logger.LogInformation("Evaluator stripped response (first 200 chars): {Stripped}", raw.Length > 200 ? raw[..200] : raw);

        try
        {
            var evalJson = JsonSerializer.Deserialize<EvalJson>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (evalJson is not null)
            {
                var scores = evalJson.Scores?.ToDictionary(
                    kv => kv.Key,
                    kv => new AgentScore
                    {
                        Accuracy = kv.Value.Accuracy,
                        Completeness = kv.Value.Completeness,
                        Clarity = kv.Value.Clarity,
                        Relevance = kv.Value.Relevance
                    }) ?? [];

                return new EvaluationResult
                {
                    Winner = evalJson.Winner ?? validResults.First().AgentName,
                    Reasoning = evalJson.Reasoning ?? "No reasoning provided",
                    Scores = scores,
                    AllResults = [.. results]
                };
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse evaluator JSON: {Raw}", raw);
        }

        // Evaluator returned non-JSON — use the text as reasoning
        return new EvaluationResult
        {
            Winner = validResults.OrderByDescending(r => r.ResponseText.Length).First().AgentName,
            Reasoning = $"[Evaluator response — non-JSON]: {raw}",
            Scores = new Dictionary<string, AgentScore>(),
            AllResults = [.. results]
        };
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Evaluator agent failed");
        return FallbackEvaluation(results, validResults, $"Evaluator failed: {ex.Message}");
    }
}

static EvaluationResult FallbackEvaluation(AgentResult[] allResults, List<AgentResult> validResults, string reason)
{
    var winner = validResults.OrderByDescending(r => r.ResponseText.Length).First();
    return new EvaluationResult
    {
        Winner = winner.AgentName,
        Reasoning = $"[Fallback heuristic — {reason}] Selected {winner.AgentName} by response length.",
        Scores = validResults.ToDictionary(
            r => r.AgentName,
            r => new AgentScore
            {
                Accuracy = r == winner ? 8 : 6,
                Completeness = r == winner ? 9 : 5,
                Clarity = r == winner ? 8 : 6,
                Relevance = r == winner ? 9 : 7
            }),
        AllResults = [.. allResults]
    };
}

static string StripCodeFences(string text)
{
    var s = text.Trim();

    // Handle ```json\n...\n``` (multi-line)
    if (s.StartsWith("```"))
    {
        var firstNewline = s.IndexOf('\n');
        if (firstNewline > 0)
        {
            var inner = s[(firstNewline + 1)..];
            var lastFence = inner.LastIndexOf("```");
            if (lastFence >= 0)
                return inner[..lastFence].Trim();
        }
        // Single-line: ```{...}```
        s = s.TrimStart('`').TrimEnd('`');
        if (s.StartsWith("json")) s = s[4..];
        return s.Trim();
    }

    // Extract first JSON object if wrapped in prose
    var firstBrace = s.IndexOf('{');
    var lastBrace = s.LastIndexOf('}');
    if (firstBrace >= 0 && lastBrace > firstBrace)
    {
        return s[firstBrace..(lastBrace + 1)];
    }

    return s;
}

// ── Inline Types ─────────────────────────────────────────────────────

public sealed record DecisionRequest
{
    public required string Decision { get; init; } // "accept", "decline", "restart"
}

internal sealed record EvalJson
{
    public string? Winner { get; init; }
    public string? Reasoning { get; init; }
    public Dictionary<string, EvalScoreJson>? Scores { get; init; }
}

internal sealed record EvalScoreJson
{
    public int Accuracy { get; init; }
    public int Completeness { get; init; }
    public int Clarity { get; init; }
    public int Relevance { get; init; }
}
