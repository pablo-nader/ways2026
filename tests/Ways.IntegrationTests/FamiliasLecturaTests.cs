using System.Net;
using System.Net.Http.Json;
using Ways.Application.Familias;
using Ways.Domain.Articulos;
using Ways.Domain.Precios;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>GET /api/familias</c> y <c>GET /api/familias/{id}</c> (doc 10 §3, "Familias de artículos") contra Postgres
/// real: el listado de las familias vivas con la cantidad de miembros vivos, y el detalle con lo que prellena el
/// alta de un artículo dentro de la familia —los miembros, los trece valores compartidos y el estado de precios
/// del artículo de referencia en cada lista fija—.
///
/// <para>La familia del detalle se siembra con la invariante ROTA a propósito en lo que no se lee: el miembro
/// dado de baja y <c>m2</c> tienen otros valores y otros precios, y <c>m3</c> no tiene precios, así leer el
/// artículo equivocado es observable. Los miembros se siembran con id explícito, en orden inverso y con códigos
/// internos que ordenan al revés de los ids: ni el orden físico de las filas ni el del índice único del código
/// coinciden con el de los ids.</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FamiliasLecturaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    /// <summary>Ids de artículo explícitos en una zona que el identity no alcanza: la clave primaria es global a
    /// todos los tenants de la base.</summary>
    private static int siguienteIdDeArticulo = 6_100_000;

    private static int[] IdsAscendentes(int cantidad)
    {
        var primero = Interlocked.Add(ref siguienteIdDeArticulo, cantidad + 1) - cantidad;

        return [.. Enumerable.Range(primero, cantidad)];
    }

    /// <summary><c>timestamptz</c> guarda microsegundos y <see cref="DateTimeOffset"/> tiene 100 ns de resolución.</summary>
    private static DateTimeOffset AMicrosegundos(DateTimeOffset instante) =>
        new(instante.Ticks - (instante.Ticks % (TimeSpan.TicksPerMillisecond / 1000)), instante.Offset);

    private static Task<HttpResponseMessage> GetListadoAsync(HttpClient cliente) => cliente.GetAsync("/api/familias");

    private static Task<HttpResponseMessage> GetDetalleAsync(HttpClient cliente, int id) =>
        cliente.GetAsync($"/api/familias/{id}");

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta)
    {
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        return (await respuesta.Content.ReadFromJsonAsync<T>(OpcionesJson))!;
    }

    // =================================================================================================
    // GET /api/familias
    // =================================================================================================

    /// <summary>El listado trae SOLO las familias vivas del tenant, con la cantidad de miembros vivos —un miembro
    /// dado de baja no cuenta—, ordenadas por nombre sin distinguir mayúsculas. Se siembran por orden de id
    /// distinto del orden de nombre, una familia inactiva (se lista igual, con <c>activo: false</c>), una vacía, una
    /// dada de baja con un artículo vivo apuntándole y la de otro tenant.</summary>
    [Fact]
    public async Task ElListadoTraeLasFamiliasVivasDelTenantPorNombreConLaCantidadDeMiembrosVivos()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElListadoTraeLasFamiliasVivasDelTenantPorNombreConLaCantidadDeMiembrosVivos));
        using var otro = await apoyo.PrepararAsync(nameof(ElListadoTraeLasFamiliasVivasDelTenantPorNombreConLaCantidadDeMiembrosVivos) + "-ajeno");

        var zeta = await apoyo.SembrarFamiliaAsync(e, "Zeta");
        var alfa = await apoyo.SembrarFamiliaAsync(e, "alfa", activa: false);
        var beta = await apoyo.SembrarFamiliaAsync(e, "Beta");
        var dadaDeBaja = await apoyo.SembrarFamiliaAsync(e, "Aaa dada de baja", dadaDeBaja: true);
        await apoyo.SembrarFamiliaAsync(otro, "Ajena");

        await apoyo.SembrarArticuloAsync(e, "z1", ValoresBase(e), zeta);
        await apoyo.SembrarArticuloAsync(e, "z2", ValoresBase(e), zeta);
        await apoyo.SembrarArticuloAsync(e, "z-de-baja", ValoresBase(e), zeta, dadoDeBaja: true);
        await apoyo.SembrarArticuloAsync(e, "a1", ValoresBase(e), alfa);
        await apoyo.SembrarArticuloAsync(e, "suelto", ValoresBase(e));
        await apoyo.SembrarArticuloAsync(e, "de-la-dada-de-baja", ValoresBase(e), dadaDeBaja);

        var listado = await LeerAsync<List<FamiliaListado>>(await GetListadoAsync(e.Admin));

        Assert.Equal(
            [
                new FamiliaListado(alfa, "alfa", false, 1),
                new FamiliaListado(beta, "Beta", true, 0),
                new FamiliaListado(zeta, "Zeta", true, 2)
            ],
            listado);
    }

    /// <summary>El orden por nombre se le pide a la base: la sentencia del listado lleva <c>ORDER BY f.nombre</c>.
    /// Afirmarlo sobre el texto es la red de esa cláusula: un recorrido del índice único parcial del nombre
    /// (<c>ux_familias_nombre</c>) devuelve las filas en ese mismo orden, y con la cláusula borrada una prueba que
    /// solo mira el resultado siguió en verde.</summary>
    [Fact]
    public async Task ElListadoLePideElOrdenPorNombreALaBase()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElListadoLePideElOrdenPorNombreALaBase));
        await apoyo.SembrarFamiliaAsync(e, "Zeta");
        await apoyo.SembrarFamiliaAsync(e, "Alfa");

        var registro = new InterceptorQueRegistraSentencias();
        await using var db = apoyo.ContextoDelTenant(e, registro);

        var listado = await ServicioDe(db).ListarAsync();

        Assert.Equal(["Alfa", "Zeta"], listado.Select(f => f.Nombre));
        var consulta = Assert.Single(registro.Sentencias, s => s.Contains("FROM familias", StringComparison.Ordinal));
        Assert.Contains("ORDER BY f.nombre", consulta, StringComparison.Ordinal);
    }

    /// <summary>Lo mismo para el orden por id de las listas fijas del detalle: la sentencia lleva <c>ORDER BY
    /// l.id_lista_precio</c>. Una consulta que solo pide el id puede resolverse con un recorrido del índice de la
    /// clave primaria, que ya devuelve ese orden, así que el texto es la única red.</summary>
    [Fact]
    public async Task ElDetalleLePideElOrdenPorIdDeLasListasALaBase()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElDetalleLePideElOrdenPorIdDeLasListasALaBase));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Con listas");
        await apoyo.SembrarArticuloAsync(e, "referencia", ValoresBase(e), familia);

        var registro = new InterceptorQueRegistraSentencias();
        await using var db = apoyo.ContextoDelTenant(e, registro);

        var detalle = await ServicioDe(db).ObtenerAsync(familia);

        Assert.Equal(
            new[] { e.IdListaGeneral, e.IdListaMayorista }.Order(), detalle.Precios.Select(p => p.IdListaPrecio));
        var consulta = Assert.Single(registro.Sentencias, s => s.Contains("FROM listas_precio", StringComparison.Ordinal));
        Assert.Contains("ORDER BY l.id_lista_precio", consulta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ElListadoDeUnTenantSinFamiliasEstaVacio()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElListadoDeUnTenantSinFamiliasEstaVacio));

        Assert.Empty(await LeerAsync<List<FamiliaListado>>(await GetListadoAsync(e.Admin)));
    }

    // =================================================================================================
    // GET /api/familias/{id}
    // =================================================================================================

    private sealed record Sembrada(
        int Familia, int DeBaja, int M1, int M2, int M3, int ListaMinorista, int ListaDerivada,
        DateTimeOffset DesdeDelPendiente);

    /// <summary>La familia "Gaseosas": un miembro DADO DE BAJA con el id más bajo y valores distintos (no es la
    /// referencia), <c>m1</c> (la referencia: el vivo de menor id) con marca viva y precios, <c>m2</c> con una
    /// marca dada de baja, otros valores y otros precios, y <c>m3</c> sin marca. Listas: general (vigente 100 y
    /// pendiente 130 dentro de tres días), mayorista (solo el vigente, 80), una tercera fija sin ningún precio y una
    /// derivada, que no es parte del estado de precios.</summary>
    private async Task<Sembrada> SembrarGaseosasAsync(Entorno e)
    {
        var minorista = await apoyo.SembrarListaFijaAsync(e, "Minorista");
        var derivada = await apoyo.SembrarListaDerivadaAsync(e, "Derivada", e.IdListaGeneral);
        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas");
        var @base = ValoresBase(e);

        var ids = IdsAscendentes(4);
        var (deBaja, m1, m2, m3) = (ids[0], ids[1], ids[2], ids[3]);

        // En orden inverso y con códigos internos que ordenan al revés de los ids (ver el resumen de la clase).
        await apoyo.SembrarArticuloAsync(e, "m3", @base, familia, idMarca: null, activo: true, id: m3, prefijoDelCodigo: "a");
        await apoyo.SembrarArticuloAsync(
            e, "m2", @base with { CostoLista = 777m }, familia, idMarca: e.Marcas[1], activo: false, id: m2, prefijoDelCodigo: "b");
        await apoyo.SembrarArticuloAsync(e, "m1", @base, familia, idMarca: e.Marcas[0], activo: true, id: m1, prefijoDelCodigo: "c");
        await apoyo.SembrarArticuloAsync(
            e, "de-baja", @base with { CostoLista = 999m }, familia, dadoDeBaja: true, id: deBaja, prefijoDelCodigo: "d");

        await apoyo.DarDeBajaAsync("marcas", "id_marca", e.Marcas[1]);

        var desdeDelPendiente = DateTimeOffset.UtcNow.AddDays(3);
        await apoyo.SembrarPrecioPendienteAsync(e, m1, e.IdListaGeneral, montoVigente: 100m, montoPendiente: 130m, desdeDelPendiente);
        await apoyo.SembrarPrecioVigenteAsync(e, m1, e.IdListaMayorista, 80m);

        await apoyo.SembrarPrecioVigenteAsync(e, m2, e.IdListaGeneral, 999m);
        await apoyo.SembrarPrecioVigenteAsync(e, m2, minorista, 555m);
        await apoyo.SembrarPrecioVigenteAsync(e, deBaja, e.IdListaMayorista, 444m);

        return new Sembrada(familia, deBaja, m1, m2, m3, minorista, derivada, AMicrosegundos(desdeDelPendiente));
    }

    /// <summary>El detalle completo, con cada campo leído con valores distintos entre sí: la familia, los miembros
    /// vivos ascendentes por id (el dado de baja no está) con su marca —la dada de baja, como <c>null</c>— y su
    /// estado, los trece valores compartidos de la referencia (<c>m1</c>, el vivo de menor id, y no el dado de
    /// baja ni el último) y su estado de precios en cada lista fija, ascendentes por id de lista: el pendiente con su
    /// fecha, un vigente solo, y una lista sin precios con el estado vacío. La derivada no figura.</summary>
    [Fact]
    public async Task ElDetalleTraeLosMiembrosVivosLosValoresYLosPreciosDeLaReferencia()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElDetalleTraeLosMiembrosVivosLosValoresYLosPreciosDeLaReferencia));
        var s = await SembrarGaseosasAsync(e);

        var detalle = await LeerAsync<FamiliaDetalle>(await GetDetalleAsync(e.Admin, s.Familia));

        Assert.Equal((s.Familia, "Gaseosas", true), (detalle.Id, detalle.Nombre, detalle.Activo));

        var codigos = new Dictionary<int, string>();
        foreach (var id in new[] { s.M1, s.M2, s.M3 })
        {
            codigos[id] = (await apoyo.LeerAsync(id)).CodigoInterno;
        }

        Assert.Equal(
            [
                new MiembroDeFamilia(s.M1, codigos[s.M1], "m1", e.Marcas[0], true),
                new MiembroDeFamilia(s.M2, codigos[s.M2], "m2", null, false),
                new MiembroDeFamilia(s.M3, codigos[s.M3], "m3", null, true)
            ],
            detalle.Articulos);

        var @base = ValoresBase(e);
        Assert.Equal(
            new ValoresCompartidosDeLaFamilia(
                IdArea: @base.IdArea, IdCategoria: @base.IdCategoria, IdGrupo: @base.IdGrupo,
                IdProveedorHabitual: @base.IdProveedorHabitual, IdAlicuotaIva: @base.IdAlicuotaIva,
                UnidadVenta: @base.UnidadVenta, UnidadesPorBulto: @base.UnidadesPorBulto, EsProducto: @base.EsProducto,
                ControlaLote: @base.ControlaLote, AcumulaEnVenta: @base.AcumulaEnVenta, CostoLista: @base.CostoLista,
                DescuentoProveedor: @base.DescuentoProveedor, CostoNominal: @base.CostoNominal),
            detalle.Valores);

        var fijas = new[] { e.IdListaGeneral, e.IdListaMayorista, s.ListaMinorista }.Order().ToList();
        Assert.Equal(fijas, detalle.Precios.Select(p => p.IdListaPrecio));
        Assert.DoesNotContain(detalle.Precios, p => p.IdListaPrecio == s.ListaDerivada);

        var porLista = detalle.Precios.ToDictionary(p => p.IdListaPrecio, p => p.Estado);
        Assert.Equal(
            new EstadoDePrecios(100m, new PrecioPendiente(130m, s.DesdeDelPendiente)), porLista[e.IdListaGeneral]);
        Assert.Equal(new EstadoDePrecios(80m, null), porLista[e.IdListaMayorista]);
        Assert.Equal(EstadoDePrecios.Vacio, porLista[s.ListaMinorista]);
    }

    /// <summary>Un id de catálogo que apunta a una fila dada de baja viaja como <c>null</c>, igual que la marca de
    /// un miembro: un id colgante nunca se expone. Una baja REAL de cada catálogo —el área, la categoría, el grupo y
    /// el proveedor habitual de la referencia—, con los artículos todavía apuntándole. Lo que no está dado de baja
    /// viaja tal cual.</summary>
    [Fact]
    public async Task LosCatalogosDadosDeBajaDeLaReferenciaViajanComoNull()
    {
        using var e = await apoyo.PrepararAsync(nameof(LosCatalogosDadosDeBajaDeLaReferenciaViajanComoNull));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Con catálogos dados de baja");
        await apoyo.SembrarArticuloAsync(e, "referencia", ValoresBase(e), familia);

        await apoyo.DarDeBajaAsync("areas", "id_area", e.Areas[0]);
        await apoyo.DarDeBajaAsync("categorias", "id_categoria", e.Categorias[0]);
        await apoyo.DarDeBajaAsync("grupos", "id_grupo", e.Grupos[0]);
        await apoyo.DarDeBajaAsync("proveedores", "id_proveedor", e.Proveedores[0]);

        var valores = (await LeerAsync<FamiliaDetalle>(await GetDetalleAsync(e.Admin, familia))).Valores!;

        var @base = ValoresBase(e);
        Assert.Equal(
            new ValoresCompartidosDeLaFamilia(
                IdArea: null, IdCategoria: null, IdGrupo: null, IdProveedorHabitual: null,
                IdAlicuotaIva: @base.IdAlicuotaIva, UnidadVenta: @base.UnidadVenta, UnidadesPorBulto: @base.UnidadesPorBulto,
                EsProducto: @base.EsProducto, ControlaLote: @base.ControlaLote, AcumulaEnVenta: @base.AcumulaEnVenta,
                CostoLista: @base.CostoLista, DescuentoProveedor: @base.DescuentoProveedor, CostoNominal: @base.CostoNominal),
            valores);
    }

    /// <summary>Una familia cuyos miembros están todos dados de baja no tiene referencia: sin miembros, sin valores
    /// compartidos y sin estado de precios (<c>valores</c> es <c>null</c> y <c>precios</c> está vacío), aunque el
    /// tenant tenga listas fijas.</summary>
    [Fact]
    public async Task UnaFamiliaSinMiembrosVivosNoTieneReferenciaNiValoresNiPrecios()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaSinMiembrosVivosNoTieneReferenciaNiValoresNiPrecios));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Vacía", activa: false);
        var deBaja = await apoyo.SembrarArticuloAsync(e, "de-baja", ValoresBase(e), familia, dadoDeBaja: true);
        await apoyo.SembrarPrecioVigenteAsync(e, deBaja, e.IdListaGeneral, 100m);

        var detalle = await LeerAsync<FamiliaDetalle>(await GetDetalleAsync(e.Admin, familia));

        Assert.Equal((familia, "Vacía", false), (detalle.Id, detalle.Nombre, detalle.Activo));
        Assert.Empty(detalle.Articulos);
        Assert.Null(detalle.Valores);
        Assert.Empty(detalle.Precios);
    }

    /// <summary>Las filas cerradas en el pasado son historia y no estado. En la lista general la referencia tiene
    /// una fila cerrada y la vigente, abierta; en la mayorista, solo una fila cerrada ayer: el estado es el vigente
    /// de la general y el vacío en la mayorista.</summary>
    [Fact]
    public async Task LaHistoriaCerradaDeLaReferenciaNoEsElEstadoDePrecios()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaHistoriaCerradaDeLaReferenciaNoEsElEstadoDePrecios));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Con historia");
        var referencia = await apoyo.SembrarArticuloAsync(e, "referencia", ValoresBase(e), familia);

        var ahora = DateTimeOffset.UtcNow;
        await apoyo.SembrarPrecioAsync(e, referencia, e.IdListaGeneral, 70m, ahora.AddDays(-90), ahora.AddDays(-30));
        await apoyo.SembrarPrecioAsync(e, referencia, e.IdListaGeneral, 90m, ahora.AddDays(-30), null);
        await apoyo.SembrarPrecioAsync(e, referencia, e.IdListaMayorista, 60m, ahora.AddDays(-90), ahora.AddDays(-1));

        var detalle = await LeerAsync<FamiliaDetalle>(await GetDetalleAsync(e.Admin, familia));

        var porLista = detalle.Precios.ToDictionary(p => p.IdListaPrecio, p => p.Estado);
        Assert.Equal(new EstadoDePrecios(90m, null), porLista[e.IdListaGeneral]);
        Assert.Equal(EstadoDePrecios.Vacio, porLista[e.IdListaMayorista]);
    }

    [Fact]
    public async Task UnaFamiliaInexistenteDadaDeBajaODeOtroTenantDa404()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaInexistenteDadaDeBajaODeOtroTenantDa404));
        using var otro = await apoyo.PrepararAsync(nameof(UnaFamiliaInexistenteDadaDeBajaODeOtroTenantDa404) + "-ajeno");
        var dadaDeBaja = await apoyo.SembrarFamiliaAsync(e, "Dada de baja", dadaDeBaja: true);
        var ajena = await apoyo.SembrarFamiliaAsync(otro, "Ajena");

        foreach (var id in new[] { 999_999_999, dadaDeBaja, ajena })
        {
            var respuesta = await GetDetalleAsync(e.Admin, id);

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
            Assert.Equal("no_encontrado", (await ProblemaAsync(respuesta)).Codigo);
        }
    }
}
