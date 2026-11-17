using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NativeWeave.Server.Hosting;

/// <summary>Optional Generic Host lifecycle integration; Rust remains the only network Host.</summary>
public static class NativeServerServiceCollectionExtensions
{
    /// <summary>Registers one application singleton and hosted lifecycle adapter; host async disposal owns the final safe join.</summary>
    /// <param name="services">Explicit service registrations.</param>
    /// <param name="factory">Creates the independently owned application from the host provider.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">An application lifecycle adapter is already registered.</exception>
    /// <remarks>Stop requests bounded close; native retirement can remain pending until async container disposal.
    /// The factory must not recursively build or dispose the external Host provider.</remarks>
    public static IServiceCollection AddNativeWeaveServer(this IServiceCollection services, Func<IServiceProvider, NativeServerApplication> factory)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(factory);
        if (services.Any(x => x.ServiceType == typeof(NativeServerApplication))) throw new InvalidOperationException("A NativeWeave Server application is already registered.");
        services.AddSingleton(factory);
        services.AddSingleton<IHostedService, ServerHostedService>();
        return services;
    }
    private sealed class ServerHostedService(NativeServerApplication application) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => application.StartAsync(cancellationToken);
        public async Task StopAsync(CancellationToken cancellationToken)
        { await application.CloseAsync(application.Configuration.Options.ShutdownTimeout, cancellationToken).ConfigureAwait(false); }
    }
}
