using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Familias;
using Ways.Application.Parametros;
using Ways.Application.Precios;
using Ways.Application.Stock;
using Ways.Domain.Articulos;
using Ways.Domain.Stock;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using static Ways.IntegrationTests.ApoyoDeAgrupacion;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>Cuál de las dos operaciones que agrupan se prueba: <c>POST /api/familias</c> o
/// <c>POST /api/familias/{id}/articulos</c>. Las dos comparten el camino que toma los locks, escribe y reconcilia, y las
/// pruebas de este archivo las ejercitan a las dos.</summary>
public enum Agrupacion
{
    Crear,
    Agregar
}

/// <summary>
/// Locks, atomicidad y reconciliación de lotes de las dos operaciones que agrupan artículos en una familia (doc 10 §3, "Familias
/// de artículos"), contra Postgres real. Cambian la pertenencia y escriben campos y precios de varios artículos, así que siguen
/// el protocolo de locks de las familias: el lock de membresía EXCLUSIVO como primera sentencia; la fila de la familia (al
/// agregar); las filas de los artículos, ascendentes; los locks de par artículo-lista, ascendentes por clave; y recién entonces
/// "ahora" y las escrituras.
///
/// <para>Las pruebas de locks son rendezvous determinísticos (<c>mutation-proof-tests</c>, regla 13): una conexión cruda sostiene
/// un lock o una escritura sin comitear, el pedido queda observado esperando en <c>pg_locks</c> y recién entonces se libera, o se
/// pausa la transacción justo antes del commit y se miran los locks que sostiene. Al que no toma un lock lo mata la espera
/// observada; al que toma el lock equivocado, el conjunto exacto de locks que sostiene; al que los toma en otro orden, los que ya
/// tiene concedidos cuando espera el siguiente.</para>
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FamiliasAgrupacionConcurrenciaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private Task<Escenario> SembrarAsync(Agrupacion operacion, string nombre) =>
        ApoyoDeAgrupacion.SembrarAsync(apoyo, nombre, enFamilia: operacion == Agrupacion.Agregar);

    private static Task<HttpResponseMessage> PedirAsync(Agrupacion operacion, HttpClient cliente, Escenario s, params int[] destinos) =>
        operacion == Agrupacion.Crear
            ? PostCrearAsync(cliente, "Nueva", s.Referencia, destinos)
            : PostAgregarAsync(cliente, s.Familia!.Value, destinos);

    private static HttpStatusCode EstadoDelExito(Agrupacion operacion) =>
        operacion == Agrupacion.Crear ? HttpStatusCode.Created : HttpStatusCode.OK;

    private Task<string> FotoAsync(Escenario s) =>
        FotoDeLaBaseAsync(fixture, apoyo, s.E, s.TodosLosArticulos, s.ListasFijas);

    private static async Task EjecutarNoWaitAsync(NpgsqlConnection conexion, int idArticulo)
    {
        await using var comando = new NpgsqlCommand(
            "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR NO KEY UPDATE NOWAIT", conexion);
        comando.Parameters.Add(new NpgsqlParameter { Value = idArticulo });

        Assert.NotNull(await comando.ExecuteScalarAsync());
    }

    private static PostgresException ErrorDePostgres(Exception error)
    {
        for (Exception? actual = error; actual is not null; actual = actual.InnerException)
        {
            if (actual is PostgresException postgres)
            {
                return postgres;
            }
        }

        throw new InvalidOperationException($"La excepción no envuelve ninguna PostgresException: {error}");
    }

    // =================================================================================================
    // El lock de membresía exclusivo, primero
    // =================================================================================================

    /// <summary>Agrupar cambia la pertenencia y pide el lock de membresía EXCLUSIVO: con un compartido ajeno sostenido
    /// (stand-in de una escritura de precios en curso) queda esperando —observado pidiendo <c>ExclusiveLock</c>— ANTES de tomar
    /// ningún otro lock y de escribir nada. Cuando el otro lo libera termina bien, y "ahora" se leyó después de esa espera:
    /// los artículos escritos llevan un <c>updated_at</c> posterior al instante de la liberación. Con "ahora" leído antes de
    /// los locks sería anterior.</summary>
    [Theory]
    [InlineData(Agrupacion.Crear)]
    [InlineData(Agrupacion.Agregar)]
    public async Task AgruparPideElLockExclusivoYEsperaAUnCompartidoAjenoAntesDeTomarNingunOtro(Agrupacion operacion)
    {
        var s = await SembrarAsync(operacion, nameof(AgruparPideElLockExclusivoYEsperaAUnCompartidoAjenoAntesDeTomarNingunOtro) + operacion);
        using var e = s.E;
        var antes = await FotoAsync(s);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock_shared($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));

        var pedido = PedirAsync(operacion, e.Admin, s, s.D1, s.D3);

        var esperando = await EsperarAsync(
            () => EsperandoLaMembresiaAsync(poll, e.IdTenant),
            "Agrupar nunca se observó esperando el lock de membresía.");

        Assert.Equal("ExclusiveLock", esperando.Modo);
        Assert.False(pedido.IsCompleted);
        Assert.Empty(await CandadosConcedidosAsync(poll, esperando.Pid));
        Assert.Equal(0, await TransactionIdsConcedidosAsync(poll, esperando.Pid));
        Assert.Equal(antes, await FotoAsync(s));

        await Task.Delay(300);
        var liberadoEn = DateTimeOffset.UtcNow;
        await transaccion.CommitAsync();

        Assert.Equal(EstadoDelExito(operacion), (await pedido.WaitAsync(EsperaMaxima)).StatusCode);
        Assert.True((await apoyo.LeerAsync(s.D1)).UpdatedAt > liberadoEn);
    }

    // =================================================================================================
    // Las filas de los artículos
    // =================================================================================================

    /// <summary>Las filas se bloquean en orden ASCENDENTE de id, todas en un statement, DESPUÉS de la membresía. Un tercero
    /// sostiene <c>FOR UPDATE</c> sobre el artículo de id más BAJO de los involucrados: el pedido espera esa fila con la membresía
    /// como único lock concedido, y un <c>FOR NO KEY UPDATE NOWAIT</c> desde otra conexión sobre todos los de id más alto —la
    /// referencia, el gemelo, los otros destinos— tiene que salir bien; si el pedido los bloqueara de mayor a menor ya los tendría y
    /// fallaría con <c>55P03</c>. Cuando el tercero suelta, el pedido termina.</summary>
    [Theory]
    [InlineData(Agrupacion.Crear)]
    [InlineData(Agrupacion.Agregar)]
    public async Task LasFilasSeBloqueanEnOrdenAscendenteDespuesDeLaMembresia(Agrupacion operacion)
    {
        var s = await SembrarAsync(operacion, nameof(LasFilasSeBloqueanEnOrdenAscendenteDespuesDeLaMembresia) + operacion);
        using var e = s.E;

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        // El id más bajo de los involucrados es el de d1; la referencia, el gemelo y d3 tienen ids más altos.
        Assert.True(s.D1 < s.Referencia && s.Referencia < s.D3);
        await EjecutarAsync(sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR UPDATE", s.D1);

        var pedido = PedirAsync(operacion, e.Admin, s, s.D1, s.D3);

        var esperandoLaFila = await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "Agrupar nunca se observó esperando la fila del artículo de id más bajo.");

        var unico = Assert.Single(await CandadosConcedidosAsync(poll, esperandoLaFila.Valor));
        var (alto, bajo) = PartesDeLaClave(LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));
        Assert.Equal((1, alto, bajo, "ExclusiveLock"), (unico.ObjSubId, unico.ClassId, unico.ObjId, unico.Modo));

        foreach (var idMasAlto in operacion == Agrupacion.Crear
            ? new[] { s.Referencia, s.D3 }
            : new[] { s.Referencia, s.Gemelo, s.D3 })
        {
            await EjecutarNoWaitAsync(poll, idMasAlto);
        }

        await transaccion.RollbackAsync();

        Assert.Equal(EstadoDelExito(operacion), (await pedido.WaitAsync(EsperaMaxima)).StatusCode);
    }

    /// <summary>Al agregar se bloquean también los miembros de la familia que el pedido no nombra: el artículo de referencia es
    /// uno de ellos y su estado no puede cambiar bajo la escritura. El gemelo —miembro que no es la referencia ni figura en el
    /// pedido— tiene una escritura sin comitear: el pedido espera esa fila.</summary>
    [Fact]
    public async Task AgregarBloqueaLasFilasDeLosMiembrosQueElPedidoNoNombra()
    {
        var s = await SembrarAsync(Agrupacion.Agregar, nameof(AgregarBloqueaLasFilasDeLosMiembrosQueElPedidoNoNombra));
        using var e = s.E;

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR UPDATE", s.Gemelo);

        var pedido = PostAgregarAsync(e.Admin, s.Familia!.Value, s.D1);

        await EsperarAsync(() => EsperandoUnaFilaAsync(poll), "Agregar nunca se observó esperando la fila del gemelo.");
        Assert.False(pedido.IsCompleted);

        await transaccion.RollbackAsync();

        Assert.Equal(HttpStatusCode.OK, (await pedido.WaitAsync(EsperaMaxima)).StatusCode);
    }

    /// <summary>La referencia se lee DESPUÉS de bloquear su fila: un tercero tiene sin comitear un cambio de su costo, el pedido
    /// espera esa fila, y cuando el cambio comitea el destino queda con el costo NUEVO y el resultado lo informa. Con la
    /// referencia leída antes del lock, o sin bloquear su fila, el destino quedaría con el costo viejo.</summary>
    [Theory]
    [InlineData(Agrupacion.Crear)]
    [InlineData(Agrupacion.Agregar)]
    public async Task LaReferenciaSeLeeBajoSuLockYElDestinoQuedaConSuValorNuevo(Agrupacion operacion)
    {
        var s = await SembrarAsync(operacion, nameof(LaReferenciaSeLeeBajoSuLockYElDestinoQuedaConSuValorNuevo) + operacion);
        using var e = s.E;
        Assert.NotEqual(123m, s.ValoresDeLaReferencia.CostoLista);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "UPDATE articulos SET costo_lista = 123 WHERE id_articulo = $1", s.Referencia);

        var pedido = PedirAsync(operacion, e.Admin, s, s.D1);

        await EsperarAsync(() => EsperandoUnaFilaAsync(poll), "Agrupar nunca se observó esperando la fila de la referencia.");
        Assert.False(pedido.IsCompleted);

        await transaccion.CommitAsync();

        var resultado = await LeerResultadoAsync(await pedido.WaitAsync(EsperaMaxima), EstadoDelExito(operacion));

        var d1 = Assert.Single(resultado.Articulos);
        Assert.Equal(123m, d1.Nuevo.CostoLista);
        Assert.Contains("costo_lista", d1.Campos);
        Assert.Equal(123m, (await apoyo.LeerAsync(s.D1)).CostoLista);
        Assert.Equal(123m, (await apoyo.LeerAsync(s.Referencia)).CostoLista);
    }

    /// <summary>La existencia se comprueba contra lo que quedó bloqueado, no contra lo que había antes de esperar: un tercero
    /// da de baja <c>d3</c> sin comitear, el pedido espera su fila, y cuando la baja comitea <c>d3</c> ya no existe: 400
    /// <c>referencia_invalida</c> que lo nombra, y no se escribe nada.</summary>
    [Theory]
    [InlineData(Agrupacion.Crear)]
    [InlineData(Agrupacion.Agregar)]
    public async Task UnDestinoQueLoDanDeBajaMientrasSeEsperaSuFilaDa400YNoSeEscribeNada(Agrupacion operacion)
    {
        var s = await SembrarAsync(operacion, nameof(UnDestinoQueLoDanDeBajaMientrasSeEsperaSuFilaDa400YNoSeEscribeNada) + operacion);
        using var e = s.E;
        var antes = await FotoAsync(s);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "UPDATE articulos SET deleted_at = now() WHERE id_articulo = $1", s.D3);

        var pedido = PedirAsync(operacion, e.Admin, s, s.D1, s.D3);

        await EsperarAsync(() => EsperandoUnaFilaAsync(poll), "Agrupar nunca se observó esperando la fila del destino.");
        await transaccion.CommitAsync();

        var respuesta = await pedido.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(("referencia_invalida", $"No existe el artículo {s.D3}."), await ProblemaAsync(respuesta));

        Assert.Null((await apoyo.LeerAsync(s.D1)).IdFamilia);
        Assert.Equal(antes, await FotoAsync(s));
    }

    /// <summary>Las filas se bloquean <c>FOR NO KEY UPDATE</c> y no <c>FOR UPDATE</c>: el segundo chocaría con el
    /// <c>FOR KEY SHARE</c> que una venta toma sobre el artículo por las FK de sus renglones. Un tercero sostiene a mano un
    /// <c>FOR KEY SHARE</c> sobre un destino y sobre la referencia, y el pedido tiene que terminar sin esperarlos.</summary>
    [Theory]
    [InlineData(Agrupacion.Crear)]
    [InlineData(Agrupacion.Agregar)]
    public async Task UnForKeyShareAjenoSobreUnArticuloNoHaceEsperarAlPedido(Agrupacion operacion)
    {
        var s = await SembrarAsync(operacion, nameof(UnForKeyShareAjenoSobreUnArticuloNoHaceEsperarAlPedido) + operacion);
        using var e = s.E;

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = ANY($1) FOR KEY SHARE", new[] { s.D1, s.Referencia });

        var respuesta = await PedirAsync(operacion, e.Admin, s, s.D1, s.D3).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(EstadoDelExito(operacion), respuesta.StatusCode);
    }

    /// <summary>Solo se bloquean las filas VIVAS: un artículo dado de baja no se espera aunque otra conexión tenga su fila
    /// tomada. Al crear, el destino dado de baja recibe su 400 y, al agregar, el miembro dado de baja de la familia no frena
    /// al pedido. Con la baja lógica fuera del <c>WHERE</c> el pedido esperaría a quien nunca la libera y la prueba fallaría
    /// por tiempo.</summary>
    [Theory]
    [InlineData(Agrupacion.Crear)]
    [InlineData(Agrupacion.Agregar)]
    public async Task UnArticuloDadoDeBajaNoSeBloqueaNiSeEspera(Agrupacion operacion)
    {
        var s = await SembrarAsync(operacion, nameof(UnArticuloDadoDeBajaNoSeBloqueaNiSeEspera) + operacion);
        using var e = s.E;

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR UPDATE", s.DeBaja);

        var respuesta = await (operacion == Agrupacion.Crear
            ? PedirAsync(operacion, e.Admin, s, s.D1, s.DeBaja)
            : PedirAsync(operacion, e.Admin, s, s.D1)).WaitAsync(TimeSpan.FromSeconds(15));

        if (operacion == Agrupacion.Crear)
        {
            Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
            Assert.Equal(("referencia_invalida", $"No existe el artículo {s.DeBaja}."), await ProblemaAsync(respuesta));
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        }
    }

    /// <summary>Las entidades se leen restringidas a lo que quedó bloqueado y ascendentes por id: la lectura de <c>articulos</c>
    /// —la única que hace el servicio— pide <c>id_articulo = ANY</c> y <c>ORDER BY</c>. Sin la restricción leería y rastrearía
    /// todos los artículos del tenant, y sin el orden la referencia de una familia —el miembro de menor id— dependería del orden
    /// físico de las filas; ningún resultado de las pruebas cambiaría: afirmarlo sobre el texto de la sentencia es la única
    /// red.</summary>
    [Theory]
    [InlineData(Agrupacion.Crear)]
    [InlineData(Agrupacion.Agregar)]
    public async Task LaLecturaDeLasEntidadesSeRestringeALoBloqueadoYSeOrdenaPorId(Agrupacion operacion)
    {
        var s = await SembrarAsync(operacion, nameof(LaLecturaDeLasEntidadesSeRestringeALoBloqueadoYSeOrdenaPorId) + operacion);
        using var e = s.E;
        var registro = new InterceptorQueRegistraSentencias();
        var (db, servicio) = CrearServicio(e, registro);
        await using var _ = db;

        await AgruparAsync(operacion, servicio, s, s.D1, s.D3);

        var lectura = Assert.Single(registro.Sentencias, sentencia => sentencia.Contains("FROM articulos", StringComparison.Ordinal));
        Assert.Contains("id_articulo = ANY", lectura, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", lectura, StringComparison.Ordinal);
    }

    // =================================================================================================
    // La fila de la familia
    // =================================================================================================

    /// <summary>Agregar lee la familia <c>FOR SHARE</c>: con un cambio de su fila sin comitear (<c>activo = false</c>, o su baja)
    /// el pedido queda esperando esa fila con la membresía como único lock concedido, y al comitear el cambio lo ve: 409
    /// <c>familia_inactiva</c>, o 404 si la familia ya no existe. No escribe nada. Una lectura simple, sin el lock, no esperaría y
    /// vería el estado viejo, activo.</summary>
    [Theory]
    [InlineData("UPDATE familias SET activo = false WHERE id_familia = $1", HttpStatusCode.Conflict, "familia_inactiva")]
    [InlineData("UPDATE familias SET deleted_at = now() WHERE id_familia = $1", HttpStatusCode.NotFound, "no_encontrado")]
    public async Task AgregarEsperaUnCambioEnCursoDeLaFamiliaYLoVeAlTerminarSuEspera(
        string cambio, HttpStatusCode estado, string codigo)
    {
        var s = await SembrarAsync(Agrupacion.Agregar, nameof(AgregarEsperaUnCambioEnCursoDeLaFamiliaYLoVeAlTerminarSuEspera) + codigo);
        using var e = s.E;
        var antes = await FotoAsync(s);

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, cambio, s.Familia!.Value);

        var pedido = PostAgregarAsync(e.Admin, s.Familia!.Value, s.D1);

        var esperandoLaFila = await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll), "Agregar nunca se observó esperando la fila de la familia.");

        var unico = Assert.Single(await CandadosConcedidosAsync(poll, esperandoLaFila.Valor));
        Assert.Equal((1, "ExclusiveLock"), (unico.ObjSubId, unico.Modo));
        Assert.False(pedido.IsCompleted);

        await transaccion.CommitAsync();

        var respuesta = await pedido.WaitAsync(EsperaMaxima);
        Assert.Equal(estado, respuesta.StatusCode);
        Assert.Equal(codigo, (await ProblemaAsync(respuesta)).Codigo);
        Assert.Null((await apoyo.LeerAsync(s.D1)).IdFamilia);
        Assert.Equal(antes, await FotoAsync(s));
    }

    // =================================================================================================
    // Los locks de par artículo-lista
    // =================================================================================================

    /// <summary>Con la transacción pausada justo antes del commit, el pedido sostiene los locks de par de EXACTAMENTE cada destino
    /// en cada lista FIJA: los tres destinos por las tres listas fijas, ni uno más. La referencia no los tiene (no se le escribe
    /// ningún precio), tampoco el gemelo ni el artículo suelto, y la lista derivada, que no guarda filas, no figura. Y el lock de
    /// membresía lo sostiene en modo exclusivo.</summary>
    [Theory]
    [InlineData(Agrupacion.Crear)]
    [InlineData(Agrupacion.Agregar)]
    public async Task LosLocksDeParQueSostieneSonLosDeLosDestinosEnLasListasFijas(Agrupacion operacion)
    {
        var s = await SembrarAsync(operacion, nameof(LosLocksDeParQueSostieneSonLosDeLosDestinosEnLasListasFijas) + operacion);
        using var e = s.E;

        await using var poll = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        var alPuntoDeComitear = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeComitear = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<HttpResponseMessage> pedido;
        List<ApoyoDeFamilias.Candado> sostenidos;

        using (fixture.ConInterceptorEnElHost(new InterceptorDePausaAntesDelPrimerCommit(alPuntoDeComitear, puedeComitear)))
        {
            pedido = PedirAsync(operacion, e.Admin, s, s.D3, s.D1, s.D2);
            await alPuntoDeComitear.Task.WaitAsync(EsperaMaxima);

            var (alto, bajo) = PartesDeLaClave(LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));
            var deLaMembresia = Assert.Single(await CandadosAdvisoryAsync(
                poll, "granted AND objsubid = 1 AND classid::bigint = $1 AND objid::bigint = $2", alto, bajo));
            Assert.Equal("ExclusiveLock", deLaMembresia.Modo);

            sostenidos = await CandadosConcedidosAsync(poll, deLaMembresia.Pid);
            puedeComitear.SetResult();
        }

        Assert.Equal(EstadoDelExito(operacion), (await pedido.WaitAsync(EsperaMaxima)).StatusCode);

        var deParesSostenidos = sostenidos.Where(c => c.ObjSubId == 2).Select(c => (c.ClassId, c.ObjId)).Order().ToList();
        var esperados = new[] { s.D1, s.D2, s.D3 }
            .SelectMany(articulo => s.ListasFijas.Select(lista => ClaveDelPar(e.IdTenant, articulo, lista)))
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal(esperados, deParesSostenidos);
        Assert.All(sostenidos.Where(c => c.ObjSubId == 2), c => Assert.Equal("ExclusiveLock", c.Modo));
        Assert.Equal(1 + esperados.Count, sostenidos.Count);
    }

    /// <summary>Los locks de par se toman en orden ascendente de su CLAVE, no de id de artículo ni de lista. Dos destinos con
    /// ids a cada lado del punto en que <c>id * 397</c> desborda un entero —el de id más bajo tiene las claves MÁS altas— y las dos
    /// listas fijas del tenant: un tercero sostiene el primer par del destino de id bajo en orden de clave; el pedido espera ese
    /// lock con tomados, exactamente, los dos pares del otro destino (los de claves más bajas) y ninguno del que lo espera. Tomados
    /// por artículo, o por lista, tendría otros. Cuando el tercero suelta, el pedido termina, y "ahora" se leyó después de esa
    /// espera.</summary>
    [Theory]
    [InlineData(Agrupacion.Crear, 5_409_270, 5_409_290)]
    [InlineData(Agrupacion.Agregar, 5_409_250, 5_409_310)]
    public async Task LosLocksDeParSeTomanEnOrdenAscendenteDeClave(Agrupacion operacion, int idBajo, int idAlto)
    {
        using var e = await apoyo.PrepararAsync(nameof(LosLocksDeParSeTomanEnOrdenAscendenteDeClave) + operacion);
        var referencia = IdsAscendentes(1)[0];
        int? familia = operacion == Agrupacion.Agregar ? await apoyo.SembrarFamiliaAsync(e, "Gaseosas") : null;
        await apoyo.SembrarArticuloAsync(e, "ref", ValoresBase(e), familia, id: referencia);
        await apoyo.SembrarArticuloAsync(e, "bajo", ValoresBase(e), id: idBajo);
        await apoyo.SembrarArticuloAsync(e, "alto", ValoresBase(e), id: idAlto);
        int[] listas = [e.IdListaGeneral, e.IdListaMayorista];

        (int Clave1, int Clave2) Clave(int articulo, int lista) => ServicioDePrecios.ClaveDeLockDePar(e.IdTenant, articulo, lista);

        // Los dos pares de cada destino, por clave. El de id bajo tiene las claves altas: el producto por 397 no desborda.
        var delBajo = listas.Select(l => (Articulo: idBajo, Lista: l)).OrderBy(p => Clave(p.Articulo, p.Lista).Clave2).ToList();
        var delAlto = listas.Select(l => (Articulo: idAlto, Lista: l)).OrderBy(p => Clave(p.Articulo, p.Lista).Clave2).ToList();
        Assert.True(delBajo.Min(p => Clave(p.Articulo, p.Lista).Clave2) > delAlto.Max(p => Clave(p.Articulo, p.Lista).Clave2));

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        var sostenido = delBajo[0];
        var (clave1, clave2) = Clave(sostenido.Articulo, sostenido.Lista);
        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1, $2)", clave1, clave2);

        var pedido = operacion == Agrupacion.Crear
            ? PostCrearAsync(e.Admin, "Nueva", referencia, idAlto, idBajo)
            : PostAgregarAsync(e.Admin, familia!.Value, idAlto, idBajo);

        var (clase, objeto) = ClaveDelPar(e.IdTenant, sostenido.Articulo, sostenido.Lista);
        var esperando = await EsperarAsync(
            async () =>
            {
                var esperandoElPar = await CandadosAdvisoryAsync(
                    poll, "NOT granted AND objsubid = 2 AND classid::bigint = $1 AND objid::bigint = $2", clase, objeto);

                return esperandoElPar.SingleOrDefault();
            },
            "Agrupar nunca se observó esperando el primer par, en orden de clave, del destino de id más bajo.");

        var concedidos = await CandadosConcedidosAsync(poll, esperando.Pid);
        Assert.Equal(
            delAlto.Select(p => ClaveDelPar(e.IdTenant, p.Articulo, p.Lista)).Order().ToList(),
            concedidos.Where(c => c.ObjSubId == 2).Select(c => (c.ClassId, c.ObjId)).Order().ToList());
        Assert.Single(concedidos, c => c.ObjSubId == 1);

        await Task.Delay(300);
        var liberadoEn = DateTimeOffset.UtcNow;
        await transaccion.CommitAsync();

        Assert.Equal(EstadoDelExito(operacion), (await pedido.WaitAsync(EsperaMaxima)).StatusCode);
        Assert.True((await apoyo.LeerAsync(idBajo)).UpdatedAt > liberadoEn);
    }

    // =================================================================================================
    // Todo o nada, sin reintento
    // =================================================================================================

    private (WaysDbContext Db, ServicioDeAgrupacionDeFamilias Servicio) CrearServicio(
        Entorno e, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptores)
    {
        var db = fixture.CrearContextoDeAplicacionConReintentos(
            new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant), interceptores);

        return (db, ServicioDeAgrupacionDe(db, e));
    }

    private static Task<ResultadoDeAgrupacion> AgruparAsync(
        Agrupacion operacion, ServicioDeAgrupacionDeFamilias servicio, Escenario s, params int[] destinos) =>
        operacion == Agrupacion.Crear
            ? servicio.CrearAsync(new AltaDeFamilia("Nueva", s.Referencia, destinos))
            : servicio.AgregarArticulosAsync(s.Familia!.Value, new AgregadoDeArticulos(destinos));

    /// <summary>Agrupar es todo o nada y no se reintenta. El <c>40001</c> se inyecta en el <c>INSERT</c> de las filas de precio,
    /// que es de la fase de escrituras, DESPUÉS de insertar la familia y de modificar los artículos dentro de la transacción: al
    /// fallar, la transacción entera se revierte —la base queda idéntica—, el <c>INSERT</c> se intentó UNA vez (con la estrategia
    /// reintentable la segunda vuelta comitearía y serían dos), y lo que la operación dejó rastreado se suelta del contexto. Sin
    /// el interceptor la misma llamada sobre el MISMO contexto agrupa, y deja una sola familia.</summary>
    [Theory]
    [InlineData(Agrupacion.Crear)]
    [InlineData(Agrupacion.Agregar)]
    public async Task UnFalloTransitorioAlEscribirRevierteTodoNoSeReintentaYNoDejaNadaRastreado(Agrupacion operacion)
    {
        var s = await SembrarAsync(operacion, nameof(UnFalloTransitorioAlEscribirRevierteTodoNoSeReintentaYNoDejaNadaRastreado) + operacion);
        using var e = s.E;
        var antes = await FotoAsync(s);

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("precios", "40001");
        var (db, servicio) = CrearServicio(e, interceptor);
        await using var _ = db;

        var error = await Assert.ThrowsAnyAsync<Exception>(() => AgruparAsync(operacion, servicio, s, s.D1, s.D3));

        Assert.Equal("40001", ErrorDePostgres(error).SqlState);
        Assert.Equal(1, interceptor.Intentos);
        Assert.Equal(antes, await FotoAsync(s));
        Assert.Empty(db.ChangeTracker.Entries());

        var resultado = await AgruparAsync(operacion, servicio, s, s.D1, s.D3);

        Assert.Equal([s.D1, s.D3], resultado.Articulos.Select(a => a.IdArticulo));
        await using var lectura = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        Assert.Equal(
            operacion == Agrupacion.Crear ? 1 : 0,
            await lectura.Familias.IgnoreQueryFilters().CountAsync(f => f.IdTenant == e.IdTenant && f.Nombre == "Nueva"));
        Assert.Equal(resultado.IdFamilia, (await apoyo.LeerAsync(s.D1)).IdFamilia);
        Assert.Equal(resultado.IdFamilia, (await apoyo.LeerAsync(s.D3)).IdFamilia);
    }

    /// <summary>"Ahora" se lee UNA vez por operación, y las escrituras lo usan: con un reloj que da un instante distinto en cada
    /// lectura, la familia, los artículos que entran y las filas de precio cerradas y nuevas llevan el mismo instante.</summary>
    [Theory]
    [InlineData(Agrupacion.Crear)]
    [InlineData(Agrupacion.Agregar)]
    public async Task AhoraSeLeeUnaSolaVezYTodaLaEscrituraLoUsa(Agrupacion operacion)
    {
        var s = await SembrarAsync(operacion, nameof(AhoraSeLeeUnaSolaVezYTodaLaEscrituraLoUsa) + operacion);
        using var e = s.E;
        var inicio = DateTimeOffset.UtcNow;
        var reloj = new RelojContador(inicio);
        await using var db = apoyo.ContextoDelTenant(e);
        var servicio = ServicioDeAgrupacionDe(db, e, reloj);

        var resultado = await AgruparAsync(operacion, servicio, s, s.D1, s.D3);

        // La primera —y única— lectura del reloj da el instante un segundo después del inicio.
        Assert.Equal(1, reloj.Lecturas);
        var ahora = AMicrosegundos(inicio.AddSeconds(1));

        Assert.Equal(ahora, (await apoyo.LeerAsync(s.D1)).UpdatedAt);
        Assert.Equal(ahora, (await apoyo.LeerAsync(s.D3)).UpdatedAt);

        if (operacion == Agrupacion.Crear)
        {
            var familia = await apoyo.LeerFamiliaAsync(resultado.IdFamilia);
            Assert.Equal((ahora, ahora), (familia.CreatedAt, familia.UpdatedAt));
            Assert.Equal(ahora, (await apoyo.LeerAsync(s.Referencia)).UpdatedAt);
        }

        foreach (var (articulo, lista) in new[]
        {
            (s.D1, e.IdListaGeneral), (s.D1, e.IdListaMayorista), (s.D3, e.IdListaGeneral), (s.D3, e.IdListaMayorista)
        })
        {
            var filas = await apoyo.FilasDePrecioAsync(articulo, lista);

            Assert.NotEmpty(filas);
            Assert.All(filas, fila => Assert.Equal(ahora, fila.UpdatedAt));
        }
    }

    // =================================================================================================
    // La reconciliación de lotes
    // =================================================================================================

    private async Task SembrarStockAsync(Entorno e, int idArticulo, decimal cantidad)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        db.Stock.Add(new Stock { IdArticulo = idArticulo, IdPuntoVenta = e.IdPuntoVenta, IdTenant = e.IdTenant, Cantidad = cantidad });
        await db.SaveChangesAsync();
    }

    private sealed record EscenarioDeLotes(
        Entorno E, int Referencia, int Alto, int Medio, int SinStock, int YaControlaba, int Suelto, int? Familia);

    /// <summary>La referencia controla lotes. Tres destinos no la controlan y flipean al alinearse: dos con stock y uno sin; otro
    /// ya la controlaba y no flipea, y un artículo suelto con stock, fuera del pedido. Los ids de los dos con stock salen del
    /// pedido en orden inverso al de sus ids.</summary>
    private async Task<EscenarioDeLotes> SembrarLotesAsync(Agrupacion operacion, string nombre)
    {
        var e = await apoyo.PrepararAsync(nombre);
        var habilitado = await e.Admin.PutAsJsonAsync(
            $"/api/parametros?idEmpresa={e.IdEmpresa}", new ParametroAlta("lotes_habilitado", "true", null));
        Assert.Equal(HttpStatusCode.OK, habilitado.StatusCode);

        var ids = IdsAscendentes(6);
        var (referencia, medio, alto, sinStock, yaControlaba, suelto) = (ids[0], ids[1], ids[2], ids[3], ids[4], ids[5]);
        var conLote = ValoresBase(e) with { ControlaLote = true };
        int? familia = operacion == Agrupacion.Agregar ? await apoyo.SembrarFamiliaAsync(e, "Con lotes") : null;

        await apoyo.SembrarArticuloAsync(e, "ref", conLote, familia, id: referencia);
        await apoyo.SembrarArticuloAsync(e, "medio", ValoresBase(e), id: medio);
        await apoyo.SembrarArticuloAsync(e, "alto", ValoresBase(e), id: alto);
        await apoyo.SembrarArticuloAsync(e, "sin-stock", ValoresBase(e), id: sinStock);
        await apoyo.SembrarArticuloAsync(e, "ya-controlaba", conLote, id: yaControlaba);
        await apoyo.SembrarArticuloAsync(e, "suelto", ValoresBase(e), id: suelto);

        await SembrarStockAsync(e, medio, 25m);
        await SembrarStockAsync(e, alto, 60m);
        await SembrarStockAsync(e, yaControlaba, 40m);
        await SembrarStockAsync(e, suelto, 99m);

        return new EscenarioDeLotes(e, referencia, alto, medio, sinStock, yaControlaba, suelto, familia);
    }

    private static Task<HttpResponseMessage> PedirLotesAsync(Agrupacion operacion, HttpClient cliente, EscenarioDeLotes s) =>
        operacion == Agrupacion.Crear
            ? PostCrearAsync(cliente, "Con lotes", s.Referencia, s.Alto, s.Medio, s.SinStock, s.YaControlaba)
            : PostAgregarAsync(cliente, s.Familia!.Value, s.Alto, s.Medio, s.SinStock, s.YaControlaba);

    /// <summary>El flip de <c>controla_lote</c> de <c>false</c> a <c>true</c> reconcilia CADA artículo que flipeó: los dos con stock
    /// quedan con su par de movimientos de reclasificación y su stock en el lote sin identificar; el que no tiene stock no escribe
    /// nada, y tampoco el que ya controlaba lotes, la referencia ni el suelto con stock. Corren en orden ascendente de id: los
    /// movimientos del de id más bajo se escriben antes, aunque el pedido los traiga al revés.</summary>
    [Theory]
    [InlineData(Agrupacion.Crear)]
    [InlineData(Agrupacion.Agregar)]
    public async Task UnFlipDeControlaLoteReconciliaCadaArticuloQueFlipeoEnOrdenAscendente(Agrupacion operacion)
    {
        var s = await SembrarLotesAsync(operacion, nameof(UnFlipDeControlaLoteReconciliaCadaArticuloQueFlipeoEnOrdenAscendente) + operacion);
        using var e = s.E;
        Assert.True(s.Medio < s.Alto);

        var respuesta = await PedirLotesAsync(operacion, e.Admin, s);
        Assert.Equal(EstadoDelExito(operacion), respuesta.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));

        foreach (var (id, cantidad) in new[] { (s.Medio, 25m), (s.Alto, 60m) })
        {
            Assert.True((await apoyo.LeerAsync(id)).ControlaLote);
            Assert.Equal(2, await db.MovimientosStock.CountAsync(
                m => m.IdArticulo == id && m.IdPuntoVenta == e.IdPuntoVenta && m.Motivo == MotivoStock.Reclasificacion));

            var loteSinIdentificar = await db.Lotes.SingleAsync(l => l.IdArticulo == id && l.EsSinIdentificar);
            var stockLote = await db.StockLotes.SingleAsync(
                sl => sl.IdArticulo == id && sl.IdPuntoVenta == e.IdPuntoVenta && sl.IdLote == loteSinIdentificar.Id);
            Assert.Equal(cantidad, stockLote.Cantidad);
        }

        Assert.True((await apoyo.LeerAsync(s.SinStock)).ControlaLote);
        foreach (var id in new[] { s.SinStock, s.YaControlaba, s.Referencia, s.Suelto })
        {
            Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdArticulo == id && m.Motivo == MotivoStock.Reclasificacion));
        }

        var delMedio = await db.MovimientosStock.Where(m => m.IdArticulo == s.Medio).Select(m => m.Id).ToListAsync();
        var delAlto = await db.MovimientosStock.Where(m => m.IdArticulo == s.Alto).Select(m => m.Id).ToListAsync();
        Assert.True(
            delMedio.Max() < delAlto.Min(),
            "La reconciliación del artículo de id más bajo tiene que escribir sus movimientos antes que la del de id más alto.");
    }

    /// <summary>La reconciliación corre DESPUÉS del commit: si falla, la agrupación ya está hecha y queda así. El pedido
    /// termina con el error de la reconciliación —el <c>XX000</c> que se inyecta en la lectura del stock—, pero la familia, los
    /// artículos alineados y su <c>controla_lote</c> están comiteados y todavía no hay ningún movimiento de reclasificación.
    /// La recuperación documentada, <c>POST /api/stock/lotes/reconciliacion</c>, completa lo que faltó. Con la reconciliación
    /// dentro de la transacción el error la revertiría entera.</summary>
    [Theory]
    [InlineData(Agrupacion.Crear)]
    [InlineData(Agrupacion.Agregar)]
    public async Task UnFalloAlReconciliarDejaLaAgrupacionComiteadaYSeRecuperaPorElEndpoint(Agrupacion operacion)
    {
        var s = await SembrarLotesAsync(operacion, nameof(UnFalloAlReconciliarDejaLaAgrupacionComiteadaYSeRecuperaPorElEndpoint) + operacion);
        using var e = s.E;

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("stock", "XX000", ClaseDeSentencia.Select);
        var (db, servicio) = CrearServicio(e, interceptor);
        await using var _ = db;

        var error = await Assert.ThrowsAnyAsync<Exception>(() => operacion == Agrupacion.Crear
            ? servicio.CrearAsync(new AltaDeFamilia("Con lotes", s.Referencia, [s.Alto, s.Medio, s.SinStock, s.YaControlaba]))
            : servicio.AgregarArticulosAsync(s.Familia!.Value, new AgregadoDeArticulos([s.Alto, s.Medio, s.SinStock, s.YaControlaba])));

        Assert.Equal("XX000", ErrorDePostgres(error).SqlState);
        Assert.Equal(1, interceptor.Intentos);

        // Comiteado: los artículos están en la familia, alineados con la referencia, controlando lotes.
        foreach (var id in new[] { s.Alto, s.Medio, s.SinStock })
        {
            var articulo = await apoyo.LeerAsync(id);
            Assert.NotNull(articulo.IdFamilia);
            Assert.True(articulo.ControlaLote);
        }

        await using (var sinReconciliar = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant)))
        {
            Assert.Equal(0, await sinReconciliar.MovimientosStock.CountAsync(m => m.Motivo == MotivoStock.Reclasificacion));
        }

        // La recuperación documentada.
        var recuperacion = await e.Admin.PostAsJsonAsync(
            "/api/stock/lotes/reconciliacion", new SolicitudDeReconciliacion(s.Alto, null), OpcionesJson);
        Assert.Equal(HttpStatusCode.OK, recuperacion.StatusCode);
        Assert.Equal(1, (await recuperacion.Content.ReadFromJsonAsync<ResultadoDeReconciliacion>(OpcionesJson))!.ParesReconciliados);

        await using var despues = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant));
        Assert.Equal(2, await despues.MovimientosStock.CountAsync(m => m.IdArticulo == s.Alto && m.Motivo == MotivoStock.Reclasificacion));
    }

    // =================================================================================================
    // Dos agrupaciones a la vez
    // =================================================================================================

    /// <summary>Dos agrupaciones que comparten un destino, con dos referencias distintas, se serializan en el lock de membresía:
    /// las dos esperan —observadas— mientras otra conexión lo sostiene, y al soltarlo una crea su familia y la otra, que ya ve al
    /// destino como miembro de ella, recibe 409 <c>articulo_en_otra_familia</c>. El destino no se mueve: queda en la familia de la
    /// ganadora, con los precios de SU referencia, y la perdedora no escribe nada.</summary>
    [Fact]
    public async Task DosAgrupacionesQueComparteUnDestinoSeSerializanYSoloUnaGana()
    {
        var s = await SembrarAsync(Agrupacion.Crear, nameof(DosAgrupacionesQueComparteUnDestinoSeSerializanYSoloUnaGana));
        using var e = s.E;

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;
        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));

        var primera = PostCrearAsync(e.Admin, "Uno", s.Referencia, s.D1);
        var segunda = PostCrearAsync(e.Admin, "Dos", s.Gemelo, s.D1);

        var (alto, bajo) = PartesDeLaClave(LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));
        await EsperarAsync(
            async () =>
            {
                var esperando = await CandadosAdvisoryAsync(
                    poll, "NOT granted AND objsubid = 1 AND classid::bigint = $1 AND objid::bigint = $2", alto, bajo);

                return esperando.Count == 2 ? esperando : null;
            },
            "Las dos agrupaciones nunca se observaron esperando el lock de membresía.");

        await transaccion.CommitAsync();

        var respuestas = await Task.WhenAll(primera, segunda).WaitAsync(EsperaMaxima);

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], respuestas.Select(r => r.StatusCode).Order());

        var perdedora = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("articulo_en_otra_familia", (await ProblemaAsync(perdedora)).Codigo);

        var ganadora = await LeerResultadoAsync(respuestas.Single(r => r.StatusCode == HttpStatusCode.Created), HttpStatusCode.Created);
        Assert.Equal(ganadora.IdFamilia, (await apoyo.LeerAsync(s.D1)).IdFamilia);

        var referenciaDeLaPerdedora = ganadora.IdArticuloReferencia == s.Referencia ? s.Gemelo : s.Referencia;
        Assert.Null((await apoyo.LeerAsync(referenciaDeLaPerdedora)).IdFamilia);

        await using var lectura = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        Assert.Equal(1, await lectura.Familias.CountAsync(f => f.IdTenant == e.IdTenant && (f.Nombre == "Uno" || f.Nombre == "Dos")));
    }
}
