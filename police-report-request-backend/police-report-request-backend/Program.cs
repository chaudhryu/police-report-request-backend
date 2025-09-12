// Program.cs - DI + rich console logging + always-on debug endpoints
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Identity.Web;
using police_report_request_backend.Data;

var builder = WebApplication.CreateBuilder(args);

// Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Services
builder.Services.AddControllers();

// Authentication: accept tokens for your exposed API (Audience)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAdApi"));

// JWT event logs
builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = ctx =>
        {
            var log = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("JWT");
            log.LogError(ctx.Exception, "JWT authentication failed: {Message}", ctx.Exception.Message);
            return Task.CompletedTask;
        },
        OnTokenValidated = ctx =>
        {
            var log = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("JWT");
            var scp = ctx.Principal?.FindFirst("scp")?.Value ?? "(none)";
            var name = ctx.Principal?.FindFirst(ClaimTypes.Name)?.Value
                       ?? ctx.Principal?.FindFirst("name")?.Value
                       ?? "(no name)";
            log.LogInformation("JWT validated for {Name}. Scopes={Scopes}", name, scp);
            return Task.CompletedTask;
        },
        OnChallenge = ctx =>
        {
            var log = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("JWT");
            log.LogWarning("JWT challenge. Error={Error}, Description={Description}", ctx.Error, ctx.ErrorDescription);
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(
        "http://localhost:5173",
        "https://localhost:5173",
        "https://prrdev.metro.net",
        "https://prr.metro.net",
        "https://webappprodtest.metro.net",
        "https://police-report-request-portal-sigma.vercel.app")
    .AllowAnyHeader()
    .AllowAnyMethod()
));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// DI registration so UsersController can resolve it
builder.Services.AddScoped<UsersRepository>();

var app = builder.Build();

// Startup diagnostics
app.Logger.LogInformation("LOG: Environment = {Env}", app.Environment.EnvironmentName);

var aad = app.Configuration.GetSection("AzureAdApi");
app.Logger.LogInformation("LOG: AzureAdApi => TenantId={TenantId}, ClientId={ClientId}, Audience={Audience}",
    aad["TenantId"], aad["ClientId"], aad["Audience"]);

var cs = app.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(cs))
{
    app.Logger.LogError("LOG: ConnectionStrings:DefaultConnection is MISSING or empty.");
}
else
{
    try
    {
        var b = new SqlConnectionStringBuilder(cs);
        app.Logger.LogInformation("LOG: DB => DataSource={DataSource}, Database={Database}, IntegratedSecurity={IntegratedSecurity}",
            b.DataSource, b.InitialCatalog, b.IntegratedSecurity);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "LOG: Failed to parse DefaultConnection.");
    }
}

// Exception handling
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler(errApp =>
    {
        errApp.Run(async context =>
        {
            var feat = context.Features.Get<IExceptionHandlerPathFeature>();
            var ex = feat?.Error;
            app.Logger.LogError(ex, "Unhandled exception at {Path}", feat?.Path);
            await Results.Problem(
                title: "Server error",
                detail: ex?.Message,
                statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
        });
    });
}

// Request logging
app.Use(async (ctx, next) =>
{
    var sw = Stopwatch.StartNew();
    var origin = ctx.Request.Headers.Origin.ToString();
    app.Logger.LogInformation("LOG: --> {Method} {Path} Origin={Origin}",
        ctx.Request.Method, ctx.Request.Path, string.IsNullOrEmpty(origin) ? "(none)" : origin);
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
        throw;
    }
});

// HTTPS redirection only outside Development (keeps HTTP for local SPA)
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Always-on debug/test endpoints
app.MapGet("/_meta/ping", () => Results.Ok(new { ok = true, env = app.Environment.EnvironmentName }));

// DI sanity: shows whether UsersRepository is registered
app.MapGet("/_debug/di-usersrepo", (IServiceProvider sp) =>
{
    var repo = sp.GetService<UsersRepository>();
    return Results.Ok(new
    {
        registered = repo is not null,
        type = repo?.GetType().FullName
    });
});

// Config echo (safe)
app.MapGet("/_debug/config", (IConfiguration cfg) =>
{
    var conn = cfg.GetConnectionString("DefaultConnection");
    object info = null;
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
        AzureAdApi = new { TenantId = cfg["AzureAdApi:TenantId"], ClientId = cfg["AzureAdApi:ClientId"], Audience = cfg["AzureAdApi:Audience"] },
        HasConnectionString = !string.IsNullOrWhiteSpace(conn),
        Info = info
    });
});

// DB ping
app.MapGet("/_debug/ping-db", async (IConfiguration cfg) =>
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

app.MapControllers();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var urls = string.Join(", ", app.Urls);
    app.Logger.LogInformation("LOG: Now listening on: {Urls}",
        string.IsNullOrWhiteSpace(urls) ? "(see Kestrel startup lines)" : urls);
});

app.Run();
