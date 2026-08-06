using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Caja;
using Ways.Application.Catalogos;
using Ways.Application.Clientes;
using Ways.Application.Compras;
using Ways.Application.CuentaCorriente;
using Ways.Application.Gastos;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Precios;
using Ways.Application.Proveedores;
using Ways.Application.Stock;
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
        services.AddScoped<ServicioDeStock>();

        // stage-6-turnos-caja, Slice 4: LectorDeMovimientosDelTurno es el único lector de la
        // derivación (design decisión 5), compartido por ServicioDeTurnos.CerrarAsync y
        // ServicioDeResumenDeTurno. LectorDeContenidoDeResumen (follow-up D6-content
        // enrichment) es un lector HERMANO, solo consumido por ServicioDeResumenDeTurno.
        services.AddScoped<LectorDeMovimientosDelTurno>();
        services.AddScoped<LectorDeContenidoDeResumen>();
        services.AddScoped<ServicioDeTurnos>();
        services.AddScoped<ServicioDeResumenDeTurno>();
        services.AddScoped<ServicioDeGastos>();

        // stage-7-cuenta-corriente (Slice 2): pago a cuenta (RC) — reusa
        // ServicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync, se registra junto a la caja.
        services.AddScoped<ServicioDeCuentaCorriente>();

        // stage-7-cuenta-corriente (Slice 3): reliquidación a precio del día — el lector de
        // elegibles alimenta tanto el preview como el commit del servicio.
        services.AddScoped<LectorDeConsumosReliquidables>();
        services.AddScoped<ServicioDeReliquidacion>();

        services.AddScoped<ServicioDeAprovisionamiento>();
        services.AddScoped<ServicioDeOrganizacion>();

        // stage-8-compras-transferencias-inventario, Slice 2: el ciclo de vida entero de la
        // compra — reusa ServicioDePrecios (AplicarPrecioSugeridoAsync), nunca
        // ServicioDeStock/ServicioDeVentas (Slice 2 non-negotiable).
        services.AddScoped<ServicioDeCompras>();

        return services;
    }
}
