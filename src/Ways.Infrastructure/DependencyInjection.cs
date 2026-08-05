using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using Ways.Infrastructure.Seguridad;

namespace Ways.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Clave del <see cref="WaysDbContext"/> atado a <see cref="TenantActualFijo.Plataforma"/>
    /// que usa <see cref="InicializadorDeBaseDeDatos"/> (ADR-2): migraciones y semilla nunca
    /// corren sobre la sesión HTTP mutable. Referencia a <see cref="ClavesDeContexto.Plataforma"/>
    /// en vez de repetir el literal (Infrastructure sí puede referenciar Application): así un
    /// typo en cualquiera de las dos constantes es un error de compilación, no un mismatch
    /// silencioso en runtime al resolver el servicio keyed.</summary>
    public const string ClaveContextoPlataforma = ClavesDeContexto.Plataforma;

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
        {
            ConfigurarNpgsql(options, cadena);
            options.AddInterceptors(sp.GetRequiredService<InterceptorDeContextoDeTenant>());
        });

        services.AddScoped<IWaysDbContext>(sp => sp.GetRequiredService<WaysDbContext>());
        services.AddSingleton<IHasheadorDeContrasenas, HasheadorPbkdf2>();

        // Migraciones y semilla (ADR-2, ADR-14): un WaysDbContext propio, atado a la
        // instancia inmutable TenantActualFijo.Plataforma — nunca a la sesión HTTP mutable
        // que usa el resto de la app.
        services.AddKeyedScoped<WaysDbContext>(ClaveContextoPlataforma, (_, _) =>
        {
            var options = new DbContextOptionsBuilder<WaysDbContext>();
            ConfigurarNpgsql(options, cadena);
            options.AddInterceptors(new InterceptorDeContextoDeTenant(TenantActualFijo.Plataforma));
            return new WaysDbContext(options.Options, TenantActualFijo.Plataforma);
        });

        // ClaveContextoPlataforma ES ClavesDeContexto.Plataforma (Ways.Application.Abstracciones):
        // Application no puede referenciar este proyecto para resolver el servicio keyed acá,
        // así que expone su propia constante — ver el comentario de ClavesDeContexto para
        // quién la consume del lado de Application (p. ej. la verificación de suspensión de
        // tenant en el login).
        services.AddKeyedScoped<IWaysDbContext>(ClaveContextoPlataforma, (sp, clave) =>
            sp.GetRequiredKeyedService<WaysDbContext>(clave));

        services.AddScoped<InicializadorDeBaseDeDatos>();

        // Las claves que firman la cookie de sesión van a la base, no al disco del
        // contenedor: así un redeploy no invalida las sesiones abiertas.
        services.AddDataProtection()
            .SetApplicationName("ways")
            .PersistKeysToDbContext<WaysDbContext>();

        services.Configure<SemillaRoot>(configuration.GetSection(SemillaRoot.Seccion));

        return services;
    }

    private static void ConfigurarNpgsql(DbContextOptionsBuilder options, string cadena) =>
        options.UseNpgsql(cadena, npgsql =>
        {
            npgsql.MapEnum<EstadoUsuario>("estado_usuario");
            npgsql.MapEnum<EstadoTenant>("estado_tenant");
            npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
            npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
            npgsql.MapEnum<TipoDocumento>("tipo_documento");
            npgsql.MapEnum<ModoLista>("modo_lista");
            npgsql.MapEnum<UnidadVenta>("unidad_venta");
            npgsql.MapEnum<EstadoComprobante>("estado_comprobante");
            npgsql.MapEnum<MotivoStock>("motivo_stock");
            npgsql.MapEnum<TipoMovimientoCc>("tipo_movimiento_cc");
            npgsql.MapEnum<EstadoTurno>("estado_turno");
            npgsql.MapEnum<TipoMovimientoCaja>("tipo_movimiento_caja");
            npgsql.MapEnum<TipoMovimientoTesoreria>("tipo_movimiento_tesoreria");
            npgsql.MapEnum<CategoriaGasto>("categoria_gasto");
            npgsql.MapEnum<EstadoCompra>("estado_compra");
            npgsql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(3), null);
        });
}
