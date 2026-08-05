using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Gastos;
using Ways.Domain.Ofertas;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;

namespace Ways.Application.Abstracciones;

/// <summary>
/// Superficie de persistencia que ve la capa de aplicación.
/// La implementación concreta (EF Core + Npgsql) vive en Infrastructure.
/// </summary>
public interface IWaysDbContext
{
    DbSet<Usuario> Usuarios { get; }
    DbSet<Rol> Roles { get; }
    DbSet<Tenant> Tenants { get; }
    DbSet<Empresa> Empresas { get; }
    DbSet<PuntoVenta> PuntosVenta { get; }

    DbSet<Area> Areas { get; }
    DbSet<Categoria> Categorias { get; }
    DbSet<Marca> Marcas { get; }
    DbSet<Grupo> Grupos { get; }
    DbSet<MedioPago> MediosPago { get; }
    DbSet<CondicionFiscal> CondicionesFiscales { get; }
    DbSet<AlicuotaIva> AlicuotasIva { get; }
    DbSet<TipoComprobante> TiposComprobante { get; }
    DbSet<Parametro> Parametros { get; }

    // Stage 2 (clientes-proveedores): AsignadorDeNumeroCliente/ServicioDeAprovisionamiento
    // consumen estos 4 desde este mismo lote (a diferencia de los catálogos de tenant de
    // arriba, sin consumidor de Application todavía).
    DbSet<Cliente> Clientes { get; }
    DbSet<Proveedor> Proveedores { get; }
    DbSet<ListaPrecio> ListasPrecio { get; }
    DbSet<NumeracionCliente> NumeracionesClientes { get; }

    // stage-3-articulos-y-precios, Slice 2: primer consumidor de Application de estos 5 —
    // ServicioDeArticulos (articulos/codigos_barra/articulos_empresas) y
    // ServicioDeArticulos.SugerirPrecioAsync (precios, solo lectura esta slice; el alta real
    // vive en ServicioDePrecios, Slice 3). NumeracionesArticulos NO se expone: su único
    // escritor legítimo (AsignadorDeCodigoInternoArticulo) recibe el WaysDbContext concreto
    // por parámetro, no a través de esta interfaz — mismo criterio que NumeracionesClientes
    // arriba, que sí está expuesto porque InicializadorDeBaseDeDatos lo consume vía esta
    // interfaz para su backfill (caso que Articulo no tiene, al ser additive-only).
    DbSet<Articulo> Articulos { get; }
    DbSet<CodigoBarra> CodigosBarra { get; }
    DbSet<ArticuloEmpresa> ArticulosEmpresas { get; }
    DbSet<Precio> Precios { get; }

    // stage-4-ofertas, Slice 2: primer consumidor de Application — ServicioDeOfertas
    // (list/create/edit/soft-delete + replace-set de ofertas_listas).
    DbSet<Oferta> Ofertas { get; }
    DbSet<OfertaLista> OfertasListas { get; }

    // stage-5-pos-ventas, Slice 4: ServicioDeVentas es el primer consumidor de Application de
    // estos 6 — Slice 3 solo adelantaba el modelo a la migración (design: Table Shapes A/B/C).
    // NumeracionesComprobante sigue sin exponerse acá (ver el comentario de WaysDbContext):
    // AsignadorDeNumeroComprobante recibe el IWaysDbContext por parámetro y opera con ADO.NET
    // crudo, no un DbSet.
    DbSet<ComprobanteVenta> ComprobantesVenta { get; }
    DbSet<ItemComprobanteVenta> ItemsComprobanteVenta { get; }
    DbSet<PagoComprobante> PagosComprobante { get; }
    DbSet<Ways.Domain.Stock.Stock> Stock { get; }
    DbSet<MovimientoStock> MovimientosStock { get; }
    DbSet<MovimientoCuentaCorriente> MovimientosCuentaCorriente { get; }

    // stage-6-turnos-caja, Slice 2: ServicioDeTurnos es el primer consumidor de Application de
    // estos 2 — Slice 1 solo adelantaba el modelo a la migración (design: Table Shapes A/B).
    // ArqueosTurno/MovimientosTesoreria siguen sin exponerse acá: su primer consumidor
    // (ServicioDeTurnos.CerrarAsync) llega en Slice 4.
    DbSet<TurnoCaja> TurnosCaja { get; }
    DbSet<MovimientoCaja> MovimientosCaja { get; }

    // stage-6-turnos-caja, Slice 3: ServicioDeGastos es el primer consumidor de Application de
    // este DbSet — Slice 1 solo adelantaba el modelo a la migración (design: Table Shapes C).
    DbSet<Gasto> Gastos { get; }

    /// <summary>Superficie de transacción/conexión de EF Core (slice 3, tarea 3F,
    /// <c>ServicioDeAprovisionamiento</c>, ADR-16): <c>CreateExecutionStrategy().ExecuteAsync</c>
    /// y <c>BeginTransactionAsync</c> no tienen un equivalente más angosto en este proyecto.
    /// Sigue sin exponer el <see cref="Microsoft.EntityFrameworkCore.DbContext"/> concreto:
    /// <c>DatabaseFacade</c> es la misma abstracción de EF Core que ya expone la superficie
    /// pública de cualquier <c>DbContext</c>, no un tipo de Infrastructure.</summary>
    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
