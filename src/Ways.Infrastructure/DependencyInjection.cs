using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AgregarInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // DATABASE_URL es la convención de los paneles de hosting; si está, gana.
        var cruda = configuration["DATABASE_URL"]
            ?? configuration.GetConnectionString("Ways")
            ?? throw new InvalidOperationException(
                "Falta la cadena de conexión: definí 'ConnectionStrings__Ways' o 'DATABASE_URL'.");

        var cadena = CadenaDeConexion.Normalizar(cruda);

        services.AddScoped<TenantActualDeSesion>();
        services.AddScoped<ITenantActual>(sp => sp.GetRequiredService<TenantActualDeSesion>());
        services.AddScoped<InterceptorDeContextoDeTenant>();

        services.AddDbContext<WaysDbContext>((sp, options) =>
            options.UseNpgsql(cadena, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
                npgsql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(3), null);
            })
            .AddInterceptors(sp.GetRequiredService<InterceptorDeContextoDeTenant>()));

        services.AddScoped<IWaysDbContext>(sp => sp.GetRequiredService<WaysDbContext>());
        services.AddSingleton<IHasheadorDeContrasenas, HasheadorPbkdf2>();
        services.AddScoped<InicializadorDeBaseDeDatos>();

        // Las claves que firman la cookie de sesión van a la base, no al disco del
        // contenedor: así un redeploy no invalida las sesiones abiertas.
        services.AddDataProtection()
            .SetApplicationName("ways")
            .PersistKeysToDbContext<WaysDbContext>();

        services.Configure<SemillaRoot>(configuration.GetSection(SemillaRoot.Seccion));

        return services;
    }
}
