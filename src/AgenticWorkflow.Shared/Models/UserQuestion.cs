using System.Text.Json.Serialization;

namespace AgenticWorkflow.Shared.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuestionInputType
{
    FreeText,
    SingleChoice,
    MultiChoice
}

public sealed record UserQuestion
{
    public required string QuestionId { get; init; }
    public required string Source { get; init; } // "agent" | "evaluator"
    public string? SourceName { get; init; }
    public required string Prompt { get; init; }
    public string? ContextMessageId { get; init; }
    public QuestionInputType InputType { get; init; } = QuestionInputType.FreeText;
    public List<string> Choices { get; init; } = [];
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record UserQuestionAnswer
{
    public required string QuestionId { get; init; }
    public string? AnswerText { get; init; }
    public List<string> SelectedChoices { get; init; } = [];
    public DateTimeOffset AnsweredAt { get; init; } = DateTimeOffset.UtcNow;
}
