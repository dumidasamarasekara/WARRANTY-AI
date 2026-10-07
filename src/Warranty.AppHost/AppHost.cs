using Aspire.Hosting.ApplicationModel;

// Local orchestration of the PoC (research R22). Containers run on Podman when
// DOTNET_ASPIRE_CONTAINER_RUNTIME=podman is set.
var builder = DistributedApplication.CreateBuilder(args);

var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));

// "live" calls the chat provider; "replay" answers from recordings and needs no key.
var aiMode = builder.Configuration["AiGateway:Mode"] ?? "live";

// Live chat provider: "anthropic" (the anthropic-api-key secret) or "ollama" (a self-hosted Ollama
// server, by default the one on this machine — not the embeddings container — with no key and no cost).
var chatProvider = builder.Configuration["AiGateway:ChatProvider"] ?? "anthropic";
string[] chatRoutes = ["extraction", "vision", "policy-reasoning", "adjudication"];

// The API connects as warranty_app (research R8); the migration service sets this password on every start.
var appRolePassword = builder.AddParameter(
    "app-role-password", new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true, persist: true);

var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector")
    .WithImageTag("pg17")
    .WithDataVolume();
var warrantyDb = postgres.AddDatabase("warranty");
var knowledgeDb = postgres.AddDatabase("knowledge");

var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator.WithDataVolume());
var blobs = storage.AddBlobs("blobs");

// Fixed port: the SPA and the realm's redirect URIs use http://localhost:8080 as the issuer.
var keycloak = builder.AddKeycloak("keycloak", port: 8080)
    .WithRealmImport(Path.Combine(repoRoot, "infra", "keycloak"));

var ollama = builder.AddOllama("ollama")
    .WithDataVolume();
var embeddings = ollama.AddModel("embeddings", "nomic-embed-text");

var migrations = builder.AddProject<Projects.Warranty_MigrationService>("migrations")
    .WithReference(warrantyDb)
    .WithReference(knowledgeDb)
    .WithReference(blobs)
    .WithReference(embeddings)
    .WithEnvironment("Database__AppRolePassword", appRolePassword)
    .WithEnvironment("Seed__Path", Path.Combine(repoRoot, "seed"))
    .WaitFor(postgres)
    .WaitFor(storage)
    .WaitFor(ollama)
    .WaitFor(embeddings);

var api = builder.AddProject<Projects.Warranty_Api>("api")
    .WithReference(warrantyDb)
    .WithReference(knowledgeDb)
    .WithReference(blobs)
    .WithReference(embeddings)
    .WithReference(keycloak)
    .WithEnvironment("Database__AppRolePassword", appRolePassword)
    .WithEnvironment("AiGateway__Mode", aiMode)
    .WaitForCompletion(migrations)
    .WaitFor(keycloak);

if (aiMode == "live" && chatProvider == "ollama")
{
    // One vision + tools model serves every chat route, so only one model has to fit in GPU memory.
    // Local models are slower than the API, so each call gets a longer timeout.
    var ollamaModel = builder.Configuration["AiGateway:OllamaChatModel"] ?? "qwen3-vl:4b-instruct";
    api.WithEnvironment("AiGateway__Ollama__Endpoint", builder.Configuration["AiGateway:OllamaEndpoint"] ?? "http://localhost:11434");
    foreach (var route in chatRoutes)
    {
        api.WithEnvironment($"AiGateway__Routes__{route}__Provider", "ollama")
            .WithEnvironment($"AiGateway__Routes__{route}__Model", ollamaModel)
            .WithEnvironment($"AiGateway__Routes__{route}__TimeoutSeconds", "180");
    }
}
else if (aiMode == "live")
{
    var anthropicApiKey = builder.AddParameter("anthropic-api-key", secret: true);
    api.WithEnvironment("AiGateway__Anthropic__ApiKey", anthropicApiKey);
}

// Fixed port 5173: the claimant channels (aurora.localhost, borealis.localhost) and the realm's
// redirect URIs are bound to it. /api is proxied by Vite so the API sees the channel Host header.
builder.AddViteApp("web", Path.Combine(repoRoot, "src", "web"))
    .WithNpm(install: true)
    .WithEndpoint("http", endpoint =>
    {
        endpoint.Port = 5173;
        endpoint.TargetPort = 5173;
        endpoint.IsProxied = false;
    })
    .WithReference(api)
    .WithEnvironment("VITE_API_URL", api.GetEndpoint("https"))
    .WithEnvironment("VITE_KEYCLOAK_AUTHORITY", ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/warranty"))
    .WithEnvironment("VITE_KEYCLOAK_CLIENT_ID", "warranty-web")
    .WaitFor(api)
    .WaitFor(keycloak);

builder.Build().Run();
