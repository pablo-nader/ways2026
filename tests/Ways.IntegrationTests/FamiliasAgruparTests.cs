using System.Net;
using System.Net.Http.Json;
using Ways.Application.Familias;
using Ways.Domain.Articulos;
using Ways.Domain.Precios;
using static Ways.IntegrationTests.ApoyoDeAgrupacion;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>POST /api/familias</c> (doc 10 §3, "Familias de artículos") contra Postgres real: crea la familia y la agrupa —el
/// artículo de referencia y los pedidos quedan como sus miembros, y los pedidos, ALINEADOS con la referencia en los trece
/// campos compartidos y en el estado de precios de cada lista fija—. Es todo o nada. Los locks, la atomicidad y la
/// reconciliación de lotes están en <c>FamiliasAgrupacionConcurrenciaTests</c>; la previsualización, en
/// <c>FamiliasPrevisualizacionTests</c>.
///
/// <para>El escenario es el de <see cref="ApoyoDeAgrupacion"/>: la referencia no es el artículo de menor id, los destinos
/// cubren cada resultado posible de la alineación y hay artículos que el pedido no toca, para que un escritor que alcance
/// de más se vea.</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FamiliasAgruparTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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

    // =================================================================================================
    // El camino feliz
    // =================================================================================================

    /// <summary>El pedido completo, con los ids desordenados, repetidos y con la referencia también en la lista: la
    /// familia se crea activa y con el nombre sin espacios en los extremos; la referencia y los tres destinos quedan
    /// miembros; cada destino queda con los trece valores de la referencia y con sus campos propios intactos; los precios
    /// de cada lista fija quedan como los de la referencia, con la fecha del pendiente; y hay una auditoría de precio por
    /// cada fila escrita, con el estado anterior que le corresponde. Todo con UN solo "ahora": la familia, los artículos
    /// escritos, las filas cerradas y las nuevas comparten el instante. El destino que ya era idéntico entra a la familia
    /// pero no recibe ninguna fila de precio. Ningún otro artículo, precio ni familia cambia, y el resultado es el de la
    /// previsualización del mismo pedido.</summary>
    [Fact]
    public async Task CrearUnaFamiliaAlineaCamposPreciosYAuditoriaDeCadaDestinoConUnSoloAhora()
    {
        var s = await SembrarAsync(apoyo, nameof(CrearUnaFamiliaAlineaCamposPreciosYAuditoriaDeCadaDestinoConUnSoloAhora));
        using var e = s.E;
        var general = e.IdListaGeneral;
        var mayorista = e.IdListaMayorista;
        var valoresDeLaReferencia = s.ValoresDeLaReferencia;

        var referenciaAntes = await apoyo.LeerAsync(s.Referencia);
        var d1Antes = await apoyo.LeerAsync(s.D1);
        var d2Antes = await apoyo.LeerAsync(s.D2);
        var d3Antes = await apoyo.LeerAsync(s.D3);
        var d1GeneralAntes = await apoyo.FilasDePrecioAsync(s.D1, general);
        var d2GeneralAntes = await apoyo.FilasDePrecioAsync(s.D2, general);
        var d2MayoristaAntes = await apoyo.FilasDePrecioAsync(s.D2, mayorista);
        var d3MayoristaAntes = await apoyo.FilasDePrecioAsync(s.D3, mayorista);
        var referenciaGeneralAntes = await apoyo.FilasDePrecioAsync(s.Referencia, general);
        var sinTocarAntes = (await LeerAsync(s.Suelto, s.DeBaja, s.DeOtraFamilia, s.Gemelo)).Select(Huella).ToList();
        var otraFamiliaAntes = await apoyo.LeerFamiliaAsync(s.FamiliaOtra);
        var sueltoGeneralAntes = Filas(await apoyo.FilasDePrecioAsync(s.Suelto, general));

        var previa = await LeerPrevisualizacionAsync(await PostPrevisualizarAsync(e.Admin, s.Referencia, s.D3, s.D1, s.D2));
        Assert.Empty(previa.Problemas);

        var respuesta = await PostCrearAsync(e.Admin, "  Gaseosas  ", s.Referencia, s.D3, s.D1, s.D1, s.D2, s.Referencia);

        var resultado = await LeerResultadoAsync(respuesta, HttpStatusCode.Created);
        Assert.Equal($"/api/familias/{resultado.IdFamilia}", respuesta.Headers.Location!.OriginalString);
        Assert.Equal("Gaseosas", resultado.Nombre);
        Assert.Equal(s.Referencia, resultado.IdArticuloReferencia);
        Assert.Equal([s.D1, s.D2, s.D3], resultado.Articulos.Select(a => a.IdArticulo));
        Assert.Equal(Json(previa.Articulos), Json(resultado.Articulos));

        var estadoDeLaReferencia = new EstadoDePrecios(100m, new PrecioPendiente(130m, s.V));
        var d1 = resultado.Articulos[0];
        Assert.Equal(CamposDeD1, d1.Campos);
        Assert.Equal(ValoresCompartidosDeFamilia.De(d1Antes), d1.Actual);
        Assert.Equal(valoresDeLaReferencia, d1.Nuevo);
        Assert.Equal(
            [
                new CambioDePreciosDeLista(general, new EstadoDePrecios(90m, null), estadoDeLaReferencia),
                new CambioDePreciosDeLista(mayorista, EstadoDePrecios.Vacio, new EstadoDePrecios(80m, null))
            ],
            d1.Precios);

        var d2 = resultado.Articulos[1];
        Assert.Empty(d2.Campos);
        Assert.Equal(valoresDeLaReferencia, d2.Actual);
        Assert.Empty(d2.Precios);

        var d3 = resultado.Articulos[2];
        Assert.Equal(CamposDeD3, d3.Campos);
        Assert.Equal(ValoresCompartidosDeFamilia.De(d3Antes), d3.Actual);
        Assert.Equal(
            [
                new CambioDePreciosDeLista(general, EstadoDePrecios.Vacio, estadoDeLaReferencia),
                new CambioDePreciosDeLista(mayorista, new EstadoDePrecios(70m, new PrecioPendiente(85m, s.W)), new EstadoDePrecios(80m, null))
            ],
            d3.Precios);

        // La familia: viva, activa, con un solo instante de creación y de modificación.
        var familia = await apoyo.LeerFamiliaAsync(resultado.IdFamilia);
        var ahora = familia.CreatedAt;
        Assert.Equal(("Gaseosas", true, (DateTimeOffset?)null), (familia.Nombre, familia.Activo, familia.DeletedAt));
        Assert.Equal(ahora, familia.UpdatedAt);
        Assert.True(ahora > referenciaAntes.UpdatedAt);

        // La referencia entra a su familia y no cambia en nada más.
        var referencia = await apoyo.LeerAsync(s.Referencia);
        Assert.Equal((resultado.IdFamilia, ahora), (referencia.IdFamilia, referencia.UpdatedAt));
        Assert.Equal(Propios(referenciaAntes), Propios(referencia));
        Assert.Equal(valoresDeLaReferencia, ValoresCompartidosDeFamilia.De(referencia));

        // Cada destino: miembro, con los trece valores de la referencia y lo propio intacto.
        foreach (var (antes, id) in new[] { (d1Antes, s.D1), (d2Antes, s.D2), (d3Antes, s.D3) })
        {
            var destino = await apoyo.LeerAsync(id);

            Assert.Equal((resultado.IdFamilia, ahora), (destino.IdFamilia, destino.UpdatedAt));
            Assert.Equal(valoresDeLaReferencia, ValoresCompartidosDeFamilia.De(destino));
            Assert.Equal(Propios(antes), Propios(destino));
        }

        // Precios de d1: lo abierto se cierra en "ahora" y el estado de la referencia empieza ahí.
        var d1General = await apoyo.FilasDePrecioAsync(s.D1, general);
        Assert.Equal(
            [
                (90m, d1GeneralAntes[0].VigenteDesde, (DateTimeOffset?)ahora),
                (100m, ahora, (DateTimeOffset?)s.V),
                (130m, s.V, (DateTimeOffset?)null)
            ],
            Filas(d1General));
        Assert.Equal([(80m, ahora, (DateTimeOffset?)null)], Filas(await apoyo.FilasDePrecioAsync(s.D1, mayorista)));

        // Precios de d3: sin nada en la general, y en la mayorista el pendiente propio se reemplaza (queda una fila
        // muerta, que cierra donde empezaba) y su vigente anterior se re-cierra en "ahora".
        Assert.Equal(
            [(100m, ahora, (DateTimeOffset?)s.V), (130m, s.V, (DateTimeOffset?)null)],
            Filas(await apoyo.FilasDePrecioAsync(s.D3, general)));
        var d3Mayorista = await apoyo.FilasDePrecioAsync(s.D3, mayorista);
        Assert.Equal(
            [
                (70m, d3MayoristaAntes[0].VigenteDesde, (DateTimeOffset?)ahora),
                (80m, ahora, (DateTimeOffset?)null),
                (85m, s.W, (DateTimeOffset?)s.W)
            ],
            Filas(d3Mayorista));

        // Un solo "ahora" en todas las filas que se escribieron.
        AfirmarUnSoloAhora(d1GeneralAntes, d1General, ahora);
        AfirmarUnSoloAhora([], await apoyo.FilasDePrecioAsync(s.D1, mayorista), ahora);
        AfirmarUnSoloAhora([], await apoyo.FilasDePrecioAsync(s.D3, general), ahora);
        AfirmarUnSoloAhora(d3MayoristaAntes, d3Mayorista, ahora);

        // El destino ya idéntico y la referencia no reciben ninguna fila de precio ni se les cierra ninguna.
        Assert.Equal(d2GeneralAntes.Select(p => (p.Id, p.Monto, p.VigenteDesde, p.VigenteHasta, p.UpdatedAt)),
            (await apoyo.FilasDePrecioAsync(s.D2, general)).Select(p => (p.Id, p.Monto, p.VigenteDesde, p.VigenteHasta, p.UpdatedAt)));
        Assert.Equal(d2MayoristaAntes.Select(p => (p.Id, p.Monto, p.VigenteDesde, p.VigenteHasta, p.UpdatedAt)),
            (await apoyo.FilasDePrecioAsync(s.D2, mayorista)).Select(p => (p.Id, p.Monto, p.VigenteDesde, p.VigenteHasta, p.UpdatedAt)));
        Assert.Equal(referenciaGeneralAntes.Select(p => (p.Id, p.Monto, p.VigenteHasta, p.UpdatedAt)),
            (await apoyo.FilasDePrecioAsync(s.Referencia, general)).Select(p => (p.Id, p.Monto, p.VigenteHasta, p.UpdatedAt)));

        // La lista fija sin precios y la derivada no reciben nada.
        foreach (var id in new[] { s.D1, s.D2, s.D3, s.Referencia })
        {
            Assert.Empty(await apoyo.FilasDePrecioAsync(id, s.Minorista));
            Assert.Empty(await apoyo.FilasDePrecioAsync(id, s.Derivada));
        }

        // La auditoría: una fila por cada fila de precio escrita, con el estado que tenía el par justo antes.
        var auditoria = await apoyo.AuditoriaDePreciosAsync(e.IdTenant);
        Assert.All(auditoria, a => Assert.Equal("articulo", a.Entidad));
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

        // Nada más cambia: ni los artículos que el pedido no nombra, ni los precios del suelto, ni la otra familia.
        Assert.Equal(sinTocarAntes, (await LeerAsync(s.Suelto, s.DeBaja, s.DeOtraFamilia, s.Gemelo)).Select(Huella).ToList());
        Assert.Equal(sueltoGeneralAntes, Filas(await apoyo.FilasDePrecioAsync(s.Suelto, general)));
        var otraFamilia = await apoyo.LeerFamiliaAsync(s.FamiliaOtra);
        Assert.Equal(
            (otraFamiliaAntes.Nombre, otraFamiliaAntes.Activo, otraFamiliaAntes.UpdatedAt),
            (otraFamilia.Nombre, otraFamilia.Activo, otraFamilia.UpdatedAt));

        // El listado ve la familia con sus cuatro miembros vivos.
        var listado = await e.Admin.GetFromJsonAsync<List<FamiliaListado>>("/api/familias", OpcionesJson);
        Assert.Equal(4, listado!.Single(f => f.Id == resultado.IdFamilia).CantidadArticulos);
    }

    /// <summary>Sin artículos que agrupar la familia nace con un único miembro, la referencia, que entra a ella y no
    /// cambia en nada más; no se escribe ninguna fila de precio ni de auditoría. La lista ausente y la vacía son lo
    /// mismo.</summary>
    [Fact]
    public async Task CrearUnaFamiliaSinOtrosArticulosLaDejaConLaReferenciaComoUnicoMiembro()
    {
        var s = await SembrarAsync(apoyo, nameof(CrearUnaFamiliaSinOtrosArticulosLaDejaConLaReferenciaComoUnicoMiembro));
        using var e = s.E;
        var antes = await apoyo.LeerAsync(s.Referencia);
        var preciosAntes = Filas(await apoyo.FilasDePrecioAsync(s.Referencia, e.IdListaGeneral));

        var sinLista = await LeerResultadoAsync(
            await e.Admin.PostAsJsonAsync("/api/familias", new { nombre = "Sola", idArticuloReferencia = s.Referencia }, OpcionesJson),
            HttpStatusCode.Created);
        var conListaVacia = await PostCrearAsync(e.Admin, "Sola también", s.Gemelo, []);

        Assert.Empty(sinLista.Articulos);
        Assert.Equal(s.Referencia, sinLista.IdArticuloReferencia);

        var referencia = await apoyo.LeerAsync(s.Referencia);
        Assert.Equal(sinLista.IdFamilia, referencia.IdFamilia);
        Assert.Equal((await apoyo.LeerFamiliaAsync(sinLista.IdFamilia)).CreatedAt, referencia.UpdatedAt);
        Assert.Equal(Propios(antes), Propios(referencia));
        Assert.Equal(ValoresCompartidosDeFamilia.De(antes), ValoresCompartidosDeFamilia.De(referencia));
        Assert.Equal(preciosAntes, Filas(await apoyo.FilasDePrecioAsync(s.Referencia, e.IdListaGeneral)));
        Assert.Empty(await apoyo.AuditoriaDePreciosAsync(e.IdTenant));

        Assert.Equal(HttpStatusCode.Created, conListaVacia.StatusCode);
        Assert.Empty((await LeerResultadoAsync(conListaVacia, HttpStatusCode.Created)).Articulos);
    }

    // =================================================================================================
    // El cuerpo y los rechazos: ninguno escribe nada
    // =================================================================================================

    /// <summary>Cada rechazo de forma da 400 con su código y no escribe nada. El primero que se evalúa es el nombre, después
    /// la referencia y por último el tope de artículos: un pedido que rompe los tres recibe el del nombre. Los repetidos y
    /// la propia referencia no cuentan para el tope, que se aplica antes de tocar la base: ninguno de los ids existe.</summary>
    [Fact]
    public async Task UnPedidoMalFormadoDa400YNoEscribeNada()
    {
        var s = await SembrarAsync(apoyo, nameof(UnPedidoMalFormadoDa400YNoEscribeNada));
        using var e = s.E;
        var antes = await FotoAsync(s);

        var cien = Enumerable.Range(900_000_001, 100).ToArray();
        var casos = new (string Descripcion, string? Nombre, int Referencia, int[]? Ids, string Codigo)[]
        {
            ("nombre ausente", null, s.Referencia, [s.D1], "nombre_requerido"),
            ("nombre en blanco", "   ", s.Referencia, [s.D1], "nombre_requerido"),
            ("nombre de 151 caracteres", new string('a', 151), s.Referencia, [s.D1], "nombre_muy_largo"),
            ("referencia cero", "Una", 0, [s.D1], "id_articulo_referencia_requerido"),
            ("referencia negativa", "Una", -1, [s.D1], "id_articulo_referencia_requerido"),
            ("cien destinos más uno", "Una", s.Referencia, [.. cien, 900_000_101], "demasiados_articulos"),
            ("todo mal a la vez: gana el nombre", null, 0, [.. cien, 900_000_101], "nombre_requerido"),
            ("referencia mal y tope roto: gana la referencia", "Una", 0, [.. cien, 900_000_101], "id_articulo_referencia_requerido")
        };

        foreach (var (descripcion, nombre, referencia, ids, codigo) in casos)
        {
            var respuesta = await PostCrearAsync(e.Admin, nombre, referencia, ids);

            Assert.True(
                respuesta.StatusCode == HttpStatusCode.BadRequest,
                $"{descripcion}: esperaba 400 y recibió {(int)respuesta.StatusCode}.");
            Assert.Equal(codigo, (await ProblemaAsync(respuesta)).Codigo);
        }

        Assert.Equal(antes, await FotoAsync(s));

        // Cien destinos exactos, con repetidos y la referencia, no pasan el tope por sí solos: llegan a la base, que no
        // los conoce.
        var admitidos = await PostCrearAsync(e.Admin, "Una", s.Referencia, [.. cien, .. cien, s.Referencia]);
        Assert.Equal(HttpStatusCode.BadRequest, admitidos.StatusCode);
        Assert.Equal("referencia_invalida", (await ProblemaAsync(admitidos)).Codigo);
        Assert.Equal(antes, await FotoAsync(s));
    }

    /// <summary>Cada rechazo de contenido, con su estado, su código y su mensaje, y la base idéntica después de cada uno:
    /// el artículo que no existe, está dado de baja o es de otro tenant (400, el menor id que falta), el que ya es de una
    /// familia —también la referencia— (409, sin moverlo) y el precio que no se puede alinear (422), aunque sea el último
    /// destino en procesarse: nada de lo anterior queda escrito. Después, la precedencia: con todos los problemas juntos
    /// el pedido real da el primero, y al sacar uno por vez da el siguiente, que es el orden en que los informa la
    /// previsualización.</summary>
    [Fact]
    public async Task CadaRechazoDeContenidoTieneSuCodigoYSuMensajeYNoEscribeNada()
    {
        var s = await SembrarAsync(apoyo, nameof(CadaRechazoDeContenidoTieneSuCodigoYSuMensajeYNoEscribeNada));
        using var e = s.E;
        using var otro = await apoyo.PrepararAsync(nameof(CadaRechazoDeContenidoTieneSuCodigoYSuMensajeYNoEscribeNada) + "-ajeno");
        var ajeno = await apoyo.SembrarArticuloAsync(otro, "ajeno", ValoresBase(otro), id: IdsAscendentes(1)[0]);

        // d3, el último destino en procesarse, tiene un precio en una lista donde la referencia no tiene ninguno.
        await apoyo.SembrarPrecioVigenteAsync(e, s.D3, s.Minorista, 33m);
        var antes = await FotoAsync(s);

        var codigoDeD3 = (await apoyo.LeerAsync(s.D3)).CodigoInterno;
        var codigoDeLaOtra = (await apoyo.LeerAsync(s.DeOtraFamilia)).CodigoInterno;
        const int inexistente = 999_999_999;
        const int otroInexistente = 888_888_888;

        var casos = new (string Descripcion, HttpStatusCode Estado, string Codigo, string Mensaje, int Referencia, int[] Ids)[]
        {
            ("destino inexistente", HttpStatusCode.BadRequest, "referencia_invalida", $"No existe el artículo {inexistente}.", s.Referencia, [s.D1, inexistente]),
            ("el menor de dos inexistentes", HttpStatusCode.BadRequest, "referencia_invalida", $"No existe el artículo {otroInexistente}.", inexistente, [otroInexistente, s.D1]),
            ("referencia inexistente", HttpStatusCode.BadRequest, "referencia_invalida", $"No existe el artículo {inexistente}.", inexistente, [s.D1]),
            ("destino dado de baja", HttpStatusCode.BadRequest, "referencia_invalida", $"No existe el artículo {s.DeBaja}.", s.Referencia, [s.D1, s.DeBaja]),
            ("referencia dada de baja", HttpStatusCode.BadRequest, "referencia_invalida", $"No existe el artículo {s.DeBaja}.", s.DeBaja, [s.D1]),
            ("destino de otro tenant", HttpStatusCode.BadRequest, "referencia_invalida", $"No existe el artículo {ajeno}.", s.Referencia, [s.D1, ajeno]),
            ("referencia de otro tenant", HttpStatusCode.BadRequest, "referencia_invalida", $"No existe el artículo {ajeno}.", ajeno, [s.D1]),
            (
                "destino en otra familia", HttpStatusCode.Conflict, "articulo_en_otra_familia",
                $"El artículo {codigoDeLaOtra} ya pertenece a la familia \"Otra familia\": hay que sacarlo de ella antes de agruparlo.",
                s.Referencia, [s.D1, s.DeOtraFamilia]
            ),
            (
                "referencia en otra familia", HttpStatusCode.Conflict, "articulo_en_otra_familia",
                $"El artículo {codigoDeLaOtra} ya pertenece a la familia \"Otra familia\": hay que sacarlo de ella antes de agruparlo.",
                s.DeOtraFamilia, [s.D1]
            ),
            (
                "precio que no se puede alinear", HttpStatusCode.UnprocessableEntity, "familia_precio_inalineable",
                $"No se puede alinear el artículo {codigoDeD3} en la lista \"Minorista\": el artículo de referencia no tiene ningún precio " +
                "en esa lista y este sí, y un precio no se puede quitar.",
                s.Referencia, [s.D1, s.D2, s.D3]
            )
        };

        foreach (var (descripcion, estado, codigo, mensaje, referencia, ids) in casos)
        {
            var respuesta = await PostCrearAsync(e.Admin, "Nueva", referencia, ids);

            Assert.True(
                respuesta.StatusCode == estado,
                $"{descripcion}: esperaba {(int)estado} y recibió {(int)respuesta.StatusCode}.");
            Assert.Equal((codigo, mensaje), await ProblemaAsync(respuesta));
            Assert.True(antes == await FotoAsync(s), $"{descripcion}: escribió algo en la base.");
        }

        // La precedencia, de a un problema por vez: inexistente, después otra familia, después el precio.
        var todos = new[] { s.D1, s.D3, s.DeOtraFamilia, inexistente };
        Assert.Equal(HttpStatusCode.BadRequest, (await PostCrearAsync(e.Admin, "Nueva", s.Referencia, todos)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostCrearAsync(e.Admin, "Nueva", s.Referencia, [.. todos.Take(3)])).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PostCrearAsync(e.Admin, "Nueva", s.Referencia, [.. todos.Take(2)])).StatusCode);
        Assert.Equal(antes, await FotoAsync(s));

        // El mensaje del pedido real es el de la previsualización del mismo pedido.
        var previa = await LeerPrevisualizacionAsync(await PostPrevisualizarAsync(e.Admin, s.Referencia, s.D1, s.D2, s.D3));
        var problema = Assert.Single(previa.Problemas);
        Assert.Equal(casos[^1].Mensaje, problema.Mensaje);
    }

    // =================================================================================================
    // El nombre: el chequeo previo y el respaldo
    // =================================================================================================

    /// <summary>Un nombre que ya tiene una familia viva —sin distinguir mayúsculas ni espacios en los extremos— da 409
    /// <c>familia_nombre_duplicado</c> con el nombre pedido y no escribe nada. El de una familia dada de baja se puede
    /// reutilizar. Los artículos pedidos no se evalúan antes que el nombre.</summary>
    [Fact]
    public async Task UnNombreRepetidoDa409YElDeUnaFamiliaDadaDeBajaSePuedeReutilizar()
    {
        var s = await SembrarAsync(apoyo, nameof(UnNombreRepetidoDa409YElDeUnaFamiliaDadaDeBajaSePuedeReutilizar));
        using var e = s.E;
        await apoyo.SembrarFamiliaAsync(e, "Existente");
        await apoyo.SembrarFamiliaAsync(e, "Vieja", dadaDeBaja: true);
        var antes = await FotoAsync(s);

        var respuesta = await PostCrearAsync(e.Admin, "  eXISTENTE ", s.Referencia, s.D1, 999_999_999);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal(
            ("familia_nombre_duplicado", "Ya existe una familia llamada \"eXISTENTE\" en este tenant."), await ProblemaAsync(respuesta));
        Assert.Equal(antes, await FotoAsync(s));

        Assert.Equal(HttpStatusCode.Created, (await PostCrearAsync(e.Admin, "Vieja", s.Referencia, s.D1)).StatusCode);
    }

    /// <summary>El chequeo previo corre ANTES de abrir la transacción: con el lock de membresía exclusivo sostenido por
    /// otra conexión, un nombre repetido recibe su 409 sin esperar. Si el chequeo estuviera después del lock, el pedido
    /// quedaría esperando a quien nunca lo libera y la prueba fallaría por tiempo.</summary>
    [Fact]
    public async Task ElChequeoDelNombreNoEsperaAlLockDeMembresia()
    {
        var s = await SembrarAsync(apoyo, nameof(ElChequeoDelNombreNoEsperaAlLockDeMembresia));
        using var e = s.E;
        await apoyo.SembrarFamiliaAsync(e, "Existente");

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;
        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));

        var respuesta = await PostCrearAsync(e.Admin, "Existente", s.Referencia, s.D1).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("familia_nombre_duplicado", (await ProblemaAsync(respuesta)).Codigo);
    }

    /// <summary>La carrera por el nombre llega al respaldo. Dos pedidos al MISMO nombre, cada uno con su referencia,
    /// pasan el chequeo previo —nadie tiene ese nombre todavía— y quedan esperando el lock de membresía que otra conexión
    /// sostiene; con los dos esperando observados se libera, y se serializan: el primero crea la familia y el segundo
    /// recibe, al insertar, el 23505 de <c>ux_familias_nombre</c> —observado en el comando, con su SQLSTATE—, que la API
    /// traduce a 409 con el mensaje del respaldo (el del chequeo previo nombra el nombre pedido, éste no). El perdedor no
    /// escribe nada: su referencia sigue sin familia y con su <c>updated_at</c>.</summary>
    [Fact]
    public async Task DosPedidosAlMismoNombreDejanUnaFamiliaYUnPerdedorConElRespaldoDe23505()
    {
        var s = await SembrarAsync(apoyo, nameof(DosPedidosAlMismoNombreDejanUnaFamiliaYUnPerdedorConElRespaldoDe23505));
        using var e = s.E;
        const string nombre = "Nombre en carrera";
        var referenciaAAntes = await apoyo.LeerAsync(s.D1);
        var referenciaBAntes = await apoyo.LeerAsync(s.D2);

        var registro = new RegistroDeFallosDeComandos();
        var (host, cliente) = await ClienteDeUnHostConAsync(fixture, e, registro);
        await using var _ = host;
        using var __ = cliente;

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;
        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));

        var a = PostCrearAsync(cliente, nombre, s.D1);
        var b = PostCrearAsync(cliente, nombre, s.D2);

        var (alto, bajo) = PartesDeLaClave(LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));
        await EsperarAsync(
            async () =>
            {
                var esperando = await CandadosAdvisoryAsync(
                    poll, "NOT granted AND objsubid = 1 AND classid::bigint = $1 AND objid::bigint = $2", alto, bajo);

                return esperando.Count == 2 ? esperando : null;
            },
            "Los dos pedidos nunca se observaron esperando el lock de membresía.");

        await transaccion.CommitAsync();

        var respuestas = await Task.WhenAll(a, b).WaitAsync(EsperaMaxima);

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], respuestas.Select(r => r.StatusCode).Order());

        var perdedora = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(("familia_nombre_duplicado", "Ya existe una familia con ese nombre."), await ProblemaAsync(perdedora));

        var violacion = Assert.Single(registro.Fallos);
        Assert.Equal(("23505", "ux_familias_nombre"), (violacion.SqlState, violacion.ConstraintName));

        var ganadora = await LeerResultadoAsync(respuestas.Single(r => r.StatusCode == HttpStatusCode.Created), HttpStatusCode.Created);
        var referenciaDeLaPerdedora = ganadora.IdArticuloReferencia == s.D1 ? s.D2 : s.D1;
        var perdedoraAntes = referenciaDeLaPerdedora == s.D1 ? referenciaAAntes : referenciaBAntes;
        var despues = await apoyo.LeerAsync(referenciaDeLaPerdedora);

        Assert.Null(despues.IdFamilia);
        Assert.Equal(perdedoraAntes.UpdatedAt, despues.UpdatedAt);
        Assert.Equal(ganadora.IdFamilia, (await apoyo.LeerAsync(ganadora.IdArticuloReferencia)).IdFamilia);
    }
}
