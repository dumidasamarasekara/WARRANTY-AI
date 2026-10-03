using System.Text.Json.Serialization;
using Warranty.AI.Gateway;
using Warranty.AI.Harness;
using Warranty.Api.Auth;
using Warranty.Api.Endpoints;
using Warranty.Api.Http;
using Warranty.Api.RateLimiting;
using Warranty.Api.Tenancy;
using Warranty.Application;
using Warranty.Guardrails;
using Warranty.Infrastructure;
using Warranty.Integrations.Simulated;
using Warranty.Knowledge;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// RFC 9457 ProblemDetails for exceptions, empty error responses and explicit problems.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions[CorrelationId.ProblemDetailsExtension] = CorrelationId.Of(context.HttpContext));
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

builder.Services.AddWarrantyAuth(builder.Configuration, builder.Environment);
builder.Services.AddTenantResolution();
builder.Services.AddWarrantyRateLimiting();

builder.Services
    .AddWarrantyInfrastructure(builder.Configuration)
    .AddSimulatedIntegrations()
    .AddWarrantyAiGateway(builder.Configuration)
    .AddWarrantyKnowledge()
    .AddWarrantyAiHarness()
    .AddWarrantyGuardrails()
    .AddWarrantyApplication();

var app = builder.Build();

app.UseCorrelationIdHeader();
app.UseExceptionHandler();
app.UseStatusCodePages();

// Authentication → tenant resolution → authorization (research R9).
app.UseAuthentication();
app.UseTenantResolution();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapDefaultEndpoints();
app.MapPublicEndpoints();
app.MapClaimEndpoints();
app.MapReviewEndpoints();
app.MapTraceEndpoints();
app.MapReferenceEndpoints();
app.MapAuditEndpoints();

app.Run();

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program;
