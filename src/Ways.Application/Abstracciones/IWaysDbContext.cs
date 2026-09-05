using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Ways.Application.Organizacion;
using Ways.Domain.Articulos;
using Ways.Domain.Auditoria;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Fiscal;
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
    DbSet<TurnoCaja> TurnosCaja { get; }
    DbSet<MovimientoCaja> MovimientosCaja { get; }

    // stage-6-turnos-caja, Slice 4: ServicioDeTurnos.CerrarAsync es el primer consumidor de
    // Application de estos 2 — Slice 1 solo adelantaba el modelo a la migración (design: Table
    // Shapes A/D). ArqueosTurno es append-only (una fila por medio arqueable, escrita una sola
    // vez al cierre); MovimientosTesoreria encadena su Inicio desde el Final de la última fila
    // del mismo punto de venta.
    DbSet<ArqueoTurno> ArqueosTurno { get; }
    DbSet<MovimientoTesoreria> MovimientosTesoreria { get; }

    // stage-6-turnos-caja, Slice 3: ServicioDeGastos es el primer consumidor de Application de
    // este DbSet — Slice 1 solo adelantaba el modelo a la migración (design: Table Shapes C).
    DbSet<Gasto> Gastos { get; }

    // stage-8-compras-transferencias-inventario, Slice 2: ServicioDeCompras es el primer
    // consumidor de Application de estos 2 — Slice 1 solo adelantaba el modelo a la migración
    // (design: Table Shapes A/B).
    DbSet<ComprobanteCompra> ComprobantesCompra { get; }
    DbSet<ItemComprobanteCompra> ItemsComprobanteCompra { get; }

    // stage-12-lotes-vencimientos, Slice 3: ServicioDeLotes es el primer consumidor de
    // Application de estos 2 — Slice 1 solo adelantaba el modelo a la migración (proposal gate
    // §A/§B).
    DbSet<Lote> Lotes { get; }
    DbSet<StockLote> StockLotes { get; }

    // stage-14-auditoria-trazabilidad, Slice 1: ServicioDeAuditoria.Registrar es el primer
    // (y único, en esta slice) escritor — nadie más consume este DbSet todavía (slices 2-4).
    // Nombre totalmente calificado, mismo motivo que Ways.Domain.Stock.Stock arriba: la
    // propiedad "Auditoria" colisionaría con el tipo del mismo nombre del namespace homónimo.
    DbSet<Ways.Domain.Auditoria.Auditoria> Auditoria { get; }

    // stage-15-cc-proveedores-ledger, Slice 1: la fixture de fidelidad del backfill (task 1.20)
    // es el primer consumidor de Application de este DbSet — EscriturasDeCuentaCorrienteProveedor
    // (slice 2) no lo necesita (opera raw-ADO, mismo criterio que EscriturasDeCuentaCorriente).
    DbSet<MovimientoCuentaCorrienteProveedor> MovimientosCuentaCorrienteProveedor { get; }

    // stage-16-ordenes-de-compra, Slice 2: ServicioDeOrdenesDeCompra es el primer consumidor de
    // Application de estos 2 — Slice 1 solo adelanta el modelo a la migración (proposal §B/§C).
    DbSet<OrdenCompra> OrdenesCompra { get; }
    DbSet<ItemOrdenCompra> ItemsOrdenCompra { get; }

    // stage-17-presupuestos-y-remitos, Slice 1 (task 1.17, design.md:448): expuestos desde esta
    // slice — a diferencia del "modelo adelantado a la migración" de OrdenCompra/ItemOrdenCompra
    // arriba (sin consumidor de Application en su propio lote), acá el task list del slice pide
    // explícitamente los dos `DbSet` en esta interfaz ya en slice 1. `ServicioDePresupuestos`
    // (slice 2) es el primer consumidor real.
    DbSet<Presupuesto> Presupuestos { get; }
    DbSet<ItemPresupuesto> ItemsPresupuesto { get; }

    // stage-17-presupuestos-y-remitos, Slice 4 (task 4.18, design.md:448): expuestos desde esta
    // slice, mismo criterio que Presupuesto/ItemPresupuesto (task 1.17) — ServicioDeRemitos
    // (slice 5) es el primer consumidor real.
    DbSet<Remito> Remitos { get; }
    DbSet<ItemRemito> ItemsRemito { get; }

    // stage-19a-slice1 (schema fiscal, DB CHANGE GATE ratificado): expuesto desde esta slice —
    // ServicioDeCertificados (slice 4) es el primer consumidor de Application. NumeracionFiscal
    // sigue sin exponerse acá (ver el comentario de WaysDbContext): AsignadorDeNumeroFiscal
    // (slice 4) recibe el IWaysDbContext concreto por parámetro y opera con ADO.NET crudo.
    DbSet<CertificadoFiscal> CertificadosFiscales { get; }

    /// <summary>Superficie de transacción/conexión de EF Core (slice 3, tarea 3F,
    /// <c>ServicioDeAprovisionamiento</c>, ADR-16): <c>CreateExecutionStrategy().ExecuteAsync</c>
    /// y <c>BeginTransactionAsync</c> no tienen un equivalente más angosto en este proyecto.
    /// Sigue sin exponer el <see cref="Microsoft.EntityFrameworkCore.DbContext"/> concreto:
    /// <c>DatabaseFacade</c> es la misma abstracción de EF Core que ya expone la superficie
    /// pública de cualquier <c>DbContext</c>, no un tipo de Infrastructure.</summary>
    DatabaseFacade Database { get; }

    /// <summary>Metadata del modelo (stage-20 slice 3, D1): <see cref="InspectorDeUso"/> arma
    /// su statement recorriendo <c>GetReferencingForeignKeys()</c>, que es la única fuente
    /// posible del conjunto de dependientes — cualquier lista escrita a mano se desactualiza en
    /// silencio. Mismo criterio que <c>Database</c> arriba: <c>IModel</c> es la misma abstracción
    /// de EF Core que ya expone la superficie pública de cualquier <c>DbContext</c>, no un tipo
    /// de Infrastructure, y <c>DbContext.Model</c> la satisface implícitamente — la interfaz gana
    /// una línea y ninguna implementación cambia.</summary>
    IModel Model { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
