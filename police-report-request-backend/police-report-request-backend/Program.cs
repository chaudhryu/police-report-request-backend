// Program.cs - COMPLETE REWRITE (ASCII ONLY)
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Identity.Abstractions;              // IDownstreamApi
using Microsoft.Identity.Web;
using police_report_request_backend.Auth;          // IBadgeSessionService, BadgeSessionService, BadgeCookieClaimMiddleware, GraphBackedBadgeClaimsTransformation
using police_report_request_backend.Data;
using police_report_request_backend.Email;
using police_report_request_backend.Storage;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// --------------------------- Logging ---------------------------
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// --------------------------- Services ---------------------------
builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();

// DataProtection + badge cookie service (used by SessionController + middleware)
builder.Services.AddDataProtection();
builder.Services.AddSingleton<IBadgeSessionService, BadgeSessionService>();

// Email options + service
builder.Services.AddOptions<SmtpEmailOptions>()
    .Bind(builder.Configuration.GetSection("Email:Smtp"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Host), "Email:Smtp:Host is required")
    .Validate(o => !string.IsNullOrWhiteSpace(o.From), "Email:Smtp:From is required")
    .ValidateOnStart();

builder.Services.AddSingleton<IEmailNotificationService, SmtpEmailNotificationService>();

// --------------------------- STORAGE ---------------------------
builder.Services.Configure<BlobStorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.AddSingleton<IBlobUploadService, BlobUploadService>();
builder.Services.AddSingleton<IStorageSasService, StorageSasService>();

// --------------------------- AUTH + OBO + GRAPH ---------------------------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(
        jwtOptions =>
        {
            builder.Configuration.Bind("AzureAdApi", jwtOptions);

            // Keep inbound token so OBO can use it later
            jwtOptions.TokenValidationParameters.SaveSigninToken = true;

            // Helpful event logging + best-effort cookie->claim injection at validate-time
            jwtOptions.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = ctx =>
                {
                    var log = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("JWT");
                    log.LogError(ctx.Exception, "JWT authentication failed: {Message}", ctx.Exception.Message);
                    return Task.CompletedTask;
                },
                OnTokenValidated = ctx =>
                {
                    var factory = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>();
                    var log = factory.CreateLogger("JWT");
                    var id = ctx.Principal?.Identity as ClaimsIdentity;

                    var name = id?.FindFirst(ClaimTypes.Name)?.Value
                               ?? id?.FindFirst("name")?.Value
                               ?? "(no name)";
                    var scp = ctx.Principal?.FindFirst("scp")?.Value ?? "(none)";
                    log.LogInformation("JWT validated for {Name}. Scopes={Scopes}", name, scp);

                    // Best-effort 'badge' claim injection from cookie here (middleware does it again reliably)
                    try
                    {
                        if (id is not null && id.FindFirst("badge") is null)
                        {
                            var email = id.FindFirst("preferred_username")?.Value
                                        ?? id.FindFirst("email")?.Value
                                        ?? id.FindFirst("upn")?.Value;

                            if (!string.IsNullOrWhiteSpace(email))
                            {
                                var svc = ctx.HttpContext.RequestServices.GetRequiredService<IBadgeSessionService>();
                                var badge = svc.TryGetBadge(ctx.HttpContext, email);
                                if (!string.IsNullOrWhiteSpace(badge))
                                {
                                    id.AddClaim(new Claim("badge", badge));
                                    log.LogInformation("Badge injected from cookie at validate-time. email={Email}, badge={Badge}", email, badge);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        var injLog = factory.CreateLogger("BadgeCookie");
                        injLog.LogDebug(ex, "Validate-time badge injection failed (non-fatal).");
                    }

                    return Task.CompletedTask;
                },
                OnChallenge = ctx =>
                {
                    var log = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("JWT");
                    log.LogWarning("JWT challenge. Error={Error}, Description={Description}", ctx.Error, ctx.ErrorDescription);
                    return Task.CompletedTask;
                }
            };
        },
        identityOptions =>
        {
            builder.Configuration.Bind("AzureAdApi", identityOptions);
        })
    // OBO client
    .EnableTokenAcquisitionToCallDownstreamApi(options =>
    {
        builder.Configuration.Bind("AzureAdApi", options);
    })
    .AddDownstreamApi("Graph", builder.Configuration.GetSection("Graph"))
    .AddInMemoryTokenCaches();

builder.Services.AddAuthorization();

// --------------------------- CORS / Swagger / DI ---------------------------
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(
        "http://localhost:5173",
        "https://localhost:5173",
        "https://prrdev.metro.net",
        "https://prrpdev.metro.net",
        "https://prr.metro.net",
        "https://webappprodtest.metro.net",
        "https://police-report-request-portal-sigma.vercel.app")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials() // REQUIRED to roundtrip the HttpOnly cookie cross-site
    .WithExposedHeaders("X-Total-Count")
));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Repositories
builder.Services.AddScoped<UsersRepository>();
builder.Services.AddScoped<SubmittedRequestFormRepository>();

// Claims transformer (last-ditch; calls Graph if badge still missing)
builder.Services.AddTransient<IClaimsTransformation, GraphBackedBadgeClaimsTransformation>();

var app = builder.Build();

// --------------------------- Startup diagnostics ---------------------------
app.Logger.LogInformation("LOG: Environment = {Env}", app.Environment.EnvironmentName);

var aad = app.Configuration.GetSection("AzureAdApi");
app.Logger.LogInformation("LOG: AzureAdApi => TenantId={TenantId}, ClientId={ClientId}, Audience={Audience}",
    aad["TenantId"], aad["ClientId"], aad["Audience"]);

if (string.IsNullOrWhiteSpace(aad["ClientSecret"]))
{
    app.Logger.LogWarning("LOG: AzureAdApi:ClientSecret is MISSING. OBO to Graph will FAIL unless you use a cert.");
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

// Email + Storage diagnostics
var emailSec = app.Configuration.GetSection("Email:Smtp");
app.Logger.LogInformation("LOG: Email:Smtp => Host={Host}, Port={Port}, UseSsl={UseSsl}, From={From}, HasOpsTo={HasOpsTo}",
    emailSec["Host"], emailSec["Port"], emailSec["UseSsl"], emailSec["From"],
    string.IsNullOrWhiteSpace(emailSec["OpsTo"]) ? "false" : "true");

var st = app.Configuration.GetSection("Storage");
app.Logger.LogInformation("LOG: Storage => HasConn={HasConn}, ContainerUser={U}, ContainerOps={O}",
    string.IsNullOrWhiteSpace(st["ConnectionString"]) ? "false" : "true",
    st["ContainerUser"], st["ContainerOps"]);

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

// --------------------------- Pipeline ---------------------------
// If you want to force HTTPS locally, uncomment the next line:
// app.UseHttpsRedirection();

app.UseCors();
app.UseAuthentication();

// CRITICAL: Ensure every authenticated request gets a 'badge' claim from the cookie
app.UseMiddleware<BadgeCookieClaimMiddleware>();

// Dev-only: header override for testing (x-badge-debug: 12345)
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
        EmailSmtp = new
        {
            Host = cfg["Email:Smtp:Host"],
            Port = cfg["Email:Smtp:Port"],
            UseSsl = cfg["Email:Smtp:UseSsl"],
            From = cfg["Email:Smtp:From"],
            HasOpsTo = !string.IsNullOrWhiteSpace(cfg["Email:Smtp:OpsTo"]),
            HasUsername = !string.IsNullOrWhiteSpace(cfg["Email:Smtp:Username"])
        },
        Storage = new
        {
            HasConn = !string.IsNullOrWhiteSpace(cfg["Storage:ConnectionString"]),
            ContainerUser = cfg["Storage:ContainerUser"],
            ContainerOps = cfg["Storage:ContainerOps"]
        },
        HasConnectionString = !string.IsNullOrWhiteSpace(conn),
        Info = info
    });
});

// Quick cookie presence probe
app.MapGet("/_debug/has-badge-cookie", (HttpContext ctx) =>
{
    var hasCookie = ctx.Request.Cookies.ContainsKey(BadgeSessionService.CookieName);
    return Results.Ok(new { hasBadgeCookie = hasCookie });
});

// Claims snapshot (no Graph)
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

// Verify OBO to Graph works
app.MapGet("/_debug/whoami", async (IDownstreamApi downstream, ClaimsPrincipal user) =>
{
    var resp = await downstream.CallApiForUserAsync(
        "Graph",
        opts => { opts.RelativePath = "me?$select=displayName,mail,userPrincipalName,jobTitle,officeLocation"; },
        user: user
    );

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
