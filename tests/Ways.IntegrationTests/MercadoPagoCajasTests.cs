using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using Ways.Application.MercadoPago;
using Ways.Application.Organizacion;
using Ways.Domain.MercadoPago;
using Ways.Domain.Organizacion;
using static Ways.IntegrationTests.ApoyoDeMercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// El registro de un punto de venta como caja (<i>POS</i>) de Mercado Pago contra Postgres real y el manejador falso de Mercado Pago:
/// alta, adopción por <c>external_id</c>, actualización, la mudanza del punto de venta a otra sucursal (re-homing), estados de la
/// consulta, rechazos de Mercado Pago, el 401 que deja la conexión en <c>requiere_reconexion</c>, el lock por punto de venta y los
/// backstops de la tabla <c>cajas_mercadopago</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MercadoPagoCajasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeMercadoPago _apoyo = new(fixture);

    private static long _siguienteLocal = 700_000;

    private sealed record Registrable(Contexto Ctx, int IdSucursal, long IdStore, int IdPuntoVenta, string ExternalId);

    private sealed record FilaDeCaja(
        int Id, int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdLocal, long IdUsuarioMercadoPago, long IdPos, string ExternalId,
        string? UrlQr, DateTimeOffset FechaRegistro, DateTimeOffset FechaSincronizacion, long IdStore);

    private static HttpResponseMessage Json(HttpStatusCode estado, string json) =>
        new(estado) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string RutaDeCajas(int idPuntoVenta) => $"/api/puntos-venta/{idPuntoVenta}/mercadopago";

    private static bool EsDeLaApi(PedidoRegistrado pedido) =>
        !pedido.Uri.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal);

    private static bool EsDeCajas(PedidoRegistrado pedido) =>
        EsDeLaApi(pedido) && pedido.Uri.AbsolutePath.StartsWith("/v2/pos", StringComparison.Ordinal);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ManejadorDeMercadoPago, System.Runtime.CompilerServices.StrongBox<int>> Marcas = [];

    /// <summary>Deja de contar los pedidos hechos hasta ahora (la preparación registra locales): las aserciones miran lo que viene después.</summary>
    private static void Marcar(Contexto ctx) => Marcas.GetOrCreateValue(ctx.MercadoPago).Value = ctx.MercadoPago.Pedidos.Count;

    private static IEnumerable<PedidoRegistrado> Pedidos(Contexto ctx) =>
        ctx.MercadoPago.Pedidos.Skip(Marcas.TryGetValue(ctx.MercadoPago, out var marca) ? marca.Value : 0);

    private static IReadOnlyList<PedidoRegistrado> Llamadas(Contexto ctx) =>
        [.. Pedidos(ctx).Where(EsDeLaApi)];

    private static IReadOnlyList<PedidoRegistrado> LlamadasDeCajas(Contexto ctx) =>
        [.. Pedidos(ctx).Where(EsDeCajas)];

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta, HttpStatusCode esperado = HttpStatusCode.OK)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {cuerpo}");
        var opciones = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        opciones.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return JsonSerializer.Deserialize<T>(cuerpo, opciones)!;
    }

    private static async Task AssertCodigoAsync(HttpResponseMessage respuesta, HttpStatusCode estado, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == estado, $"{respuesta.StatusCode}: {cuerpo}");
        Assert.Equal(codigo, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private static SucursalAlta AltaCompleta(int idEmpresa, string nombre) => new(
        idEmpresa, nombre, "Av. Córdoba", "1234 bis", "Rosario", "Santa Fe", "2000", -32.944243m, -60.650539m, "Frente a la plaza");

    private async Task<Contexto> ConectarAsync(string nombre)
    {
        var ctx = await _apoyo.PrepararAsync(nombre);
        ctx.MercadoPago.Responder = _ => Exitoso(AccessToken, RefreshToken, ctx.MercadoPago.IdUsuario);

        var (_, estado) = await IniciarAsync(ctx);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(ctx, estado)).StatusCode);

        ctx.MercadoPago.Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        return ctx;
    }

    private static async Task<int> CrearSucursalAsync(Contexto ctx, string nombre)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/sucursales", AltaCompleta(ctx.IdEmpresa, nombre));
        return (await LeerAsync<SucursalListado>(respuesta, HttpStatusCode.Created)).Id;
    }

    private static async Task<int> CrearPuntoVentaAsync(Contexto ctx, string nombre, int idSucursal)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(ctx.IdEmpresa, nombre, ModoPuntoVenta.Web, IdSucursal: idSucursal));
        return (await LeerAsync<PuntoVentaListado>(respuesta, HttpStatusCode.Created)).Id;
    }

    private static Task<HttpResponseMessage> MoverAsync(Contexto ctx, int idPuntoVenta, int idSucursal) =>
        ctx.Admin.PostAsJsonAsync($"/api/puntos-venta/{idPuntoVenta}/sucursal", new PuntoVentaSucursalEdicion(idSucursal));

    private const string BusquedaVacia = """{"paging": {"total": 0, "offset": 0, "limit": 30}, "data": []}""";

    /// <summary>La búsqueda de locales sin coincidencia contesta 404, no una lista vacía.</summary>
    private const string LocalNoEncontrado = """{"error": "store_not_found", "message": "store not found"}""";

    /// <summary>Guion del manejador: la búsqueda de cajas responde <paramref name="busqueda"/>, el alta de caja <paramref name="idPos"/> con
    /// un QR, las actualizaciones un 200 y los locales se crean con ids nuevos. <paramref name="antes"/> puede responder primero.</summary>
    private static void Guionar(
        Contexto ctx, string busqueda = BusquedaVacia, long idPos = 880_001, Func<PedidoRegistrado, HttpResponseMessage?>? antes = null) =>
        ctx.MercadoPago.ResponderPedido = pedido =>
        {
            if (!EsDeLaApi(pedido))
            {
                return Exitoso(AccessToken, RefreshToken, ctx.MercadoPago.IdUsuario);
            }

            if (antes?.Invoke(pedido) is { } propia)
            {
                return propia;
            }

            var ruta = pedido.Uri.AbsolutePath;
            if (ruta.Contains("/stores", StringComparison.Ordinal))
            {
                if (pedido.Metodo == HttpMethod.Get)
                {
                    return Json(HttpStatusCode.NotFound, LocalNoEncontrado);
                }

                return pedido.Metodo == HttpMethod.Post
                    ? Json(HttpStatusCode.Created, $$"""{"id": {{Interlocked.Increment(ref _siguienteLocal)}}}""")
                    : Json(HttpStatusCode.OK, """{"id": 1}""");
            }

            if (pedido.Metodo == HttpMethod.Get && ruta == "/v2/pos")
            {
                return Json(HttpStatusCode.OK, busqueda);
            }

            if (pedido.Metodo == HttpMethod.Post && ruta == "/v2/pos")
            {
                return Json(
                    HttpStatusCode.Created,
                    $$"""{"id": {{idPos}}, "name": "x", "qr_response": {"image": "https://qr.test/{{idPos}}.png", "template_document": "d"}, "otro": [1]}""");
            }

            if (pedido.Metodo == HttpMethod.Patch)
            {
                return Json(HttpStatusCode.OK, """{"id": 1}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

    private static async Task<long> RegistrarSucursalAsync(Contexto ctx, int idSucursal)
    {
        Guionar(ctx);
        var respuesta = await ctx.Admin.PostAsync($"/api/sucursales/{idSucursal}/mercadopago", null);
        return (await LeerAsync<EstadoDeLocalMercadoPago>(respuesta)).IdStoreMercadoPago!.Value;
    }

    private async Task<Registrable> PrepararRegistrableAsync(string nombre, string nombrePv = "Caja Uno")
    {
        var ctx = await ConectarAsync(nombre);
        var idSucursal = await CrearSucursalAsync(ctx, "Sucursal Centro");
        var idStore = await RegistrarSucursalAsync(ctx, idSucursal);
        var idPuntoVenta = await CrearPuntoVentaAsync(ctx, nombrePv, idSucursal);
        Marcar(ctx);
        return new Registrable(ctx, idSucursal, idStore, idPuntoVenta, $"WAYSPV{idPuntoVenta}");
    }

    private async Task<List<FilaDeCaja>> FilasAsync(int idPuntoVenta)
    {
        var filas = new List<FilaDeCaja>();
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT id_caja_mercadopago, id_tenant, id_empresa, id_punto_venta, id_local_mercadopago, id_usuario_mercadopago, " +
            "id_pos_mercadopago, external_id, url_qr, fecha_registro, fecha_sincronizacion, id_store_mercadopago FROM cajas_mercadopago " +
            "WHERE id_punto_venta = $1 ORDER BY 1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta });

        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            filas.Add(new FilaDeCaja(
                lector.GetInt32(0), lector.GetInt32(1), lector.GetInt32(2), lector.GetInt32(3), lector.GetInt32(4), lector.GetInt64(5),
                lector.GetInt64(6), lector.GetString(7), lector.IsDBNull(8) ? null : lector.GetString(8),
                lector.GetFieldValue<DateTimeOffset>(9), lector.GetFieldValue<DateTimeOffset>(10), lector.GetInt64(11)));
        }

        return filas;
    }

    private async Task<int> IdLocalAsync(int idSucursal)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT id_local_mercadopago FROM locales_mercadopago WHERE id_sucursal = $1 ORDER BY 1 LIMIT 1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idSucursal });
        return (int)(await comando.ExecuteScalarAsync())!;
    }

    private async Task InsertarFilaAsync(
        Registrable r, long idUsuarioMercadoPago, long idPos, int? idLocal = null, string? externalId = null, int? idEmpresa = null,
        int? idPuntoVenta = null)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "INSERT INTO cajas_mercadopago (id_tenant, id_empresa, id_punto_venta, id_local_mercadopago, id_usuario_mercadopago, " +
            "id_store_mercadopago, id_pos_mercadopago, external_id, fecha_registro, fecha_sincronizacion, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, $5, 1, $6, $7, now(), now(), now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = r.Ctx.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = idEmpresa ?? r.Ctx.IdEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta ?? r.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = idLocal ?? await IdLocalAsync(r.IdSucursal) });
        comando.Parameters.Add(new NpgsqlParameter { Value = idUsuarioMercadoPago });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPos });
        comando.Parameters.Add(new NpgsqlParameter { Value = externalId ?? r.ExternalId });
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<(string Valor, int Cantidad)> AuditoriaAsync(int idPuntoVenta)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT valor_nuevo::text FROM auditoria WHERE accion = 'pv.mercadopago' AND id_entidad = $1 ORDER BY id_auditoria DESC";
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta });

        var valores = new List<string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            valores.Add(lector.GetString(0));
        }

        return (valores.FirstOrDefault() ?? string.Empty, valores.Count);
    }

    // ---- Orden de los errores --------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnPuntoDeVentaInexistenteContesta404AntesQueCualquierOtraCosaEnLasDosRutas()
    {
        var ctx = await _apoyo.PrepararAsync("mp-caja-404");

        await AssertCodigoAsync(await ctx.Admin.GetAsync(RutaDeCajas(987_654)), HttpStatusCode.NotFound, "no_encontrado");
        await AssertCodigoAsync(await ctx.Admin.PostAsync(RutaDeCajas(987_654), null), HttpStatusCode.NotFound, "no_encontrado");
        Assert.Empty(Llamadas(ctx));
    }

    [Fact]
    public async Task UnPuntoDeVentaDeOtroTenantContesta404()
    {
        var a = await ConectarAsync("mp-caja-aislada-a");
        var b = await PrepararRegistrableAsync("mp-caja-aislada-b");

        await AssertCodigoAsync(await a.Admin.GetAsync(RutaDeCajas(b.IdPuntoVenta)), HttpStatusCode.NotFound, "no_encontrado");
        await AssertCodigoAsync(await a.Admin.PostAsync(RutaDeCajas(b.IdPuntoVenta), null), HttpStatusCode.NotFound, "no_encontrado");
        Assert.Empty(Llamadas(a));
        Assert.Empty(await FilasAsync(b.IdPuntoVenta));
    }

    [Fact]
    public async Task SinConexionLaConsultaDiceSinConexionYElRegistroEsUn409SinLlamarAMercadoPago()
    {
        var ctx = await _apoyo.PrepararAsync("mp-caja-sin-conexion");
        var idSucursal = await CrearSucursalAsync(ctx, "Sucursal Sur");
        var idPuntoVenta = await CrearPuntoVentaAsync(ctx, "Caja Sur", idSucursal);

        var estado = await LeerAsync<EstadoDeCajaMercadoPago>(await ctx.Admin.GetAsync(RutaDeCajas(idPuntoVenta)));
        Assert.Equal(EstadosDeCajaMercadoPago.SinConexion, estado.Estado);

        await AssertCodigoAsync(
            await ctx.Admin.PostAsync(RutaDeCajas(idPuntoVenta), null), HttpStatusCode.Conflict, CodigosDeErrorDeMercadoPago.NoConectada);
        Assert.Empty(Llamadas(ctx));
        Assert.Empty(await FilasAsync(idPuntoVenta));
    }

    // Clausula: la sucursal del punto de venta tiene que estar registrada como local de la cuenta actual.
    [Fact]
    public async Task SiLaSucursalNoEstaRegistradaComoLocalElRegistroEsUn409SinLlamarAMercadoPago()
    {
        var ctx = await ConectarAsync("mp-caja-sin-local");
        var idSucursal = await CrearSucursalAsync(ctx, "Sucursal Norte");
        var idPuntoVenta = await CrearPuntoVentaAsync(ctx, "Caja Norte", idSucursal);
        Guionar(ctx);

        var estado = await LeerAsync<EstadoDeCajaMercadoPago>(await ctx.Admin.GetAsync(RutaDeCajas(idPuntoVenta)));
        Assert.Equal(EstadosDeCajaMercadoPago.LocalNoRegistrado, estado.Estado);

        await AssertCodigoAsync(
            await ctx.Admin.PostAsync(RutaDeCajas(idPuntoVenta), null), HttpStatusCode.Conflict,
            CodigosDeErrorDeMercadoPago.LocalNoRegistrado);
        Assert.Empty(Llamadas(ctx));
        Assert.Empty(await FilasAsync(idPuntoVenta));
    }

    [Fact]
    public async Task UnNombreSinNingunCaracterAdmitidoEsUn400QueNoLlamaAMercadoPago()
    {
        var r = await PrepararRegistrableAsync("mp-caja-nombre", nombrePv: "###");
        Guionar(r.Ctx);

        await AssertCodigoAsync(
            await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null), HttpStatusCode.BadRequest, "nombre_invalido_para_mercadopago");
        Assert.Empty(Llamadas(r.Ctx));
        Assert.Empty(await FilasAsync(r.IdPuntoVenta));
    }

    // ---- Alta, adopción y actualización ----------------------------------------------------------------------------------

    [Fact]
    public async Task UnPuntoDeVentaSeBuscaPorExternalIdYSeCreaBajoElLocalDeSuSucursalConUnaClaveDeIdempotencia()
    {
        var r = await PrepararRegistrableAsync("mp-caja-alta", nombrePv: "Caja Ñandú #1 (centro) con un nombre bastante largo para cortar");
        Guionar(r.Ctx, idPos: 880_123);

        var estado = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        Assert.Equal(EstadosDeCajaMercadoPago.Registrada, estado.Estado);
        Assert.Equal(880_123, estado.IdPosMercadoPago);
        Assert.Equal(r.IdStore, estado.IdStoreMercadoPago);
        Assert.Equal(r.ExternalId, estado.ExternalId);
        Assert.Equal("https://qr.test/880123.png", estado.UrlQr);

        var llamadas = LlamadasDeCajas(r.Ctx);
        Assert.Equal(2, llamadas.Count);

        var busqueda = llamadas[0];
        Assert.Equal(HttpMethod.Get, busqueda.Metodo);
        Assert.Equal("/v2/pos", busqueda.Uri.AbsolutePath);
        Assert.Equal($"?external_id={r.ExternalId}", busqueda.Uri.Query);
        Assert.StartsWith("Bearer ", busqueda.Autorizacion);

        var alta = llamadas[1];
        Assert.Equal(HttpMethod.Post, alta.Metodo);
        Assert.Equal("/v2/pos", alta.Uri.AbsolutePath);
        Assert.False(string.IsNullOrWhiteSpace(alta.ClaveDeIdempotencia));
        Assert.True(alta.ClaveDeIdempotencia!.Length <= 64);

        var cuerpo = JsonDocument.Parse(alta.Cuerpo).RootElement;
        var nombre = cuerpo.GetProperty("name").GetString()!;
        Assert.True(nombre.Length <= 45);
        Assert.StartsWith("Caja Nandu 1 centro con un nombre", nombre);
        Assert.Equal(r.ExternalId, cuerpo.GetProperty("external_id").GetString());
        Assert.Equal(JsonValueKind.String, cuerpo.GetProperty("store_id").ValueKind);
        Assert.Equal(r.IdStore.ToString(), cuerpo.GetProperty("store_id").GetString());
        Assert.False(cuerpo.TryGetProperty("fixed_amount", out _));
        Assert.Equal("pdv", cuerpo.GetProperty("config").GetProperty("qr").GetProperty("operating_mode").GetString());

        var fila = Assert.Single(await FilasAsync(r.IdPuntoVenta));
        Assert.Equal(r.Ctx.IdTenant, fila.IdTenant);
        Assert.Equal(r.Ctx.IdEmpresa, fila.IdEmpresa);
        Assert.Equal(r.Ctx.MercadoPago.IdUsuario, fila.IdUsuarioMercadoPago);
        Assert.Equal(await IdLocalAsync(r.IdSucursal), fila.IdLocal);
        Assert.Equal(880_123, fila.IdPos);
        Assert.Equal(r.ExternalId, fila.ExternalId);
        Assert.Equal("https://qr.test/880123.png", fila.UrlQr);

        var (auditado, cantidad) = await AuditoriaAsync(r.IdPuntoVenta);
        Assert.Equal(1, cantidad);
        Assert.Contains("\"operacion\": \"alta\"", auditado);
        Assert.Contains("880123", auditado);
        Assert.DoesNotContain(AccessToken, auditado);
        Assert.DoesNotContain(RefreshToken, auditado);
    }

    // Clausula: una caja que ya existe con el mismo external_id se adopta en vez de crear otra.
    [Fact]
    public async Task UnaCajaQueYaExisteConElMismoExternalIdSeAdoptaEnVezDeCrearOtra()
    {
        var r = await PrepararRegistrableAsync("mp-caja-adopcion");
        Guionar(r.Ctx, $$"""{"paging": {"total": 2}, "data": [{"id": 111, "external_id": "OTRO"}, {"id": 777001, "external_id": "{{r.ExternalId}}", "store_id": 5}]}""");

        var estado = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        Assert.Equal(777_001, estado.IdPosMercadoPago);
        var llamadas = LlamadasDeCajas(r.Ctx);
        Assert.DoesNotContain(llamadas, l => l.Metodo == HttpMethod.Post);
        var actualizacion = Assert.Single(llamadas, l => l.Metodo == HttpMethod.Patch);
        Assert.Equal("/v2/pos/777001", actualizacion.Uri.AbsolutePath);
        Assert.Equal(r.IdStore.ToString(), JsonDocument.Parse(actualizacion.Cuerpo).RootElement.GetProperty("store_id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(actualizacion.ClaveDeIdempotencia));
        Assert.Equal(777_001, Assert.Single(await FilasAsync(r.IdPuntoVenta)).IdPos);
        Assert.Contains("\"operacion\": \"adopcion\"", (await AuditoriaAsync(r.IdPuntoVenta)).Valor);
    }

    // Clausula: un alta que contesta 409 pos_already_exists (otra instancia la creo despues de la busqueda) busca por external_id y adopta.
    [Fact]
    public async Task UnAltaQueContesta409PosAlreadyExistsBuscaPorExternalIdYLaAdopta()
    {
        var r = await PrepararRegistrableAsync("mp-caja-409-adopta");
        var busquedas = 0;
        Guionar(
            r.Ctx,
            antes: p =>
            {
                if (p.Metodo == HttpMethod.Get && p.Uri.AbsolutePath == "/v2/pos")
                {
                    return Interlocked.Increment(ref busquedas) == 1
                        ? Json(HttpStatusCode.OK, BusquedaVacia)
                        : Json(HttpStatusCode.OK, $$"""{"data": [{"id": 770001, "external_id": "{{r.ExternalId}}"}]}""");
                }

                return p.Metodo == HttpMethod.Post && p.Uri.AbsolutePath == "/v2/pos"
                    ? Json(HttpStatusCode.Conflict, """{"error": "pos_already_exists", "message": "pos already exists"}""")
                    : null;
            });

        var estado = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        Assert.Equal(770_001, estado.IdPosMercadoPago);
        var llamadas = LlamadasDeCajas(r.Ctx);
        Assert.Equal(["GET", "POST", "GET", "PATCH"], llamadas.Select(l => l.Metodo.Method));
        Assert.EndsWith("/770001", llamadas[3].Uri.AbsolutePath);
        Assert.Equal(770_001, Assert.Single(await FilasAsync(r.IdPuntoVenta)).IdPos);
        Assert.Contains("\"operacion\": \"adopcion\"", (await AuditoriaAsync(r.IdPuntoVenta)).Valor);
    }

    [Fact]
    public async Task UnAltaQueContesta409SinCajaConEseExternalIdSigueSiendo502()
    {
        var r = await PrepararRegistrableAsync("mp-caja-409-sin-caja");
        Guionar(
            r.Ctx,
            antes: p => p.Metodo == HttpMethod.Post && p.Uri.AbsolutePath == "/v2/pos"
                ? Json(HttpStatusCode.Conflict, """{"error": "pos_already_exists"}""")
                : null);

        await AssertCodigoAsync(
            await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null), HttpStatusCode.BadGateway,
            CodigosDeErrorDeMercadoPago.SolicitudRechazada);
        Assert.Equal(["GET", "POST", "GET"], LlamadasDeCajas(r.Ctx).Select(l => l.Metodo.Method));
        Assert.Empty(await FilasAsync(r.IdPuntoVenta));
    }

    [Fact]
    public async Task UnResultadoDeLaBusquedaConOtroExternalIdNoSeAdopta()
    {
        var r = await PrepararRegistrableAsync("mp-caja-otro-external");
        Guionar(r.Ctx, """{"data": [{"id": 111, "external_id": "WAYSPV999999"}]}""", idPos: 880_002);

        var estado = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        Assert.Equal(880_002, estado.IdPosMercadoPago);
        Assert.Single(LlamadasDeCajas(r.Ctx), l => l.Metodo == HttpMethod.Post);
    }

    [Fact]
    public async Task ElQrVieneEnQrResponseYSiFaltaLaCajaSeGuardaSinUrl()
    {
        var r = await PrepararRegistrableAsync("mp-caja-qr");
        Guionar(
            r.Ctx, antes: p => p.Metodo == HttpMethod.Post && p.Uri.AbsolutePath == "/v2/pos"
                ? Json(HttpStatusCode.Created, """{"id": 880100, "qr_response": {"image": "https://qr.test/otro.png"}}""")
                : null);
        var primera = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));
        Assert.Equal("https://qr.test/otro.png", primera.UrlQr);

        var sinQr = await PrepararRegistrableAsync("mp-caja-sin-qr");
        Guionar(sinQr.Ctx, antes: p => p.Metodo == HttpMethod.Post && p.Uri.AbsolutePath == "/v2/pos" ? Json(HttpStatusCode.Created, """{"id": 880101}""") : null);
        var segunda = await LeerAsync<EstadoDeCajaMercadoPago>(await sinQr.Ctx.Admin.PostAsync(RutaDeCajas(sinQr.IdPuntoVenta), null));
        Assert.Null(segunda.UrlQr);
        Assert.Null(Assert.Single(await FilasAsync(sinQr.IdPuntoVenta)).UrlQr);
    }

    [Fact]
    public async Task RegistrarDeNuevoActualizaLaCajaExistenteSinBuscarNiCrearYConservaElRegistro()
    {
        var r = await PrepararRegistrableAsync("mp-caja-actualiza");
        Guionar(r.Ctx, idPos: 880_300);
        var primera = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        var edicion = await r.Ctx.Admin.PutAsJsonAsync(
            $"/api/puntos-venta/{r.IdPuntoVenta}", new PuntoVentaEdicion("Caja Uno Nueva", null, null, null, null, null, null));
        Assert.Equal(HttpStatusCode.OK, edicion.StatusCode);
        var antes = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeCajas(r.IdPuntoVenta)));
        Assert.Equal(EstadosDeCajaMercadoPago.Desactualizada, antes.Estado);

        var previas = Llamadas(r.Ctx).Count;
        var segunda = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        var actualizacion = Assert.Single(Llamadas(r.Ctx).Skip(previas));
        Assert.Equal(HttpMethod.Patch, actualizacion.Metodo);
        Assert.Equal("/v2/pos/880300", actualizacion.Uri.AbsolutePath);
        Assert.Equal("Caja Uno Nueva", JsonDocument.Parse(actualizacion.Cuerpo).RootElement.GetProperty("name").GetString());

        Assert.Equal(EstadosDeCajaMercadoPago.Registrada, segunda.Estado);
        Assert.Equal(primera.FechaRegistro!.Value, segunda.FechaRegistro!.Value, TimeSpan.FromMilliseconds(1));
        Assert.True(segunda.FechaSincronizacion > primera.FechaSincronizacion);
        Assert.Equal("https://qr.test/880300.png", Assert.Single(await FilasAsync(r.IdPuntoVenta)).UrlQr);

        var despues = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeCajas(r.IdPuntoVenta)));
        Assert.Equal(EstadosDeCajaMercadoPago.Registrada, despues.Estado);
        Assert.Equal(2, (await AuditoriaAsync(r.IdPuntoVenta)).Cantidad);
    }

    // ---- Mudanza de sucursal (re-homing) -------------------------------------------------------------------------------

    // Clausula: el PATCH lleva el store_id del local de la sucursal ACTUAL del punto de venta, y el vinculo guarda el local nuevo.
    [Fact]
    public async Task UnPuntoDeVentaMovidoAOtraSucursalRegistradaSeReapuntaConElStoreIdDelLocalNuevo()
    {
        var r = await PrepararRegistrableAsync("mp-caja-rehoming");
        var idSucursalB = await CrearSucursalAsync(r.Ctx, "Sucursal B");
        var idStoreB = await RegistrarSucursalAsync(r.Ctx, idSucursalB);
        Assert.NotEqual(r.IdStore, idStoreB);
        await CrearPuntoVentaAsync(r.Ctx, "Otra caja de A", r.IdSucursal);
        Guionar(r.Ctx, idPos: 880_400);
        await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        Assert.Equal(HttpStatusCode.OK, (await MoverAsync(r.Ctx, r.IdPuntoVenta, idSucursalB)).StatusCode);
        var desactualizada = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeCajas(r.IdPuntoVenta)));
        Assert.Equal(EstadosDeCajaMercadoPago.Desactualizada, desactualizada.Estado);
        Assert.Equal(r.IdStore, desactualizada.IdStoreMercadoPago);

        var previas = LlamadasDeCajas(r.Ctx).Count;
        var registrada = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        var nuevas = LlamadasDeCajas(r.Ctx).Skip(previas).ToList();
        var actualizacion = Assert.Single(nuevas);
        Assert.Equal(HttpMethod.Patch, actualizacion.Metodo);
        Assert.Equal("/v2/pos/880400", actualizacion.Uri.AbsolutePath);
        Assert.Equal(idStoreB.ToString(), JsonDocument.Parse(actualizacion.Cuerpo).RootElement.GetProperty("store_id").GetString());

        Assert.Equal(EstadosDeCajaMercadoPago.Registrada, registrada.Estado);
        Assert.Equal(idStoreB, registrada.IdStoreMercadoPago);
        var fila = Assert.Single(await FilasAsync(r.IdPuntoVenta));
        Assert.Equal(await IdLocalAsync(idSucursalB), fila.IdLocal);
        Assert.Equal(880_400, fila.IdPos);

        var despues = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeCajas(r.IdPuntoVenta)));
        Assert.Equal(EstadosDeCajaMercadoPago.Registrada, despues.Estado);
    }

    // Clausula: la caja guarda el store con el que se registro; si el local se registra de nuevo con otro id queda desactualizada.
    [Fact]
    public async Task SiElLocalSeRegistraDeNuevoConOtroStoreIdLaCajaQuedaDesactualizadaYRegistrarlaLaReapunta()
    {
        var r = await PrepararRegistrableAsync("mp-caja-store-nuevo");
        Guionar(r.Ctx, idPos: 881_100);
        await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        Guionar(
            r.Ctx, antes: p => p.Metodo == HttpMethod.Put && p.Uri.AbsolutePath.Contains("/stores/", StringComparison.Ordinal)
                ? Json(HttpStatusCode.NotFound, LocalNoEncontrado)
                : null);
        var local = await LeerAsync<EstadoDeLocalMercadoPago>(
            await r.Ctx.Admin.PostAsync($"/api/sucursales/{r.IdSucursal}/mercadopago", null));
        var idStoreNuevo = local.IdStoreMercadoPago!.Value;
        Assert.NotEqual(r.IdStore, idStoreNuevo);

        var desactualizada = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeCajas(r.IdPuntoVenta)));
        Assert.Equal(EstadosDeCajaMercadoPago.Desactualizada, desactualizada.Estado);
        Assert.Equal(r.IdStore, desactualizada.IdStoreMercadoPago);

        Guionar(r.Ctx, idPos: 881_100);
        Marcar(r.Ctx);
        var registrada = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        var actualizacion = Assert.Single(LlamadasDeCajas(r.Ctx));
        Assert.Equal(HttpMethod.Patch, actualizacion.Metodo);
        Assert.Equal(idStoreNuevo.ToString(), JsonDocument.Parse(actualizacion.Cuerpo).RootElement.GetProperty("store_id").GetString());
        Assert.Equal(EstadosDeCajaMercadoPago.Registrada, registrada.Estado);
        Assert.Equal(idStoreNuevo, registrada.IdStoreMercadoPago);
        Assert.Equal(idStoreNuevo, Assert.Single(await FilasAsync(r.IdPuntoVenta)).IdStore);
        Assert.Equal(
            EstadosDeCajaMercadoPago.Registrada,
            (await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeCajas(r.IdPuntoVenta)))).Estado);
    }

    // Clausula: orden de locks sucursal -> punto de venta; una registracion del local de la sucursal excluye a la de sus cajas.
    [Fact]
    public async Task UnaRegistracionDelLocalDeLaSucursalExcluyeLaRegistracionDeSusCajas()
    {
        var r = await PrepararRegistrableAsync("mp-caja-lock-sucursal");
        Guionar(r.Ctx, idPos: 881_200);

        var detenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primera = 0;
        r.Ctx.MercadoPago.AntesDeResponder = async () =>
        {
            if (Interlocked.Increment(ref primera) == 1)
            {
                detenido.TrySetResult();
                await liberar.Task;
            }
        };

        var local = r.Ctx.Admin.PostAsync($"/api/sucursales/{r.IdSucursal}/mercadopago", null);
        await detenido.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var caja = r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null);

        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Single(Llamadas(r.Ctx));
        Assert.False(caja.IsCompleted);

        liberar.SetResult();
        foreach (var respuesta in await Task.WhenAll(local, caja))
        {
            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        }

        Assert.Equal(1, LlamadasDeCajas(r.Ctx).Count(l => l.Metodo == HttpMethod.Post));
        Assert.Single(await FilasAsync(r.IdPuntoVenta));
    }

    // El vinculo no bloquea mover la caja ni siquiera cuando es la ultima de una sucursal registrada como local.
    [Fact]
    public async Task ElVinculoNoBloqueaMoverLaUnicaCajaDeUnaSucursalRegistradaYDespuesSeReapunta()
    {
        var ctx = await ConectarAsync("mp-caja-mover-ultima");
        var idSucursalA = await CrearSucursalAsync(ctx, "Sucursal A");
        await RegistrarSucursalAsync(ctx, idSucursalA);
        var idSucursalB = await CrearSucursalAsync(ctx, "Sucursal B");
        var idStoreB = await RegistrarSucursalAsync(ctx, idSucursalB);
        var idPuntoVenta = await CrearPuntoVentaAsync(ctx, "Unica caja de A", idSucursalA);
        Guionar(ctx, idPos: 880_500);
        await LeerAsync<EstadoDeCajaMercadoPago>(await ctx.Admin.PostAsync(RutaDeCajas(idPuntoVenta), null));

        var mover = await MoverAsync(ctx, idPuntoVenta, idSucursalB);
        Assert.True(mover.StatusCode == HttpStatusCode.OK, $"{mover.StatusCode}: {await mover.Content.ReadAsStringAsync()}");

        var registrada = await LeerAsync<EstadoDeCajaMercadoPago>(await ctx.Admin.PostAsync(RutaDeCajas(idPuntoVenta), null));
        Assert.Equal(idStoreB, registrada.IdStoreMercadoPago);
    }

    [Fact]
    public async Task UnPuntoDeVentaMovidoAUnaSucursalSinLocalAvisaYNoLlamaAMercadoPago()
    {
        var r = await PrepararRegistrableAsync("mp-caja-rehoming-sin-local");
        var idSucursalB = await CrearSucursalAsync(r.Ctx, "Sucursal B");
        await CrearPuntoVentaAsync(r.Ctx, "Otra caja de A", r.IdSucursal);
        Guionar(r.Ctx);
        await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));
        Assert.Equal(HttpStatusCode.OK, (await MoverAsync(r.Ctx, r.IdPuntoVenta, idSucursalB)).StatusCode);
        var previas = Llamadas(r.Ctx).Count;

        var estado = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeCajas(r.IdPuntoVenta)));
        Assert.Equal(EstadosDeCajaMercadoPago.LocalNoRegistrado, estado.Estado);
        await AssertCodigoAsync(
            await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null), HttpStatusCode.Conflict,
            CodigosDeErrorDeMercadoPago.LocalNoRegistrado);
        Assert.Equal(previas, Llamadas(r.Ctx).Count);
    }

    // Clausula: la clave de la actualizacion lleva el store_id y el estado del punto de venta; A -> B -> A no repite la clave.
    [Fact]
    public async Task MoverYVolverAUnaSucursalAnteriorNoReusaLaClaveDeLaActualizacion()
    {
        var r = await PrepararRegistrableAsync("mp-caja-clave-put");
        var idSucursalB = await CrearSucursalAsync(r.Ctx, "Sucursal B");
        await RegistrarSucursalAsync(r.Ctx, idSucursalB);
        await CrearPuntoVentaAsync(r.Ctx, "Otra caja de A", r.IdSucursal);
        await CrearPuntoVentaAsync(r.Ctx, "Otra caja de B", idSucursalB);
        Guionar(r.Ctx, idPos: 880_600);
        var ruta = RutaDeCajas(r.IdPuntoVenta);
        await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(ruta, null));

        async Task RegistrarTrasMoverAAsync(int idSucursal)
        {
            Assert.Equal(HttpStatusCode.OK, (await MoverAsync(r.Ctx, r.IdPuntoVenta, idSucursal)).StatusCode);
            await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(ruta, null));
        }

        await RegistrarTrasMoverAAsync(idSucursalB);
        await RegistrarTrasMoverAAsync(r.IdSucursal);
        await RegistrarTrasMoverAAsync(idSucursalB);

        var claves = LlamadasDeCajas(r.Ctx).Where(l => l.Metodo == HttpMethod.Patch).Select(l => l.ClaveDeIdempotencia).ToList();
        Assert.Equal(3, claves.Count);
        Assert.Equal(3, claves.Distinct().Count());
        Assert.All(claves, c => Assert.True(c!.Length <= 64));
    }

    // Clausula: la clave del alta lleva la huella del cuerpo (Mercado Pago contesta 409 si la misma clave llega con otro cuerpo).
    [Fact]
    public async Task UnAltaRechazadaYReintentadaConOtroNombreUsaOtraClaveDeIdempotenciaYUnReintentoIgualLaRepite()
    {
        var r = await PrepararRegistrableAsync("mp-caja-clave-alta");
        Guionar(
            r.Ctx, idPos: 880_700,
            antes: p => p.Metodo == HttpMethod.Post && p.Uri.AbsolutePath == "/v2/pos"
                ? Json(HttpStatusCode.BadRequest, """{"error": "INVALID_NAME", "message": "no"}""")
                : null);
        var ruta = RutaDeCajas(r.IdPuntoVenta);

        await AssertCodigoAsync(await r.Ctx.Admin.PostAsync(ruta, null), HttpStatusCode.BadGateway, CodigosDeErrorDeMercadoPago.SolicitudRechazada);
        await AssertCodigoAsync(await r.Ctx.Admin.PostAsync(ruta, null), HttpStatusCode.BadGateway, CodigosDeErrorDeMercadoPago.SolicitudRechazada);
        Assert.Equal(
            HttpStatusCode.OK,
            (await r.Ctx.Admin.PutAsJsonAsync($"/api/puntos-venta/{r.IdPuntoVenta}", new PuntoVentaEdicion("Caja Nueva", null, null, null, null, null, null))).StatusCode);
        await AssertCodigoAsync(await r.Ctx.Admin.PostAsync(ruta, null), HttpStatusCode.BadGateway, CodigosDeErrorDeMercadoPago.SolicitudRechazada);

        var altas = LlamadasDeCajas(r.Ctx).Where(l => l.Metodo == HttpMethod.Post).ToList();
        Assert.Equal(3, altas.Count);
        Assert.Equal(altas[0].ClaveDeIdempotencia, altas[1].ClaveDeIdempotencia);
        Assert.NotEqual(altas[1].ClaveDeIdempotencia, altas[2].ClaveDeIdempotencia);
    }

    // Clausula: un 404 del PATCH (la caja se borro en Mercado Pago) busca por external_id y adopta, con otra clave que la del PATCH fallido.
    [Fact]
    public async Task SiLaCajaSeBorroEnMercadoPagoElRegistroLaAdoptaPorExternalIdYActualizaElVinculo()
    {
        var r = await PrepararRegistrableAsync("mp-caja-borrada-adopta");
        Guionar(r.Ctx, idPos: 880_720);
        await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        Guionar(
            r.Ctx, $$"""{"data": [{"id": 990001, "external_id": "{{r.ExternalId}}"}]}""",
            antes: p => p.Metodo == HttpMethod.Patch && p.Uri.AbsolutePath == "/v2/pos/880720"
                ? Json(HttpStatusCode.NotFound, """{"error": "not_found", "message": "pos not found"}""")
                : null);
        var previas = LlamadasDeCajas(r.Ctx).Count;

        var estado = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        Assert.Equal(990_001, estado.IdPosMercadoPago);
        var nuevas = LlamadasDeCajas(r.Ctx).Skip(previas).ToList();
        Assert.Equal(["PATCH", "GET", "PATCH"], nuevas.Select(l => l.Metodo.Method));
        Assert.EndsWith("/990001", nuevas[2].Uri.AbsolutePath);
        Assert.NotEqual(nuevas[0].ClaveDeIdempotencia, nuevas[2].ClaveDeIdempotencia);
        Assert.Equal(990_001, Assert.Single(await FilasAsync(r.IdPuntoVenta)).IdPos);
        Assert.Contains("\"operacion\": \"adopcion\"", (await AuditoriaAsync(r.IdPuntoVenta)).Valor);
    }

    // Clausula: una búsqueda de cajas que contesta 404 cuenta como sin coincidencias y la caja se crea.
    [Fact]
    public async Task UnaBusquedaDeCajasQueContesta404CreaLaCaja()
    {
        var r = await PrepararRegistrableAsync("mp-caja-busqueda-404");
        Guionar(
            r.Ctx, idPos: 880_740,
            antes: p => p.Metodo == HttpMethod.Get && p.Uri.AbsolutePath == "/v2/pos"
                ? Json(HttpStatusCode.NotFound, """{"error": "pos_not_found", "message": "pos not found"}""")
                : null);
        var previas = LlamadasDeCajas(r.Ctx).Count;

        var estado = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        Assert.Equal(880_740, estado.IdPosMercadoPago);
        Assert.Equal(["GET", "POST"], LlamadasDeCajas(r.Ctx).Skip(previas).Select(l => l.Metodo.Method));
    }

    // Clausula: un 404 store_not_found del PATCH no es una caja borrada: no se busca ni se crea otra, sale como 502.
    [Fact]
    public async Task UnPatchQueContestaStoreNotFoundNoSeTomaComoCajaBorrada()
    {
        var r = await PrepararRegistrableAsync("mp-caja-patch-sin-local");
        Guionar(r.Ctx, idPos: 880_750);
        await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        Guionar(
            r.Ctx, idPos: 990_751,
            antes: p => p.Metodo == HttpMethod.Patch
                ? Json(HttpStatusCode.NotFound, """{"error": "store_not_found", "message": "store not found"}""")
                : null);
        var previas = LlamadasDeCajas(r.Ctx).Count;

        await AssertCodigoAsync(
            await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null), HttpStatusCode.BadGateway,
            CodigosDeErrorDeMercadoPago.SolicitudRechazada);

        Assert.Equal(["PATCH"], LlamadasDeCajas(r.Ctx).Skip(previas).Select(l => l.Metodo.Method));
        Assert.Equal(880_750, Assert.Single(await FilasAsync(r.IdPuntoVenta)).IdPos);
    }

    // Clausula: un 404 del PATCH sin caja con ese external_id la vuelve a crear.
    [Fact]
    public async Task SiLaCajaSeBorroEnMercadoPagoYNoHayOtraElRegistroLaCreaDeNuevoYActualizaElVinculo()
    {
        var r = await PrepararRegistrableAsync("mp-caja-borrada-crea");
        Guionar(r.Ctx, idPos: 880_730);
        await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        Guionar(
            r.Ctx, idPos: 990_731,
            antes: p => p.Metodo == HttpMethod.Patch && p.Uri.AbsolutePath == "/v2/pos/880730" ? Json(HttpStatusCode.NotFound, """{"error": "not_found"}""") : null);
        var previas = LlamadasDeCajas(r.Ctx).Count;

        var estado = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        Assert.Equal(990_731, estado.IdPosMercadoPago);
        Assert.Equal(["PATCH", "GET", "POST"], LlamadasDeCajas(r.Ctx).Skip(previas).Select(l => l.Metodo.Method));
        Assert.Equal(990_731, Assert.Single(await FilasAsync(r.IdPuntoVenta)).IdPos);
        Assert.Contains("\"operacion\": \"alta\"", (await AuditoriaAsync(r.IdPuntoVenta)).Valor);
    }

    // Clausula: solo el 404 dispara la recuperacion; otro 4xx del PATCH sigue siendo un 502.
    [Fact]
    public async Task UnRechazoDelPutQueNoEs404SigueSiendo502SinBuscarNiCrear()
    {
        var r = await PrepararRegistrableAsync("mp-caja-put-400");
        Guionar(r.Ctx, idPos: 880_740);
        await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        Guionar(r.Ctx, antes: p => p.Metodo == HttpMethod.Patch ? Json(HttpStatusCode.BadRequest, """{"error": "INVALID_NAME"}""") : null);
        var previas = LlamadasDeCajas(r.Ctx).Count;

        await AssertCodigoAsync(
            await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null), HttpStatusCode.BadGateway,
            CodigosDeErrorDeMercadoPago.SolicitudRechazada);
        Assert.Equal(["PATCH"], LlamadasDeCajas(r.Ctx).Skip(previas).Select(l => l.Metodo.Method));
        Assert.Equal(880_740, Assert.Single(await FilasAsync(r.IdPuntoVenta)).IdPos);
    }

    [Fact]
    public async Task LaConsultaDistingueNoRegistradaYRegistrada()
    {
        var r = await PrepararRegistrableAsync("mp-caja-estados");
        Guionar(r.Ctx);

        var antes = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeCajas(r.IdPuntoVenta)));
        Assert.Equal(EstadosDeCajaMercadoPago.NoRegistrada, antes.Estado);
        Assert.Null(antes.IdPosMercadoPago);
        Assert.Null(antes.FechaRegistro);

        await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));
        var registrada = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeCajas(r.IdPuntoVenta)));
        Assert.Equal(EstadosDeCajaMercadoPago.Registrada, registrada.Estado);
        Assert.Equal(r.ExternalId, registrada.ExternalId);
        Assert.Equal(r.IdStore, registrada.IdStoreMercadoPago);
        Assert.NotNull(registrada.FechaRegistro);
    }

    [Fact]
    public async Task UnaReconexionConOtraCuentaRegistraDeNuevoYLaFilaDeLaCuentaAnteriorQuedaDeHistoria()
    {
        var r = await PrepararRegistrableAsync("mp-caja-reconexion");
        Guionar(r.Ctx, idPos: 880_800);
        await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        Assert.Equal(HttpStatusCode.NoContent, (await r.Ctx.Admin.DeleteAsync($"{r.Ctx.Ruta}")).StatusCode);
        var sinConexion = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeCajas(r.IdPuntoVenta)));
        Assert.Equal(EstadosDeCajaMercadoPago.SinConexion, sinConexion.Estado);

        var otraCuenta = NuevoIdUsuario();
        r.Ctx.MercadoPago.Responder = _ => Exitoso(AccessToken, RefreshToken, otraCuenta);
        r.Ctx.MercadoPago.ResponderPedido = null;
        var (_, estado) = await IniciarAsync(r.Ctx);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(r.Ctx, estado)).StatusCode);

        var nuevaCuenta = await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeCajas(r.IdPuntoVenta)));
        Assert.Equal(EstadosDeCajaMercadoPago.LocalNoRegistrado, nuevaCuenta.Estado);

        await RegistrarSucursalAsync(r.Ctx, r.IdSucursal);
        Guionar(r.Ctx, idPos: 880_801);
        await LeerAsync<EstadoDeCajaMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null));

        var filas = await FilasAsync(r.IdPuntoVenta);
        Assert.Equal(2, filas.Count);
        Assert.Equal(880_800, filas.Single(f => f.IdUsuarioMercadoPago == r.Ctx.MercadoPago.IdUsuario).IdPos);
        Assert.Equal(880_801, filas.Single(f => f.IdUsuarioMercadoPago == otraCuenta).IdPos);
    }

    // ---- Fallas de Mercado Pago ------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnRechazoDeMercadoPagoEs502ConSuMensajeSaneadoYNoDejaVinculoNiRastro()
    {
        var r = await PrepararRegistrableAsync("mp-caja-rechazo");
        Guionar(
            r.Ctx,
            antes: p => p.Metodo == HttpMethod.Post && p.Uri.AbsolutePath == "/v2/pos"
                ? Json(HttpStatusCode.BadRequest, """{"error": "INVALID_NAME", "message": "invalid name\u0007 for pos"}""")
                : null);

        var respuesta = await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null);

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadGateway, CodigosDeErrorDeMercadoPago.SolicitudRechazada);
        Assert.Empty(await FilasAsync(r.IdPuntoVenta));
        Assert.Equal(0, (await AuditoriaAsync(r.IdPuntoVenta)).Cantidad);
        Assert.DoesNotContain(AccessToken, await respuesta.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("""{"name": "sin id"}""")]
    [InlineData("""{"id": 0}""")]
    [InlineData("""[1, 2]""")]
    public async Task UnAltaSinIdentificadorValidoEs502DeRespuestaInvalidaYNoGuardaNada(string respuestaDelAlta)
    {
        var r = await PrepararRegistrableAsync("mp-caja-sin-id");
        Guionar(r.Ctx, antes: p => p.Metodo == HttpMethod.Post && p.Uri.AbsolutePath == "/v2/pos" ? Json(HttpStatusCode.Created, respuestaDelAlta) : null);

        var respuesta = await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null);

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadGateway, CodigosDeErrorDeMercadoPago.RespuestaInvalida);
        Assert.Empty(await FilasAsync(r.IdPuntoVenta));
    }

    [Fact]
    public async Task MercadoPagoCaidoEs503YNoGuardaNada()
    {
        var r = await PrepararRegistrableAsync("mp-caja-caido");
        Guionar(r.Ctx, antes: p => p.Metodo == HttpMethod.Get && p.Uri.AbsolutePath == "/v2/pos" ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null);

        await AssertCodigoAsync(
            await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null), HttpStatusCode.ServiceUnavailable,
            CodigosDeErrorDeMercadoPago.NoDisponible);
        Assert.Empty(await FilasAsync(r.IdPuntoVenta));
    }

    [Fact]
    public async Task UnoDe401DeMercadoPagoDejaLaConexionEnRequiereReconexionAunqueElRegistroFalle()
    {
        var r = await PrepararRegistrableAsync("mp-caja-401");
        Guionar(r.Ctx, antes: p => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await AssertCodigoAsync(
            await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null), HttpStatusCode.Conflict,
            CodigosDeErrorDeMercadoPago.RequiereReconexion);

        var conexion = Assert.Single(await _apoyo.FilasAsync(r.Ctx.IdEmpresa), f => f.Estado != "desconectada");
        Assert.Equal("requiere_reconexion", conexion.Estado);
        Assert.Null(conexion.AccessCifrado);
        Assert.Empty(await FilasAsync(r.IdPuntoVenta));
    }

    // ---- Lock por punto de venta y backstop ------------------------------------------------------------------------------

    // Clausula: el pg_advisory_xact_lock por punto de venta serializa dos registros simultaneos.
    [Fact]
    public async Task DosRegistrosSimultaneosDelMismoPuntoDeVentaSeSerializanYSoloElPrimeroCrea()
    {
        var r = await PrepararRegistrableAsync("mp-caja-lock");
        Guionar(r.Ctx, idPos: 880_900);

        var detenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primera = 0;
        r.Ctx.MercadoPago.AntesDeResponder = async () =>
        {
            if (Interlocked.Increment(ref primera) == 1)
            {
                detenido.TrySetResult();
                await liberar.Task;
            }
        };

        var uno = r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null);
        await detenido.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var dos = r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null);

        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Single(Llamadas(r.Ctx));
        Assert.False(dos.IsCompleted);

        liberar.SetResult();
        var respuestas = await Task.WhenAll(uno, dos);

        foreach (var respuesta in respuestas)
        {
            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        }

        var llamadas = LlamadasDeCajas(r.Ctx);
        Assert.Single(llamadas, l => l.Metodo == HttpMethod.Post);
        Assert.Single(llamadas, l => l.Metodo == HttpMethod.Patch);
        Assert.Single(await FilasAsync(r.IdPuntoVenta));
        Assert.Equal(2, (await AuditoriaAsync(r.IdPuntoVenta)).Cantidad);
    }

    [Fact]
    public async Task UnVinculoInsertadoPorFueraEntreLaLlamadaYElGuardadoRespondeLaCarreraConElIndiceUnico()
    {
        var r = await PrepararRegistrableAsync("mp-caja-carrera");
        var competidora = 0;
        Guionar(
            r.Ctx, idPos: 880_950,
            antes: p =>
            {
                if (p.Metodo == HttpMethod.Post && p.Uri.AbsolutePath == "/v2/pos" && Interlocked.Increment(ref competidora) == 1)
                {
                    InsertarFilaAsync(r, r.Ctx.MercadoPago.IdUsuario, 999_001).GetAwaiter().GetResult();
                }

                return null;
            });

        await AssertCodigoAsync(
            await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null), HttpStatusCode.Conflict, "mercadopago_caja_duplicada");

        Assert.Equal(999_001, Assert.Single(await FilasAsync(r.IdPuntoVenta)).IdPos);
    }

    [Fact]
    public async Task LaBaseRechazaUnSegundoVinculoDelMismoPuntoDeVentaYCuentaConElIndiceUnico()
    {
        var r = await PrepararRegistrableAsync("mp-caja-unico");
        var idUsuario = NuevoIdUsuario();
        await InsertarFilaAsync(r, idUsuario, 1);

        var duplicada = await Assert.ThrowsAsync<PostgresException>(() => InsertarFilaAsync(r, idUsuario, 2));
        Assert.Equal("23505", duplicada.SqlState);
        Assert.Equal("ux_cajas_mercadopago_punto_venta_usuario", duplicada.ConstraintName);

        await InsertarFilaAsync(r, NuevoIdUsuario(), 3);
        Assert.Equal(2, (await FilasAsync(r.IdPuntoVenta)).Count);
    }

    [Fact]
    public async Task LaBaseRechazaUnExternalIdEnBlanco()
    {
        var r = await PrepararRegistrableAsync("mp-caja-blanco");

        var enBlanco = await Assert.ThrowsAsync<PostgresException>(() => InsertarFilaAsync(r, NuevoIdUsuario(), 1, externalId: "   "));
        Assert.Equal("23514", enBlanco.SqlState);
        Assert.Equal("ck_cajas_mercadopago_external_id", enBlanco.ConstraintName);
    }

    [Fact]
    public async Task LaBaseRechazaUnVinculoConUnLocalDeOtraEmpresaOUnPuntoDeVentaInexistente()
    {
        var a = await PrepararRegistrableAsync("mp-caja-fk");
        var (idOtraEmpresa, _) = await _apoyo.CrearOtraEmpresaConCuentaAsync(a.Ctx, "Otra SA");
        var respuesta = await a.Ctx.Admin.PostAsJsonAsync("/api/sucursales", AltaCompleta(idOtraEmpresa, "Sucursal ajena"));
        var idSucursalAjena = (await LeerAsync<SucursalListado>(respuesta, HttpStatusCode.Created)).Id;

        await using (var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null))
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText =
                "INSERT INTO locales_mercadopago (id_tenant, id_empresa, id_sucursal, id_usuario_mercadopago, id_store_mercadopago, " +
                "external_id, fecha_registro, fecha_sincronizacion, created_at, updated_at) " +
                "VALUES ($1, $2, $3, 1, 1, 'X', now(), now(), now(), now())";
            comando.Parameters.Add(new NpgsqlParameter { Value = a.Ctx.IdTenant });
            comando.Parameters.Add(new NpgsqlParameter { Value = idOtraEmpresa });
            comando.Parameters.Add(new NpgsqlParameter { Value = idSucursalAjena });
            await comando.ExecuteNonQueryAsync();
        }

        var idLocalAjeno = await IdLocalAsync(idSucursalAjena);

        // Punto de venta y empresa de la caja coinciden; el local es de otra empresa.
        var localAjeno = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarFilaAsync(a, NuevoIdUsuario(), 1, idLocal: idLocalAjeno));
        Assert.Equal("23503", localAjeno.SqlState);
        Assert.Equal("fk_cajas_mercadopago_local", localAjeno.ConstraintName);

        // El punto de venta se une solo por (id, tenant): la base no compara su empresa, la fija el servicio al leerlo.
        var puntoVentaInexistente = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarFilaAsync(a, NuevoIdUsuario(), 1, idPuntoVenta: 2_000_000_000));
        Assert.Equal("23503", puntoVentaInexistente.SqlState);
        Assert.Equal("fk_cajas_mercadopago_punto_venta", puntoVentaInexistente.ConstraintName);
    }

    // Clausula: la politica RLS de cajas_mercadopago.
    [Fact]
    public async Task LaPoliticaDeTenantEscondeElVinculoDeOtroTenantYImpideEscribirEnEl()
    {
        var a = await PrepararRegistrableAsync("mp-caja-rls-a");
        var b = await PrepararRegistrableAsync("mp-caja-rls-b");
        await InsertarFilaAsync(a, NuevoIdUsuario(), 42);

        async Task<long> ContarComoAsync(string modo, int? idTenant)
        {
            await using var conexion = await fixture.AbrirConexionCrudaAsync(modo, idTenant);
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT count(*) FROM cajas_mercadopago WHERE id_punto_venta = $1";
            comando.Parameters.Add(new NpgsqlParameter { Value = a.IdPuntoVenta });
            return (long)(await comando.ExecuteScalarAsync())!;
        }

        Assert.Equal(1, await ContarComoAsync("tenant", a.Ctx.IdTenant));
        Assert.Equal(0, await ContarComoAsync("tenant", b.Ctx.IdTenant));
        Assert.Equal(0, await ContarComoAsync("ninguno", null));

        await using var conexionB = await fixture.AbrirConexionCrudaAsync("tenant", b.Ctx.IdTenant);
        await using var insercion = conexionB.CreateCommand();
        insercion.CommandText =
            "INSERT INTO cajas_mercadopago (id_tenant, id_empresa, id_punto_venta, id_local_mercadopago, id_usuario_mercadopago, " +
            "id_store_mercadopago, id_pos_mercadopago, external_id, fecha_registro, fecha_sincronizacion, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, 7, 1, 7, 'X', now(), now(), now(), now())";
        insercion.Parameters.Add(new NpgsqlParameter { Value = a.Ctx.IdTenant });
        insercion.Parameters.Add(new NpgsqlParameter { Value = a.Ctx.IdEmpresa });
        insercion.Parameters.Add(new NpgsqlParameter { Value = a.IdPuntoVenta });
        insercion.Parameters.Add(new NpgsqlParameter { Value = await IdLocalAsync(a.IdSucursal) });

        var violacion = await Assert.ThrowsAsync<PostgresException>(() => insercion.ExecuteNonQueryAsync());
        Assert.Equal("42501", violacion.SqlState);
    }

    // ---- Autorización ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SoloElAdminPuedeConsultarYRegistrar()
    {
        var r = await PrepararRegistrableAsync("mp-caja-roles");
        Guionar(r.Ctx);
        var ruta = RutaDeCajas(r.IdPuntoVenta);

        foreach (var cliente in new[] { r.Ctx.Supervisor, r.Ctx.Vendedor })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.GetAsync(ruta)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.PostAsync(ruta, null)).StatusCode);
        }

        using var anonimo = r.Ctx.Host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync(ruta)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.PostAsync(ruta, null)).StatusCode);

        Assert.Empty(Llamadas(r.Ctx));
        Assert.Equal(HttpStatusCode.OK, (await r.Ctx.Admin.PostAsync(ruta, null)).StatusCode);
    }

    // ---- Terminal Point ----------------------------------------------------------------------------------------------------

    private static long _siguientePos = 770_000;

    private sealed record CajaConTerminales(Registrable R, long IdPos)
    {
        public Contexto Ctx => R.Ctx;
    }

    private const string Pax = "PAX_A910__8701";

    private const string Newland = "NEWLAND_N950__N950NCB123";

    private static string RutaDeTerminales(int idPuntoVenta) => $"/api/puntos-venta/{idPuntoVenta}/mercadopago/terminales";

    private static string RutaDeTerminal(int idPuntoVenta) => $"/api/puntos-venta/{idPuntoVenta}/mercadopago/terminal";

    private static bool EsDeTerminales(PedidoRegistrado pedido) =>
        EsDeLaApi(pedido) && pedido.Uri.AbsolutePath.StartsWith("/terminals/", StringComparison.Ordinal);

    private static IReadOnlyList<PedidoRegistrado> LlamadasDeTerminales(Contexto ctx) =>
        [.. Pedidos(ctx).Where(EsDeTerminales)];

    private static IReadOnlyList<PedidoRegistrado> Cambios(Contexto ctx) =>
        [.. LlamadasDeTerminales(ctx).Where(p => p.Metodo == HttpMethod.Patch)];

    private static string ListadoJson(params (string Id, long PosId, string Modo)[] terminales) =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                terminals = terminales.Select(t => new
                {
                    id = t.Id, pos_id = t.PosId, store_id = 1, external_pos_id = "WAYSPV1", operating_mode = t.Modo
                })
            },
            paging = new { total = terminales.Length, offset = 0, limit = 50 }
        });

    /// <summary>Guion de las rutas de Point: el listado responde <paramref name="listado"/> y el cambio de modo, un eco o lo que diga
    /// <paramref name="cambio"/>.</summary>
    private static Func<PedidoRegistrado, HttpResponseMessage?> Point(
        Func<string> listado, Func<PedidoRegistrado, HttpResponseMessage>? cambio = null) =>
        pedido => pedido.Uri.AbsolutePath switch
        {
            "/terminals/v1/list" => Json(HttpStatusCode.OK, listado()),
            "/terminals/v1/setup" => cambio?.Invoke(pedido) ?? Json(HttpStatusCode.OK, pedido.Cuerpo),
            _ => null
        };

    private async Task<CajaConTerminales> PrepararCajaAsync(string nombre)
    {
        var r = await PrepararRegistrableAsync(nombre);
        var idPos = Interlocked.Increment(ref _siguientePos);
        Guionar(r.Ctx, idPos: idPos);
        Assert.Equal(HttpStatusCode.OK, (await r.Ctx.Admin.PostAsync(RutaDeCajas(r.IdPuntoVenta), null)).StatusCode);
        Marcar(r.Ctx);
        return new CajaConTerminales(r, idPos);
    }

    private static Task<HttpResponseMessage> AsignarAsync(Contexto ctx, int idPuntoVenta, string? idTerminal) =>
        ctx.Admin.PostAsJsonAsync(RutaDeTerminal(idPuntoVenta), new SolicitudDeTerminalMercadoPago(idTerminal));

    private async Task<(string? IdTerminal, DateTimeOffset? Fecha)> TerminalGuardadaAsync(int idPuntoVenta)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT id_terminal_point, fecha_terminal FROM cajas_mercadopago WHERE id_punto_venta = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta });
        await using var lector = await comando.ExecuteReaderAsync();
        Assert.True(await lector.ReadAsync());
        return (
            lector.IsDBNull(0) ? null : lector.GetString(0),
            lector.IsDBNull(1) ? null : lector.GetFieldValue<DateTimeOffset>(1));
    }

    private async Task<List<(string? Anterior, string Nuevo)>> AuditoriaDeTerminalAsync(int idPuntoVenta)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT valor_anterior::text, valor_nuevo::text FROM auditoria WHERE accion = 'pv.terminalmercadopago' " +
            "AND id_entidad = $1 ORDER BY id_auditoria";
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta });

        var filas = new List<(string?, string)>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            filas.Add((lector.IsDBNull(0) ? null : lector.GetString(0), lector.GetString(1)));
        }

        return filas;
    }

    [Fact]
    public async Task UnPuntoDeVentaInexistenteContesta404AntesQueElCuerpoEnLasTresRutasDeTerminal()
    {
        var ctx = await _apoyo.PrepararAsync("mp-term-404");

        await AssertCodigoAsync(await ctx.Admin.GetAsync(RutaDeTerminales(987_654)), HttpStatusCode.NotFound, "no_encontrado");
        await AssertCodigoAsync(await AsignarAsync(ctx, 987_654, "no es una terminal"), HttpStatusCode.NotFound, "no_encontrado");
        await AssertCodigoAsync(await ctx.Admin.PostAsync(RutaDeTerminal(987_654), null), HttpStatusCode.NotFound, "no_encontrado");
        await AssertCodigoAsync(await ctx.Admin.DeleteAsync(RutaDeTerminal(987_654)), HttpStatusCode.NotFound, "no_encontrado");
        Assert.Empty(Llamadas(ctx));
    }

    [Fact]
    public async Task UnPuntoDeVentaDeOtroTenantContesta404EnLasTresRutasDeTerminalSinTocarSuCaja()
    {
        var a = await ConectarAsync("mp-term-aislada-a");
        var b = await PrepararCajaAsync("mp-term-aislada-b");

        await AssertCodigoAsync(await a.Admin.GetAsync(RutaDeTerminales(b.R.IdPuntoVenta)), HttpStatusCode.NotFound, "no_encontrado");
        await AssertCodigoAsync(await AsignarAsync(a, b.R.IdPuntoVenta, Pax), HttpStatusCode.NotFound, "no_encontrado");
        await AssertCodigoAsync(await a.Admin.DeleteAsync(RutaDeTerminal(b.R.IdPuntoVenta)), HttpStatusCode.NotFound, "no_encontrado");
        Assert.Empty(Llamadas(a));
        Assert.Null((await TerminalGuardadaAsync(b.R.IdPuntoVenta)).IdTerminal);
    }

    [Fact]
    public async Task SinConexionListarYAsignarSon409YQuitarEsUnNoOpSinLlamarAMercadoPago()
    {
        var ctx = await _apoyo.PrepararAsync("mp-term-sin-conexion");
        var idSucursal = await CrearSucursalAsync(ctx, "Sucursal Sur");
        var idPuntoVenta = await CrearPuntoVentaAsync(ctx, "Caja Sur", idSucursal);

        await AssertCodigoAsync(await ctx.Admin.GetAsync(RutaDeTerminales(idPuntoVenta)), HttpStatusCode.Conflict, CodigosDeErrorDeMercadoPago.NoConectada);
        await AssertCodigoAsync(await AsignarAsync(ctx, idPuntoVenta, Pax), HttpStatusCode.Conflict, CodigosDeErrorDeMercadoPago.NoConectada);
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.DeleteAsync(RutaDeTerminal(idPuntoVenta))).StatusCode);
        Assert.Empty(Llamadas(ctx));
    }

    [Fact]
    public async Task SiElPuntoDeVentaNoEsUnaCajaListarYAsignarSon409CajaNoRegistradaYQuitarEsUnNoOp()
    {
        var r = await PrepararRegistrableAsync("mp-term-sin-caja");
        Guionar(r.Ctx, antes: Point(() => ListadoJson((Pax, 1, "STANDALONE"))));

        await AssertCodigoAsync(
            await r.Ctx.Admin.GetAsync(RutaDeTerminales(r.IdPuntoVenta)), HttpStatusCode.Conflict, CodigosDeErrorDeMercadoPago.CajaNoRegistrada);
        await AssertCodigoAsync(
            await AsignarAsync(r.Ctx, r.IdPuntoVenta, Pax), HttpStatusCode.Conflict, CodigosDeErrorDeMercadoPago.CajaNoRegistrada);
        Assert.Equal(HttpStatusCode.OK, (await r.Ctx.Admin.DeleteAsync(RutaDeTerminal(r.IdPuntoVenta))).StatusCode);
        Assert.Empty(LlamadasDeTerminales(r.Ctx));
        Assert.Empty(await FilasAsync(r.IdPuntoVenta));
    }

    [Fact]
    public async Task ElListadoPideLasTerminalesDeLaCajaMarcaLaAsignadaYDescartaLasDeOtraCaja()
    {
        var c = await PrepararCajaAsync("mp-term-listado");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "PDV"), (Newland, c.IdPos, "STANDALONE"), ("PAX_A910__9999", c.IdPos + 1, "PDV"))));
        Assert.Equal(HttpStatusCode.OK, (await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax)).StatusCode);
        Marcar(c.Ctx);

        var listado = await LeerAsync<ListadoDeTerminalesDelPuntoDeVenta>(await c.Ctx.Admin.GetAsync(RutaDeTerminales(c.R.IdPuntoVenta)));

        Assert.Equal(
            [new TerminalDelPuntoDeVenta(Pax, "PDV", true), new TerminalDelPuntoDeVenta(Newland, "STANDALONE", false)],
            listado.Terminales);
        Assert.Null(listado.Instrucciones);
        var pedido = Assert.Single(LlamadasDeTerminales(c.Ctx));
        Assert.Equal(HttpMethod.Get, pedido.Metodo);
        Assert.Equal($"?pos_id={c.IdPos}", pedido.Uri.Query);
    }

    [Fact]
    public async Task SinTerminalesVinculadasElListadoTraeLasInstruccionesParaVincularlasEnLaApp()
    {
        var c = await PrepararCajaAsync("mp-term-vacio");
        Guionar(c.Ctx, antes: Point(() => ListadoJson()));

        var listado = await LeerAsync<ListadoDeTerminalesDelPuntoDeVenta>(await c.Ctx.Admin.GetAsync(RutaDeTerminales(c.R.IdPuntoVenta)));

        Assert.Empty(listado.Terminales);
        Assert.Contains("app de Mercado Pago", listado.Instrucciones);
        Assert.Contains("QR", listado.Instrucciones);
    }

    // Clausula: el cuerpo tiene un solo campo, obligatorio, con la forma TIPO__SERIE y hasta 80 caracteres.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pax_a910__8701")]
    [InlineData("PAX_A910_8701")]
    [InlineData("PAX_A910__87 01")]
    [InlineData("PAX_A910__8701; DROP")]
    public async Task UnIdentificadorDeTerminalInvalidoEsUn400SinLlamarAMercadoPago(string? idTerminal)
    {
        var c = await PrepararCajaAsync("mp-term-dto-" + Guid.NewGuid().ToString("N")[..8]);
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "STANDALONE"))));

        await AssertCodigoAsync(
            await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, idTerminal), HttpStatusCode.BadRequest, TerminalPointMercadoPago.CodigoDeIdInvalido);
        Assert.Empty(LlamadasDeTerminales(c.Ctx));
        Assert.Null((await TerminalGuardadaAsync(c.R.IdPuntoVenta)).IdTerminal);
    }

    [Fact]
    public async Task UnCuerpoSinElCampoOMasLargoQueElMaximoEsUn400SinLlamarAMercadoPago()
    {
        var c = await PrepararCajaAsync("mp-term-dto-largo");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "STANDALONE"))));
        var ruta = RutaDeTerminal(c.R.IdPuntoVenta);

        var sinCampo = await c.Ctx.Admin.PostAsync(ruta, new StringContent("{}", Encoding.UTF8, "application/json"));
        await AssertCodigoAsync(sinCampo, HttpStatusCode.BadRequest, TerminalPointMercadoPago.CodigoDeIdInvalido);

        var sinCuerpo = await c.Ctx.Admin.PostAsync(ruta, null);
        await AssertCodigoAsync(sinCuerpo, HttpStatusCode.BadRequest, TerminalPointMercadoPago.CodigoDeIdInvalido);

        var largo = "PAX__" + new string('A', 80);
        await AssertCodigoAsync(await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, largo), HttpStatusCode.BadRequest, TerminalPointMercadoPago.CodigoDeIdInvalido);
        Assert.Empty(LlamadasDeTerminales(c.Ctx));
    }

    // Clausula: la terminal tiene que figurar en el listado de la caja de este punto de venta, con el pos_id de esa caja.
    [Fact]
    public async Task UnaTerminalQueNoFiguraEnElListadoDeLaCajaEs409SinCambiarElModoNiGuardarNada()
    {
        var c = await PrepararCajaAsync("mp-term-no-vinculada");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Newland, c.IdPos, "STANDALONE"))));

        var respuesta = await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax);

        await AssertCodigoAsync(respuesta, HttpStatusCode.Conflict, CodigosDeErrorDeMercadoPago.TerminalNoVinculada);
        Assert.Contains("app de Mercado Pago", await respuesta.Content.ReadAsStringAsync());
        Assert.Empty(Cambios(c.Ctx));
        Assert.Null((await TerminalGuardadaAsync(c.R.IdPuntoVenta)).IdTerminal);
        Assert.Empty(await AuditoriaDeTerminalAsync(c.R.IdPuntoVenta));
    }

    [Fact]
    public async Task UnaTerminalDeOtraCajaQueMercadoPagoDevuelveIgualNoSeAsigna()
    {
        var c = await PrepararCajaAsync("mp-term-otra-caja");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos + 1, "STANDALONE"))));

        await AssertCodigoAsync(
            await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax), HttpStatusCode.Conflict, CodigosDeErrorDeMercadoPago.TerminalNoVinculada);
        Assert.Empty(Cambios(c.Ctx));
        Assert.Null((await TerminalGuardadaAsync(c.R.IdPuntoVenta)).IdTerminal);
    }

    [Fact]
    public async Task UnaTerminalFueraDePdvSePasaAPdvConUnaClaveDeIdempotenciaYSeGuardaConSuAuditoria()
    {
        var c = await PrepararCajaAsync("mp-term-asigna");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "STANDALONE"))));

        var resultado = await LeerAsync<AsignacionDeTerminalMercadoPago>(await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, $"  {Pax} "));

        Assert.Equal(Pax, resultado.IdTerminalPoint);
        Assert.NotNull(resultado.FechaTerminal);
        Assert.True(resultado.ModoCambiado);
        var cambio = Assert.Single(Cambios(c.Ctx));
        Assert.Equal("/terminals/v1/setup", cambio.Uri.AbsolutePath);
        using (var cuerpo = JsonDocument.Parse(cambio.Cuerpo))
        {
            var terminal = Assert.Single(cuerpo.RootElement.GetProperty("terminals").EnumerateArray());
            Assert.Equal(Pax, terminal.GetProperty("id").GetString());
            Assert.Equal("PDV", terminal.GetProperty("operating_mode").GetString());
        }

        Assert.StartsWith($"ways-terminal-{c.R.IdPuntoVenta}-", cambio.ClaveDeIdempotencia);
        Assert.InRange(cambio.ClaveDeIdempotencia!.Length, 1, 64);
        Assert.Equal($"Bearer {AccessToken}", cambio.Autorizacion);

        var (guardada, fecha) = await TerminalGuardadaAsync(c.R.IdPuntoVenta);
        Assert.Equal(Pax, guardada);
        Assert.Equal(resultado.FechaTerminal!.Value.ToUnixTimeMilliseconds(), fecha!.Value.ToUnixTimeMilliseconds());

        var auditoria = Assert.Single(await AuditoriaDeTerminalAsync(c.R.IdPuntoVenta));
        using var nuevo = JsonDocument.Parse(auditoria.Nuevo);
        Assert.Equal("asignacion", nuevo.RootElement.GetProperty("operacion").GetString());
        Assert.Equal(Pax, nuevo.RootElement.GetProperty("id_terminal_point").GetString());
        Assert.True(nuevo.RootElement.GetProperty("modo_cambiado").GetBoolean());
        Assert.DoesNotContain(AccessToken, auditoria.Nuevo);
        Assert.DoesNotContain("token", auditoria.Nuevo, StringComparison.OrdinalIgnoreCase);

        var estado = await LeerAsync<EstadoDeCajaMercadoPago>(await c.Ctx.Admin.GetAsync(RutaDeCajas(c.R.IdPuntoVenta)));
        Assert.Equal(Pax, estado.IdTerminalPoint);
    }

    // Clausula: una terminal que ya esta en PDV no se vuelve a mandar a PDV.
    [Fact]
    public async Task UnaTerminalQueYaEstaEnPdvSeGuardaSinMandarElCambioDeModo()
    {
        var c = await PrepararCajaAsync("mp-term-ya-pdv");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "PDV"))));

        var resultado = await LeerAsync<AsignacionDeTerminalMercadoPago>(await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax));

        Assert.Equal(Pax, resultado.IdTerminalPoint);
        Assert.False(resultado.ModoCambiado);
        Assert.Empty(Cambios(c.Ctx));
        Assert.Equal(Pax, (await TerminalGuardadaAsync(c.R.IdPuntoVenta)).IdTerminal);
        using var nuevo = JsonDocument.Parse(Assert.Single(await AuditoriaDeTerminalAsync(c.R.IdPuntoVenta)).Nuevo);
        Assert.False(nuevo.RootElement.GetProperty("modo_cambiado").GetBoolean());
    }

    [Fact]
    public async Task AsignarDeNuevoLaMismaTerminalEnPdvNoCambiaNadaNiAuditaNiLlamaAlCambioDeModo()
    {
        var c = await PrepararCajaAsync("mp-term-repetida");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "STANDALONE"))));
        var primera = await LeerAsync<AsignacionDeTerminalMercadoPago>(await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax));
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "PDV"))));

        var segunda = await LeerAsync<AsignacionDeTerminalMercadoPago>(await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax));

        Assert.Single(Cambios(c.Ctx));
        Assert.Equal(Pax, segunda.IdTerminalPoint);
        Assert.False(segunda.ModoCambiado);
        Assert.Equal(primera.FechaTerminal!.Value.ToUnixTimeMilliseconds(), segunda.FechaTerminal!.Value.ToUnixTimeMilliseconds());
        Assert.Single(await AuditoriaDeTerminalAsync(c.R.IdPuntoVenta));
    }

    [Fact]
    public async Task ElegirOtraTerminalLaReemplazaYLaAuditoriaGuardaLaAnterior()
    {
        var c = await PrepararCajaAsync("mp-term-reemplazo");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "PDV"), (Newland, c.IdPos, "PDV"))));

        Assert.Equal(HttpStatusCode.OK, (await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Newland)).StatusCode);

        Assert.Equal(Newland, (await TerminalGuardadaAsync(c.R.IdPuntoVenta)).IdTerminal);
        var auditoria = await AuditoriaDeTerminalAsync(c.R.IdPuntoVenta);
        Assert.Equal(2, auditoria.Count);
        using var anterior = JsonDocument.Parse(auditoria[1].Anterior!);
        Assert.Equal(Pax, anterior.RootElement.GetProperty("id_terminal_point").GetString());
    }

    // Clausula: los rechazos del cambio de modo son 409 con codigo propio (403 modelo, 403 sin local ni caja, 412 otra terminal).
    [Theory]
    [InlineData(403, "terminal_not_allowed_action", CodigosDeErrorDeMercadoPago.TerminalModeloNoAdmitido, 409)]
    [InlineData(403, "store_pos_not_found", CodigosDeErrorDeMercadoPago.TerminalNoVinculada, 409)]
    [InlineData(412, "precondition_failed", CodigosDeErrorDeMercadoPago.CajaConOtraTerminalEnPdv, 409)]
    [InlineData(412, null, CodigosDeErrorDeMercadoPago.CajaConOtraTerminalEnPdv, 409)]
    [InlineData(403, "otro_codigo", CodigosDeErrorDeMercadoPago.SolicitudRechazada, 502)]
    [InlineData(400, "invalid_mode", CodigosDeErrorDeMercadoPago.SolicitudRechazada, 502)]
    public async Task LosRechazosDelCambioDeModoSeMapeanAUn409ConCodigoPropioYNoGuardanNada(
        int estadoDeMercadoPago, string? codigoDeMercadoPago, string codigoEsperado, int estadoEsperado)
    {
        var c = await PrepararCajaAsync($"mp-term-rechazo-{estadoDeMercadoPago}-{codigoDeMercadoPago}");
        var cuerpo = codigoDeMercadoPago is null
            ? """{"message":"x"}"""
            : $$"""{"error":"{{codigoDeMercadoPago}}","message":"rechazado"}""";
        Guionar(
            c.Ctx,
            antes: Point(
                () => ListadoJson((Pax, c.IdPos, "STANDALONE")),
                _ => Json((HttpStatusCode)estadoDeMercadoPago, cuerpo)));

        var respuesta = await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax);

        await AssertCodigoAsync(respuesta, (HttpStatusCode)estadoEsperado, codigoEsperado);
        Assert.Single(Cambios(c.Ctx));
        Assert.Null((await TerminalGuardadaAsync(c.R.IdPuntoVenta)).IdTerminal);
        Assert.Empty(await AuditoriaDeTerminalAsync(c.R.IdPuntoVenta));
    }

    [Fact]
    public async Task ReintentarUnCambioDeModoFallidoRepiteLaMismaClaveDeIdempotencia()
    {
        var c = await PrepararCajaAsync("mp-term-clave");
        Guionar(
            c.Ctx,
            antes: Point(() => ListadoJson((Pax, c.IdPos, "STANDALONE")), _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        await AssertCodigoAsync(
            await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax), HttpStatusCode.ServiceUnavailable, CodigosDeErrorDeMercadoPago.NoDisponible);
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "STANDALONE"))));
        Assert.Equal(HttpStatusCode.OK, (await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax)).StatusCode);

        var cambios = Cambios(c.Ctx);
        Assert.Equal(2, cambios.Count);
        Assert.NotEqual(cambios[0].ClaveDeIdempotencia, cambios[1].ClaveDeIdempotencia);
    }

    // Clausula: la clave del cambio de modo es nueva en cada intento, para que un cambio posterior a una vuelta a STANDALONE se aplique.
    [Fact]
    public async Task DosAsignacionesTrasVolverALaTerminalAStandaloneUsanClavesDistintasYLaClaveEntraEnElLimite()
    {
        var c = await PrepararCajaAsync("mp-term-clave-nueva");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "STANDALONE"))));
        Assert.Equal(HttpStatusCode.OK, (await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.Ctx.Admin.DeleteAsync(RutaDeTerminal(c.R.IdPuntoVenta))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax)).StatusCode);

        var cambios = Cambios(c.Ctx);
        Assert.Equal(2, cambios.Count);
        Assert.NotEqual(cambios[0].ClaveDeIdempotencia, cambios[1].ClaveDeIdempotencia);
        Assert.All(cambios, x => Assert.InRange(x.ClaveDeIdempotencia!.Length, 1, 64));
    }

    // Clausula: si el eco del cambio trae la terminal en otro modo que PDV, no se guarda la asignacion.
    [Fact]
    public async Task UnEcoDelCambioDeModoQueNoEstaEnPdvEs502SinGuardarLaAsignacion()
    {
        var c = await PrepararCajaAsync("mp-term-eco-standalone");
        Guionar(
            c.Ctx,
            antes: Point(
                () => ListadoJson((Pax, c.IdPos, "STANDALONE")),
                _ => Json(HttpStatusCode.OK, $$"""{"terminals":[{"id":"{{Pax}}","operating_mode":"STANDALONE"}]}""")));

        var respuesta = await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax);

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadGateway, CodigosDeErrorDeMercadoPago.RespuestaInvalida);
        Assert.Null((await TerminalGuardadaAsync(c.R.IdPuntoVenta)).IdTerminal);
        Assert.Empty(await AuditoriaDeTerminalAsync(c.R.IdPuntoVenta));
    }

    // Clausula: el listado informa la asignacion guardada y si sigue entre las terminales de la caja.
    [Fact]
    public async Task ElListadoInformaLaTerminalAsignadaYSiSigueVigente()
    {
        var c = await PrepararCajaAsync("mp-term-vigente");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "PDV"))));
        var sinAsignar = await LeerAsync<ListadoDeTerminalesDelPuntoDeVenta>(await c.Ctx.Admin.GetAsync(RutaDeTerminales(c.R.IdPuntoVenta)));
        Assert.Null(sinAsignar.IdTerminalAsignada);
        Assert.False(sinAsignar.AsignadaVigente);
        Assert.Equal(HttpStatusCode.OK, (await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax)).StatusCode);

        var vigente = await LeerAsync<ListadoDeTerminalesDelPuntoDeVenta>(await c.Ctx.Admin.GetAsync(RutaDeTerminales(c.R.IdPuntoVenta)));
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Newland, c.IdPos, "PDV"))));
        var vieja = await LeerAsync<ListadoDeTerminalesDelPuntoDeVenta>(await c.Ctx.Admin.GetAsync(RutaDeTerminales(c.R.IdPuntoVenta)));

        Assert.Equal(Pax, vigente.IdTerminalAsignada);
        Assert.True(vigente.AsignadaVigente);
        Assert.Equal(Pax, vieja.IdTerminalAsignada);
        Assert.False(vieja.AsignadaVigente);
        Assert.DoesNotContain(vieja.Terminales, t => t.Asignada);
    }

    // Clausula: si la caja cambia de pos_id (recreada), la terminal asignada a la caja vieja se limpia y se audita.
    [Fact]
    public async Task SiElRegistroRecreaLaCajaConOtroPosLaTerminalAsignadaSeLimpia()
    {
        var c = await PrepararCajaAsync("mp-term-caja-recreada");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "PDV"))));
        Assert.Equal(HttpStatusCode.OK, (await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax)).StatusCode);
        Guionar(
            c.Ctx, idPos: c.IdPos + 500_000,
            antes: p => p.Metodo == HttpMethod.Patch && p.Uri.AbsolutePath == $"/v2/pos/{c.IdPos}" ? Json(HttpStatusCode.NotFound, """{"error": "not_found"}""") : null);

        var estado = await LeerAsync<EstadoDeCajaMercadoPago>(await c.Ctx.Admin.PostAsync(RutaDeCajas(c.R.IdPuntoVenta), null));

        Assert.Equal(c.IdPos + 500_000, estado.IdPosMercadoPago);
        Assert.Null(estado.IdTerminalPoint);
        Assert.Equal((null, null), await TerminalGuardadaAsync(c.R.IdPuntoVenta));
        var auditoria = await AuditoriaDeTerminalAsync(c.R.IdPuntoVenta);
        Assert.Equal(2, auditoria.Count);
        using var desasignada = JsonDocument.Parse(auditoria[1].Nuevo);
        Assert.Equal("desasignacion", desasignada.RootElement.GetProperty("operacion").GetString());
    }

    // Clausula: quitar la terminal solo limpia datos de Ways y no pide una conexion activa.
    [Fact]
    public async Task QuitarLaTerminalFuncionaConLaConexionEnRequiereReconexion()
    {
        var c = await PrepararCajaAsync("mp-term-quitar-reconexion");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "PDV"))));
        Assert.Equal(HttpStatusCode.OK, (await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax)).StatusCode);
        Guionar(c.Ctx, antes: p => EsDeTerminales(p) ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : null);
        await AssertCodigoAsync(
            await c.Ctx.Admin.GetAsync(RutaDeTerminales(c.R.IdPuntoVenta)), HttpStatusCode.Conflict, CodigosDeErrorDeMercadoPago.RequiereReconexion);
        Marcar(c.Ctx);

        var respuesta = await c.Ctx.Admin.DeleteAsync(RutaDeTerminal(c.R.IdPuntoVenta));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal((null, null), await TerminalGuardadaAsync(c.R.IdPuntoVenta));
        Assert.Empty(Llamadas(c.Ctx));
    }

    [Fact]
    public async Task UnCambioDeModoQueSeAplicoPeroNoSeGuardoSeCompletaAlReintentarSinOtroCambio()
    {
        var c = await PrepararCajaAsync("mp-term-reintento");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "PDV"))));

        Assert.Equal(HttpStatusCode.OK, (await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax)).StatusCode);

        Assert.Empty(Cambios(c.Ctx));
        Assert.Equal(Pax, (await TerminalGuardadaAsync(c.R.IdPuntoVenta)).IdTerminal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnoDe401EnLasTerminalesDejaLaConexionEnRequiereReconexion(bool alAsignar)
    {
        var c = await PrepararCajaAsync("mp-term-401-" + alAsignar);
        Guionar(c.Ctx, antes: p => EsDeTerminales(p) ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : null);

        var respuesta = alAsignar
            ? await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax)
            : await c.Ctx.Admin.GetAsync(RutaDeTerminales(c.R.IdPuntoVenta));

        await AssertCodigoAsync(respuesta, HttpStatusCode.Conflict, CodigosDeErrorDeMercadoPago.RequiereReconexion);
        var conexion = Assert.Single(await _apoyo.FilasAsync(c.Ctx.IdEmpresa), f => f.Estado != "desconectada");
        Assert.Equal("requiere_reconexion", conexion.Estado);
        Assert.Null(conexion.AccessCifrado);
        Assert.Null((await TerminalGuardadaAsync(c.R.IdPuntoVenta)).IdTerminal);
    }

    // Clausula: quitar la terminal solo borra la asignacion en Ways; no llama a Mercado Pago.
    [Fact]
    public async Task QuitarLaTerminalBorraLaAsignacionDeWaysSinLlamarAMercadoPagoYEsIdempotente()
    {
        var c = await PrepararCajaAsync("mp-term-quitar");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "STANDALONE"))));
        Assert.Equal(HttpStatusCode.OK, (await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax)).StatusCode);
        Marcar(c.Ctx);

        var resultado = await LeerAsync<AsignacionDeTerminalMercadoPago>(await c.Ctx.Admin.DeleteAsync(RutaDeTerminal(c.R.IdPuntoVenta)));
        var otra = await LeerAsync<AsignacionDeTerminalMercadoPago>(await c.Ctx.Admin.DeleteAsync(RutaDeTerminal(c.R.IdPuntoVenta)));

        Assert.Null(resultado.IdTerminalPoint);
        Assert.Null(otra.IdTerminalPoint);
        Assert.Empty(Llamadas(c.Ctx));
        Assert.Equal((null, null), await TerminalGuardadaAsync(c.R.IdPuntoVenta));
        var filas = Assert.Single(await FilasAsync(c.R.IdPuntoVenta));
        Assert.Equal(c.IdPos, filas.IdPos);

        var auditoria = await AuditoriaDeTerminalAsync(c.R.IdPuntoVenta);
        Assert.Equal(2, auditoria.Count);
        using var desasignada = JsonDocument.Parse(auditoria[1].Nuevo);
        Assert.Equal("desasignacion", desasignada.RootElement.GetProperty("operacion").GetString());
        using var anterior = JsonDocument.Parse(auditoria[1].Anterior!);
        Assert.Equal(Pax, anterior.RootElement.GetProperty("id_terminal_point").GetString());
        var estado = await LeerAsync<EstadoDeCajaMercadoPago>(await c.Ctx.Admin.GetAsync(RutaDeCajas(c.R.IdPuntoVenta)));
        Assert.Null(estado.IdTerminalPoint);
    }

    [Fact]
    public async Task RegistrarDeNuevoLaCajaConservaLaTerminalAsignada()
    {
        var c = await PrepararCajaAsync("mp-term-conserva");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "PDV"))));
        Assert.Equal(HttpStatusCode.OK, (await AsignarAsync(c.Ctx, c.R.IdPuntoVenta, Pax)).StatusCode);

        var estado = await LeerAsync<EstadoDeCajaMercadoPago>(await c.Ctx.Admin.PostAsync(RutaDeCajas(c.R.IdPuntoVenta), null));

        Assert.Equal(Pax, estado.IdTerminalPoint);
        Assert.Equal(Pax, (await TerminalGuardadaAsync(c.R.IdPuntoVenta)).IdTerminal);
    }

    [Fact]
    public async Task LaBaseRechazaUnaTerminalSinFechaOUnaFechaSinTerminalOUnaTerminalEnBlanco()
    {
        var c = await PrepararCajaAsync("mp-term-check");

        foreach (var asignacion in new[]
        {
            "id_terminal_point = 'PAX_A910__1'", "fecha_terminal = now()", "id_terminal_point = '  ', fecha_terminal = now()"
        })
        {
            var violacion = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
                await using var comando = conexion.CreateCommand();
                comando.CommandText = $"UPDATE cajas_mercadopago SET {asignacion} WHERE id_punto_venta = $1";
                comando.Parameters.Add(new NpgsqlParameter { Value = c.R.IdPuntoVenta });
                await comando.ExecuteNonQueryAsync();
            });
            Assert.Equal("23514", violacion.SqlState);
            Assert.Equal("ck_cajas_mercadopago_terminal", violacion.ConstraintName);
        }

        await using var ok = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var bueno = ok.CreateCommand();
        bueno.CommandText =
            "UPDATE cajas_mercadopago SET id_terminal_point = 'PAX_A910__1', fecha_terminal = now() WHERE id_punto_venta = $1";
        bueno.Parameters.Add(new NpgsqlParameter { Value = c.R.IdPuntoVenta });
        Assert.Equal(1, await bueno.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task SoloElAdminPuedeConsultarAsignarYQuitarLaTerminal()
    {
        var c = await PrepararCajaAsync("mp-term-roles");
        Guionar(c.Ctx, antes: Point(() => ListadoJson((Pax, c.IdPos, "STANDALONE"))));
        var idPuntoVenta = c.R.IdPuntoVenta;
        var cuerpo = JsonContent.Create(new SolicitudDeTerminalMercadoPago(Pax));

        foreach (var cliente in new[] { c.Ctx.Supervisor, c.Ctx.Vendedor })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.GetAsync(RutaDeTerminales(idPuntoVenta))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.PostAsync(RutaDeTerminal(idPuntoVenta), cuerpo)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await cliente.DeleteAsync(RutaDeTerminal(idPuntoVenta))).StatusCode);
        }

        using var anonimo = c.Ctx.Host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync(RutaDeTerminales(idPuntoVenta))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.DeleteAsync(RutaDeTerminal(idPuntoVenta))).StatusCode);

        Assert.Empty(Llamadas(c.Ctx));
        Assert.Equal(HttpStatusCode.OK, (await AsignarAsync(c.Ctx, idPuntoVenta, Pax)).StatusCode);
    }
}
