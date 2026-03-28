var builder = DistributedApplication.CreateBuilder(args);

var agentSonnet = builder.AddProject<Projects.AgenticWorkflow_Agent_Sonnet>("agent-sonnet");
var agentCodex = builder.AddProject<Projects.AgenticWorkflow_Agent_GptCodex>("agent-codex");
var agentGpt54 = builder.AddProject<Projects.AgenticWorkflow_Agent_Gpt54>("agent-gpt54");

var gateway = builder.AddProject<Projects.AgenticWorkflow_Gateway>("gateway")
    .WithReference(agentSonnet)
    .WithReference(agentCodex)
    .WithReference(agentGpt54)
    .WithExternalHttpEndpoints();

builder.AddViteApp("frontend", "../AgenticWorkflow.Frontend")
    .WithReference(gateway)
    .WithExternalHttpEndpoints();

builder.Build().Run();
