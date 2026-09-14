using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Verce.Platform.Audit;
using Verce.Platform.Cli;
using Verce.Platform.Health;
using Verce.Platform.Identity;
using Verce.Platform.Ownership;
using Verce.Platform.Outbox;
using Verce.Platform.Scheduling;
using Verce.Platform.UnitOfWork;
using Verce.SharedKernel.Time;

namespace Verce.Platform.Persistence;

public static class PlatformServiceCollectionExtensions
{
    /// <summary>
    /// Wires the composed persistence stack (ADR-0001 §5.1): VerceDbContext (Npgsql,
    /// snake_case naming per DATA-MODEL.md), the audit + aggregate-version interceptors, the
    /// multi-wave Unit of Work, Identity, Data Protection and the outbox processor.
    /// </summary>
    public static IServiceCollection AddVercePlatform(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment,
        IEnumerable<Assembly> moduleAssemblies)
    {
        VerceDbContext.ConfigureModuleAssemblies(moduleAssemblies);
        services.AddSingleton<IClock, SystemClock>();

        // Built once at startup; throws loudly on a broken ownership chain (ADR-0011 §2.5).
        var assembliesForOwnership = moduleAssemblies
            .Concat(new[] { typeof(VerceDbContext).Assembly })
            .Distinct()
            .ToList();
        services.AddSingleton(_ => AggregateOwnershipRegistry.BuildAndValidate(assembliesForOwnership));

        // Scoped: one AmbientOperationContext per HTTP request / per CLI invocation.
        services.AddScoped(sp =>
        {
            var clock = sp.GetRequiredService<IClock>();
            return new AmbientOperationContext(Guid.CreateVersion7(), AuditSource.Api, clock.UtcNow);
        });

        services.AddScoped<AggregateVersionInterceptor>();
        services.AddScoped<AuditSaveChangesInterceptor>();
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        services.AddScoped<IUnitOfWork, UnitOfWork.UnitOfWork>();
        services.AddScoped<OutboxProcessor>();
        services.AddScoped<OutboxDispatcher>();
        services.AddScoped<OutboxRetentionService>();
        services.AddScoped<OutboxAdministrationService>();
        services.AddSingleton<OutboxRetryPolicy>();
        services.AddHostedService<OutboxConsumerStartupValidator>();
        services.AddVerceOutboxScheduling(configuration);
        services.AddSingleton<ISecretPrompt, ConsoleSecretPrompt>();
        services.AddSingleton<IExpectedSecretProvider, MountedFileSecretProvider>();
        services.AddScoped<OwnerBootstrapService>();
        services.AddScoped<OwnerGuard>();
        services.AddScoped<Verce.Platform.DataProtection.DataProtectionRecoveryService>();

        services.AddHealthChecks()
            .AddDbContextCheck<VerceDbContext>("database", tags: new[] { "ready" })
            .AddCheck<OutboxDispatcherHealthCheck>("outbox", tags: new[] { "ready" });

        services.AddDbContext<VerceDbContext>((sp, options) =>
        {
            var connectionString = configuration.GetConnectionString("Verce")
                ?? throw new InvalidOperationException("ConnectionStrings:Verce is not configured.");

            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(VerceDbContext).Assembly.FullName);
            });
            options.UseSnakeCaseNamingConvention();
            options.AddInterceptors(
                sp.GetRequiredService<AggregateVersionInterceptor>(),
                sp.GetRequiredService<AuditSaveChangesInterceptor>());
        });

        services.AddIdentityCore<ApplicationUser>(options =>
        {
            // SECURITY §2.4: length beats symbol theatre.
            options.Password.RequiredLength = 12;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireDigit = false;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedAccount = false; // setup-token flow replaces email confirmation
        })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<VerceDbContext>()
            .AddDefaultTokenProviders()
            .AddSignInManager();

        AddDataProtection(services, configuration, environment);

        return services;
    }

    /// <summary>
    /// ADR-0008 §2 / SECURITY §5.1: keys persisted in Postgres, wrapped by a certificate ring
    /// held OUTSIDE the database. Production fails closed if the certificate is unusable —
    /// enforced by throwing during configuration rather than falling back to an unprotected
    /// ring, which the framework would otherwise do silently.
    /// </summary>
    private static void AddDataProtection(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // The injected IHostEnvironment (not a raw re-read of the ASPNETCORE_ENVIRONMENT
        // configuration key) is the single source of truth for this decision — WebApplicationFactory's
        // UseEnvironment(...) sets IHostEnvironment.EnvironmentName without necessarily also
        // populating that exact configuration key, and a fail-closed security check must not
        // depend on which of two equivalent-looking mechanisms happened to set it.
        var isDevelopment = environment.IsDevelopment();

        var dataProtectionBuilder = services.AddDataProtection()
            .SetApplicationName("Verce3D")
            .PersistKeysToDbContext<VerceDbContext>();

        var currentCertPath = configuration["DataProtection:CurrentCertificatePath"];
        var certPasswordFile = configuration["DataProtection:CertificatePasswordFile"];
        var unprotectPaths = configuration.GetSection("DataProtection:UnprotectCertificatePaths").Get<string[]>() ?? [];

        if (isDevelopment && string.IsNullOrWhiteSpace(currentCertPath))
        {
            // Development-only fallback (OPERATIONS §7): unprotected local key ring. This
            // branch MUST be unreachable when isDevelopment is false.
            var devKeyDirectory = configuration["DataProtection:DevKeyDirectory"] ?? ".dataprotection";
            dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(devKeyDirectory));
            return;
        }

        if (string.IsNullOrWhiteSpace(currentCertPath) || !File.Exists(currentCertPath))
        {
            throw new InvalidOperationException(
                "Production Data Protection configuration requires DataProtection:CurrentCertificatePath " +
                "to point to a readable certificate file. Startup validation fails closed rather than " +
                "falling back to an unprotected key ring (SECURITY §5.1, OPERATIONS §3.2). " +
                "Recovery: run the offline 'recover-data-protection' command.");
        }

        var password = string.IsNullOrWhiteSpace(certPasswordFile) ? null : File.ReadAllText(certPasswordFile).Trim();
        var currentCert = string.IsNullOrEmpty(password)
            ? System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(currentCertPath)
            : System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(currentCertPath, password);

        dataProtectionBuilder.ProtectKeysWithCertificate(currentCert);

        var ring = new List<System.Security.Cryptography.X509Certificates.X509Certificate2> { currentCert };
        foreach (var path in unprotectPaths.Where(p => !string.Equals(p, currentCertPath, StringComparison.OrdinalIgnoreCase)))
        {
            if (!File.Exists(path))
                throw new InvalidOperationException($"DataProtection:UnprotectCertificatePaths entry '{path}' does not exist.");
            ring.Add(string.IsNullOrEmpty(password)
                ? System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(path)
                : System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(path, password));
        }

        dataProtectionBuilder.UnprotectKeysWithAnyCertificate(ring.ToArray());
    }
}
