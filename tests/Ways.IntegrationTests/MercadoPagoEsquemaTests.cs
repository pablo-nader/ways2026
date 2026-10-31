using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using static Ways.IntegrationTests.ApoyoDeMercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// El esquema de <c>conexiones_mercadopago</c> contra Postgres real: RLS bajo <c>ways_app</c>, los CHECK del estado y del material
/// cifrado, la FK compuesta que impide una cuenta de otra empresa y los dos índices únicos parciales, cada uno con su carrera
/// (SQLSTATE 23505 y nombre del índice) y con el backstop del callback anónimo forzado entre el chequeo previo y el INSERT.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MercadoPagoEsquemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeMercadoPago _apoyo = new(fixture);

    private static string Retorno(int idEmpresa, string resultado) =>
        $"http://localhost/organizacion/empresas?mercadopago={resultado}&empresa={idEmpresa}";

    private static async Task<PostgresException> ViolacionAsync(Task tarea) =>
        await Assert.ThrowsAsync<PostgresException>(() => tarea);

    // ---- RLS (cláusula bajo prueba: la política conexiones_mercadopago_tenant) -----------------------------------------

    [Fact]
    public async Task LaPoliticaDeTenantEscondeLaConexionDeOtroTenantYImpideEscribirEnEl()
    {
        var a = await _apoyo.PrepararAsync("mp-rls-a");
        var b = await _apoyo.PrepararAsync("mp-rls-b");
        await _apoyo.InsertarAsync(a.IdTenant, a.IdEmpresa, a.IdCuenta, NuevoIdUsuario(), a.IdUsuarioAdmin);

        async Task<long> ContarComoAsync(string modo, int? idTenant)
        {
            await using var conexion = await fixture.AbrirConexionCrudaAsync(modo, idTenant);
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT count(*) FROM conexiones_mercadopago WHERE id_empresa = $1";
            comando.Parameters.Add(new NpgsqlParameter { Value = a.IdEmpresa });
            return (long)(await comando.ExecuteScalarAsync())!;
        }

        Assert.Equal(1, await ContarComoAsync("tenant", a.IdTenant));
        Assert.Equal(0, await ContarComoAsync("tenant", b.IdTenant));
        Assert.Equal(0, await ContarComoAsync("ninguno", null));

        await using var conexionB = await fixture.AbrirConexionCrudaAsync("tenant", b.IdTenant);
        await using var insercion = conexionB.CreateCommand();
        insercion.CommandText =
            "INSERT INTO conexiones_mercadopago (id_tenant, id_empresa, id_cuenta_tesoreria, id_usuario_mercadopago, " +
            "access_token_cifrado, access_token_nonce, access_token_tag, refresh_token_cifrado, refresh_token_nonce, " +
            "refresh_token_tag, id_clave_maestra, access_token_expira_en, estado, id_usuario_conecto, fecha_conexion, " +
            "created_at, updated_at) VALUES ($1, $2, $3, $4, '\\x00', '\\x000000000000000000000000', " +
            "'\\x00000000000000000000000000000000', '\\x00', '\\x000000000000000000000000', " +
            "'\\x00000000000000000000000000000000', 't1', now(), 'activa', $5, now(), now(), now())";
        insercion.Parameters.Add(new NpgsqlParameter { Value = a.IdTenant });
        insercion.Parameters.Add(new NpgsqlParameter { Value = a.IdEmpresa });
        insercion.Parameters.Add(new NpgsqlParameter { Value = a.IdCuenta });
        insercion.Parameters.Add(new NpgsqlParameter { Value = NuevoIdUsuario() });
        insercion.Parameters.Add(new NpgsqlParameter { Value = a.IdUsuarioAdmin });

        Assert.Equal("42501", (await ViolacionAsync(insercion.ExecuteNonQueryAsync())).SqlState);
    }

    // ---- CHECK y FK ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnaConexionActivaNoAdmiteMaterialFaltanteNiDeLargoIncorrecto()
    {
        var ctx = await _apoyo.PrepararAsync("mp-check-activa");

        async Task AfirmarAsync(Task insercion)
        {
            var error = await ViolacionAsync(insercion);
            Assert.Equal("23514", error.SqlState);
            Assert.Equal("ck_conexiones_mercadopago_activa", error.ConstraintName);
        }

        await AfirmarAsync(_apoyo.InsertarAsync(
            ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, NuevoIdUsuario(), ctx.IdUsuarioAdmin, sinMaterial: true));
        await AfirmarAsync(_apoyo.InsertarAsync(
            ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, NuevoIdUsuario(), ctx.IdUsuarioAdmin, accessNonce: new byte[11]));
        await AfirmarAsync(_apoyo.InsertarAsync(
            ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, NuevoIdUsuario(), ctx.IdUsuarioAdmin, refreshTag: new byte[15]));
        await AfirmarAsync(_apoyo.InsertarAsync(
            ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, NuevoIdUsuario(), ctx.IdUsuarioAdmin, accessCifrado: []));
        await AfirmarAsync(_apoyo.InsertarAsync(
            ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, NuevoIdUsuario(), ctx.IdUsuarioAdmin, fechaDesconexion: DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task UnaConexionDesconectadaExigeFechaYNingunBlobYElEstadoSoloAdmiteLosDosValores()
    {
        var ctx = await _apoyo.PrepararAsync("mp-check-desconectada");

        var conBlobs = await ViolacionAsync(_apoyo.InsertarAsync(
            ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, NuevoIdUsuario(), ctx.IdUsuarioAdmin,
            estado: "desconectada", fechaDesconexion: DateTimeOffset.UtcNow));
        Assert.Equal("ck_conexiones_mercadopago_desconectada", conBlobs.ConstraintName);

        var sinFecha = await ViolacionAsync(_apoyo.InsertarAsync(
            ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, NuevoIdUsuario(), ctx.IdUsuarioAdmin,
            estado: "desconectada", sinMaterial: true));
        Assert.Equal("ck_conexiones_mercadopago_desconectada", sinFecha.ConstraintName);

        var otroEstado = await ViolacionAsync(_apoyo.InsertarAsync(
            ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, NuevoIdUsuario(), ctx.IdUsuarioAdmin, estado: "revocada"));
        Assert.Equal("23514", otroEstado.SqlState);
        Assert.Equal("ck_conexiones_mercadopago_estado", otroEstado.ConstraintName);

        // La forma correcta de una desconectada entra, y no ocupa el lugar de la activa de la empresa ni el de la cuenta de Mercado Pago.
        var idUsuarioMp = NuevoIdUsuario();
        await _apoyo.InsertarAsync(
            ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, idUsuarioMp, ctx.IdUsuarioAdmin,
            estado: "desconectada", sinMaterial: true, fechaDesconexion: DateTimeOffset.UtcNow);
        await _apoyo.InsertarAsync(
            ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, idUsuarioMp, ctx.IdUsuarioAdmin,
            estado: "desconectada", sinMaterial: true, fechaDesconexion: DateTimeOffset.UtcNow);
        await _apoyo.InsertarAsync(ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, idUsuarioMp, ctx.IdUsuarioAdmin);
    }

    [Fact]
    public async Task LaBaseRechazaUnaCuentaDeTesoreriaDeOtraEmpresaYDeOtroTenant()
    {
        var ctx = await _apoyo.PrepararAsync("mp-fk-cuenta");
        var otro = await _apoyo.PrepararAsync("mp-fk-cuenta-ajeno");
        var (_, cuentaDeOtraEmpresa) = await _apoyo.CrearOtraEmpresaConCuentaAsync(ctx, "Otra empresa");

        var deOtraEmpresa = await ViolacionAsync(_apoyo.InsertarAsync(
            ctx.IdTenant, ctx.IdEmpresa, cuentaDeOtraEmpresa, NuevoIdUsuario(), ctx.IdUsuarioAdmin));
        Assert.Equal("23503", deOtraEmpresa.SqlState);
        Assert.Equal("fk_conexiones_mercadopago_cuenta_tesoreria", deOtraEmpresa.ConstraintName);

        var deOtroTenant = await ViolacionAsync(_apoyo.InsertarAsync(
            ctx.IdTenant, ctx.IdEmpresa, otro.IdCuenta, NuevoIdUsuario(), ctx.IdUsuarioAdmin));
        Assert.Equal("23503", deOtroTenant.SqlState);
        Assert.Equal("fk_conexiones_mercadopago_cuenta_tesoreria", deOtroTenant.ConstraintName);
    }

    // ---- Índices únicos parciales: carreras reales a nivel de base ----------------------------------------------------

    [Fact]
    public async Task DosAltasConcurrentesActivasEnLaMismaEmpresaDejanUnaYLaOtraRecibe23505DelIndiceDeEmpresa()
    {
        var ctx = await _apoyo.PrepararAsync("mp-carrera-empresa");

        var resultados = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => IntentarAsync(
            _apoyo.InsertarAsync(ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, NuevoIdUsuario(), ctx.IdUsuarioAdmin))));

        Assert.Single(resultados, r => r is null);
        var perdedora = Assert.Single(resultados, r => r is not null)!;
        Assert.Equal("23505", perdedora.SqlState);
        Assert.Equal("ux_conexiones_mercadopago_empresa_activa", perdedora.ConstraintName);
    }

    [Fact]
    public async Task DosAltasConcurrentesActivasDeLaMismaCuentaDeMercadoPagoEnEmpresasDistintasDejanUnaYLaOtraRecibe23505DelIndiceDeUsuario()
    {
        var a = await _apoyo.PrepararAsync("mp-carrera-usuario-a");
        var b = await _apoyo.PrepararAsync("mp-carrera-usuario-b");
        var idUsuarioMp = NuevoIdUsuario();

        var resultados = await Task.WhenAll(
            IntentarAsync(_apoyo.InsertarAsync(a.IdTenant, a.IdEmpresa, a.IdCuenta, idUsuarioMp, a.IdUsuarioAdmin)),
            IntentarAsync(_apoyo.InsertarAsync(b.IdTenant, b.IdEmpresa, b.IdCuenta, idUsuarioMp, b.IdUsuarioAdmin)));

        Assert.Single(resultados, r => r is null);
        var perdedora = Assert.Single(resultados, r => r is not null)!;
        Assert.Equal("23505", perdedora.SqlState);
        Assert.Equal("ux_conexiones_mercadopago_usuario_activa", perdedora.ConstraintName);
    }

    private static async Task<PostgresException?> IntentarAsync(Task<int> insercion)
    {
        try
        {
            await insercion;
            return null;
        }
        catch (PostgresException e)
        {
            return e;
        }
    }

    // ---- Backstops del callback, forzados entre el chequeo previo y el INSERT -----------------------------------------

    /// <summary>Antes de ejecutar el INSERT de la conexión, corre <paramref name="competidora"/> en otra conexión y ya comiteada:
    /// el chequeo previo del servicio no la puede ver, así que solo el índice único decide.</summary>
    private sealed class InterceptorQueCompiteLaInsercion(Func<Task> competidora) : DbCommandInterceptor
    {
        private int _disparos;

        public int Disparos => _disparos;

        private async Task CompetirSiEsLaInsercionAsync(DbCommand comando)
        {
            var texto = comando.CommandText;
            if (texto.Contains("conexiones_mercadopago", StringComparison.Ordinal)
                && texto.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref _disparos) == 1)
            {
                await competidora();
            }
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            await CompetirSiEsLaInsercionAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            await CompetirSiEsLaInsercionAsync(command);
            return result;
        }
    }

    [Fact]
    public async Task SiOtraConexionActivaGanaLaEmpresaEntreElChequeoYElInsertElCallbackVuelveYaConectadaPorElIndice()
    {
        var ctx = await _apoyo.PrepararAsync("mp-backstop-empresa");
        using var anonimo = ClienteAnonimoSinRedirecciones(ctx.Host);
        var (_, estado) = await IniciarAsync(ctx);
        var interceptor = new InterceptorQueCompiteLaInsercion(async () =>
            await _apoyo.InsertarAsync(ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, NuevoIdUsuario(), ctx.IdUsuarioAdmin));

        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(interceptor))
        {
            respuesta = await LlamarAlCallbackAsync(anonimo, estado);
        }

        Assert.Equal(1, interceptor.Disparos);
        Assert.Equal(Retorno(ctx.IdEmpresa, "ya_conectada"), Ubicacion(respuesta));
        var fila = Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa));
        Assert.NotEqual(ctx.MercadoPago.IdUsuario, fila.IdUsuarioMercadoPago);
    }

    [Fact]
    public async Task SiOtraEmpresaConectaLaMismaCuentaDeMercadoPagoEntreElChequeoYElInsertElCallbackVuelveCuentaEnUsoPorElIndice()
    {
        var ctx = await _apoyo.PrepararAsync("mp-backstop-usuario");
        var (idOtraEmpresa, idOtraCuenta) = await _apoyo.CrearOtraEmpresaConCuentaAsync(ctx, "Otra empresa");
        using var anonimo = ClienteAnonimoSinRedirecciones(ctx.Host);
        var (_, estado) = await IniciarAsync(ctx);
        var interceptor = new InterceptorQueCompiteLaInsercion(async () =>
            await _apoyo.InsertarAsync(ctx.IdTenant, idOtraEmpresa, idOtraCuenta, ctx.MercadoPago.IdUsuario, ctx.IdUsuarioAdmin));

        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(interceptor))
        {
            respuesta = await LlamarAlCallbackAsync(anonimo, estado);
        }

        Assert.Equal(1, interceptor.Disparos);
        Assert.Equal(Retorno(ctx.IdEmpresa, "cuenta_en_uso"), Ubicacion(respuesta));
        Assert.Empty(await _apoyo.FilasAsync(ctx.IdEmpresa));
        Assert.Single(await _apoyo.FilasAsync(idOtraEmpresa));
    }
}
