using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Combos;
using Ways.Domain.Articulos;
using Ways.Domain.Combos;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>/api/combos</c> de punta a punta contra Postgres real: lecturas, alta, edición con reemplazo del conjunto,
/// baja lógica, cada código de validación, el 404 antes de cualquier otra cosa, la asignación del código y el
/// aislamiento entre tenants. Las guardas de baja de familias están en <see cref="CombosGuardasTests"/> y los
/// respaldos de esquema y los locks en <see cref="CombosRespaldosTests"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CombosEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private static ComponenteDeSolicitudDeCombo Art(int id, decimal cantidad = 1m) => new(id, null, cantidad);

    private static ComponenteDeSolicitudDeCombo Fam(int id, decimal cantidad = 1m) => new(null, id, cantidad);

    private static AltaCombo Alta(
        int? codigo, IReadOnlyList<ComponenteDeSolicitudDeCombo>? componentes, string nombre = "Combo",
        decimal precio = 5000m, int? idEmpresa = null, bool activo = true) =>
        new(codigo, nombre, precio, idEmpresa, componentes, activo);

    private static EdicionCombo Edicion(
        int? codigo, IReadOnlyList<ComponenteDeSolicitudDeCombo>? componentes, string nombre = "Combo editado",
        decimal precio = 4000m, int? idEmpresa = null, bool activo = true) =>
        new(codigo, nombre, precio, idEmpresa, componentes, activo);

    private static Task<HttpResponseMessage> PostAsync(HttpClient cliente, AltaCombo alta) =>
        cliente.PostAsJsonAsync("/api/combos", alta, OpcionesJson);

    private static Task<HttpResponseMessage> PutAsync(HttpClient cliente, int id, EdicionCombo edicion) =>
        cliente.PutAsJsonAsync($"/api/combos/{id}", edicion, OpcionesJson);

    private static async Task<ComboListado> LeerAsync(HttpResponseMessage respuesta) =>
        (await respuesta.Content.ReadFromJsonAsync<ComboListado>(OpcionesJson))!;

    private static async Task<ComboListado> CrearAsync(HttpClient cliente, AltaCombo alta)
    {
        var respuesta = await PostAsync(cliente, alta);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        return await LeerAsync(respuesta);
    }

    private async Task<int> ArticuloAsync(Entorno e, string nombre, bool pesable = false, bool esProducto = true) =>
        await apoyo.SembrarArticuloAsync(
            e, nombre,
            ValoresBase(e) with { UnidadVenta = pesable ? UnidadVenta.Peso : UnidadVenta.Unidad, EsProducto = esProducto });

    private async Task<Combo> ComboEnBaseAsync(int id)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.Combos.IgnoreQueryFilters().SingleAsync(c => c.Id == id);
    }

    private async Task<List<ComboComponente>> ComponentesEnBaseAsync(int idCombo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        return await db.CombosComponentes.IgnoreQueryFilters().Where(c => c.IdCombo == idCombo).OrderBy(c => c.Id).ToListAsync();
    }

    private static async Task<List<ComboListado>> ListarAsync(HttpClient cliente)
    {
        var respuesta = await cliente.GetAsync("/api/combos");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        return (await respuesta.Content.ReadFromJsonAsync<List<ComboListado>>(OpcionesJson))!;
    }

    private static (int? IdArticulo, int? IdFamilia, decimal Cantidad) Fila(ComboComponente c) =>
        (c.IdArticulo, c.IdFamilia, c.Cantidad);

    // =================================================================================================
    // Alta
    // =================================================================================================

    [Fact]
    public async Task ElAltaCreaElComboConSusComponentesYDevuelveLoQueGuardo()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElAltaCreaElComboConSusComponentesYDevuelveLoQueGuardo));
        var pancho = await ArticuloAsync(e, "pancho");
        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas");

        var respuesta = await PostAsync(
            e.Admin,
            Alta(7, [Art(pancho, 2m), Fam(familia, 3m)], nombre: "  Pancho + gaseosa  ", precio: 5000.50m,
                idEmpresa: e.IdEmpresa, activo: false));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var creado = await LeerAsync(respuesta);
        Assert.Equal($"/api/combos/{creado.Id}", respuesta.Headers.Location!.OriginalString);
        Assert.Equal(7, creado.Codigo);
        Assert.Equal("Pancho + gaseosa", creado.Nombre);
        Assert.Equal(5000.50m, creado.Precio);
        Assert.Equal(e.IdEmpresa, creado.IdEmpresa);
        Assert.False(creado.Activo);
        Assert.Equal(
            [
                new ComponenteDeComboListado(pancho, null, "pancho", 2m, true),
                new ComponenteDeComboListado(null, familia, "Gaseosas", 3m, true)
            ],
            creado.Componentes);

        var guardado = await ComboEnBaseAsync(creado.Id);
        Assert.Equal(e.IdTenant, guardado.IdTenant);
        Assert.Equal(7, guardado.Codigo);
        Assert.Equal("Pancho + gaseosa", guardado.Nombre);
        Assert.Equal(5000.50m, guardado.Precio);
        Assert.Equal(e.IdEmpresa, guardado.IdEmpresa);
        Assert.False(guardado.Activo);
        Assert.Null(guardado.DeletedAt);

        var filas = await ComponentesEnBaseAsync(creado.Id);
        Assert.Equal(new (int?, int?, decimal)[] { (pancho, null, 2m), (null, familia, 3m) }, filas.Select(Fila));
        Assert.All(filas, f => Assert.Equal(e.IdTenant, f.IdTenant));
    }

    [Fact]
    public async Task UnServicioQueNoEsProductoPuedeSerComponente()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnServicioQueNoEsProductoPuedeSerComponente));
        var servicio = await ArticuloAsync(e, "delivery", esProducto: false);
        var producto = await ArticuloAsync(e, "pizza");

        var creado = await CrearAsync(e.Admin, Alta(null, [Art(servicio), Art(producto)]));

        Assert.Equal(2, creado.Componentes.Count);
    }

    [Fact]
    public async Task UnaFamiliaSinMiembrosOConMiembrosPorUnidadEsComponenteValido()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaSinMiembrosOConMiembrosPorUnidadEsComponenteValido));
        var vacia = await apoyo.SembrarFamiliaAsync(e, "Vacia");
        var conMiembros = await apoyo.SembrarFamiliaAsync(e, "Con miembros");
        await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), conMiembros);

        var creado = await CrearAsync(e.Admin, Alta(null, [Fam(vacia), Fam(conMiembros)]));

        Assert.Equal(2, creado.Componentes.Count);
    }

    [Fact]
    public async Task ElAltaSinCodigoAsignaElSiguienteAlMaximoVivoYNoContaLosDadosDeBaja()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElAltaSinCodigoAsignaElSiguienteAlMaximoVivoYNoContaLosDadosDeBaja));
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");

        var primero = await CrearAsync(e.Admin, Alta(null, [Art(a), Art(b)]));
        var segundo = await CrearAsync(e.Admin, Alta(null, [Art(a), Art(b)]));
        var conSalto = await CrearAsync(e.Admin, Alta(10, [Art(a), Art(b)]));
        var siguiente = await CrearAsync(e.Admin, Alta(null, [Art(a), Art(b)]));
        Assert.Equal([1, 2, 10, 11], [primero.Codigo, segundo.Codigo, conSalto.Codigo, siguiente.Codigo]);

        Assert.Equal(HttpStatusCode.NoContent, (await e.Admin.DeleteAsync($"/api/combos/{siguiente.Id}")).StatusCode);
        var trasLaBaja = await CrearAsync(e.Admin, Alta(null, [Art(a), Art(b)]));

        Assert.Equal(11, trasLaBaja.Codigo);
    }

    [Fact]
    public async Task ElCodigoMaximoOcupadoHaceQueLaAsignacionAutomaticaDe409()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElCodigoMaximoOcupadoHaceQueLaAsignacionAutomaticaDe409));
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");
        await CrearAsync(e.Admin, Alta(ReglaDeCombos.CodigoMaximo, [Art(a), Art(b)]));

        var respuesta = await PostAsync(e.Admin, Alta(null, [Art(a), Art(b)]));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("combo_codigo_agotado", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Single(await ListarAsync(e.Admin));
    }

    [Fact]
    public async Task SeisAltasSimultaneasSinCodigoRecibenSeisCodigosDistintos()
    {
        using var e = await apoyo.PrepararAsync(nameof(SeisAltasSimultaneasSinCodigoRecibenSeisCodigosDistintos));
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");

        var respuestas = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => PostAsync(e.Admin, Alta(null, [Art(a), Art(b)]))));

        Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var codigos = new List<int>();
        foreach (var r in respuestas)
        {
            codigos.Add((await LeerAsync(r)).Codigo);
        }

        Assert.Equal([1, 2, 3, 4, 5, 6], codigos.Order());
    }

    [Fact]
    public async Task DosAltasSimultaneasConElMismoCodigoDanUnCreatedYUnConflict()
    {
        using var e = await apoyo.PrepararAsync(nameof(DosAltasSimultaneasConElMismoCodigoDanUnCreatedYUnConflict));
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");

        var respuestas = await Task.WhenAll(
            PostAsync(e.Admin, Alta(5, [Art(a), Art(b)])), PostAsync(e.Admin, Alta(5, [Art(a), Art(b)])));

        var estados = respuestas.Select(r => r.StatusCode).Order().ToList();
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], estados);
        var perdedora = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("combo_codigo_duplicado", (await ProblemaAsync(perdedora)).Codigo);
        Assert.Single(await ListarAsync(e.Admin));
    }

    [Fact]
    public async Task ElCodigoDeUnComboVivoDa409YElDeUnoDadoDeBajaSePuedeReutilizar()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElCodigoDeUnComboVivoDa409YElDeUnoDadoDeBajaSePuedeReutilizar));
        using var otro = await apoyo.PrepararAsync(nameof(ElCodigoDeUnComboVivoDa409YElDeUnoDadoDeBajaSePuedeReutilizar) + "-ajeno");
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");
        var primero = await CrearAsync(e.Admin, Alta(3, [Art(a), Art(b)]));

        var duplicado = await PostAsync(e.Admin, Alta(3, [Art(a), Art(b)]));
        Assert.Equal(HttpStatusCode.Conflict, duplicado.StatusCode);
        Assert.Equal("combo_codigo_duplicado", (await ProblemaAsync(duplicado)).Codigo);

        var ajeno = await ArticuloAsync(otro, "ajeno-a");
        var ajeno2 = await ArticuloAsync(otro, "ajeno-b");
        await CrearAsync(otro.Admin, Alta(3, [Art(ajeno), Art(ajeno2)]));

        Assert.Equal(HttpStatusCode.NoContent, (await e.Admin.DeleteAsync($"/api/combos/{primero.Id}")).StatusCode);
        var reutilizado = await CrearAsync(e.Admin, Alta(3, [Art(a), Art(b)]));

        Assert.Equal(3, reutilizado.Codigo);
        Assert.NotEqual(primero.Id, reutilizado.Id);
    }

    // =================================================================================================
    // Validación
    // =================================================================================================

    private static AltaCombo AltaInvalida(string caso, Entorno e, int a, int b) => caso switch
    {
        "precio_cero" => Alta(null, [Art(a), Art(b)], precio: 0m),
        "precio_sobre_el_tope" => Alta(null, [Art(a), Art(b)], precio: ReglaDeCombos.PrecioMaximo + 0.01m),
        "componente_vacio" => Alta(null, [Art(a), new ComponenteDeSolicitudDeCombo(null, null, 1m)]),
        "componente_con_ambos" => Alta(null, [Art(a), new ComponenteDeSolicitudDeCombo(b, e.IdEmpresa, 1m)]),
        "componente_nulo" => Alta(null, [Art(a), null!]),
        "cantidad_fraccionaria" => Alta(null, [Art(a, 1.5m), Art(b)]),
        "cantidad_cero" => Alta(null, [Art(a, 0m), Art(b)]),
        "cantidad_sobre_el_tope" => Alta(null, [Art(a, ReglaDeCombos.CantidadMaxima + 1m), Art(b)]),
        "articulo_repetido" => Alta(null, [Art(a), Art(b), Art(a)]),
        "una_sola_unidad" => Alta(null, [Art(a)]),
        "sin_componentes" => Alta(null, []),
        "componentes_ausentes" => Alta(null, null),
        "nombre_vacio" => Alta(null, [Art(a), Art(b)], nombre: "   "),
        "nombre_largo" => Alta(null, [Art(a), Art(b)], nombre: new string('x', 151)),
        "codigo_cero" => Alta(0, [Art(a), Art(b)]),
        "codigo_negativo" => Alta(-4, [Art(a), Art(b)]),
        "codigo_sobre_el_tope" => Alta(ReglaDeCombos.CodigoMaximo + 1, [Art(a), Art(b)]),
        _ => throw new ArgumentOutOfRangeException(nameof(caso), caso, null)
    };

    [Theory]
    [InlineData("precio_cero", "combo_precio_invalido")]
    [InlineData("precio_sobre_el_tope", "combo_precio_invalido")]
    [InlineData("componente_vacio", "combo_componente_invalido")]
    [InlineData("componente_con_ambos", "combo_componente_invalido")]
    [InlineData("componente_nulo", "combo_componente_invalido")]
    [InlineData("cantidad_fraccionaria", "combo_cantidad_invalida")]
    [InlineData("cantidad_cero", "combo_cantidad_invalida")]
    [InlineData("cantidad_sobre_el_tope", "combo_cantidad_invalida")]
    [InlineData("articulo_repetido", "combo_componente_duplicado")]
    [InlineData("una_sola_unidad", "combo_unidades_insuficientes")]
    [InlineData("sin_componentes", "combo_unidades_insuficientes")]
    [InlineData("componentes_ausentes", "combo_unidades_insuficientes")]
    [InlineData("nombre_vacio", "nombre_requerido")]
    [InlineData("nombre_largo", "nombre_muy_largo")]
    [InlineData("codigo_cero", "combo_codigo_invalido")]
    [InlineData("codigo_negativo", "combo_codigo_invalido")]
    [InlineData("codigo_sobre_el_tope", "combo_codigo_invalido")]
    public async Task CadaCuerpoInvalidoDa400ConSuCodigoYNoCreaNada(string caso, string codigoEsperado)
    {
        using var e = await apoyo.PrepararAsync(nameof(CadaCuerpoInvalidoDa400ConSuCodigoYNoCreaNada) + caso);
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");

        var respuesta = await PostAsync(e.Admin, AltaInvalida(caso, e, a, b));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(codigoEsperado, (await ProblemaAsync(respuesta)).Codigo);
        Assert.Empty(await ListarAsync(e.Admin));
    }

    [Fact]
    public async Task UnaEmpresaInexistenteODeOtroTenantDa400ReferenciaInvalida()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaEmpresaInexistenteODeOtroTenantDa400ReferenciaInvalida));
        using var otro = await apoyo.PrepararAsync(nameof(UnaEmpresaInexistenteODeOtroTenantDa400ReferenciaInvalida) + "-ajeno");
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");

        foreach (var idEmpresa in new[] { otro.IdEmpresa, 999_999_999 })
        {
            var respuesta = await PostAsync(e.Admin, Alta(null, [Art(a), Art(b)], idEmpresa: idEmpresa));

            Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
            Assert.Equal("referencia_invalida", (await ProblemaAsync(respuesta)).Codigo);
        }

        Assert.Empty(await ListarAsync(e.Admin));
    }

    [Theory]
    [InlineData("articulo_inexistente")]
    [InlineData("articulo_dado_de_baja")]
    [InlineData("articulo_de_otro_tenant")]
    [InlineData("familia_inexistente")]
    [InlineData("familia_dada_de_baja")]
    [InlineData("familia_de_otro_tenant")]
    public async Task UnComponenteQueNoExisteNoEstaVivoONoEsDelTenantDa400ReferenciaInvalida(string caso)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnComponenteQueNoExisteNoEstaVivoONoEsDelTenantDa400ReferenciaInvalida) + caso);
        using var otro = await apoyo.PrepararAsync(nameof(UnComponenteQueNoExisteNoEstaVivoONoEsDelTenantDa400ReferenciaInvalida) + caso + "-ajeno");
        var sano = await ArticuloAsync(e, "sano");

        var malo = caso switch
        {
            "articulo_inexistente" => Art(999_999_999),
            "articulo_dado_de_baja" => Art(await apoyo.SembrarArticuloAsync(e, "baja", ValoresBase(e), dadoDeBaja: true)),
            "articulo_de_otro_tenant" => Art(await ArticuloAsync(otro, "ajeno")),
            "familia_inexistente" => Fam(999_999_999),
            "familia_dada_de_baja" => Fam(await apoyo.SembrarFamiliaAsync(e, "baja", dadaDeBaja: true)),
            _ => Fam(await apoyo.SembrarFamiliaAsync(otro, "ajena"))
        };

        var respuesta = await PostAsync(e.Admin, Alta(null, [Art(sano), malo]));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("referencia_invalida", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Empty(await ListarAsync(e.Admin));
    }

    [Fact]
    public async Task UnArticuloPesableComoComponenteDa400NoUnitario()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnArticuloPesableComoComponenteDa400NoUnitario));
        var pesable = await ArticuloAsync(e, "jamon", pesable: true);
        var unitario = await ArticuloAsync(e, "pan");

        var respuesta = await PostAsync(e.Admin, Alta(null, [Art(pesable), Art(unitario)]));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("combo_componente_no_unitario", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Empty(await ListarAsync(e.Admin));
    }

    [Fact]
    public async Task UnaFamiliaConUnMiembroPesableDa400NoUnitarioPeroNoSiEseMiembroEstaDadoDeBaja()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaConUnMiembroPesableDa400NoUnitarioPeroNoSiEseMiembroEstaDadoDeBaja));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Fiambres");
        var pesable = await apoyo.SembrarArticuloAsync(
            e, "jamon", ValoresBase(e) with { UnidadVenta = UnidadVenta.Peso }, familia);
        var unitario = await ArticuloAsync(e, "pan");

        var rechazada = await PostAsync(e.Admin, Alta(null, [Fam(familia), Art(unitario)]));
        Assert.Equal(HttpStatusCode.BadRequest, rechazada.StatusCode);
        Assert.Equal("combo_componente_no_unitario", (await ProblemaAsync(rechazada)).Codigo);

        await apoyo.DarDeBajaAsync("articulos", "id_articulo", pesable);

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(e.Admin, Alta(null, [Fam(familia), Art(unitario)]))).StatusCode);
    }

    // =================================================================================================
    // Lecturas
    // =================================================================================================

    [Fact]
    public async Task ElListadoYElDetalleDevuelvenLosCombosVivosOrdenadosPorCodigoConSusNombres()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElListadoYElDetalleDevuelvenLosCombosVivosOrdenadosPorCodigoConSusNombres));
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");
        var familia = await apoyo.SembrarFamiliaAsync(e, "Fam");
        var dos = await CrearAsync(e.Admin, Alta(2, [Art(a), Art(b)], nombre: "Dos"));
        var uno = await CrearAsync(e.Admin, Alta(1, [Fam(familia, 2m)], nombre: "Uno"));
        var tres = await CrearAsync(e.Admin, Alta(3, [Art(a), Art(b)], nombre: "Tres"));
        await e.Admin.DeleteAsync($"/api/combos/{tres.Id}");

        var lista = await ListarAsync(e.Admin);

        Assert.Equal([uno.Id, dos.Id], lista.Select(c => c.Id));
        Assert.Equal(uno.Componentes, lista[0].Componentes);
        Assert.Equal(2, lista[1].Componentes.Count);
        var detalle = await e.Admin.GetAsync($"/api/combos/{dos.Id}");
        Assert.Equal(HttpStatusCode.OK, detalle.StatusCode);
        Assert.Equal(dos, await LeerAsync(detalle), new ComboListadoComparer());
    }

    [Fact]
    public async Task UnComponenteCuyoArticuloOFamiliaFueDadoDeBajaSigueEnElComboConNombreNuloYNoDisponible()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnComponenteCuyoArticuloOFamiliaFueDadoDeBajaSigueEnElComboConNombreNuloYNoDisponible));
        var vivo = await ArticuloAsync(e, "vivo");
        var caido = await ArticuloAsync(e, "caido");
        var familia = await apoyo.SembrarFamiliaAsync(e, "Caida");
        var creado = await CrearAsync(e.Admin, Alta(null, [Art(vivo), Art(caido), Fam(familia)]));

        await apoyo.DarDeBajaAsync("articulos", "id_articulo", caido);
        await apoyo.DarDeBajaAsync("familias", "id_familia", familia);

        var esperados = new[]
        {
            new ComponenteDeComboListado(vivo, null, "vivo", 1m, true),
            new ComponenteDeComboListado(caido, null, null, 1m, false),
            new ComponenteDeComboListado(null, familia, null, 1m, false)
        };
        var lista = await ListarAsync(e.Admin);
        Assert.Equal(esperados, lista.Single().Componentes);
        var detalle = await LeerAsync(await e.Admin.GetAsync($"/api/combos/{creado.Id}"));
        Assert.Equal(esperados, detalle.Componentes);
    }

    [Fact]
    public async Task ElIdDeUnComboInexistenteDeOtroTenantODadoDeBajaDa404EnElDetalle()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElIdDeUnComboInexistenteDeOtroTenantODadoDeBajaDa404EnElDetalle));
        using var otro = await apoyo.PrepararAsync(nameof(ElIdDeUnComboInexistenteDeOtroTenantODadoDeBajaDa404EnElDetalle) + "-ajeno");
        var ajeno = await CrearAsync(
            otro.Admin, Alta(null, [Art(await ArticuloAsync(otro, "x")), Art(await ArticuloAsync(otro, "y"))]));
        var propio = await CrearAsync(
            e.Admin, Alta(null, [Art(await ArticuloAsync(e, "x")), Art(await ArticuloAsync(e, "y"))]));
        await e.Admin.DeleteAsync($"/api/combos/{propio.Id}");

        foreach (var id in new[] { 999_999_999, ajeno.Id, propio.Id })
        {
            var respuesta = await e.Admin.GetAsync($"/api/combos/{id}");

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
            Assert.Equal("no_encontrado", (await ProblemaAsync(respuesta)).Codigo);
        }
    }

    [Fact]
    public async Task CadaTenantVeSoloSusCombos()
    {
        using var e = await apoyo.PrepararAsync(nameof(CadaTenantVeSoloSusCombos));
        using var otro = await apoyo.PrepararAsync(nameof(CadaTenantVeSoloSusCombos) + "-ajeno");
        var propio = await CrearAsync(
            e.Admin, Alta(1, [Art(await ArticuloAsync(e, "x")), Art(await ArticuloAsync(e, "y"))], nombre: "propio"));
        var ajeno = await CrearAsync(
            otro.Admin, Alta(1, [Art(await ArticuloAsync(otro, "x")), Art(await ArticuloAsync(otro, "y"))], nombre: "ajeno"));

        Assert.Equal([propio.Id], (await ListarAsync(e.Admin)).Select(c => c.Id));
        Assert.Equal([ajeno.Id], (await ListarAsync(otro.Admin)).Select(c => c.Id));
    }

    // =================================================================================================
    // Edición
    // =================================================================================================

    [Fact]
    public async Task ElPutReemplazaElConjuntoCompletoYTodosLosCamposEditables()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElPutReemplazaElConjuntoCompletoYTodosLosCamposEditables));
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");
        var c = await ArticuloAsync(e, "c");
        var familia = await apoyo.SembrarFamiliaAsync(e, "Fam");
        var original = await CrearAsync(e.Admin, Alta(4, [Art(a), Art(b, 2m)], nombre: "Original", precio: 5000m));

        var respuesta = await PutAsync(
            e.Admin, original.Id, Edicion(9, [Art(a, 3m), Art(c), Fam(familia)], nombre: " Nuevo ", precio: 4200m,
                idEmpresa: e.IdEmpresa, activo: false));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var editado = await LeerAsync(respuesta);
        Assert.Equal(original.Id, editado.Id);
        Assert.Equal((9, "Nuevo", 4200m, e.IdEmpresa, false), (editado.Codigo, editado.Nombre, editado.Precio, editado.IdEmpresa, editado.Activo));
        Assert.Equal(
            [
                new ComponenteDeComboListado(a, null, "a", 3m, true),
                new ComponenteDeComboListado(c, null, "c", 1m, true),
                new ComponenteDeComboListado(null, familia, "Fam", 1m, true)
            ],
            editado.Componentes);

        var guardado = await ComboEnBaseAsync(original.Id);
        Assert.Equal((9, "Nuevo", 4200m, e.IdEmpresa, false), (guardado.Codigo, guardado.Nombre, guardado.Precio, guardado.IdEmpresa, guardado.Activo));
        Assert.True(guardado.UpdatedAt >= guardado.CreatedAt);
        var filas = await ComponentesEnBaseAsync(original.Id);
        Assert.Equal(3, filas.Count);
        Assert.DoesNotContain(filas, f => f.IdArticulo == b);
    }

    [Fact]
    public async Task ElPutSinCodigoConservaElActualYConsumeElMismoCodigoSinChocarConsigoMismo()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElPutSinCodigoConservaElActualYConsumeElMismoCodigoSinChocarConsigoMismo));
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");
        var combo = await CrearAsync(e.Admin, Alta(8, [Art(a), Art(b)]));

        var sinCodigo = await PutAsync(e.Admin, combo.Id, Edicion(null, [Art(a), Art(b)]));
        var conSuCodigo = await PutAsync(e.Admin, combo.Id, Edicion(8, [Art(a), Art(b)]));

        Assert.Equal(HttpStatusCode.OK, sinCodigo.StatusCode);
        Assert.Equal(8, (await LeerAsync(sinCodigo)).Codigo);
        Assert.Equal(HttpStatusCode.OK, conSuCodigo.StatusCode);
        Assert.Equal(8, (await LeerAsync(conSuCodigo)).Codigo);
    }

    [Fact]
    public async Task ElPutConElCodigoDeOtroComboVivoDa409YConElDeUnoDadoDeBajaFunciona()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElPutConElCodigoDeOtroComboVivoDa409YConElDeUnoDadoDeBajaFunciona));
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");
        var uno = await CrearAsync(e.Admin, Alta(1, [Art(a), Art(b)]));
        var dos = await CrearAsync(e.Admin, Alta(2, [Art(a), Art(b)]));

        var choque = await PutAsync(e.Admin, dos.Id, Edicion(1, [Art(a), Art(b)]));
        Assert.Equal(HttpStatusCode.Conflict, choque.StatusCode);
        Assert.Equal("combo_codigo_duplicado", (await ProblemaAsync(choque)).Codigo);
        Assert.Equal(2, (await ComboEnBaseAsync(dos.Id)).Codigo);

        await e.Admin.DeleteAsync($"/api/combos/{uno.Id}");
        var libre = await PutAsync(e.Admin, dos.Id, Edicion(1, [Art(a), Art(b)]));

        Assert.Equal(HttpStatusCode.OK, libre.StatusCode);
        Assert.Equal(1, (await ComboEnBaseAsync(dos.Id)).Codigo);
    }

    [Theory]
    [InlineData("referencia_invalida")]
    [InlineData("no_unitario")]
    [InlineData("duplicado_en_el_pedido")]
    [InlineData("codigo_ocupado")]
    public async Task UnPutRechazadoNoTocaNiElComboNiSusComponentes(string caso)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnPutRechazadoNoTocaNiElComboNiSusComponentes) + caso);
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");
        var pesable = await ArticuloAsync(e, "pesable", pesable: true);
        await CrearAsync(e.Admin, Alta(2, [Art(a), Art(b)]));
        var combo = await CrearAsync(e.Admin, Alta(1, [Art(a, 2m), Art(b)], nombre: "Intacto"));
        var antes = await ComboEnBaseAsync(combo.Id);

        var (edicion, estado, codigo) = caso switch
        {
            "referencia_invalida" => (Edicion(null, [Art(a), Art(999_999_999)]), HttpStatusCode.BadRequest, "referencia_invalida"),
            "no_unitario" => (Edicion(null, [Art(a), Art(pesable)]), HttpStatusCode.BadRequest, "combo_componente_no_unitario"),
            "duplicado_en_el_pedido" => (Edicion(null, [Art(a), Art(a)]), HttpStatusCode.BadRequest, "combo_componente_duplicado"),
            _ => (Edicion(2, [Art(a), Art(b)]), HttpStatusCode.Conflict, "combo_codigo_duplicado")
        };

        var respuesta = await PutAsync(e.Admin, combo.Id, edicion);

        Assert.Equal(estado, respuesta.StatusCode);
        Assert.Equal(codigo, (await ProblemaAsync(respuesta)).Codigo);
        var despues = await ComboEnBaseAsync(combo.Id);
        Assert.Equal((antes.Codigo, antes.Nombre, antes.Precio, antes.UpdatedAt), (despues.Codigo, despues.Nombre, despues.Precio, despues.UpdatedAt));
        Assert.Equal([(a, (int?)null, 2m), (b, (int?)null, 1m)], (await ComponentesEnBaseAsync(combo.Id)).Select(f => (f.IdArticulo!.Value, f.IdFamilia, f.Cantidad)));
    }

    // =================================================================================================
    // 404 primero
    // =================================================================================================

    [Theory]
    [InlineData("inexistente")]
    [InlineData("dado_de_baja")]
    [InlineData("de_otro_tenant")]
    public async Task UnPutSobreUnIdQueNoExisteDa404AunqueElCuerpoSeaInvalidoOElCodigoChoque(string caso)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnPutSobreUnIdQueNoExisteDa404AunqueElCuerpoSeaInvalidoOElCodigoChoque) + caso);
        using var otro = await apoyo.PrepararAsync(nameof(UnPutSobreUnIdQueNoExisteDa404AunqueElCuerpoSeaInvalidoOElCodigoChoque) + caso + "-ajeno");
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");
        await CrearAsync(e.Admin, Alta(1, [Art(a), Art(b)]));

        var id = caso switch
        {
            "inexistente" => 999_999_999,
            "dado_de_baja" => await DarDeBajaPorApiAsync(e, 2, a, b),
            _ => (await CrearAsync(otro.Admin, Alta(2, [Art(await ArticuloAsync(otro, "x")), Art(await ArticuloAsync(otro, "y"))]))).Id
        };

        var cuerpoInvalido = await PutAsync(e.Admin, id, Edicion(null, [Art(a)], nombre: "  ", precio: -1m));
        var codigoQueChoca = await PutAsync(e.Admin, id, Edicion(1, [Art(a), Art(b)]));

        foreach (var respuesta in new[] { cuerpoInvalido, codigoQueChoca })
        {
            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
            Assert.Equal("no_encontrado", (await ProblemaAsync(respuesta)).Codigo);
        }
    }

    private static async Task<int> DarDeBajaPorApiAsync(Entorno e, int codigo, int a, int b)
    {
        var combo = await CrearAsync(e.Admin, Alta(codigo, [Art(a), Art(b)]));
        Assert.Equal(HttpStatusCode.NoContent, (await e.Admin.DeleteAsync($"/api/combos/{combo.Id}")).StatusCode);

        return combo.Id;
    }

    // =================================================================================================
    // Baja
    // =================================================================================================

    [Fact]
    public async Task LaBajaEsLogicaBorraLosComponentesYDejaLibreElCodigo()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaBajaEsLogicaBorraLosComponentesYDejaLibreElCodigo));
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");
        var combo = await CrearAsync(e.Admin, Alta(6, [Art(a), Art(b)]));

        var respuesta = await e.Admin.DeleteAsync($"/api/combos/{combo.Id}");

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);
        var guardado = await ComboEnBaseAsync(combo.Id);
        Assert.NotNull(guardado.DeletedAt);
        Assert.Empty(await ComponentesEnBaseAsync(combo.Id));
        Assert.Empty(await ListarAsync(e.Admin));
        Assert.Equal(HttpStatusCode.NotFound, (await e.Admin.GetAsync($"/api/combos/{combo.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await e.Admin.DeleteAsync($"/api/combos/{combo.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(e.Admin, Alta(6, [Art(a), Art(b)]))).StatusCode);
    }

    [Theory]
    [InlineData("inexistente")]
    [InlineData("de_otro_tenant")]
    public async Task LaBajaDeUnIdQueNoExisteODeOtroTenantDa404YNoTocaAlOtro(string caso)
    {
        using var e = await apoyo.PrepararAsync(nameof(LaBajaDeUnIdQueNoExisteODeOtroTenantDa404YNoTocaAlOtro) + caso);
        using var otro = await apoyo.PrepararAsync(nameof(LaBajaDeUnIdQueNoExisteODeOtroTenantDa404YNoTocaAlOtro) + caso + "-ajeno");
        var ajeno = await CrearAsync(
            otro.Admin, Alta(null, [Art(await ArticuloAsync(otro, "x")), Art(await ArticuloAsync(otro, "y"))]));

        var respuesta = await e.Admin.DeleteAsync($"/api/combos/{(caso == "inexistente" ? 999_999_999 : ajeno.Id)}");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("no_encontrado", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Null((await ComboEnBaseAsync(ajeno.Id)).DeletedAt);
        Assert.Equal(2, (await ComponentesEnBaseAsync(ajeno.Id)).Count);
    }

    [Fact]
    public async Task UnPutSobreUnComboDeOtroTenantNoLoModifica()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnPutSobreUnComboDeOtroTenantNoLoModifica));
        using var otro = await apoyo.PrepararAsync(nameof(UnPutSobreUnComboDeOtroTenantNoLoModifica) + "-ajeno");
        var x = await ArticuloAsync(otro, "x");
        var y = await ArticuloAsync(otro, "y");
        var ajeno = await CrearAsync(otro.Admin, Alta(1, [Art(x), Art(y)], nombre: "ajeno"));

        var respuesta = await PutAsync(e.Admin, ajeno.Id, Edicion(null, [Art(x), Art(y)], nombre: "secuestrado"));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("ajeno", (await ComboEnBaseAsync(ajeno.Id)).Nombre);
    }

    // =================================================================================================
    // Autorización
    // =================================================================================================

    [Fact]
    public async Task UnVendedorLeeLosCombosPeroNoLosEscribe()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnVendedorLeeLosCombosPeroNoLosEscribe));
        using var vendedor = await apoyo.ClienteConRolAsync(e, RolConocido.Vendedor);
        var a = await ArticuloAsync(e, "a");
        var b = await ArticuloAsync(e, "b");
        var combo = await CrearAsync(e.Admin, Alta(null, [Art(a), Art(b)]));

        Assert.Single(await ListarAsync(vendedor));
        Assert.Equal(HttpStatusCode.OK, (await vendedor.GetAsync($"/api/combos/{combo.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(vendedor, Alta(null, [Art(a), Art(b)]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(vendedor, combo.Id, Edicion(null, [Art(a), Art(b)]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vendedor.DeleteAsync($"/api/combos/{combo.Id}")).StatusCode);
        Assert.Null((await ComboEnBaseAsync(combo.Id)).DeletedAt);
    }

    private sealed class ComboListadoComparer : IEqualityComparer<ComboListado>
    {
        public bool Equals(ComboListado? x, ComboListado? y) =>
            x is not null && y is not null
            && (x.Id, x.Codigo, x.Nombre, x.Precio, x.IdEmpresa, x.Activo) == (y.Id, y.Codigo, y.Nombre, y.Precio, y.IdEmpresa, y.Activo)
            && x.Componentes.SequenceEqual(y.Componentes);

        public int GetHashCode(ComboListado obj) => obj.Id;
    }
}
