using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Auditoria;
using Ways.Application.Caja;
using Ways.Application.Catalogos;
using Ways.Application.Clientes;
using Ways.Application.Compras;
using Ways.Application.CuentaCorriente;
using Ways.Application.Etiquetas;
using Ways.Application.Fiscal;
using Ways.Application.Gastos;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Precios;
using Ways.Application.Proveedores;
using Ways.Application.Reportes;
using Ways.Application.Stock;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;

namespace Ways.Application;

public static class DependencyInjection
{
    public static IServiceCollection AgregarApplication(this IServiceCollection services)
    {
        services.AddSingleton<IRelojDelSistema, RelojDelSistema>();

        // stage-14-auditoria-trazabilidad, Slice 1: el writer se registra completo desde esta
        // slice, aunque sin call sites todavía (slices 2-4 lo inyectan recién ahí).
        services.AddScoped<ServicioDeAuditoria>();
        // stage-14-auditoria-trazabilidad, Slice 5: el lado de lectura — GET /api/auditoria.
        services.AddScoped<ServicioDeConsultaDeAuditoria>();

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

        // stage-18-etiquetas-y-consulta, Slice 2 (task 2.23): compone ServicioDeArticulos
        // (selección por filtro) + ServicioDeOfertas (precio/ofertas) — registrado después de
        // ambos, aunque el orden de AddScoped no importa para la resolución de DI.
        services.AddScoped<ServicioDeEtiquetas>();

        services.AddScoped<ServicioDeEscaneo>();
        services.AddScoped<ServicioDeVentas>();
        services.AddScoped<ServicioDeStock>();
        services.AddScoped<ServicioDeLotes>();

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

        // stage-20-organizacion-relaciones-y-bajas, Slice 3: el guard de uso se registra completo
        // desde esta slice y SIN NINGÚN LLAMADOR a propósito (design OD3) — no hay red de base
        // atrás suyo (db-error-backstops es estructuralmente N/A), así que se entrega inerte para
        // poder revisarlo por sus propios méritos antes de que algo pueda invocarlo. La slice 4 lo
        // cablea. InventarioDeDependientes es estático y puro: no tiene ciclo de vida que registrar.
        services.AddScoped<InspectorDeUso>();

        // stage-8-compras-transferencias-inventario, Slice 2: el ciclo de vida entero de la
        // compra — reusa ServicioDePrecios (AplicarPrecioSugeridoAsync), nunca
        // ServicioDeStock/ServicioDeVentas (Slice 2 non-negotiable).
        services.AddScoped<ServicioDeCompras>();
        // stage-8-compras-transferencias-inventario, Slice 4: el saldo derivado del proveedor
        // (design decisión 11) — dedicado, no extiende ServicioDeProveedores.
        services.AddScoped<ServicioDeSaldoDeProveedor>();

        // stage-16-ordenes-de-compra, Slice 2: borrador CRUD + enviar (numeración propia, serie
        // 'OC', vía AsignadorDeNumeroComprobante — no se toca). cerrar/anular llegan en slice 4.
        services.AddScoped<ServicioDeOrdenesDeCompra>();

        // stage-15-cc-proveedores-ledger, Slice 4: el lado de lectura del ledger de proveedores —
        // estado de cuenta paginado (task 4.3).
        services.AddScoped<ServicioDeCuentaCorrienteDeProveedor>();

        // stage-17-presupuestos-y-remitos, Slice 2: ABM + enviar (numeración propia, serie
        // 'PRES', vía AsignadorDeNumeroComprobante — no se toca) + anular. La conversión
        // (EscriturasDePresupuesto, llamada desde ServicioDeVentas) llega en Slice 3.
        services.AddScoped<ServicioDePresupuestos>();

        // stage-17-presupuestos-y-remitos, Slice 5: ABM + emitir (numeración propia, serie 'REM',
        // FEFO, el cuarto write site de stock) + anular.
        services.AddScoped<ServicioDeRemitos>();

        // stage-17-presupuestos-y-remitos, Slice 6: la consolidación — N remitos emitidos en UN
        // comprobante TXR itemless (serie 'TXR', vía AsignadorDeNumeroComprobante — no se toca).
        services.AddScoped<ServicioDeFacturacionDeRemitos>();

        // stage-10-agregacion-dashboard, Slice 2: LectorDeSerieTemporal es la única superficie de
        // SQL crudo de toda la etapa (design decisión 2) — ServicioDeReportesDeVentas es su
        // primer consumidor.
        services.AddScoped<LectorDeSerieTemporal>();
        services.AddScoped<ServicioDeReportesDeVentas>();

        // stage-10-agregacion-dashboard, Slice 5: ServicioDeReportesDeEgresos reusa
        // LectorDeSerieTemporal para gastos/resumen; compras/por-proveedor es LINQ puro.
        services.AddScoped<ServicioDeReportesDeEgresos>();
        // stage-10-agregacion-dashboard, Slice 5: top artículos — LINQ puro, sin costo/margen
        // (eso vive en ServicioDeReportesDeRentabilidad, slice 4, bajo LecturaDeRentabilidad).
        services.AddScoped<ServicioDeReportesDeArticulos>();
        // stage-10-agregacion-dashboard, Slice 4: el margen — LINQ propio, sin dependencia de
        // LectorDeSerieTemporal (no bucketea, design: Interfaces / Contracts Rentabilidad).
        services.AddScoped<ServicioDeReportesDeRentabilidad>();

        // stage-11-exportacion-reportes, Slice 5a (design: G2/G3 — minimal aggregation):
        // ServicioDeHistoricoDeCajas es la única agregación nueva de la slice (G2 histórico);
        // LectorDeLineasDelTurno son dos lecturas indexadas llanas para el detalle del turno
        // (G2 detail), consumidas junto a ServicioDeResumenDeTurno, ya registrado arriba.
        services.AddScoped<ServicioDeHistoricoDeCajas>();
        services.AddScoped<LectorDeLineasDelTurno>();

        // stage-11-exportacion-reportes, Slice 7 (design: G2/G3 — minimal aggregation): G3, el
        // libro de tesorería — cero derivación, solo lectura encadenada por Id.
        services.AddScoped<ServicioDeTesoreria>();

        // stage-11-exportacion-reportes, Slice 9 (proposal decisión 10, droppable a Etapa 13):
        // existencias — LINQ puro sobre stock ⋈ articulos, sin dependencia de LectorDeSerieTemporal.
        services.AddScoped<ServicioDeReportesDeStock>();

        // stage-19a-slice4: el ABM de certificados fiscales bajo Politicas.AdministracionFiscal —
        // IAlmacenDeClavesFiscales se registra del lado de Infrastructure (DependencyInjection.cs),
        // que es donde vive la implementación concreta del cifrado (CifradoDeClavesFiscales).
        services.AddScoped<ServicioDeCertificados>();

        return services;
    }
}
