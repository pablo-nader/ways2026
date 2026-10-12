using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Ways.Application.Articulos;
using Ways.Application.Precios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Precios;
using static Ways.IntegrationTests.ApoyoDeAgrupacion;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// El artículo que se vende por importe (<see cref="UnidadVenta.Importe"/>) contra Postgres real: nunca acumula en la
/// venta, es el único que puede tener una tecla de atajo del POS (F5 a F8) y esa tecla es única por tenant entre los
/// artículos vivos. Los pedidos que rompen una invariante se rechazan, no se corrigen. Cubre el alta, la edición y sus
/// transiciones, los miembros de una familia, la agrupación, los respaldos de la base (SQLSTATE y carrera) y las
/// lecturas que exponen la tecla.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ArticulosPorImporteTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private static JsonObject Cuerpo(Entorno e, string nombre, string unidad = "Importe") => new()
    {
        ["nombre"] = nombre,
        ["idArea"] = e.Areas[0],
        ["idAlicuotaIva"] = e.Alicuotas[0],
        ["unidadVenta"] = unidad,
        ["esProducto"] = true,
        ["disponibleParaTodas"] = true,
        ["activo"] = true,
        ["controlaLote"] = false
    };

    private static Task<HttpResponseMessage> PostAsync(HttpClient admin, JsonObject cuerpo) =>
        admin.PostAsJsonAsync("/api/articulos", cuerpo);

    private static async Task<ArticuloListado> LeerListadoAsync(HttpResponseMessage respuesta)
    {
        Assert.True(
            respuesta.IsSuccessStatusCode,
            $"Esperaba éxito y recibió {(int)respuesta.StatusCode}: {await respuesta.Content.ReadAsStringAsync()}");

        return (await respuesta.Content.ReadFromJsonAsync<ArticuloListado>(OpcionesJson))!;
    }

    private static ValoresCompartidosDeFamilia ValoresImporte(Entorno e) =>
        ValoresBase(e) with { UnidadVenta = UnidadVenta.Importe, AcumulaEnVenta = false };

    private static ValoresCompartidosDeFamilia ValoresDeUnidad(Entorno e) => ValoresBase(e);

    private static async Task AfirmarProblemaAsync(HttpResponseMessage respuesta, HttpStatusCode estado, string codigo)
    {
        Assert.Equal(estado, respuesta.StatusCode);
        Assert.Equal(codigo, (await ProblemaAsync(respuesta)).Codigo);
    }

    private async Task<PostgresException> EscrituraCrudaRechazadaAsync(Entorno e, string sql, params object[] parametros)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = valor });
        }

        return await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
    }

    // =================================================================================================
    // Alta
    // =================================================================================================

    /// <summary>Sin <c>acumulaEnVenta</c> en el pedido, el artículo por importe nace con el único valor válido (false);
    /// uno por unidad conserva el default de siempre (true). La tecla viaja en la respuesta y queda guardada.</summary>
    [Fact]
    public async Task ElAltaSinAcumulaEnVentaTomaElUnicoValorValidoDeSuUnidad()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElAltaSinAcumulaEnVentaTomaElUnicoValorValidoDeSuUnidad));
        var cuerpo = Cuerpo(e, "fiambreria");
        cuerpo["teclaAtajo"] = "F6";

        var importe = await LeerListadoAsync(await PostAsync(e.Admin, cuerpo));
        var unidad = await LeerListadoAsync(await PostAsync(e.Admin, Cuerpo(e, "pan", "Unidad")));

        Assert.Equal((UnidadVenta.Importe, false, TeclaAtajo.F6), (importe.UnidadVenta, importe.AcumulaEnVenta, importe.TeclaAtajo));
        var guardado = await apoyo.LeerAsync(importe.Id);
        Assert.Equal((UnidadVenta.Importe, false, TeclaAtajo.F6), (guardado.UnidadVenta, guardado.AcumulaEnVenta, guardado.TeclaAtajo));
        Assert.Equal((UnidadVenta.Unidad, true, (TeclaAtajo?)null), (unidad.UnidadVenta, unidad.AcumulaEnVenta, unidad.TeclaAtajo));
        Assert.True((await apoyo.LeerAsync(unidad.Id)).AcumulaEnVenta);
    }

    [Fact]
    public async Task ElAltaPorImporteConAcumulaEnVentaExplicitoEsRechazadaYNoDejaNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElAltaPorImporteConAcumulaEnVentaExplicitoEsRechazadaYNoDejaNada));
        var cuerpo = Cuerpo(e, "verduleria");
        cuerpo["acumulaEnVenta"] = true;

        await AfirmarProblemaAsync(await PostAsync(e.Admin, cuerpo), HttpStatusCode.BadRequest, "importe_no_acumula");

        Assert.Empty(await ListarPorNombreAsync(e, "verduleria"));
    }

    [Theory]
    [InlineData("Unidad")]
    [InlineData("Peso")]
    public async Task UnaTeclaDeAtajoEnUnArticuloQueNoEsPorImporteEsRechazadaEnElAlta(string unidad)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaTeclaDeAtajoEnUnArticuloQueNoEsPorImporteEsRechazadaEnElAlta) + unidad);
        var cuerpo = Cuerpo(e, "con tecla", unidad);
        cuerpo["teclaAtajo"] = "F5";

        await AfirmarProblemaAsync(await PostAsync(e.Admin, cuerpo), HttpStatusCode.BadRequest, "tecla_atajo_solo_importe");

        Assert.Empty(await ListarPorNombreAsync(e, "con tecla"));
    }

    /// <summary>El JSON del servidor acepta el ordinal además del nombre: uno fuera del enum no llega al CHECK.</summary>
    [Fact]
    public async Task UnOrdinalDeTeclaFueraDelEnumEsRechazadoEnElAltaYEnLaEdicion()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnOrdinalDeTeclaFueraDelEnumEsRechazadoEnElAltaYEnLaEdicion));
        var cuerpo = Cuerpo(e, "ordinal malo");
        cuerpo["teclaAtajo"] = 99;

        await AfirmarProblemaAsync(await PostAsync(e.Admin, cuerpo), HttpStatusCode.BadRequest, "tecla_atajo_invalida");

        var id = await apoyo.SembrarArticuloAsync(e, "existente", ValoresImporte(e));
        var edicion = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
            EdicionIgualA(await apoyo.LeerAsync(id)), OpcionesJson))!.AsObject();
        edicion["teclaAtajo"] = 99;

        await AfirmarProblemaAsync(
            await e.Admin.PutAsJsonAsync($"/api/articulos/{id}", edicion), HttpStatusCode.BadRequest, "tecla_atajo_invalida");
    }

    [Fact]
    public async Task UnaTeclaTomadaPorOtroArticuloVivoEsUn409PeroOtroTenantYUnaBajaLaLiberan()
    {
        using var a = await apoyo.PrepararAsync(nameof(UnaTeclaTomadaPorOtroArticuloVivoEsUn409PeroOtroTenantYUnaBajaLaLiberan) + "A");
        using var b = await apoyo.PrepararAsync(nameof(UnaTeclaTomadaPorOtroArticuloVivoEsUn409PeroOtroTenantYUnaBajaLaLiberan) + "B");

        JsonObject ConTecla(Entorno e, string nombre, string tecla)
        {
            var cuerpo = Cuerpo(e, nombre);
            cuerpo["teclaAtajo"] = tecla;
            return cuerpo;
        }

        var primero = await LeerListadoAsync(await PostAsync(a.Admin, ConTecla(a, "primero", "F5")));

        await AfirmarProblemaAsync(
            await PostAsync(a.Admin, ConTecla(a, "segundo", "F5")), HttpStatusCode.Conflict, "tecla_atajo_duplicada");
        Assert.Empty(await ListarPorNombreAsync(a, "segundo"));

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(a.Admin, ConTecla(a, "otra tecla", "F6"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(b.Admin, ConTecla(b, "otro tenant", "F5"))).StatusCode);

        await apoyo.DarDeBajaAsync("articulos", "id_articulo", primero.Id);

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(a.Admin, ConTecla(a, "tercero", "F5"))).StatusCode);
    }

    // =================================================================================================
    // Respaldos de la base
    // =================================================================================================

    /// <summary>Dos altas simultáneas con la misma tecla: exactamente una gana y la otra recibe el 409 con el código
    /// de dominio. Que el 23505 del índice llegue a ese código lo prueba la prueba de SQLSTATE de abajo, que escribe sin
    /// pasar por el pre-chequeo.</summary>
    [Fact]
    public async Task DosAltasConcurrentesConLaMismaTeclaDanExactamenteUnGanador()
    {
        using var e = await apoyo.PrepararAsync(nameof(DosAltasConcurrentesConLaMismaTeclaDanExactamenteUnGanador));
        var altaA = Cuerpo(e, "carrera A");
        altaA["teclaAtajo"] = "F8";
        var altaB = Cuerpo(e, "carrera B");
        altaB["teclaAtajo"] = "F8";

        var respuestas = await Task.WhenAll(PostAsync(e.Admin, altaA), PostAsync(e.Admin, altaB));

        Assert.Equal(1, respuestas.Count(r => r.StatusCode == HttpStatusCode.Created));
        var perdedora = Assert.Single(respuestas, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("tecla_atajo_duplicada", (await ProblemaAsync(perdedora)).Codigo);
    }

    /// <summary>El respaldo de la carrera es el índice: una escritura que esquiva el pre-chequeo choca con 23505
    /// <c>ux_articulos_tecla_atajo</c>.</summary>
    [Fact]
    public async Task LaBaseRechazaUnaSegundaTeclaIgualEntreArticulosVivosConElSqlstateDelIndice()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaBaseRechazaUnaSegundaTeclaIgualEntreArticulosVivosConElSqlstateDelIndice));
        await apoyo.SembrarArticuloAsync(e, "tiene la tecla", ValoresImporte(e), teclaAtajo: TeclaAtajo.F7);
        var otro = await apoyo.SembrarArticuloAsync(e, "la quiere", ValoresImporte(e));

        var excepcion = await EscrituraCrudaRechazadaAsync(
            e, "UPDATE articulos SET tecla_atajo = 'F7' WHERE id_articulo = $1", otro);

        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_articulos_tecla_atajo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task LaBaseRechazaLosEstadosQueRompenLasTresInvariantesConSuCheck()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaBaseRechazaLosEstadosQueRompenLasTresInvariantesConSuCheck));
        var importe = await apoyo.SembrarArticuloAsync(e, "por importe", ValoresImporte(e));
        var porUnidad = await apoyo.SembrarArticuloAsync(e, "por unidad", ValoresDeUnidad(e));

        var fueraDelRango = await EscrituraCrudaRechazadaAsync(
            e, "UPDATE articulos SET tecla_atajo = 'F9' WHERE id_articulo = $1", importe);
        var enUnaUnidad = await EscrituraCrudaRechazadaAsync(
            e, "UPDATE articulos SET tecla_atajo = 'F5' WHERE id_articulo = $1", porUnidad);
        var queAcumula = await EscrituraCrudaRechazadaAsync(
            e, "UPDATE articulos SET acumula_en_venta = true WHERE id_articulo = $1", importe);

        Assert.Equal(
            [("23514", "ck_articulos_tecla_atajo"), ("23514", "ck_articulos_tecla_atajo_solo_importe"), ("23514", "ck_articulos_importe_no_acumula")],
            [(fueraDelRango.SqlState, fueraDelRango.ConstraintName), (enUnaUnidad.SqlState, enUnaUnidad.ConstraintName), (queAcumula.SqlState, queAcumula.ConstraintName)]);
    }

    // =================================================================================================
    // Edición y transiciones
    // =================================================================================================

    [Fact]
    public async Task PasarDeImporteAOtraUnidadExigeSacarLaTeclaYAsiQuedaConsistente()
    {
        using var e = await apoyo.PrepararAsync(nameof(PasarDeImporteAOtraUnidadExigeSacarLaTeclaYAsiQuedaConsistente));
        var id = await apoyo.SembrarArticuloAsync(e, "rubro", ValoresImporte(e), teclaAtajo: TeclaAtajo.F5);
        var guardado = await apoyo.LeerAsync(id);

        var conTecla = EdicionIgualA(guardado) with { UnidadVenta = UnidadVenta.Unidad, AcumulaEnVenta = true };
        await AfirmarProblemaAsync(await PutArticuloAsync(e.Admin, id, conTecla), HttpStatusCode.BadRequest, "tecla_atajo_solo_importe");
        Assert.Equal((UnidadVenta.Importe, TeclaAtajo.F5), ((await apoyo.LeerAsync(id)).UnidadVenta, (await apoyo.LeerAsync(id)).TeclaAtajo));

        var sinTecla = conTecla with { TeclaAtajo = null };
        var respuesta = await PutArticuloAsync(e.Admin, id, sinTecla);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var despues = await apoyo.LeerAsync(id);
        Assert.Equal((UnidadVenta.Unidad, true, (TeclaAtajo?)null), (despues.UnidadVenta, despues.AcumulaEnVenta, despues.TeclaAtajo));
    }

    [Fact]
    public async Task PasarAImporteExigeQueElEstadoResultanteNoAcumule()
    {
        using var e = await apoyo.PrepararAsync(nameof(PasarAImporteExigeQueElEstadoResultanteNoAcumule));
        var id = await apoyo.SembrarArticuloAsync(e, "rubro", ValoresDeUnidad(e));
        var guardado = await apoyo.LeerAsync(id);

        // acumulaEnVenta ausente conserva el guardado (true): el estado resultante es inválido y se rechaza.
        var conservandoElGuardado = EdicionIgualA(guardado) with { UnidadVenta = UnidadVenta.Importe, AcumulaEnVenta = null };
        await AfirmarProblemaAsync(
            await PutArticuloAsync(e.Admin, id, conservandoElGuardado), HttpStatusCode.BadRequest, "importe_no_acumula");

        var explicito = conservandoElGuardado with { AcumulaEnVenta = true };
        await AfirmarProblemaAsync(
            await PutArticuloAsync(e.Admin, id, explicito), HttpStatusCode.BadRequest, "importe_no_acumula");
        Assert.Equal(UnidadVenta.Unidad, (await apoyo.LeerAsync(id)).UnidadVenta);

        var valido = conservandoElGuardado with { AcumulaEnVenta = false, TeclaAtajo = TeclaAtajo.F8 };
        var respuesta = await PutArticuloAsync(e.Admin, id, valido);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var despues = await apoyo.LeerAsync(id);
        Assert.Equal((UnidadVenta.Importe, false, (TeclaAtajo?)TeclaAtajo.F8), (despues.UnidadVenta, despues.AcumulaEnVenta, despues.TeclaAtajo));
    }

    /// <summary>Una tecla en un artículo por unidad es un 400 en la edición, con el artículo intacto.</summary>
    [Fact]
    public async Task UnaTeclaEnUnArticuloQueNoEsPorImporteEsRechazadaEnLaEdicion()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaTeclaEnUnArticuloQueNoEsPorImporteEsRechazadaEnLaEdicion));
        var id = await apoyo.SembrarArticuloAsync(e, "unitario", ValoresDeUnidad(e));

        var respuesta = await PutArticuloAsync(
            e.Admin, id, EdicionIgualA(await apoyo.LeerAsync(id)) with { TeclaAtajo = TeclaAtajo.F5 });

        await AfirmarProblemaAsync(respuesta, HttpStatusCode.BadRequest, "tecla_atajo_solo_importe");
        Assert.Null((await apoyo.LeerAsync(id)).TeclaAtajo);
    }

    [Fact]
    public async Task EditarLaTeclaLaCambiaLaConservaSinChocarConSiMismaYLaSacaConNull()
    {
        using var e = await apoyo.PrepararAsync(nameof(EditarLaTeclaLaCambiaLaConservaSinChocarConSiMismaYLaSacaConNull));
        var id = await apoyo.SembrarArticuloAsync(e, "mio", ValoresImporte(e), teclaAtajo: TeclaAtajo.F5);
        var ajeno = await apoyo.SembrarArticuloAsync(e, "ajeno", ValoresImporte(e), teclaAtajo: TeclaAtajo.F6);

        var igual = await PutArticuloAsync(e.Admin, id, EdicionIgualA(await apoyo.LeerAsync(id)) with { Nombre = "mio renombrado" });
        Assert.Equal(HttpStatusCode.OK, igual.StatusCode);
        Assert.Equal(TeclaAtajo.F5, (await apoyo.LeerAsync(id)).TeclaAtajo);

        var choque = await PutArticuloAsync(e.Admin, id, EdicionIgualA(await apoyo.LeerAsync(id)) with { TeclaAtajo = TeclaAtajo.F6 });
        await AfirmarProblemaAsync(choque, HttpStatusCode.Conflict, "tecla_atajo_duplicada");
        Assert.Equal(TeclaAtajo.F5, (await apoyo.LeerAsync(id)).TeclaAtajo);
        Assert.Equal(TeclaAtajo.F6, (await apoyo.LeerAsync(ajeno)).TeclaAtajo);

        var cambio = await PutArticuloAsync(e.Admin, id, EdicionIgualA(await apoyo.LeerAsync(id)) with { TeclaAtajo = TeclaAtajo.F7 });
        Assert.Equal(HttpStatusCode.OK, cambio.StatusCode);
        Assert.Equal(TeclaAtajo.F7, (await apoyo.LeerAsync(id)).TeclaAtajo);

        var libre = await PutArticuloAsync(e.Admin, ajeno, EdicionIgualA(await apoyo.LeerAsync(ajeno)) with { TeclaAtajo = TeclaAtajo.F5 });
        Assert.Equal(HttpStatusCode.OK, libre.StatusCode);

        var sacada = await PutArticuloAsync(e.Admin, id, EdicionIgualA(await apoyo.LeerAsync(id)) with { TeclaAtajo = null });
        Assert.Equal(HttpStatusCode.OK, sacada.StatusCode);
        Assert.Null((await apoyo.LeerAsync(id)).TeclaAtajo);
    }

    // =================================================================================================
    // Familias
    // =================================================================================================

    /// <summary>La unidad de venta es un campo compartido: cambiarla para toda la familia a una que no es por importe
    /// mientras OTRO miembro tiene tecla se rechaza con 409 y no cambia nada; con "solo este" el artículo sale de la
    /// familia y la edición es válida.</summary>
    [Fact]
    public async Task CambiarLaUnidadDeLaFamiliaAUnaQueNoEsPorImporteConUnaTeclaEnOtroMiembroEsUn409()
    {
        using var e = await apoyo.PrepararAsync(nameof(CambiarLaUnidadDeLaFamiliaAUnaQueNoEsPorImporteConUnaTeclaEnOtroMiembroEsUn409));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Rubros");
        var sinTecla = await apoyo.SembrarArticuloAsync(e, "sin tecla", ValoresImporte(e), familia);
        var conTecla = await apoyo.SembrarArticuloAsync(e, "con tecla", ValoresImporte(e), familia, teclaAtajo: TeclaAtajo.F5);

        var aUnidad = EdicionIgualA(await apoyo.LeerAsync(sinTecla), AlcanceDeFamilia.Familia) with
        {
            UnidadVenta = UnidadVenta.Unidad, AcumulaEnVenta = true
        };
        var rechazada = await PutArticuloAsync(e.Admin, sinTecla, aUnidad);

        await AfirmarProblemaAsync(rechazada, HttpStatusCode.Conflict, "familia_miembro_con_tecla_atajo");
        Assert.Equal(
            [(UnidadVenta.Importe, TeclaAtajo.F5, false)],
            [((await apoyo.LeerAsync(conTecla)).UnidadVenta, (await apoyo.LeerAsync(conTecla)).TeclaAtajo, (await apoyo.LeerAsync(conTecla)).AcumulaEnVenta)]);
        Assert.Equal(UnidadVenta.Importe, (await apoyo.LeerAsync(sinTecla)).UnidadVenta);

        var soloEste = await PutArticuloAsync(
            e.Admin, sinTecla, aUnidad with { Alcance = AlcanceDeFamilia.SoloEste });

        Assert.Equal(HttpStatusCode.OK, soloEste.StatusCode);
        Assert.Equal(UnidadVenta.Unidad, (await apoyo.LeerAsync(sinTecla)).UnidadVenta);
        Assert.Equal((UnidadVenta.Importe, TeclaAtajo.F5), ((await apoyo.LeerAsync(conTecla)).UnidadVenta, (await apoyo.LeerAsync(conTecla)).TeclaAtajo));
    }

    [Fact]
    public async Task CambiarLaUnidadDeLaFamiliaAImporteSinSacarElAcumuladoEs400YConAcumuladoFalsoLaAlineaATodos()
    {
        using var e = await apoyo.PrepararAsync(nameof(CambiarLaUnidadDeLaFamiliaAImporteSinSacarElAcumuladoEs400YConAcumuladoFalsoLaAlineaATodos));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Rubros");
        var uno = await apoyo.SembrarArticuloAsync(e, "uno", ValoresDeUnidad(e), familia);
        var dos = await apoyo.SembrarArticuloAsync(e, "dos", ValoresDeUnidad(e), familia);

        var sinSacarElAcumulado = EdicionIgualA(await apoyo.LeerAsync(uno), AlcanceDeFamilia.Familia) with
        {
            UnidadVenta = UnidadVenta.Importe, AcumulaEnVenta = null
        };
        await AfirmarProblemaAsync(
            await PutArticuloAsync(e.Admin, uno, sinSacarElAcumulado), HttpStatusCode.BadRequest, "importe_no_acumula");
        Assert.Equal(UnidadVenta.Unidad, (await apoyo.LeerAsync(dos)).UnidadVenta);

        var respuesta = await PutArticuloAsync(e.Admin, uno, sinSacarElAcumulado with { AcumulaEnVenta = false });

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        foreach (var id in new[] { uno, dos })
        {
            var miembro = await apoyo.LeerAsync(id);
            Assert.Equal((UnidadVenta.Importe, false), (miembro.UnidadVenta, miembro.AcumulaEnVenta));
        }
    }

    /// <summary>Un miembro nuevo de una familia por importe nace con acumulaEnVenta false aun sin mandarlo, así que
    /// iguala a la referencia en lugar de chocar por <c>familia_valores_distintos</c>.</summary>
    [Fact]
    public async Task UnMiembroNuevoPorImporteSinAcumulaEnVentaIgualaALaReferenciaDeLaFamilia()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnMiembroNuevoPorImporteSinAcumulaEnVentaIgualaALaReferenciaDeLaFamilia));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Rubros");
        await apoyo.SembrarArticuloAsync(e, "referencia", ValoresImporte(e), familia);
        var cuerpo = Cuerpo(e, "nuevo");
        cuerpo["idFamilia"] = familia;
        cuerpo["unidadesPorBulto"] = 6m;
        cuerpo["costoLista"] = 50m;
        cuerpo["idCategoria"] = e.Categorias[0];
        cuerpo["idGrupo"] = e.Grupos[0];
        cuerpo["idProveedorHabitual"] = e.Proveedores[0];
        cuerpo["descuentoProveedor"] = 10m;

        var creado = await LeerListadoAsync(await PostAsync(e.Admin, cuerpo));

        Assert.Equal((familia, false), (creado.IdFamilia, creado.AcumulaEnVenta));
    }

    /// <summary>Agrupar alinea la unidad de venta del destino con la de la referencia: un destino con tecla no puede
    /// pasar a una referencia que no es por importe, ni en la previsualización ni en el pedido real.</summary>
    [Fact]
    public async Task AgruparUnDestinoConTeclaConUnaReferenciaQueNoEsPorImporteEsUn409YUnProblemaEnLaPrevia()
    {
        using var e = await apoyo.PrepararAsync(nameof(AgruparUnDestinoConTeclaConUnaReferenciaQueNoEsPorImporteEsUn409YUnProblemaEnLaPrevia));
        var referencia = await apoyo.SembrarArticuloAsync(e, "referencia", ValoresDeUnidad(e));
        var conTecla = await apoyo.SembrarArticuloAsync(e, "con tecla", ValoresImporte(e), teclaAtajo: TeclaAtajo.F5);
        var sinTecla = await apoyo.SembrarArticuloAsync(e, "sin tecla", ValoresImporte(e));

        var previa = await LeerPrevisualizacionAsync(await PostPrevisualizarAsync(e.Admin, referencia, conTecla, sinTecla));
        var problema = Assert.Single(previa.Problemas);
        Assert.Equal(("articulo_con_tecla_atajo", (int?)conTecla), (problema.Codigo, problema.IdArticulo));

        var rechazada = await PostCrearAsync(e.Admin, "Mixta", referencia, conTecla, sinTecla);
        await AfirmarProblemaAsync(rechazada, HttpStatusCode.Conflict, "articulo_con_tecla_atajo");
        var intacto = await apoyo.LeerAsync(conTecla);
        Assert.Equal((UnidadVenta.Importe, TeclaAtajo.F5, (int?)null), (intacto.UnidadVenta, intacto.TeclaAtajo, intacto.IdFamilia));

        var admitido = await PostCrearAsync(e.Admin, "Sin la tecla", referencia, sinTecla);
        await LeerResultadoAsync(admitido, HttpStatusCode.Created);
    }

    [Fact]
    public async Task AgruparUnDestinoConTeclaConUnaReferenciaPorImporteConservaLaTecla()
    {
        using var e = await apoyo.PrepararAsync(nameof(AgruparUnDestinoConTeclaConUnaReferenciaPorImporteConservaLaTecla));
        var referencia = await apoyo.SembrarArticuloAsync(e, "referencia", ValoresImporte(e));
        var conTecla = await apoyo.SembrarArticuloAsync(e, "con tecla", ValoresImporte(e), teclaAtajo: TeclaAtajo.F8);

        var creada = await PostCrearAsync(e.Admin, "Importes", referencia, conTecla);

        await LeerResultadoAsync(creada, HttpStatusCode.Created);
        var miembro = await apoyo.LeerAsync(conTecla);
        Assert.Equal((TeclaAtajo.F8, UnidadVenta.Importe), (miembro.TeclaAtajo, miembro.UnidadVenta));
    }

    // =================================================================================================
    // Lecturas
    // =================================================================================================

    [Fact]
    public async Task ElListadoElDetalleYElEscaneoExponenLaTeclaDelArticulo()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElListadoElDetalleYElEscaneoExponenLaTeclaDelArticulo));
        var cuerpo = Cuerpo(e, "fiambreria lectura");
        cuerpo["codigoInterno"] = "7001";
        cuerpo["teclaAtajo"] = "F7";
        var creado = await LeerListadoAsync(await PostAsync(e.Admin, cuerpo));
        var sinTecla = Cuerpo(e, "pan lectura", "Unidad");
        sinTecla["codigoInterno"] = "7002";
        await LeerListadoAsync(await PostAsync(e.Admin, sinTecla));

        var detalle = await e.Admin.GetFromJsonAsync<ArticuloListado>($"/api/articulos/{creado.Id}", OpcionesJson);
        var listado = await e.Admin.GetFromJsonAsync<Ways.Application.Usuarios.PaginaDe<ArticuloListado>>(
            "/api/articulos?busqueda=lectura&tamanio=50", OpcionesJson);
        var escaneado = await e.Admin.GetFromJsonAsync<ArticuloEscaneado>("/api/articulos/escaneo?entrada=7001", OpcionesJson);
        var escaneadoSinTecla = await e.Admin.GetFromJsonAsync<ArticuloEscaneado>("/api/articulos/escaneo?entrada=7002", OpcionesJson);

        Assert.Equal(TeclaAtajo.F7, detalle!.TeclaAtajo);
        Assert.Equal(
            [("fiambreria lectura", (TeclaAtajo?)TeclaAtajo.F7), ("pan lectura", null)],
            listado!.Items.OrderBy(i => i.Nombre).Select(i => (i.Nombre, i.TeclaAtajo)).ToArray());
        Assert.Equal((UnidadVenta.Importe, TeclaAtajo.F7), (escaneado!.UnidadVenta, escaneado.TeclaAtajo));
        Assert.Null(escaneadoSinTecla!.TeclaAtajo);
    }

    private static async Task<List<ArticuloListado>> ListarPorNombreAsync(Entorno e, string nombre)
    {
        var pagina = await e.Admin.GetFromJsonAsync<Ways.Application.Usuarios.PaginaDe<ArticuloListado>>(
            $"/api/articulos?busqueda={Uri.EscapeDataString(nombre)}&tamanio=50", OpcionesJson);

        return [.. pagina!.Items.Where(i => i.Nombre == nombre)];
    }
}
