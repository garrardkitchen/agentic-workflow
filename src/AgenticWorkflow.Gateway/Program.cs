using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using AgenticWorkflow.Shared.Models;
using AgenticWorkflow.Shared.Services;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Http.Resilience;

var builder = WebApplication.CreateBuilder(args);
var agentRequestTimeout = TimeSpan.FromSeconds(90);

// Add service defaults but override HTTP resilience for LLM-bound calls
builder.AddServiceDefaults();

// Configure HTTP resilience for LLM-bound calls — fail fast on unresponsive agents
builder.Services.ConfigureHttpClientDefaults(http =>
{
    http.AddServiceDiscovery();
    http.AddStandardResilienceHandler(options =>
    {
        options.TotalRequestTimeout.Timeout = agentRequestTimeout;
        options.AttemptTimeout.Timeout = agentRequestTimeout;
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
        c.Timeout = agentRequestTimeout;
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
var sessionMutationLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

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
    var sessionId = session.Id;

    await SendEvent(httpContext, new StreamEvent
    {
        Type = "status",
        SessionId = sessionId,
        Content = "Session created",
        Status = SessionStatus.Created
    }, jsonOptions, sseLock);

    // ── Fan-out to 3 agents ──────────────────────────────────────────
    session.Status = SessionStatus.AgentsRunning;
    await sessionStore.UpdateAsync(session, ct);

    await SendEvent(httpContext, new StreamEvent
    {
        Type = "status",
        SessionId = sessionId,
        Content = "Agents running",
        Status = SessionStatus.AgentsRunning
    }, jsonOptions, sseLock);

    var agentNames = new[] { "agent-sonnet", "agent-codex", "agent-gpt54" };
    var emittedQuestionKeys = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
    var emittedQuestionsByKey = new ConcurrentDictionary<string, UserQuestion>(StringComparer.OrdinalIgnoreCase);
    using var questionStateLock = new SemaphoreSlim(1, 1);
    var agentTasks = agentNames.Select(async name =>
    {
        try
        {
            var client = httpClientFactory.CreateClient(name);
            var agentRequest = new AgentRequest
            {
                Prompt = request.Prompt,
                SessionId = sessionId,
                SystemPromptOverride = GetAgentPrompt(promptConfig, name, systemPrompt)
            };

            var streamPath =
                $"/api/run-stream?prompt={Uri.EscapeDataString(agentRequest.Prompt)}" +
                $"&systemPrompt={Uri.EscapeDataString(agentRequest.SystemPromptOverride ?? string.Empty)}";

            using var streamRequest = new HttpRequestMessage(HttpMethod.Get, streamPath);
            using var response = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(responseStream);
            var streamMessageId = $"m-{Guid.NewGuid():N}";

            var responseText = new StringBuilder();
            var resultAgentName = GetAgentNameForServiceName(name);
            var resultModel = "unknown";
            long elapsedMs = 0;
            bool failed = false;
            string? errorText = null;

            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
                    continue;

                var payload = line[6..];
                try
                {
                    using var json = JsonDocument.Parse(payload);
                    var root = json.RootElement;

                    if (root.TryGetProperty("agent", out var agentProp))
                    {
                        var streamedAgent = agentProp.GetString();
                        if (!string.IsNullOrWhiteSpace(streamedAgent))
                            resultAgentName = streamedAgent!;
                    }

                    if (root.TryGetProperty("model", out var modelProp))
                    {
                        var streamedModel = modelProp.GetString();
                        if (!string.IsNullOrWhiteSpace(streamedModel))
                            resultModel = streamedModel!;
                    }

                    if (root.TryGetProperty("text", out var textProp))
                    {
                        var token = textProp.GetString() ?? string.Empty;
                        if (token.Length > 0)
                        {
                            responseText.Append(token);
                            await SendEvent(httpContext, new StreamEvent
                            {
                                Type = "agent_token",
                                SessionId = sessionId,
                                AgentName = resultAgentName,
                                MessageId = streamMessageId,
                                Content = token
                            }, jsonOptions, sseLock);
                        }
                    }

                    if (root.TryGetProperty("elapsedMs", out var elapsedProp) && elapsedProp.TryGetInt64(out var parsedElapsed))
                    {
                        elapsedMs = parsedElapsed;
                    }

                    if (root.TryGetProperty("error", out var errorProp))
                    {
                        failed = true;
                        errorText = errorProp.GetString() ?? "Unknown agent streaming error";
                    }

                    if (root.TryGetProperty("done", out var doneProp) &&
                        doneProp.ValueKind == JsonValueKind.True)
                    {
                        break;
                    }
                }
                catch (JsonException jsonEx)
                {
                    logger.LogWarning(jsonEx, "Agent {AgentName} sent invalid streaming payload", name);
                }
            }

            var result = new AgentResult
            {
                AgentName = resultAgentName,
                Model = resultModel,
                ResponseText = responseText.ToString(),
                ElapsedMs = elapsedMs,
                Failed = failed,
                Error = errorText
            };

            await SendEvent(httpContext, new StreamEvent
            {
                Type = "agent_complete",
                SessionId = sessionId,
                AgentName = result.AgentName,
                MessageId = streamMessageId,
                Content = result.ResponseText,
                AgentResult = result
            }, jsonOptions, sseLock);

            // Prompt follow-up questions as soon as an agent finishes instead of waiting for all agents.
            if (!result.Failed)
            {
                var contextByAgent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [result.AgentName] = streamMessageId
                };
                var earlyQuestions = TryExtractUserQuestions([result], contextByAgent);
                foreach (var q in earlyQuestions)
                {
                    var key = GetQuestionKey(q);
                    if (!emittedQuestionKeys.TryAdd(key, 0)) continue;
                    emittedQuestionsByKey.TryAdd(key, q);

                    await questionStateLock.WaitAsync(ct);
                    try
                    {
                        var alreadyPending = session.PendingQuestions.Any(existing =>
                            string.Equals(GetQuestionKey(existing), key, StringComparison.OrdinalIgnoreCase));
                        if (!alreadyPending)
                        {
                            session.PendingQuestions.Add(q);
                            session.ChatHistory.Add(new ChatMessage
                            {
                                Role = "question",
                                ParentMessageId = q.ContextMessageId,
                                Content = q.Prompt,
                                AgentName = q.SourceName
                            });
                        }

                        session.Status = SessionStatus.AwaitingInput;
                        await sessionStore.UpdateAsync(session, ct);
                    }
                    finally
                    {
                        questionStateLock.Release();
                    }

                    await SendEvent(httpContext, new StreamEvent
                    {
                        Type = "question_required",
                        SessionId = sessionId,
                        Content = q.Prompt,
                        Question = q,
                        Status = SessionStatus.AwaitingInput
                    }, jsonOptions, sseLock);
                }
            }
            return (Result: result, MessageId: streamMessageId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent {AgentName} failed", name);
            await SendEvent(httpContext, new StreamEvent
            {
                Type = "error",
                SessionId = sessionId,
                AgentName = name,
                Content = $"Agent {name} failed: {ex.Message}"
            }, jsonOptions, sseLock);

            return (Result: new AgentResult
            {
                AgentName = GetAgentNameForServiceName(name),
                Model = "unknown",
                ResponseText = "",
                Failed = true,
                Error = ex.Message
            }, MessageId: (string?)null);
        }
    });

    var executions = await Task.WhenAll(agentTasks);
    var results = executions.Select(e => e.Result).ToArray();
    session.AgentResults = [.. results];
    var agentMessageContext = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var execution in executions.Where(e => !e.Result.Failed))
    {
        var messageId = execution.MessageId ?? $"m-{Guid.NewGuid():N}";
        agentMessageContext[execution.Result.AgentName] = messageId;
        session.ChatHistory.Add(new ChatMessage
        {
            Role = "agent",
            MessageId = messageId,
            Content = execution.Result.ResponseText,
            AgentName = execution.Result.AgentName
        });
    }

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
            SessionId = sessionId,
            Content = "All agents failed. Use the recover endpoint to retry.",
            Status = SessionStatus.Failed
        }, jsonOptions, sseLock);

        logger.LogError("Session {SessionId} failed — all agents returned errors", session.Id);
        return;
    }

    logger.LogInformation("Session {SessionId} — {SuccessCount}/{TotalCount} agents succeeded",
        session.Id, results.Count(r => !r.Failed), results.Length);

    // ── Human Input Gate (AG-UI) ──────────────────────────────────────
    var extractedFollowUpQuestions = TryExtractUserQuestions(results, agentMessageContext);
    var followUpQuestions = new List<UserQuestion>();
    var seenQuestionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var pending in session.PendingQuestions)
    {
        var key = GetQuestionKey(pending);
        if (seenQuestionKeys.Add(key)) followUpQuestions.Add(pending);
    }

    foreach (var q in extractedFollowUpQuestions)
    {
        var key = GetQuestionKey(q);
        if (!seenQuestionKeys.Add(key)) continue;
        if (emittedQuestionsByKey.TryGetValue(key, out var emittedQuestion))
            followUpQuestions.Add(emittedQuestion);
        else
            followUpQuestions.Add(q);
    }

    if (followUpQuestions.Count > 0)
    {
        session.PendingQuestions = followUpQuestions;
        session.Status = SessionStatus.AwaitingInput;
        foreach (var q in followUpQuestions)
        {
            var existingQuestionMessage = session.ChatHistory.Any(m =>
                string.Equals(m.Role, "question", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.AgentName, q.SourceName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.ParentMessageId, q.ContextMessageId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m.Content?.Trim(), q.Prompt.Trim(), StringComparison.OrdinalIgnoreCase));

            if (!existingQuestionMessage)
            {
                session.ChatHistory.Add(new ChatMessage
                {
                    Role = "question",
                    ParentMessageId = q.ContextMessageId,
                    Content = q.Prompt,
                    AgentName = q.SourceName
                });
            }
        }
        await sessionStore.UpdateAsync(session, ct);

        foreach (var q in followUpQuestions)
        {
            var key = GetQuestionKey(q);
            if (emittedQuestionKeys.ContainsKey(key)) continue;
            await SendEvent(httpContext, new StreamEvent
            {
                Type = "question_required",
                SessionId = sessionId,
                Content = q.Prompt,
                Question = q,
                Status = SessionStatus.AwaitingInput
            }, jsonOptions, sseLock);
        }

        logger.LogInformation(
            "Session {SessionId} awaiting user input with {QuestionCount} follow-up questions",
            session.Id, followUpQuestions.Count);
        return;
    }

    // ── Evaluate ─────────────────────────────────────────────────────
    session.Status = SessionStatus.Evaluating;
    await sessionStore.UpdateAsync(session, ct);

    await SendEvent(httpContext, new StreamEvent
    {
        Type = "status",
        SessionId = sessionId,
        Content = "Evaluating responses",
        Status = SessionStatus.Evaluating
    }, jsonOptions, sseLock);

    var evaluation = await EvaluateResponsesAsync(results, request.Prompt, promptConfig.EvaluatorPrompt, copilotClient, logger);
    session.Evaluation = evaluation;

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
        SessionId = sessionId,
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
    IPromptConfigStore promptStore,
    CopilotClient copilotClient,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var sessionLock = sessionMutationLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    await sessionLock.WaitAsync(ct);
    try
    {
        var validDecisions = new[] { "accept", "decline", "restart" };
        if (!validDecisions.Contains(decision.Decision))
            return Results.BadRequest(new { error = $"Invalid decision '{decision.Decision}'. Must be one of: {string.Join(", ", validDecisions)}" });

        var session = await sessionStore.GetAsync(id, ct);
        if (session is null) return Results.NotFound();

        if (session.Status == SessionStatus.AwaitingInput)
        {
            if (decision.Decision != "decline")
                return Results.BadRequest(new { error = "Only 'decline' is allowed while awaiting user input." });

            var skipped = session.PendingQuestions.FirstOrDefault();
            if (skipped is not null)
            {
                session.PendingQuestions.Remove(skipped);
                session.ChatHistory.Add(new ChatMessage
                {
                    Role = "system",
                    ParentMessageId = skipped.ContextMessageId,
                    Content = $"Skipped clarification from {skipped.SourceName ?? skipped.Source}; keeping current response."
                });
            }

            if (session.PendingQuestions.Count > 0)
            {
                session.Status = SessionStatus.AwaitingInput;
            }
            else
            {
                var promptConfig = await promptStore.GetAsync(ct);
                session.Status = SessionStatus.Evaluating;
                session.Evaluation = await EvaluateResponsesAsync(
                    [.. session.AgentResults],
                    session.Prompt,
                    promptConfig.EvaluatorPrompt,
                    copilotClient,
                    logger);
                session.ChatHistory.Add(new ChatMessage
                {
                    Role = "evaluator",
                    Content = $"Winner: {session.Evaluation.Winner}\n\n{session.Evaluation.Reasoning}"
                });
                session.Status = SessionStatus.AwaitingApproval;
            }
        }
        else if (session.Status == SessionStatus.AwaitingApproval)
        {
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
        }
        else
        {
            return Results.BadRequest(new { error = $"Session is not in a decision state (current status: {session.Status})" });
        }

        await sessionStore.UpdateAsync(session, ct);
        logger.LogInformation("Session {SessionId} decision: {Decision}", id, decision.Decision);

        return Results.Ok(session);
    }
    finally
    {
        sessionLock.Release();
    }
});

// ── Question Answer Endpoint ──────────────────────────────────────────

app.MapPost("/api/sessions/{id}/answer", async (
    string id,
    QuestionAnswerRequest answer,
    ISessionStore sessionStore,
    IPromptConfigStore promptStore,
    IHttpClientFactory httpClientFactory,
    CopilotClient copilotClient,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var sessionLock = sessionMutationLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
    await sessionLock.WaitAsync(ct);
    try
    {
        var session = await sessionStore.GetAsync(id, ct);
        if (session is null) return Results.NotFound();

        if (session.PendingQuestions.Count == 0)
            return Results.BadRequest(new { error = $"Session is not awaiting input (current status: {session.Status})" });

        var question = session.PendingQuestions.FirstOrDefault(q => q.QuestionId == answer.QuestionId);
        if (question is null)
            return Results.BadRequest(new { error = $"Question '{answer.QuestionId}' not found for this session." });

        if (question.InputType == QuestionInputType.FreeText && string.IsNullOrWhiteSpace(answer.AnswerText))
            return Results.BadRequest(new { error = "Answer text is required for free-text questions." });

        if (question.InputType != QuestionInputType.FreeText && (answer.SelectedChoices is null || answer.SelectedChoices.Count == 0))
            return Results.BadRequest(new { error = "At least one selected choice is required for choice questions." });

        if (question.InputType == QuestionInputType.SingleChoice && (answer.SelectedChoices is null || answer.SelectedChoices.Count != 1))
            return Results.BadRequest(new { error = "SingleChoice questions require exactly one selected choice." });

        if (question.InputType != QuestionInputType.FreeText && answer.SelectedChoices is not null)
        {
            var invalidChoices = answer.SelectedChoices
                .Where(c => !question.Choices.Contains(c, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (invalidChoices.Count > 0)
                return Results.BadRequest(new { error = $"Invalid choices: {string.Join(", ", invalidChoices)}" });
        }

        session.PendingQuestions.RemoveAll(q => q.QuestionId == question.QuestionId);
        var recorded = new UserQuestionAnswer
        {
            QuestionId = question.QuestionId,
            AnswerText = answer.AnswerText,
            SelectedChoices = answer.SelectedChoices ?? []
        };
        session.AnsweredQuestions.Add(recorded);

        var renderedAnswer = !string.IsNullOrWhiteSpace(answer.AnswerText)
            ? answer.AnswerText!
            : string.Join(", ", answer.SelectedChoices ?? []);

        session.ChatHistory.Add(new ChatMessage
        {
            Role = "answer",
            Content = renderedAnswer,
            ParentMessageId = question.ContextMessageId,
            AgentName = question.SourceName
        });

        // Continue only the originating agent thread with this clarification.
        if (!string.IsNullOrWhiteSpace(question.SourceName))
        {
            var serviceName = GetServiceNameForAgent(question.SourceName);
            if (serviceName is null)
                return Results.BadRequest(new { error = $"Unknown question source '{question.SourceName}'." });

            var promptConfig = await promptStore.GetAsync(ct);
            var defaultPrompt = session.SystemPrompt ?? promptConfig.DrivingSystemPrompt;
            var agentPrompt = GetAgentPrompt(promptConfig, serviceName, defaultPrompt);
            var continuationPrompt = BuildAgentContinuationPrompt(session.Prompt, question.Prompt, renderedAnswer);

            var client = httpClientFactory.CreateClient(serviceName);
            var continuationRequest = new AgentRequest
            {
                Prompt = continuationPrompt,
                SessionId = session.Id,
                SystemPromptOverride = agentPrompt
            };

            var continuationResponse = await client.PostAsJsonAsync("/api/run", continuationRequest, ct);
            continuationResponse.EnsureSuccessStatusCode();
            var updatedResult = await continuationResponse.Content.ReadFromJsonAsync<AgentResult>(ct);
            if (updatedResult is not null)
            {
                var existing = session.AgentResults.FindIndex(r => r.AgentName == updatedResult.AgentName);
                if (existing >= 0) session.AgentResults[existing] = updatedResult;
                else session.AgentResults.Add(updatedResult);

                session.ChatHistory.Add(new ChatMessage
                {
                    Role = "agent",
                    MessageId = $"m-{Guid.NewGuid():N}",
                    ParentMessageId = question.ContextMessageId,
                    Content = updatedResult.ResponseText,
                    AgentName = updatedResult.AgentName
                });
                var contextMessageId = session.ChatHistory.Last().MessageId;

                var contextByAgent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(contextMessageId))
                {
                    contextByAgent[updatedResult.AgentName] = contextMessageId;
                }

                var followUps = TryExtractUserQuestions([updatedResult], contextByAgent);
                foreach (var followUp in followUps)
                {
                    var duplicatePending = session.PendingQuestions.Any(p =>
                        string.Equals(p.SourceName, followUp.SourceName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(p.Prompt.Trim(), followUp.Prompt.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (!duplicatePending)
                    {
                        session.PendingQuestions.Add(followUp);
                        session.ChatHistory.Add(new ChatMessage
                        {
                            Role = "question",
                            ParentMessageId = followUp.ContextMessageId,
                            Content = followUp.Prompt,
                            AgentName = followUp.SourceName
                        });
                    }
                }
            }
        }

        if (session.PendingQuestions.Count > 0)
        {
            session.Status = SessionStatus.AwaitingInput;
        }
        else
        {
            var promptConfig = await promptStore.GetAsync(ct);
            session.Status = SessionStatus.Evaluating;
            session.Evaluation = await EvaluateResponsesAsync(
                [.. session.AgentResults],
                session.Prompt,
                promptConfig.EvaluatorPrompt,
                copilotClient,
                logger);
            session.ChatHistory.Add(new ChatMessage
            {
                Role = "evaluator",
                Content = $"Winner: {session.Evaluation.Winner}\n\n{session.Evaluation.Reasoning}"
            });
            session.Status = SessionStatus.AwaitingApproval;
        }

        await sessionStore.UpdateAsync(session, ct);
        logger.LogInformation("Session {SessionId} answered question {QuestionId}", id, question.QuestionId);

        return Results.Ok(session);
    }
    finally
    {
        sessionLock.Release();
    }
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

static string? GetServiceNameForAgent(string agentName)
{
    if (agentName.Equals("Agent-Sonnet", StringComparison.OrdinalIgnoreCase)) return "agent-sonnet";
    if (agentName.Equals("Agent-GptCodex", StringComparison.OrdinalIgnoreCase)) return "agent-codex";
    if (agentName.Equals("Agent-Gpt54", StringComparison.OrdinalIgnoreCase)) return "agent-gpt54";
    return null;
}

static string BuildAgentContinuationPrompt(string originalPrompt, string question, string answer)
{
    return $"""
Original request:
{originalPrompt}

Clarification requested:
{question}

User answer:
{answer}

Please continue your previous response with this clarification and provide an updated final answer.
""";
}

static string GetQuestionKey(UserQuestion question)
{
    return $"{question.SourceName}|{question.ContextMessageId}|{question.Prompt.Trim()}";
}

static string GetAgentNameForServiceName(string serviceName)
{
    if (serviceName.Equals("agent-sonnet", StringComparison.OrdinalIgnoreCase)) return "Agent-Sonnet";
    if (serviceName.Equals("agent-codex", StringComparison.OrdinalIgnoreCase)) return "Agent-GptCodex";
    if (serviceName.Equals("agent-gpt54", StringComparison.OrdinalIgnoreCase)) return "Agent-Gpt54";
    return serviceName;
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

static List<UserQuestion> TryExtractUserQuestions(
    IEnumerable<AgentResult> results,
    IReadOnlyDictionary<string, string>? contextByAgent = null)
{
    // 1) Explicit marker contract (preferred):
    //    [QUESTION] ...
    //    [CHOICES] a|b|c
    //    [INPUT] FreeText|SingleChoice|MultiChoice
    // 2) Fallback natural-language detection:
    //    last non-empty line that ends with "?" and appears to ask the user for input
    var questions = new List<UserQuestion>();
    foreach (var r in results.Where(r => !r.Failed))
    {
        var text = r.ResponseText;
        if (string.IsNullOrWhiteSpace(text)) continue;

        if (text.Contains("[QUESTION]", StringComparison.OrdinalIgnoreCase))
        {
            var markerQuestion = ParseMarkerQuestion(
                text,
                r.AgentName,
                contextByAgent is not null && contextByAgent.TryGetValue(r.AgentName, out var markerContext) ? markerContext : null);
            if (markerQuestion is not null)
            {
                questions.Add(markerQuestion);
                continue;
            }
        }

        var naturalQuestion = TryExtractNaturalLanguageQuestion(
            text,
            r.AgentName,
            contextByAgent is not null && contextByAgent.TryGetValue(r.AgentName, out var naturalContext) ? naturalContext : null);
        if (naturalQuestion is not null)
        {
            questions.Add(naturalQuestion);
        }
    }

    return questions
        .GroupBy(q => new
        {
            Source = q.SourceName?.Trim().ToLowerInvariant() ?? string.Empty,
            Prompt = q.Prompt.Trim().ToLowerInvariant(),
            q.InputType,
            ChoicesKey = string.Join("|",
                q.Choices
                    .Select(c => c.Trim().ToLowerInvariant())
                    .OrderBy(c => c, StringComparer.Ordinal))
        })
        .Select(g => g.First())
        .ToList();
}

static UserQuestion? ParseMarkerQuestion(string text, string sourceName, string? contextMessageId)
{
    var qMatch = Regex.Match(text, @"\[QUESTION\]\s*(.+?)(?:\r?\n|$)", RegexOptions.IgnoreCase);
    if (!qMatch.Success) return null;
    var prompt = qMatch.Groups[1].Value.Trim();
    if (string.IsNullOrWhiteSpace(prompt)) return null;

    var choices = new List<string>();
    var choicesMatch = Regex.Match(text, @"\[CHOICES\]\s*(.+?)(?:\r?\n|$)", RegexOptions.IgnoreCase);
    if (choicesMatch.Success)
    {
        choices = choicesMatch.Groups[1].Value
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    var inputType = QuestionInputType.FreeText;
    var inputMatch = Regex.Match(text, @"\[INPUT\]\s*(.+?)(?:\r?\n|$)", RegexOptions.IgnoreCase);
    if (inputMatch.Success && Enum.TryParse<QuestionInputType>(inputMatch.Groups[1].Value.Trim(), true, out var parsed))
    {
        inputType = parsed;
    }
    else if (choices.Count > 0)
    {
        inputType = QuestionInputType.SingleChoice;
    }

    return new UserQuestion
    {
        QuestionId = $"q-{Guid.NewGuid():N}",
        Source = "agent",
        SourceName = sourceName,
        Prompt = prompt,
        ContextMessageId = contextMessageId,
        InputType = inputType,
        Choices = choices
    };
}

static UserQuestion? TryExtractNaturalLanguageQuestion(string text, string sourceName, string? contextMessageId)
{
    var lines = text
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(l => !l.StartsWith("Acceptance Criteria", StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (lines.Count == 0) return null;

    // Use last question-like line to avoid picking up earlier rhetorical lines.
    var candidate = lines.LastOrDefault(l => l.EndsWith('?'));
    if (string.IsNullOrWhiteSpace(candidate)) return null;

    // Avoid capturing obvious non-user prompts.
    if (candidate.Length < 8 || candidate.Length > 300) return null;

    // Ask-for-input cues to reduce false positives.
    var asksUser = Regex.IsMatch(candidate,
        @"\b(would you|do you|can you|could you|please provide|which|what is your|choose|select|tell me)\b",
        RegexOptions.IgnoreCase);
    if (!asksUser) return null;

    return new UserQuestion
    {
        QuestionId = $"q-{Guid.NewGuid():N}",
        Source = "agent",
        SourceName = sourceName,
        Prompt = candidate.Trim(),
        ContextMessageId = contextMessageId,
        InputType = QuestionInputType.FreeText,
        Choices = []
    };
}

// ── Inline Types ─────────────────────────────────────────────────────

public sealed record DecisionRequest
{
    public required string Decision { get; init; } // "accept", "decline", "restart"
}

public sealed record QuestionAnswerRequest
{
    public required string QuestionId { get; init; }
    public string? AnswerText { get; init; }
    public List<string>? SelectedChoices { get; init; }
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
