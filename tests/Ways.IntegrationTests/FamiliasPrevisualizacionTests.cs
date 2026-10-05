using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Familias;
using Ways.Domain.Articulos;
using Ways.Domain.Precios;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>POST /api/familias/previsualizacion</c> (doc 10 §3, "Familias de artículos") contra Postgres real: qué cambiaría
/// si se agruparan unos artículos con uno de referencia. Solo lee: no escribe, no toma locks y no tiene transacción.
///
/// <para>El escenario común tiene una referencia con precios en dos listas fijas y una tercera sin precios, y cinco
/// artículos pedidos que cubren cada resultado posible: uno con dos campos distintos y precios que se alinean en dos
/// listas pero que NO se puede alinear en la tercera (tiene un precio y la referencia no), uno ya idéntico a la
/// referencia, uno con cinco campos distintos y precios que se alinean, uno que ya está en otra familia y uno dado de
/// baja, más un id que no existe. Los ids de los artículos son explícitos y ascendentes en ese orden.</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FamiliasPrevisualizacionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    /// <summary>Ids de artículo explícitos en una zona que el identity no alcanza: la clave primaria es global a todos
    /// los tenants de la base.</summary>
    private static int siguienteIdDeArticulo = 6_300_000;

    private static int[] IdsAscendentes(int cantidad)
    {
        var primero = Interlocked.Add(ref siguienteIdDeArticulo, cantidad + 1) - cantidad;

        return [.. Enumerable.Range(primero, cantidad)];
    }

    /// <summary><c>timestamptz</c> guarda microsegundos y <see cref="DateTimeOffset"/> tiene 100 ns de resolución.</summary>
    private static DateTimeOffset AMicrosegundos(DateTimeOffset instante) =>
        new(instante.Ticks - (instante.Ticks % (TimeSpan.TicksPerMillisecond / 1000)), instante.Offset);

    private static Task<HttpResponseMessage> PostAsync(HttpClient cliente, int idReferencia, params int[]? ids) =>
        cliente.PostAsJsonAsync(
            "/api/familias/previsualizacion", new SolicitudDePrevisualizacion(idReferencia, ids), OpcionesJson);

    private static async Task<PrevisualizacionDeAgrupacion> LeerAsync(HttpResponseMessage respuesta)
    {
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        return (await respuesta.Content.ReadFromJsonAsync<PrevisualizacionDeAgrupacion>(OpcionesJson))!;
    }

    private sealed record Escenario(
        Entorno E, int Referencia, int D1, int D2, int D3, int D4, int D5, int Minorista, int FamiliaAjena,
        DateTimeOffset V, DateTimeOffset W);

    private async Task<Escenario> SembrarAsync(string nombre)
    {
        var e = await apoyo.PrepararAsync(nombre);
        var minorista = await apoyo.SembrarListaFijaAsync(e, "Minorista");
        var @base = ValoresBase(e);
        var ids = IdsAscendentes(6);
        var (referencia, d1, d2, d3, d4, d5) = (ids[0], ids[1], ids[2], ids[3], ids[4], ids[5]);
        var familiaAjena = await apoyo.SembrarFamiliaAsync(e, "Familia ajena");

        await apoyo.SembrarArticuloAsync(e, "ref", @base, id: referencia);
        await apoyo.SembrarArticuloAsync(e, "d1", @base with { IdGrupo = e.Grupos[1], CostoLista = 60m }, id: d1);
        await apoyo.SembrarArticuloAsync(e, "d2", @base, id: d2);
        await apoyo.SembrarArticuloAsync(
            e, "d3",
            @base with
            {
                IdArea = e.Areas[1], IdCategoria = e.Categorias[1], UnidadVenta = UnidadVenta.Peso, ControlaLote = true,
                CostoNominal = 45m
            },
            id: d3);
        await apoyo.SembrarArticuloAsync(e, "d4", @base, familiaAjena, id: d4);
        await apoyo.SembrarArticuloAsync(e, "d5", @base, dadoDeBaja: true, id: d5);

        var v = DateTimeOffset.UtcNow.AddDays(3);
        var w = DateTimeOffset.UtcNow.AddDays(5);

        // La referencia: lista general con vigente 100 y pendiente 130 a partir de V; mayorista 80; minorista sin precios.
        await apoyo.SembrarPrecioPendienteAsync(e, referencia, e.IdListaGeneral, 100m, 130m, v);
        await apoyo.SembrarPrecioVigenteAsync(e, referencia, e.IdListaMayorista, 80m);

        // d1: otro vigente en la general, nada en la mayorista y un precio en la minorista, donde la referencia no tiene.
        await apoyo.SembrarPrecioVigenteAsync(e, d1, e.IdListaGeneral, 90m);
        await apoyo.SembrarPrecioVigenteAsync(e, d1, minorista, 55m);

        // d2: exactamente el estado de la referencia.
        await apoyo.SembrarPrecioPendienteAsync(e, d2, e.IdListaGeneral, 100m, 130m, v);
        await apoyo.SembrarPrecioVigenteAsync(e, d2, e.IdListaMayorista, 80m);

        // d3: nada en la general y un vigente con pendiente propio en la mayorista.
        await apoyo.SembrarPrecioPendienteAsync(e, d3, e.IdListaMayorista, 70m, 85m, w);

        return new Escenario(e, referencia, d1, d2, d3, d4, d5, minorista, familiaAjena, AMicrosegundos(v), AMicrosegundos(w));
    }

    private static ValoresCompartidosDeFamilia ValoresDeLaBase(Entorno e) => ValoresBase(e);

    // =================================================================================================
    // El resultado completo
    // =================================================================================================

    /// <summary>La previsualización completa del escenario, con cada campo leído con valores distintos: los artículos que
    /// se pueden alinear (el ya idéntico incluido, sin cambios), ascendentes por id y SIN los de otra familia, dado de baja
    /// o inexistentes; por cada uno las columnas que difieren con los trece valores actuales y nuevos, y el cambio de
    /// precios de cada lista fija en la que cambia; y los cuatro problemas, en el orden en que el pedido real los
    /// rechaza: los artículos inexistentes (el dado de baja y el id que no existe, ascendentes), el que ya está en otra
    /// familia, y el precio que no se puede alinear.</summary>
    [Fact]
    public async Task LaPrevisualizacionTraeLosCambiosDeCadaArticuloYTodosLosProblemas()
    {
        var s = await SembrarAsync(nameof(LaPrevisualizacionTraeLosCambiosDeCadaArticuloYTodosLosProblemas));
        using var e = s.E;
        const int inexistente = 999_999_999;
        var @base = ValoresDeLaBase(e);

        // Los ids van desordenados y con repetidos, y la referencia también figura en la lista.
        var previa = await LeerAsync(await PostAsync(
            e.Admin, s.Referencia, s.D3, s.D5, s.D1, s.D1, inexistente, s.D2, s.D4, s.Referencia));

        Assert.Equal(s.Referencia, previa.IdArticuloReferencia);
        Assert.Null(previa.IdFamilia);
        Assert.Equal([s.D1, s.D2, s.D3], previa.Articulos.Select(a => a.IdArticulo));

        var d1 = previa.Articulos[0];
        Assert.Equal(["id_grupo", "costo_lista"], d1.Campos);
        Assert.Equal(await ValoresDeAsync(s.D1), d1.Actual);
        Assert.Equal(@base, d1.Nuevo);
        Assert.Equal(
            [
                new CambioDePreciosDeLista(
                    e.IdListaGeneral, new EstadoDePrecios(90m, null), new EstadoDePrecios(100m, new PrecioPendiente(130m, s.V))),
                new CambioDePreciosDeLista(e.IdListaMayorista, EstadoDePrecios.Vacio, new EstadoDePrecios(80m, null))
            ],
            d1.Precios);

        var d2 = previa.Articulos[1];
        Assert.Empty(d2.Campos);
        Assert.Equal(@base, d2.Actual);
        Assert.Equal(@base, d2.Nuevo);
        Assert.Empty(d2.Precios);

        var d3 = previa.Articulos[2];
        Assert.Equal(["id_area", "id_categoria", "unidad_venta", "controla_lote", "costo_nominal"], d3.Campos);
        Assert.Equal(await ValoresDeAsync(s.D3), d3.Actual);
        Assert.Equal(@base, d3.Nuevo);
        Assert.Equal(
            [
                new CambioDePreciosDeLista(
                    e.IdListaGeneral, EstadoDePrecios.Vacio, new EstadoDePrecios(100m, new PrecioPendiente(130m, s.V))),
                new CambioDePreciosDeLista(
                    e.IdListaMayorista, new EstadoDePrecios(70m, new PrecioPendiente(85m, s.W)), new EstadoDePrecios(80m, null))
            ],
            d3.Precios);

        var nombreDeLaFamiliaAjena = (await apoyo.LeerFamiliaAsync(s.FamiliaAjena)).Nombre;
        var codigoDeD4 = (await apoyo.LeerAsync(s.D4)).CodigoInterno;
        var codigoDeD1 = (await apoyo.LeerAsync(s.D1)).CodigoInterno;

        Assert.Equal(
            [
                new ProblemaDeAgrupacion("referencia_invalida", $"No existe el artículo {s.D5}.", s.D5, null),
                new ProblemaDeAgrupacion("referencia_invalida", $"No existe el artículo {inexistente}.", inexistente, null),
                new ProblemaDeAgrupacion(
                    "articulo_en_otra_familia",
                    $"El artículo {codigoDeD4} ya pertenece a la familia \"{nombreDeLaFamiliaAjena}\": hay que sacarlo de ella antes de agruparlo.",
                    s.D4, null),
                new ProblemaDeAgrupacion(
                    "familia_precio_inalineable",
                    $"No se puede alinear el artículo {codigoDeD1} en la lista \"Minorista\": el artículo de referencia no tiene " +
                    "ningún precio en esa lista y este sí, y un precio no se puede quitar.",
                    s.D1, s.Minorista)
            ],
            previa.Problemas);
    }

    private async Task<ValoresCompartidosDeFamilia> ValoresDeAsync(int idArticulo) =>
        ValoresCompartidosDeFamilia.De(await apoyo.LeerAsync(idArticulo));

    /// <summary>Los dos motivos de rechazo de precios que decide el estado, cada uno con su mensaje: la referencia con
    /// un solo precio programado y un destino con vigente, y la referencia sin precios contra un destino que tiene
    /// alguno (que ya cubre el escenario común). El rechazo es por par: el artículo conserva sus demás
    /// listas.</summary>
    [Fact]
    public async Task UnaReferenciaSoloConUnPrecioProgramadoNoSeAlineaConUnDestinoConVigente()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaReferenciaSoloConUnPrecioProgramadoNoSeAlineaConUnDestinoConVigente));
        var ids = IdsAscendentes(3);
        var (referencia, conVigente, sinVigente) = (ids[0], ids[1], ids[2]);
        await apoyo.SembrarArticuloAsync(e, "ref", ValoresBase(e), id: referencia);
        await apoyo.SembrarArticuloAsync(e, "con-vigente", ValoresBase(e), id: conVigente);
        await apoyo.SembrarArticuloAsync(e, "sin-vigente", ValoresBase(e), id: sinVigente);

        var v = DateTimeOffset.UtcNow.AddDays(4);
        await apoyo.SembrarPrecioAsync(e, referencia, e.IdListaGeneral, 130m, v, null);
        await apoyo.SembrarPrecioVigenteAsync(e, conVigente, e.IdListaGeneral, 100m);

        var previa = await LeerAsync(await PostAsync(e.Admin, referencia, conVigente, sinVigente));

        var problema = Assert.Single(previa.Problemas);
        Assert.Equal(("familia_precio_inalineable", conVigente, (int?)e.IdListaGeneral), (problema.Codigo, problema.IdArticulo, problema.IdListaPrecio));
        Assert.Contains("solo tiene un precio programado y este ya tiene un precio vigente", problema.Mensaje, StringComparison.Ordinal);

        // El que no tiene vigente sí se alinea: queda con el pendiente de la referencia.
        var alineable = previa.Articulos.Single(a => a.IdArticulo == sinVigente);
        Assert.Equal(
            [new CambioDePreciosDeLista(e.IdListaGeneral, EstadoDePrecios.Vacio, new EstadoDePrecios(null, new PrecioPendiente(130m, AMicrosegundos(v))))],
            alineable.Precios);
    }

    /// <summary>Un dato que la API no produce: el pendiente del destino reemplaza a una fila vigente que empieza después de
    /// "ahora" (una ventana futura cerrada). Alinear la cerraría en "ahora", con la fecha de cierre antes que la de
    /// inicio, así que es un rechazo del par en vez del <c>vigente_desde_invalido</c> de un cambio de precio.</summary>
    [Fact]
    public async Task UnPendienteQueReemplazaAUnaFilaQueEmpiezaDespuesDeAhoraEsUnRechazoDelPar()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnPendienteQueReemplazaAUnaFilaQueEmpiezaDespuesDeAhoraEsUnRechazoDelPar));
        var ids = IdsAscendentes(2);
        var (referencia, destino) = (ids[0], ids[1]);
        await apoyo.SembrarArticuloAsync(e, "ref", ValoresBase(e), id: referencia);
        await apoyo.SembrarArticuloAsync(e, "destino", ValoresBase(e), id: destino);

        await apoyo.SembrarPrecioVigenteAsync(e, referencia, e.IdListaGeneral, 100m);

        var ahora = DateTimeOffset.UtcNow;
        await apoyo.SembrarPrecioAsync(e, destino, e.IdListaGeneral, 50m, ahora.AddDays(1), ahora.AddDays(2));
        await apoyo.SembrarPrecioAsync(e, destino, e.IdListaGeneral, 60m, ahora.AddDays(2), null);

        var previa = await LeerAsync(await PostAsync(e.Admin, referencia, destino));

        var problema = Assert.Single(previa.Problemas);
        Assert.Equal(("familia_precio_inalineable", destino, (int?)e.IdListaGeneral), (problema.Codigo, problema.IdArticulo, problema.IdListaPrecio));
        Assert.Contains("reemplaza a uno que empieza después de este momento", problema.Mensaje, StringComparison.Ordinal);
        Assert.Empty(previa.Articulos.Single().Precios);
    }

    // =================================================================================================
    // Solo lee
    // =================================================================================================

    /// <summary>La previsualización no escribe nada: ningún artículo, ninguna fila de precio, ninguna auditoría, ninguna
    /// familia. Las huellas completas de los artículos y los conteos de lo demás, antes y después.</summary>
    [Fact]
    public async Task LaPrevisualizacionNoEscribeNada()
    {
        var s = await SembrarAsync(nameof(LaPrevisualizacionNoEscribeNada));
        using var e = s.E;
        var ids = new[] { s.Referencia, s.D1, s.D2, s.D3, s.D4, s.D5 };

        var antes = await EstadoDeLaBaseAsync(e, ids);

        await LeerAsync(await PostAsync(e.Admin, s.Referencia, s.D1, s.D2, s.D3, s.D4, s.D5));

        Assert.Equal(antes, await EstadoDeLaBaseAsync(e, ids));
    }

    private async Task<string> EstadoDeLaBaseAsync(Entorno e, int[] ids)
    {
        var partes = new List<string>();
        foreach (var id in ids)
        {
            var a = await apoyo.LeerAsync(id);
            partes.Add($"{a.Id}|{a.IdFamilia}|{a.UpdatedAt:O}|{ValoresCompartidosDeFamilia.De(a)}");

            foreach (var lista in new[] { e.IdListaGeneral, e.IdListaMayorista })
            {
                partes.AddRange((await apoyo.FilasDePrecioAsync(id, lista)).Select(p => $"{p.Id}|{p.Monto}|{p.VigenteDesde:O}|{p.VigenteHasta:O}|{p.UpdatedAt:O}"));
            }
        }

        await using var db = fixture.CrearContextoDeAplicacion(Ways.Infrastructure.Multitenancy.TenantActualFijo.Plataforma);
        partes.Add($"familias={await db.Familias.IgnoreQueryFilters().CountAsync(f => f.IdTenant == e.IdTenant)}");
        partes.Add($"auditoria={(await apoyo.AuditoriaDePreciosAsync(e.IdTenant)).Count}");

        return string.Join("\n", partes);
    }

    /// <summary>La previsualización no toma ningún lock y por eso no espera a ninguno: con el lock de membresía EXCLUSIVO
    /// sostenido por otra conexión, las filas de la referencia y de dos artículos bloqueadas <c>FOR UPDATE</c> y el lock
    /// de un par artículo-lista tomado, termina antes de 15 segundos. Si pidiera cualquiera de ellos quedaría esperando
    /// a quien nunca los libera y la prueba fallaría por tiempo.</summary>
    [Fact]
    public async Task LaPrevisualizacionNoEsperaANingunLock()
    {
        var s = await SembrarAsync(nameof(LaPrevisualizacionNoEsperaANingunLock));
        using var e = s.E;

        await using var sostenedor = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var transaccion = await sostenedor.BeginTransactionAsync();
        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1)", Ways.Application.Familias.LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));
        await EjecutarAsync(
            sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = ANY($1) FOR UPDATE", new[] { s.Referencia, s.D1, s.D3 });
        var (clave1, clave2) = Ways.Application.Precios.ServicioDePrecios.ClaveDeLockDePar(e.IdTenant, s.D1, e.IdListaGeneral);
        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1, $2)", clave1, clave2);

        var respuesta = await PostAsync(e.Admin, s.Referencia, s.D1, s.D2, s.D3).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    /// <summary>La previsualización no rastrea ninguna entidad: ni los artículos, ni las familias, ni los precios que lee.
    /// Una lectura con rastreo dejaría en el contexto estado que nadie va a guardar, y que un guardado posterior sobre el
    /// mismo contexto escribiría por detrás.</summary>
    [Fact]
    public async Task LaPrevisualizacionNoDejaNingunaEntidadRastreada()
    {
        var s = await SembrarAsync(nameof(LaPrevisualizacionNoDejaNingunaEntidadRastreada));
        using var e = s.E;
        await using var db = apoyo.ContextoDelTenant(e);

        await ServicioDeAgrupacionDe(db, e).PrevisualizarAsync(
            new SolicitudDePrevisualizacion(s.Referencia, [s.D1, s.D2, s.D3, s.D4, s.D5]));

        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Los precios de todos los artículos y listas se leen en UNA consulta que se restringe a los artículos y a
    /// las listas pedidos: <c>id_articulo = ANY</c> y <c>id_lista_precio = ANY</c>. Quitar cualquiera de las dos
    /// condiciones no cambia ningún resultado —el estado de cada par se arma con sus propias filas— pero leería la
    /// historia de precios de todo el tenant, así que afirmarlo sobre el texto de la sentencia es la única red.</summary>
    [Fact]
    public async Task LaLecturaDeLosPreciosSeRestringeALosArticulosYALasListasPedidos()
    {
        var s = await SembrarAsync(nameof(LaLecturaDeLosPreciosSeRestringeALosArticulosYALasListasPedidos));
        using var e = s.E;
        var registro = new InterceptorQueRegistraSentencias();
        await using var db = apoyo.ContextoDelTenant(e, registro);

        await ServicioDeAgrupacionDe(db, e).PrevisualizarAsync(
            new SolicitudDePrevisualizacion(s.Referencia, [s.D1, s.D2, s.D3]));

        var lecturaDeLosPrecios = Assert.Single(
            registro.Sentencias, sentencia => sentencia.Contains("FROM precios", StringComparison.Ordinal));
        Assert.Contains("id_articulo = ANY", lecturaDeLosPrecios, StringComparison.Ordinal);
        Assert.Contains("id_lista_precio = ANY", lecturaDeLosPrecios, StringComparison.Ordinal);
    }

    /// <summary>"Ahora" se lee UNA vez: el reloj de la prueba da un instante distinto en cada lectura, así que una lectura
    /// por artículo o por lista no daría el mismo estado de precios para todos. Con un solo instante, un precio programado
    /// para dentro de dos segundos es pendiente para toda la operación.</summary>
    [Fact]
    public async Task LaPrevisualizacionLeeElRelojUnaSolaVez()
    {
        var s = await SembrarAsync(nameof(LaPrevisualizacionLeeElRelojUnaSolaVez));
        using var e = s.E;

        var reloj = new RelojContador(DateTimeOffset.UtcNow);
        await using var db = apoyo.ContextoDelTenant(e);

        await ServicioDeAgrupacionDe(db, e, reloj).PrevisualizarAsync(
            new SolicitudDePrevisualizacion(s.Referencia, [s.D1, s.D2, s.D3]));

        Assert.Equal(1, reloj.Lecturas);
    }

    // =================================================================================================
    // La referencia y su familia
    // =================================================================================================

    /// <summary>Si la referencia ya es miembro de una familia, agrupar sería sumar a ESA familia: <c>idFamilia</c> es la
    /// suya; un artículo que ya es miembro de ella no es un problema (y trae sus cambios, ninguno si ya está alineado),
    /// uno suelto se alinea y uno de otra familia es el único problema.</summary>
    [Fact]
    public async Task UnaReferenciaMiembroDeUnaFamiliaPrevisualizaSumarAEsaFamilia()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaReferenciaMiembroDeUnaFamiliaPrevisualizaSumarAEsaFamilia));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Gaseosas");
        var otraFamilia = await apoyo.SembrarFamiliaAsync(e, "Otra");
        var ids = IdsAscendentes(4);
        var (referencia, miembro, suelto, deOtra) = (ids[0], ids[1], ids[2], ids[3]);
        await apoyo.SembrarArticuloAsync(e, "ref", ValoresBase(e), familia, id: referencia);
        await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), familia, id: miembro);
        await apoyo.SembrarArticuloAsync(e, "suelto", ValoresConUnCampoCambiado(e, "costo_lista"), id: suelto);
        await apoyo.SembrarArticuloAsync(e, "de-otra", ValoresBase(e), otraFamilia, id: deOtra);

        var previa = await LeerAsync(await PostAsync(e.Admin, referencia, suelto, miembro, deOtra));

        Assert.Equal(familia, previa.IdFamilia);
        Assert.Equal([miembro, suelto], previa.Articulos.Select(a => a.IdArticulo));
        Assert.Empty(previa.Articulos[0].Campos);
        Assert.Equal(["costo_lista"], previa.Articulos[1].Campos);

        var problema = Assert.Single(previa.Problemas);
        Assert.Equal(("articulo_en_otra_familia", deOtra), (problema.Codigo, problema.IdArticulo));
        Assert.Contains("\"Otra\"", problema.Mensaje, StringComparison.Ordinal);
    }

    /// <summary>Una familia inactiva es el primer problema —el del pedido real, que la rechaza antes que a cualquier
    /// artículo—, y los cambios de los artículos se informan igual. Una referencia que apunta a una familia dada de
    /// baja es <c>no_encontrado</c>.</summary>
    [Fact]
    public async Task LosProblemasDeLaFamiliaVanPrimero()
    {
        using var e = await apoyo.PrepararAsync(nameof(LosProblemasDeLaFamiliaVanPrimero));
        var inactiva = await apoyo.SembrarFamiliaAsync(e, "Inactiva", activa: false);
        var dadaDeBaja = await apoyo.SembrarFamiliaAsync(e, "Dada de baja", dadaDeBaja: true);
        var ids = IdsAscendentes(4);
        var (deLaInactiva, deLaDadaDeBaja, suelto, inexistente) = (ids[0], ids[1], ids[2], 999_999_998);
        await apoyo.SembrarArticuloAsync(e, "de-la-inactiva", ValoresBase(e), inactiva, id: deLaInactiva);
        await apoyo.SembrarArticuloAsync(e, "de-la-dada-de-baja", ValoresBase(e), dadaDeBaja, id: deLaDadaDeBaja);
        await apoyo.SembrarArticuloAsync(e, "suelto", ValoresConUnCampoCambiado(e, "costo_lista"), id: suelto);

        var deLaFamiliaInactiva = await LeerAsync(await PostAsync(e.Admin, deLaInactiva, inexistente, suelto));

        Assert.Equal(["familia_inactiva", "referencia_invalida"], deLaFamiliaInactiva.Problemas.Select(p => p.Codigo));
        Assert.Null(deLaFamiliaInactiva.Problemas[0].IdArticulo);
        Assert.Contains("\"Inactiva\"", deLaFamiliaInactiva.Problemas[0].Mensaje, StringComparison.Ordinal);
        Assert.Equal(["costo_lista"], Assert.Single(deLaFamiliaInactiva.Articulos).Campos);

        var deLaFamiliaDadaDeBaja = await LeerAsync(await PostAsync(e.Admin, deLaDadaDeBaja, suelto));

        var problema = Assert.Single(deLaFamiliaDadaDeBaja.Problemas);
        Assert.Equal(("no_encontrado", $"No existe la familia {dadaDeBaja}."), (problema.Codigo, problema.Mensaje));
    }

    /// <summary>Una referencia que no existe, está dada de baja o es de otro tenant no deja nada contra qué comparar: la
    /// respuesta trae solo los artículos que no existen —la referencia y otro id—, sin cambios y sin familia, aunque
    /// los demás existan.</summary>
    [Fact]
    public async Task UnaReferenciaInexistenteDadaDeBajaODeOtroTenantSoloInformaLosInexistentes()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaReferenciaInexistenteDadaDeBajaODeOtroTenantSoloInformaLosInexistentes));
        using var otro = await apoyo.PrepararAsync(nameof(UnaReferenciaInexistenteDadaDeBajaODeOtroTenantSoloInformaLosInexistentes) + "-ajeno");
        var ids = IdsAscendentes(3);
        var (vivo, deBaja, ajeno) = (ids[0], ids[1], ids[2]);
        const int inexistente = 777_777;
        await apoyo.SembrarArticuloAsync(e, "vivo", ValoresBase(e), id: vivo);
        await apoyo.SembrarArticuloAsync(e, "de-baja", ValoresBase(e), dadoDeBaja: true, id: deBaja);
        await apoyo.SembrarArticuloAsync(otro, "ajeno", ValoresBase(otro), id: ajeno);

        // Cada referencia es MÁS ALTA que el otro id que no existe: los problemas salen ascendentes por id, no con la
        // referencia primero.
        foreach (var referencia in new[] { 999_999_997, deBaja, ajeno })
        {
            var previa = await LeerAsync(await PostAsync(e.Admin, referencia, vivo, inexistente));

            Assert.Null(previa.IdFamilia);
            Assert.Empty(previa.Articulos);
            Assert.Equal(
                [("referencia_invalida", inexistente), ("referencia_invalida", referencia)],
                previa.Problemas.Select(p => (p.Codigo, p.IdArticulo!.Value)));
        }
    }

    // =================================================================================================
    // El cuerpo
    // =================================================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task UnaReferenciaQueNoEsUnIdPosibleDa400(int idReferencia)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaReferenciaQueNoEsUnIdPosibleDa400));

        var respuesta = await PostAsync(e.Admin, idReferencia, 5);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("id_articulo_referencia_requerido", (await ProblemaAsync(respuesta)).Codigo);
    }

    /// <summary>Más de cien artículos además de la referencia dan 400 <c>demasiados_articulos</c>; cien exactos se
    /// aceptan (los inexistentes se informan como problemas, no como un rechazo). Los repetidos y la propia referencia
    /// no cuentan, y una lista ausente es una lista vacía.</summary>
    [Fact]
    public async Task ElTopeDeArticulosPorPedidoSeAplicaALosDestinosDistintosSinLaReferencia()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElTopeDeArticulosPorPedidoSeAplicaALosDestinosDistintosSinLaReferencia));
        var referencia = (await apoyo.SembrarArticuloAsync(e, "ref", ValoresBase(e)));

        var cien = Enumerable.Range(900_000_001, 100).ToArray();
        var admitido = await LeerAsync(await PostAsync(e.Admin, referencia, [.. cien, .. cien, referencia]));
        Assert.Equal(100, admitido.Problemas.Count);

        var demasiados = await PostAsync(e.Admin, referencia, [.. cien, 900_000_101]);
        Assert.Equal(HttpStatusCode.BadRequest, demasiados.StatusCode);
        Assert.Equal("demasiados_articulos", (await ProblemaAsync(demasiados)).Codigo);

        var sinLista = await e.Admin.PostAsJsonAsync(
            "/api/familias/previsualizacion", new { idArticuloReferencia = referencia }, OpcionesJson);
        var vacia = await LeerAsync(sinLista);
        Assert.Empty(vacia.Articulos);
        Assert.Empty(vacia.Problemas);
    }
}
