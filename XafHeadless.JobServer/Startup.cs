using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ApplicationBuilder;
using DevExpress.ExpressApp.MultiTenancy;
using DevExpress.ExpressApp.MultiTenancy.EFCore;
using DevExpress.ExpressApp.Security;
using DevExpress.ExpressApp.WebApi.Services;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OutlookInspiredDemo.Module;
using OutlookInspiredDemo.Module.BusinessObjects;
using System.Text;
using XafHeadless.JobServer.BusinessObjects;
using XafHeadless.JobServer.Jobs;
using XafHeadless.JobServer.Services.Email;

namespace XafHeadless.JobServer;

// UI-less XAF host dedicated to running background jobs via Hangfire (26.1 / net10.0; the exact
// DevExpress patch is pinned in the .csproj).
//
// Mirrors XafHeadless.Api\Startup.cs's proven AddXafWebApi wiring MINUS the data surface (no
// ConfigureOptions(BusinessObject<>()), no OData, no command controllers — clients poll the Api's
// OData for JobExecutionRecord), PLUS Hangfire (storage + worker) and an anonymous /health endpoint.
//
// ===================== TENANCY MODE: MULTI-TENANT (SVR-001, Task 1.2 spike) =====================
// The demo's OutlookInspiredModule is intrinsically multi-tenant: its DatabaseUpdate.Updater
// (UpdateDatabaseAfterUpdateSchema) resolves ITenantProvider (Updater.cs:353) and throws at
// startup CheckCompatibility if AddMultiTenancy did not register it. The plan's working hypothesis
// (two plain ObjectSpaceProviders chains, no AddMultiTenancy) is therefore not viable — verified
// against installed 26.1 source, not memory (see docs/DEVIATIONS.md, SVR-001-A). This host takes
// the plan's fallback: the Api's full multi-tenancy wiring, copied.
//   - Host database  = this host's own catalog "XafHeadlessDemo" (ConnectionStrings:ConnectionString);
//     created/updated/seeded by XAF at first run. The four new SVR-001 BOs ride the SAME shared-BO
//     path as UserLayoutPref (.WithSharedBusinessObjects) into this catalog.
//   - Tenant database = "OutlookInspiredDemo_company1" (hardcoded by the demo's Updater.CreateTenant),
//     resolved PER-REQUEST via IConnectionStringProvider — never touched at boot (lazy per-logon), so
//     the boot-test path only creates the host catalog + the Hangfire schema.
// A worker host has no HTTP request to resolve a tenant from, so the eventual background-job path to
// the tenant DB (report render, Phase 3) is a later-dispatch concern. TenantByEmailResolver is kept
// verbatim (a DevExpress built-in) purely to satisfy the multi-tenancy builder; it is never invoked
// on the boot path.
public class Startup {
    public Startup(IConfiguration configuration, IWebHostEnvironment environment) {
        Configuration = configuration;
        Environment = environment;
    }
    public IConfiguration Configuration { get; }
    public IWebHostEnvironment Environment { get; }

    public void ConfigureServices(IServiceCollection services) {
        // Host-owned shared BOs -> host catalog XafHeadlessDemo (same routing UserLayoutPref documents).
        // TaxRate is required here because the demo's Updater host branch (CreateTaxRates) queries it;
        // omitting it would break that idempotent seeder. UserLayoutPref/LookupProbe are Api-only and
        // deliberately not replicated (this host never touches them).
        Type[] sharedBusinessObjects = [
            typeof(TaxRate),
            typeof(JobDefinition), typeof(JobExecutionRecord),
            typeof(ReportArtifact), typeof(EmailArchive)
        ];
        services.AddHttpContextAccessor();

        services.AddXafWebApi(builder => {
            builder.Modules
                // ReportsModuleV2.Setup (pulled in by OutlookInspiredModule) hard-resolves report
                // services, so AddReports is mandatory; the demo types carry [RuleRequiredField], so
                // AddValidation is registered — same as the Api.
                .AddReports(options => {
                    options.EnableInplaceReports = false;
                    options.ReportDataType = typeof(ReportDataV2);
                    options.ReportStoreMode = DevExpress.ExpressApp.ReportsV2.ReportStoreModes.XML;
                })
                .AddValidation()
                .Add<OutlookInspiredModule>();
            builder.ObjectSpaceProviders
                .AddSecuredEFCore(options => options.PreFetchReferenceProperties())
                .WithDbContext<OutlookInspiredEFCoreDbContext>((sp, options) => {
                    // Multi-tenant: the tenant's connection string is resolved per-request, not static.
                    var cs = sp.GetRequiredService<IConnectionStringProvider>().GetConnectionString();
                    options.UseSqlServer(cs, o => o.UseCompatibilityLevel(120));
                    // The demo DbContext uses ChangingAndChangedNotificationsWithOriginalValues, which
                    // requires INotifyPropertyChanged on every entity — supplied by change-tracking proxies.
                    options.UseChangeTrackingProxies();
                    options.UseObjectSpaceLinkProxies();
                    options.UseLazyLoadingProxies();
                })
                .AddNonPersistent();
            builder.Security
                .UseIntegratedMode(options => {
                    options.RoleType = typeof(PermissionPolicyRole);
                    options.UserType = typeof(ApplicationUser);
                    options.UserLoginInfoType = typeof(ApplicationUserLoginInfo);
                    options.Events.OnSecurityStrategyCreated += ss =>
                        ((SecurityStrategy)ss).PermissionsReloadMode = PermissionsReloadMode.CacheOnFirstAccess;
                })
                .AddPasswordAuthentication(options => options.IsSupportChangePassword = true);
            builder.AddMultiTenancy()
                .WithHostDbContext((sp, options) => {
                    var hostCs = Configuration.GetConnectionString("ConnectionString");
                    ArgumentNullException.ThrowIfNull(hostCs);
                    options.UseSqlServer(hostCs, o => o.UseCompatibilityLevel(120));
                })
                .WithMultiTenancyModelDifferenceStore(e => e.UseTenantSpecificModel = false)
                .WithSharedBusinessObjects(sharedBusinessObjects)
                .WithTenantDatabaseUpdater()
                .WithTenantResolver<TenantByEmailResolver>();
            builder.AddBuildStep(application => {
                application.ApplicationName = "OutlookInspiredDemo";
                // This host shares the disposable host catalog with the Api: let XAF create/update it.
                // The demo's host-branch seeder is idempotent (CreateTenant/CreateTaxRates/EnsureUser
                // are all find-or-create), so re-running it when this host adds its 4 tables is a no-op.
                application.CheckCompatibilityType = DevExpress.ExpressApp.CheckCompatibilityType.DatabaseSchema;
                application.DatabaseVersionMismatch += (_, e) => { e.Updater.Update(); e.Handled = true; };
            });
        }, Configuration);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => {
            options.TokenValidationParameters = new TokenValidationParameters {
                ValidateIssuerSigningKey = true, ValidateIssuer = false, ValidateAudience = false,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(Configuration["Authentication:Jwt:IssuerSigningKey"]!)),
                AuthenticationType = JwtBearerDefaults.AuthenticationScheme
            };
        });
        services.AddAuthorization(options => options.DefaultPolicy =
            new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireXafAuthentication().Build());

        // SVR-001: provision the host catalog (the 4 shared-BO tables) at startup, before the Hangfire
        // worker starts — this host serves no request that would otherwise trigger the host schema update.
        services.AddHostedService<HostDatabaseInitializer>();

        // Hangfire: SqlServer storage in the host catalog's own "Hangfire" schema + the worker.
        // PrepareSchemaIfNecessary auto-creates the Hangfire tables on first storage access.
        services.AddHangfire(cfg => cfg
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
            .UseSqlServerStorage(Configuration.GetConnectionString("ConnectionString"),
                new SqlServerStorageOptions {
                    SchemaName = "Hangfire",
                    PrepareSchemaIfNecessary = true,
                    QueuePollInterval = TimeSpan.FromSeconds(5)
                }));
        services.AddHangfireServer();

        // SVR-001 Task 3.1-3.3: job dispatch infra + the one EmailOrdersReport handler.
        services.AddTransient<IJobDispatcher, HangfireJobDispatcher>();
        services.AddTransient<IJobHandler<EmailOrdersReportCommand>, EmailOrdersReportHandler>();
        services.AddTransient<JobExecutor<EmailOrdersReportCommand>>();
        services.AddScoped<IJobExecutionRecorder, XafJobExecutionRecorder>();
        services.AddScoped<IJobScopeInitializer, NoOpJobScopeInitializer>();
        services.AddTransient<IJobProgressReporter, NullJobProgressReporter>();
        services.AddScoped<JobDispatchService>();
        services.AddScoped<Reports.ReportRenderService>();

        // SVR-001 Task 5.1: reconciles JobDefinition rows into Hangfire recurring jobs on startup and
        // every Jobs:ScheduleSyncSeconds. Registered after HostDatabaseInitializer (host schema/seed)
        // and AddHangfireServer above, so hosted-service start order guarantees both are ready before
        // the first tick (the per-tick try/catch self-heals a transient early miss anyway).
        services.AddHostedService<ScheduleSyncService>();

        // SVR-001 Task 4.1: MailKit-backed email delivery for EmailOrdersReportHandler.
        services.Configure<EmailSettings>(Configuration.GetSection("EmailSettings"));
        services.AddScoped<IEmailService, EmailService>();

        services.AddControllers();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env) {
        if (env.IsDevelopment()) app.UseDeveloperExceptionPage();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints => {
            endpoints.MapGet("/health", () => Results.Ok("ok")); // anonymous — not subject to DefaultPolicy
            endpoints.MapControllers();
        });
    }
}
