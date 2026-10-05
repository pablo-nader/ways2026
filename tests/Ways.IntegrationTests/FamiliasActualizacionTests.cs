using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Familias;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;
using static Ways.IntegrationTests.ApoyoDeFamilias;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>PUT /api/familias/{id}</c> (doc 10 §3, "Familias de artículos") contra Postgres real: el nombre y el estado de
/// una familia. Lo que la prueba defiende, cláusula por cláusula:
///
/// <list type="bullet">
/// <item>cada campo del cuerpo se escribe y se rechaza lo que no vale (<c>dto-contract-honesty</c>);</item>
/// <item>la unicidad del nombre: el chequeo previo y, sobre todo, el respaldo <c>ux_familias_nombre</c> —el que
/// responde en una carrera, con su SQLSTATE <c>23505</c> observado—;</item>
/// <item>la lectura bajo el lock de la fila (<c>single-read-under-lock</c>): una edición que pierde una carrera
/// escribe sobre lo que vio bajo el lock y no sobre una foto previa;</item>
/// <item>que no toma ningún otro lock, y que no se reintenta (<c>ef-retry-safe-writes</c>, forma (b)).</item>
/// </list>
///
/// Las pruebas de carrera son rendezvous determinísticos: una conexión cruda sostiene una escritura sin comitear y la
/// edición queda observada esperando en <c>pg_locks</c>, o un interceptor pausa la transacción después de abrirla y
/// antes de su primer statement.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class FamiliasActualizacionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeFamilias apoyo = new(fixture);

    private static Task<HttpResponseMessage> PutAsync(HttpClient cliente, int id, string? nombre, bool? activo) =>
        cliente.PutAsJsonAsync($"/api/familias/{id}", new EdicionFamilia(nombre, activo), OpcionesJson);

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta)
    {
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        return (await respuesta.Content.ReadFromJsonAsync<T>(OpcionesJson))!;
    }

    private static object Huella(Familia f) => new { f.Id, f.Nombre, f.Activo, f.CreatedAt, f.UpdatedAt, f.DeletedAt };

    // =================================================================================================
    // Qué se escribe
    // =================================================================================================

    /// <summary>Round trip del cuerpo: el nombre —sin los espacios de los extremos— y el estado se escriben, en los
    /// dos sentidos del estado. La respuesta es la familia como la lista, con sus miembros vivos (el dado de baja no
    /// cuenta). <c>created_at</c> y <c>deleted_at</c> no cambian, <c>updated_at</c> avanza, y los miembros no se
    /// tocan.</summary>
    [Fact]
    public async Task UnaEdicionEscribeElNombreSinEspaciosYElEstadoEnLosDosSentidosYNoTocaALosMiembros()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaEdicionEscribeElNombreSinEspaciosYElEstadoEnLosDosSentidosYNoTocaALosMiembros));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Original");
        var m1 = await apoyo.SembrarArticuloAsync(e, "m1", ValoresBase(e), familia);
        var m2 = await apoyo.SembrarArticuloAsync(e, "m2", ValoresBase(e), familia);
        var deBaja = await apoyo.SembrarArticuloAsync(e, "de-baja", ValoresBase(e), familia, dadoDeBaja: true);

        var antes = await apoyo.LeerFamiliaAsync(familia);
        var miembrosAntes = new[] { await apoyo.LeerAsync(m1), await apoyo.LeerAsync(m2), await apoyo.LeerAsync(deBaja) }
            .Select(a => new { a.Id, a.IdFamilia, a.Nombre, a.UpdatedAt, a.DeletedAt }).ToList();

        var respuesta = await LeerAsync<FamiliaListado>(await PutAsync(e.Admin, familia, "  Nuevo nombre  ", activo: false));

        Assert.Equal(new FamiliaListado(familia, "Nuevo nombre", false, 2), respuesta);

        var despues = await apoyo.LeerFamiliaAsync(familia);
        Assert.Equal(("Nuevo nombre", false, antes.CreatedAt, null as DateTimeOffset?), (despues.Nombre, despues.Activo, despues.CreatedAt, despues.DeletedAt));
        Assert.True(despues.UpdatedAt > antes.UpdatedAt);

        var activada = await LeerAsync<FamiliaListado>(await PutAsync(e.Admin, familia, "Nuevo nombre", activo: true));
        Assert.Equal(new FamiliaListado(familia, "Nuevo nombre", true, 2), activada);
        Assert.True((await apoyo.LeerFamiliaAsync(familia)).Activo);

        var miembrosDespues = new[] { await apoyo.LeerAsync(m1), await apoyo.LeerAsync(m2), await apoyo.LeerAsync(deBaja) }
            .Select(a => new { a.Id, a.IdFamilia, a.Nombre, a.UpdatedAt, a.DeletedAt }).ToList();
        Assert.Equal(miembrosAntes, miembrosDespues);
    }

    /// <summary>Cambiar solo la capitalización del propio nombre y repetir el mismo nombre son ediciones válidas: el
    /// chequeo previo excluye a la propia familia, que de otro modo se encontraría a sí misma (el nombre es
    /// <c>citext</c>).</summary>
    [Fact]
    public async Task CambiarSoloLaCapitalizacionDelPropioNombreOGuardarElMismoNoEsUnDuplicado()
    {
        using var e = await apoyo.PrepararAsync(nameof(CambiarSoloLaCapitalizacionDelPropioNombreOGuardarElMismoNoEsUnDuplicado));
        var familia = await apoyo.SembrarFamiliaAsync(e, "gaseosas");

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(e.Admin, familia, "GASEOSAS", true)).StatusCode);
        Assert.Equal("GASEOSAS", (await apoyo.LeerFamiliaAsync(familia)).Nombre);

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(e.Admin, familia, "GASEOSAS", true)).StatusCode);
        Assert.Equal("GASEOSAS", (await apoyo.LeerFamiliaAsync(familia)).Nombre);
    }

    // =================================================================================================
    // Qué se rechaza
    // =================================================================================================

    /// <summary>Cada cuerpo inválido da 400 con el código de su campo y no escribe nada: la fila queda idéntica. El
    /// <c>activo</c> ausente se rechaza en vez de leerse como <c>false</c> —que desactivaría la familia en silencio—.</summary>
    [Theory]
    [InlineData("""{"nombre":"","activo":true}""", "nombre_requerido")]
    [InlineData("""{"nombre":"   ","activo":true}""", "nombre_requerido")]
    [InlineData("""{"activo":true}""", "nombre_requerido")]
    [InlineData("""{"nombre":"Otro nombre"}""", "activo_requerido")]
    [InlineData("""{"nombre":"Otro nombre","activo":null}""", "activo_requerido")]
    public async Task UnCuerpoInvalidoDa400ConElCodigoDeSuCampoYNoEscribeNada(string cuerpo, string codigoEsperado)
    {
        using var e = await apoyo.PrepararAsync(nameof(UnCuerpoInvalidoDa400ConElCodigoDeSuCampoYNoEscribeNada));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Intacta");
        var antes = Huella(await apoyo.LeerFamiliaAsync(familia));

        var respuesta = await e.Admin.PutAsync(
            $"/api/familias/{familia}", new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(codigoEsperado, (await ProblemaAsync(respuesta)).Codigo);
        Assert.Equal(antes, Huella(await apoyo.LeerFamiliaAsync(familia)));
    }

    /// <summary>El nombre admite hasta 150 caracteres —la columna <c>citext</c> de 150— y no uno más.</summary>
    [Fact]
    public async Task UnNombreDeExactamenteElLargoMaximoSeAceptaYUnoMasLargoDa400()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnNombreDeExactamenteElLargoMaximoSeAceptaYUnoMasLargoDa400));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Corta");

        var justo = new string('a', 150);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(e.Admin, familia, justo, true)).StatusCode);
        Assert.Equal(justo, (await apoyo.LeerFamiliaAsync(familia)).Nombre);

        var largo = new string('b', 151);
        var respuesta = await PutAsync(e.Admin, familia, largo, true);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("nombre_muy_largo", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Equal(justo, (await apoyo.LeerFamiliaAsync(familia)).Nombre);
    }

    /// <summary>El nombre de OTRA familia viva del tenant, sin distinguir mayúsculas, da 409 con el mensaje del chequeo
    /// previo —que nombra el nombre pedido— y no escribe nada. El de una familia dada de baja y el de una familia de
    /// otro tenant no cuentan: la unicidad es parcial y por tenant.</summary>
    [Fact]
    public async Task ElNombreDeOtraFamiliaVivaDa409YElDeUnaDadaDeBajaOAjenaNo()
    {
        using var e = await apoyo.PrepararAsync(nameof(ElNombreDeOtraFamiliaVivaDa409YElDeUnaDadaDeBajaOAjenaNo));
        using var otro = await apoyo.PrepararAsync(nameof(ElNombreDeOtraFamiliaVivaDa409YElDeUnaDadaDeBajaOAjenaNo) + "-ajeno");
        await apoyo.SembrarFamiliaAsync(e, "Alfa");
        var beta = await apoyo.SembrarFamiliaAsync(e, "Beta");
        await apoyo.SembrarFamiliaAsync(e, "Vieja", dadaDeBaja: true);
        await apoyo.SembrarFamiliaAsync(otro, "Ajena");
        var antes = Huella(await apoyo.LeerFamiliaAsync(beta));

        var respuesta = await PutAsync(e.Admin, beta, "ALFA", true);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var (codigo, mensaje) = await ProblemaAsync(respuesta);
        Assert.Equal("familia_nombre_duplicado", codigo);
        Assert.Contains("\"ALFA\"", mensaje, StringComparison.Ordinal);
        Assert.Equal(antes, Huella(await apoyo.LeerFamiliaAsync(beta)));

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(e.Admin, beta, "Vieja", true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(e.Admin, beta, "Ajena", true)).StatusCode);
    }

    [Fact]
    public async Task UnaFamiliaInexistenteDadaDeBajaODeOtroTenantDa404YNoEscribeNada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaFamiliaInexistenteDadaDeBajaODeOtroTenantDa404YNoEscribeNada));
        using var otro = await apoyo.PrepararAsync(nameof(UnaFamiliaInexistenteDadaDeBajaODeOtroTenantDa404YNoEscribeNada) + "-ajeno");
        var dadaDeBaja = await apoyo.SembrarFamiliaAsync(e, "Dada de baja", dadaDeBaja: true);
        var ajena = await apoyo.SembrarFamiliaAsync(otro, "Ajena");
        var antesDeLaDadaDeBaja = Huella(await apoyo.LeerFamiliaAsync(dadaDeBaja));
        var antesDeLaAjena = Huella(await apoyo.LeerFamiliaAsync(ajena));

        foreach (var id in new[] { 999_999_999, dadaDeBaja, ajena })
        {
            var respuesta = await PutAsync(e.Admin, id, "Nombre nuevo", false);

            Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
            Assert.Equal("no_encontrado", (await ProblemaAsync(respuesta)).Codigo);
        }

        Assert.Equal(antesDeLaDadaDeBaja, Huella(await apoyo.LeerFamiliaAsync(dadaDeBaja)));
        Assert.Equal(antesDeLaAjena, Huella(await apoyo.LeerFamiliaAsync(ajena)));
    }

    // =================================================================================================
    // Unicidad del nombre: el respaldo, en una carrera
    // =================================================================================================

    /// <summary>Barrera sobre el <c>UPDATE familias</c> del host: cada edición que llega espera a que lleguen todas.
    /// Para entonces ya pasaron el chequeo previo del nombre, así que la carrera llega al respaldo, y ninguna escribió
    /// todavía. Registra además cada <see cref="PostgresException"/> que la base le devolvió a un comando: es lo que
    /// permite afirmar el SQLSTATE de la carrera aunque la API lo traduzca a un 409.</summary>
    private sealed class InterceptorDeBarreraDeEscrituras(int participantes) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource todasLlegaron = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<PostgresException> fallos = new();
        private int llegadas;

        public IReadOnlyList<PostgresException> Fallos => [.. fallos];

        private async Task EsperarSiEsUnaEscrituraDeFamiliasAsync(DbCommand comando)
        {
            if (!comando.CommandText.Contains("UPDATE familias", StringComparison.Ordinal))
            {
                return;
            }

            if (Interlocked.Increment(ref llegadas) == participantes)
            {
                todasLlegaron.TrySetResult();
            }

            await todasLlegaron.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await EsperarSiEsUnaEscrituraDeFamiliasAsync(command);

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await EsperarSiEsUnaEscrituraDeFamiliasAsync(command);

            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override Task CommandFailedAsync(
            DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is PostgresException postgres)
            {
                fallos.Enqueue(postgres);
            }

            return base.CommandFailedAsync(command, eventData, cancellationToken);
        }
    }

    /// <summary>Un host derivado de la fixture cuyo <see cref="WaysDbContext"/> suma <paramref name="interceptor"/>, y un
    /// cliente con la sesión del admin abierta contra ESE host. El host y el cliente se liberan con el
    /// <c>await using</c> de la prueba. Lo que la prueba lance contra el cliente de <see cref="Entorno.Admin"/> corre en
    /// el host sin interceptor.</summary>
    private async Task<(WebApplicationFactory<Program> Host, HttpClient Cliente)> ClienteDeUnHostConAsync(
        Entorno e, IInterceptor interceptor)
    {
        var host = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        var cliente = host.CreateClient();
        var login = await cliente.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(e.MailAdmin, e.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        return (host, cliente);
    }

    /// <summary>Dos ediciones simultáneas de dos familias distintas al MISMO nombre: las dos pasan el chequeo previo y
    /// se esperan en el <c>UPDATE</c> (la barrera las suelta juntas). Una gana con 200 y la otra recibe 409
    /// <c>familia_nombre_duplicado</c>, y el mensaje es el del RESPALDO —el del chequeo previo nombra el nombre
    /// pedido, éste no—. La base le devolvió a esa edición un <c>23505</c> sobre <c>ux_familias_nombre</c>: exactamente
    /// uno, observado en el comando. Queda una sola familia viva con ese nombre y la perdedora conserva el suyo.</summary>
    [Fact]
    public async Task DosEdicionesAlMismoNombreDejanUnaGanadoraYUnaPerdedoraConElRespaldoDe23505()
    {
        using var e = await apoyo.PrepararAsync(nameof(DosEdicionesAlMismoNombreDejanUnaGanadoraYUnaPerdedoraConElRespaldoDe23505));
        var uno = await apoyo.SembrarFamiliaAsync(e, "Uno");
        var dos = await apoyo.SembrarFamiliaAsync(e, "Dos");
        const string nombre = "Nombre en carrera";

        var interceptor = new InterceptorDeBarreraDeEscrituras(participantes: 2);
        var (host, cliente) = await ClienteDeUnHostConAsync(e, interceptor);
        await using var _ = host;
        using var __ = cliente;

        var respuestas = await Task.WhenAll(PutAsync(cliente, uno, nombre, true), PutAsync(cliente, dos, nombre, true))
            .WaitAsync(EsperaMaxima);

        var estados = respuestas.Select(r => r.StatusCode).Order().ToList();
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], estados);

        var perdedora = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var (codigo, mensaje) = await ProblemaAsync(perdedora);
        Assert.Equal("familia_nombre_duplicado", codigo);
        Assert.Equal("Ya existe una familia con ese nombre.", mensaje);

        var violacion = Assert.Single(interceptor.Fallos);
        Assert.Equal(("23505", "ux_familias_nombre"), (violacion.SqlState, violacion.ConstraintName));

        var nombres = new[] { (await apoyo.LeerFamiliaAsync(uno)).Nombre, (await apoyo.LeerFamiliaAsync(dos)).Nombre };
        Assert.Single(nombres, n => n == nombre);
        Assert.Single(nombres, n => n is "Uno" or "Dos");
    }

    // =================================================================================================
    // La lectura bajo el lock de la fila
    // =================================================================================================

    /// <summary>La edición toma el <c>FOR UPDATE</c> de la fila ANTES de leerla. Otra transacción tiene una escritura
    /// sin comitear sobre la familia (<c>activo = false</c>); la edición pide <c>activo = true</c> y queda observada
    /// esperando esa fila. Cuando la otra comitea, la edición lee el valor nuevo y escribe el suyo: la fila final
    /// tiene el nombre y el <c>activo</c> de la edición. Sin el lock, la edición leería el <c>activo</c> comiteado
    /// (<c>true</c>, igual al que pide), EF no vería cambio y el <c>UPDATE</c> saldría sin esa columna: la
    /// escritura de la ganadora sobreviviría y la fila quedaría con <c>false</c>. La aserción discriminante es la fila
    /// releída, no la respuesta ni la espera.</summary>
    [Fact]
    public async Task LaEdicionEsperaElLockDeLaFilaAntesDeLeerYEscribeSobreLoQueDejoLaOtra()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaEdicionEsperaElLockDeLaFilaAntesDeLeerYEscribeSobreLoQueDejoLaOtra));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Original");

        var (poll, sostenedor, transaccion) = await apoyo.AbrirSostenedorAsync(e.IdTenant);
        await using var _poll = poll;
        await using var _sostenedor = sostenedor;
        await using var _transaccion = transaccion;

        await EjecutarAsync(sostenedor, transaccion, "UPDATE familias SET activo = false WHERE id_familia = $1", familia);

        var edicion = PutAsync(e.Admin, familia, "Perdedora", activo: true);

        await EsperarAsync(
            () => EsperandoUnaFilaAsync(poll),
            "La edición nunca se observó esperando la fila de la familia.");
        Assert.False(edicion.IsCompleted);

        await transaccion.CommitAsync();

        Assert.Equal(HttpStatusCode.OK, (await edicion.WaitAsync(EsperaMaxima)).StatusCode);

        var fila = await apoyo.LeerFamiliaAsync(familia);
        Assert.Equal(("Perdedora", true), (fila.Nombre, fila.Activo));
    }

    /// <summary>Dos ediciones de la MISMA familia, con la perdedora pausada después de abrir su transacción y antes de
    /// su primer statement (<c>InterceptorDePausaTrasIniciarLaTransaccion</c>). La ganadora corre entera en el host
    /// sin interceptor y comitea un nombre y un <c>activo = false</c>. La perdedora pide justamente el
    /// <c>activo = true</c> que tenía la familia cuando ella empezó: con una lectura previa a la transacción, el valor
    /// pedido y el original de EF coincidirían, el <c>UPDATE</c> saldría sin esa columna y la fila se quedaría con el
    /// <c>false</c> de la ganadora con un 200 en la mano. La aserción es sobre la fila releída: la edición perdedora
    /// se escribe ENTERA.</summary>
    [Fact]
    public async Task LaEdicionQuePierdeLaCarreraEscribeEnteraSobreLoQueVioBajoElLock()
    {
        using var e = await apoyo.PrepararAsync(nameof(LaEdicionQuePierdeLaCarreraEscribeEnteraSobreLoQueVioBajoElLock));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Original");

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, clientePerdedor) = await ClienteDeUnHostConAsync(
            e, new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar));
        await using var _ = host;
        using var __ = clientePerdedor;

        var perdedora = PutAsync(clientePerdedor, familia, "Perdedora", activo: true);
        await transaccionIniciada.Task.WaitAsync(EsperaMaxima);

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(e.Admin, familia, "Ganadora", activo: false)).StatusCode);

        puedeContinuar.TrySetResult();

        var respuesta = await LeerAsync<FamiliaListado>(await perdedora.WaitAsync(EsperaMaxima));
        Assert.Equal(("Perdedora", true), (respuesta.Nombre, respuesta.Activo));

        var fila = await apoyo.LeerFamiliaAsync(familia);
        Assert.Equal(("Perdedora", true), (fila.Nombre, fila.Activo));
    }

    /// <summary>La familia se lee DESPUÉS del lock y con el filtro de baja lógica: si la dan de baja mientras la
    /// edición espera, el <c>FOR UPDATE</c> sigue encontrando la fila física pero la lectura ya no la ve, y la
    /// respuesta es <c>404</c> sin escribir nada. Con una lectura previa o sin ese filtro la edición escribiría sobre
    /// una familia dada de baja y daría 200.</summary>
    [Fact]
    public async Task UnaEdicionPausadaCuandoDanDeBajaLaFamiliaDa404YNoEscribeSobreLaFilaDadaDeBaja()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaEdicionPausadaCuandoDanDeBajaLaFamiliaDa404YNoEscribeSobreLaFilaDadaDeBaja));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Se disuelve");

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (host, clienteQuePierde) = await ClienteDeUnHostConAsync(
            e, new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar));
        await using var _ = host;
        using var __ = clienteQuePierde;

        var edicion = PutAsync(clienteQuePierde, familia, "Nombre tardío", activo: false);
        await transaccionIniciada.Task.WaitAsync(EsperaMaxima);

        await apoyo.DarDeBajaAsync("familias", "id_familia", familia);
        var dadaDeBaja = Huella(await apoyo.LeerFamiliaAsync(familia));

        puedeContinuar.TrySetResult();

        var respuesta = await edicion.WaitAsync(EsperaMaxima);
        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("no_encontrado", (await ProblemaAsync(respuesta)).Codigo);
        Assert.Equal(dadaDeBaja, Huella(await apoyo.LeerFamiliaAsync(familia)));
    }

    // =================================================================================================
    // Qué locks toma: solo el de su fila
    // =================================================================================================

    /// <summary>La edición no toma el lock de membresía ni bloquea ninguna fila de artículo: con el lock de membresía
    /// EXCLUSIVO sostenido por otra conexión y una fila de un miembro bloqueada <c>FOR UPDATE</c>, termina con 200
    /// antes de 15 segundos. Si tomara cualquiera de los dos, en el modo que fuera, quedaría esperando a quien nunca
    /// los libera y la prueba fallaría por tiempo. No observa nada en <c>pg_locks</c>: prueba que terminó con los dos
    /// locks todavía sostenidos.</summary>
    [Fact]
    public async Task UnaEdicionNoEsperaAlLockDeMembresiaNiALasFilasDeLosMiembros()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnaEdicionNoEsperaAlLockDeMembresiaNiALasFilasDeLosMiembros));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Con miembros bloqueados");
        var miembro = await apoyo.SembrarArticuloAsync(e, "miembro", ValoresBase(e), familia);

        await using var sostenedor = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var transaccion = await sostenedor.BeginTransactionAsync();
        await EjecutarAsync(sostenedor, transaccion, "SELECT pg_advisory_xact_lock($1)", LockDeMembresiaDeFamilias.ClaveDe(e.IdTenant));
        await EjecutarAsync(sostenedor, transaccion, "SELECT 1 FROM articulos WHERE id_articulo = $1 FOR UPDATE", miembro);

        var respuesta = await PutAsync(e.Admin, familia, "Renombrada", true).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    // =================================================================================================
    // Sin reintento (ef-retry-safe-writes, forma (b))
    // =================================================================================================

    private (WaysDbContext Db, ServicioDeFamilias Servicio) CrearServicio(Entorno e, params IInterceptor[] interceptores)
    {
        var db = fixture.CrearContextoDeAplicacionConReintentos(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant), interceptores);

        return (db, ServicioDe(db));
    }

    /// <summary>La edición no se reintenta ante un fallo transitorio: el contexto es el de la API, con
    /// <c>EnableRetryOnFailure</c>, y el interceptor rompe el primer <c>UPDATE familias</c> con un <c>40001</c>. El
    /// error llega tal cual, el <c>UPDATE</c> se intentó UNA vez y la fila queda intacta. Lo que se soltó del
    /// contexto: la familia que la edición había leído no queda rastreada, y la misma edición sobre el MISMO contexto
    /// —ya sin el interceptor— entra y escribe una vez. (Con la estrategia reintentable el segundo intento comitearía
    /// y esta prueba vería <c>Intentos == 2</c>.)</summary>
    [Fact]
    public async Task UnFalloTransitorioAlEditarNoSeReintentaYElContextoNoQuedaConLaFamiliaRastreada()
    {
        using var e = await apoyo.PrepararAsync(nameof(UnFalloTransitorioAlEditarNoSeReintentaYElContextoNoQuedaConLaFamiliaRastreada));
        var familia = await apoyo.SembrarFamiliaAsync(e, "Original");
        var antes = Huella(await apoyo.LeerFamiliaAsync(familia));

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("familias", "40001", ClaseDeSentencia.Update);
        var (db, servicio) = CrearServicio(e, interceptor);
        await using var _ = db;

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => servicio.ActualizarAsync(familia, new EdicionFamilia("Nueva", false)));

        Assert.Equal("40001", ErrorDePostgres(error).SqlState);
        Assert.Equal(1, interceptor.Intentos);
        Assert.Equal(antes, Huella(await apoyo.LeerFamiliaAsync(familia)));
        Assert.DoesNotContain(db.ChangeTracker.Entries(), entrada => entrada.Entity is Familia);

        var editada = await servicio.ActualizarAsync(familia, new EdicionFamilia("Nueva", false));

        Assert.Equal(("Nueva", false), (editada.Nombre, editada.Activo));
        Assert.Equal(("Nueva", false), ((await apoyo.LeerFamiliaAsync(familia)).Nombre, (await apoyo.LeerFamiliaAsync(familia)).Activo));
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
}
