using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Ofertas;
using Ways.Application.Precios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Ofertas;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Ofertas;

/// <summary>
/// <see cref="ServicioDeOfertas.ResolverConEscalonesAsync"/> sobre el proveedor InMemory (mismo
/// criterio que <see cref="ServicioDeOfertasTests"/>: la resolución es query-only, nunca abre
/// transacción, así que es alcanzable sin Postgres). Cubre el cableado del punto de entrada nuevo —
/// la proyección de la curva a <see cref="EscalonDeCantidad"/> y que el resultado plano siga siendo
/// EXACTAMENTE el de <see cref="ServicioDeOfertas.ResolverAsync"/>. La lógica de escalones en sí
/// (descubrimiento, orden, colapso) se prueba pura en <c>EscalonesDeCantidadTests</c>
/// (Ways.Domain.Tests); el guard de "cero consultas extra" vive en <c>OfertasResolucionTests</c>
/// (Postgres real, es el único lugar donde hay un <c>DbCommandInterceptor</c> que contar).
/// </summary>
public class ResolucionConEscalonesTests
{
    private const int IdTenant = 1;
    private static readonly DateTimeOffset Ahora = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo : IRelojDelSistema
    {
        public DateTimeOffset Ahora => ResolucionConEscalonesTests.Ahora;
    }

    private sealed class ContextoFijo : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant => ResolucionConEscalonesTests.IdTenant;
    }

    private static WaysDbContext CrearContexto(string nombreDeBase, ITenantActual tenantActual) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options, tenantActual);

    private static ServicioDeOfertas CrearServicio(string nombreDeBase)
    {
        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, IdTenant);

        return new ServicioDeOfertas(
            CrearContexto(nombreDeBase, tenantActual),
            new RelojFijo(),
            new ContextoFijo(),
            new ServicioDePrecios(CrearContexto(nombreDeBase, tenantActual), new RelojFijo(), new ContextoFijo()));
    }

    /// <summary>Lista fija + artículo + precio vigente: lo mínimo para que la resolución tenga un
    /// <c>PrecioOriginal</c> sobre el que descontar.</summary>
    private static async Task<(int IdArticulo, int IdLista)> SembrarArticuloConPrecioAsync(
        string nombreDeBase, decimal precio)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var lista = new ListaPrecio
        {
            IdTenant = IdTenant, Nombre = "General", EsDefault = true, Modo = ModoLista.Fija,
            CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.ListasPrecio.Add(lista);

        var articulo = new Articulo
        {
            IdTenant = IdTenant, CodigoInterno = $"art-{Guid.NewGuid():N}", Nombre = "Artículo",
            IdArea = 1, IdAlicuotaIva = 1, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            Activo = true, CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.Articulos.Add(articulo);
        await siembra.SaveChangesAsync();

        siembra.Precios.Add(new Precio
        {
            IdTenant = IdTenant, IdArticulo = articulo.Id, IdListaPrecio = lista.Id, Monto = precio,
            VigenteDesde = Ahora.AddDays(-1), VigenteHasta = null, CreatedAt = Ahora, UpdatedAt = Ahora
        });
        await siembra.SaveChangesAsync();

        return (articulo.Id, lista.Id);
    }

    private static async Task<int> SembrarOfertaAsync(
        string nombreDeBase, int idArticulo, decimal porcentaje, decimal? cantidadMinima)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);

        var oferta = new Oferta
        {
            IdTenant = IdTenant,
            Nombre = $"Volumen {cantidadMinima}",
            IdArticulo = idArticulo,
            Porcentaje = porcentaje,
            CantidadMinima = cantidadMinima,
            Activo = true,
            CreatedAt = Ahora,
            UpdatedAt = Ahora
        };
        siembra.Ofertas.Add(oferta);
        await siembra.SaveChangesAsync();

        return oferta.Id;
    }

    /// <summary>El cableado completo: el resultado plano sigue siendo el de cantidad 1 (sin
    /// descuento, porque la oferta arranca en 6) y la curva viaja aparte, con el precio final y la
    /// oferta aplicada YA resueltos por el motor.</summary>
    [Fact]
    public async Task LaCurvaViajaConElEscalonResueltoYElResultadoPlanoSigueSiendoElDeCantidadUno()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArticulo, idLista) = await SembrarArticuloConPrecioAsync(nombreDeBase, precio: 100m);
        var idOferta = await SembrarOfertaAsync(nombreDeBase, idArticulo, porcentaje: 20m, cantidadMinima: 6m);
        var servicio = CrearServicio(nombreDeBase);

        var resolucion = await servicio.ResolverConEscalonesAsync(
            [new LineaDeResolucion(idArticulo, null, idLista, 1m)], momento: null);

        var linea = Assert.Single(resolucion);
        Assert.Equal(100m, linea.Resultado.PrecioOriginal);
        Assert.Equal(100m, linea.Resultado.PrecioFinal);
        Assert.Equal(0m, linea.Resultado.DescuentoUnitario);
        Assert.Empty(linea.Resultado.Aplicadas);

        var escalon = Assert.Single(linea.Escalones);
        Assert.Equal(6m, escalon.CantidadDesde);
        Assert.Equal(20m, escalon.DescuentoUnitario);
        Assert.Equal(80m, escalon.PrecioFinal);
        var aplicada = Assert.Single(escalon.Aplicadas);
        Assert.Equal(idOferta, aplicada.IdOferta);
        Assert.Equal("Volumen 6", aplicada.Nombre);
        Assert.Equal(20m, aplicada.DescuentoUnitario);
    }

    /// <summary>Una oferta directa (sin <c>cantidad_minima</c>) ya vive en el resultado plano: no
    /// genera curva. La lista de escalones sale VACÍA — el <c>null</c> que viaja en el JSON de la
    /// instantánea lo decide <c>ServicioDeInstantaneaDePos</c>, no este servicio.</summary>
    [Fact]
    public async Task UnaOfertaSinCantidadMinimaNoGeneraCurva()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArticulo, idLista) = await SembrarArticuloConPrecioAsync(nombreDeBase, precio: 100m);
        await SembrarOfertaAsync(nombreDeBase, idArticulo, porcentaje: 10m, cantidadMinima: null);
        var servicio = CrearServicio(nombreDeBase);

        var resolucion = await servicio.ResolverConEscalonesAsync(
            [new LineaDeResolucion(idArticulo, null, idLista, 1m)], momento: null);

        var linea = Assert.Single(resolucion);
        Assert.Equal(10m, linea.Resultado.DescuentoUnitario);
        Assert.Empty(linea.Escalones);
    }

    /// <summary>El refactor que extrajo el contexto compartido no movió el resultado plano:
    /// <see cref="ServicioDeOfertas.ResolverAsync"/> y
    /// <see cref="ServicioDeOfertas.ResolverConEscalonesAsync"/> devuelven lo mismo, campo por
    /// campo, para el MISMO lote — incluida la línea sin precio vigente (lista existente, artículo
    /// sin fila en <c>precios</c>: los dos precios en <c>null</c> y sin aplicadas).</summary>
    [Fact]
    public async Task LosDosPuntosDeEntradaDevuelvenElMismoResultadoPlano()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var (idArticulo, idLista) = await SembrarArticuloConPrecioAsync(nombreDeBase, precio: 100m);
        await SembrarOfertaAsync(nombreDeBase, idArticulo, porcentaje: 20m, cantidadMinima: 6m);
        await SembrarOfertaAsync(nombreDeBase, idArticulo, porcentaje: 5m, cantidadMinima: null);

        int idArticuloSinPrecio;
        await using (var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma))
        {
            var articulo = new Articulo
            {
                IdTenant = IdTenant, CodigoInterno = $"art-{Guid.NewGuid():N}", Nombre = "Sin precio",
                IdArea = 1, IdAlicuotaIva = 1, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
                Activo = true, CreatedAt = Ahora, UpdatedAt = Ahora
            };
            siembra.Articulos.Add(articulo);
            await siembra.SaveChangesAsync();
            idArticuloSinPrecio = articulo.Id;
        }

        List<LineaDeResolucion> lineas =
        [
            new(idArticulo, null, idLista, 1m),
            new(idArticuloSinPrecio, null, idLista, 1m)
        ];

        var plano = await CrearServicio(nombreDeBase).ResolverAsync(lineas, momento: null);
        var conEscalones = await CrearServicio(nombreDeBase).ResolverConEscalonesAsync(lineas, momento: null);

        Assert.Equal(lineas.Count, plano.Count);
        Assert.Equal(plano.Count, conEscalones.Count);

        for (var i = 0; i < plano.Count; i++)
        {
            var esperado = plano[i];
            var obtenido = conEscalones[i].Resultado;

            Assert.Equal(esperado.IdArticulo, obtenido.IdArticulo);
            Assert.Equal(esperado.IdListaPrecio, obtenido.IdListaPrecio);
            Assert.Equal(esperado.PrecioOriginal, obtenido.PrecioOriginal);
            Assert.Equal(esperado.PrecioFinal, obtenido.PrecioFinal);
            Assert.Equal(esperado.DescuentoUnitario, obtenido.DescuentoUnitario);
            Assert.Equal(
                esperado.Aplicadas.Select(a => (a.IdOferta, a.Nombre, a.DescuentoUnitario)),
                obtenido.Aplicadas.Select(a => (a.IdOferta, a.Nombre, a.DescuentoUnitario)));
        }

        // La línea con precio sí tiene curva; la que no tiene precio nunca la tiene (no hay nada
        // sobre lo que descontar).
        Assert.Equal(6m, Assert.Single(conEscalones[0].Escalones).CantidadDesde);
        Assert.Null(conEscalones[1].Resultado.PrecioOriginal);
        Assert.Empty(conEscalones[1].Escalones);
    }

    /// <summary>Lote vacío: los dos puntos de entrada cortan ANTES de consultar nada (un catálogo
    /// sin artículos activos no dispara ninguna consulta de resolución).</summary>
    [Fact]
    public async Task UnLoteVacioResuelveVacioEnLosDosPuntosDeEntrada()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var servicio = CrearServicio(nombreDeBase);

        Assert.Empty(await servicio.ResolverAsync([], momento: null));
        Assert.Empty(await servicio.ResolverConEscalonesAsync([], momento: null));
    }

    /// <summary>La guarda compartida <c>ExigirLineas</c>: <c>lineas</c> ausente o <c>null</c> en el
    /// body es 400 <c>lineas_requeridas</c>, no un lote vacío legítimo — en los DOS puntos de
    /// entrada.</summary>
    [Fact]
    public async Task UnLoteNuloEsRechazadoEnLosDosPuntosDeEntrada()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var servicio = CrearServicio(nombreDeBase);

        var plano = await Assert.ThrowsAsync<ErrorDominio>(() => servicio.ResolverAsync(null, momento: null));
        var conEscalones = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.ResolverConEscalonesAsync(null, momento: null));

        Assert.Equal("lineas_requeridas", plano.Codigo);
        Assert.Equal("lineas_requeridas", conEscalones.Codigo);
    }
}
