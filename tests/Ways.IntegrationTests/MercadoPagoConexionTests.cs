using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.MercadoPago;
using Ways.Domain.MercadoPago;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.MercadoPago;
using static Ways.IntegrationTests.ApoyoDeMercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// Conexión de una empresa con Mercado Pago por OAuth punta a punta contra Postgres real: inicio (solo Admin, 404 antes que nada,
/// PKCE y state), callback anónimo que solo redirige con el fragmento, completar autenticado (tokens cifrados, state ajeno, cuenta ya usada), lectura que nunca
/// expone tokens y desconexión que borra los blobs. El endpoint de tokens de Mercado Pago es un manejador falso.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MercadoPagoConexionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeMercadoPago _apoyo = new(fixture);

    // ---- Inicio -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ElInicioDevuelveLaUrlDeAutorizacionConPkceYUnStateQueAtaTenantEmpresaCuentaYUsuario()
    {
        var ctx = await _apoyo.PrepararAsync("mp-inicio");

        var (url, estado) = await IniciarAsync(ctx);

        var uri = new Uri(url);
        Assert.Equal(UrlAutorizacion, $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}");
        var consulta = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal(ClientId, consulta["client_id"]);
        Assert.Equal("code", consulta["response_type"]);
        Assert.Equal("mp", consulta["platform_id"]);
        Assert.Equal(RedirectUri, consulta["redirect_uri"]);
        Assert.Equal("S256", consulta["code_challenge_method"]);
        Assert.DoesNotContain(ClientSecret, url);

        var protector = ctx.Host.Services.GetRequiredService<IProtectorDeEstadoOAuth>();
        var contenido = protector.Desproteger(estado)!;
        Assert.Equal(ctx.IdTenant, contenido.IdTenant);
        Assert.Equal(ctx.IdEmpresa, contenido.IdEmpresa);
        Assert.Equal(ctx.IdCuenta, contenido.IdCuentaTesoreria);
        Assert.Equal(ctx.IdUsuarioAdmin, contenido.IdUsuario);
        Assert.Equal("http://localhost", contenido.Origen);
        Assert.Equal(Pkce.DesafioS256(contenido.CodeVerifier), consulta["code_challenge"]);
    }

    [Fact]
    public async Task ElInicioEsSoloDeAdminYExigeSesion()
    {
        var ctx = await _apoyo.PrepararAsync("mp-inicio-roles");
        var cuerpo = new SolicitudDeInicioDeConexion(ctx.IdCuenta);

        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Supervisor.PostAsJsonAsync($"{ctx.Ruta}/inicio", cuerpo)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.PostAsJsonAsync($"{ctx.Ruta}/inicio", cuerpo)).StatusCode);

        using var anonimo = ctx.Host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsJsonAsync($"{ctx.Ruta}/inicio", cuerpo)).StatusCode);
    }

    [Fact]
    public async Task UnaEmpresaInexistenteOAjenaOdadaDeBajaDa404AunConUnCuerpoInvalidoYAunSinConfiguracion()
    {
        var ctx = await _apoyo.PrepararAsync("mp-inicio-404");
        var otro = await _apoyo.PrepararAsync("mp-inicio-404-otro");
        var (idBaja, idCuentaBaja) = await _apoyo.CrearOtraEmpresaConCuentaAsync(ctx, "Empresa dada de baja");
        await using (var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null))
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "UPDATE empresas SET deleted_at = now() WHERE id_empresa = $1";
            comando.Parameters.Add(new Npgsql.NpgsqlParameter { Value = idBaja });
            await comando.ExecuteNonQueryAsync();
        }

        var sinConfigurar = await _apoyo.PrepararAsync("mp-inicio-404-sin-config", configurado: false);

        // Cuerpo con una cuenta que no existe (0): si se validara antes que la empresa, daría 400.
        var cuerpoInvalido = new SolicitudDeInicioDeConexion(0);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ctx.Admin.PostAsJsonAsync("/api/empresas/987654/mercadopago/conexion/inicio", cuerpoInvalido)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ctx.Admin.PostAsJsonAsync($"/api/empresas/{otro.IdEmpresa}/mercadopago/conexion/inicio", cuerpoInvalido)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ctx.Admin.PostAsJsonAsync($"/api/empresas/{otro.IdEmpresa}/mercadopago/conexion/inicio",
                new SolicitudDeInicioDeConexion(otro.IdCuenta))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ctx.Admin.PostAsJsonAsync($"/api/empresas/{idBaja}/mercadopago/conexion/inicio",
                new SolicitudDeInicioDeConexion(idCuentaBaja))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await sinConfigurar.Admin.PostAsJsonAsync("/api/empresas/987654/mercadopago/conexion/inicio", cuerpoInvalido)).StatusCode);
    }

    [Fact]
    public async Task SinCredencialesConfiguradasElInicioResponde503MercadoPagoNoConfigurado()
    {
        var ctx = await _apoyo.PrepararAsync("mp-inicio-sin-config", configurado: false);

        var respuesta = await ctx.Admin.PostAsJsonAsync($"{ctx.Ruta}/inicio", new SolicitudDeInicioDeConexion(ctx.IdCuenta));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, respuesta.StatusCode);
        Assert.Equal("mercadopago_no_configurado", await CodigoAsync(respuesta));
    }

    [Fact]
    public async Task ElInicioRechazaUnaCuentaInexistenteDeOtraEmpresaOInactiva()
    {
        var ctx = await _apoyo.PrepararAsync("mp-inicio-cuenta");
        var (_, cuentaDeOtraEmpresa) = await _apoyo.CrearOtraEmpresaConCuentaAsync(ctx, "Otra empresa");
        var inactiva = await CrearCuentaAsync(ctx.Admin, ctx.IdEmpresa, "Inactiva", activo: false);
        var otroTenant = await _apoyo.PrepararAsync("mp-inicio-cuenta-ajena");

        var inexistente = await ctx.Admin.PostAsJsonAsync($"{ctx.Ruta}/inicio", new SolicitudDeInicioDeConexion(987654));
        Assert.Equal(HttpStatusCode.BadRequest, inexistente.StatusCode);
        Assert.Equal("referencia_invalida", await CodigoAsync(inexistente));

        var ajena = await ctx.Admin.PostAsJsonAsync($"{ctx.Ruta}/inicio", new SolicitudDeInicioDeConexion(otroTenant.IdCuenta));
        Assert.Equal(HttpStatusCode.BadRequest, ajena.StatusCode);
        Assert.Equal("referencia_invalida", await CodigoAsync(ajena));

        var deOtraEmpresa = await ctx.Admin.PostAsJsonAsync($"{ctx.Ruta}/inicio", new SolicitudDeInicioDeConexion(cuentaDeOtraEmpresa));
        Assert.Equal(HttpStatusCode.Conflict, deOtraEmpresa.StatusCode);
        Assert.Equal("cuenta_tesoreria_de_otra_empresa", await CodigoAsync(deOtraEmpresa));

        var deInactiva = await ctx.Admin.PostAsJsonAsync($"{ctx.Ruta}/inicio", new SolicitudDeInicioDeConexion(inactiva));
        Assert.Equal(HttpStatusCode.Conflict, deInactiva.StatusCode);
        Assert.Equal("cuenta_tesoreria_inactiva", await CodigoAsync(deInactiva));
    }

    [Fact]
    public async Task ConUnaConexionActivaElInicioResponde409YDesconectarPermiteVolverAConectar()
    {
        var ctx = await _apoyo.PrepararAsync("mp-inicio-ya-conectada");
        var (_, estado) = await IniciarAsync(ctx);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(ctx, estado)).StatusCode);

        var repetido = await ctx.Admin.PostAsJsonAsync($"{ctx.Ruta}/inicio", new SolicitudDeInicioDeConexion(ctx.IdCuenta));
        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);
        Assert.Equal("mercadopago_ya_conectada", await CodigoAsync(repetido));

        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync(ctx.Ruta)).StatusCode);

        ctx.MercadoPago.Responder = _ => Exitoso("APP_USR-segundo-acceso", "TG-segundo-refresco", NuevoIdUsuario());
        var (_, nuevoEstado) = await IniciarAsync(ctx);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(ctx, nuevoEstado)).StatusCode);
        var filas = await _apoyo.FilasAsync(ctx.IdEmpresa);
        Assert.Equal(["desconectada", "activa"], filas.Select(f => f.Estado));
    }

    // ---- Callback (solo redirige) -----------------------------------------------------------------------------------

    [Fact]
    public async Task ElCallbackSoloRedirigeConElCodeYElStateEnElFragmentoSinCanjearNiGuardarNada()
    {
        var ctx = await _apoyo.PrepararAsync("mp-callback");
        using var anonimo = ClienteAnonimoSinRedirecciones(ctx.Host);
        var (_, estado) = await IniciarAsync(ctx);

        var respuesta = await LlamarAlCallbackAsync(anonimo, estado, codigo: "TG-codigo+especial/1");

        var destino = Ubicacion(respuesta);
        Assert.StartsWith($"http://localhost/organizacion/empresas#mercadopago=completar&empresa={ctx.IdEmpresa}&", destino);
        Assert.DoesNotContain('?', destino);
        var fragmento = Fragmento(destino);
        Assert.Equal("TG-codigo+especial/1", fragmento["code"]);
        Assert.Equal(estado, fragmento["state"]);
        Assert.Empty(ctx.MercadoPago.Cuerpos);
        Assert.Empty(await _apoyo.FilasAsync(ctx.IdEmpresa));
    }

    [Fact]
    public async Task SiElUsuarioDeniegaElAccesoElCallbackVuelveCanceladoSinLlamarAMercadoPagoNiGuardarNada()
    {
        var ctx = await _apoyo.PrepararAsync("mp-denegado");
        using var anonimo = ClienteAnonimoSinRedirecciones(ctx.Host);
        var (_, estado) = await IniciarAsync(ctx);

        var respuesta = await LlamarAlCallbackAsync(anonimo, estado, codigo: null, error: "access_denied");

        Assert.Equal($"http://localhost/organizacion/empresas#mercadopago=cancelado&empresa={ctx.IdEmpresa}", Ubicacion(respuesta));
        Assert.Empty(ctx.MercadoPago.Cuerpos);
        Assert.Empty(await _apoyo.FilasAsync(ctx.IdEmpresa));
    }

    [Fact]
    public async Task ElCallbackConUnStateInvalidoAlteradoOVencidoOSinCodeNiErrorDa400SinLlamarAMercadoPago()
    {
        var reloj = new RelojControlable(DateTimeOffset.UtcNow);
        var ctx = await _apoyo.PrepararAsync("mp-state-invalido", reloj: reloj);
        using var anonimo = ClienteAnonimoSinRedirecciones(ctx.Host);
        var (_, estado) = await IniciarAsync(ctx);

        Assert.Equal(HttpStatusCode.BadRequest, (await LlamarAlCallbackAsync(anonimo, "basura")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await LlamarAlCallbackAsync(anonimo, Alterar(estado))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonimo.GetAsync(RutaCallback)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await LlamarAlCallbackAsync(anonimo, estado, codigo: null)).StatusCode);

        reloj.Ahora = reloj.Ahora.AddMinutes(11);
        Assert.Equal(HttpStatusCode.BadRequest, (await LlamarAlCallbackAsync(anonimo, estado)).StatusCode);

        Assert.Empty(ctx.MercadoPago.Cuerpos);
        Assert.Empty(await _apoyo.FilasAsync(ctx.IdEmpresa));
    }

    private static string Alterar(string estado)
    {
        var medio = estado.Length / 2;
        return estado[..medio] + (estado[medio] == 'A' ? 'B' : 'A') + estado[(medio + 1)..];
    }

    // ---- Completar (dentro de la sesión) ----------------------------------------------------------------------------

    [Fact]
    public async Task CompletarCanjeaElCodigoConElVerificadorGuardaLosTokensCifradosYDevuelveLaConexion()
    {
        var ctx = await _apoyo.PrepararAsync("mp-completar");
        using var anonimo = ClienteAnonimoSinRedirecciones(ctx.Host);
        var (_, estado) = await IniciarAsync(ctx);
        var verificador = ctx.Host.Services.GetRequiredService<IProtectorDeEstadoOAuth>().Desproteger(estado)!.CodeVerifier;

        // El recorrido completo: el callback arma el fragmento y la web postea lo que lee de él.
        var fragmento = Fragmento(Ubicacion(await LlamarAlCallbackAsync(anonimo, estado, codigo: "TG-codigo-especial")));
        var respuesta = await CompletarAsync(ctx, fragmento["state"]!, fragmento["code"]!);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var conexion = (await respuesta.Content.ReadFromJsonAsync<EstadoDeConexionMercadoPago>())!;
        Assert.True(conexion.Conectada);
        Assert.Equal("activa", conexion.Estado);
        Assert.Equal(ctx.IdCuenta, conexion.IdCuentaTesoreria);

        var pedido = Assert.Single(ctx.MercadoPago.Cuerpos);
        Assert.StartsWith("POST https://api.mp.test/oauth/token ", pedido);
        using var json = JsonDocument.Parse(pedido[(pedido.IndexOf('{'))..]);
        Assert.Equal("authorization_code", json.RootElement.GetProperty("grant_type").GetString());
        Assert.Equal("TG-codigo-especial", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(verificador, json.RootElement.GetProperty("code_verifier").GetString());
        Assert.Equal(ClientId, json.RootElement.GetProperty("client_id").GetString());
        Assert.Equal(RedirectUri, json.RootElement.GetProperty("redirect_uri").GetString());

        var fila = Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa));
        Assert.Equal(ctx.IdTenant, fila.IdTenant);
        Assert.Equal(ctx.IdCuenta, fila.IdCuenta);
        Assert.Equal(ctx.MercadoPago.IdUsuario, fila.IdUsuarioMercadoPago);
        Assert.Equal("activa", fila.Estado);
        Assert.Equal(ctx.IdUsuarioAdmin, fila.IdUsuarioConecto);
        Assert.Equal("t1", fila.IdClaveMaestra);
        Assert.Equal(TimeSpan.FromSeconds(15_552_000), fila.AccessExpiraEn - fila.FechaConexion);
        Assert.Equal(12, fila.AccessNonce!.Length);
        Assert.Equal(16, fila.RefreshTag!.Length);

        // Ni el texto plano de los tokens ni el del secreto de la aplicación aparecen en ningún byte de la fila.
        byte[] todo = [
            .. fila.AccessCifrado!, .. fila.AccessNonce, .. fila.AccessTag!,
            .. fila.RefreshCifrado!, .. fila.RefreshNonce!, .. fila.RefreshTag,
            .. Encoding.UTF8.GetBytes(fila.IdClaveMaestra)];
        Assert.False(todo.AsSpan().IndexOf(Encoding.UTF8.GetBytes(AccessToken)) >= 0);
        Assert.False(todo.AsSpan().IndexOf(Encoding.UTF8.GetBytes(RefreshToken)) >= 0);
        Assert.False(todo.AsSpan().IndexOf(Encoding.UTF8.GetBytes(ClientSecret)) >= 0);

        // Los blobs descifran, con el AAD de SU conexión, a lo que devolvió Mercado Pago.
        var almacen = ctx.Host.Services.GetRequiredService<IAlmacenDeTokensMercadoPago>();
        var descifrados = almacen.Descifrar(new ConexionMercadoPago
        {
            Id = fila.Id,
            IdTenant = fila.IdTenant,
            IdEmpresa = fila.IdEmpresa,
            IdClaveMaestra = fila.IdClaveMaestra,
            AccessTokenCifrado = fila.AccessCifrado,
            AccessTokenNonce = fila.AccessNonce,
            AccessTokenTag = fila.AccessTag,
            RefreshTokenCifrado = fila.RefreshCifrado,
            RefreshTokenNonce = fila.RefreshNonce,
            RefreshTokenTag = fila.RefreshTag
        });
        Assert.Equal((AccessToken, RefreshToken), descifrados);
    }

    [Fact]
    public async Task UnStateIniciadoPorOtroUsuarioDelMismoTenantDa403SinLlamarAMercadoPagoNiGuardarNada()
    {
        var ctx = await _apoyo.PrepararAsync("mp-completar-otro-usuario");
        // Un Admin de tenant no puede dar de alta a otro Admin: el state lo arma el protector con el mismo tenant y la misma
        // empresa de la sesión, pero con el id de otro usuario (el que inició el flujo y le pasó el enlace a este Admin).
        var estado = ctx.Host.Services.GetRequiredService<IProtectorDeEstadoOAuth>().Proteger(new EstadoOAuth(
            ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, ctx.IdUsuarioAdmin + 1, "verificador", "http://localhost"));

        var respuesta = await CompletarAsync(ctx, estado);

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
        Assert.Equal("mercadopago_estado_ajeno", await CodigoAsync(respuesta));
        Assert.Empty(ctx.MercadoPago.Cuerpos);
        Assert.Empty(await _apoyo.FilasAsync(ctx.IdEmpresa));
    }

    [Fact]
    public async Task UnStateDeOtroTenantDa403ParaLaEmpresaPropiaY404ParaLaAjenaSinLlamarAMercadoPago()
    {
        var ctx = await _apoyo.PrepararAsync("mp-completar-tenant-a");
        var otro = await _apoyo.PrepararAsync("mp-completar-tenant-b");
        var (_, estadoAjeno) = await IniciarAsync(otro);

        var propia = await CompletarAsync(ctx, estadoAjeno);
        Assert.Equal(HttpStatusCode.Forbidden, propia.StatusCode);
        Assert.Equal("mercadopago_estado_ajeno", await CodigoAsync(propia));

        var rutaAjena = await ctx.Admin.PostAsJsonAsync(
            $"{otro.Ruta}/completar", new SolicitudDeCompletarConexion("TG-codigo", estadoAjeno));
        Assert.Equal(HttpStatusCode.NotFound, rutaAjena.StatusCode);

        Assert.Empty(ctx.MercadoPago.Cuerpos);
        Assert.Empty(otro.MercadoPago.Cuerpos);
        Assert.Empty(await _apoyo.FilasAsync(ctx.IdEmpresa));
        Assert.Empty(await _apoyo.FilasAsync(otro.IdEmpresa));
    }

    [Fact]
    public async Task UnStateDeOtraEmpresaDelMismoTenantDa403SinLlamarAMercadoPago()
    {
        var ctx = await _apoyo.PrepararAsync("mp-completar-otra-empresa");
        var (idOtraEmpresa, _) = await _apoyo.CrearOtraEmpresaConCuentaAsync(ctx, "Otra empresa");
        var (_, estado) = await IniciarAsync(ctx);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/empresas/{idOtraEmpresa}/mercadopago/conexion/completar",
            new SolicitudDeCompletarConexion("TG-codigo", estado));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
        Assert.Equal("mercadopago_estado_ajeno", await CodigoAsync(respuesta));
        Assert.Empty(ctx.MercadoPago.Cuerpos);
        Assert.Empty(await _apoyo.FilasAsync(idOtraEmpresa));
    }

    [Fact]
    public async Task CompletarConUnStateAlteradoOVencidoDa400SinLlamarAMercadoPago()
    {
        var reloj = new RelojControlable(DateTimeOffset.UtcNow);
        var ctx = await _apoyo.PrepararAsync("mp-completar-invalido", reloj: reloj);
        var (_, estado) = await IniciarAsync(ctx);

        foreach (var invalido in new[] { "basura", Alterar(estado) })
        {
            var respuesta = await CompletarAsync(ctx, invalido);
            Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
            Assert.Equal("mercadopago_estado_invalido", await CodigoAsync(respuesta));
        }

        reloj.Ahora = reloj.Ahora.AddMinutes(11);
        var vencido = await CompletarAsync(ctx, estado);
        Assert.Equal(HttpStatusCode.BadRequest, vencido.StatusCode);
        Assert.Equal("mercadopago_estado_invalido", await CodigoAsync(vencido));

        Assert.Empty(ctx.MercadoPago.Cuerpos);
        Assert.Empty(await _apoyo.FilasAsync(ctx.IdEmpresa));
    }

    [Fact]
    public async Task CompletarExigeCodeYStateNoVaciosYAcotadosPeroUnaEmpresaInexistenteDa404Primero()
    {
        var ctx = await _apoyo.PrepararAsync("mp-completar-cuerpo");
        var (_, estado) = await IniciarAsync(ctx);

        foreach (var cuerpo in new[]
        {
            new SolicitudDeCompletarConexion("", estado),
            new SolicitudDeCompletarConexion("  ", estado),
            new SolicitudDeCompletarConexion("TG-codigo", ""),
            new SolicitudDeCompletarConexion(new string('a', 513), estado),
            new SolicitudDeCompletarConexion("TG-codigo", new string('a', 2049))
        })
        {
            var respuesta = await ctx.Admin.PostAsJsonAsync($"{ctx.Ruta}/completar", cuerpo);
            Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
            Assert.Equal("solicitud_invalida", await CodigoAsync(respuesta));
        }

        var sinCampos = await ctx.Admin.PostAsJsonAsync($"{ctx.Ruta}/completar", new { });
        Assert.Equal(HttpStatusCode.BadRequest, sinCampos.StatusCode);

        var inexistente = await ctx.Admin.PostAsJsonAsync(
            "/api/empresas/987654/mercadopago/conexion/completar", new SolicitudDeCompletarConexion("", ""));
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
        Assert.Empty(ctx.MercadoPago.Cuerpos);
    }

    [Fact]
    public async Task CompletarEsSoloDeAdminYExigeSesion()
    {
        var ctx = await _apoyo.PrepararAsync("mp-completar-roles");
        var (_, estado) = await IniciarAsync(ctx);

        Assert.Equal(HttpStatusCode.Forbidden, (await CompletarAsync(ctx, estado, cliente: ctx.Supervisor)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CompletarAsync(ctx, estado, cliente: ctx.Vendedor)).StatusCode);
        using var anonimo = ctx.Host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await CompletarAsync(ctx, estado, cliente: anonimo)).StatusCode);
        Assert.Empty(ctx.MercadoPago.Cuerpos);
    }

    [Fact]
    public async Task SinCredencialesConfiguradasCompletarResponde503SinLlamarAMercadoPago()
    {
        var ctx = await _apoyo.PrepararAsync("mp-completar-sin-config", configurado: false);
        var estado = ctx.Host.Services.GetRequiredService<IProtectorDeEstadoOAuth>().Proteger(
            new EstadoOAuth(ctx.IdTenant, ctx.IdEmpresa, ctx.IdCuenta, ctx.IdUsuarioAdmin, "verificador", "http://localhost"));

        var respuesta = await CompletarAsync(ctx, estado);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, respuesta.StatusCode);
        Assert.Equal("mercadopago_no_configurado", await CodigoAsync(respuesta));
        Assert.Empty(ctx.MercadoPago.Cuerpos);
    }

    [Fact]
    public async Task SiMercadoPagoRechazaElCanjeCompletarResponde502YNoGuardaNada()
    {
        var ctx = await _apoyo.PrepararAsync("mp-canje-falla");
        ctx.MercadoPago.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent($$"""{"message":"invalid_grant","detalle":"{{ClientSecret}}"}""")
        };
        var (_, estado) = await IniciarAsync(ctx);

        var respuesta = await CompletarAsync(ctx, estado);

        Assert.Equal(HttpStatusCode.BadGateway, respuesta.StatusCode);
        Assert.Equal("mercadopago_intercambio_fallido", await CodigoAsync(respuesta));
        Assert.DoesNotContain(ClientSecret, await respuesta.Content.ReadAsStringAsync());
        Assert.Empty(await _apoyo.FilasAsync(ctx.IdEmpresa));
    }

    [Fact]
    public async Task UnaCuentaDeMercadoPagoYaConectadaEnOtroTenantDa409CuentaEnUsoSinSegundaFila()
    {
        var uno = await _apoyo.PrepararAsync("mp-en-uso-uno");
        var dos = await _apoyo.PrepararAsync("mp-en-uso-dos");

        var (_, estadoUno) = await IniciarAsync(uno);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(uno, estadoUno)).StatusCode);

        dos.MercadoPago.Responder = _ => Exitoso(AccessToken, RefreshToken, uno.MercadoPago.IdUsuario);
        var (_, estadoDos) = await IniciarAsync(dos);
        var respuesta = await CompletarAsync(dos, estadoDos);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("mercadopago_cuenta_en_uso", await CodigoAsync(respuesta));
        Assert.Empty(await _apoyo.FilasAsync(dos.IdEmpresa));
        Assert.Single(await _apoyo.FilasAsync(uno.IdEmpresa));
    }

    [Fact]
    public async Task SiLaEmpresaYaTieneConexionActivaCompletarDa409YaConectadaSinQuemarElCodigo()
    {
        var ctx = await _apoyo.PrepararAsync("mp-ya-conectada-completar");
        var (_, primero) = await IniciarAsync(ctx);
        var (_, segundo) = await IniciarAsync(ctx);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(ctx, primero)).StatusCode);
        var pedidosAntes = ctx.MercadoPago.Cuerpos.Count;

        var respuesta = await CompletarAsync(ctx, segundo);

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("mercadopago_ya_conectada", await CodigoAsync(respuesta));
        Assert.Equal(pedidosAntes, ctx.MercadoPago.Cuerpos.Count);
        Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa));
    }

    [Fact]
    public async Task SiLaCuentaDeTesoreriaSeDesactivaAntesDeCompletarResponde502SinCanjear()
    {
        var ctx = await _apoyo.PrepararAsync("mp-cuenta-desactivada");
        var (_, estado) = await IniciarAsync(ctx);
        var desactivar = await ctx.Admin.PutAsJsonAsync(
            $"/api/cuentas-tesoreria/{ctx.IdCuenta}", new Ways.Application.Caja.EdicionDeCuentaTesoreria("Mercado Pago", 0m, false));
        Assert.Equal(HttpStatusCode.OK, desactivar.StatusCode);

        var respuesta = await CompletarAsync(ctx, estado);

        Assert.Equal(HttpStatusCode.BadGateway, respuesta.StatusCode);
        Assert.Empty(ctx.MercadoPago.Cuerpos);
    }

    // ---- Lectura ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LaLecturaSinConexionDiceNoConectada()
    {
        var ctx = await _apoyo.PrepararAsync("mp-lectura-vacia");

        var estado = await ctx.Admin.GetFromJsonAsync<EstadoDeConexionMercadoPago>(ctx.Ruta);

        Assert.Equal(new EstadoDeConexionMercadoPago(false, null, null, null, null, null, null), estado);
    }

    [Fact]
    public async Task LaLecturaDeUnaConexionActivaMuestraCuentaYVencimientoPeroNuncaTokens()
    {
        var ctx = await _apoyo.PrepararAsync("mp-lectura");
        var (_, estado) = await IniciarAsync(ctx);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(ctx, estado)).StatusCode);
        var fila = Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa));

        var respuesta = await ctx.Admin.GetAsync(ctx.Ruta);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.DoesNotContain(AccessToken, texto);
        Assert.DoesNotContain(RefreshToken, texto);
        using var json = JsonDocument.Parse(texto);
        Assert.Equal(
            ["accessTokenExpiraEn", "conectada", "estado", "fechaConexion", "idCuentaTesoreria", "idUsuarioMercadoPago", "nombreCuenta"],
            json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        var leido = JsonSerializer.Deserialize<EstadoDeConexionMercadoPago>(texto, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(leido.Conectada);
        Assert.Equal("activa", leido.Estado);
        Assert.Equal(ctx.IdCuenta, leido.IdCuentaTesoreria);
        Assert.Equal("Mercado Pago", leido.NombreCuenta);
        Assert.Equal(ctx.MercadoPago.IdUsuario, leido.IdUsuarioMercadoPago);
        Assert.Equal(fila.FechaConexion, leido.FechaConexion);
        Assert.Equal(fila.AccessExpiraEn, leido.AccessTokenExpiraEn);
    }

    [Fact]
    public async Task LaLecturaYLaDesconexionSonSoloDeAdmin()
    {
        var ctx = await _apoyo.PrepararAsync("mp-roles");

        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Supervisor.GetAsync(ctx.Ruta)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.GetAsync(ctx.Ruta)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Supervisor.DeleteAsync(ctx.Ruta)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.DeleteAsync(ctx.Ruta)).StatusCode);

        using var anonimo = ctx.Host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync(ctx.Ruta)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.DeleteAsync(ctx.Ruta)).StatusCode);
    }

    [Fact]
    public async Task LaEmpresaDeOtroTenantDa404EnLecturaYDesconexionYNoSeVeSuConexion()
    {
        var ctx = await _apoyo.PrepararAsync("mp-aislamiento-a");
        var otro = await _apoyo.PrepararAsync("mp-aislamiento-b");
        otro.MercadoPago.Responder = _ => Exitoso("APP_USR-ajeno", "TG-ajeno", NuevoIdUsuario());
        var (_, estado) = await IniciarAsync(otro);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(otro, estado)).StatusCode);

        var ruta = $"/api/empresas/{otro.IdEmpresa}/mercadopago/conexion";
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.GetAsync(ruta)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.DeleteAsync(ruta)).StatusCode);
        Assert.Equal("activa", Assert.Single(await _apoyo.FilasAsync(otro.IdEmpresa)).Estado);
    }

    // ---- Desconexión ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DesconectarBorraLosBlobsMarcaLaFechaYUnaSegundaVezDa404()
    {
        var ctx = await _apoyo.PrepararAsync("mp-desconexion");
        var (_, estado) = await IniciarAsync(ctx);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(ctx, estado)).StatusCode);

        var respuesta = await ctx.Admin.DeleteAsync(ctx.Ruta);

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);
        var fila = Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa));
        Assert.Equal("desconectada", fila.Estado);
        Assert.NotNull(fila.FechaDesconexion);
        Assert.All(
            new[] { fila.AccessCifrado, fila.AccessNonce, fila.AccessTag, fila.RefreshCifrado, fila.RefreshNonce, fila.RefreshTag },
            blob => Assert.Null(blob));
        Assert.False((await ctx.Admin.GetFromJsonAsync<EstadoDeConexionMercadoPago>(ctx.Ruta))!.Conectada);

        var otra = await ctx.Admin.DeleteAsync(ctx.Ruta);
        Assert.Equal(HttpStatusCode.NotFound, otra.StatusCode);
        Assert.Equal(fila.FechaDesconexion, Assert.Single(await _apoyo.FilasAsync(ctx.IdEmpresa)).FechaDesconexion);
    }

    [Fact]
    public async Task LaCuentaDeTesoreriaConUnaConexionNoSeDaDeBajaNiActivaNiDesconectada()
    {
        var ctx = await _apoyo.PrepararAsync("mp-baja-cuenta");
        var (_, estado) = await IniciarAsync(ctx);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(ctx, estado)).StatusCode);

        var activa = await ctx.Admin.DeleteAsync($"/api/cuentas-tesoreria/{ctx.IdCuenta}");
        Assert.Equal(HttpStatusCode.Conflict, activa.StatusCode);
        Assert.Equal("cuenta_tesoreria_en_uso", await CodigoAsync(activa));

        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync(ctx.Ruta)).StatusCode);

        var desconectada = await ctx.Admin.DeleteAsync($"/api/cuentas-tesoreria/{ctx.IdCuenta}");
        Assert.Equal(HttpStatusCode.Conflict, desconectada.StatusCode);
        Assert.Equal("cuenta_tesoreria_en_uso", await CodigoAsync(desconectada));
    }

    [Fact]
    public async Task DesconectarSinConexionActivaDa404()
    {
        var ctx = await _apoyo.PrepararAsync("mp-desconexion-vacia");

        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.DeleteAsync(ctx.Ruta)).StatusCode);
    }
}
