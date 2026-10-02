namespace Typebeat.Web.Storage;

/// <summary>Builds and registers the public object store (backlog 364).</summary>
public static class PublicObjectStores
{
    /// <summary>The configured R2 store, or the disabled one (with the reason it is off).</summary>
    public static IPublicObjectStore FromConfiguration(IConfiguration config)
    {
        var options = PublicObjectStoreOptions.FromConfiguration(config, out string reason);
        return options == null ? new DisabledPublicObjectStore(reason) : new R2PublicObjectStore(options);
    }

    /// <summary>
    /// Registers the public store, the installer catalog over it, the one-time package backfill
    /// and the releases mirror (backlog 380; both return at once when the store is disabled, and
    /// the mirror's status is registered either way for the ops readout). One call, so Program.cs
    /// carries a single line for the whole feature.
    /// </summary>
    public static IServiceCollection AddPublicObjectStore(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<IPublicObjectStore>(_ => FromConfiguration(config));
        services.AddSingleton<GameInstallers>();
        services.AddHostedService<Packages.PublicPackageBackfill>();
        services.AddSingleton(_ => ReleasesMirrorOptions.FromConfiguration(config));
        services.AddSingleton<ReleasesMirrorStatus>();
        services.AddHostedService<ReleasesMirror>();
        return services;
    }
}
