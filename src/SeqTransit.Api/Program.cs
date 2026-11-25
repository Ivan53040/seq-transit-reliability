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
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:5173")
    .AllowAnyHeader()
    .WithMethods("GET")));

// In AWS, API Gateway (HTTP API) invokes this app through Lambda instead of Kestrel.
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
builder.Services.AddOpenApi();

var app = builder.Build();

// First, so a request that skipped CloudFront never reaches the database.
app.UseOriginVerification(app.Configuration);
app.UseApiCacheHeaders();
app.UseExceptionHandler();
app.UseCors();
app.MapOpenApi();
// CloudFront forwards /api/* to API Gateway; expose the document on that behaviour too.
app.MapOpenApi("/api/openapi/{documentName}.json");
app.MapReliabilityEndpoints();

app.Run();

/// <summary>Exposed so integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
