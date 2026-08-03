using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;

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

    /// <summary>Superficie de transacción/conexión de EF Core (slice 3, tarea 3F,
    /// <c>ServicioDeAprovisionamiento</c>, ADR-16): <c>CreateExecutionStrategy().ExecuteAsync</c>
    /// y <c>BeginTransactionAsync</c> no tienen un equivalente más angosto en este proyecto.
    /// Sigue sin exponer el <see cref="Microsoft.EntityFrameworkCore.DbContext"/> concreto:
    /// <c>DatabaseFacade</c> es la misma abstracción de EF Core que ya expone la superficie
    /// pública de cualquier <c>DbContext</c>, no un tipo de Infrastructure.</summary>
    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
