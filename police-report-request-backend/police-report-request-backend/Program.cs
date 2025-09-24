// Program.cs - Logging, Auth (AAD + OBO), Graph via IDownstreamWebApi, CORS, Swagger, DI, and debug endpoints.
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;                // IClaimsTransformation
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Identity.Web;                             // IDownstreamWebApi + AddDownstreamWebApi
using police_report_request_backend.Data;
using police_report_request_backend.Auth;                 // transformer namespace

var builder = WebApplication.CreateBuilder(args);

// --------------------------- Logging ---------------------------
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// --------------------------- Services ---------------------------
builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor(); // ensure HttpContext is available to services

// --------------------------- AUTH + OBO + GRAPH (no Graph SDK) ---------------------------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(
        jwtOptions =>
        {
            builder.Configuration.Bind("AzureAdApi", jwtOptions);

            // REQUIRED for OBO: keep the inbound user token on HttpContext.User
            jwtOptions.TokenValidationParameters.SaveSigninToken = true;

            // Optional: detailed JWT event logging
            jwtOptions.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = ctx =>
                {
                    var log = ctx.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("JWT");
                    log.LogError(ctx.Exception, "JWT authentication failed: {Message}", ctx.Exception.Message);
                    return Task.CompletedTask;
                },
                OnTokenValidated = ctx =>
                {
                    var log = ctx.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("JWT");
                    var scp = ctx.Principal?.FindFirst("scp")?.Value ?? "(none)";
                    var name = ctx.Principal?.FindFirst(ClaimTypes.Name)?.Value
                               ?? ctx.Principal?.FindFirst("name")?.Value
                               ?? "(no name)";
                    log.LogInformation("JWT validated for {Name}. Scopes={Scopes}", name, scp);
                    return Task.CompletedTask;
                },
                OnChallenge = ctx =>
                {
                    var log = ctx.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("JWT");
                    log.LogWarning("JWT challenge. Error={Error}, Description={Description}", ctx.Error, ctx.ErrorDescription);
                    return Task.CompletedTask;
                }
            };
        },
        identityOptions =>
        {
            // Binds Instance/TenantId/ClientId from AzureAdApi section
            builder.Configuration.Bind("AzureAdApi", identityOptions);
        })
    // OBO plumbing: configure confidential client (must include ClientSecret for OBO)
    .EnableTokenAcquisitionToCallDownstreamApi(options =>
    {
        builder.Configuration.Bind("AzureAdApi", options);
    })
    // Generic downstream Web API client to call Microsoft Graph (no Graph SDK)
    .AddDownstreamWebApi("Graph", builder.Configuration.GetSection("Graph"))
    .AddInMemoryTokenCaches();

builder.Services.AddAuthorization();

// --------------------------- CORS / Swagger / DI ---------------------------
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

// DI so controllers can resolve repos
builder.Services.AddScoped<UsersRepository>();
builder.Services.AddScoped<SubmittedRequestFormRepository>();

// --------------------------- Claims transformation ---------------------------
// Register the Graph-backed claims transformer (defined in Auth/GraphBackedBadgeClaimsTransformation.cs)
builder.Services.AddTransient<IClaimsTransformation, GraphBackedBadgeClaimsTransformation>();

var app = builder.Build();

// --------------------------- Startup diagnostics ---------------------------
app.Logger.LogInformation("LOG: Environment = {Env}", app.Environment.EnvironmentName);

var aad = app.Configuration.GetSection("AzureAdApi");
app.Logger.LogInformation("LOG: AzureAdApi => TenantId={TenantId}, ClientId={ClientId}, Audience={Audience}",
    aad["TenantId"], aad["ClientId"], aad["Audience"]);

if (string.IsNullOrWhiteSpace(aad["ClientSecret"]))
{
    app.Logger.LogWarning("LOG: AzureAdApi:ClientSecret is MISSING. OBO to Graph will FAIL. Add a client secret or configure a certificate.");
}

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

// --------------------------- Exception handling ---------------------------
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

// --------------------------- Request logging middleware ---------------------------
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

// --------------------------- Middleware pipeline ---------------------------
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors();
app.UseAuthentication();

// Dev-only: allow overriding badge via header (becomes a real claim).
// Place AFTER UseAuthentication and BEFORE UseAuthorization.
if (app.Environment.IsDevelopment())
{
    app.Use(async (ctx, next) =>
    {
        var debugBadge = ctx.Request.Headers["x-badge-debug"].ToString();
        if (ctx.User?.Identity?.IsAuthenticated == true &&
            !string.IsNullOrWhiteSpace(debugBadge) &&
            ctx.User.FindFirst("badge") is null)
        {
            var id = ctx.User.Identity as ClaimsIdentity;
            id?.AddClaim(new Claim("badge", debugBadge));
        }
        await next();
    });
}

app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// --------------------------- Debug/Test endpoints ---------------------------
app.MapGet("/_meta/ping", () => Results.Ok(new { ok = true, env = app.Environment.EnvironmentName }));

app.MapGet("/_debug/di-usersrepo", (IServiceProvider sp) =>
{
    var repo = sp.GetService<UsersRepository>();
    return Results.Ok(new { registered = repo is not null, type = repo?.GetType().FullName });
});

app.MapGet("/_debug/di-formsrepo", (IServiceProvider sp) =>
{
    var repo = sp.GetService<SubmittedRequestFormRepository>();
    return Results.Ok(new { registered = repo is not null, type = repo?.GetType().FullName });
});

app.MapGet("/_debug/config", (IConfiguration cfg) =>
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
            Audience = cfg["AzureAdApi:Audience"],
            HasClientSecret = !string.IsNullOrWhiteSpace(cfg["AzureAdApi:ClientSecret"])
        },
        Graph = new
        {
            BaseUrl = cfg["Graph:BaseUrl"],
            Scopes = cfg["Graph:Scopes"]
        },
        HasConnectionString = !string.IsNullOrWhiteSpace(conn),
        Info = info
    });
});

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

// Claims-only view (no Graph) - used by your front-end "Debug: whoami" button
app.MapGet("/_debug/whoami-claims", (ClaimsPrincipal user) =>
{
    var fromToken = new
    {
        preferred_username = user.FindFirst("preferred_username")?.Value,
        email = user.FindFirst("email")?.Value,
        upn = user.FindFirst("upn")?.Value,
        unique_name = user.FindFirst("unique_name")?.Value,
        name = user.FindFirst("name")?.Value,
        oid = user.FindFirst("oid")?.Value,
        scp = user.FindFirst("scp")?.Value,
        badge = user.FindFirst("badge")?.Value
    };
    return Results.Ok(new { fromToken });
}).RequireAuthorization();

// Verify OBO to Graph works (no Graph SDK; raw downstream call)
app.MapGet("/_debug/whoami", async (IDownstreamWebApi downstream, ClaimsPrincipal user) =>
{
    var resp = await downstream.CallWebApiForUserAsync("Graph", opts =>
    {
        opts.RelativePath = "me?$select=displayName,mail,userPrincipalName,jobTitle,officeLocation";
    });

    resp.EnsureSuccessStatusCode();
    JsonDocument me = (await resp.Content.ReadFromJsonAsync<JsonDocument>()) ?? JsonDocument.Parse("{}");

    return Results.Ok(new
    {
        fromToken = new
        {
            preferred_username = user.FindFirst("preferred_username")?.Value,
            unique_name = user.FindFirst("unique_name")?.Value,
            upn = user.FindFirst("upn")?.Value,
            email = user.FindFirst("email")?.Value,
            name = user.FindFirst("name")?.Value,
            oid = user.FindFirst("oid")?.Value,
            badge = user.FindFirst("badge")?.Value
        },
        fromGraph = me.RootElement
    });
}).RequireAuthorization();

app.MapControllers();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var urls = string.Join(", ", app.Urls);
    app.Logger.LogInformation("LOG: Now listening on: {Urls}",
        string.IsNullOrWhiteSpace(urls) ? "(see Kestrel startup lines)" : urls);
});

app.Run();
