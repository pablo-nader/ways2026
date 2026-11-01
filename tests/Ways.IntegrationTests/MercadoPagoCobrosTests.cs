using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using Ways.Application.Dispositivos;
using Ways.Application.MercadoPago;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using static Ways.IntegrationTests.ApoyoDeMercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// Los cobros con QR de Mercado Pago que pide el dispositivo del POS contra Postgres real y el manejador falso de Mercado Pago: crear
/// (cuerpo de la orden, cobro vivo único, reuso y conflictos, reintentos con la misma clave), consultar (estados, refresco limitado a
/// uno cada 2 segundos), cancelar (incluido el 409 que termina en aprobado), el alcance por punto de venta y tenant del dispositivo,
/// el 401 que deja la conexión en <c>requiere_reconexion</c> y los backstops de <c>cobros_mercadopago</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MercadoPagoCobrosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Ruta = "/api/pos/mercadopago/cobros";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";
    private const string BusquedaVacia = """{"paging": {"total": 0, "offset": 0, "limit": 30}, "data": []}""";

    private readonly ApoyoDeMercadoPago _apoyo = new(fixture);

    private static long _siguienteLocal = 760_000;
    private static long _siguientePos = 770_000;
    private static int _siguienteOrden;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>El Mercado Pago de la prueba: crea órdenes idempotentes por clave y contesta lo que diga <see cref="Estados"/>.</summary>
    private sealed class OrdenesFalsas
    {
        public ConcurrentDictionary<string, string> OrdenPorClave { get; } = new();

        public ConcurrentDictionary<string, string> Estados { get; } = new();

        public ConcurrentDictionary<string, decimal> Totales { get; } = new();

        /// <summary>Lo que Mercado Pago informa como pagado; por defecto, el total de la orden.</summary>
        public decimal? Pagado { get; set; }

        /// <summary>Responde antes que el guion; nulo sigue con el guion.</summary>
        public Func<PedidoRegistrado, HttpResponseMessage?>? Antes { get; set; }

        /// <summary>Respuesta de <c>POST /v1/orders/{id}/cancel</c>; por defecto la orden queda cancelada.</summary>
        public Func<string, HttpResponseMessage?>? Cancelar { get; set; }

        public string? ReferenceId { get; set; } = "9876543";
    }

    private sealed class Escenario
    {
        public required Contexto Ctx { get; init; }

        public required RelojControlable Reloj { get; init; }

        public required OrdenesFalsas Ordenes { get; init; }

        public required int IdSucursal { get; init; }

        public required int IdPuntoVenta { get; init; }

        public required int IdMedio { get; init; }

        public required int IdDispositivo { get; init; }

        public required HttpClient Cajero { get; init; }

        public string ExternalIdCaja => $"WAYSPV{IdPuntoVenta}";

        public int IdTenant => Ctx.IdTenant;
    }

    private static HttpResponseMessage Json(HttpStatusCode estado, string json) =>
        new(estado) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static bool EsDeLaApi(PedidoRegistrado pedido) =>
        !pedido.Uri.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal);

    private static string IdDeOrdenEnRuta(string ruta) => ruta.Split('/')[3];

    private static string JsonDeOrden(OrdenesFalsas o, string id)
    {
        var estado = o.Estados.GetValueOrDefault(id, "created");
        var total = o.Totales.GetValueOrDefault(id, 0m);
        var pagos = estado == "processed"
            ? $$"""
              "transactions": {"payments": [{"id": "PAY{{id}}", "status": "processed", "amount": "{{total.ToString("F2", CultureInfo.InvariantCulture)}}",
              "paid_amount": "{{(o.Pagado ?? total).ToString("F2", CultureInfo.InvariantCulture)}}", "reference_id": {{(o.ReferenceId ?? "null")}}}]},
              """
            : string.Empty;
        return $$"""{ {{pagos}} "id": "{{id}}", "status": "{{estado}}", "extra": {"x": 1}, "type_response": {"qr_data": "000201QR{{id}}"} }""";
    }

    private static void Guionar(Contexto ctx, OrdenesFalsas ordenes) =>
        ctx.MercadoPago.ResponderPedido = pedido =>
        {
            if (!EsDeLaApi(pedido))
            {
                return Exitoso(AccessToken, RefreshToken, ctx.MercadoPago.IdUsuario);
            }

            if (ordenes.Antes?.Invoke(pedido) is { } propia)
            {
                return propia;
            }

            var ruta = pedido.Uri.AbsolutePath;
            if (ruta.Contains("/stores", StringComparison.Ordinal))
            {
                return pedido.Metodo == HttpMethod.Get
                    ? Json(HttpStatusCode.NotFound, """{"error": "store_not_found", "message": "store not found"}""")
                    : pedido.Metodo == HttpMethod.Post
                        ? Json(HttpStatusCode.Created, $$"""{"id": {{Interlocked.Increment(ref _siguienteLocal)}}}""")
                        : Json(HttpStatusCode.OK, """{"id": 1}""");
            }

            if (ruta == "/v2/pos")
            {
                return pedido.Metodo == HttpMethod.Get
                    ? Json(HttpStatusCode.OK, BusquedaVacia)
                    : Json(
                        HttpStatusCode.Created,
                        $$"""{"id": {{Interlocked.Increment(ref _siguientePos)}}, "qr_response": {"image": "https://qr.test/x.png"} }""");
            }

            if (ruta == "/v1/orders" && pedido.Metodo == HttpMethod.Post)
            {
                var clave = pedido.ClaveDeIdempotencia ?? string.Empty;
                var id = ordenes.OrdenPorClave.GetOrAdd(clave, _ => $"ORD{Interlocked.Increment(ref _siguienteOrden):D6}");
                ordenes.Estados.TryAdd(id, "created");
                using var cuerpo = JsonDocument.Parse(pedido.Cuerpo);
                ordenes.Totales[id] = decimal.Parse(
                    cuerpo.RootElement.GetProperty("total_amount").GetString()!, CultureInfo.InvariantCulture);
                return Json(HttpStatusCode.Created, JsonDeOrden(ordenes, id));
            }

            if (ruta.StartsWith("/v1/orders/", StringComparison.Ordinal))
            {
                var id = IdDeOrdenEnRuta(ruta);
                if (ruta.EndsWith("/cancel", StringComparison.Ordinal) && pedido.Metodo == HttpMethod.Post)
                {
                    if (ordenes.Cancelar?.Invoke(id) is { } cancelada)
                    {
                        return cancelada;
                    }

                    ordenes.Estados[id] = "canceled";
                    return Json(HttpStatusCode.OK, JsonDeOrden(ordenes, id));
                }

                if (pedido.Metodo == HttpMethod.Get)
                {
                    return Json(HttpStatusCode.OK, JsonDeOrden(ordenes, id));
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

    private static SucursalAlta AltaCompleta(int idEmpresa, string nombre) => new(
        idEmpresa, nombre, "Av. Córdoba", "1234 bis", "Rosario", "Santa Fe", "2000", -32.944243m, -60.650539m, "Frente a la plaza");

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta, HttpStatusCode esperado = HttpStatusCode.OK)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {cuerpo}");
        return JsonSerializer.Deserialize<T>(cuerpo, Web)!;
    }

    private static async Task AssertCodigoAsync(HttpResponseMessage respuesta, HttpStatusCode estado, string codigo)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == estado, $"{respuesta.StatusCode}: {cuerpo}");
        Assert.Equal(codigo, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private static string ExtraerCookieDeDispositivo(HttpResponseMessage respuesta)
    {
        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(respuesta.Headers.GetValues("Set-Cookie"), v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valor = setCookie[prefijo.Length..];
        return valor[..valor.IndexOf(';')];
    }

    /// <summary>Vincula un dispositivo al punto de venta y devuelve un cliente ya logueado como un cajero Vendedor, más el id del dispositivo.</summary>
    private static async Task<(HttpClient Cajero, int IdDispositivo)> VincularCajeroAsync(Contexto ctx, int idPuntoVenta, string sufijo)
    {
        var alta = await ctx.Admin.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, $"Caja {sufijo}"));
        var vinculado = await LeerAsync<DispositivoVinculado>(alta, HttpStatusCode.Created);
        var cookie = ExtraerCookieDeDispositivo(alta);

        var usuario = $"cajero-{sufijo}-{Guid.NewGuid().ToString("N")[..6]}";
        var creado = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario(usuario, $"{usuario}@ways.test", (int)RolConocido.Vendedor, PasswordCajero));
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);

        var cajero = ctx.Host.CreateClient();
        using var solicitud = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo(usuario, PasswordCajero))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={cookie}");
        Assert.Equal(HttpStatusCode.OK, (await cajero.SendAsync(solicitud)).StatusCode);
        return (cajero, vinculado.Datos.Id);
    }

    private static async Task<int> CrearMedioAsync(Contexto ctx, string nombre, string comportamiento, string? proveedor, bool activo = true)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/catalogos/medios-pago", new Dictionary<string, object?>
        {
            ["nombre"] = nombre,
            ["idEmpresa"] = ctx.IdEmpresa,
            ["orden"] = 9,
            ["comportamiento"] = comportamiento,
            ["admiteVuelto"] = false,
            ["requiereReferencia"] = false,
            ["recargoPorcentaje"] = null,
            ["proveedor"] = proveedor,
            ["activo"] = activo
        });
        return (await LeerAsync<JsonElement>(respuesta, HttpStatusCode.Created)).GetProperty("id").GetInt32();
    }

    private async Task<Escenario> PrepararAsync(string nombre, bool conCaja = true, bool conectar = true)
    {
        var reloj = new RelojControlable(DateTimeOffset.UtcNow);
        var ctx = await _apoyo.PrepararAsync(nombre, reloj: reloj);
        var ordenes = new OrdenesFalsas();
        Guionar(ctx, ordenes);

        if (conectar)
        {
            var (_, estado) = await IniciarAsync(ctx);
            Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(ctx, estado)).StatusCode);
        }

        var sucursal = await ctx.Admin.PostAsJsonAsync("/api/sucursales", AltaCompleta(ctx.IdEmpresa, "Sucursal Centro"));
        var idSucursal = (await LeerAsync<SucursalListado>(sucursal, HttpStatusCode.Created)).Id;
        var puntoVenta = await ctx.Admin.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(ctx.IdEmpresa, "Caja Uno", ModoPuntoVenta.Escritorio, IdSucursal: idSucursal));
        var idPuntoVenta = (await LeerAsync<PuntoVentaListado>(puntoVenta, HttpStatusCode.Created)).Id;

        if (conectar && conCaja)
        {
            Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/sucursales/{idSucursal}/mercadopago", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/puntos-venta/{idPuntoVenta}/mercadopago", null)).StatusCode);
        }

        var idMedio = await CrearMedioAsync(ctx, "QR Mercado Pago", "Electronico", "mercadopago_qr");
        var (cajero, idDispositivo) = await VincularCajeroAsync(ctx, idPuntoVenta, "uno");

        return new Escenario
        {
            Ctx = ctx,
            Reloj = reloj,
            Ordenes = ordenes,
            IdSucursal = idSucursal,
            IdPuntoVenta = idPuntoVenta,
            IdMedio = idMedio,
            IdDispositivo = idDispositivo,
            Cajero = cajero
        };
    }

    private static Task<HttpResponseMessage> CrearAsync(
        Escenario e, long numero, decimal importe, int? idMedio = null, HttpClient? cliente = null) =>
        (cliente ?? e.Cajero).PostAsJsonAsync(Ruta, new SolicitudDeCobroMercadoPago(idMedio ?? e.IdMedio, numero, importe));

    private static Task<HttpResponseMessage> ConsultarAsync(Escenario e, int idCobro, HttpClient? cliente = null) =>
        (cliente ?? e.Cajero).GetAsync($"{Ruta}/{idCobro}");

    private static Task<HttpResponseMessage> CancelarAsync(Escenario e, int idCobro, HttpClient? cliente = null) =>
        (cliente ?? e.Cajero).PostAsync($"{Ruta}/{idCobro}/cancelar", null);

    private static async Task<CobroDeMercadoPago> CrearOkAsync(Escenario e, long numero, decimal importe) =>
        await LeerAsync<CobroDeMercadoPago>(await CrearAsync(e, numero, importe));

    private static IReadOnlyList<PedidoRegistrado> Pedidos(Escenario e) =>
        [.. e.Ctx.MercadoPago.Pedidos.Where(p => EsDeLaApi(p) && p.Uri.AbsolutePath.StartsWith("/v1/orders", StringComparison.Ordinal))];

    private static IReadOnlyList<PedidoRegistrado> Creaciones(Escenario e) =>
        [.. Pedidos(e).Where(p => p.Metodo == HttpMethod.Post && p.Uri.AbsolutePath == "/v1/orders")];

    private static IReadOnlyList<PedidoRegistrado> Lecturas(Escenario e) =>
        [.. Pedidos(e).Where(p => p.Metodo == HttpMethod.Get)];

    private static IReadOnlyList<PedidoRegistrado> Cancelaciones(Escenario e) =>
        [.. Pedidos(e).Where(p => p.Uri.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal))];

    private async Task<List<Dictionary<string, object?>>> FilasAsync(int idPuntoVenta)
    {
        var filas = new List<Dictionary<string, object?>>();
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT * FROM cobros_mercadopago WHERE id_punto_venta = $1 ORDER BY id_cobro_mercadopago";
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta });

        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            var fila = new Dictionary<string, object?>();
            for (var i = 0; i < lector.FieldCount; i++)
            {
                fila[lector.GetName(i)] = lector.IsDBNull(i) ? null : lector.GetValue(i);
            }

            filas.Add(fila);
        }

        return filas;
    }

    private async Task InsertarFilaAsync(Escenario e, long numero, string estado, int intento, int? idPuntoVenta = null)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "INSERT INTO cobros_mercadopago (id_tenant, id_empresa, id_punto_venta, id_dispositivo, id_medio_pago, numero_preasignado, " +
            "intento, tipo, importe, external_reference, id_orden_mercadopago, estado, expira_en, fecha_aprobacion, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, 'qr', 10, $8, $9, $10, now(), now(), now(), now())";
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.Ctx.IdEmpresa });
        comando.Parameters.Add(new NpgsqlParameter { Value = idPuntoVenta ?? e.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdDispositivo });
        comando.Parameters.Add(new NpgsqlParameter { Value = e.IdMedio });
        comando.Parameters.Add(new NpgsqlParameter { Value = numero });
        comando.Parameters.Add(new NpgsqlParameter { Value = intento });
        comando.Parameters.Add(new NpgsqlParameter { Value = $"crudo-{Guid.NewGuid():N}" });
        comando.Parameters.Add(new NpgsqlParameter { Value = $"ORDCRUDA{Guid.NewGuid():N}"[..20] });
        comando.Parameters.Add(new NpgsqlParameter { Value = estado });
        await comando.ExecuteNonQueryAsync();
    }

    // ---- Crear: la orden que se manda y lo que se guarda -----------------------------------------------------------------

    // Clausula: el cuerpo de POST /v1/orders (type, external_reference, importes como cadena con 2 decimales, expiration_time,
    // caja, mode dynamic, item) y la clave de idempotencia; la fila queda pendiente con los ids del dispositivo.
    [Fact]
    public async Task CrearUnCobroMandaLaOrdenQrConSuReferenciaYGuardaLaFilaPendiente()
    {
        var e = await PrepararAsync("mp-cobro-alta");

        var cobro = await CrearOkAsync(e, 5001, 123.4m);

        Assert.Equal("pendiente", cobro.Estado);
        Assert.StartsWith("000201QRORD", cobro.QrData);
        Assert.Equal(123.4m, cobro.Importe);
        Assert.Null(cobro.ImportePagado);
        Assert.False(cobro.ImporteDistinto);
        Assert.True(cobro.ExpiraEn > DateTimeOffset.UtcNow.AddMinutes(9));

        var pedido = Assert.Single(Creaciones(e));
        Assert.Equal($"W{e.IdTenant}P{e.IdPuntoVenta}N5001I1", JsonDocument.Parse(pedido.Cuerpo).RootElement.GetProperty("external_reference").GetString());
        using var cuerpo = JsonDocument.Parse(pedido.Cuerpo);
        var raiz = cuerpo.RootElement;
        Assert.Equal("qr", raiz.GetProperty("type").GetString());
        Assert.Equal("123.40", raiz.GetProperty("total_amount").GetString());
        Assert.Equal("PT10M", raiz.GetProperty("expiration_time").GetString());
        Assert.Equal(e.ExternalIdCaja, raiz.GetProperty("config").GetProperty("qr").GetProperty("external_pos_id").GetString());
        Assert.Equal("dynamic", raiz.GetProperty("config").GetProperty("qr").GetProperty("mode").GetString());
        var item = Assert.Single(raiz.GetProperty("items").EnumerateArray());
        Assert.Equal("Venta 5001", item.GetProperty("title").GetString());
        Assert.Equal("123.40", item.GetProperty("unit_price").GetString());
        Assert.Equal(1, item.GetProperty("quantity").GetInt32());
        Assert.Equal("unit", item.GetProperty("unit_measure").GetString());
        Assert.StartsWith("ways-cobro-", pedido.ClaveDeIdempotencia);
        Assert.InRange(pedido.ClaveDeIdempotencia!.Length, 1, 64);

        var fila = Assert.Single(await FilasAsync(e.IdPuntoVenta));
        Assert.Equal(cobro.IdCobro, fila["id_cobro_mercadopago"]);
        Assert.Equal(e.IdTenant, fila["id_tenant"]);
        Assert.Equal(e.Ctx.IdEmpresa, fila["id_empresa"]);
        Assert.Equal(e.IdDispositivo, fila["id_dispositivo"]);
        Assert.Equal(e.IdMedio, fila["id_medio_pago"]);
        Assert.Equal(5001L, fila["numero_preasignado"]);
        Assert.Equal(1, fila["intento"]);
        Assert.Equal("qr", fila["tipo"]);
        Assert.Equal(123.40m, fila["importe"]);
        Assert.Equal("pendiente", fila["estado"]);
        Assert.Equal($"W{e.IdTenant}P{e.IdPuntoVenta}N5001I1", fila["external_reference"]);
        Assert.StartsWith("000201QRORD", (string)fila["qr_data"]!);
    }

    // Clausula: un cobro pendiente del mismo numero, medio e importe se devuelve tal cual; con otro importe es 409 en_curso.
    [Fact]
    public async Task UnReintentoConElMismoImporteDevuelveElMismoCobroSinOtraOrdenYConOtroImporteEsUn409()
    {
        var e = await PrepararAsync("mp-cobro-reuso");
        var primero = await CrearOkAsync(e, 5002, 100m);

        var segundo = await CrearOkAsync(e, 5002, 100m);

        Assert.Equal(primero.IdCobro, segundo.IdCobro);
        Assert.Equal(primero.QrData, segundo.QrData);
        Assert.Single(Creaciones(e));

        await AssertCodigoAsync(await CrearAsync(e, 5002, 100.01m), HttpStatusCode.Conflict, "cobro_mercadopago_en_curso");
        Assert.Single(Creaciones(e));
        Assert.Single(await FilasAsync(e.IdPuntoVenta));
    }

    [Fact]
    public async Task UnCobroAprobadoDelNumeroHaceDeLaCreacionUn409YaAprobado()
    {
        var e = await PrepararAsync("mp-cobro-ya-aprobado");
        var cobro = await CrearOkAsync(e, 5003, 100m);
        e.Ordenes.Estados[e.Ordenes.OrdenPorClave.Values.Single()] = "processed";
        Assert.Equal("aprobado", (await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro))).Estado);

        await AssertCodigoAsync(await CrearAsync(e, 5003, 100m), HttpStatusCode.Conflict, "cobro_mercadopago_ya_aprobado");
        await AssertCodigoAsync(await CrearAsync(e, 5003, 50m), HttpStatusCode.Conflict, "cobro_mercadopago_ya_aprobado");
        Assert.Single(Creaciones(e));
    }

    // Clausula: el intento sube despues de un cobro cancelado y la clave de idempotencia y la referencia cambian con el.
    [Fact]
    public async Task DespuesDeCancelarUnCobroElMismoNumeroAbreOtroIntentoConOtraReferenciaYOtraClave()
    {
        var e = await PrepararAsync("mp-cobro-intento");
        var primero = await CrearOkAsync(e, 5004, 100m);
        Assert.Equal("cancelado", (await LeerAsync<CobroDeMercadoPago>(await CancelarAsync(e, primero.IdCobro))).Estado);

        var segundo = await CrearOkAsync(e, 5004, 100m);

        Assert.NotEqual(primero.IdCobro, segundo.IdCobro);
        Assert.Equal("pendiente", segundo.Estado);
        var creaciones = Creaciones(e);
        Assert.Equal(2, creaciones.Count);
        Assert.NotEqual(creaciones[0].ClaveDeIdempotencia, creaciones[1].ClaveDeIdempotencia);
        Assert.Equal(
            $"W{e.IdTenant}P{e.IdPuntoVenta}N5004I2",
            JsonDocument.Parse(creaciones[1].Cuerpo).RootElement.GetProperty("external_reference").GetString());
        Assert.Equal(new[] { 1, 2 }, (await FilasAsync(e.IdPuntoVenta)).Select(f => (int)f["intento"]!).ToArray());
    }

    // Clausula: la clave de idempotencia depende del intento y del importe, no del tiempo: un intento que fallo se reintenta con la misma
    // clave (Mercado Pago devuelve la misma orden) y un importe distinto usa otra.
    [Fact]
    public async Task UnaCreacionQueFalloSeReintentaConLaMismaClaveYNoAbreOtraOrden()
    {
        var e = await PrepararAsync("mp-cobro-reintento");
        var fallar = 1;
        e.Ordenes.Antes = p => p.Metodo == HttpMethod.Post && p.Uri.AbsolutePath == "/v1/orders" && Interlocked.Exchange(ref fallar, 0) == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : null;

        await AssertCodigoAsync(await CrearAsync(e, 5005, 100m), HttpStatusCode.ServiceUnavailable, CodigosDeErrorDeMercadoPago.NoDisponible);
        Assert.Empty(await FilasAsync(e.IdPuntoVenta));

        var cobro = await CrearOkAsync(e, 5005, 100m);

        var creaciones = Creaciones(e);
        Assert.Equal(2, creaciones.Count);
        Assert.Equal(creaciones[0].ClaveDeIdempotencia, creaciones[1].ClaveDeIdempotencia);
        Assert.Single(e.Ordenes.OrdenPorClave);
        Assert.Equal("pendiente", cobro.Estado);
        Assert.Equal(1, (int)Assert.Single(await FilasAsync(e.IdPuntoVenta))["intento"]!);
    }

    // Clausula: el pg_advisory_xact_lock por punto de venta serializa dos creaciones simultaneas del mismo numero.
    [Fact]
    public async Task DosCreacionesSimultaneasDelMismoNumeroAbrenUnaSolaOrdenYDevuelvenElMismoCobro()
    {
        var e = await PrepararAsync("mp-cobro-lock");
        var detenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primera = 0;
        e.Ctx.MercadoPago.AntesDeResponder = async () =>
        {
            if (Interlocked.Increment(ref primera) == 1)
            {
                detenido.TrySetResult();
                await liberar.Task;
            }
        };

        var uno = CrearAsync(e, 5006, 100m);
        await detenido.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var dos = CrearAsync(e, 5006, 100m);

        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(dos.IsCompleted);
        Assert.Single(Creaciones(e));

        liberar.SetResult();
        var respuestas = await Task.WhenAll(uno, dos);

        var cobros = new List<CobroDeMercadoPago>();
        foreach (var respuesta in respuestas)
        {
            cobros.Add(await LeerAsync<CobroDeMercadoPago>(respuesta));
        }

        Assert.Equal(cobros[0].IdCobro, cobros[1].IdCobro);
        Assert.Single(Creaciones(e));
        Assert.Single(await FilasAsync(e.IdPuntoVenta));
    }

    // Clausula: ux_cobros_mercadopago_vivo_numero, traducido a 409 cuando una escritura por fuera gana la carrera.
    [Fact]
    public async Task UnCobroVivoInsertadoPorFueraEntreLaLlamadaYElGuardadoRespondeLaCarreraConElIndiceUnico()
    {
        var e = await PrepararAsync("mp-cobro-carrera");
        var competidora = 0;
        e.Ordenes.Antes = p =>
        {
            if (p.Metodo == HttpMethod.Post && p.Uri.AbsolutePath == "/v1/orders" && Interlocked.Increment(ref competidora) == 1)
            {
                InsertarFilaAsync(e, 5007, "pendiente", 7).GetAwaiter().GetResult();
            }

            return null;
        };

        await AssertCodigoAsync(await CrearAsync(e, 5007, 100m), HttpStatusCode.Conflict, "cobro_mercadopago_en_curso");

        Assert.Equal(7, (int)Assert.Single(await FilasAsync(e.IdPuntoVenta))["intento"]!);
    }

    [Fact]
    public async Task LaBaseRechazaUnSegundoCobroVivoDelNumeroPeroAdmiteLosNoVivosYOtroNumero()
    {
        var e = await PrepararAsync("mp-cobro-unico");
        await InsertarFilaAsync(e, 5008, "aprobado", 1);

        var pendiente = await Assert.ThrowsAsync<PostgresException>(() => InsertarFilaAsync(e, 5008, "pendiente", 2));
        Assert.Equal("23505", pendiente.SqlState);
        Assert.Equal("ux_cobros_mercadopago_vivo_numero", pendiente.ConstraintName);

        await InsertarFilaAsync(e, 5008, "cancelado", 3);
        await InsertarFilaAsync(e, 5008, "expirado", 4);
        await InsertarFilaAsync(e, 5008, "devuelto", 5);
        await InsertarFilaAsync(e, 5009, "pendiente", 1);

        var mismoIntento = await Assert.ThrowsAsync<PostgresException>(() => InsertarFilaAsync(e, 5008, "cancelado", 3));
        Assert.Equal("ux_cobros_mercadopago_intento", mismoIntento.ConstraintName);
        Assert.Equal(5, (await FilasAsync(e.IdPuntoVenta)).Count);
    }

    // Clausula: un cobro pendiente vencido se consulta a Mercado Pago antes de decidir; si venció, el numero abre otro intento.
    [Fact]
    public async Task UnCobroPendienteVencidoSeConsultaYSiExpiroElNumeroAbreOtroIntento()
    {
        var e = await PrepararAsync("mp-cobro-vencido");
        var viejo = await CrearOkAsync(e, 5010, 100m);
        e.Ordenes.Estados[e.Ordenes.OrdenPorClave.Values.Single()] = "expired";
        e.Reloj.Ahora = e.Reloj.Ahora.AddMinutes(11);

        var nuevo = await CrearOkAsync(e, 5010, 100m);

        Assert.NotEqual(viejo.IdCobro, nuevo.IdCobro);
        Assert.Equal(new[] { "expirado", "pendiente" }, (await FilasAsync(e.IdPuntoVenta)).Select(f => (string)f["estado"]!).ToArray());
        Assert.Equal(2, Creaciones(e).Count);
    }

    [Fact]
    public async Task UnCobroPendienteVencidoQueSePagoAntesDeVencerEsUn409YaAprobadoYQuedaAprobado()
    {
        var e = await PrepararAsync("mp-cobro-vencido-pagado");
        await CrearOkAsync(e, 5011, 100m);
        e.Ordenes.Estados[e.Ordenes.OrdenPorClave.Values.Single()] = "processed";
        e.Reloj.Ahora = e.Reloj.Ahora.AddMinutes(11);

        await AssertCodigoAsync(await CrearAsync(e, 5011, 100m), HttpStatusCode.Conflict, "cobro_mercadopago_ya_aprobado");

        Assert.Equal("aprobado", Assert.Single(await FilasAsync(e.IdPuntoVenta))["estado"]);
        Assert.Single(Creaciones(e));
    }

    // ---- Crear: validaciones y precondiciones ----------------------------------------------------------------------------

    [Fact]
    public async Task ElMedioTieneQueExistirEstarActivoSerDeLaEmpresaYCobrarConQr()
    {
        var e = await PrepararAsync("mp-cobro-medio");
        var sinProveedor = await CrearMedioAsync(e.Ctx, "Transferencia", "Electronico", null);
        var point = await CrearMedioAsync(e.Ctx, "Point", "Electronico", "mercadopago_point");
        var efectivo = await CrearMedioAsync(e.Ctx, "Efectivo", "Efectivo", null);
        var inactivo = await CrearMedioAsync(e.Ctx, "QR viejo", "Electronico", "mercadopago_qr", activo: false);
        var otro = await PrepararAsync("mp-cobro-medio-ajeno");

        foreach (var idMedio in new[] { sinProveedor, point, efectivo, inactivo, otro.IdMedio, 987_654 })
        {
            await AssertCodigoAsync(
                await CrearAsync(e, 5012, 100m, idMedio), HttpStatusCode.BadRequest, "cobro_mercadopago_medio_invalido");
        }

        Assert.Empty(Creaciones(e));
        Assert.Empty(await FilasAsync(e.IdPuntoVenta));
    }

    [Theory]
    [InlineData(0, 100, "cobro_mercadopago_numero_invalido")]
    [InlineData(-3, 100, "cobro_mercadopago_numero_invalido")]
    [InlineData(5013, 0, "cobro_mercadopago_importe_invalido")]
    [InlineData(5013, -1, "cobro_mercadopago_importe_invalido")]
    [InlineData(5013, 10.005, "cobro_mercadopago_importe_invalido")]
    [InlineData(5013, 99999999999999.0, "cobro_mercadopago_importe_invalido")]
    public async Task UnCuerpoInvalidoEsUn400SinLlamarAMercadoPago(long numero, double importe, string codigo)
    {
        var e = await PrepararAsync("mp-cobro-cuerpo-" + Math.Abs(numero) + (int)importe);

        await AssertCodigoAsync(await CrearAsync(e, numero, (decimal)importe), HttpStatusCode.BadRequest, codigo);

        Assert.Empty(Creaciones(e));
    }

    [Fact]
    public async Task SinCajaRegistradaOSinConexionLaCreacionEsUn409SinCrearNada()
    {
        var sinCaja = await PrepararAsync("mp-cobro-sin-caja", conCaja: false);
        await AssertCodigoAsync(
            await CrearAsync(sinCaja, 5014, 100m), HttpStatusCode.Conflict, CodigosDeErrorDeMercadoPago.CajaNoRegistrada);
        Assert.Empty(Creaciones(sinCaja));
        Assert.Empty(await FilasAsync(sinCaja.IdPuntoVenta));

        var sinConexion = await PrepararAsync("mp-cobro-sin-conexion", conectar: false);
        await AssertCodigoAsync(
            await CrearAsync(sinConexion, 5014, 100m), HttpStatusCode.Conflict, CodigosDeErrorDeMercadoPago.NoConectada);
        Assert.Empty(Creaciones(sinConexion));
    }

    [Fact]
    public async Task UnCuerpoDeMercadoPagoSinIdOSinQrEsUn502SinFila()
    {
        var e = await PrepararAsync("mp-cobro-respuesta-invalida");
        e.Ordenes.Antes = p => p.Metodo == HttpMethod.Post && p.Uri.AbsolutePath == "/v1/orders"
            ? Json(HttpStatusCode.Created, """{"id": "ORDX", "status": "created", "type_response": {}}""")
            : null;
        await AssertCodigoAsync(
            await CrearAsync(e, 5015, 100m), HttpStatusCode.BadGateway, CodigosDeErrorDeMercadoPago.RespuestaInvalida);

        e.Ordenes.Antes = p => p.Metodo == HttpMethod.Post && p.Uri.AbsolutePath == "/v1/orders"
            ? Json(HttpStatusCode.BadRequest, """{"error": "invalid_params", "message": "x"}""")
            : null;
        await AssertCodigoAsync(
            await CrearAsync(e, 5015, 100m), HttpStatusCode.BadGateway, CodigosDeErrorDeMercadoPago.SolicitudRechazada);

        Assert.Empty(await FilasAsync(e.IdPuntoVenta));
    }

    [Fact]
    public async Task UnoDe401DeMercadoPagoAlCrearDejaLaConexionEnRequiereReconexion()
    {
        var e = await PrepararAsync("mp-cobro-401");
        e.Ordenes.Antes = p => p.Uri.AbsolutePath == "/v1/orders" ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : null;

        await AssertCodigoAsync(
            await CrearAsync(e, 5016, 100m), HttpStatusCode.Conflict, CodigosDeErrorDeMercadoPago.RequiereReconexion);

        var conexion = Assert.Single(await _apoyo.FilasAsync(e.Ctx.IdEmpresa), f => f.Estado != "desconectada");
        Assert.Equal("requiere_reconexion", conexion.Estado);
        Assert.Empty(await FilasAsync(e.IdPuntoVenta));
    }

    // ---- Alcance del dispositivo ----------------------------------------------------------------------------------------

    // Clausula: el punto de venta sale de la sesion del dispositivo; un cobro de otro punto de venta o de otro tenant es 404.
    [Fact]
    public async Task UnCobroDeOtroPuntoDeVentaODeOtroTenantEsUn404ParaLasTresRutas()
    {
        var e = await PrepararAsync("mp-cobro-alcance");
        var cobro = await CrearOkAsync(e, 5017, 100m);

        var otroPv = await e.Ctx.Admin.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(e.Ctx.IdEmpresa, "Caja Dos", ModoPuntoVenta.Escritorio, IdSucursal: e.IdSucursal));
        var idOtroPv = (await LeerAsync<PuntoVentaListado>(otroPv, HttpStatusCode.Created)).Id;
        var (cajeroDos, _) = await VincularCajeroAsync(e.Ctx, idOtroPv, "dos");
        var ajeno = await PrepararAsync("mp-cobro-alcance-ajeno");

        foreach (var cliente in new[] { cajeroDos, ajeno.Cajero })
        {
            await AssertCodigoAsync(await ConsultarAsync(e, cobro.IdCobro, cliente), HttpStatusCode.NotFound, "no_encontrado");
            await AssertCodigoAsync(await CancelarAsync(e, cobro.IdCobro, cliente), HttpStatusCode.NotFound, "no_encontrado");
        }

        Assert.Empty(Lecturas(e));
        Assert.Empty(Cancelaciones(e));
        Assert.Equal("pendiente", Assert.Single(await FilasAsync(e.IdPuntoVenta))["estado"]);
    }

    [Fact]
    public async Task UnCobroNuevoLoCreaElPuntoDeVentaDelDispositivoYNoElDeOtro()
    {
        var e = await PrepararAsync("mp-cobro-pv-propio");
        var otroPv = await e.Ctx.Admin.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(e.Ctx.IdEmpresa, "Caja Dos", ModoPuntoVenta.Escritorio, IdSucursal: e.IdSucursal));
        var idOtroPv = (await LeerAsync<PuntoVentaListado>(otroPv, HttpStatusCode.Created)).Id;
        var (cajeroDos, idDispositivoDos) = await VincularCajeroAsync(e.Ctx, idOtroPv, "dos");
        Assert.Equal(HttpStatusCode.OK, (await e.Ctx.Admin.PostAsync($"/api/puntos-venta/{idOtroPv}/mercadopago", null)).StatusCode);

        await LeerAsync<CobroDeMercadoPago>(await CrearAsync(e, 5018, 100m, cliente: cajeroDos));

        Assert.Empty(await FilasAsync(e.IdPuntoVenta));
        var fila = Assert.Single(await FilasAsync(idOtroPv));
        Assert.Equal(idDispositivoDos, fila["id_dispositivo"]);
        var cuerpo = JsonDocument.Parse(Assert.Single(Creaciones(e)).Cuerpo).RootElement;
        Assert.Equal($"WAYSPV{idOtroPv}", cuerpo.GetProperty("config").GetProperty("qr").GetProperty("external_pos_id").GetString());
    }

    [Fact]
    public async Task UnaSesionWebSinDispositivoRecibe403EnLasTresRutas()
    {
        var e = await PrepararAsync("mp-cobro-sin-dispositivo");
        var cobro = await CrearOkAsync(e, 5019, 100m);

        foreach (var web in new[] { e.Ctx.Admin, e.Ctx.Vendedor })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await CrearAsync(e, 5020, 100m, cliente: web)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ConsultarAsync(e, cobro.IdCobro, web)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await CancelarAsync(e, cobro.IdCobro, web)).StatusCode);
        }

        using var anonimo = e.Ctx.Host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync($"{Ruta}/{cobro.IdCobro}")).StatusCode);
        Assert.Single(Creaciones(e));
    }

    // ---- Consultar: estados y limite de refresco -------------------------------------------------------------------------

    // Clausula: processed -> aprobado con el pago, la referencia numerica y el importe pagado.
    [Fact]
    public async Task ConsultarUnCobroPagadoLoApruebaGuardandoElPagoYLaReferencia()
    {
        var e = await PrepararAsync("mp-cobro-aprobado");
        var cobro = await CrearOkAsync(e, 5021, 250.5m);
        var idOrden = e.Ordenes.OrdenPorClave.Values.Single();
        e.Ordenes.Estados[idOrden] = "processed";

        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro));

        Assert.Equal("aprobado", consultado.Estado);
        Assert.Null(consultado.QrData);
        Assert.Equal(250.5m, consultado.ImportePagado);
        Assert.False(consultado.ImporteDistinto);
        var fila = Assert.Single(await FilasAsync(e.IdPuntoVenta));
        Assert.Equal("aprobado", fila["estado"]);
        Assert.Equal($"PAY{idOrden}", fila["id_pago_mercadopago"]);
        Assert.Equal(9876543L, fila["referencia_pago_mercadopago"]);
        Assert.Equal(250.50m, fila["importe_pagado"]);
        Assert.NotNull(fila["fecha_aprobacion"]);
        Assert.NotNull(fila["fecha_ultima_consulta"]);
    }

    [Fact]
    public async Task UnImportePagadoDistintoSigueAprobadoPeroLoAvisa()
    {
        var e = await PrepararAsync("mp-cobro-distinto");
        var cobro = await CrearOkAsync(e, 5022, 100m);
        e.Ordenes.Estados[e.Ordenes.OrdenPorClave.Values.Single()] = "processed";
        e.Ordenes.Pagado = 90m;

        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro));

        Assert.Equal("aprobado", consultado.Estado);
        Assert.Equal(90m, consultado.ImportePagado);
        Assert.True(consultado.ImporteDistinto);
    }

    [Fact]
    public async Task UnReferenceIdQueNoEsNumericoNoImpideAprobarElCobro()
    {
        var e = await PrepararAsync("mp-cobro-referencia-rara");
        var cobro = await CrearOkAsync(e, 5023, 100m);
        e.Ordenes.Estados[e.Ordenes.OrdenPorClave.Values.Single()] = "processed";
        e.Ordenes.ReferenceId = "\"no-es-un-numero\"";

        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro));

        Assert.Equal("aprobado", consultado.Estado);
        Assert.Null(Assert.Single(await FilasAsync(e.IdPuntoVenta))["referencia_pago_mercadopago"]);
    }

    // Clausula: canceled -> cancelado, expired -> expirado, refunded -> devuelto; un estado que Ways no conoce deja el cobro pendiente.
    [Theory]
    [InlineData("canceled", "cancelado")]
    [InlineData("expired", "expirado")]
    [InlineData("refunded", "devuelto")]
    [InlineData("created", "pendiente")]
    [InlineData("at_terminal", "pendiente")]
    public async Task ElEstadoDeLaOrdenSeTraduceAlEstadoDelCobro(string estadoDeMercadoPago, string estadoEsperado)
    {
        var e = await PrepararAsync("mp-cobro-estado-" + estadoDeMercadoPago.Replace("_", string.Empty));
        var cobro = await CrearOkAsync(e, 5024, 100m);
        e.Ordenes.Estados[e.Ordenes.OrdenPorClave.Values.Single()] = estadoDeMercadoPago;

        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro));

        Assert.Equal(estadoEsperado, consultado.Estado);
        Assert.Equal(estadoEsperado, Assert.Single(await FilasAsync(e.IdPuntoVenta))["estado"]);
        Assert.Equal(estadoEsperado == "pendiente", consultado.QrData is not null);
    }

    // Clausula: a lo sumo una lectura de la orden cada 2 segundos por cobro (fecha_ultima_consulta).
    [Fact]
    public async Task UnCobroPendienteSeRefrescaComoMuchoUnaVezCada2Segundos()
    {
        var e = await PrepararAsync("mp-cobro-limite");
        var cobro = await CrearOkAsync(e, 5025, 100m);

        await ConsultarAsync(e, cobro.IdCobro);
        Assert.Single(Lecturas(e));

        e.Reloj.Ahora = e.Reloj.Ahora.AddMilliseconds(1999);
        await ConsultarAsync(e, cobro.IdCobro);
        await ConsultarAsync(e, cobro.IdCobro);
        Assert.Single(Lecturas(e));

        e.Reloj.Ahora = e.Reloj.Ahora.AddMilliseconds(1);
        await ConsultarAsync(e, cobro.IdCobro);
        Assert.Equal(2, Lecturas(e).Count);
    }

    // Clausula: el lock por cobro serializa dos consultas simultaneas, que juntas hacen una sola lectura.
    [Fact]
    public async Task DosConsultasSimultaneasDelMismoCobroHacenUnaSolaLecturaDeLaOrden()
    {
        var e = await PrepararAsync("mp-cobro-limite-lock");
        var cobro = await CrearOkAsync(e, 5026, 100m);

        var detenido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primera = 0;
        e.Ctx.MercadoPago.AntesDeResponder = async () =>
        {
            if (Interlocked.Increment(ref primera) == 1)
            {
                detenido.TrySetResult();
                await liberar.Task;
            }
        };

        var uno = ConsultarAsync(e, cobro.IdCobro);
        await detenido.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var dos = ConsultarAsync(e, cobro.IdCobro);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(dos.IsCompleted);

        liberar.SetResult();
        foreach (var respuesta in await Task.WhenAll(uno, dos))
        {
            Assert.Equal("pendiente", (await LeerAsync<CobroDeMercadoPago>(respuesta)).Estado);
        }

        Assert.Single(Lecturas(e));
    }

    [Fact]
    public async Task UnCobroQueYaNoEstaPendienteSeDevuelveSinLlamarAMercadoPago()
    {
        var e = await PrepararAsync("mp-cobro-final");
        var cobro = await CrearOkAsync(e, 5027, 100m);
        await CancelarAsync(e, cobro.IdCobro);

        var consultado = await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro));

        Assert.Equal("cancelado", consultado.Estado);
        Assert.Empty(Lecturas(e));
    }

    [Fact]
    public async Task SiMercadoPagoNoEstaDisponibleAlConsultarElCobroSigueConsultableYPendiente()
    {
        var e = await PrepararAsync("mp-cobro-caido");
        var cobro = await CrearOkAsync(e, 5028, 100m);
        e.Ordenes.Antes = p => p.Metodo == HttpMethod.Get ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;

        await AssertCodigoAsync(
            await ConsultarAsync(e, cobro.IdCobro), HttpStatusCode.ServiceUnavailable, CodigosDeErrorDeMercadoPago.NoDisponible);

        e.Ordenes.Antes = null;
        Assert.Equal("pendiente", (await LeerAsync<CobroDeMercadoPago>(await ConsultarAsync(e, cobro.IdCobro))).Estado);
        Assert.Equal("pendiente", Assert.Single(await FilasAsync(e.IdPuntoVenta))["estado"]);
    }

    // ---- Cancelar --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CancelarUnCobroPendienteCancelaLaOrdenConUnaClaveDeIdempotenciaYEsIdempotente()
    {
        var e = await PrepararAsync("mp-cobro-cancelar");
        var cobro = await CrearOkAsync(e, 5029, 100m);
        var idOrden = e.Ordenes.OrdenPorClave.Values.Single();

        var cancelado = await LeerAsync<CobroDeMercadoPago>(await CancelarAsync(e, cobro.IdCobro));

        Assert.Equal("cancelado", cancelado.Estado);
        Assert.Null(cancelado.QrData);
        var pedido = Assert.Single(Cancelaciones(e));
        Assert.Equal($"/v1/orders/{idOrden}/cancel", pedido.Uri.AbsolutePath);
        Assert.Equal($"ways-cobro-{cobro.IdCobro}-cancelar", pedido.ClaveDeIdempotencia);
        Assert.Equal("cancelado", Assert.Single(await FilasAsync(e.IdPuntoVenta))["estado"]);

        var otraVez = await LeerAsync<CobroDeMercadoPago>(await CancelarAsync(e, cobro.IdCobro));
        Assert.Equal("cancelado", otraVez.Estado);
        Assert.Single(Cancelaciones(e));
    }

    // Clausula: un 409 de Mercado Pago al cancelar lee la orden y devuelve su estado real, que puede ser aprobado.
    [Fact]
    public async Task UnCancelarQueContesta409LeeLaOrdenYSiSePagoDevuelveAprobado()
    {
        var e = await PrepararAsync("mp-cobro-cancelar-409");
        var cobro = await CrearOkAsync(e, 5030, 100m);
        var idOrden = e.Ordenes.OrdenPorClave.Values.Single();
        e.Ordenes.Cancelar = id =>
        {
            e.Ordenes.Estados[id] = "processed";
            return Json(HttpStatusCode.Conflict, """{"error": "cannot_cancel_order", "message": "order already processed"}""");
        };

        var resultado = await LeerAsync<CobroDeMercadoPago>(await CancelarAsync(e, cobro.IdCobro));

        Assert.Equal("aprobado", resultado.Estado);
        Assert.Equal(100m, resultado.ImportePagado);
        Assert.Single(Cancelaciones(e));
        var lectura = Assert.Single(Lecturas(e));
        Assert.Equal($"/v1/orders/{idOrden}", lectura.Uri.AbsolutePath);
        var fila = Assert.Single(await FilasAsync(e.IdPuntoVenta));
        Assert.Equal("aprobado", fila["estado"]);
        Assert.Equal($"PAY{idOrden}", fila["id_pago_mercadopago"]);
    }

    [Fact]
    public async Task UnCancelarQueContesta409SobreUnaOrdenQueSigueCreadaDejaElCobroPendiente()
    {
        var e = await PrepararAsync("mp-cobro-cancelar-409-sigue");
        var cobro = await CrearOkAsync(e, 5031, 100m);
        e.Ordenes.Cancelar = _ => Json(HttpStatusCode.Conflict, """{"error": "cannot_cancel_order"}""");

        var resultado = await LeerAsync<CobroDeMercadoPago>(await CancelarAsync(e, cobro.IdCobro));

        Assert.Equal("pendiente", resultado.Estado);
        Assert.NotNull(resultado.QrData);
        Assert.Equal("pendiente", Assert.Single(await FilasAsync(e.IdPuntoVenta))["estado"]);
    }

    [Fact]
    public async Task CancelarUnCobroAprobadoEsUn409SinLlamarAMercadoPago()
    {
        var e = await PrepararAsync("mp-cobro-cancelar-aprobado");
        var cobro = await CrearOkAsync(e, 5032, 100m);
        e.Ordenes.Estados[e.Ordenes.OrdenPorClave.Values.Single()] = "processed";
        await ConsultarAsync(e, cobro.IdCobro);

        await AssertCodigoAsync(await CancelarAsync(e, cobro.IdCobro), HttpStatusCode.Conflict, "cobro_mercadopago_ya_aprobado");

        Assert.Empty(Cancelaciones(e));
        Assert.Equal("aprobado", Assert.Single(await FilasAsync(e.IdPuntoVenta))["estado"]);
    }

    [Fact]
    public async Task UnoDe401AlCancelarDejaLaConexionEnRequiereReconexionYElCobroPendiente()
    {
        var e = await PrepararAsync("mp-cobro-cancelar-401");
        var cobro = await CrearOkAsync(e, 5033, 100m);
        e.Ordenes.Cancelar = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        await AssertCodigoAsync(
            await CancelarAsync(e, cobro.IdCobro), HttpStatusCode.Conflict, CodigosDeErrorDeMercadoPago.RequiereReconexion);

        var conexion = Assert.Single(await _apoyo.FilasAsync(e.Ctx.IdEmpresa), f => f.Estado != "desconectada");
        Assert.Equal("requiere_reconexion", conexion.Estado);
        Assert.Equal("pendiente", Assert.Single(await FilasAsync(e.IdPuntoVenta))["estado"]);
    }

    // ---- RLS -------------------------------------------------------------------------------------------------------------

    // Clausula: HabilitarRlsDeTenant -- la conexion ways_app de otro tenant no ve la fila.
    [Fact]
    public async Task OtroTenantNoVeLosCobrosPorRls()
    {
        var a = await PrepararAsync("mp-cobro-rls-a");
        var b = await PrepararAsync("mp-cobro-rls-b");
        await CrearOkAsync(a, 5034, 100m);

        async Task<long> ContarAsync(int idTenant)
        {
            await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
            await using var comando = new NpgsqlCommand("SELECT count(*) FROM cobros_mercadopago", conexion);
            return (long)(await comando.ExecuteScalarAsync())!;
        }

        Assert.Equal(1, await ContarAsync(a.IdTenant));
        Assert.Equal(0, await ContarAsync(b.IdTenant));
    }

    private static class EstadosDeCobroMercadoPagoTexto
    {
        public const string Pendiente = "pendiente";
    }
}
