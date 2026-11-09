using Deming.Data.Models;
using Deming.Data.Services;
using Deming.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Deming.Data;

public static class DataServiceCollectionExtensions
{
    /// <summary>Configuration key for the database file. Default: <see cref="DemingDb.DefaultPath"/>.
    /// Command line: --Deming:DatabasePath=..., environment: Deming__DatabasePath.</summary>
    public const string DatabasePathKey = "Deming:DatabasePath";

    /// <summary>
    /// Registers the database, data services, the Identity owner store and the Data Protection
    /// key ring. New services are registered here, one line each.
    /// </summary>
    public static IServiceCollection AddDemingData(this IServiceCollection services, IConfiguration configuration)
    {
        var path = configuration[DatabasePathKey];
        services.AddSingleton(new DemingDb(string.IsNullOrWhiteSpace(path) ? DemingDb.DefaultPath() : path));

        // Data services (stateless; one per area).
        services.AddSingleton<ProjectService>();
        services.TryAddSingleton(TimeProvider.System);
        services.Configure<TaskLockOptions>(configuration.GetSection(TaskLockOptions.SectionName));
        services.AddSingleton<TaskService>();
        services.AddSingleton<TaskLifecycleService>();
        services.AddSingleton<TaskQueryService>();
        services.AddSingleton<DecisionService>();
        services.AddSingleton<FindingService>();
        services.AddSingleton<SearchService>();
        services.AddSingleton<MessageService>();

        // Identity with Deming's own store, no EF. No roles. The sign-in manager,
        // token providers and cookies are the panel's (PanelAuth); the lockout policy is set here.
        services.AddIdentityCore<OwnerAccount>(o =>
            {
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                // Identity's character list cannot say "any letter"; OwnerUserNameValidator enforces the rule.
                o.User.AllowedUserNameCharacters = "";
            })
            .AddUserStore<OwnerAccountStore>()
            .AddUserValidator<OwnerUserNameValidator>();

        services.AddDataProtection().SetApplicationName("Deming");
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
            new ConfigureOptions<KeyManagementOptions>(o =>
                o.XmlRepository = new DataProtectionKeyRepository(sp.GetRequiredService<DemingDb>())));

        return services;
    }
}
