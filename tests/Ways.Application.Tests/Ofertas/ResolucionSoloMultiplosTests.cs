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
/// Ofertas "solo múltiplos" en <see cref="ServicioDeOfertas"/> sobre InMemory (la resolución es query-only): el descuento
/// de línea de la resolución, el conteo por familia en un documento y por línea sin documento, la curva de la
/// instantánea que las deja afuera, y la validación del alta y de la edición. Artículo a 4000 con "2 x 7500".
/// </summary>
public class ResolucionSoloMultiplosTests
{
    private const int IdTenant = 1;
    private const decimal Lista = 4000m;
    private static readonly DateTimeOffset Ahora = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class RelojFijo : IRelojDelSistema
    {
        public DateTimeOffset Ahora => ResolucionSoloMultiplosTests.Ahora;
    }

    private sealed class ContextoFijo : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 999;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant => ResolucionSoloMultiplosTests.IdTenant;
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

    private static async Task<int> SembrarListaAsync(string nombreDeBase)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);
        var lista = new ListaPrecio
        {
            IdTenant = IdTenant, Nombre = "General", EsDefault = true, Modo = ModoLista.Fija,
            CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.ListasPrecio.Add(lista);
        await siembra.SaveChangesAsync();

        return lista.Id;
    }

    private static async Task<int> SembrarArticuloAsync(string nombreDeBase, int idLista, int? idFamilia = null)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);
        var articulo = new Articulo
        {
            IdTenant = IdTenant, CodigoInterno = $"art-{Guid.NewGuid():N}", Nombre = "Artículo",
            IdArea = 1, IdAlicuotaIva = 1, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            Activo = true, IdFamilia = idFamilia, CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.Articulos.Add(articulo);
        await siembra.SaveChangesAsync();

        siembra.Precios.Add(new Precio
        {
            IdTenant = IdTenant, IdArticulo = articulo.Id, IdListaPrecio = idLista, Monto = Lista,
            VigenteDesde = Ahora.AddDays(-1), VigenteHasta = null, CreatedAt = Ahora, UpdatedAt = Ahora
        });
        await siembra.SaveChangesAsync();

        return articulo.Id;
    }

    private static async Task<int> SembrarOfertaAsync(
        string nombreDeBase, bool soloMultiplos, int? idArticulo = null, int? idFamilia = null)
    {
        await using var siembra = CrearContexto(nombreDeBase, TenantActualFijo.Plataforma);
        var oferta = new Oferta
        {
            IdTenant = IdTenant, Nombre = "2 x 7500", IdArticulo = idArticulo, IdFamilia = idFamilia,
            PrecioUnitario = 3750m, CantidadMinima = 2m, SoloMultiplos = soloMultiplos, Activo = true,
            CreatedAt = Ahora, UpdatedAt = Ahora
        };
        siembra.Ofertas.Add(oferta);
        await siembra.SaveChangesAsync();

        return oferta.Id;
    }

    private static List<LineaDeResolucion> Lineas(int idLista, params (int IdArticulo, decimal Cantidad)[] lineas) =>
        [.. lineas.Select(l => new LineaDeResolucion(l.IdArticulo, null, idLista, l.Cantidad))];

    [Fact]
    public async Task ElDocumentoInformaElDescuentoDeLineaDeLosGruposCompletosYNadaEnUnaOfertaComun()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaAsync(nombreDeBase);
        var estricto = await SembrarArticuloAsync(nombreDeBase, idLista);
        var comun = await SembrarArticuloAsync(nombreDeBase, idLista);
        await SembrarOfertaAsync(nombreDeBase, soloMultiplos: true, idArticulo: estricto);
        await SembrarOfertaAsync(nombreDeBase, soloMultiplos: false, idArticulo: comun);

        var resultado = await CrearServicio(nombreDeBase).ResolverDocumentoAsync(
            Lineas(idLista, (estricto, 3m), (comun, 3m)), momento: null);

        Assert.Equal(500m, resultado[0].DescuentoDeLinea);
        Assert.Equal(250m, resultado[0].DescuentoUnitario);
        Assert.Equal(2m, Assert.Single(resultado[0].Aplicadas).MultiploDe);
        Assert.Null(resultado[1].DescuentoDeLinea);
        Assert.Null(Assert.Single(resultado[1].Aplicadas).MultiploDe);
    }

    [Fact]
    public async Task UnDocumentoCuentaLosGruposDeFamiliaSobreTodasSusLineas()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaAsync(nombreDeBase);
        var a = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        var b = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        await SembrarOfertaAsync(nombreDeBase, soloMultiplos: true, idFamilia: 7);

        var resultado = await CrearServicio(nombreDeBase).ResolverDocumentoAsync(
            Lineas(idLista, (a, 1m), (b, 2m)), momento: null);

        Assert.Equal([250m, 250m], resultado.Select(r => r.DescuentoDeLinea));
        Assert.True(resultado[0].Aplicadas[0].PorFamilia);
    }

    [Fact]
    public async Task SinDocumentoCadaLineaCuentaSusPropiosGruposDeFamilia()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaAsync(nombreDeBase);
        var a = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        var b = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        await SembrarOfertaAsync(nombreDeBase, soloMultiplos: true, idFamilia: 7);

        var resultado = await CrearServicio(nombreDeBase).ResolverPorLineaAsync(
            Lineas(idLista, (a, 3m), (b, 2m)), momento: null);

        Assert.Equal([500m, 500m], resultado.Select(r => r.DescuentoDeLinea));
    }

    [Fact]
    public void ElDescuentoDeLineaUsaLasUnidadesQueLePasan()
    {
        ResultadoDeResolucion[] resultados =
        [
            new(1, 1, Lista, 3750m, 250m, [new OfertaAplicadaDto(1, "2 x 7500", 250m, MultiploDe: 2m)]),
            new(2, 1, Lista, 3750m, 250m, [new OfertaAplicadaDto(2, "comun", 250m)]),
            new(3, 1, null, null, 0m, []),
        ];

        Assert.Equal([500m, null, null], ServicioDeOfertas.DescuentosDeLineaSoloMultiplos(resultados, [3m, 3m, 3m]));
        Assert.Equal([0m, null, null], ServicioDeOfertas.DescuentosDeLineaSoloMultiplos(resultados, [1m, 3m, 3m]));
    }

    /// <summary>La curva de la instantánea lleva la oferta "solo múltiplos" en <c>AplicadasCompletas</c> y los campos de
    /// siempre quedan con las ofertas comunes (ninguna acá: precio de lista), así un POS anterior no la cobra en las
    /// unidades que no completan un grupo. Una oferta común no cambia: sin <c>AplicadasCompletas</c>.</summary>
    [Fact]
    public async Task LaCurvaDeLaInstantaneaLlevaLaOfertaSoloMultiplosAparteDeLosCamposDeSiempre()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaAsync(nombreDeBase);
        var estricto = await SembrarArticuloAsync(nombreDeBase, idLista);
        var comun = await SembrarArticuloAsync(nombreDeBase, idLista);
        var deFamilia = await SembrarArticuloAsync(nombreDeBase, idLista, idFamilia: 7);
        var idEstricta = await SembrarOfertaAsync(nombreDeBase, soloMultiplos: true, idArticulo: estricto);
        await SembrarOfertaAsync(nombreDeBase, soloMultiplos: false, idArticulo: comun);
        var idDeFamilia = await SembrarOfertaAsync(nombreDeBase, soloMultiplos: true, idFamilia: 7);

        var resolucion = await CrearServicio(nombreDeBase).ResolverConEscalonesAsync(
            Lineas(idLista, (estricto, 1m), (comun, 1m), (deFamilia, 1m)), momento: null);

        var escalonEstricto = Assert.Single(resolucion[0].Escalones);
        Assert.Equal(2m, escalonEstricto.CantidadDesde);
        Assert.Equal(Lista, escalonEstricto.PrecioFinal);
        Assert.Equal(0m, escalonEstricto.DescuentoUnitario);
        Assert.Empty(escalonEstricto.Aplicadas);
        var estricta = Assert.Single(escalonEstricto.AplicadasCompletas!);
        Assert.Equal((idEstricta, 250m, 2m, false), (estricta.IdOferta, estricta.DescuentoUnitario, estricta.MultiploDe, estricta.PorFamilia));

        var escalonComun = Assert.Single(resolucion[1].Escalones);
        Assert.Equal((2m, 3750m, 250m), (escalonComun.CantidadDesde, escalonComun.PrecioFinal, escalonComun.DescuentoUnitario));
        Assert.Null(escalonComun.AplicadasCompletas);

        var celda = Assert.Single(resolucion[2].EscalonesDeFamilia!);
        Assert.Equal((2m, 1m, Lista, 0m), (celda.CantidadDeFamiliaDesde, celda.CantidadDesde, celda.PrecioFinal, celda.DescuentoUnitario));
        Assert.Empty(celda.Aplicadas);
        var deLaFamilia = Assert.Single(celda.AplicadasCompletas!);
        Assert.Equal((idDeFamilia, 2m, true), (deLaFamilia.IdOferta, deLaFamilia.MultiploDe, deLaFamilia.PorFamilia));
        Assert.Equal(idDeFamilia, Assert.Single(Assert.Single(resolucion[2].Escalones).AplicadasCompletas!).IdOferta);
    }

    /// <summary>La parte común lleva el tope del motor (la suma entre cero y el precio original) y las completas conservan
    /// el orden del servidor; sin ninguna "solo múltiplos" la resolución pasa tal cual.</summary>
    [Fact]
    public void SepararDejaEnLosCamposDeSiempreSoloLasOfertasComunes()
    {
        OfertaAplicadaDto[] aplicadas =
        [
            new(1, "estricta", 60m, MultiploDe: 2m),
            new(2, "comun", 70m),
            new(3, "acumulable", 50m),
        ];

        var separado = PrecioParaInstantanea.Separar(100m, 0m, 100m, aplicadas);

        Assert.Equal((0m, 100m), (separado.PrecioFinal, separado.DescuentoUnitario));
        Assert.Equal([2, 3], separado.Aplicadas.Select(a => a.IdOferta));
        Assert.Equal([1, 2, 3], separado.AplicadasCompletas!.Select(a => a.IdOferta));

        var sinEstricta = PrecioParaInstantanea.Separar(100m, 30m, 70m, [aplicadas[1]]);
        Assert.Equal((30m, 70m), (sinEstricta.PrecioFinal, sinEstricta.DescuentoUnitario));
        Assert.Null(sinEstricta.AplicadasCompletas);
    }

    private static AltaOferta Alta(int idArticulo, decimal? cantidadMinima, bool soloMultiplos) =>
        new("2 x 7500", null, idArticulo, null, null, null, null, null, null, null, cantidadMinima, 3750m, null, null,
            0, false, SoloMultiplos: soloMultiplos);

    [Fact]
    public async Task ElAltaRechazaSoloMultiplosSinCantidadMinima()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaAsync(nombreDeBase);
        var articulo = await SembrarArticuloAsync(nombreDeBase, idLista);

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => CrearServicio(nombreDeBase).CrearAsync(Alta(articulo, null, soloMultiplos: true)));

        Assert.Equal("solo_multiplos_sin_cantidad_minima", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public async Task EditarUnaOfertaInexistenteConSoloMultiplosInvalidoDa404()
    {
        var nombreDeBase = Guid.NewGuid().ToString();
        var idLista = await SembrarListaAsync(nombreDeBase);
        var articulo = await SembrarArticuloAsync(nombreDeBase, idLista);

        var error = await Assert.ThrowsAsync<ErrorDominio>(() => CrearServicio(nombreDeBase).ActualizarAsync(
            12345,
            new EdicionOferta(
                "2 x 7500", null, articulo, null, null, null, null, null, null, null, null, 3750m, null, null, 0, false,
                null, true, SoloMultiplos: true)));

        Assert.Equal(404, error.EstadoHttp);
    }
}
