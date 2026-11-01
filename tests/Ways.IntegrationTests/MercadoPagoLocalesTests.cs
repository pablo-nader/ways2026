using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using Ways.Application.MercadoPago;
using Ways.Application.Organizacion;
using static Ways.IntegrationTests.ApoyoDeMercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// El registro de una sucursal como local (<i>store</i>) de Mercado Pago contra Postgres real y el manejador falso de Mercado Pago:
/// alta, adopción por <c>external_id</c>, actualización, estados de la consulta, rechazos de Mercado Pago, el 401 que deja la
/// conexión en <c>requiere_reconexion</c>, el lock por sucursal y los backstops de la tabla <c>locales_mercadopago</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MercadoPagoLocalesTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private readonly ApoyoDeMercadoPago _apoyo = new(fixture);

    private sealed record Registrable(Contexto Ctx, int IdSucursal, string ExternalId);

    private sealed record FilaDeLocal(
        int Id, int IdTenant, int IdEmpresa, int IdSucursal, long IdUsuarioMercadoPago, long IdStore, string ExternalId,
        DateTimeOffset FechaRegistro, DateTimeOffset FechaSincronizacion);

    private static HttpResponseMessage Json(HttpStatusCode estado, string json) =>
        new(estado) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string RutaDeLocales(int idSucursal) => $"/api/sucursales/{idSucursal}/mercadopago";

    private static bool EsDeLaApi(PedidoRegistrado pedido) =>
        !pedido.Uri.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal);

    private static IReadOnlyList<PedidoRegistrado> Llamadas(Contexto ctx) =>
        [.. ctx.MercadoPago.Pedidos.Where(EsDeLaApi)];

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta, HttpStatusCode esperado = HttpStatusCode.OK)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {cuerpo}");
        return JsonSerializer.Deserialize<T>(cuerpo, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task AssertCodigoAsync(HttpResponseMessage respuesta, HttpStatusCode estado, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == estado, $"{respuesta.StatusCode}: {cuerpo}");
        Assert.Equal(codigo, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private static SucursalAlta AltaCompleta(int idEmpresa, string nombre = "Sucursal Centro") => new(
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

    private static async Task<int> CrearSucursalAsync(Contexto ctx, SucursalAlta alta)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/sucursales", alta);
        return (await LeerAsync<SucursalListado>(respuesta, HttpStatusCode.Created)).Id;
    }

    private async Task<Registrable> PrepararRegistrableAsync(string nombre)
    {
        var ctx = await ConectarAsync(nombre);
        var idSucursal = await CrearSucursalAsync(ctx, AltaCompleta(ctx.IdEmpresa));
        return new Registrable(ctx, idSucursal, $"WAYSS{idSucursal}");
    }

    /// <summary>La búsqueda por <c>external_id</c> responde <paramref name="busqueda"/>, el alta <paramref name="idStore"/> y la
    /// actualización un 200.</summary>
    private static void Guionar(Contexto ctx, string busqueda, long idStore = 555_001, Func<PedidoRegistrado, HttpResponseMessage?>? antes = null) =>
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

            if (pedido.Metodo == HttpMethod.Get && pedido.Uri.AbsolutePath.EndsWith("/stores/search", StringComparison.Ordinal))
            {
                return busqueda == BusquedaVacia
                    ? Json(HttpStatusCode.NotFound, """{"error": "store_not_found", "message": "store not found"}""")
                    : Json(HttpStatusCode.OK, busqueda);
            }

            if (pedido.Metodo == HttpMethod.Post)
            {
                return Json(HttpStatusCode.Created, $$"""{"id": {{idStore}}, "name": "x", "external_id": "x", "otro_campo": [1, 2]}""");
            }

            if (pedido.Metodo == HttpMethod.Put)
            {
                return Json(HttpStatusCode.OK, $$"""{"id": {{idStore}}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

    /// <summary>"Sin coincidencias": Mercado Pago contesta 404 <c>store_not_found</c>, no una lista vacía (<see cref="Guionar"/> lo traduce).</summary>
    private const string BusquedaVacia = "sin-coincidencias";

    private const string BusquedaConListaVacia = """{"paging": {"total": 0, "offset": 0, "limit": 30}, "results": []}""";

    private async Task<List<FilaDeLocal>> FilasAsync(int idSucursal)
    {
        var filas = new List<FilaDeLocal>();
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT id_local_mercadopago, id_tenant, id_empresa, id_sucursal, id_usuario_mercadopago, id_store_mercadopago, " +
            "external_id, fecha_registro, fecha_sincronizacion FROM locales_mercadopago WHERE id_sucursal = $1 ORDER BY 1";
        comando.Parameters.Add(new NpgsqlParameter { Value = idSucursal });

        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            filas.Add(new FilaDeLocal(
                lector.GetInt32(0), lector.GetInt32(1), lector.GetInt32(2), lector.GetInt32(3), lector.GetInt64(4),
                lector.GetInt64(5), lector.GetString(6), lector.GetFieldValue<DateTimeOffset>(7),
                lector.GetFieldValue<DateTimeOffset>(8)));
        }

        return filas;
    }

    private async Task InsertarFilaAsync(Registrable r, long idUsuarioMercadoPago, long idStore, string? externalId = null)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "INSERT INTO locales_mercadopago (id_tenant, id_empresa, id_sucursal, id_usuario_mercadopago, id_store_mercadopago, " +
            "external_id, fecha_registro, fecha_sincronizacion, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, now(), now(), now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = r.Ctx.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = r.Ctx.IdEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = r.IdSucursal });
        comando.Parameters.Add(new NpgsqlParameter { Value = idUsuarioMercadoPago });
        comando.Parameters.Add(new NpgsqlParameter { Value = idStore });
        comando.Parameters.Add(new NpgsqlParameter { Value = externalId ?? r.ExternalId });
        await comando.ExecuteNonQueryAsync();
    }

    private async Task<(string Valor, int Cantidad)> AuditoriaAsync(int idSucursal)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT valor_nuevo::text FROM auditoria WHERE accion = 'sucursal.mercadopago' AND id_entidad = $1 ORDER BY id_auditoria DESC";
        comando.Parameters.Add(new NpgsqlParameter { Value = idSucursal });

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
    public async Task UnaSucursalInexistenteContesta404AntesQueCualquierOtraCosaEnLasDosRutas()
    {
        var ctx = await _apoyo.PrepararAsync("mp-loc-404");

        await AssertCodigoAsync(await ctx.Admin.GetAsync(RutaDeLocales(987_654)), HttpStatusCode.NotFound, "no_encontrado");
        await AssertCodigoAsync(await ctx.Admin.PostAsync(RutaDeLocales(987_654), null), HttpStatusCode.NotFound, "no_encontrado");
        Assert.Empty(Llamadas(ctx));
    }

    [Fact]
    public async Task UnaSucursalDeOtroTenantContesta404()
    {
        var a = await ConectarAsync("mp-loc-aislada-a");
        var b = await ConectarAsync("mp-loc-aislada-b");
        var idSucursalDeB = await CrearSucursalAsync(b, AltaCompleta(b.IdEmpresa));

        await AssertCodigoAsync(await a.Admin.PostAsync(RutaDeLocales(idSucursalDeB), null), HttpStatusCode.NotFound, "no_encontrado");
        Assert.Empty(Llamadas(a));
        Assert.Empty(await FilasAsync(idSucursalDeB));
    }

    [Fact]
    public async Task SinConexionLaConsultaDiceSinConexionYElRegistroEsUn409SinLlamarAMercadoPago()
    {
        var ctx = await _apoyo.PrepararAsync("mp-loc-sin-conexion");
        var idSucursal = await CrearSucursalAsync(ctx, AltaCompleta(ctx.IdEmpresa));

        var estado = await LeerAsync<EstadoDeLocalMercadoPago>(await ctx.Admin.GetAsync(RutaDeLocales(idSucursal)));
        Assert.Equal(EstadosDeLocalMercadoPago.SinConexion, estado.Estado);
        Assert.Empty(estado.CamposFaltantes);

        await AssertCodigoAsync(
            await ctx.Admin.PostAsync(RutaDeLocales(idSucursal), null), HttpStatusCode.Conflict, CodigosDeErrorDeMercadoPago.NoConectada);
        Assert.Empty(Llamadas(ctx));
        Assert.Empty(await FilasAsync(idSucursal));
    }

    [Fact]
    public async Task UnaDireccionIncompletaEsUn400QueNombraLoQueFaltaYNoLlamaAMercadoPago()
    {
        var ctx = await ConectarAsync("mp-loc-incompleta");
        var idSucursal = await CrearSucursalAsync(ctx, new SucursalAlta(ctx.IdEmpresa, "Sucursal Sur", Calle: "Mitre", Ciudad: "Rosario"));
        Guionar(ctx, BusquedaVacia);

        var estado = await LeerAsync<EstadoDeLocalMercadoPago>(await ctx.Admin.GetAsync(RutaDeLocales(idSucursal)));
        Assert.Equal(EstadosDeLocalMercadoPago.NoRegistrada, estado.Estado);
        Assert.Equal(["altura", "provincia", "latitud", "longitud"], estado.CamposFaltantes);

        var respuesta = await ctx.Admin.PostAsync(RutaDeLocales(idSucursal), null);
        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "direccion_incompleta_para_mercadopago");
        var mensaje = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString();
        Assert.Contains("altura", mensaje);
        Assert.Contains("provincia", mensaje);
        Assert.DoesNotContain("calle", mensaje);

        Assert.Empty(Llamadas(ctx));
        Assert.Empty(await FilasAsync(idSucursal));
    }

    // ---- Alta, adopción y actualización ----------------------------------------------------------------------------------

    // Clausula: un 404 de la busqueda es "no hay local con ese external_id" y se crea; una lista vacia se tolera igual.
    [Fact]
    public async Task UnaBusquedaSinCoincidenciasQueContesta404CreaElLocal()
    {
        var r = await PrepararRegistrableAsync("mp-loc-busqueda-404");
        Guionar(r.Ctx, BusquedaVacia, idStore: 555_150);

        var estado = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));

        Assert.Equal(555_150, estado.IdStoreMercadoPago);
        Assert.Equal(["GET", "POST"], Llamadas(r.Ctx).Select(l => l.Metodo.Method));
        Assert.Contains("\"operacion\": \"alta\"", (await AuditoriaAsync(r.IdSucursal)).Valor);
    }

    [Fact]
    public async Task UnaBusquedaConUnaListaVaciaTambienCreaElLocal()
    {
        var r = await PrepararRegistrableAsync("mp-loc-busqueda-lista-vacia");
        Guionar(r.Ctx, BusquedaConListaVacia, idStore: 555_151);

        var estado = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));

        Assert.Equal(555_151, estado.IdStoreMercadoPago);
        Assert.Equal(["GET", "POST"], Llamadas(r.Ctx).Select(l => l.Metodo.Method));
    }

    [Fact]
    public async Task UnaSucursalCompletaSeBuscaPorExternalIdYSeCreaConElTextoLimpioYUnaClaveDeIdempotencia()
    {
        var r = await PrepararRegistrableAsync("mp-loc-alta");
        Guionar(r.Ctx, BusquedaVacia, idStore: 555_123);

        var estado = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));

        Assert.Equal(EstadosDeLocalMercadoPago.Registrada, estado.Estado);
        Assert.Equal(555_123, estado.IdStoreMercadoPago);
        Assert.Equal(r.ExternalId, estado.ExternalId);
        Assert.Empty(estado.CamposFaltantes);

        var llamadas = Llamadas(r.Ctx);
        Assert.Equal(2, llamadas.Count);
        var idUsuario = r.Ctx.MercadoPago.IdUsuario;

        var busqueda = llamadas[0];
        Assert.Equal(HttpMethod.Get, busqueda.Metodo);
        Assert.Equal($"/users/{idUsuario}/stores/search", busqueda.Uri.AbsolutePath);
        Assert.Equal($"?external_id={r.ExternalId}", busqueda.Uri.Query);
        Assert.StartsWith("Bearer ", busqueda.Autorizacion);

        var alta = llamadas[1];
        Assert.Equal(HttpMethod.Post, alta.Metodo);
        Assert.Equal($"/users/{idUsuario}/stores", alta.Uri.AbsolutePath);
        Assert.False(string.IsNullOrWhiteSpace(alta.ClaveDeIdempotencia));
        Assert.True(alta.ClaveDeIdempotencia!.Length <= 64);

        var cuerpo = JsonDocument.Parse(alta.Cuerpo).RootElement;
        Assert.Equal("Sucursal Centro", cuerpo.GetProperty("name").GetString());
        Assert.Equal(r.ExternalId, cuerpo.GetProperty("external_id").GetString());
        var ubicacion = cuerpo.GetProperty("location");
        Assert.Equal("Av. Cordoba", ubicacion.GetProperty("street_name").GetString());
        Assert.Equal("1234 bis", ubicacion.GetProperty("street_number").GetString());
        Assert.Equal("Rosario", ubicacion.GetProperty("city_name").GetString());
        Assert.Equal("Santa Fe", ubicacion.GetProperty("state_name").GetString());
        Assert.Equal(-32.944243m, ubicacion.GetProperty("latitude").GetDecimal());
        Assert.Equal(-60.650539m, ubicacion.GetProperty("longitude").GetDecimal());
        Assert.Equal("Frente a la plaza", ubicacion.GetProperty("reference").GetString());

        var fila = Assert.Single(await FilasAsync(r.IdSucursal));
        Assert.Equal(r.Ctx.IdTenant, fila.IdTenant);
        Assert.Equal(r.Ctx.IdEmpresa, fila.IdEmpresa);
        Assert.Equal(idUsuario, fila.IdUsuarioMercadoPago);
        Assert.Equal(555_123, fila.IdStore);
        Assert.Equal(r.ExternalId, fila.ExternalId);

        var (auditado, cantidad) = await AuditoriaAsync(r.IdSucursal);
        Assert.Equal(1, cantidad);
        Assert.Contains("\"operacion\": \"alta\"", auditado);
        Assert.Contains("555123", auditado);
        Assert.DoesNotContain(AccessToken, auditado);
        Assert.DoesNotContain(RefreshToken, auditado);
    }

    [Fact]
    public async Task UnLocalQueYaExisteConElMismoExternalIdSeAdoptaEnVezDeCrearOtro()
    {
        var r = await PrepararRegistrableAsync("mp-loc-adopcion");
        Guionar(r.Ctx, $$"""{"paging": {"total": 2}, "results": [{"id": 111, "external_id": "OTRO"}, {"id": 777001, "external_id": "{{r.ExternalId}}"}]}""");

        var estado = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));

        Assert.Equal(777_001, estado.IdStoreMercadoPago);
        var llamadas = Llamadas(r.Ctx);
        Assert.DoesNotContain(llamadas, l => l.Metodo == HttpMethod.Post);
        var actualizacion = Assert.Single(llamadas, l => l.Metodo == HttpMethod.Put);
        Assert.Equal($"/users/{r.Ctx.MercadoPago.IdUsuario}/stores/777001", actualizacion.Uri.AbsolutePath);
        Assert.False(string.IsNullOrWhiteSpace(actualizacion.ClaveDeIdempotencia));
        Assert.Equal(777_001, Assert.Single(await FilasAsync(r.IdSucursal)).IdStore);
        Assert.Contains("\"operacion\": \"adopcion\"", (await AuditoriaAsync(r.IdSucursal)).Valor);
    }

    [Fact]
    public async Task UnResultadoDeLaBusquedaConOtroExternalIdNoSeAdopta()
    {
        var r = await PrepararRegistrableAsync("mp-loc-otro-external");
        Guionar(r.Ctx, """{"results": [{"id": 111, "external_id": "WAYSS999999"}]}""", idStore: 555_002);

        var estado = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));

        Assert.Equal(555_002, estado.IdStoreMercadoPago);
        Assert.Single(Llamadas(r.Ctx), l => l.Metodo == HttpMethod.Post);
    }

    [Fact]
    public async Task RegistrarDeNuevoActualizaElLocalExistenteSinBuscarNiCrearYConservaElRegistro()
    {
        var r = await PrepararRegistrableAsync("mp-loc-actualiza");
        Guionar(r.Ctx, BusquedaVacia, idStore: 555_300);
        var primera = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));

        var editada = await r.Ctx.Admin.PutAsJsonAsync(
            $"/api/sucursales/{r.IdSucursal}",
            new SucursalEdicion("Sucursal Centro Nueva", "Mitre", "99", "Rosario", "Santa Fe", null, -32.9m, -60.6m, null));
        Assert.Equal(HttpStatusCode.OK, editada.StatusCode);
        var antesDelEstado = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeLocales(r.IdSucursal)));
        Assert.Equal(EstadosDeLocalMercadoPago.Desactualizada, antesDelEstado.Estado);

        var llamadasPrevias = Llamadas(r.Ctx).Count;
        var segunda = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));

        var nuevas = Llamadas(r.Ctx).Skip(llamadasPrevias).ToList();
        var actualizacion = Assert.Single(nuevas);
        Assert.Equal(HttpMethod.Put, actualizacion.Metodo);
        Assert.Equal($"/users/{r.Ctx.MercadoPago.IdUsuario}/stores/555300", actualizacion.Uri.AbsolutePath);
        var cuerpo = JsonDocument.Parse(actualizacion.Cuerpo).RootElement;
        Assert.Equal("Sucursal Centro Nueva", cuerpo.GetProperty("name").GetString());
        Assert.Equal("Mitre", cuerpo.GetProperty("location").GetProperty("street_name").GetString());
        Assert.False(cuerpo.GetProperty("location").TryGetProperty("reference", out _));

        Assert.Equal(EstadosDeLocalMercadoPago.Registrada, segunda.Estado);
        Assert.Equal(primera.FechaRegistro!.Value, segunda.FechaRegistro!.Value, TimeSpan.FromMilliseconds(1));
        Assert.True(segunda.FechaSincronizacion > primera.FechaSincronizacion);
        var fila = Assert.Single(await FilasAsync(r.IdSucursal));
        Assert.Equal(555_300, fila.IdStore);

        var despues = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeLocales(r.IdSucursal)));
        Assert.Equal(EstadosDeLocalMercadoPago.Registrada, despues.Estado);
        Assert.Equal(2, (await AuditoriaAsync(r.IdSucursal)).Cantidad);
    }

    // Clausula: la clave de idempotencia del alta lleva la huella del cuerpo (Mercado Pago contesta 409 si la misma clave llega con otro cuerpo).
    [Fact]
    public async Task UnAltaRechazadaYReintentadaConOtrosDatosUsaOtraClaveDeIdempotencia()
    {
        var r = await PrepararRegistrableAsync("mp-loc-clave-alta");
        Guionar(
            r.Ctx, BusquedaVacia, idStore: 555_700,
            antes: p => p.Metodo == HttpMethod.Post && p.Cuerpo.Contains("Sucursal Centro\"", StringComparison.Ordinal)
                ? Json(HttpStatusCode.BadRequest, """{"error": "INVALID_NAME", "message": "no"}""")
                : null);
        var ruta = RutaDeLocales(r.IdSucursal);

        await AssertCodigoAsync(await r.Ctx.Admin.PostAsync(ruta, null), HttpStatusCode.BadGateway, CodigosDeErrorDeMercadoPago.SolicitudRechazada);
        var edicion = new SucursalEdicion("Sucursal Centro Nueva", "Av. Córdoba", "1234 bis", "Rosario", "Santa Fe", "2000", -32.944243m, -60.650539m, null);
        Assert.Equal(HttpStatusCode.OK, (await r.Ctx.Admin.PutAsJsonAsync($"/api/sucursales/{r.IdSucursal}", edicion)).StatusCode);
        await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(ruta, null));

        var altas = Llamadas(r.Ctx).Where(l => l.Metodo == HttpMethod.Post).ToList();
        Assert.Equal(2, altas.Count);
        Assert.NotEqual(altas[0].ClaveDeIdempotencia, altas[1].ClaveDeIdempotencia);
    }

    // Clausula: la clave de la actualizacion lleva el estado de la sucursal, asi A -> B -> A no repite la clave de un cuerpo anterior.
    [Fact]
    public async Task VolverAUnContenidoAnteriorDespuesDeUnaEdicionNoReusaLaClaveDeLaActualizacion()
    {
        var r = await PrepararRegistrableAsync("mp-loc-clave-put");
        Guionar(r.Ctx, BusquedaVacia, idStore: 555_710);
        var ruta = RutaDeLocales(r.IdSucursal);
        var a = new SucursalEdicion("Sucursal Centro", "Av. Córdoba", "1234 bis", "Rosario", "Santa Fe", "2000", -32.944243m, -60.650539m, "Frente a la plaza");
        var b = a with { Nombre = "Sucursal Otra" };

        async Task RegistrarComoAsync(SucursalEdicion edicion)
        {
            Assert.Equal(HttpStatusCode.OK, (await r.Ctx.Admin.PutAsJsonAsync($"/api/sucursales/{r.IdSucursal}", edicion)).StatusCode);
            await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(ruta, null));
        }

        await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(ruta, null));
        await RegistrarComoAsync(b);
        await RegistrarComoAsync(a);
        await RegistrarComoAsync(b);

        var claves = Llamadas(r.Ctx).Where(l => l.Metodo == HttpMethod.Put).Select(l => l.ClaveDeIdempotencia).ToList();
        Assert.Equal(3, claves.Count);
        Assert.Equal(3, claves.Distinct().Count());
    }

    // Clausula: un 404 del PUT (el local se borro en Mercado Pago) busca por external_id y adopta, con otra clave que la del PUT fallido.
    [Fact]
    public async Task SiElLocalSeBorroEnMercadoPagoElRegistroLoAdoptaPorExternalIdYActualizaElVinculo()
    {
        var r = await PrepararRegistrableAsync("mp-loc-borrado-adopta");
        Guionar(r.Ctx, BusquedaVacia, idStore: 555_720);
        await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));

        Guionar(
            r.Ctx, $$"""{"results": [{"id": 888001, "external_id": "{{r.ExternalId}}"}]}""", idStore: 888_001,
            antes: p => p.Metodo == HttpMethod.Put && p.Uri.AbsolutePath.EndsWith("/555720", StringComparison.Ordinal)
                ? Json(HttpStatusCode.NotFound, """{"error": "not_found", "message": "store not found"}""")
                : null);
        var previas = Llamadas(r.Ctx).Count;

        var estado = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));

        Assert.Equal(888_001, estado.IdStoreMercadoPago);
        var nuevas = Llamadas(r.Ctx).Skip(previas).ToList();
        Assert.Equal(["PUT", "GET", "PUT"], nuevas.Select(l => l.Metodo.Method));
        Assert.EndsWith("/888001", nuevas[2].Uri.AbsolutePath);
        Assert.NotEqual(nuevas[0].ClaveDeIdempotencia, nuevas[2].ClaveDeIdempotencia);
        Assert.DoesNotContain(nuevas, l => l.Metodo == HttpMethod.Post);
        Assert.Equal(888_001, Assert.Single(await FilasAsync(r.IdSucursal)).IdStore);
        Assert.Contains("\"operacion\": \"adopcion\"", (await AuditoriaAsync(r.IdSucursal)).Valor);
    }

    // Clausula: un 404 del PUT sin local con ese external_id lo vuelve a crear.
    [Fact]
    public async Task SiElLocalSeBorroEnMercadoPagoYNoHayOtroElRegistroLoCreaDeNuevoYActualizaElVinculo()
    {
        var r = await PrepararRegistrableAsync("mp-loc-borrado-crea");
        Guionar(r.Ctx, BusquedaVacia, idStore: 555_730);
        await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));

        Guionar(
            r.Ctx, BusquedaVacia, idStore: 999_731,
            antes: p => p.Metodo == HttpMethod.Put ? Json(HttpStatusCode.NotFound, """{"error": "not_found"}""") : null);
        var previas = Llamadas(r.Ctx).Count;

        var estado = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));

        Assert.Equal(999_731, estado.IdStoreMercadoPago);
        Assert.Equal(["PUT", "GET", "POST"], Llamadas(r.Ctx).Skip(previas).Select(l => l.Metodo.Method));
        Assert.Equal(999_731, Assert.Single(await FilasAsync(r.IdSucursal)).IdStore);
        Assert.Contains("\"operacion\": \"alta\"", (await AuditoriaAsync(r.IdSucursal)).Valor);
    }

    // Clausula: solo el 404 dispara la recuperacion; otro 4xx del PUT sigue siendo un 502.
    [Fact]
    public async Task UnRechazoDelPutQueNoEs404SigueSiendo502SinBuscarNiCrear()
    {
        var r = await PrepararRegistrableAsync("mp-loc-put-400");
        Guionar(r.Ctx, BusquedaVacia, idStore: 555_740);
        await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));

        Guionar(
            r.Ctx, BusquedaVacia,
            antes: p => p.Metodo == HttpMethod.Put ? Json(HttpStatusCode.BadRequest, """{"error": "INVALID_NAME"}""") : null);
        var previas = Llamadas(r.Ctx).Count;

        await AssertCodigoAsync(
            await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null), HttpStatusCode.BadGateway,
            CodigosDeErrorDeMercadoPago.SolicitudRechazada);
        Assert.Equal(["PUT"], Llamadas(r.Ctx).Skip(previas).Select(l => l.Metodo.Method));
        Assert.Equal(555_740, Assert.Single(await FilasAsync(r.IdSucursal)).IdStore);
    }

    [Fact]
    public async Task LaConsultaDistingueNoRegistradaRegistradaYDesactualizada()
    {
        var r = await PrepararRegistrableAsync("mp-loc-estados");
        Guionar(r.Ctx, BusquedaVacia);

        var antes = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeLocales(r.IdSucursal)));
        Assert.Equal(EstadosDeLocalMercadoPago.NoRegistrada, antes.Estado);
        Assert.Null(antes.IdStoreMercadoPago);
        Assert.Null(antes.FechaRegistro);

        await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));
        var registrada = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeLocales(r.IdSucursal)));
        Assert.Equal(EstadosDeLocalMercadoPago.Registrada, registrada.Estado);
        Assert.Equal(r.ExternalId, registrada.ExternalId);
        Assert.NotNull(registrada.FechaRegistro);

        var edicion = new SucursalEdicion("Sucursal Centro", "Av. Córdoba", "1234 bis", "Rosario", "Santa Fe", "2000", -32.944243m, -60.650539m, null);
        Assert.Equal(HttpStatusCode.OK, (await r.Ctx.Admin.PutAsJsonAsync($"/api/sucursales/{r.IdSucursal}", edicion)).StatusCode);

        var desactualizada = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeLocales(r.IdSucursal)));
        Assert.Equal(EstadosDeLocalMercadoPago.Desactualizada, desactualizada.Estado);
    }

    [Fact]
    public async Task UnaReconexionConOtraCuentaRegistraDeNuevoYLaFilaDeLaCuentaAnteriorQuedaDeHistoria()
    {
        var r = await PrepararRegistrableAsync("mp-loc-reconexion");
        Guionar(r.Ctx, BusquedaVacia, idStore: 555_400);
        await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));

        Assert.Equal(HttpStatusCode.NoContent, (await r.Ctx.Admin.DeleteAsync($"{r.Ctx.Ruta}")).StatusCode);
        var sinConexion = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeLocales(r.IdSucursal)));
        Assert.Equal(EstadosDeLocalMercadoPago.SinConexion, sinConexion.Estado);

        var otraCuenta = NuevoIdUsuario();
        r.Ctx.MercadoPago.Responder = _ => Exitoso(AccessToken, RefreshToken, otraCuenta);
        r.Ctx.MercadoPago.ResponderPedido = null;
        var (_, estado) = await IniciarAsync(r.Ctx);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(r.Ctx, estado)).StatusCode);

        var nuevaCuenta = await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.GetAsync(RutaDeLocales(r.IdSucursal)));
        Assert.Equal(EstadosDeLocalMercadoPago.NoRegistrada, nuevaCuenta.Estado);

        Guionar(r.Ctx, BusquedaVacia, idStore: 555_401);
        await LeerAsync<EstadoDeLocalMercadoPago>(await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null));

        var filas = await FilasAsync(r.IdSucursal);
        Assert.Equal(2, filas.Count);
        Assert.Equal(555_400, filas.Single(f => f.IdUsuarioMercadoPago == r.Ctx.MercadoPago.IdUsuario).IdStore);
        Assert.Equal(555_401, filas.Single(f => f.IdUsuarioMercadoPago == otraCuenta).IdStore);
        Assert.Contains(Llamadas(r.Ctx), l => l.Uri.AbsolutePath == $"/users/{otraCuenta}/stores" && l.Metodo == HttpMethod.Post);
    }

    // ---- Fallas de Mercado Pago ------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnRechazoDeMercadoPagoEs502ConSuMensajeSaneadoYNoDejaVinculoNiRastro()
    {
        var r = await PrepararRegistrableAsync("mp-loc-rechazo");
        Guionar(
            r.Ctx, BusquedaVacia,
            antes: p => p.Metodo == HttpMethod.Post
                ? Json(HttpStatusCode.BadRequest, """{"error": "INVALID_NAME", "message": "invalid name\u0007 for store"}""")
                : null);

        var respuesta = await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null);

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadGateway, CodigosDeErrorDeMercadoPago.SolicitudRechazada);
        Assert.Empty(await FilasAsync(r.IdSucursal));
        Assert.Equal(0, (await AuditoriaAsync(r.IdSucursal)).Cantidad);
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.DoesNotContain(AccessToken, texto);
    }

    [Theory]
    [InlineData("""{"name": "sin id"}""")]
    [InlineData("""{"id": 0}""")]
    [InlineData("""[1, 2]""")]
    public async Task UnAltaSinIdentificadorValidoEs502DeRespuestaInvalidaYNoGuardaNada(string respuestaDelAlta)
    {
        var r = await PrepararRegistrableAsync("mp-loc-sin-id");
        Guionar(r.Ctx, BusquedaVacia, antes: p => p.Metodo == HttpMethod.Post ? Json(HttpStatusCode.Created, respuestaDelAlta) : null);

        var respuesta = await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null);

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadGateway, CodigosDeErrorDeMercadoPago.RespuestaInvalida);
        Assert.Empty(await FilasAsync(r.IdSucursal));
    }

    [Fact]
    public async Task MercadoPagoCaidoEs503YNoGuardaNada()
    {
        var r = await PrepararRegistrableAsync("mp-loc-caido");
        Guionar(r.Ctx, BusquedaVacia, antes: p => p.Metodo == HttpMethod.Get ? new HttpResponseMessage(HttpStatusCode.BadGateway) : null);

        await AssertCodigoAsync(
            await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null), HttpStatusCode.ServiceUnavailable,
            CodigosDeErrorDeMercadoPago.NoDisponible);
        Assert.Empty(await FilasAsync(r.IdSucursal));
    }

    [Fact]
    public async Task UnoDe401DeMercadoPagoDejaLaConexionEnRequiereReconexionAunqueElRegistroFalle()
    {
        var r = await PrepararRegistrableAsync("mp-loc-401");
        Guionar(r.Ctx, BusquedaVacia, antes: p => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await AssertCodigoAsync(
            await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null), HttpStatusCode.Conflict,
            CodigosDeErrorDeMercadoPago.RequiereReconexion);

        var conexion = Assert.Single(await _apoyo.FilasAsync(r.Ctx.IdEmpresa), f => f.Estado != "desconectada");
        Assert.Equal("requiere_reconexion", conexion.Estado);
        Assert.Null(conexion.AccessCifrado);
        Assert.Empty(await FilasAsync(r.IdSucursal));

        await AssertCodigoAsync(
            await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null), HttpStatusCode.Conflict,
            CodigosDeErrorDeMercadoPago.RequiereReconexion);
    }

    // ---- Lock por sucursal y backstop ------------------------------------------------------------------------------------

    [Fact]
    public async Task DosRegistrosSimultaneosDeLaMismaSucursalSeSerializanYSoloElPrimeroCrea()
    {
        var r = await PrepararRegistrableAsync("mp-loc-lock");
        Guionar(r.Ctx, BusquedaVacia, idStore: 555_500);

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

        var uno = r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null);
        await detenido.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var dos = r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null);

        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Single(Llamadas(r.Ctx));
        Assert.False(dos.IsCompleted);

        liberar.SetResult();
        var respuestas = await Task.WhenAll(uno, dos);

        foreach (var respuesta in respuestas)
        {
            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        }

        var llamadas = Llamadas(r.Ctx);
        Assert.Single(llamadas, l => l.Metodo == HttpMethod.Post);
        Assert.Single(llamadas, l => l.Metodo == HttpMethod.Put);
        Assert.Single(await FilasAsync(r.IdSucursal));
        Assert.Equal(2, (await AuditoriaAsync(r.IdSucursal)).Cantidad);
    }

    [Fact]
    public async Task UnVinculoInsertadoPorFueraEntreLaLlamadaYElGuardadoRespondeLaCarreraConElIndiceUnicoYElProximoIntentoAdopta()
    {
        var r = await PrepararRegistrableAsync("mp-loc-carrera");
        var competidora = 0;
        Guionar(
            r.Ctx, BusquedaVacia, idStore: 555_600,
            antes: p =>
            {
                if (p.Metodo == HttpMethod.Post && Interlocked.Increment(ref competidora) == 1)
                {
                    InsertarFilaAsync(r, r.Ctx.MercadoPago.IdUsuario, 999_001).GetAwaiter().GetResult();
                }

                return null;
            });

        await AssertCodigoAsync(
            await r.Ctx.Admin.PostAsync(RutaDeLocales(r.IdSucursal), null), HttpStatusCode.Conflict, "mercadopago_local_duplicado");

        var fila = Assert.Single(await FilasAsync(r.IdSucursal));
        Assert.Equal(999_001, fila.IdStore);
    }

    [Fact]
    public async Task LaBaseRechazaUnSegundoVinculoDeLaMismaSucursalYCuentaConElIndiceUnico()
    {
        var r = await PrepararRegistrableAsync("mp-loc-unico");
        var idUsuario = NuevoIdUsuario();
        await InsertarFilaAsync(r, idUsuario, 1);

        var duplicada = await Assert.ThrowsAsync<PostgresException>(() => InsertarFilaAsync(r, idUsuario, 2));
        Assert.Equal("23505", duplicada.SqlState);
        Assert.Equal("ux_locales_mercadopago_sucursal_usuario", duplicada.ConstraintName);

        await InsertarFilaAsync(r, NuevoIdUsuario(), 3);
        Assert.Equal(2, (await FilasAsync(r.IdSucursal)).Count);
    }

    [Fact]
    public async Task LaBaseRechazaUnVinculoConUnaSucursalDeOtraEmpresaYUnExternalIdEnBlanco()
    {
        var a = await PrepararRegistrableAsync("mp-loc-fk-a");
        var (idOtraEmpresa, _) = await _apoyo.CrearOtraEmpresaConCuentaAsync(a.Ctx, "Otra SA");

        await using (var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null))
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText =
                "INSERT INTO locales_mercadopago (id_tenant, id_empresa, id_sucursal, id_usuario_mercadopago, id_store_mercadopago, " +
                "external_id, fecha_registro, fecha_sincronizacion, created_at, updated_at) " +
                "VALUES ($1, $2, $3, 1, 1, 'X', now(), now(), now(), now())";
            comando.Parameters.Add(new NpgsqlParameter { Value = a.Ctx.IdTenant });
            comando.Parameters.Add(new NpgsqlParameter { Value = idOtraEmpresa });
            comando.Parameters.Add(new NpgsqlParameter { Value = a.IdSucursal });

            var error = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
            Assert.Equal("23503", error.SqlState);
            Assert.Equal("fk_locales_mercadopago_sucursal", error.ConstraintName);
        }

        var enBlanco = await Assert.ThrowsAsync<PostgresException>(() => InsertarFilaAsync(a, NuevoIdUsuario(), 1, externalId: "   "));
        Assert.Equal("23514", enBlanco.SqlState);
        Assert.Equal("ck_locales_mercadopago_external_id", enBlanco.ConstraintName);
    }

    [Fact]
    public async Task LaPoliticaDeTenantEscondeElVinculoDeOtroTenantYImpideEscribirEnEl()
    {
        var a = await PrepararRegistrableAsync("mp-loc-rls-a");
        var b = await PrepararRegistrableAsync("mp-loc-rls-b");
        await InsertarFilaAsync(a, NuevoIdUsuario(), 42);

        async Task<long> ContarComoAsync(string modo, int? idTenant)
        {
            await using var conexion = await fixture.AbrirConexionCrudaAsync(modo, idTenant);
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT count(*) FROM locales_mercadopago WHERE id_sucursal = $1";
            comando.Parameters.Add(new NpgsqlParameter { Value = a.IdSucursal });
            return (long)(await comando.ExecuteScalarAsync())!;
        }

        Assert.Equal(1, await ContarComoAsync("tenant", a.Ctx.IdTenant));
        Assert.Equal(0, await ContarComoAsync("tenant", b.Ctx.IdTenant));
        Assert.Equal(0, await ContarComoAsync("ninguno", null));

        await using var conexionB = await fixture.AbrirConexionCrudaAsync("tenant", b.Ctx.IdTenant);
        await using var insercion = conexionB.CreateCommand();
        insercion.CommandText =
            "INSERT INTO locales_mercadopago (id_tenant, id_empresa, id_sucursal, id_usuario_mercadopago, id_store_mercadopago, " +
            "external_id, fecha_registro, fecha_sincronizacion, created_at, updated_at) " +
            "VALUES ($1, $2, $3, 7, 7, 'X', now(), now(), now(), now())";
        insercion.Parameters.Add(new NpgsqlParameter { Value = a.Ctx.IdTenant });
        insercion.Parameters.Add(new NpgsqlParameter { Value = a.Ctx.IdEmpresa });
        insercion.Parameters.Add(new NpgsqlParameter { Value = a.IdSucursal });

        var violacion = await Assert.ThrowsAsync<PostgresException>(() => insercion.ExecuteNonQueryAsync());
        Assert.Equal("42501", violacion.SqlState);
    }

    // ---- Autorización ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SoloElAdminPuedeConsultarYRegistrar()
    {
        var r = await PrepararRegistrableAsync("mp-loc-roles");
        Guionar(r.Ctx, BusquedaVacia);
        var ruta = RutaDeLocales(r.IdSucursal);

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
}
