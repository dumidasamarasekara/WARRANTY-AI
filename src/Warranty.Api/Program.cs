using System.Text.Json.Serialization;
using Warranty.AI.Gateway;
using Warranty.AI.Harness;
using Warranty.Api.Auth;
using Warranty.Api.Endpoints;
using Warranty.Api.Http;
using Warranty.Api.RateLimiting;
using Warranty.Api.Security;
using Warranty.Api.Tenancy;
using Warranty.Api.Workers;
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
builder.Services.AddWarrantyCors();

builder.Services
    .AddWarrantyInfrastructure(builder.Configuration)
    .AddSimulatedIntegrations()
    .AddWarrantyAiGateway(builder.Configuration)
    .AddWarrantyKnowledge()
    .AddWarrantyAiHarness()
    .AddWarrantyGuardrails()
    .AddWarrantyApplication();

builder.Services.AddClaimJobWorker(builder.Configuration);

var app = builder.Build();

app.UseSecurityHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseCorrelationIdHeader();
// A malformed or unreadable request body is the client's error (400), also where bad requests throw (Development).
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();

// CORS before authentication so preflights are answered without a token.
app.UseCors(SecurityHeaders.CorsPolicyName);

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
