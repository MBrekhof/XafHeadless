using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ApplicationBuilder;
using DevExpress.ExpressApp.MultiTenancy;
using DevExpress.ExpressApp.MultiTenancy.EFCore;
using DevExpress.ExpressApp.Security;
using DevExpress.ExpressApp.Security.Authentication.ClientServer;
using DevExpress.ExpressApp.WebApi.Services;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OData;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OutlookInspiredDemo.Module;
using OutlookInspiredDemo.Module.BusinessObjects;
using System.Text;
using XafHeadless.Api.Auth;
using XafHeadless.Api.BusinessObjects;
using XafHeadless.Api.Middleware;
using XafHeadless.JobServer.BusinessObjects;

namespace XafHeadless.Api;

// UI-less XAF Web API host over DevExpress's OutlookInspired demo module (26.1 / net10.0; the exact
// DevExpress patch is pinned in the .csproj).
//
// ===================== TENANCY MODE: MULTI-TENANT (spike decision) =====================
// The demo module is intrinsically multi-tenant: its DatabaseUpdate.Updater
// (UpdateDatabaseAfterUpdateSchema) resolves ITenantProvider and only seeds real demo data +
// per-tenant users in the TENANT branch. A genuine SINGLE-tenant attempt (no AddMultiTenancy) was
// made first per the spike order; it crashes at startup warm-up (CheckCompatibility -> Updater):
//   "No service for type 'DevExpress.ExpressApp.MultiTenancy.ITenantProvider' has been registered."
// (Updater.cs:353). So the module cannot be hosted single-tenant. This host therefore mirrors the
// demo's Blazor.Server multi-tenancy configuration, adapted to the standalone Web API builder per the
// dxdocs "Convert an Existing Application into a Multi-Tenant Application" -> Web API Service tab
// (AddMultiTenancy on IWebApiApplicationBuilder / DevExpress.ExpressApp.MultiTenancy.WebApi.EFCore).
//   - Host database  = this host's own catalog "XafHeadlessDemo" (ConnectionStrings:ConnectionString);
//     created/seeded by XAF at first run (host branch: Admin + tenant list + TaxRate).
//   - Tenant database = "OutlookInspiredDemo_company1" (hardcoded by the demo's Updater.CreateTenant);
//     seeded by the demo itself (55k orders / 51 employees / users incl. Admin@company1.com).
//     Logon as Admin@company1.com -> TenantByEmailResolver -> tenant "company1.com" -> that DB.
// The shared application model (used by the diagnostics/metadata endpoints) runs on an in-memory DB
// (MultiTenancyOptions.UseInMemoryDatabaseForSharedApplication = true) and is tenant-independent.
public class Startup {
    public Startup(IConfiguration configuration, IWebHostEnvironment environment) {
        Configuration = configuration;
        Environment = environment;
    }
    public IConfiguration Configuration { get; }
    public IWebHostEnvironment Environment { get; }

    public void ConfigureServices(IServiceCollection services) {
        // DATA-001: LookupProbe is a Development-only projection-test fixture (see its class comment) --
        // dev-gated here so it does NOT ship as a shared BO / model view in a production deployment. The
        // Api.Tests host runs Development (launchSettings ASPNETCORE_ENVIRONMENT=Development), so the probe
        // and its LookupProbe_DetailView are present there and absent in Production.
        // SVR-001 Task 2.2: JobDefinition/JobExecutionRecord are host-owned shared BOs defined in
        // XafHeadless.JobServer; they ride the same .WithSharedBusinessObjects path as UserLayoutPref.
        // RPT-001 adds ReportArtifact: the API serves a rendered report back to the user who asked for it,
        // which is a cheap read of stored bytes, not a render -- MIG-002's "heavy rendering off the request
        // path" boundary is about producing the PDF, and that stays in the JobServer. Reading it here keeps
        // the client talking to ONE host with ONE token instead of giving the worker an HTTP surface.
        // It remains deliberately NOT OData-exposed (see the BusinessObject<T> list below).
        Type[] sharedBusinessObjects = Environment.IsDevelopment()
            ? [typeof(TaxRate), typeof(UserLayoutPref), typeof(LookupProbe), typeof(JobDefinition), typeof(JobExecutionRecord), typeof(ReportArtifact)]
            : [typeof(TaxRate), typeof(UserLayoutPref), typeof(JobDefinition), typeof(JobExecutionRecord), typeof(ReportArtifact)];
        services.AddHttpContextAccessor();
        // The Blazor Web App (localhost:5220) is the browser consumer of this host.
        services.AddCors(o => o.AddDefaultPolicy(p => p
            .WithOrigins("http://localhost:5220").AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Allow")));
        services.AddScoped<IAuthenticationTokenProvider, JwtTokenProviderService>();
        services.AddScoped<Metadata.ViewMetadataProjector>();
        services.AddScoped<Metadata.NavigationProjector>();
        // NPO-001 (option B): the host declares which non-persistent [DomainComponent] types it can serve
        // and how each is populated. Singleton because it is immutable configuration -- the populators run
        // per-request against a per-request ObjectSpace, not against anything held here.
        // AddOutlookInspiredDemo is DEMO scaffolding; a real app registers its own (see the populator file).
        services.AddSingleton(NonPersistent.OutlookInspiredPopulators
            .AddOutlookInspiredDemo(new NonPersistent.NonPersistentRegistry()));
        services.AddScoped<Commands.IHeadlessCommand, Commands.OrderSummaryCommand>();
        services.AddScoped<Commands.IHeadlessCommand, Commands.EmailOrdersReportApiCommand>();

        // SVR-001 Task 3.4: Hangfire CLIENT only -- this host enqueues into the shared SQL storage,
        // XafHeadless.JobServer is the sole worker (no AddHangfireServer/dashboard here). The
        // serializer settings must match the JobServer's AddHangfire call line-for-line: Hangfire
        // stores each job as a serialized {type, method, args}, and the worker resolves that type in
        // a SEPARATE process/GlobalConfiguration -- a mismatched serializer or compat level risks
        // "Could not load type" at pick-up. Verified empirically at the Task 3.4 phase gate.
        services.AddHangfire(cfg => cfg
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
            .UseSqlServerStorage(Configuration.GetConnectionString("ConnectionString"),
                new SqlServerStorageOptions { SchemaName = "Hangfire" }));

        services.AddXafWebApi(builder => {
            builder.ConfigureOptions(options => {
                // Order_DetailView surface: the type, its nested collection, and its lookup targets.
                options.BusinessObject<Order>();
                options.BusinessObject<OrderItem>();
                options.BusinessObject<Customer>();
                options.BusinessObject<CustomerStore>();
                options.BusinessObject<Product>();
                // Employee_DetailView surface + Order.Employee lookup.
                options.BusinessObject<Employee>();
                options.BusinessObject<Evaluation>();
                options.BusinessObject<EmployeeTask>();
                options.BusinessObject<Probation>();
                options.BusinessObject<Picture>();
                // SVR-001 Task 2.2: JobDefinition/JobExecutionRecord OData surface for the client's
                // job-management UI. ReportArtifact (PDF bytes) and EmailArchive (audit-only) are
                // deliberately NOT exposed here -- same "not via OData" precedent as UserLayoutPref.
                options.BusinessObject<JobDefinition>();
                options.BusinessObject<JobExecutionRecord>();
            });
            builder.Modules
                // Only the module-service extensions that (a) apply to the Web API builder and (b) a
                // headless host actually needs. ReportsModuleV2.Setup hard-resolves report services, so
                // AddReports is mandatory; the demo type carries [RuleRequiredField] rules, so
                // AddValidation is registered. The remaining UI modules the demo pulls in
                // (ConditionalAppearance/Office/Scheduler/ViewVariants/FileAttachments) are auto-created
                // via OutlookInspiredModule.RequiredModuleTypes and need no service registration in a
                // UI-less host — their Add* extensions are Blazor/Win-only (IApplicationBuilder<T>) and
                // do not apply to IWebApiApplicationBuilder.
                .AddReports(options => {
                    options.EnableInplaceReports = false;
                    options.ReportDataType = typeof(ReportDataV2);
                    options.ReportStoreMode = DevExpress.ExpressApp.ReportsV2.ReportStoreModes.XML;
                })
                .AddValidation()
                // Headless host: the demo's OutlookInspiredBlazorModule (UI) is intentionally omitted.
                .Add<OutlookInspiredModule>();
            builder.ObjectSpaceProviders
                .AddSecuredEFCore(options => options.PreFetchReferenceProperties())
                .WithDbContext<OutlookInspiredEFCoreDbContext>((sp, options) => {
                    // Multi-tenant: the tenant's connection string is resolved per-request, not static.
                    var cs = sp.GetRequiredService<IConnectionStringProvider>().GetConnectionString();
                    options.UseSqlServer(cs, o => o.UseCompatibilityLevel(120));
                    // The demo DbContext declares the ChangingAndChangedNotificationsWithOriginalValues
                    // change-tracking strategy, which requires INotifyPropertyChanged on every entity —
                    // supplied at runtime by EF change-tracking proxies. Without these the model fails
                    // validation (e.g. 'DashboardData' has no INotifyPropertyChanged).
                    options.UseChangeTrackingProxies();
                    options.UseObjectSpaceLinkProxies();
                    options.UseLazyLoadingProxies();
                })
                .AddNonPersistent();
            // NPO-001: the documented seam for serving non-persistent types from a headless host
            // (dxdocs 403164, which names MySolution.WebApi/Startup.cs for exactly this shape).
            //
            // Two things happen per created ObjectSpace, and both are required:
            //  1. PopulateAdditionalObjectSpaces -- without it a NonPersistentObjectSpace cannot reach
            //     persistent data, so a populator's GetObjectsQuery<Quote>() has nothing to query. The
            //     Owner guard is DevExpress's own: a nested composite space inherits its owner's.
            //  2. Attach -- subscribes ObjectsGetting/ObjectByKeyGetting to the registry. This is the
            //     whole of option B: the app's ListView controllers never activate here (no Frame, no
            //     View), so nothing else would ever populate these types.
            builder.ObjectSpaceProviders.Events.OnObjectSpaceCreated = context => {
                if (context.ObjectSpace is CompositeObjectSpace composite && composite.Owner is not CompositeObjectSpace) {
                    composite.PopulateAdditionalObjectSpaces(
                        context.ServiceProvider.GetRequiredService<DevExpress.ExpressApp.Core.IObjectSpaceProviderService>(),
                        context.ServiceProvider.GetRequiredService<DevExpress.ExpressApp.Core.IObjectSpaceCustomizerService>());
                }
                if (context.ObjectSpace is NonPersistentObjectSpace nonPersistent) {
                    context.ServiceProvider.GetRequiredService<NonPersistent.NonPersistentRegistry>()
                        .Attach(nonPersistent);
                }
            };
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
                // GAP-008: UserLayoutPref rides the SAME shared-BO path as the demo's TaxRate, so its
                // table is created in the host catalog (XafHeadlessDemo) by the host schema auto-update
                // and IObjectSpaceFactory.CreateObjectSpace(typeof(UserLayoutPref)) routes to the host DB
                // (ObjectSpaceFactoryBase: shared type + tenant set -> CreateObjectSpace(type, null)).
                // DATA-001: sharedBusinessObjects additionally includes LookupProbe under Development only.
                .WithSharedBusinessObjects(sharedBusinessObjects)
                // MT-001: self-seed a tenant DB via the demo's own Updater/DataGenerator on a fresh
                // machine (no more "run the demo Blazor app once" manual step). Lazy + per-tenant, NOT
                // eager-at-startup-for-all-tenants: wires TenantInitializationOptions.OnUpdateTenantDatabase,
                // which SignInManager.AuthenticateByLogonParametersCore fires once per logon, for the tenant
                // resolved from that logon's credentials (DevExpress.ExpressApp.Security/SignInManager.cs).
                // TenantDatabaseUpdater.EnsureTenantDatabaseCreated (MultiTenancy.AspNetCore/Services/
                // TenantDatabaseUpdater.cs) then runs setupApplication.CheckCompatibility() for that tenant
                // AT MOST ONCE PER TENANT PER HOST PROCESS (guarded by a ConcurrentDictionary-backed
                // IMultipleDatabaseCheckCompatibilityHelper) — so it only touches the already-seeded
                // company1 tenant if a DatabaseVersionMismatch is actually detected. Safe even then: the
                // demo's own Updater.UpdateDatabaseAfterUpdateSchema (tenant branch) gates DataGenerator.Execute()
                // on `GetObjectsCount(typeof(Customer)) == 0` (DataGenerator.cs) and the Ensure*
                // user/role helpers only mutate newly-created objects, not ones found by criteria — so
                // re-running it against a populated tenant is a no-op. Verified via installed 26.1 source
                // (not memory); no-op proof: 46/46 XafHeadless.Api.Tests green post-change.
                .WithTenantDatabaseUpdater()
                .WithTenantResolver<TenantByEmailResolver>();
            builder.AddBuildStep(application => {
                application.ApplicationName = "OutlookInspiredDemo";
                // This host owns its disposable host catalog: let XAF create/update/seed it.
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

        // GRID-001: MaxTop must be >= the client's capped in-memory load (XafListView.RowCap = 5000). The
        // client fetches up to RowCap rows in ONE $top request so DxGrid can group/sort/filter them
        // client-side; a lower MaxTop 400s that request ("The limit of 'N' for Top query has been exceeded").
        // Reads are permission-trimmed and the client caps the load, so a higher ceiling is safe here; writes
        // are separately blocked by ODataReadOnlyMiddleware. Keep in sync with XafListView.RowCap.
        // SVR-003: HostSharedODataQueryConvention disables OData constant-parameterization app-wide so
        // $filter/$top literals inline and read CORRECT data for host-shared BOs (see the convention's
        // header for the root cause). No-op for per-tenant types (Order).
        services.AddControllers(o => o.Conventions.Add(new Infrastructure.HostSharedODataQueryConvention()))
            .AddOData((options, sp) => options
            .AddRouteComponents("api/odata", new EdmModelBuilder(sp).GetEdmModel(),
                Microsoft.OData.ODataVersion.V401, rs => rs.ConfigureXafWebApiServices())
            .EnableQueryFeatures(5000));
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o => {
            o.JsonSerializerOptions.PropertyNamingPolicy = null;
            o.JsonSerializerOptions.MaxDepth = 128;
        });
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env) {
        if (env.IsDevelopment()) app.UseDeveloperExceptionPage();
        UseFailedRequestLogging(app);
        app.UseRouting();
        app.UseCors();
        app.UseMiddleware<ODataReadOnlyMiddleware>(); // SEC-001: block OData writes (after CORS, before XAF endpoints)
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints => { endpoints.MapControllers(); endpoints.MapXafEndpoints(); });
    }

    // Runtime diagnostics (docs/superpowers/specs/2026-08-08-runtime-diagnostics-design.md): log every
    // request this host answers with 4xx/5xx, WITH its query string. An OData 400 is a normal response,
    // not an exception, so nothing else records it -- a bad $filter left no trace anywhere, which is
    // what made the GRID date-filter bug so expensive to find. Registered before UseRouting so it also
    // sees requests rejected before an endpoint is selected.
    //
    // Deliberately ~10 lines rather than UseHttpLogging: that middleware logs EVERY request and needs an
    // IHttpLoggingInterceptor to narrow itself to failures -- more configuration than it replaces.
    static void UseFailedRequestLogging(IApplicationBuilder app) => app.Use(async (context, next) => {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await next();
        var status = context.Response.StatusCode;
        if (status < 400) return;
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("XafHeadless.Api.FailedRequests");
        var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // 4xx is the caller's problem, 5xx is ours -- different levels so a log filter can separate them.
        logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning,
            "{Method} {Path}{Query} -> {Status} in {ElapsedMs:F0}ms (user {User})",
            context.Request.Method, context.Request.Path, context.Request.QueryString,
            status, elapsedMs, context.User?.Identity?.Name ?? "anonymous");
    });
}
