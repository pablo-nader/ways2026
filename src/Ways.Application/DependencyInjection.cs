using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Catalogos;
using Ways.Application.Clientes;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Precios;
using Ways.Application.Proveedores;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;

namespace Ways.Application;

public static class DependencyInjection
{
    public static IServiceCollection AgregarApplication(this IServiceCollection services)
    {
        services.AddSingleton<IRelojDelSistema, RelojDelSistema>();

        // AsignadorDeNumeroCliente (Ways.Application.Clientes) es estática, sin ciclo de
        // vida de DI que registrar — el IWaysDbContext llega por parámetro en cada llamada.

        services.AddScoped<ServicioDeAutenticacion>();
        services.AddScoped<ServicioDeUsuarios>();

        services.AddScoped<ServicioDeAreas>();
        services.AddScoped<ServicioDeMarcas>();
        services.AddScoped<ServicioDeGrupos>();
        services.AddScoped<ServicioDeMediosPago>();
        services.AddScoped<ServicioDeCategorias>();
        services.AddScoped<ServicioDeCatalogosFiscales>();
        services.AddScoped<ServicioDeListasPrecio>();

        services.AddScoped<ServicioDeParametros>();

        services.AddScoped<ServicioDeClientes>();
        services.AddScoped<ServicioDeProveedores>();
        services.AddScoped<ServicioDeArticulos>();
        services.AddScoped<ServicioDePrecios>();
        services.AddScoped<ServicioDeOfertas>();

        services.AddScoped<ServicioDeEscaneo>();
        services.AddScoped<ServicioDeVentas>();

        services.AddScoped<ServicioDeAprovisionamiento>();
        services.AddScoped<ServicioDeOrganizacion>();

        return services;
    }
}
