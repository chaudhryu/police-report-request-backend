// Program.cs — with rich console logging + debug endpoints
using System.Diagnostics;
using System.Security.Claims;
using Dapper;                      // if you register UsersRepository here
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;   // for connection string parsing + ping
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

// ------------- Logging providers -------------
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// ------------- Services -------------
builder.Services.AddControllers();

// Authentication: accept tokens issued for your exposed API (Audience)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAdApi"));

// Add detailed JWT event logs to surface auth problems early
builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = ctx =>
        {
            var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("JWT");
            logger.LogError(ctx.Exception, "JWT authentication failed. Error={Message}", ctx.Exception.Message);
            return Task.CompletedTask;
        },
        OnTokenValidated = ctx =>
        {
            var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("JWT");
            var scp = ctx.Principal?.FindFirst("scp")?.Value;
            var name = ctx.Principal?.FindFirst(ClaimTypes.Name)?.Value
                       ?? ctx.Principal?.FindFirst("name")?.Value
                       ?? "(no name)";
            logger.LogInformation("JWT validated for {Name}. Scopes={Scopes}", name, scp);
            return Task.CompletedTask;
        },
        OnChallenge = ctx =>
        {
            var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("JWT");
            logger.LogWarning("JWT challenge. Error={Error}, Description={Description}", ctx.Error, ctx.ErrorDescription);
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(
        "http://localhost:5173",
        "https://localhost:5173",           // if you also run SPA on https
        "https://prrdev.metro.net",
        "https://prr.metro.net",
        "https://webappprodtest.metro.net",
        "https://police-report-request-portal-sigma.vercel.app")
    .AllowAnyHeader()
    .AllowAnyMethod()
));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// TODO: register your repository if you haven't
// builder.Services.AddSingleton<UsersRepository>();

var app = builder.Build();

// ----------- Startup diagnostics (LOG current config) -----------
{
    var log = app.Logger;
    log.LogInformation("LOG: Environment = {Env}", app.Environment.EnvironmentName);

    // AzureAdApi config echo (safe fields only)
    var aadSection = app.Configuration.GetSection("AzureAdApi");
    var tenantId = aadSection["TenantId"];
    var clientId = aadSection["ClientId"];
    var audience = aadSection["Audience"];
    log.LogInformation("LOG: AzureAdApi => TenantId={TenantId}, ClientId={ClientId}, Audience={Audience}",
        tenantId, clientId, audience);

    // Connection string presence + parsed bits
    var cs = app.Configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(cs))
    {
        log.LogError("LOG: ConnectionStrings:DefaultConnection is MISSING or empty.");
    }
    else
    {
        try
        {
            var b = new SqlConnectionStringBuilder(cs);
            log.LogInformation("LOG: DB => DataSource={DataSource}, Database={Database}, IntegratedSecurity={IntegratedSecurity}",
                b.DataSource, b.InitialCatalog, b.IntegratedSecurity);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "LOG: Failed to parse DefaultConnection.");
        }
    }
}

// ----------- Exception handling (shows real 500 reasons) -----------
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage(); // detailed stack traces in console/browser
}
else
{
    app.UseExceptionHandler(errApp =>
    {
        errApp.Run(async context =>
        {
            var exceptionHandlerPathFeature = context.Features.Get<IExceptionHandlerPathFeature>();
            var ex = exceptionHandlerPathFeature?.Error;
            app.Logger.LogError(ex, "Unhandled exception at {Path}", exceptionHandlerPathFeature?.Path);
            await Results.Problem(
                title: "Server error",
                detail: ex?.Message,
                statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
        });
    });
}

// ----------- Request/response logging middleware -----------
app.Use(async (ctx, next) =>
{
    var sw = Stopwatch.StartNew();
    var origin = ctx.Request.Headers.Origin.ToString();
    app.Logger.LogInformation("LOG: --> {Method} {Path} Origin={Origin}", ctx.Request.Method, ctx.Request.Path, string.IsNullOrEmpty(origin) ? "(none)" : origin);
    try
    {
        await next();
        app.Logger.LogInformation("LOG: <-- {Status} {Method} {Path} in {Elapsed} ms",
            ctx.Response.StatusCode, ctx.Request.Method, ctx.Request.Path, sw.ElapsedMilliseconds);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "LOG: EXCEPTION {Method} {Path} after {Elapsed} ms",
            ctx.Request.Method, ctx.Request.Path, sw.ElapsedMilliseconds);
        throw; // rethrow so exception handler / dev page shows it
    }
});

// ----------- HTTPS redirection -----------
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
else
{
    app.Logger.LogWarning("LOG: Development mode - HTTPS redirection is DISABLED. Current URLs will be logged after start.");
}

// ----------- CORS / Auth -----------
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// ----------- Swagger (dev) -----------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ----------- Dev-only debug endpoints -----------
if (app.Environment.IsDevelopment())
{
    app.MapGet("/debug/config", (IConfiguration cfg) =>
    {
        var conn = cfg.GetConnectionString("DefaultConnection");
        object? info = null;
        if (!string.IsNullOrWhiteSpace(conn))
        {
            try
            {
                var b = new SqlConnectionStringBuilder(conn);
                info = new { b.DataSource, Database = b.InitialCatalog, b.IntegratedSecurity };
            }
            catch (Exception ex)
            {
                info = new { ParseError = ex.Message };
            }
        }
        return Results.Ok(new
        {
            AzureAdApi = new
            {
                TenantId = cfg["AzureAdApi:TenantId"],
                ClientId = cfg["AzureAdApi:ClientId"],
                Audience = cfg["AzureAdApi:Audience"]
            },
            HasConnectionString = !string.IsNullOrWhiteSpace(conn),
            Info = info
        });
    });

    app.MapGet("/debug/ping-db", async (IConfiguration cfg) =>
    {
        try
        {
            await using var conn = new SqlConnection(cfg.GetConnectionString("DefaultConnection"));
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("SELECT 1", conn);
            var one = await cmd.ExecuteScalarAsync();
            return Results.Ok(new { Connected = true, Result = one });
        }
        catch (Exception ex)
        {
            return Results.Problem("DB connect failed: " + ex.Message);
        }
    });
}

// ----------- Controllers -----------
app.MapControllers();

// ----------- Log bound URLs after server starts -----------
app.Lifetime.ApplicationStarted.Register(() =>
{
    var urls = string.Join(", ", app.Urls);
    app.Logger.LogInformation("LOG: Now listening on: {Urls}", string.IsNullOrWhiteSpace(urls) ? "(urls not set here; see Kestrel startup lines)" : urls);
});

app.Run();
