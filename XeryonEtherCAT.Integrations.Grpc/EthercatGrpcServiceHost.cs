using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Reflection;
using Grpc.Reflection.V1Alpha;
using Microsoft.Extensions.Logging;
using XeryonEtherCAT.Core.Abstractions;

namespace XeryonEtherCAT.Integrations.Grpc;

public sealed class EthercatGrpcServiceHost : IAsyncDisposable
{
    private readonly IEthercatDriveService _driveService;
    private readonly EthercatGrpcServerOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<EthercatGrpcServiceHost> _logger;
    private Server? _server;

    public EthercatGrpcServiceHost(
        IEthercatDriveService driveService,
        EthercatGrpcServerOptions options,
        ILoggerFactory loggerFactory,
        ILogger<EthercatGrpcServiceHost> logger)
    {
        _driveService = driveService ?? throw new ArgumentNullException(nameof(driveService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_server is not null)
        {
            return Task.CompletedTask;
        }

        ct.ThrowIfCancellationRequested();

        var serviceLogger = _loggerFactory.CreateLogger<EthercatGrpcService>();
        var service = new EthercatGrpcService(_driveService, _options, serviceLogger);

        var reflectionServiceImpl = new ReflectionServiceImpl(EthercatControl.Descriptor);

        var server = new Server
        {
            Services =
            {
                EthercatControl.BindService(service),
                ServerReflection.BindService(reflectionServiceImpl)
            },
            Ports = { new ServerPort(_options.Host, _options.Port, ServerCredentials.Insecure) }
        };

        try
        {
            server.Start();
            _server = server;
            _logger.LogInformation("gRPC server listening on {Host}:{Port}.", _options.Host, _options.Port);
        }
        catch
        {
            try
            {
                server.ShutdownAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // ignored
            }

            throw;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_server is null)
        {
            return;
        }

        var server = _server;
        _server = null;

        _logger.LogInformation("Stopping gRPC server on {Host}:{Port}.", _options.Host, _options.Port);
        await server.ShutdownAsync().WaitAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
