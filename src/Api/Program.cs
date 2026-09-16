using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using SaasMultiTenant.Api.Endpoints;
using SaasMultiTenant.Api.Extensions;
using SaasMultiTenant.Infrastructure;
using SaasMultiTenant.Infrastructure.Persistence;
using Serilog;
using Serilog.Context;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext());

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddTenantAuthentication(builder.Configuration);

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(swagger =>
{
    swagger.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SaaS Multi Tenant API",
        Version = "v1",
        Description =
            "A multi tenant SaaS API where tenant isolation is enforced by the persistence layer rather than "
            + "by each query. Sign in through /api/auth/login, then send the returned token as a bearer token. "
            + "The tenant is read from that token and from nowhere else.",
    });

    swagger.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the access token returned by /api/auth/login.",
    });

    swagger.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
            },
            Array.Empty<string>()
        },
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);

    if (File.Exists(xmlPath))
    {
        swagger.IncludeXmlComments(xmlPath);
    }
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging();

// Every log line written during a request carries the tenant, which is what
// makes an audit trail usable: "what did tenant X do" is a filter rather than
// a correlation exercise.
app.Use(async (context, next) =>
{
    var tenantId = context.User.FindFirst("tenant_id")?.Value;

    if (tenantId is not null)
    {
        using (LogContext.PushProperty("TenantId", tenantId))
        {
            await next(context);

            return;
        }
    }

    await next(context);
});

app.UseAuthentication();
app.UseAuthorization();

if (!app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI(ui =>
    {
        ui.SwaggerEndpoint("/swagger/v1/swagger.json", "SaaS Multi Tenant API v1");
        ui.DocumentTitle = "SaaS Multi Tenant API";
    });
}

app.MapAuthEndpoints();
app.MapTenantEndpoints();
app.MapCustomerEndpoints();
app.MapProductEndpoints();
app.MapOrderEndpoints();

// Liveness must not touch the database. A process that is running but cannot
// reach SQL Server should not be killed and restarted, because restarting it
// does not bring the database back.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

await ApplyMigrationsAsync(app);

app.Run();

static async Task ApplyMigrationsAsync(WebApplication app)
{
    if (!app.Configuration.GetValue("Database:MigrateOnStartup", false))
    {
        return;
    }

    using var scope = app.Services.CreateScope();

    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    await context.Database.MigrateAsync();

    app.Logger.LogInformation("Database migrations applied.");
}

/// <summary>
/// Exposed so the integration tests can host the real pipeline through
/// WebApplicationFactory, rather than testing a separately wired copy of it.
/// </summary>
public partial class Program;
