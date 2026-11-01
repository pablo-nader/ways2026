using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Ways.Application.MercadoPago;
using Ways.Api.MercadoPago;
using Ways.Domain.MercadoPago;
using Ways.Infrastructure.MercadoPago;
using Ways.Infrastructure.Persistencia;
using static Ways.IntegrationTests.ApoyoDeMercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// Renovación automática de las conexiones de Mercado Pago contra Postgres real: la ventana de selección, la rotación de los dos
/// tokens, el paso a <c>requiere_reconexion</c> ante un rechazo 4xx o una cuenta distinta, el 5xx que no toca nada, el
/// aislamiento entre conexiones y entre tenants y las dos carreras entre la selección y el lock. El endpoint de tokens es un
/// guion: responde solo a los refresh tokens que el test conoce y con un 503 al resto, así las conexiones que dejaron otros
/// tests en la misma base (la selección es entre todos los tenants) quedan intactas.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MercadoPagoRenovacionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeMercadoPago _apoyo = new(fixture);

    private sealed class Guion
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _porRefresco = [];

        public Guion Con(string refresco, Func<HttpResponseMessage> respuesta)
        {
            _porRefresco[refresco] = respuesta;
            return this;
        }

        public HttpResponseMessage Responder(string cuerpo)
        {
            using var json = JsonDocument.Parse(cuerpo);
            var raiz = json.RootElement;
            if (raiz.GetProperty("grant_type").GetString() == "refresh_token"
                && _porRefresco.TryGetValue(raiz.GetProperty("refresh_token").GetString()!, out var respuesta))
            {
                return respuesta();
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    private sealed record Conectada(Contexto Ctx, FilaDeConexion Fila, string Acceso, string Refresco);

    private static string Etiqueta() => Guid.NewGuid().ToString("N")[..8];

    private static HttpResponseMessage Rechazo(HttpStatusCode estado, string error = "invalid_grant") =>
        new(estado) { Content = new StringContent($$"""{"error":"{{error}}"}""", Encoding.UTF8, "application/json") };

    /// <summary>Conecta la empresa del contexto con tokens propios del test y deja al manejador sin guion (503 a todo).</summary>
    private async Task<Conectada> ConectarAsync(Contexto ctx)
    {
        var etiqueta = Etiqueta();
        var acceso = $"APP_USR-{etiqueta}-acceso-secreto";
        var refresco = $"TG-{etiqueta}-refresco-secreto";
        ctx.MercadoPago.Responder = _ => Exitoso(acceso, refresco, ctx.MercadoPago.IdUsuario);

        var (_, estado) = await IniciarAsync(ctx);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(ctx, estado)).StatusCode);

        ctx.MercadoPago.Responder = new Guion().Responder;
        var fila = (await _apoyo.FilasAsync(ctx.IdEmpresa)).Last(f => f.Estado == "activa");
        return new Conectada(ctx, fila, acceso, refresco);
    }

    private static (string Acceso, string Refresco) Descifrar(FilaDeConexion f) =>
        new CifradoDeTokensMercadoPago(ConfiguracionDeClaves()).Descifrar(new ConexionMercadoPago
        {
            Id = f.Id,
            IdTenant = f.IdTenant,
            IdEmpresa = f.IdEmpresa,
            IdClaveMaestra = f.IdClaveMaestra,
            AccessTokenCifrado = f.AccessCifrado,
            AccessTokenNonce = f.AccessNonce,
            AccessTokenTag = f.AccessTag,
            RefreshTokenCifrado = f.RefreshCifrado,
            RefreshTokenNonce = f.RefreshNonce,
            RefreshTokenTag = f.RefreshTag
        });

    private static void AfirmarSinCambios(FilaDeConexion antes, FilaDeConexion despues)
    {
        Assert.Equal(antes.Estado, despues.Estado);
        Assert.Equal(antes.AccessExpiraEn, despues.AccessExpiraEn);
        Assert.Equal(antes.FechaUltimaRenovacion, despues.FechaUltimaRenovacion);
        Assert.Null(despues.FechaRequiereReconexion);
        Assert.Equal(antes.AccessCifrado, despues.AccessCifrado);
        Assert.Equal(antes.AccessNonce, despues.AccessNonce);
        Assert.Equal(antes.AccessTag, despues.AccessTag);
        Assert.Equal(antes.RefreshCifrado, despues.RefreshCifrado);
        Assert.Equal(antes.RefreshNonce, despues.RefreshNonce);
        Assert.Equal(antes.RefreshTag, despues.RefreshTag);
    }

    private static void AfirmarRequiereReconexion(FilaDeConexion fila, DateTimeOffset ahora)
    {
        Assert.Equal("requiere_reconexion", fila.Estado);
        Assert.Null(fila.AccessCifrado);
        Assert.Null(fila.AccessNonce);
        Assert.Null(fila.AccessTag);
        Assert.Null(fila.RefreshCifrado);
        Assert.Null(fila.RefreshNonce);
        Assert.Null(fila.RefreshTag);
        Assert.Null(fila.FechaDesconexion);
        Assert.True(Math.Abs((fila.FechaRequiereReconexion!.Value - ahora).TotalMilliseconds) < 1);
    }

    private static int Llamadas(Contexto ctx, string refresco) => ctx.MercadoPago.Cuerpos.Count(c => c.Contains(refresco));

    private static ServicioDeRenovacionDeConexionesMercadoPago Servicio(IServiceScope alcance) =>
        alcance.ServiceProvider.GetRequiredService<ServicioDeRenovacionDeConexionesMercadoPago>();

    private async Task EjecutarSqlAsync(string sql, params object[] parametros)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var parametro in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = parametro });
        }

        await comando.ExecuteNonQueryAsync();
    }

    private static void AfirmarRenovada(Conectada antes, FilaDeConexion despues, string acceso, string refresco, DateTimeOffset ahora)
    {
        Assert.Equal("activa", despues.Estado);
        Assert.Equal((acceso, refresco), Descifrar(despues));
        Assert.NotEqual(antes.Fila.AccessCifrado, despues.AccessCifrado);
        Assert.NotEqual(antes.Fila.RefreshCifrado, despues.RefreshCifrado);
        Assert.NotEqual(antes.Fila.AccessNonce, despues.AccessNonce);
        Assert.NotEqual(antes.Fila.RefreshNonce, despues.RefreshNonce);
        Assert.True(Math.Abs((despues.AccessExpiraEn - ahora.AddSeconds(15_552_000)).TotalMilliseconds) < 1);
        Assert.True(Math.Abs((despues.FechaUltimaRenovacion!.Value - ahora).TotalMilliseconds) < 1);
        Assert.Null(despues.FechaRequiereReconexion);
    }

    // ---- Renovación y ventana ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UnaConexionDentroDeLaVentanaSeRenuevaYRotaLosDosTokensYElRefreshViejoYaNoQuedaGuardado()
    {
        var ctx = await _apoyo.PrepararAsync("mp-ren-ok");
        var conectada = await ConectarAsync(ctx);
        var (acceso2, refresco2) = ($"APP_USR-{Etiqueta()}-nuevo", $"TG-{Etiqueta()}-nuevo");
        ctx.MercadoPago.Responder = new Guion()
            .Con(conectada.Refresco, () => Exitoso(acceso2, refresco2, ctx.MercadoPago.IdUsuario)).Responder;
        var ahora = conectada.Fila.AccessExpiraEn.AddDays(-10);

        using var alcance = ctx.Host.Services.CreateScope();
        var resultado = await Servicio(alcance).RenovarConexionAsync(conectada.Fila.Id, ctx.IdTenant, ahora);

        Assert.Equal(ResultadoDeRenovacion.Renovada, resultado);
        Assert.Equal(1, Llamadas(ctx, conectada.Refresco));
        var despues = Assert.Single((await _apoyo.FilasAsync(ctx.IdEmpresa)).Where(f => f.Id == conectada.Fila.Id));
        AfirmarRenovada(conectada, despues, acceso2, refresco2, ahora);

        // Ningún byte de la fila conserva el refresh token viejo ni los tokens nuevos en claro.
        byte[] todo = [.. despues.AccessCifrado!, .. despues.RefreshCifrado!];
        Assert.False(todo.AsSpan().IndexOf(Encoding.UTF8.GetBytes(conectada.Refresco)) >= 0);
        Assert.False(todo.AsSpan().IndexOf(Encoding.UTF8.GetBytes(refresco2)) >= 0);
        Assert.DoesNotContain(refresco2, Descifrar(despues).Acceso);
    }

    [Fact]
    public async Task UnaConexionFueraDeLaVentanaNoSeTocaNiSeLlamaAMercadoPago()
    {
        var ctx = await _apoyo.PrepararAsync("mp-ren-fuera");
        var conectada = await ConectarAsync(ctx);
        ctx.MercadoPago.Responder = new Guion()
            .Con(conectada.Refresco, () => Exitoso("a", "r", ctx.MercadoPago.IdUsuario)).Responder;
        var ahora = conectada.Fila.AccessExpiraEn.AddDays(-40);

        using var alcance = ctx.Host.Services.CreateScope();
        var resultado = await Servicio(alcance).RenovarConexionAsync(conectada.Fila.Id, ctx.IdTenant, ahora);

        Assert.Equal(ResultadoDeRenovacion.Omitida, resultado);
        Assert.Equal(0, Llamadas(ctx, conectada.Refresco));
        AfirmarSinCambios(conectada.Fila, Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa)));
    }

    [Fact]
    public async Task LaCorridaSeleccionaSoloLasConexionesDentroDeLaVentanaDeTodosLosTenants()
    {
        var a = await _apoyo.PrepararAsync("mp-ren-ventana-a");
        var b = await _apoyo.PrepararAsync("mp-ren-ventana-b");
        var enVentana = await ConectarAsync(a);
        var fuera = await ConectarAsync(b);
        var (acceso2, refresco2) = ($"APP_USR-{Etiqueta()}-nuevo", $"TG-{Etiqueta()}-nuevo");

        // La de A vence 5 días antes que la de B: la corrida de "ahora" deja a A dentro de los 30 días y a B apenas afuera.
        await EjecutarSqlAsync(
            "UPDATE conexiones_mercadopago SET access_token_expira_en = $1 WHERE id_conexion_mercadopago = $2",
            enVentana.Fila.AccessExpiraEn.AddDays(-5), enVentana.Fila.Id);
        var ahora = fuera.Fila.AccessExpiraEn.AddDays(-30).AddMinutes(-1);
        a.MercadoPago.Responder = new Guion()
            .Con(enVentana.Refresco, () => Exitoso(acceso2, refresco2, a.MercadoPago.IdUsuario))
            .Con(fuera.Refresco, () => Exitoso("no-debe-usarse", "no-debe-usarse", b.MercadoPago.IdUsuario)).Responder;

        using var alcance = a.Host.Services.CreateScope();
        var resumen = await Servicio(alcance).RenovarConexionesPorVencerAsync(ahora);

        Assert.True(resumen.Renovadas >= 1);
        Assert.Equal(1, Llamadas(a, enVentana.Refresco));
        Assert.Equal(0, Llamadas(a, fuera.Refresco));
        var deA = Assert.Single(await _apoyo.FilasAsync(a.IdEmpresa));
        Assert.Equal((acceso2, refresco2), Descifrar(deA));
        AfirmarSinCambios(fuera.Fila, Assert.Single(await _apoyo.FilasAsync(b.IdEmpresa)));
    }

    [Fact]
    public async Task LaCorridaRenuevaConexionesDeDosTenantsDistintosCadaUnaConSuPropioAad()
    {
        var a = await _apoyo.PrepararAsync("mp-ren-cruzada-a");
        var b = await _apoyo.PrepararAsync("mp-ren-cruzada-b");
        var deA = await ConectarAsync(a);
        var deB = await ConectarAsync(b);
        var (accesoA, refrescoA) = ($"APP_USR-{Etiqueta()}-a", $"TG-{Etiqueta()}-a");
        var (accesoB, refrescoB) = ($"APP_USR-{Etiqueta()}-b", $"TG-{Etiqueta()}-b");
        a.MercadoPago.Responder = new Guion()
            .Con(deA.Refresco, () => Exitoso(accesoA, refrescoA, a.MercadoPago.IdUsuario))
            .Con(deB.Refresco, () => Exitoso(accesoB, refrescoB, b.MercadoPago.IdUsuario)).Responder;
        var ahora = deA.Fila.AccessExpiraEn.AddDays(-10);

        using var alcance = a.Host.Services.CreateScope();
        var resumen = await Servicio(alcance).RenovarConexionesPorVencerAsync(ahora);

        Assert.True(resumen.Renovadas >= 2);
        var filaA = Assert.Single(await _apoyo.FilasAsync(a.IdEmpresa));
        var filaB = Assert.Single(await _apoyo.FilasAsync(b.IdEmpresa));
        AfirmarRenovada(deA, filaA, accesoA, refrescoA, ahora);
        AfirmarRenovada(deB, filaB, accesoB, refrescoB, ahora);
        Assert.NotEqual(filaA.IdTenant, filaB.IdTenant);
    }

    // ---- Rechazos y fallas ------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnBadRequestInvalidGrantPasaLaConexionARequiereReconexionBorraElMaterialYPermiteVolverAConectar()
    {
        var ctx = await _apoyo.PrepararAsync("mp-ren-4xx");
        var conectada = await ConectarAsync(ctx);
        ctx.MercadoPago.Responder = new Guion().Con(conectada.Refresco, () => Rechazo(HttpStatusCode.BadRequest)).Responder;
        var ahora = conectada.Fila.AccessExpiraEn.AddDays(-10);

        using (var alcance = ctx.Host.Services.CreateScope())
        {
            var resultado = await Servicio(alcance).RenovarConexionAsync(conectada.Fila.Id, ctx.IdTenant, ahora);
            Assert.Equal(ResultadoDeRenovacion.RequiereReconexion, resultado);
        }

        Assert.Equal(1, Llamadas(ctx, conectada.Refresco));
        AfirmarRequiereReconexion(Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa)), ahora);

        var lectura = await ctx.Admin.GetFromJsonAsync<EstadoDeConexionMercadoPago>(ctx.Ruta);
        Assert.False(lectura!.Conectada);
        Assert.Equal("requiere_reconexion", lectura.Estado);
        Assert.Equal(ctx.IdCuenta, lectura.IdCuentaTesoreria);
        Assert.Null(lectura.AccessTokenExpiraEn);

        // La empresa vuelve a conectar: la fila vieja queda como historia y la nueva es la activa.
        var otra = await ConectarAsync(ctx);
        Assert.Equal(["requiere_reconexion", "activa"], (await _apoyo.FilasAsync(ctx.IdEmpresa)).Select(f => f.Estado));
        var activa = await ctx.Admin.GetFromJsonAsync<EstadoDeConexionMercadoPago>(ctx.Ruta);
        Assert.True(activa!.Conectada);
        Assert.Equal("activa", activa.Estado);
        Assert.NotEqual(conectada.Fila.Id, otra.Fila.Id);

        // Y si después se desconecta, la última conexión es la desconectada: ya no se ofrece "reconectar".
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync(ctx.Ruta)).StatusCode);
        var tras = await ctx.Admin.GetFromJsonAsync<EstadoDeConexionMercadoPago>(ctx.Ruta);
        Assert.False(tras!.Conectada);
        Assert.Null(tras.Estado);
    }

    [Fact]
    public async Task UnTokenDeAccesoYaVencidoCuyaRenovacionSeRechazaTambienRequiereReconexion()
    {
        var ctx = await _apoyo.PrepararAsync("mp-ren-vencido");
        var conectada = await ConectarAsync(ctx);
        ctx.MercadoPago.Responder = new Guion().Con(conectada.Refresco, () => Rechazo(HttpStatusCode.BadRequest)).Responder;
        var ahora = conectada.Fila.AccessExpiraEn.AddDays(2);

        using var alcance = ctx.Host.Services.CreateScope();
        var resultado = await Servicio(alcance).RenovarConexionAsync(conectada.Fila.Id, ctx.IdTenant, ahora);

        Assert.Equal(ResultadoDeRenovacion.RequiereReconexion, resultado);
        AfirmarRequiereReconexion(Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa)), ahora);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    [InlineData(408)]
    [InlineData(0)]
    public async Task UnaFallaTransitoriaNoTocaLaConexionYLaProximaCorridaLaRenueva(int estado)
    {
        var ctx = await _apoyo.PrepararAsync("mp-ren-5xx");
        var conectada = await ConectarAsync(ctx);
        var ahora = conectada.Fila.AccessExpiraEn.AddDays(2);
        ctx.MercadoPago.Responder = estado == 0
            ? (Func<string, HttpResponseMessage>)(_ => throw new HttpRequestException("sin red"))
            : new Guion().Con(conectada.Refresco, () => Rechazo((HttpStatusCode)estado)).Responder;

        using (var alcance = ctx.Host.Services.CreateScope())
        {
            var resultado = await Servicio(alcance).RenovarConexionAsync(conectada.Fila.Id, ctx.IdTenant, ahora);
            Assert.Equal(ResultadoDeRenovacion.Fallida, resultado);
        }

        Assert.Equal(1, Llamadas(ctx, conectada.Refresco));
        var trasLaFalla = Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa));
        AfirmarSinCambios(conectada.Fila, trasLaFalla);
        Assert.Equal(ahora, trasLaFalla.FechaUltimoIntentoRenovacion);

        var (acceso2, refresco2) = ($"APP_USR-{Etiqueta()}-nuevo", $"TG-{Etiqueta()}-nuevo");
        ctx.MercadoPago.Responder = new Guion()
            .Con(conectada.Refresco, () => Exitoso(acceso2, refresco2, ctx.MercadoPago.IdUsuario)).Responder;
        using var otraCorrida = ctx.Host.Services.CreateScope();
        Assert.Equal(
            ResultadoDeRenovacion.Renovada,
            await Servicio(otraCorrida).RenovarConexionAsync(conectada.Fila.Id, ctx.IdTenant, ahora));
        AfirmarRenovada(conectada, Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa)), acceso2, refresco2, ahora);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task UnaConexionQueFallaNoImpideRenovarLaDeOtroTenant(HttpStatusCode estadoDeLaQueFalla)
    {
        var a = await _apoyo.PrepararAsync("mp-ren-aislada-a");
        var b = await _apoyo.PrepararAsync("mp-ren-aislada-b");
        var queFalla = await ConectarAsync(a);
        var queAndan = await ConectarAsync(b);
        var (acceso2, refresco2) = ($"APP_USR-{Etiqueta()}-nuevo", $"TG-{Etiqueta()}-nuevo");
        a.MercadoPago.Responder = new Guion()
            .Con(queFalla.Refresco, () => Rechazo(estadoDeLaQueFalla))
            .Con(queAndan.Refresco, () => Exitoso(acceso2, refresco2, b.MercadoPago.IdUsuario)).Responder;
        var ahora = queAndan.Fila.AccessExpiraEn.AddDays(-10);

        using var alcance = a.Host.Services.CreateScope();
        await Servicio(alcance).RenovarConexionesPorVencerAsync(ahora);

        // La de A se procesó primero (menor id) y falló; la de B igual se renovó.
        Assert.True(queFalla.Fila.Id < queAndan.Fila.Id);
        Assert.Equal(1, Llamadas(a, queFalla.Refresco));
        AfirmarRenovada(queAndan, Assert.Single(await _apoyo.FilasAsync(b.IdEmpresa)), acceso2, refresco2, ahora);
        var deA = Assert.Single(await _apoyo.FilasAsync(a.IdEmpresa));
        if (estadoDeLaQueFalla == HttpStatusCode.BadRequest)
        {
            AfirmarRequiereReconexion(deA, ahora);
        }
        else
        {
            AfirmarSinCambios(queFalla.Fila, deA);
        }
    }

    [Fact]
    public async Task SiMercadoPagoDevuelveOtraCuentaLaConexionRequiereReconexionYLosTokensNuevosNoSeGuardan()
    {
        var ctx = await _apoyo.PrepararAsync("mp-ren-otra-cuenta");
        var conectada = await ConectarAsync(ctx);
        var (acceso2, refresco2) = ($"APP_USR-{Etiqueta()}-ajeno", $"TG-{Etiqueta()}-ajeno");
        ctx.MercadoPago.Responder = new Guion()
            .Con(conectada.Refresco, () => Exitoso(acceso2, refresco2, NuevoIdUsuario())).Responder;
        var ahora = conectada.Fila.AccessExpiraEn.AddDays(-10);

        using var alcance = ctx.Host.Services.CreateScope();
        var resultado = await Servicio(alcance).RenovarConexionAsync(conectada.Fila.Id, ctx.IdTenant, ahora);

        Assert.Equal(ResultadoDeRenovacion.RequiereReconexion, resultado);
        var fila = Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa));
        AfirmarRequiereReconexion(fila, ahora);
        Assert.Equal(ctx.MercadoPago.IdUsuario, fila.IdUsuarioMercadoPago);
    }

    // ---- Credenciales de la aplicación y rotaciones perdidas --------------------------------------------------------

    private sealed class RegistroDeLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Nivel, string Categoria, string Texto)> _entradas = [];

        public IReadOnlyList<(LogLevel Nivel, string Categoria, string Texto)> Entradas => [.. _entradas];

        public ILogger CreateLogger(string categoryName) => new Registrador(categoryName, _entradas);

        public void Dispose()
        {
        }

        private sealed class Registrador(
            string categoria, ConcurrentQueue<(LogLevel Nivel, string Categoria, string Texto)> entradas) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entradas.Enqueue((logLevel, categoria, formatter(state, exception) + exception));
        }
    }

    private sealed class InterceptorDeGuardado(Action alGuardar) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            alGuardar();
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static WebApplicationFactory<Program> Derivar(
        Contexto ctx, RegistroDeLogs logs, SaveChangesInterceptor? interceptor = null) =>
        ctx.Host.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services => services.AddLogging(l => l.AddProvider(logs)));
            if (interceptor is not null)
            {
                builder.ConfigureServices(services =>
                    services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor)));
            }
        });

    private static List<string> ErroresDelServicio(RegistroDeLogs logs) =>
        [.. logs.Entradas
            .Where(e => e.Nivel == LogLevel.Error && e.Categoria.Contains(nameof(ServicioDeRenovacionDeConexionesMercadoPago)))
            .Select(e => e.Texto)];

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "invalid_client")]
    [InlineData(HttpStatusCode.Forbidden, "invalid_client")]
    [InlineData(HttpStatusCode.BadRequest, "invalid_client")]
    [InlineData(HttpStatusCode.NotFound, "not_found")]
    public async Task UnRechazoDeLasCredencialesDeLaAplicacionNoTocaNingunaConexionYCortaLaCorrida(HttpStatusCode estado, string error)
    {
        var a = await _apoyo.PrepararAsync("mp-ren-cred-a");
        var b = await _apoyo.PrepararAsync("mp-ren-cred-b");
        var queRechazan = await ConectarAsync(a);
        var queNoSeAlcanzan = await ConectarAsync(b);
        var (acceso2, refresco2) = ($"APP_USR-{Etiqueta()}-nuevo", $"TG-{Etiqueta()}-nuevo");
        a.MercadoPago.Responder = new Guion()
            .Con(queRechazan.Refresco, () => Rechazo(estado, error))
            .Con(queNoSeAlcanzan.Refresco, () => Exitoso(acceso2, refresco2, b.MercadoPago.IdUsuario)).Responder;
        var ahora = queNoSeAlcanzan.Fila.AccessExpiraEn.AddDays(-10);
        var logs = new RegistroDeLogs();

        await using var derivado = Derivar(a, logs);
        using var alcance = derivado.Services.CreateScope();
        var resumen = await Servicio(alcance).RenovarConexionesPorVencerAsync(ahora);

        Assert.True(queRechazan.Fila.Id < queNoSeAlcanzan.Fila.Id);
        Assert.True(resumen.Interrumpida);
        Assert.Equal(1, Llamadas(a, queRechazan.Refresco));
        Assert.Equal(0, Llamadas(a, queNoSeAlcanzan.Refresco));
        AfirmarSinCambios(queRechazan.Fila, Assert.Single(await _apoyo.FilasAsync(a.IdEmpresa)));
        AfirmarSinCambios(queNoSeAlcanzan.Fila, Assert.Single(await _apoyo.FilasAsync(b.IdEmpresa)));
        Assert.Equal(ahora, Assert.Single(await _apoyo.FilasAsync(a.IdEmpresa)).FechaUltimoIntentoRenovacion);
        Assert.Null(Assert.Single(await _apoyo.FilasAsync(b.IdEmpresa)).FechaUltimoIntentoRenovacion);

        var errores = ErroresDelServicio(logs);
        var unico = Assert.Single(errores);
        Assert.DoesNotContain(ClientSecret, unico);
        Assert.DoesNotContain(queRechazan.Refresco, unico);
        Assert.DoesNotContain(queRechazan.Acceso, unico);
    }

    [Fact]
    public async Task SiElGuardadoFallaDespuesDeQueMercadoPagoRotoElTokenLaRotacionSeInformaComoPerdidaYNoComoReintentable()
    {
        var ctx = await _apoyo.PrepararAsync("mp-ren-rotacion-perdida");
        var conectada = await ConectarAsync(ctx);
        var (acceso2, refresco2) = ($"APP_USR-{Etiqueta()}-nuevo", $"TG-{Etiqueta()}-nuevo");
        ctx.MercadoPago.Responder = new Guion()
            .Con(conectada.Refresco, () => Exitoso(acceso2, refresco2, ctx.MercadoPago.IdUsuario)).Responder;
        var ahora = conectada.Fila.AccessExpiraEn.AddDays(-10);
        var logs = new RegistroDeLogs();
        var interceptor = new InterceptorDeGuardado(() => throw new InvalidOperationException("el guardado falló"));

        await using var derivado = Derivar(ctx, logs, interceptor);
        using (var alcance = derivado.Services.CreateScope())
        {
            var resultado = await Servicio(alcance).RenovarConexionAsync(conectada.Fila.Id, ctx.IdTenant, ahora);
            Assert.Equal(ResultadoDeRenovacion.RotacionPerdida, resultado);
        }

        Assert.Equal(1, Llamadas(ctx, conectada.Refresco));
        var trasLaPerdida = Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa));
        AfirmarSinCambios(conectada.Fila, trasLaPerdida);
        Assert.Equal(ahora, trasLaPerdida.FechaUltimoIntentoRenovacion);

        var unico = Assert.Single(ErroresDelServicio(logs));
        Assert.Contains("rotación se perdió", unico);
        Assert.DoesNotContain("se reintenta", unico);
        foreach (var secreto in new[] { acceso2, refresco2, conectada.Acceso, conectada.Refresco, ClientSecret })
        {
            Assert.DoesNotContain(secreto, unico);
        }

        // La corrida lo cuenta aparte de las fallas reintentables.
        using var otraCorrida = derivado.Services.CreateScope();
        var resumen = await Servicio(otraCorrida).RenovarConexionesPorVencerAsync(ahora);
        Assert.Equal(1, resumen.RotacionesPerdidas);
        Assert.False(resumen.Interrumpida);
    }

    [Fact]
    public async Task UnApagadoDespuesDeQueMercadoPagoRotoElTokenNoAbortaElGuardadoDeLaRotacion()
    {
        var ctx = await _apoyo.PrepararAsync("mp-ren-apagado");
        var conectada = await ConectarAsync(ctx);
        var (acceso2, refresco2) = ($"APP_USR-{Etiqueta()}-nuevo", $"TG-{Etiqueta()}-nuevo");
        ctx.MercadoPago.Responder = new Guion()
            .Con(conectada.Refresco, () => Exitoso(acceso2, refresco2, ctx.MercadoPago.IdUsuario)).Responder;
        var ahora = conectada.Fila.AccessExpiraEn.AddDays(-10);
        using var apagado = new CancellationTokenSource();
        var interceptor = new InterceptorDeGuardado(apagado.Cancel);

        await using var derivado = Derivar(ctx, new RegistroDeLogs(), interceptor);
        using var alcance = derivado.Services.CreateScope();
        var resultado = await Servicio(alcance).RenovarConexionAsync(conectada.Fila.Id, ctx.IdTenant, ahora, apagado.Token);

        Assert.True(apagado.IsCancellationRequested);
        Assert.Equal(ResultadoDeRenovacion.Renovada, resultado);
        AfirmarRenovada(conectada, Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa)), acceso2, refresco2, ahora);
    }

    // ---- Carreras entre la selección y el lock ----------------------------------------------------------------------

    private async Task<(ResultadoDeRenovacion Resultado, Conectada Conectada)> RenovarMientrasOtroEscribeAsync(
        string nombre, Func<Conectada, Task> escritorEnPugna)
    {
        var ctx = await _apoyo.PrepararAsync(nombre);
        var conectada = await ConectarAsync(ctx);
        ctx.MercadoPago.Responder = new Guion()
            .Con(conectada.Refresco, () => Exitoso("APP_USR-no-debe-usarse", "TG-no-debe-usarse", ctx.MercadoPago.IdUsuario))
            .Responder;

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);
        await using var perdedora = ctx.Host.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var alcance = perdedora.Services.CreateScope();
        var ahora = conectada.Fila.AccessExpiraEn.AddDays(-10);
        var renovacion = Task.Run(() => Servicio(alcance).RenovarConexionAsync(conectada.Fila.Id, ctx.IdTenant, ahora));

        await transaccionIniciada.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await escritorEnPugna(conectada);
        puedeContinuar.SetResult();

        return (await renovacion.WaitAsync(TimeSpan.FromSeconds(30)), conectada);
    }

    [Fact]
    public async Task UnaConexionDesconectadaEntreLaSeleccionYElLockSeOmiteSinLlamarAMercadoPago()
    {
        var (resultado, conectada) = await RenovarMientrasOtroEscribeAsync(
            "mp-ren-desconectada",
            c => EjecutarSqlAsync(
                "UPDATE conexiones_mercadopago SET estado = 'desconectada', fecha_desconexion = now(), " +
                "access_token_cifrado = NULL, access_token_nonce = NULL, access_token_tag = NULL, " +
                "refresh_token_cifrado = NULL, refresh_token_nonce = NULL, refresh_token_tag = NULL " +
                "WHERE id_conexion_mercadopago = $1",
                c.Fila.Id));

        Assert.Equal(ResultadoDeRenovacion.Omitida, resultado);
        Assert.Equal(0, Llamadas(conectada.Ctx, conectada.Refresco));
        var fila = Assert.Single(await _apoyo.FilasAsync(conectada.Ctx.IdEmpresa));
        Assert.Equal("desconectada", fila.Estado);
        Assert.Null(fila.AccessCifrado);
        Assert.Null(fila.FechaUltimaRenovacion);
    }

    [Fact]
    public async Task UnaConexionYaRenovadaPorOtroProcesoEntreLaSeleccionYElLockSeOmite()
    {
        var (resultado, conectada) = await RenovarMientrasOtroEscribeAsync(
            "mp-ren-ya-renovada",
            c => EjecutarSqlAsync(
                "UPDATE conexiones_mercadopago SET access_token_expira_en = access_token_expira_en + interval '150 days' " +
                "WHERE id_conexion_mercadopago = $1",
                c.Fila.Id));

        Assert.Equal(ResultadoDeRenovacion.Omitida, resultado);
        Assert.Equal(0, Llamadas(conectada.Ctx, conectada.Refresco));
        var fila = Assert.Single(await _apoyo.FilasAsync(conectada.Ctx.IdEmpresa));
        Assert.Equal(conectada.Fila.AccessExpiraEn.AddDays(150), fila.AccessExpiraEn);
        Assert.Equal(conectada.Fila.AccessCifrado, fila.AccessCifrado);
    }

    // ---- Job de fondo -----------------------------------------------------------------------------------------------

    private sealed class FabricaDeAlcancesProhibida : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("El job desactivado no debe abrir un alcance.");
    }

    private static RenovacionDeConexionesMercadoPago Job(OpcionesDeMercadoPago opciones) =>
        new(
            new FabricaDeAlcancesProhibida(),
            Options.Create(opciones),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RenovacionDeConexionesMercadoPago>.Instance);

    private static OpcionesDeMercadoPago Configuradas(bool renovacionAutomatica = true) => new()
    {
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        RedirectUri = RedirectUri,
        RenovacionAutomatica = renovacionAutomatica
    };

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SinCredencialesOConLaRenovacionApagadaElJobTerminaAlArrancarSinHacerNada(bool configurado, bool automatica)
    {
        var job = Job(configurado ? Configuradas(automatica) : new OpcionesDeMercadoPago { RenovacionAutomatica = automatica });

        Assert.False(job.Habilitada);
        await job.StartAsync(CancellationToken.None);
        await job.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void ConCredencialesYLaRenovacionPrendidaElJobQuedaHabilitado()
    {
        Assert.True(Job(Configuradas()).Habilitada);
    }

    [Fact]
    public async Task ElHostDePruebasNoCorreElJobDeRenovacion()
    {
        var ctx = await _apoyo.PrepararAsync("mp-ren-host-sin-job");

        var job = Assert.Single(ctx.Host.Services.GetServices<IHostedService>().OfType<RenovacionDeConexionesMercadoPago>());

        Assert.False(job.Habilitada);
    }
}
