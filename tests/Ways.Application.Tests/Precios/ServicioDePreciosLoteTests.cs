using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Precios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Precios;

/// <summary>
/// stage-4-ofertas, Slice 3 (task 3.10, design: Testing Strategy — "Integration (parity)"; spec:
/// precios / Batch Current-Price Resolution) — <see cref="ServicioDePrecios.PreciosVigentesEnLoteAsync"/>
/// tiene que devolver EXACTAMENTE lo mismo que <see cref="ServicioDePrecios.PrecioVigenteAsync"/>
/// pareja por pareja, para fija con precio, fija sin precio, derivada, y lista inactiva (misma
/// semántica explícita-por-id que <see cref="ServicioDePrecios.PrecioVigenteAsync"/>, sin filtro
/// de <c>Activo</c> — divergencia documentada en ambos métodos). Corre contra el proveedor
/// InMemory: ninguno de los dos caminos (lote/single) abre transacción.
/// </summary>
public class ServicioDePreciosLoteTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
    private const int IdTenant = 1;

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int? idTenant) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options, tenantActual);

    private static ServicioDePrecios CrearServicio(string nombreDeBase) =>
        new(
            CrearContexto(nombreDeBase, new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant)),
            new RelojFijo(Ahora),
            new ContextoFijo(IdTenant));

    private static async Task<int> SembrarArticuloAsync(string nombreDeBase, string nombre)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var articulo = new Articulo
        {
            IdTenant = IdTenant,
            CodigoInterno = nombre,
            Nombre = nombre,
            IdArea = 1,
            IdAlicuotaIva = 1,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = true,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };
        siembra.Articulos.Add(articulo);
        await siembra.SaveChangesAsync();
        return articulo.Id;
    }

    private static async Task<int> SembrarListaFijaAsync(string nombreDeBase, string nombre, bool activo = true)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var lista = new ListaPrecio
        {
            IdTenant = IdTenant, Nombre = nombre, EsDefault = false, Modo = ModoLista.Fija, Activo = activo,
            CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.ListasPrecio.Add(lista);
        await siembra.SaveChangesAsync();
        return lista.Id;
    }

    private static async Task<int> SembrarListaDerivadaAsync(
        string nombreDeBase, string nombre, int idListaBase, decimal porcentaje)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var lista = new ListaPrecio
        {
            IdTenant = IdTenant, Nombre = nombre, EsDefault = false, Modo = ModoLista.Derivada,
            IdListaBase = idListaBase, Porcentaje = porcentaje, CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.ListasPrecio.Add(lista);
        await siembra.SaveChangesAsync();
        return lista.Id;
    }

    private static async Task SembrarPrecioAsync(
        string nombreDeBase, int idArticulo, int idListaPrecio, decimal monto)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        siembra.Precios.Add(new Precio
        {
            IdTenant = IdTenant,
            IdArticulo = idArticulo,
            IdListaPrecio = idListaPrecio,
            Monto = monto,
            VigenteDesde = Ahora.AddDays(-1),
            VigenteHasta = null,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        });
        await siembra.SaveChangesAsync();
    }

    /// <summary>Cubre las 4 combinaciones del design (fija con precio, fija sin precio,
    /// derivada, lista inactiva) en un solo lote — el lote y el camino single tienen que
    /// coincidir en TODAS.</summary>
    [Fact]
    public async Task PreciosVigentesEnLoteCoincideConPrecioVigenteParaCadaPar()
    {
        var nombreDeBase = nameof(PreciosVigentesEnLoteCoincideConPrecioVigenteParaCadaPar);

        var idArticuloConPrecio = await SembrarArticuloAsync(nombreDeBase, "con-precio");
        var idArticuloSinPrecio = await SembrarArticuloAsync(nombreDeBase, "sin-precio");

        var idListaFija = await SembrarListaFijaAsync(nombreDeBase, "General");
        var idListaInactiva = await SembrarListaFijaAsync(nombreDeBase, "Inactiva", activo: false);
        var idListaDerivada = await SembrarListaDerivadaAsync(nombreDeBase, "Derivada", idListaFija, porcentaje: -10m);

        await SembrarPrecioAsync(nombreDeBase, idArticuloConPrecio, idListaFija, 100m);
        await SembrarPrecioAsync(nombreDeBase, idArticuloConPrecio, idListaInactiva, 50m);

        var idsArticulo = new[] { idArticuloConPrecio, idArticuloSinPrecio };
        var idsListaPrecio = new[] { idListaFija, idListaInactiva, idListaDerivada };

        var servicioLote = CrearServicio(nombreDeBase);
        var lote = await servicioLote.PreciosVigentesEnLoteAsync(idsArticulo, idsListaPrecio, Ahora);

        foreach (var idArticulo in idsArticulo)
        {
            foreach (var idListaPrecio in idsListaPrecio)
            {
                var servicioSingle = CrearServicio(nombreDeBase);
                var single = await servicioSingle.PrecioVigenteAsync(idArticulo, idListaPrecio, Ahora);

                Assert.True(
                    lote.TryGetValue((idArticulo, idListaPrecio), out var precioDelLote),
                    $"Falta el par ({idArticulo}, {idListaPrecio}) en el resultado del lote.");
                Assert.Equal(single.Precio, precioDelLote);
            }
        }

        // Aserciones puntuales, para que un futuro cambio que rompa la paridad falle acá con un
        // mensaje concreto, no solo en el loop genérico de arriba.
        Assert.Equal(100m, lote[(idArticuloConPrecio, idListaFija)]);
        Assert.Null(lote[(idArticuloSinPrecio, idListaFija)]);
        Assert.Equal(90m, lote[(idArticuloConPrecio, idListaDerivada)]);
        Assert.Equal(50m, lote[(idArticuloConPrecio, idListaInactiva)]);
    }

    /// <summary>Spec: precios / "Existing single-articulo methods are unaffected" — el método
    /// nuevo es aditivo, <see cref="ServicioDePrecios.PrecioVigenteAsync"/> sigue funcionando
    /// exactamente igual sin pasar por el lote.</summary>
    [Fact]
    public async Task PrecioVigenteAsyncSigueFuncionandoIgualSinPasarPorElLote()
    {
        var nombreDeBase = nameof(PrecioVigenteAsyncSigueFuncionandoIgualSinPasarPorElLote);
        var idArticulo = await SembrarArticuloAsync(nombreDeBase, "articulo");
        var idLista = await SembrarListaFijaAsync(nombreDeBase, "General");
        await SembrarPrecioAsync(nombreDeBase, idArticulo, idLista, 55m);

        var servicio = CrearServicio(nombreDeBase);
        var resultado = await servicio.PrecioVigenteAsync(idArticulo, idLista, Ahora);

        Assert.Equal(55m, resultado.Precio);
    }

    [Fact]
    public async Task PreciosVigentesEnLoteConSetsVaciosDevuelveVacioSinConsultar()
    {
        var nombreDeBase = nameof(PreciosVigentesEnLoteConSetsVaciosDevuelveVacioSinConsultar);
        var servicio = CrearServicio(nombreDeBase);

        var resultado = await servicio.PreciosVigentesEnLoteAsync([], [1], Ahora);

        Assert.Empty(resultado);
    }
}
