var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

using var host = builder.Build();

// Migration, roles/RLS, seeding and knowledge indexing are added in T038; until then the service
// starts and exits successfully so the AppHost can depend on its completion.
return 0;
