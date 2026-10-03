using Warranty.MigrationService.Seeding;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddMigrationService();

using var host = builder.Build();
await host.RunAsync();

// MigrationWorker sets 0 only when every step succeeded; the AppHost waits for that before starting the API.
return Environment.ExitCode;
