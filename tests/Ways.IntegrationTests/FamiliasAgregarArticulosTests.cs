using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Ways.Application.Familias;
using Ways.Domain.Articulos;
using Ways.Domain.Precios;
using static Ways.IntegrationTests.ApoyoDeAgrupacion;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>POST /api/familias/{id}/articulos</c> (doc 10 §3, "Familias de artículos") contra Postgres real: suma artículos a una
/// familia que ya existe, ALINEADOS con su artículo de referencia —el miembro vivo de menor id— en los trece campos
/// compartidos y en el estado de precios de cada lista fija. Es todo o nada y no mueve a nadie: un artículo de otra familia
/// se rechaza. Los locks y la atomicidad están en <c>FamiliasAgrupacionConcurrenciaTests</c>.
///
/// <para>El escenario es el de <see cref="ApoyoDeAgrupacion"/> con familia: la referencia y su gemelo son los miembros
/// vivos, el miembro dado de baja tiene el id MÁS bajo y valores y precios distintos (usarlo de referencia se vería), y los
/// destinos tienen ids más bajos que la referencia (elegir el menor entre los involucrados se vería).</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FamiliasAgregarArticulosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private async Task<List<Articulo>> LeerAsync(params int[] ids)
    {
        var articulos = new List<Articulo>(ids.Length);
        foreach (var id in ids)
        {
            articulos.Add(await apoyo.LeerAsync(id));
        }

        return articulos;
    }

    private Task<string> FotoAsync(Escenario s) =>
        FotoDeLaBaseAsync(fixture, apoyo, s.E, s.TodosLosArticulos, s.ListasFijas);

    /// <summary>Un artículo que ya está en la familia pero con otros valores y otros precios: la invariante rota a
    /// propósito. Se escribe directo porque ninguna operación de la API lo permite.</summary>
    private async Task MoverAFamiliaAsync(int idArticulo, int idFamilia)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = new NpgsqlCommand("UPDATE articulos SET id_familia = $1 WHERE id_articulo = $2", cruda);
        comando.Parameters.Add(new NpgsqlParameter { Value = idFamilia });
        comando.Parameters.Add(new NpgsqlParameter { Value = idArticulo });

        Assert.Equal(1, await comando.ExecuteNonQueryAsync());
    }

    // =================================================================================================
    // El camino feliz
    // =================================================================================================

    /// <summary>El pedido completo, con los ids desordenados y repetidos: los tres destinos entran a la familia, alineados
    /// con la referencia —no con el miembro dado de baja, que tiene el id más bajo, ni con un destino, que también lo
    /// tienen más bajo que ella—: sus trece valores, sus precios y la auditoría de cada fila escrita son los del
    /// escenario de crear. Con UN solo "ahora" en los artículos y en las filas de precio. La familia, la referencia, su
    /// gemelo, el miembro dado de baja y todo lo que el pedido no nombra quedan exactamente como estaban, también su
    /// <c>updated_at</c>; el resultado es el de la previsualización del mismo pedido.</summary>
    [Fact]
    public async Task AgregarAlineaConElMiembroVivoDeMenorIdYNoTocaNadaMas()
    {
        var s = await SembrarAsync(apoyo, nameof(AgregarAlineaConElMiembroVivoDeMenorIdYNoTocaNadaMas), enFamilia: true);
        using var e = s.E;
        var general = e.IdListaGeneral;
        var mayorista = e.IdListaMayorista;
        var familiaId = s.Familia!.Value;
        var valoresDeLaReferencia = s.ValoresDeLaReferencia;

        var d1Antes = await apoyo.LeerAsync(s.D1);
        var d2Antes = await apoyo.LeerAsync(s.D2);
        var d3Antes = await apoyo.LeerAsync(s.D3);
        var d1GeneralAntes = await apoyo.FilasDePrecioAsync(s.D1, general);
        var d3MayoristaAntes = await apoyo.FilasDePrecioAsync(s.D3, mayorista);
        var d2PreciosAntes = (await apoyo.FilasDePrecioAsync(s.D2, general)).Concat(await apoyo.FilasDePrecioAsync(s.D2, mayorista))
            .Select(p => (p.Id, p.Monto, p.VigenteDesde, p.VigenteHasta, p.UpdatedAt)).ToList();
        var sinTocarAntes = (await LeerAsync(s.Referencia, s.Gemelo, s.DeBaja, s.Suelto, s.DeOtraFamilia)).Select(Huella).ToList();
        var familiaAntes = await apoyo.LeerFamiliaAsync(familiaId);
        var preciosDeLaFamiliaAntes = new List<List<(decimal, DateTimeOffset, DateTimeOffset?, DateTimeOffset)>>();
        foreach (var miembro in new[] { s.Referencia, s.Gemelo, s.DeBaja })
        {
            preciosDeLaFamiliaAntes.Add([.. (await apoyo.FilasDePrecioAsync(miembro, general)).Select(p => (p.Monto, p.VigenteDesde, p.VigenteHasta, p.UpdatedAt))]);
        }

        var previa = await LeerPrevisualizacionAsync(await PostPrevisualizarAsync(e.Admin, s.Referencia, s.D3, s.D1, s.D2));
        Assert.Empty(previa.Problemas);

        var respuesta = await PostAgregarAsync(e.Admin, familiaId, s.D3, s.D1, s.D1, s.D2);

        var resultado = await LeerResultadoAsync(respuesta, HttpStatusCode.OK);
        Assert.Equal((familiaId, "Gaseosas", s.Referencia), (resultado.IdFamilia, resultado.Nombre, resultado.IdArticuloReferencia));
        Assert.Equal([s.D1, s.D2, s.D3], resultado.Articulos.Select(a => a.IdArticulo));
        Assert.Equal(Json(previa.Articulos), Json(resultado.Articulos));

        var estadoDeLaReferencia = new EstadoDePrecios(100m, new PrecioPendiente(130m, s.V));
        var d1 = resultado.Articulos[0];
        Assert.Equal(CamposDeD1, d1.Campos);
        Assert.Equal(ComoLoLeeElCliente(ValoresCompartidosDeFamilia.De(d1Antes)), d1.Actual);
        Assert.Equal(ComoLoLeeElCliente(valoresDeLaReferencia), d1.Nuevo);
        Assert.Equal(
            [
                new CambioDePreciosDeLista(general, new EstadoDePrecios(90m, null), estadoDeLaReferencia),
                new CambioDePreciosDeLista(mayorista, EstadoDePrecios.Vacio, new EstadoDePrecios(80m, null))
            ],
            d1.Precios);

        var d2 = resultado.Articulos[1];
        Assert.Empty(d2.Campos);
        Assert.Empty(d2.Precios);

        var d3 = resultado.Articulos[2];
        Assert.Equal(CamposDeD3, d3.Campos);
        Assert.Equal(
            [
                new CambioDePreciosDeLista(general, EstadoDePrecios.Vacio, estadoDeLaReferencia),
                new CambioDePreciosDeLista(mayorista, new EstadoDePrecios(70m, new PrecioPendiente(85m, s.W)), new EstadoDePrecios(80m, null))
            ],
            d3.Precios);

        // Los tres destinos entran con un solo instante, y quedan con los valores de la referencia y lo propio intacto.
        var d1Despues = await apoyo.LeerAsync(s.D1);
        var ahora = d1Despues.UpdatedAt;
        Assert.True(ahora > d1Antes.UpdatedAt);

        foreach (var (antes, id) in new[] { (d1Antes, s.D1), (d2Antes, s.D2), (d3Antes, s.D3) })
        {
            var destino = await apoyo.LeerAsync(id);

            Assert.Equal((familiaId, ahora), (destino.IdFamilia, destino.UpdatedAt));
            Assert.Equal(valoresDeLaReferencia, ValoresCompartidosDeFamilia.De(destino));
            Assert.Equal(Propios(antes), Propios(destino));
        }

        var d1General = await apoyo.FilasDePrecioAsync(s.D1, general);
        Assert.Equal(
            [
                (90m, d1GeneralAntes[0].VigenteDesde, (DateTimeOffset?)ahora),
                (100m, ahora, (DateTimeOffset?)s.V),
                (130m, s.V, (DateTimeOffset?)null)
            ],
            Filas(d1General));
        AfirmarUnSoloAhora(d1GeneralAntes, d1General, ahora);

        var d1Mayorista = await apoyo.FilasDePrecioAsync(s.D1, mayorista);
        Assert.Equal([(80m, ahora, (DateTimeOffset?)null)], Filas(d1Mayorista));
        AfirmarUnSoloAhora([], d1Mayorista, ahora);

        var d3General = await apoyo.FilasDePrecioAsync(s.D3, general);
        Assert.Equal([(100m, ahora, (DateTimeOffset?)s.V), (130m, s.V, (DateTimeOffset?)null)], Filas(d3General));
        AfirmarUnSoloAhora([], d3General, ahora);

        var d3Mayorista = await apoyo.FilasDePrecioAsync(s.D3, mayorista);
        Assert.Equal(
            [
                (70m, d3MayoristaAntes[0].VigenteDesde, (DateTimeOffset?)ahora),
                (80m, ahora, (DateTimeOffset?)null),
                (85m, s.W, (DateTimeOffset?)s.W)
            ],
            Filas(d3Mayorista));
        AfirmarUnSoloAhora(d3MayoristaAntes, d3Mayorista, ahora);

        Assert.Equal(
            d2PreciosAntes,
            (await apoyo.FilasDePrecioAsync(s.D2, general)).Concat(await apoyo.FilasDePrecioAsync(s.D2, mayorista))
                .Select(p => (p.Id, p.Monto, p.VigenteDesde, p.VigenteHasta, p.UpdatedAt)).ToList());

        // La auditoría: las seis filas del escenario, con el estado que tenía el par justo antes.
        var auditoria = await apoyo.AuditoriaDePreciosAsync(e.IdTenant);
        var registrada = auditoria
            .Select(a => (a.IdEntidad, Anterior: LeerValorDeAuditoria(a.ValorAnterior), Nuevo: LeerValorDeAuditoria(a.ValorNuevo)))
            .OrderBy(a => a.IdEntidad).ThenBy(a => a.Nuevo!.IdListaPrecio).ThenBy(a => a.Nuevo!.VigenteDesde)
            .ToList();
        var d1Desde = AMicrosegundos(d1GeneralAntes[0].VigenteDesde);

        Assert.Equal(
            [
                (s.D1, (ValorDeAuditoria?)new ValorDeAuditoria(general, 90m, d1Desde), new ValorDeAuditoria(general, 100m, ahora)),
                (s.D1, new ValorDeAuditoria(general, 100m, ahora), new ValorDeAuditoria(general, 130m, s.V)),
                (s.D1, null, new ValorDeAuditoria(mayorista, 80m, ahora)),
                (s.D3, null, new ValorDeAuditoria(general, 100m, ahora)),
                (s.D3, new ValorDeAuditoria(general, 100m, ahora), new ValorDeAuditoria(general, 130m, s.V)),
                (s.D3, new ValorDeAuditoria(mayorista, 85m, s.W), new ValorDeAuditoria(mayorista, 80m, ahora))
            ],
            registrada);

        // La familia y los que ya estaban no cambian, ni su updated_at; el miembro dado de baja sigue en ella.
        var familia = await apoyo.LeerFamiliaAsync(familiaId);
        Assert.Equal(
            (familiaAntes.Nombre, familiaAntes.Activo, familiaAntes.CreatedAt, familiaAntes.UpdatedAt, familiaAntes.DeletedAt),
            (familia.Nombre, familia.Activo, familia.CreatedAt, familia.UpdatedAt, familia.DeletedAt));
        Assert.Equal(sinTocarAntes, (await LeerAsync(s.Referencia, s.Gemelo, s.DeBaja, s.Suelto, s.DeOtraFamilia)).Select(Huella).ToList());
        Assert.Equal(familiaId, (await apoyo.LeerAsync(s.DeBaja)).IdFamilia);

        var preciosDeLaFamiliaDespues = new List<List<(decimal, DateTimeOffset, DateTimeOffset?, DateTimeOffset)>>();
        foreach (var miembro in new[] { s.Referencia, s.Gemelo, s.DeBaja })
        {
            preciosDeLaFamiliaDespues.Add([.. (await apoyo.FilasDePrecioAsync(miembro, general)).Select(p => (p.Monto, p.VigenteDesde, p.VigenteHasta, p.UpdatedAt))]);
        }

        Assert.Equal(preciosDeLaFamiliaAntes, preciosDeLaFamiliaDespues);

        var listado = await e.Admin.GetFromJsonAsync<List<FamiliaListado>>("/api/familias", OpcionesJson);
        Assert.Equal(5, listado!.Single(f => f.Id == familiaId).CantidadArticulos);
    }

    /// <summary>Repetir el pedido no cambia nada: los artículos ya son miembros y ya están alineados, así que cada uno trae
    /// las listas vacías y no se escribe ni una fila, ni siquiera un <c>updated_at</c>. La referencia ahora es otra: el
    /// miembro vivo de menor id pasó a ser <c>d1</c>, que entró con un id más bajo que el de la referencia anterior, y
    /// como la referencia no figura entre los artículos del resultado, éste trae a los otros dos.</summary>
    [Fact]
    public async Task RepetirElPedidoNoCambiaNada()
    {
        var s = await SembrarAsync(apoyo, nameof(RepetirElPedidoNoCambiaNada), enFamilia: true);
        using var e = s.E;
        var familiaId = s.Familia!.Value;
        var primera = await LeerResultadoAsync(await PostAgregarAsync(e.Admin, familiaId, s.D1, s.D2, s.D3), HttpStatusCode.OK);
        Assert.Equal(s.Referencia, primera.IdArticuloReferencia);
        var antes = await FotoAsync(s);

        var segunda = await LeerResultadoAsync(await PostAgregarAsync(e.Admin, familiaId, s.D3, s.D2, s.D1), HttpStatusCode.OK);

        Assert.Equal(s.D1, segunda.IdArticuloReferencia);
        Assert.Equal([s.D2, s.D3], segunda.Articulos.Select(a => a.IdArticulo));
        Assert.All(segunda.Articulos, a =>
        {
            Assert.Empty(a.Campos);
            Assert.Empty(a.Precios);
            Assert.Equal(ComoLoLeeElCliente(s.ValoresDeLaReferencia), a.Actual);
        });
        Assert.Equal(antes, await FotoAsync(s));
    }

    /// <summary>Un artículo que ya es miembro de la familia pero se apartó de ella se alinea igual y sigue en la familia:
    /// trae sus cambios y se le escribe <c>updated_at</c>. La referencia en la lista no cuenta como destino, y el gemelo,
    /// que ya está alineado, aparece con las listas vacías y no se toca. El miembro que se apartó tiene un id más alto que
    /// la referencia: con uno más bajo sería él la referencia.</summary>
    [Fact]
    public async Task UnMiembroQueSeApartoSeAlineaIgualYLaReferenciaEnLaListaSeIgnora()
    {
        var s = await SembrarAsync(apoyo, nameof(UnMiembroQueSeApartoSeAlineaIgualYLaReferenciaEnLaListaSeIgnora), enFamilia: true);
        using var e = s.E;
        var familiaId = s.Familia!.Value;
        await MoverAFamiliaAsync(s.D3, familiaId);
        var referenciaAntes = await apoyo.LeerAsync(s.Referencia);
        var gemeloAntes = await apoyo.LeerAsync(s.Gemelo);
        var d3Antes = await apoyo.LeerAsync(s.D3);

        var resultado = await LeerResultadoAsync(
            await PostAgregarAsync(e.Admin, familiaId, s.Referencia, s.D3, s.Gemelo), HttpStatusCode.OK);

        Assert.Equal(s.Referencia, resultado.IdArticuloReferencia);
        Assert.Equal([s.Gemelo, s.D3], resultado.Articulos.Select(a => a.IdArticulo));
        Assert.Empty(resultado.Articulos[0].Campos);
        Assert.Empty(resultado.Articulos[0].Precios);
        Assert.Equal(CamposDeD3, resultado.Articulos[1].Campos);
        Assert.Equal(2, resultado.Articulos[1].Precios.Count);

        var d3 = await apoyo.LeerAsync(s.D3);
        Assert.Equal(familiaId, d3.IdFamilia);
        Assert.True(d3.UpdatedAt > d3Antes.UpdatedAt);
        Assert.Equal(s.ValoresDeLaReferencia, ValoresCompartidosDeFamilia.De(d3));
        Assert.Equal(Propios(d3Antes), Propios(d3));

        Assert.Equal(Huella(referenciaAntes), Huella(await apoyo.LeerAsync(s.Referencia)));
        Assert.Equal(Huella(gemeloAntes), Huella(await apoyo.LeerAsync(s.Gemelo)));
    }

    // =================================================================================================
    // Los rechazos: ninguno escribe nada
    // =================================================================================================

    /// <summary>La lista de ids se valida antes que la familia: una lista ausente o vacía da 400 aunque la familia no
    /// exista, y más de 101 ids distintos —el tope más uno: la referencia de la familia puede ser uno de ellos y no
    /// cuenta— dan 400 antes de tocar la base. Un cuerpo sin la propiedad es una lista vacía. Con 101 ids el tope exacto
    /// se exige después, bajo el lock: <see cref="LaReferenciaDeLaFamiliaEnLaListaNoCuentaParaElTopeYUnDestinoDeMasSiCuenta"/>.</summary>
    [Fact]
    public async Task UnaListaVaciaOExcedidaDa400AntesDeBuscarLaFamilia()
    {
        var s = await SembrarAsync(apoyo, nameof(UnaListaVaciaOExcedidaDa400AntesDeBuscarLaFamilia), enFamilia: true);
        using var e = s.E;
        var familiaId = s.Familia!.Value;
        var antes = await FotoAsync(s);
        var cien = Enumerable.Range(900_000_001, 100).ToArray();

        var casos = new (string Descripcion, HttpResponseMessage Respuesta, string Codigo)[]
        {
            ("lista vacía en una familia que existe", await PostAgregarAsync(e.Admin, familiaId, []), "articulos_requeridos"),
            ("lista vacía en una familia que no existe", await PostAgregarAsync(e.Admin, 999_999_999, []), "articulos_requeridos"),
            ("lista ausente", await PostAgregarAsync(e.Admin, familiaId, null), "articulos_requeridos"),
            (
                "cuerpo sin la propiedad",
                await e.Admin.PostAsJsonAsync($"/api/familias/{familiaId}/articulos", new { }, OpcionesJson),
                "articulos_requeridos"
            ),
            ("101 ids más uno", await PostAgregarAsync(e.Admin, familiaId, [.. cien, 900_000_101, 900_000_102]), "demasiados_articulos"),
            (
                "101 ids más uno en una familia que no existe",
                await PostAgregarAsync(e.Admin, 999_999_999, [.. cien, 900_000_101, 900_000_102]),
                "demasiados_articulos"
            )
        };

        foreach (var (descripcion, respuesta, codigo) in casos)
        {
            Assert.True(
                respuesta.StatusCode == HttpStatusCode.BadRequest,
                $"{descripcion}: esperaba 400 y recibió {(int)respuesta.StatusCode}.");
            Assert.Equal(codigo, (await ProblemaAsync(respuesta)).Codigo);
        }

        Assert.Equal(antes, await FotoAsync(s));
    }

    /// <summary>La familia que no existe, está dada de baja o es de otro tenant da 404; la inactiva, 409
    /// <c>familia_inactiva</c>, y la que no tiene ningún miembro vivo, 409 <c>familia_sin_articulos</c>: sin una
    /// referencia no hay a qué alinear. La familia se resuelve ANTES que los artículos: con un id que no existe en la
    /// lista la inactiva y la vacía dan igual su 409.</summary>
    [Fact]
    public async Task LaFamiliaQueNoAdmiteArticulosDaSuRechazoAntesQueLosArticulos()
    {
        var s = await SembrarAsync(apoyo, nameof(LaFamiliaQueNoAdmiteArticulosDaSuRechazoAntesQueLosArticulos), enFamilia: true);
        using var e = s.E;
        using var otro = await apoyo.PrepararAsync(nameof(LaFamiliaQueNoAdmiteArticulosDaSuRechazoAntesQueLosArticulos) + "-ajeno");
        var familiaAjena = await apoyo.SembrarFamiliaAsync(otro, "Ajena");
        var dadaDeBaja = await apoyo.SembrarFamiliaAsync(e, "Dada de baja", dadaDeBaja: true);
        var inactiva = await apoyo.SembrarFamiliaAsync(e, "Inactiva", activa: false);
        await apoyo.SembrarArticuloAsync(e, "de-la-inactiva", ValoresBase(e), inactiva);
        var vacia = await apoyo.SembrarFamiliaAsync(e, "Vacía");
        await apoyo.SembrarArticuloAsync(e, "de-la-vacia", ValoresBase(e), vacia, dadoDeBaja: true);
        var antes = await FotoAsync(s);
        const int inexistente = 999_999_999;

        var casos = new (string Descripcion, int Familia, int[] Ids, HttpStatusCode Estado, string Codigo, string Mensaje)[]
        {
            ("familia inexistente", inexistente, [s.D1], HttpStatusCode.NotFound, "no_encontrado", $"No existe la familia {inexistente}."),
            ("familia dada de baja", dadaDeBaja, [s.D1], HttpStatusCode.NotFound, "no_encontrado", $"No existe la familia {dadaDeBaja}."),
            ("familia de otro tenant", familiaAjena, [s.D1], HttpStatusCode.NotFound, "no_encontrado", $"No existe la familia {familiaAjena}."),
            (
                "familia inactiva", inactiva, [s.D1], HttpStatusCode.Conflict, "familia_inactiva",
                "La familia \"Inactiva\" está inactiva: no se le pueden agregar artículos."
            ),
            (
                "familia inactiva con un artículo que no existe", inactiva, [s.D1, inexistente], HttpStatusCode.Conflict, "familia_inactiva",
                "La familia \"Inactiva\" está inactiva: no se le pueden agregar artículos."
            ),
            (
                "familia sin miembros vivos", vacia, [s.D1], HttpStatusCode.Conflict, "familia_sin_articulos",
                "La familia \"Vacía\" no tiene artículos vivos: no hay un artículo de referencia al que alinear."
            ),
            (
                "familia sin miembros vivos con un artículo que no existe", vacia, [s.D1, inexistente], HttpStatusCode.Conflict,
                "familia_sin_articulos", "La familia \"Vacía\" no tiene artículos vivos: no hay un artículo de referencia al que alinear."
            )
        };

        foreach (var (descripcion, familia, ids, estado, codigo, mensaje) in casos)
        {
            var respuesta = await PostAgregarAsync(e.Admin, familia, ids);

            Assert.True(
                respuesta.StatusCode == estado,
                $"{descripcion}: esperaba {(int)estado} y recibió {(int)respuesta.StatusCode}.");
            Assert.Equal((codigo, mensaje), await ProblemaAsync(respuesta));
            Assert.True(antes == await FotoAsync(s), $"{descripcion}: escribió algo en la base.");
        }
    }

    /// <summary>Cada rechazo de los artículos, con su estado, su código y su mensaje, y la base idéntica después de cada
    /// uno: el que no existe, está dado de baja o es de otro tenant (400, el menor id que falta), el que ya es de OTRA
    /// familia (409, y no se mueve: sigue en la suya) y el precio que no se puede alinear (422), aunque sea el último
    /// destino en procesarse: lo que se alineó antes no queda escrito. Con todos juntos gana el primero; al sacar uno por
    /// vez da el siguiente, que es el orden de la previsualización.</summary>
    [Fact]
    public async Task CadaRechazoDeArticulosTieneSuCodigoYSuMensajeYNoMueveNiEscribeNada()
    {
        var s = await SembrarAsync(apoyo, nameof(CadaRechazoDeArticulosTieneSuCodigoYSuMensajeYNoMueveNiEscribeNada), enFamilia: true);
        using var e = s.E;
        var familiaId = s.Familia!.Value;
        using var otro = await apoyo.PrepararAsync(nameof(CadaRechazoDeArticulosTieneSuCodigoYSuMensajeYNoMueveNiEscribeNada) + "-ajeno");
        var ajeno = await apoyo.SembrarArticuloAsync(otro, "ajeno", ValoresBase(otro), id: IdsAscendentes(1)[0]);

        await apoyo.SembrarPrecioVigenteAsync(e, s.D3, s.Minorista, 33m);
        var antes = await FotoAsync(s);
        var codigoDeLaOtra = (await apoyo.LeerAsync(s.DeOtraFamilia)).CodigoInterno;
        var codigoDeD3 = (await apoyo.LeerAsync(s.D3)).CodigoInterno;
        const int inexistente = 999_999_999;
        const int otroInexistente = 888_888_888;

        var casos = new (string Descripcion, HttpStatusCode Estado, string Codigo, string Mensaje, int[] Ids)[]
        {
            ("artículo inexistente", HttpStatusCode.BadRequest, "referencia_invalida", $"No existe el artículo {inexistente}.", [s.D1, inexistente]),
            ("el menor de dos inexistentes", HttpStatusCode.BadRequest, "referencia_invalida", $"No existe el artículo {otroInexistente}.", [inexistente, s.D1, otroInexistente]),
            ("artículo dado de baja", HttpStatusCode.BadRequest, "referencia_invalida", $"No existe el artículo {s.DeBaja}.", [s.D1, s.DeBaja]),
            ("artículo de otro tenant", HttpStatusCode.BadRequest, "referencia_invalida", $"No existe el artículo {ajeno}.", [s.D1, ajeno]),
            (
                "artículo de otra familia", HttpStatusCode.Conflict, "articulo_en_otra_familia",
                $"El artículo {codigoDeLaOtra} ya pertenece a la familia \"Otra familia\": hay que sacarlo de ella antes de agruparlo.",
                [s.D1, s.DeOtraFamilia]
            ),
            (
                "precio que no se puede alinear", HttpStatusCode.UnprocessableEntity, "familia_precio_inalineable",
                $"No se puede alinear el artículo {codigoDeD3} en la lista \"Minorista\": el artículo de referencia no tiene ningún precio " +
                "en esa lista y este sí, y un precio no se puede quitar.",
                [s.D1, s.D2, s.D3]
            )
        };

        foreach (var (descripcion, estado, codigo, mensaje, ids) in casos)
        {
            var respuesta = await PostAgregarAsync(e.Admin, familiaId, ids);

            Assert.True(
                respuesta.StatusCode == estado,
                $"{descripcion}: esperaba {(int)estado} y recibió {(int)respuesta.StatusCode}.");
            Assert.Equal((codigo, mensaje), await ProblemaAsync(respuesta));
            Assert.True(antes == await FotoAsync(s), $"{descripcion}: escribió algo en la base.");
        }

        Assert.Equal(s.FamiliaOtra, (await apoyo.LeerAsync(s.DeOtraFamilia)).IdFamilia);

        var todos = new[] { s.D1, s.D3, s.DeOtraFamilia, inexistente };
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAgregarAsync(e.Admin, familiaId, todos)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAgregarAsync(e.Admin, familiaId, [.. todos.Take(3)])).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostAgregarAsync(e.Admin, familiaId, [.. todos.Take(2)])).StatusCode);
        Assert.Equal(antes, await FotoAsync(s));

        var previa = await LeerPrevisualizacionAsync(await PostPrevisualizarAsync(e.Admin, s.Referencia, s.D1, s.D2, s.D3));
        Assert.Equal(casos[^1].Mensaje, Assert.Single(previa.Problemas).Mensaje);
    }

    /// <summary>El tope de cien es sobre los destinos, y la referencia de la familia no es un destino aunque figure en la
    /// lista: 101 ids que la incluyen son cien destinos, pasan el tope y recién los rechaza que no existen (400
    /// <c>referencia_invalida</c>, el menor id que falta); 101 ids que no la incluyen son 101 destinos, y el tope exacto,
    /// que se exige bajo el lock cuando se conoce la referencia, los rechaza con 400 <c>demasiados_articulos</c>. Ningún
    /// pedido escribe nada.</summary>
    [Fact]
    public async Task LaReferenciaDeLaFamiliaEnLaListaNoCuentaParaElTopeYUnDestinoDeMasSiCuenta()
    {
        var s = await SembrarAsync(
            apoyo, nameof(LaReferenciaDeLaFamiliaEnLaListaNoCuentaParaElTopeYUnDestinoDeMasSiCuenta), enFamilia: true);
        using var e = s.E;
        var familiaId = s.Familia!.Value;
        var antes = await FotoAsync(s);
        var cien = Enumerable.Range(900_000_001, 100).ToArray();

        var conLaReferencia = await PostAgregarAsync(e.Admin, familiaId, [s.Referencia, .. cien]);

        Assert.Equal(HttpStatusCode.BadRequest, conLaReferencia.StatusCode);
        Assert.Equal(("referencia_invalida", $"No existe el artículo {cien[0]}."), await ProblemaAsync(conLaReferencia));

        var sinLaReferencia = await PostAgregarAsync(e.Admin, familiaId, [.. cien, 900_000_101]);

        Assert.Equal(HttpStatusCode.BadRequest, sinLaReferencia.StatusCode);
        Assert.Equal(
            ("demasiados_articulos", "Un pedido agrupa como máximo 100 artículos además del de referencia."),
            await ProblemaAsync(sinLaReferencia));
        Assert.Equal(antes, await FotoAsync(s));
    }

    // =================================================================================================
    // Una referencia sin precio vigente
    // =================================================================================================

    /// <summary>Con una referencia que solo tiene un precio PROGRAMADO, agregar escribe su programado en el destino que no
    /// tiene precios y en el que tiene uno programado propio (que queda cerrado en su propio inicio) y no toca al ya
    /// idéntico, con la auditoría y un solo "ahora".</summary>
    [Fact]
    public async Task AgregarConUnaReferenciaSoloProgramadaEscribeSuPendienteEnLosDestinosQueSeAlinean()
    {
        var s = await SembrarProgramadosAsync(
            apoyo, nameof(AgregarConUnaReferenciaSoloProgramadaEscribeSuPendienteEnLosDestinosQueSeAlinean), enFamilia: true);
        using var e = s.E;

        await AfirmarLaEscrituraDeUnaReferenciaSoloProgramadaAsync(
            apoyo, s, ids => PostAgregarAsync(e.Admin, s.Familia!.Value, ids), HttpStatusCode.OK);
    }

    [Fact]
    public async Task AgregarConUnaReferenciaSoloProgramadaRechazaLosParesQueNoSePuedenAlinearYNoEscribeNada()
    {
        var s = await SembrarProgramadosAsync(
            apoyo, nameof(AgregarConUnaReferenciaSoloProgramadaRechazaLosParesQueNoSePuedenAlinearYNoEscribeNada), enFamilia: true);
        using var e = s.E;

        await AfirmarLosRechazosDeLaAlineacionDeUnaReferenciaSoloProgramadaAsync(
            fixture, apoyo, s, ids => PostAgregarAsync(e.Admin, s.Familia!.Value, ids));
    }

    // =================================================================================================
    // Los catálogos de la referencia
    // =================================================================================================

    /// <summary>Un catálogo de la referencia de la familia dado de baja se rechaza con 400 <c>referencia_invalida</c> y el
    /// mismo mensaje que da la edición de un artículo con ese id, y no escribe nada: la referencia es la que copian los
    /// demás. Una fila por catálogo, y en cada una ese catálogo y todos los que lo siguen en el orden de los chequeos
    /// están dados de baja.</summary>
    [Theory]
    [InlineData("alicuota")]
    [InlineData("area")]
    [InlineData("categoria")]
    [InlineData("grupo")]
    [InlineData("proveedor")]
    public async Task UnCatalogoDeLaReferenciaDeLaFamiliaDadoDeBajaDa400ConElMensajeDeLaEdicionYNoEscribeNada(string primerMuerto)
    {
        var s = await SembrarCatalogosAsync(
            apoyo, nameof(UnCatalogoDeLaReferenciaDeLaFamiliaDadoDeBajaDa400ConElMensajeDeLaEdicionYNoEscribeNada), enFamilia: true);
        using var e = s.E;
        await DarDeBajaLosCatalogosAsync(apoyo, s, Catalogos.SkipWhile(c => c != primerMuerto));
        var idMuerto = s.IdDeLaReferencia(primerMuerto);
        var listas = new[] { e.IdListaGeneral, e.IdListaMayorista };
        var antes = await FotoDeLaBaseAsync(fixture, apoyo, e, s.TodosLosArticulos, listas);

        var respuesta = await PostAgregarAsync(e.Admin, s.Familia!.Value, s.Destino);

        var mensaje = MensajeDeCatalogoInexistente(primerMuerto, idMuerto);
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(("referencia_invalida", mensaje), await ProblemaAsync(respuesta));
        Assert.Equal(
            (HttpStatusCode.BadRequest, "referencia_invalida", mensaje),
            await LoQueDiceLaEdicionAsync(apoyo, e, s.Destino, primerMuerto, idMuerto));
        Assert.Equal(antes, await FotoDeLaBaseAsync(fixture, apoyo, e, s.TodosLosArticulos, listas));
    }

    // =================================================================================================
    // El tope de pares
    // =================================================================================================

    /// <summary>Con cincuenta listas fijas, veinte destinos son mil pares y se aceptan, y veintiuno son 1050 y dan 400
    /// <c>demasiados_articulos</c> con el mensaje exacto, sin escribir nada: lo que se rechaza son los pares, y cada destino
    /// cabe en el tope de artículos.</summary>
    [Fact]
    public async Task AgregarAdmiteMilParesYRechazaMilUnoSinEscribirNada()
    {
        var s = await SembrarParesAsync(apoyo, nameof(AgregarAdmiteMilParesYRechazaMilUnoSinEscribirNada), enFamilia: true);
        using var e = s.E;
        var antes = await FotoDeLaBaseAsync(fixture, apoyo, e, s.TodosLosArticulos, s.ListasFijas);

        var respuesta = await PostAgregarAsync(e.Admin, s.Familia!.Value, [.. s.Destinos]);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(("demasiados_articulos", s.MensajeDeLosParesExcedidos), await ProblemaAsync(respuesta));
        Assert.Equal(antes, await FotoDeLaBaseAsync(fixture, apoyo, e, s.TodosLosArticulos, s.ListasFijas));

        var aceptado = await LeerResultadoAsync(
            await PostAgregarAsync(e.Admin, s.Familia.Value, [.. s.Destinos.Take(EscenarioDeLosPares.DestinosEnElTope)]),
            HttpStatusCode.OK);

        Assert.Equal(EscenarioDeLosPares.DestinosEnElTope, aceptado.Articulos.Count);
    }
}
