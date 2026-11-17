using NativeWeave.Database;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers independently owned singleton sources through the optional database DI package.</summary>
/// <remarks>Configuration runs once when the singleton is constructed. A container owns and disposes the
/// created source; dispose the container asynchronously to await physical retirement. Sessions are never registered implicitly.</remarks>
public static class NativeWeaveDatabaseServiceCollectionExtensions
{
    /// <summary>Registers one unkeyed container-owned singleton source using the ordinary builder.</summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configure">The configuration evaluated once at singleton construction.</param>
    /// <returns>The supplied service collection.</returns>
    /// <exception cref="ArgumentNullException">The collection or configuration is null.</exception>
    /// <exception cref="InvalidOperationException">An unkeyed source is already registered.</exception>
    public static IServiceCollection AddNativeWeaveDatabase(this IServiceCollection services, Action<DataSourceBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(DataSource) && !descriptor.IsKeyedService))
            throw new InvalidOperationException("An unkeyed NativeWeave data source is already registered.");
        services.AddSingleton<DataSource>(_ => { var builder = new DataSourceBuilder(); configure(builder); return builder.Build(); });
        return services;
    }

    /// <summary>Registers one keyed container-owned singleton for standard keyed service resolution.</summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="name">The nonempty ordinal source key.</param>
    /// <param name="configure">The configuration evaluated once at singleton construction.</param>
    /// <returns>The supplied service collection.</returns>
    /// <exception cref="ArgumentNullException">The collection, key or configuration is null.</exception>
    /// <exception cref="ArgumentException">The key is empty.</exception>
    /// <exception cref="InvalidOperationException">A source with this key is already registered.</exception>
    public static IServiceCollection AddKeyedNativeWeaveDatabase(this IServiceCollection services, string name, Action<DataSourceBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(configure);
        if (name.Length == 0) throw new ArgumentException("A keyed source name cannot be empty.", nameof(name));
        if (services.Any(descriptor => descriptor.ServiceType == typeof(DataSource) && descriptor.IsKeyedService && Equals(descriptor.ServiceKey, name)))
            throw new InvalidOperationException("A NativeWeave data source with this key is already registered.");
        services.AddKeyedSingleton<DataSource>(name, (_, _) => { var builder = new DataSourceBuilder().WithName(name); configure(builder); return builder.Build(); });
        return services;
    }
}
