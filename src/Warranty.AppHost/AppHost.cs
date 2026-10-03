var builder = DistributedApplication.CreateBuilder(args);

// Postgres, Azurite, Keycloak, Ollama, the migration service, the API and the SPA are wired in T039.

builder.Build().Run();
