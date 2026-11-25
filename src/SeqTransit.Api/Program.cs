using Microsoft.EntityFrameworkCore;
using SeqTransit.Api;
using SeqTransit.Api.Endpoints;
using SeqTransit.Core.Reliability;
using SeqTransit.Data;

var builder = WebApplication.CreateBuilder(args);

// Database: DB_* environment variables in Lambda; appsettings.Development.json locally.
var databaseOptions = DatabaseOptions.FromEnvironment();
builder.Configuration.GetSection(DatabaseOptions.SectionName).Bind(databaseOptions);
var dataSource = TransitDataSource.Create(databaseOptions);

builder.Services.AddSingleton(dataSource);
builder.Services.AddDbContext<TransitDbContext>(options => options.UseNpgsql(dataSource));
builder.Services.AddScoped<ReliabilityQueries>();
builder.Services.AddSingleton(TimeProvider.System);
// OnTime__EarlyToleranceSeconds / OnTime__LateToleranceSeconds override the default window. The weekly
// summary function reads the same variables (OnTimePolicy.FromEnvironment), so both report one definition.
builder.Services.AddSingleton(builder.Configuration.GetSection("OnTime").Get<OnTimePolicy>() ?? OnTimePolicy.Default);

builder.Services.AddProblemDetails();
// CORS is only for the Vite dev server on another port. In AWS the page and the API share one
// CloudFront origin, so the deployed API sends no CORS headers and no other site may call it.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins(builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:5173")
        .AllowAnyHeader()
        .WithMethods("GET")));
}

// In AWS, API Gateway (HTTP API) invokes this app through Lambda instead of Kestrel.
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
builder.Services.AddOpenApi();

var app = builder.Build();

// First, so a request that skipped CloudFront never reaches the database.
app.UseOriginVerification(app.Configuration);
app.UseApiCacheHeaders();
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.UseCors();
}
// The OpenAPI document stays public on purpose: the API is public and read-only, and the README
// links to it. It lists the same endpoints the dashboard already calls.
app.MapOpenApi();
// CloudFront forwards /api/* to API Gateway; expose the document on that behaviour too.
app.MapOpenApi("/api/openapi/{documentName}.json");
app.MapReliabilityEndpoints();

app.Run();

/// <summary>Exposed so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
