using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XeryonEtherCAT.Core.Abstractions;
using XeryonEtherCAT.Core.Internal.Soem;
using XeryonEtherCAT.Core.Options;
using XeryonEtherCAT.Core.Services;

namespace XeryonEtherCAT.Core.Extensions;

/// <summary>
/// Dependency injection helpers for the EtherCAT drive service.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEthercatDriveService(this IServiceCollection services, Action<EthercatDriveOptions>? configure = null)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.AddOptions<EthercatDriveOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<ISoemClient, SoemClient>();
        services.AddSingleton<IEthercatDriveService, EthercatDriveService>();
        return services;
    }
}
