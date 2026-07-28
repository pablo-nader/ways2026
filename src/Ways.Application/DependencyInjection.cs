using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Usuarios;

namespace Ways.Application;

public static class DependencyInjection
{
    public static IServiceCollection AgregarApplication(this IServiceCollection services)
    {
        services.AddSingleton<IRelojDelSistema, RelojDelSistema>();
        services.AddScoped<ServicioDeAutenticacion>();
        services.AddScoped<ServicioDeUsuarios>();

        return services;
    }
}
