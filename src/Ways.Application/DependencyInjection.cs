using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Catalogos;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Usuarios;

namespace Ways.Application;

public static class DependencyInjection
{
    public static IServiceCollection AgregarApplication(this IServiceCollection services)
    {
        services.AddSingleton<IRelojDelSistema, RelojDelSistema>();
        services.AddScoped<ServicioDeAutenticacion>();
        services.AddScoped<ServicioDeUsuarios>();

        services.AddScoped<ServicioDeAreas>();
        services.AddScoped<ServicioDeMarcas>();
        services.AddScoped<ServicioDeGrupos>();
        services.AddScoped<ServicioDeMediosPago>();
        services.AddScoped<ServicioDeCategorias>();
        services.AddScoped<ServicioDeCatalogosFiscales>();

        services.AddScoped<ServicioDeParametros>();

        services.AddScoped<ServicioDeAprovisionamiento>();

        return services;
    }
}
