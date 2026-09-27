using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Dispositivos;
using Ways.Application.Organizacion;
using Ways.Application.Pos;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Las dos CARRERAS que las guardas transaccionales de
/// <see cref="ServicioDeRendicionDeCola"/> existen para cubrir, más el delta estructural del
/// pre-chequeo del piso. Las tres nacen del mismo fenómeno que <c>mutation-proof-tests</c> regla 3
/// describe: el servicio pre-chequea lo mismo que el <c>WHERE</c> del <c>UPDATE</c> impone, así que
/// todo camino SECUENCIAL muere en el pre-chequeo y la rama <c>filas == 0</c> sobrevive a su borrado.
/// Sin estos casos, borrarla entera dejaba un <c>204</c> sobre una rendición que no escribió nada.
///
/// El rendezvous es un <see cref="DbCommandInterceptor"/> que pausa la PRIMERA lectura de
/// <c>reservas_numeracion</c> —el pre-chequeo— después de que el servidor la resolvió: en esa ventana
/// el servicio ya leyó el bloque y todavía no mandó el <c>UPDATE</c>, que es exactamente el
/// interleaving que una carrera libre no puede garantizar. No sirve acá el
/// <see cref="InterceptorDePausaTrasIniciarLaTransaccion"/> que usan los otros tests de carrera del
/// repo: esta operación es un único statement y NO abre transacción, así que el interceptor de
/// transacciones nunca dispararía (el propio doc-comment de esa clase remite al patrón de
/// <c>DbCommand</c> para este caso).
///
/// El servicio se llama DIRECTO, sin pipeline HTTP: la claim de dispositivo se modela con un
/// <see cref="IContextoDeUsuario"/> fijo, así que no hace falta el trámite de
/// <c>login-dispositivo</c> y el interceptor solo ve las consultas de la operación bajo prueba.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RendicionDeColaConcurrenciaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly DateTimeOffset Momento = new(2026, 9, 26, 16, 0, 0, TimeSpan.Zero);

    private sealed record Escenario(int IdTenant, int IdPuntoVenta, int IdDispositivo, long Desde, long Hasta);

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoDeDispositivo(int idTenant, int idDispositivo) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => 1;
        public string NombreUsuario => "cajero-de-dispositivo";
        public RolConocido Rol => RolConocido.Vendedor;
        public int? IdTenant { get; } = idTenant;
        public int? IdDispositivo { get; } = idDispositivo;
    }

    /// <summary>Pausa la PRIMERA lectura de <c>reservas_numeracion</c> que pase por el pipeline de EF,
    /// ya EJECUTADA: el <c>SELECT</c> resolvió su snapshot del lado del servidor, así que lo que
    /// comitee otro escritor durante la pausa no puede cambiar lo que el servicio va a leer — la
    /// lectura queda stale, que es el punto. La relectura que el servicio hace después de un
    /// <c>UPDATE</c> sin filas es la SEGUNDA y no se pausa.</summary>
    private sealed class InterceptorDePausaTrasLeerLaReserva(
        TaskCompletionSource lecturaHecha, TaskCompletionSource puedeContinuar) : DbCommandInterceptor
    {
        private int _lecturas;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("reservas_numeracion", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref _lecturas) == 1)
            {
                lecturaHecha.TrySetResult();
                await puedeContinuar.Task;
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Cuenta las lecturas de <c>reservas_numeracion</c> que EF emite por su pipeline. El
    /// <c>UPDATE</c> de la rendición es ADO crudo sobre la conexión y NO pasa por acá — por eso el
    /// delta que mide el test del pre-chequeo es la RELECTURA, no el statement.</summary>
    private sealed class ContadorDeLecturasDeReservas : DbCommandInterceptor
    {
        public int Lecturas { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("reservas_numeracion", StringComparison.OrdinalIgnoreCase))
            {
                Lecturas++;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Tenant aprovisionado de verdad (el catálogo de <c>tipos_comprobante</c> sale de ahí),
    /// un dispositivo vinculado por su endpoint real y un bloque reservado por el escritor de
    /// producción — ninguna siembra cruda de <c>reservas_numeracion</c>.</summary>
    private async Task<Escenario> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Escritorio));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        using var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        var altaDispositivo = await admin.PostAsJsonAsync(
            "/api/dispositivos", new AltaDispositivo(resultado.IdPuntoVenta, "Caja de escritorio"));
        Assert.Equal(HttpStatusCode.Created, altaDispositivo.StatusCode);
        var vinculado = (await altaDispositivo.Content.ReadFromJsonAsync<DispositivoVinculado>())!;

        await using var db = fixture.CrearContextoDeAplicacion(
            new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var (desde, hasta) = await AsignadorDeNumeroComprobante.ReservarBloqueAsync(
            db, resultado.IdTenant, resultado.IdPuntoVenta, "TX", vinculado.Datos.Id, cantidad: 10,
            momento: Momento);

        return new Escenario(
            resultado.IdTenant, resultado.IdPuntoVenta, vinculado.Datos.Id, desde, hasta);
    }

    private async Task<long?> LeerEntregadoHastaVivoAsync(Escenario e)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT entregado_hasta FROM reservas_numeracion " +
            "WHERE id_dispositivo = $1 AND abandonada_at IS NULL";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdDispositivo });

        var valor = await comando.ExecuteScalarAsync();
        return valor is null or DBNull ? null : Convert.ToInt64(valor);
    }

    /// <summary>La carrera del conjunto <c>abandonada_at IS NULL</c> del <c>UPDATE</c>, y el kill de la
    /// rama <c>filas == 0</c>: el pre-chequeo lee el bloque VIVO, el bloque se abandona mientras la
    /// request espera, y el <c>UPDATE</c> —solo él— tiene que negarse. Con la rama borrada, esta
    /// rendición responde como si hubiera escrito y no escribió nada, que es lo peor posible para una
    /// guarda de cierre que después le va a creer al reporte.
    ///
    /// El abandono se fuerza con el <c>UPDATE</c> crudo equivalente y no llamando a
    /// <c>ReservarBloqueAsync</c> a propósito, y hay que decir por qué: ese método abandona e INSERTA
    /// el bloque nuevo en la MISMA transacción, así que después de su commit siempre hay un bloque
    /// vivo. O sea que la transición "no queda ningún bloque vivo" no la produce hoy ningún escritor de
    /// producción — lo que se prueba acá es el ESTADO, no el autor (regla 3: entradas que las capas de
    /// arriba nunca producirían). La rama se conserva igual porque es fail-closed sobre un resultado
    /// que el servicio no puede explicar de otra manera.</summary>
    [Fact]
    public async Task LaRendicionSeRechazaCuandoElBloqueSeAbandonaEntreElPreChequeoYElUpdate()
    {
        var e = await PrepararAsync(nameof(LaRendicionSeRechazaCuandoElBloqueSeAbandonaEntreElPreChequeoYElUpdate));

        var lecturaHecha = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var db = fixture.CrearContextoDeAplicacion(
            new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant),
            new InterceptorDePausaTrasLeerLaReserva(lecturaHecha, puedeContinuar));
        var servicio = new ServicioDeRendicionDeCola(
            db, new RelojFijo(Momento), new ContextoDeDispositivo(e.IdTenant, e.IdDispositivo));

        var rendicion = Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.RegistrarAsync(new SolicitudDeRendicionDeCola("TX", e.Desde + 2, 0)));

        await lecturaHecha.Task;
        await AbandonarElBloqueVivoAsync(e);
        puedeContinuar.TrySetResult();

        var error = await rendicion;

        Assert.Equal("rendicion_sin_bloque_vivo", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
        Assert.Null(await LeerEntregadoHastaDeCualquierBloqueAsync(e));
    }

    /// <summary>La carrera del PISO MONÓTONO, y la que sí ocurre en producción tal cual: dos
    /// rendiciones del mismo dispositivo se solapan (un POST en vuelo que llega tarde, un reenvío del
    /// ciclo de sincronización). La pausada pasó su pre-chequeo contra la marca de agua vieja
    /// (<c>desde - 1</c>); mientras esperaba, la otra —el escritor de PRODUCCIÓN, no SQL a mano— dejó
    /// la marca en <c>desde + 5</c>. El conjunto del <c>UPDATE</c> es lo único que puede frenar a la
    /// pausada, y el valor persistido tiene que seguir siendo el más alto.</summary>
    [Fact]
    public async Task LaRendicionSeRechazaCuandoOtraSubeLaMarcaDeAguaEntreElPreChequeoYElUpdate()
    {
        var e = await PrepararAsync(nameof(LaRendicionSeRechazaCuandoOtraSubeLaMarcaDeAguaEntreElPreChequeoYElUpdate));

        var lecturaHecha = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var db = fixture.CrearContextoDeAplicacion(
            new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant),
            new InterceptorDePausaTrasLeerLaReserva(lecturaHecha, puedeContinuar));
        var servicio = new ServicioDeRendicionDeCola(
            db, new RelojFijo(Momento), new ContextoDeDispositivo(e.IdTenant, e.IdDispositivo));

        var rendicion = Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.RegistrarAsync(new SolicitudDeRendicionDeCola("TX", e.Desde + 2, 0)));

        await lecturaHecha.Task;

        await using (var otro = fixture.CrearContextoDeAplicacion(
            new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant)))
        {
            var filas = await AsignadorDeNumeroComprobante.RegistrarRendicionAsync(
                otro, e.IdTenant, e.IdPuntoVenta, "TX", e.IdDispositivo,
                entregadoHasta: e.Desde + 5, pendientes: 0, momento: Momento);
            Assert.Equal(1, filas);
        }

        puedeContinuar.TrySetResult();

        var error = await rendicion;

        Assert.Equal("rendicion_regresiva", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
        // El piso que informa es el que la OTRA rendición dejó, releído: un mutante que informe el
        // valor stale del pre-chequeo diría desde - 1.
        Assert.Contains((e.Desde + 5).ToString(), error.Message, StringComparison.Ordinal);
        Assert.Equal(e.Desde + 5, await LeerEntregadoHastaVivoAsync(e));
    }

    /// <summary>El pre-chequeo del piso NO tiene efecto observable en la respuesta —el conjunto del
    /// <c>UPDATE</c> produce el MISMO código y el MISMO mensaje— así que su único efecto real es un
    /// recurso que no se gasta, y se afirma como pide <c>mutation-proof-tests</c> regla 17: la
    /// cantidad de lecturas de <c>reservas_numeracion</c>. Con el pre-chequeo vivo hay UNA (la del
    /// pre-chequeo); borrándolo, el <c>UPDATE</c> se manda igual y la relectura que separa las dos
    /// causas suma una SEGUNDA, así que el test muere con <c>Expected 1, Actual 2</c>.</summary>
    [Fact]
    public async Task ElPreChequeoDelPisoRechazaSinVolverALeerElBloque()
    {
        var e = await PrepararAsync(nameof(ElPreChequeoDelPisoRechazaSinVolverALeerElBloque));

        await using (var previo = fixture.CrearContextoDeAplicacion(
            new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant)))
        {
            var filas = await AsignadorDeNumeroComprobante.RegistrarRendicionAsync(
                previo, e.IdTenant, e.IdPuntoVenta, "TX", e.IdDispositivo,
                entregadoHasta: e.Desde + 5, pendientes: 0, momento: Momento);
            Assert.Equal(1, filas);
        }

        var contador = new ContadorDeLecturasDeReservas();
        await using var db = fixture.CrearContextoDeAplicacion(
            new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant), contador);
        var servicio = new ServicioDeRendicionDeCola(
            db, new RelojFijo(Momento), new ContextoDeDispositivo(e.IdTenant, e.IdDispositivo));

        var error = await Assert.ThrowsAsync<ErrorDominio>(
            () => servicio.RegistrarAsync(new SolicitudDeRendicionDeCola("TX", e.Desde + 1, 0)));

        Assert.Equal("rendicion_regresiva", error.Codigo);
        Assert.Equal(1, contador.Lecturas);
        Assert.Equal(e.Desde + 5, await LeerEntregadoHastaVivoAsync(e));
    }

    /// <summary>Abandona el bloque vivo SIN insertar ninguno nuevo — ver el doc-comment del test que
    /// lo usa para por qué la transición se fuerza así.</summary>
    private async Task AbandonarElBloqueVivoAsync(Escenario e)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "UPDATE reservas_numeracion SET abandonada_at = now(), updated_at = now() " +
            "WHERE id_dispositivo = $1 AND abandonada_at IS NULL";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdDispositivo });

        Assert.Equal(1, await comando.ExecuteNonQueryAsync());
    }

    private async Task<long?> LeerEntregadoHastaDeCualquierBloqueAsync(Escenario e)
    {
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", e.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "SELECT entregado_hasta FROM reservas_numeracion WHERE id_dispositivo = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdDispositivo });

        var valor = await comando.ExecuteScalarAsync();
        return valor is null or DBNull ? null : Convert.ToInt64(valor);
    }
}
