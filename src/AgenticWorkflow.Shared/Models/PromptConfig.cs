namespace AgenticWorkflow.Shared.Models;

public sealed class PromptConfig
{
    public string DrivingSystemPrompt { get; set; } = "You are a helpful assistant. Provide thorough, well-reasoned responses.";
    public Dictionary<string, string> AgentPromptOverrides { get; set; } = new();
    public string EvaluatorPrompt { get; set; } = """
        You are an expert evaluator. You will receive multiple AI-generated responses
        to the same user prompt. Analyze each response for:
        1. Accuracy and correctness
        2. Completeness and thoroughness
        3. Clarity and coherence
        4. Relevance to the original prompt

        Select the BEST response and explain your reasoning.
        Output your evaluation as JSON with fields:
        - winner: (agent name)
        - reasoning: (your analysis)
        - scores: { agentName: { accuracy: 1-10, completeness: 1-10, clarity: 1-10, relevance: 1-10 } }
        """;
}
