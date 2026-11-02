using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Dispositivos;
using Ways.Application.CuentaCorriente;
using Ways.Application.MercadoPago;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using static Ways.IntegrationTests.ApoyoDeMercadoPago;

namespace Ways.IntegrationTests;

/// <summary>
/// La venta del POS cobrada con Mercado Pago (E3-T2) contra Postgres real y el manejador falso de Mercado Pago: el pago con
/// <c>IdCobroMercadoPago</c> liga el cobro aprobado a la venta y toma su referencia del pago de Mercado Pago; cada rechazo de forma (400)
/// y de cobro (409); el reenvío de la misma venta; el cobro que ya pagó otra venta; los índices y claves nuevos con su SQLSTATE y su
/// traducción; y que el cambio de medios y los recibos no puedan tocar ni agregar un pago de Mercado Pago.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MercadoPagoVentaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Ruta = "/api/pos/mercadopago/cobros";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";
    private const string BusquedaVacia = """{"paging": {"total": 0, "offset": 0, "limit": 30}, "data": []}""";
    private const decimal PrecioDelArticulo = 100m;

    private static long _siguienteLocal = 780_000;
    private static long _siguientePos = 790_000;
    private static int _siguienteOrden;

    private readonly ApoyoDeMercadoPago _apoyo = new(fixture);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed class OrdenesFalsas
    {
        public ConcurrentDictionary<string, string> OrdenPorClave { get; } = new();

        public ConcurrentDictionary<string, string> Estados { get; } = new();

        public ConcurrentDictionary<string, decimal> Totales { get; } = new();

        /// <summary>Lo que Mercado Pago informa como pagado; por defecto, el total de la orden.</summary>
        public decimal? Pagado { get; set; }
    }

    private sealed class Escenario
    {
        public required Contexto Ctx { get; init; }

        public required OrdenesFalsas Ordenes { get; init; }

        public required int IdSucursal { get; init; }

        public required int IdPuntoVenta { get; init; }

        public required int IdMedio { get; init; }

        public required int IdEfectivo { get; init; }

        public required int IdArticulo { get; init; }

        public required int IdCliente { get; init; }

        public required int IdDispositivo { get; init; }

        public required HttpClient Cajero { get; init; }

        /// <summary>Primer número del bloque reservado; los siguientes hasta <see cref="Desde"/> + 19 son del mismo dispositivo.</summary>
        public required long Desde { get; init; }

        public int IdTenant => Ctx.IdTenant;
    }

    // ---- Mercado Pago falso -----------------------------------------------------------------------------------------------

    private static HttpResponseMessage Json(HttpStatusCode estado, string json) =>
        new(estado) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string JsonDeOrden(OrdenesFalsas o, string id)
    {
        var estado = o.Estados.GetValueOrDefault(id, "created");
        var pagado = (o.Pagado ?? o.Totales.GetValueOrDefault(id, 0m)).ToString("F2", CultureInfo.InvariantCulture);
        var pagos = estado == "processed"
            ? "\"transactions\": {\"payments\": [{\"id\": \"PAY" + id + "\", \"status\": \"processed\", \"amount\": \""
                + pagado + "\", \"paid_amount\": \"" + pagado + "\", \"reference_id\": 9876543}]},"
            : string.Empty;
        return "{ " + pagos + " \"id\": \"" + id + "\", \"status\": \"" + estado + "\", \"type_response\": {\"qr_data\": \"000201QR" + id + "\"} }";
    }

    private static void Guionar(Contexto ctx, OrdenesFalsas ordenes) =>
        ctx.MercadoPago.ResponderPedido = pedido =>
        {
            if (pedido.Uri.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                return Exitoso(AccessToken, RefreshToken, ctx.MercadoPago.IdUsuario);
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
                // El QR lleva total_amount; la orden Point no (solo transactions.payments[{amount}]).
                var textoDelTotal = cuerpo.RootElement.TryGetProperty("total_amount", out var total)
                    ? total.GetString()!
                    : cuerpo.RootElement.GetProperty("transactions").GetProperty("payments")[0].GetProperty("amount").GetString()!;
                ordenes.Totales[id] = decimal.Parse(textoDelTotal, CultureInfo.InvariantCulture);
                return Json(HttpStatusCode.Created, JsonDeOrden(ordenes, id));
            }

            if (ruta.StartsWith("/v1/orders/", StringComparison.Ordinal) && pedido.Metodo == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, JsonDeOrden(ordenes, ruta.Split('/')[3]));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

    // ---- Siembra ------------------------------------------------------------------------------------------------------------

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

    private static async Task<(HttpClient Cajero, int IdDispositivo)> VincularCajeroAsync(Contexto ctx, int idPuntoVenta)
    {
        var alta = await ctx.Admin.PostAsJsonAsync("/api/dispositivos", new AltaDispositivo(idPuntoVenta, "Caja venta"));
        var vinculado = await LeerAsync<DispositivoVinculado>(alta, HttpStatusCode.Created);
        var cookie = ExtraerCookieDeDispositivo(alta);

        var usuario = $"cajero-venta-{Guid.NewGuid().ToString("N")[..6]}";
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

    private static async Task<int> CrearMedioConProveedorAsync(Contexto ctx, string nombre)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/catalogos/medios-pago", new Dictionary<string, object?>
        {
            ["nombre"] = nombre,
            ["idEmpresa"] = ctx.IdEmpresa,
            ["orden"] = 9,
            ["comportamiento"] = "Electronico",
            ["admiteVuelto"] = false,
            ["requiereReferencia"] = false,
            ["recargoPorcentaje"] = null,
            ["proveedor"] = "mercadopago_qr",
            ["activo"] = true
        });
        return (await LeerAsync<JsonElement>(respuesta, HttpStatusCode.Created)).GetProperty("id").GetInt32();
    }

    /// <summary>Un tenant conectado a Mercado Pago, con un punto de venta de escritorio y su caja, un medio con proveedor, un
    /// cajero de dispositivo, un turno abierto, un servicio de 100 en la lista general, un cliente con cuenta corriente y un bloque
    /// de 20 números reservado.</summary>
    private async Task<Escenario> PrepararAsync(string nombre)
    {
        var ctx = await _apoyo.PrepararAsync(nombre, reloj: new RelojControlable(DateTimeOffset.UtcNow));
        var ordenes = new OrdenesFalsas();
        Guionar(ctx, ordenes);

        var (_, estado) = await IniciarAsync(ctx);
        Assert.Equal(HttpStatusCode.OK, (await CompletarAsync(ctx, estado)).StatusCode);

        var sucursal = await ctx.Admin.PostAsJsonAsync(
            "/api/sucursales",
            new SucursalAlta(ctx.IdEmpresa, "Sucursal Venta", "Av. Córdoba", "1234 bis", "Rosario", "Santa Fe", "2000", -32.944243m, -60.650539m, "Frente a la plaza"));
        var idSucursal = (await LeerAsync<SucursalListado>(sucursal, HttpStatusCode.Created)).Id;
        var puntoVenta = await ctx.Admin.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(ctx.IdEmpresa, "Caja Venta", ModoPuntoVenta.Escritorio, IdSucursal: idSucursal));
        var idPuntoVenta = (await LeerAsync<PuntoVentaListado>(puntoVenta, HttpStatusCode.Created)).Id;
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/sucursales/{idSucursal}/mercadopago", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/puntos-venta/{idPuntoVenta}/mercadopago", null)).StatusCode);

        var idMedio = await CrearMedioConProveedorAsync(ctx, "QR Mercado Pago");
        var (cajero, idDispositivo) = await VincularCajeroAsync(ctx, idPuntoVenta);

        int idArticulo, idEfectivo, idCliente;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.TurnosCaja.Add(new TurnoCaja
            {
                IdTenant = ctx.IdTenant, IdPuntoVenta = idPuntoVenta, IdEmpleadoApertura = ctx.IdUsuarioAdmin, FechaApertura = ahora,
                FondoInicial = 0m, Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
            });

            var area = new Area { IdTenant = ctx.IdTenant, Nombre = "Ventas", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
            db.Areas.Add(area);
            await db.SaveChangesAsync();

            var idListaGeneral = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();
            var articulo = new Articulo
            {
                IdTenant = ctx.IdTenant, CodigoInterno = "servicio-mp", Nombre = "Servicio de prueba", IdArea = area.Id,
                IdAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync(), UnidadVenta = UnidadVenta.Unidad,
                EsProducto = false, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Articulos.Add(articulo);
            var cliente = new Cliente
            {
                IdTenant = ctx.IdTenant, Numero = 5000, Nombre = "Cliente con cuenta",
                IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(),
                IdListaPrecio = idListaGeneral, LimiteCredito = 1000m, Saldo = 0m, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Clientes.Add(cliente);
            await db.SaveChangesAsync();

            db.Precios.Add(new Precio
            {
                IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = idListaGeneral, Monto = PrecioDelArticulo,
                VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();

            idArticulo = articulo.Id;
            idCliente = cliente.Id;
            idEfectivo = await db.MediosPago.Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
        }

        var reserva = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 20));
        var bloque = await LeerAsync<BloqueDeNumeracionReservado>(reserva);

        return new Escenario
        {
            Ctx = ctx,
            Ordenes = ordenes,
            IdSucursal = idSucursal,
            IdPuntoVenta = idPuntoVenta,
            IdMedio = idMedio,
            IdEfectivo = idEfectivo,
            IdArticulo = idArticulo,
            IdCliente = idCliente,
            IdDispositivo = idDispositivo,
            Cajero = cajero,
            Desde = bloque.Desde
        };
    }

    /// <summary>Crea un cobro por el endpoint del dispositivo y lo aprueba como lo hace el polling: la orden falsa pasa a
    /// <c>processed</c> y la consulta lo refresca. Una sola orden sin aprobar a la vez.</summary>
    private static async Task<CobroDeMercadoPago> CobroAprobadoAsync(
        Escenario e, long numero, decimal importe = PrecioDelArticulo, int? idMedio = null)
    {
        var creado = await CobroPendienteAsync(e, numero, importe, idMedio);
        var orden = e.Ordenes.Estados.Single(kv => kv.Value == "created").Key;
        e.Ordenes.Estados[orden] = "processed";

        var aprobado = await LeerAsync<CobroDeMercadoPago>(await e.Cajero.GetAsync($"{Ruta}/{creado.IdCobro}"));
        Assert.Equal("aprobado", aprobado.Estado);
        return aprobado;
    }

    private static async Task<CobroDeMercadoPago> CobroPendienteAsync(
        Escenario e, long numero, decimal importe = PrecioDelArticulo, int? idMedio = null)
    {
        var creado = await LeerAsync<CobroDeMercadoPago>(
            await e.Cajero.PostAsJsonAsync(Ruta, new SolicitudDeCobroMercadoPago(idMedio ?? e.IdMedio, numero, importe)));
        Assert.Equal("pendiente", creado.Estado);
        return creado;
    }

    private static SolicitudDeVenta Venta(Escenario e, long? numero, params PagoDeVenta[] pagos) =>
        new(e.IdPuntoVenta, null, "TX", null,
            [new LineaDeVenta(e.IdArticulo, 1m, null, PrecioUnitario: numero is null ? null : PrecioDelArticulo)],
            pagos, null, null, NumeroPreasignado: numero);

    private static PagoDeVenta PagoMp(Escenario e, int? idCobro, decimal importe = PrecioDelArticulo, string? referencia = null, int? idMedio = null) =>
        new(idMedio ?? e.IdMedio, importe, referencia, 0m, idCobro);

    private static PagoDeVenta PagoEfectivo(Escenario e, decimal importe = PrecioDelArticulo, int? idCobro = null) =>
        new(e.IdEfectivo, importe, null, 0m, idCobro);

    private static Task<HttpResponseMessage> VenderAsync(Escenario e, SolicitudDeVenta solicitud) =>
        e.Cajero.PostAsJsonAsync("/api/ventas", solicitud);

    // ---- Lectura cruda -------------------------------------------------------------------------------------------------------

    private async Task<List<Dictionary<string, object?>>> ConsultarAsync(string sql, params object[] parametros)
    {
        var filas = new List<Dictionary<string, object?>>();
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var parametro in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = parametro });
        }

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

    private async Task EjecutarAsync(string sql, params object[] parametros)
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

    private async Task<int?> VentaLigadaAsync(int idCobro) =>
        (await ConsultarAsync("SELECT id_comprobante_venta FROM cobros_mercadopago WHERE id_cobro_mercadopago = $1", idCobro))
            .Single()["id_comprobante_venta"] as int?;

    private async Task<List<Dictionary<string, object?>>> PagosDeLaVentaAsync(int idVenta) =>
        await ConsultarAsync(
            "SELECT id_medio_pago, importe, referencia, id_cobro_mercadopago, verificacion_mercadopago FROM cobros WHERE id_comprobante_venta = $1 AND deleted_at IS NULL ORDER BY id_cobro",
            idVenta);

    private async Task<long> CantidadDeVentasAsync(Escenario e) =>
        (long)(await ConsultarAsync("SELECT count(*) AS n FROM comprobantes_venta WHERE id_punto_venta = $1", e.IdPuntoVenta)).Single()["n"]!;

    /// <summary>Un cobro de otra clase (cancelado, intento 2) del mismo número, que no ocupa el número vivo.</summary>
    private async Task<int> InsertarCobroNoVivoAsync(Escenario e, long numero, int intento)
    {
        var filas = await ConsultarAsync(
            "INSERT INTO cobros_mercadopago (id_tenant, id_empresa, id_punto_venta, id_dispositivo, id_medio_pago, numero_preasignado, " +
            "intento, tipo, importe, external_reference, external_pos_id, clave_idempotencia, id_orden_mercadopago, estado, expira_en, " +
            "created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, 'qr', 100, $8, 'WAYSPVCRUDO', $8, $9, 'cancelado', now(), now(), now()) " +
            "RETURNING id_cobro_mercadopago",
            e.IdTenant, e.Ctx.IdEmpresa, e.IdPuntoVenta, e.IdDispositivo, e.IdMedio, numero, intento,
            $"crudo-{Guid.NewGuid():N}", $"ORDCRUDA{Guid.NewGuid():N}"[..20]);
        return (int)filas.Single()["id_cobro_mercadopago"]!;
    }

    // ---- La venta liga el cobro ------------------------------------------------------------------------------------------------

    // Clausula: EmitirAsync liga el cobro aprobado (LigarAsync) y persiste id_cobro_mercadopago en el pago (la entidad Cobro); la
    // referencia del pago sale del id del pago de Mercado Pago y no de lo que mande el cliente.
    [Fact]
    public async Task UnaVentaConUnCobroAprobadoLoLigaGuardaSuReferenciaYElPagoLoRecuerda()
    {
        var e = await PrepararAsync("mp-venta-liga");
        var cobro = await CobroAprobadoAsync(e, e.Desde);

        var respuesta = await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro)));

        var emitido = await LeerAsync<ComprobanteEmitido>(respuesta, HttpStatusCode.Created);
        Assert.Equal(cobro.IdCobro, Assert.Single(emitido.Pagos).IdCobroMercadoPago);
        Assert.Equal(emitido.Id, await VentaLigadaAsync(cobro.IdCobro));

        var pago = Assert.Single(await PagosDeLaVentaAsync(emitido.Id));
        Assert.Equal(e.IdMedio, pago["id_medio_pago"]);
        Assert.Equal(PrecioDelArticulo, pago["importe"]);
        Assert.Equal(cobro.IdCobro, pago["id_cobro_mercadopago"]);
        Assert.Equal("mercadopago", pago["verificacion_mercadopago"]);
        Assert.Equal("mercadopago", Assert.Single(emitido.Pagos).VerificacionMercadoPago);
        var idOrden = Assert.Single(e.Ordenes.Estados).Key;
        Assert.Equal($"PAY{idOrden}", pago["referencia"]);
        Assert.Equal($"PAY{idOrden}", Assert.Single(emitido.Pagos).Referencia);
    }

    // Clausula: el GET del cobro avisa usadoEnVenta cuando una venta ya quedo ligada; el dispositivo descarta con eso su reserva local.
    [Fact]
    public async Task ElCobroConsultadoAvisaCuandoUnaVentaYaLoUso()
    {
        var e = await PrepararAsync("mp-venta-usado");
        var cobro = await CobroAprobadoAsync(e, e.Desde);
        Assert.False(cobro.UsadoEnVenta);

        var emitido = await LeerAsync<ComprobanteEmitido>(await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro))), HttpStatusCode.Created);

        var consultado = await LeerAsync<CobroDeMercadoPago>(await e.Cajero.GetAsync($"{Ruta}/{cobro.IdCobro}"));
        Assert.Equal(emitido.Id, await VentaLigadaAsync(cobro.IdCobro));
        Assert.True(consultado.UsadoEnVenta);
    }

    // Clausula: el importe se compara contra lo que Mercado Pago cobro (importe_pagado) y no contra el pedido; una venta mixta con
    // efectivo por el resto es valida.
    [Fact]
    public async Task ElPagoSeComparaContraLoQueMercadoPagoCobroYNoContraLoPedido()
    {
        var e = await PrepararAsync("mp-venta-pagado");
        e.Ordenes.Pagado = 90m;
        var cobro = await CobroAprobadoAsync(e, e.Desde, importe: 100m);
        Assert.True(cobro.ImporteDistinto);

        var respuesta = await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro, importe: 90m), PagoEfectivo(e, 10m)));

        var emitido = await LeerAsync<ComprobanteEmitido>(respuesta, HttpStatusCode.Created);
        Assert.Equal(2, emitido.Pagos.Count);
        Assert.Equal(emitido.Id, await VentaLigadaAsync(cobro.IdCobro));
    }

    // ---- Forma del pago (400) -----------------------------------------------------------------------------------------------------

    // Clausula: un medio con proveedor sin cobro es 400 cobro_mercadopago_requerido (el unico camino que lo rechaza es la forma del pago).
    [Fact]
    public async Task UnMedioConProveedorSinCobroEs400()
    {
        var e = await PrepararAsync("mp-venta-requerido");

        var respuesta = await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, null)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "cobro_mercadopago_requerido");
        Assert.Equal(0, await CantidadDeVentasAsync(e));
    }

    // Clausula: un medio sin proveedor no admite cobro.
    [Fact]
    public async Task UnMedioSinProveedorConCobroEs400()
    {
        var e = await PrepararAsync("mp-venta-sin-proveedor");
        var cobro = await CobroAprobadoAsync(e, e.Desde);

        var respuesta = await VenderAsync(e, Venta(e, e.Desde, PagoEfectivo(e, idCobro: cobro.IdCobro)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "cobro_mercadopago_en_medio_sin_proveedor");
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
    }

    // Clausula: el cobro solo se admite con numero preasignado; el cajero de dispositivo sin numero cobra por el camino de siempre.
    [Fact]
    public async Task UnCobroSinNumeroPreasignadoEs400()
    {
        var e = await PrepararAsync("mp-venta-sin-numero");
        var cobro = await CobroAprobadoAsync(e, e.Desde);

        var respuesta = await VenderAsync(e, Venta(e, null, PagoMp(e, cobro.IdCobro)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "cobro_mercadopago_sin_numero_preasignado");
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
    }

    // Clausula: la referencia del cliente no tiene destino en un pago con cobro (se toma del pago de Mercado Pago).
    [Fact]
    public async Task UnPagoConCobroYReferenciaDelClienteEs400()
    {
        var e = await PrepararAsync("mp-venta-referencia");
        var cobro = await CobroAprobadoAsync(e, e.Desde);

        var respuesta = await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro, referencia: "A-1")));

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "cobro_mercadopago_referencia_no_admitida");
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
    }

    // Clausula: una venta admite un solo cobro de Mercado Pago, aunque los dos cobros sean distintos.
    [Fact]
    public async Task DosCobrosDistintosEnUnaVentaEs400()
    {
        var e = await PrepararAsync("mp-venta-dos-cobros");
        var uno = await CobroAprobadoAsync(e, e.Desde, importe: 50m);
        var otro = await CobroAprobadoAsync(e, e.Desde + 1, importe: 50m);

        var respuesta = await VenderAsync(
            e, Venta(e, e.Desde, PagoMp(e, uno.IdCobro, importe: 50m), PagoMp(e, otro.IdCobro, importe: 50m)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "venta_con_mas_de_un_cobro_mercadopago");
        Assert.Null(await VentaLigadaAsync(uno.IdCobro));
        Assert.Null(await VentaLigadaAsync(otro.IdCobro));
    }

    // Clausula: el mismo cobro en dos pagos de la misma venta tambien cuenta como mas de un cobro.
    [Fact]
    public async Task UnMismoCobroEnDosPagosEs400()
    {
        var e = await PrepararAsync("mp-venta-repetido");
        var cobro = await CobroAprobadoAsync(e, e.Desde);

        var respuesta = await VenderAsync(
            e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro, importe: 50m), PagoMp(e, cobro.IdCobro, importe: 50m)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "venta_con_mas_de_un_cobro_mercadopago");
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
    }

    // Clausula: un cobro que no existe en el tenant es 400 y no deja la venta a medias.
    [Fact]
    public async Task UnCobroInexistenteEs400()
    {
        var e = await PrepararAsync("mp-venta-inexistente");

        var respuesta = await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, 987_654_321)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "cobro_mercadopago_inexistente");
        Assert.Equal(0, await CantidadDeVentasAsync(e));
    }

    // ---- El cobro no sirve (409) ---------------------------------------------------------------------------------------------------

    // Clausula (punto de venta): el cobro de otro punto de venta es de otra venta. Se mueve la fila a un segundo punto de venta
    // del mismo tenant; numero, medio, estado e importe siguen coincidiendo, asi que solo el punto de venta lo rechaza.
    [Fact]
    public async Task UnCobroDeOtroPuntoDeVentaEs409DeOtraVenta()
    {
        var e = await PrepararAsync("mp-venta-otro-pv");
        var cobro = await CobroAprobadoAsync(e, e.Desde);
        var otro = await e.Ctx.Admin.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(e.Ctx.IdEmpresa, "Caja Otra", ModoPuntoVenta.Escritorio, IdSucursal: e.IdSucursal));
        var idOtro = (await LeerAsync<PuntoVentaListado>(otro, HttpStatusCode.Created)).Id;
        await EjecutarAsync("UPDATE cobros_mercadopago SET id_punto_venta = $1 WHERE id_cobro_mercadopago = $2", idOtro, cobro.IdCobro);

        var respuesta = await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.Conflict, "cobro_mercadopago_de_otra_venta");
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
    }

    // Clausula (numero): el cobro de otro numero es de otra venta; punto de venta, medio, estado e importe coinciden.
    [Fact]
    public async Task UnCobroDeOtroNumeroEs409DeOtraVenta()
    {
        var e = await PrepararAsync("mp-venta-otro-numero");
        var cobro = await CobroAprobadoAsync(e, e.Desde + 1);

        var respuesta = await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.Conflict, "cobro_mercadopago_de_otra_venta");
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
    }

    // Clausula (medio): el cobro pedido con un medio y presentado con otro medio con proveedor es de otra venta.
    [Fact]
    public async Task UnCobroDeOtroMedioEs409DeOtraVenta()
    {
        var e = await PrepararAsync("mp-venta-otro-medio");
        var otroMedio = await CrearMedioConProveedorAsync(e.Ctx, "QR Otro");
        var cobro = await CobroAprobadoAsync(e, e.Desde);

        var respuesta = await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro, idMedio: otroMedio)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.Conflict, "cobro_mercadopago_de_otra_venta");
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
    }

    // Clausula (estado): un cobro pendiente todavia no se pago.
    [Fact]
    public async Task UnCobroPendienteEs409NoAprobado()
    {
        var e = await PrepararAsync("mp-venta-pendiente");
        var cobro = await CobroPendienteAsync(e, e.Desde);

        var respuesta = await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.Conflict, "cobro_mercadopago_no_aprobado");
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
        Assert.Equal(0, await CantidadDeVentasAsync(e));
    }

    // Clausula (estado): un cobro apartado (sin_venta) no paga ninguna venta: solo el aprobado la paga.
    [Fact]
    public async Task UnCobroApartadoSinVentaEs409NoAprobadoYNoLigaNada()
    {
        var e = await PrepararAsync("mp-venta-sin-venta");
        var cobro = await CobroAprobadoAsync(e, e.Desde);
        var apartado = await LeerAsync<CobroDeMercadoPago>(await e.Cajero.PostAsync($"{Ruta}/{cobro.IdCobro}/apartar", null));
        Assert.Equal("sin_venta", apartado.Estado);

        var respuesta = await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.Conflict, "cobro_mercadopago_no_aprobado");
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
        Assert.Equal(0, await CantidadDeVentasAsync(e));
    }

    // Clausula (apartar): un cobro que una venta ya uso no se aparta (409 ya_usado) y queda ligado.
    [Fact]
    public async Task ApartarUnCobroQueUnaVentaYaUsoEs409YaUsado()
    {
        var e = await PrepararAsync("mp-venta-apartar-usado");
        var cobro = await CobroAprobadoAsync(e, e.Desde);
        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro))), HttpStatusCode.Created);

        var respuesta = await e.Cajero.PostAsync($"{Ruta}/{cobro.IdCobro}/apartar", null);

        await AssertCodigoAsync(respuesta, HttpStatusCode.Conflict, "cobro_mercadopago_ya_usado");
        Assert.Equal(emitido.Id, await VentaLigadaAsync(cobro.IdCobro));
        var fila = (await ConsultarAsync("SELECT estado, fecha_apartado FROM cobros_mercadopago WHERE id_cobro_mercadopago = $1", cobro.IdCobro)).Single();
        Assert.Equal("aprobado", fila["estado"]);
        Assert.Null(fila["fecha_apartado"]);
    }

    // Clausula (importe): el pago por mas o por menos de lo que cobro Mercado Pago no sirve, aunque el total de la venta cierre con efectivo.
    [Theory]
    [InlineData(99.99)]
    [InlineData(100.01)]
    public async Task UnPagoPorOtroImporteQueElCobradoEs409(double importe)
    {
        var e = await PrepararAsync($"mp-venta-importe-{importe.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', '-')}");
        var cobro = await CobroAprobadoAsync(e, e.Desde);
        var pagoMp = (decimal)importe;
        var resto = PrecioDelArticulo - pagoMp;

        var pagos = resto > 0
            ? new[] { PagoMp(e, cobro.IdCobro, importe: pagoMp), PagoEfectivo(e, resto) }
            : new[] { PagoMp(e, cobro.IdCobro, importe: pagoMp) };
        var respuesta = await VenderAsync(e, Venta(e, e.Desde, pagos));

        await AssertCodigoAsync(respuesta, HttpStatusCode.Conflict, "cobro_mercadopago_importe_distinto");
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
    }

    // Clausula (unicidad): un cobro que ya pago otra venta no paga una segunda. Se liga a mano a una venta ajena; el numero, el medio
    // y el importe coinciden, asi que solo la guarda de uso lo rechaza.
    [Fact]
    public async Task UnCobroYaLigadoAOtraVentaEs409YaUsado()
    {
        var e = await PrepararAsync("mp-venta-ya-usado");
        var idVentaAjena = await VentaSembradaAsync(e, e.Desde);
        var cobro = await CobroAprobadoAsync(e, e.Desde + 1);
        await EjecutarAsync(
            "UPDATE cobros_mercadopago SET id_comprobante_venta = $1 WHERE id_cobro_mercadopago = $2", idVentaAjena, cobro.IdCobro);

        var respuesta = await VenderAsync(e, Venta(e, e.Desde + 1, PagoMp(e, cobro.IdCobro)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.Conflict, "cobro_mercadopago_ya_usado");
        Assert.Equal(idVentaAjena, await VentaLigadaAsync(cobro.IdCobro));
        Assert.Equal(1, await CantidadDeVentasAsync(e));
    }

    // Clausula: un cobro que ya pago una venta no sirve para otra venta bajo el mismo numero: es el contenido distinto de siempre.
    [Fact]
    public async Task UnaSegundaVentaBajoElMismoNumeroYElMismoCobroNoCobraDeNuevo()
    {
        var e = await PrepararAsync("mp-venta-segunda");
        var cobro = await CobroAprobadoAsync(e, e.Desde);
        await LeerAsync<ComprobanteEmitido>(await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro))), HttpStatusCode.Created);

        var segunda = await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro, importe: 60m), PagoEfectivo(e, 40m)));

        await AssertCodigoAsync(segunda, HttpStatusCode.Conflict, "numero_preasignado_con_otro_contenido");
        Assert.Equal(1, await CantidadDeVentasAsync(e));
    }

    // ---- Reenvío ---------------------------------------------------------------------------------------------------------------------

    // Clausula: el reenvio de la misma venta devuelve el comprobante emitido; la referencia que el servidor puso en el pago no cuenta
    // para la identidad (el pedido no la trae) y el cobro ligado a ESTA venta no es un cobro ya usado.
    [Fact]
    public async Task ElReenvioDeLaMismaVentaConCobroDevuelveElMismoComprobante()
    {
        var e = await PrepararAsync("mp-venta-reenvio");
        var cobro = await CobroAprobadoAsync(e, e.Desde);
        var solicitud = Venta(e, e.Desde, PagoMp(e, cobro.IdCobro));

        var primera = await LeerAsync<ComprobanteEmitido>(await VenderAsync(e, solicitud), HttpStatusCode.Created);
        var segunda = await LeerAsync<ComprobanteEmitido>(await VenderAsync(e, solicitud), HttpStatusCode.Created);

        Assert.Equal(primera.Id, segunda.Id);
        Assert.Equal(1, await CantidadDeVentasAsync(e));
        Assert.Equal(primera.Id, await VentaLigadaAsync(cobro.IdCobro));
    }

    // Clausula: el cobro entra en el contenido que se compara. Mismo numero, mismo medio, mismo importe y distinto cobro es otra
    // venta (la otra fila no esta viva, por eso se puede nombrar).
    [Fact]
    public async Task ElReenvioConOtroCobroEsContenidoDistinto()
    {
        var e = await PrepararAsync("mp-venta-reenvio-otro-cobro");
        var cobro = await CobroAprobadoAsync(e, e.Desde);
        await LeerAsync<ComprobanteEmitido>(await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro))), HttpStatusCode.Created);
        var otroCobro = await InsertarCobroNoVivoAsync(e, e.Desde, intento: 2);

        var reenvio = await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, otroCobro)));

        await AssertCodigoAsync(reenvio, HttpStatusCode.Conflict, "numero_preasignado_con_otro_contenido");
    }

    // Clausula: el reenvio de la misma venta con otra forma de pago (efectivo en lugar del cobro) tampoco es el mismo pedido.
    [Fact]
    public async Task ElReenvioEnEfectivoDeUnaVentaCobradaConMercadoPagoEsContenidoDistinto()
    {
        var e = await PrepararAsync("mp-venta-reenvio-efectivo");
        var cobro = await CobroAprobadoAsync(e, e.Desde);
        await LeerAsync<ComprobanteEmitido>(await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro))), HttpStatusCode.Created);

        var reenvio = await VenderAsync(e, Venta(e, e.Desde, PagoEfectivo(e)));

        await AssertCodigoAsync(reenvio, HttpStatusCode.Conflict, "numero_preasignado_con_otro_contenido");
    }

    // Clausula: dos pedidos identicos a la vez sobre el mismo cobro: el FOR UPDATE del cobro los serializa; uno emite y el otro
    // devuelve la misma venta o es 409 cobro_mercadopago_ya_usado, nunca un 500 ni dos ventas ni dos vinculos.
    [Fact]
    public async Task DosPedidosIdenticosConElMismoCobroEmitenUnaSolaVenta()
    {
        var e = await PrepararAsync("mp-venta-carrera");
        var cobro = await CobroAprobadoAsync(e, e.Desde);
        var solicitud = Venta(e, e.Desde, PagoMp(e, cobro.IdCobro));

        var respuestas = await Task.WhenAll(VenderAsync(e, solicitud), VenderAsync(e, solicitud));

        Assert.Contains(respuestas, r => r.StatusCode == HttpStatusCode.Created);
        foreach (var respuesta in respuestas.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            await AssertCodigoAsync(respuesta, HttpStatusCode.Conflict, "cobro_mercadopago_ya_usado");
        }

        Assert.Equal(1, await CantidadDeVentasAsync(e));
        Assert.NotNull(await VentaLigadaAsync(cobro.IdCobro));
    }

    // ---- Respaldos de la base ------------------------------------------------------------------------------------------------------

    private static async Task<int> VentaSembradaAsync(Escenario e, long numero)
    {
        var emitido = await LeerAsync<ComprobanteEmitido>(await VenderAsync(e, Venta(e, numero, PagoEfectivo(e))), HttpStatusCode.Created);
        return emitido.Id;
    }

    // Clausula: ux_cobros_mercadopago_comprobante_venta: dos cobros no pagan la misma venta (23505).
    [Fact]
    public async Task LaBaseRechazaDosCobrosLigadosALaMismaVenta()
    {
        var e = await PrepararAsync("mp-venta-unico");
        var idVenta = await VentaSembradaAsync(e, e.Desde);
        var uno = await CobroAprobadoAsync(e, e.Desde + 1);
        var otro = await CobroAprobadoAsync(e, e.Desde + 2);
        await EjecutarAsync("UPDATE cobros_mercadopago SET id_comprobante_venta = $1 WHERE id_cobro_mercadopago = $2", idVenta, uno.IdCobro);

        var rechazo = await Assert.ThrowsAsync<PostgresException>(() =>
            EjecutarAsync("UPDATE cobros_mercadopago SET id_comprobante_venta = $1 WHERE id_cobro_mercadopago = $2", idVenta, otro.IdCobro));

        Assert.Equal("23505", rechazo.SqlState);
        Assert.Equal("ux_cobros_mercadopago_comprobante_venta", rechazo.ConstraintName);

        var (estado, codigo) = await ManejarAsync(rechazo);
        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("cobro_mercadopago_ya_usado", codigo);
    }

    // Clausula: ck_cobros_mercadopago_aprobado_con_pago: un cobro aprobado sin id de pago lo rechaza la base (23514 -> 409 sin_pago).
    [Fact]
    public async Task LaBaseRechazaUnCobroAprobadoSinIdDePago()
    {
        var e = await PrepararAsync("mp-venta-sin-pago");
        var cobro = await CobroAprobadoAsync(e, e.Desde);

        var rechazo = await Assert.ThrowsAsync<PostgresException>(() =>
            EjecutarAsync("UPDATE cobros_mercadopago SET id_pago_mercadopago = NULL WHERE id_cobro_mercadopago = $1", cobro.IdCobro));

        Assert.Equal("23514", rechazo.SqlState);
        Assert.Equal("ck_cobros_mercadopago_aprobado_con_pago", rechazo.ConstraintName);

        var (estado, codigo) = await ManejarAsync(rechazo);
        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("cobro_mercadopago_sin_pago", codigo);
    }

    // Clausula: los cobros sin venta no chocan entre si en el indice unico (los nulos son distintos).
    [Fact]
    public async Task VariosCobrosSinVentaConviveEnElIndiceUnico()
    {
        var e = await PrepararAsync("mp-venta-nulos");
        var uno = await CobroAprobadoAsync(e, e.Desde);
        var otro = await CobroAprobadoAsync(e, e.Desde + 1);

        Assert.Null(await VentaLigadaAsync(uno.IdCobro));
        Assert.Null(await VentaLigadaAsync(otro.IdCobro));
    }

    // Clausula: fk_cobros_mercadopago_comprobante_venta: una venta que no existe no se puede ligar (23503).
    [Fact]
    public async Task LaBaseRechazaLigarUnCobroAUnaVentaInexistente()
    {
        var e = await PrepararAsync("mp-venta-fk-venta");
        var cobro = await CobroAprobadoAsync(e, e.Desde);

        var rechazo = await Assert.ThrowsAsync<PostgresException>(() =>
            EjecutarAsync("UPDATE cobros_mercadopago SET id_comprobante_venta = 2000000000 WHERE id_cobro_mercadopago = $1", cobro.IdCobro));

        Assert.Equal("23503", rechazo.SqlState);
        Assert.Equal("fk_cobros_mercadopago_comprobante_venta", rechazo.ConstraintName);
        var (estado, codigo) = await ManejarAsync(rechazo);
        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("referencia_invalida", codigo);
    }

    // Clausula: fk_cobros_cobro_mercadopago: un pago no nombra un cobro de Mercado Pago que no existe (23503).
    [Fact]
    public async Task LaBaseRechazaUnPagoConUnCobroDeMercadoPagoInexistente()
    {
        var e = await PrepararAsync("mp-venta-fk-cobro");
        var idVenta = await VentaSembradaAsync(e, e.Desde);

        var rechazo = await Assert.ThrowsAsync<PostgresException>(() =>
            EjecutarAsync(
                "INSERT INTO cobros (id_tenant, id_comprobante_venta, id_medio_pago, importe, vuelto, id_cobro_mercadopago, " +
                "verificacion_mercadopago, created_at, updated_at) " +
                "VALUES ($1, $2, $3, 1, 0, 2000000000, 'mercadopago', now(), now())",
                e.IdTenant, idVenta, e.IdMedio));

        Assert.Equal("23503", rechazo.SqlState);
        Assert.Equal("fk_cobros_cobro_mercadopago", rechazo.ConstraintName);
        var (estado, codigo) = await ManejarAsync(rechazo);
        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("referencia_invalida", codigo);
    }

    // ---- Otros escritores de pagos ----------------------------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> CambiarMediosAsync(Escenario e, int idVenta, params PagoDeVenta[] pagos) =>
        e.Ctx.Admin.PutAsJsonAsync($"/api/ventas/administracion/{idVenta}/medios", new SolicitudDeCambioDeMedios(pagos));

    // Clausula: el cambio de medios no toca un pago cobrado con Mercado Pago (la venta y el cobro ligado quedan como estaban).
    [Fact]
    public async Task ElCambioDeMediosDeUnaVentaCobradaConMercadoPagoEs409()
    {
        var e = await PrepararAsync("mp-venta-cambio-cobrada");
        var cobro = await CobroAprobadoAsync(e, e.Desde);
        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro))), HttpStatusCode.Created);

        var respuesta = await CambiarMediosAsync(e, emitido.Id, PagoEfectivo(e));

        await AssertCodigoAsync(respuesta, HttpStatusCode.Conflict, "pago_mercadopago_no_editable");
        Assert.Equal(cobro.IdCobro, Assert.Single(await PagosDeLaVentaAsync(emitido.Id))["id_cobro_mercadopago"]);
        Assert.Equal(emitido.Id, await VentaLigadaAsync(cobro.IdCobro));
    }

    // Clausula: el cambio de medios no agrega un medio con proveedor sin cobro.
    [Fact]
    public async Task ElCambioDeMediosNoAgregaUnMedioConProveedorSinCobro()
    {
        var e = await PrepararAsync("mp-venta-cambio-agrega");
        var idVenta = await VentaSembradaAsync(e, e.Desde);

        var respuesta = await CambiarMediosAsync(e, idVenta, PagoMp(e, null));

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "cobro_mercadopago_requerido");
        Assert.Equal(e.IdEfectivo, Assert.Single(await PagosDeLaVentaAsync(idVenta))["id_medio_pago"]);
    }

    // Clausula: el cambio de medios tampoco admite un cobro (no tiene numero preasignado).
    [Fact]
    public async Task ElCambioDeMediosNoAdmiteUnCobroDeMercadoPago()
    {
        var e = await PrepararAsync("mp-venta-cambio-cobro");
        var idVenta = await VentaSembradaAsync(e, e.Desde);
        var cobro = await CobroAprobadoAsync(e, e.Desde + 1);

        var respuesta = await CambiarMediosAsync(e, idVenta, PagoMp(e, cobro.IdCobro));

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "cobro_mercadopago_sin_numero_preasignado");
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
    }

    // Clausula: un recibo (cobro de cuenta corriente) no puede usar un medio con proveedor: no hay cobro que lo respalde.
    [Fact]
    public async Task UnReciboConUnMedioConProveedorEs400()
    {
        var e = await PrepararAsync("mp-venta-recibo");

        var respuesta = await e.Cajero.PostAsJsonAsync(
            $"/api/clientes/{e.IdCliente}/cuenta-corriente/pagos",
            new SolicitudDePagoACuenta(e.IdPuntoVenta, [new PagoDeCuenta(e.IdMedio, 50m, null, 0m)], null));

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "cobro_mercadopago_requerido");
    }

    // Clausula: la facturacion de remitos no tiene numero preasignado, asi que ni agrega un medio con proveedor sin cobro ni admite un
    // cobro. El remito se emite y no se factura.
    [Fact]
    public async Task LaFacturacionDeRemitosRechazaUnMedioConProveedorYUnPagoConCobro()
    {
        var e = await PrepararAsync("mp-venta-remitos");
        int idProducto;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, e.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var servicio = await db.Articulos.SingleAsync(a => a.Id == e.IdArticulo);
            var producto = new Articulo
            {
                IdTenant = e.IdTenant, CodigoInterno = "producto-mp", Nombre = "Producto de prueba", IdArea = servicio.IdArea,
                IdAlicuotaIva = servicio.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad, EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Articulos.Add(producto);
            await db.SaveChangesAsync();
            db.Precios.Add(new Precio
            {
                IdTenant = e.IdTenant, IdArticulo = producto.Id, IdListaPrecio = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync(),
                Monto = PrecioDelArticulo, VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
            db.Stock.Add(new Ways.Domain.Stock.Stock { IdArticulo = producto.Id, IdSucursal = e.IdSucursal, IdTenant = e.IdTenant, Cantidad = 10m });
            await db.SaveChangesAsync();
            idProducto = producto.Id;
        }

        var creado = await e.Cajero.PostAsJsonAsync(
            "/api/remitos", new SolicitudDeRemito(e.IdPuntoVenta, e.IdCliente, null, null, [new LineaDeRemito(idProducto, 1m, null)]));
        var remito = await LeerAsync<RemitoDetalle>(creado, HttpStatusCode.Created);
        await LeerAsync<RemitoDetalle>(await e.Cajero.PostAsync($"/api/remitos/{remito.Id}/emitir", null));
        var cobro = await CobroAprobadoAsync(e, e.Desde);

        var sinCobro = await e.Cajero.PostAsJsonAsync(
            "/api/remitos/facturacion", new SolicitudDeFacturacionDeRemitos(e.IdPuntoVenta, [remito.Id], [PagoMp(e, null)], null));
        var conCobro = await e.Cajero.PostAsJsonAsync(
            "/api/remitos/facturacion", new SolicitudDeFacturacionDeRemitos(e.IdPuntoVenta, [remito.Id], [PagoMp(e, cobro.IdCobro)], null));

        await AssertCodigoAsync(sinCobro, HttpStatusCode.BadRequest, "cobro_mercadopago_requerido");
        await AssertCodigoAsync(conCobro, HttpStatusCode.BadRequest, "cobro_mercadopago_sin_numero_preasignado");
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
    }

    // ---- E5: aprobacion manual sin verificar -----------------------------------------------------------------------------------

    private static PagoDeVenta PagoManual(Escenario e, string? referencia = null, decimal importe = PrecioDelArticulo, int? idMedio = null) =>
        new(idMedio ?? e.IdMedio, importe, referencia, 0m, null, AprobacionManualSinVerificar: true);

    // Clausula: la aprobacion manual emite la venta SIN cobro ni orden: el pago queda con verificacion sin_verificar, la referencia
    // del cajero se guarda tal cual y no se crea ni se liga ninguna fila de cobros_mercadopago.
    [Fact]
    public async Task UnaAprobacionManualEmiteLaVentaSinCobroYLaMarcaSinVerificar()
    {
        var e = await PrepararAsync("mp-venta-manual");

        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e, "OP-77"))), HttpStatusCode.Created);

        var pagoEmitido = Assert.Single(emitido.Pagos);
        Assert.Null(pagoEmitido.IdCobroMercadoPago);
        Assert.Equal("sin_verificar", pagoEmitido.VerificacionMercadoPago);
        Assert.Equal("OP-77", pagoEmitido.Referencia);
        var pago = Assert.Single(await PagosDeLaVentaAsync(emitido.Id));
        Assert.Null(pago["id_cobro_mercadopago"]);
        Assert.Equal("sin_verificar", pago["verificacion_mercadopago"]);
        Assert.Equal("OP-77", pago["referencia"]);
        Assert.Equal(e.IdMedio, pago["id_medio_pago"]);
        Assert.Equal(
            0L,
            (long)(await ConsultarAsync("SELECT count(*) AS n FROM cobros_mercadopago WHERE id_punto_venta = $1", e.IdPuntoVenta))
                .Single()["n"]!);
    }

    // Clausula: la referencia de la aprobacion manual es opcional (el cajero puede no tener el numero de operacion).
    [Fact]
    public async Task LaAprobacionManualAdmiteNoTraerReferencia()
    {
        var e = await PrepararAsync("mp-venta-manual-sin-referencia");

        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e))), HttpStatusCode.Created);

        var pago = Assert.Single(await PagosDeLaVentaAsync(emitido.Id));
        Assert.Null(pago["referencia"]);
        Assert.Equal("sin_verificar", pago["verificacion_mercadopago"]);
    }

    // Clausula: un pago en efectivo no lleva marca de Mercado Pago.
    [Fact]
    public async Task UnPagoSinMercadoPagoNoLlevaVerificacion()
    {
        var e = await PrepararAsync("mp-venta-sin-marca");

        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoEfectivo(e))), HttpStatusCode.Created);

        Assert.Null(Assert.Single(emitido.Pagos).VerificacionMercadoPago);
        Assert.Null(Assert.Single(await PagosDeLaVentaAsync(emitido.Id))["verificacion_mercadopago"]);
    }

    // Clausula: la aprobacion manual solo existe en una venta del dispositivo con numero preasignado.
    [Fact]
    public async Task LaAprobacionManualSinNumeroPreasignadoEs400()
    {
        var e = await PrepararAsync("mp-venta-manual-sin-numero");

        var respuesta = await VenderAsync(e, Venta(e, null, PagoManual(e)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "aprobacion_manual_solo_offline");
        Assert.Equal(0, await CantidadDeVentasAsync(e));
    }

    // Clausula: un pago lleva exactamente UNO de los dos: el cobro o la aprobacion manual.
    [Fact]
    public async Task ElCobroYLaAprobacionManualJuntosEs400YNoLigaElCobro()
    {
        var e = await PrepararAsync("mp-venta-manual-y-cobro");
        var cobro = await CobroAprobadoAsync(e, e.Desde);

        var respuesta = await VenderAsync(
            e, Venta(e, e.Desde, new PagoDeVenta(e.IdMedio, PrecioDelArticulo, null, 0m, cobro.IdCobro, AprobacionManualSinVerificar: true)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "cobro_mercadopago_y_aprobacion_manual");
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
        Assert.Equal(0, await CantidadDeVentasAsync(e));
    }

    // Clausula: la aprobacion manual solo se admite en un medio con proveedor.
    [Fact]
    public async Task LaAprobacionManualEnUnMedioSinProveedorEs400()
    {
        var e = await PrepararAsync("mp-venta-manual-efectivo");

        var respuesta = await VenderAsync(e, Venta(e, e.Desde, PagoManual(e, idMedio: e.IdEfectivo)));

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "aprobacion_manual_en_medio_sin_proveedor");
        Assert.Equal(0, await CantidadDeVentasAsync(e));
    }

    // Clausula: el reenvio de la misma aprobacion manual devuelve el mismo comprobante; otra referencia u otro respaldo del pago
    // (un cobro en lugar de la aprobacion) es otro contenido bajo el mismo numero.
    [Fact]
    public async Task ElReenvioDeLaMismaAprobacionManualDevuelveElMismoComprobanteYOtroContenidoEs409()
    {
        var e = await PrepararAsync("mp-venta-manual-reenvio");
        var solicitud = Venta(e, e.Desde, PagoManual(e, "OP-1"));
        var primera = await LeerAsync<ComprobanteEmitido>(await VenderAsync(e, solicitud), HttpStatusCode.Created);

        var segunda = await LeerAsync<ComprobanteEmitido>(await VenderAsync(e, solicitud), HttpStatusCode.Created);
        var otraReferencia = await VenderAsync(e, Venta(e, e.Desde, PagoManual(e, "OP-2")));
        // Un cobro aprobado DESPUES de la venta manual quedaria sin_venta: se siembra aprobado en otro numero y se reasigna a este.
        var cobro = await CobroAprobadoAsync(e, e.Desde + 5);
        await EjecutarAsync("UPDATE cobros_mercadopago SET numero_preasignado = $1 WHERE id_cobro_mercadopago = $2", e.Desde, cobro.IdCobro);
        var conCobro = await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro)));

        Assert.Equal(primera.Id, segunda.Id);
        Assert.Equal("sin_verificar", Assert.Single(segunda.Pagos).VerificacionMercadoPago);
        Assert.Equal(1, await CantidadDeVentasAsync(e));
        await AssertCodigoAsync(otraReferencia, HttpStatusCode.Conflict, "numero_preasignado_con_otro_contenido");
        await AssertCodigoAsync(conCobro, HttpStatusCode.Conflict, "numero_preasignado_con_otro_contenido");
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
    }

    // Clausula: una venta cobrada con Mercado Pago no se reenvia como aprobacion manual (el reverso del test anterior).
    [Fact]
    public async Task ElReenvioDeUnaVentaConCobroComoAprobacionManualEsContenidoDistinto()
    {
        var e = await PrepararAsync("mp-venta-reenvio-como-manual");
        var cobro = await CobroAprobadoAsync(e, e.Desde);
        await LeerAsync<ComprobanteEmitido>(await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro))), HttpStatusCode.Created);

        var reenvio = await VenderAsync(e, Venta(e, e.Desde, PagoManual(e)));

        await AssertCodigoAsync(reenvio, HttpStatusCode.Conflict, "numero_preasignado_con_otro_contenido");
    }

    // Clausula: el cambio de medios trata un pago sin verificar como uno cobrado: 409 pago_mercadopago_no_editable y nada cambia.
    [Fact]
    public async Task ElCambioDeMediosDeUnPagoSinVerificarEs409()
    {
        var e = await PrepararAsync("mp-venta-cambio-manual");
        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e, "OP-5"))), HttpStatusCode.Created);

        var respuesta = await CambiarMediosAsync(e, emitido.Id, PagoEfectivo(e));

        await AssertCodigoAsync(respuesta, HttpStatusCode.Conflict, "pago_mercadopago_no_editable");
        var pago = Assert.Single(await PagosDeLaVentaAsync(emitido.Id));
        Assert.Equal(e.IdMedio, pago["id_medio_pago"]);
        Assert.Equal("sin_verificar", pago["verificacion_mercadopago"]);
    }

    // Clausula: el cambio de medios no agrega una aprobacion manual (no tiene numero preasignado).
    [Fact]
    public async Task ElCambioDeMediosNoAdmiteLaAprobacionManual()
    {
        var e = await PrepararAsync("mp-venta-cambio-agrega-manual");
        var idVenta = await VentaSembradaAsync(e, e.Desde);

        var respuesta = await CambiarMediosAsync(e, idVenta, PagoManual(e));

        await AssertCodigoAsync(respuesta, HttpStatusCode.BadRequest, "aprobacion_manual_solo_offline");
        Assert.Equal(e.IdEfectivo, Assert.Single(await PagosDeLaVentaAsync(idVenta))["id_medio_pago"]);
    }

    // ---- E5: listado de pagos sin verificar ------------------------------------------------------------------------------------

    private const string RutaSinVerificar = "/api/mercadopago/pagos-sin-verificar";

    private static string Dia(int desplazamiento)
    {
        var zona = TimeZoneInfo.FindSystemTimeZoneById(JsonSerializer.Deserialize<string>(ParametroConocido.ZonaHoraria.ValorPorDefecto)!);
        var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zona).DateTime);
        return hoy.AddDays(desplazamiento).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string UrlSinVerificar(int idEmpresa, string? desde = null, string? hasta = null, int? limite = null) =>
        $"{RutaSinVerificar}?idEmpresa={idEmpresa}&desde={desde ?? Dia(-2)}&hasta={hasta ?? Dia(2)}"
        + (limite is null ? string.Empty : $"&limite={limite}");

    private static async Task<PagosSinVerificarListado> ListarSinVerificarAsync(
        HttpClient cliente, int idEmpresa, string? desde = null, string? hasta = null, int? limite = null) =>
        await LeerAsync<PagosSinVerificarListado>(await cliente.GetAsync(UrlSinVerificar(idEmpresa, desde, hasta, limite)));

    // Clausula: el listado es de Supervisor y Admin (Vendedor y dispositivo reciben 403), solo trae pagos sin_verificar vivos de la
    // empresa pedida, y se acota por dia (inclusivo).
    [Fact]
    public async Task ElListadoDePagosSinVerificarEsDeSupervisorYAdminPorEmpresaYSoloListaLosSinVerificar()
    {
        var e = await PrepararAsync("mp-sin-verificar-listado");
        var manual = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e, "OP-9"))), HttpStatusCode.Created);
        var cobro = await CobroAprobadoAsync(e, e.Desde + 1);
        await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde + 1, PagoMp(e, cobro.IdCobro))), HttpStatusCode.Created);
        await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde + 2, PagoEfectivo(e))), HttpStatusCode.Created);
        var deLaOtraEmpresa = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde + 3, PagoManual(e, "OP-otra"))), HttpStatusCode.Created);

        var (idOtraEmpresa, _) = await _apoyo.CrearOtraEmpresaConCuentaAsync(e.Ctx, "Otra empresa del tenant");
        var otroPv = await e.Ctx.Admin.PostAsJsonAsync(
            "/api/puntos-venta", new PuntoVentaAlta(idOtraEmpresa, "Caja de la otra empresa", ModoPuntoVenta.Escritorio));
        var idOtroPv = (await LeerAsync<PuntoVentaListado>(otroPv, HttpStatusCode.Created)).Id;
        await EjecutarAsync(
            "UPDATE comprobantes_venta SET id_punto_venta = $1 WHERE id_comprobante_venta = $2", idOtroPv, deLaOtraEmpresa.Id);

        foreach (var cliente in new[] { e.Ctx.Admin, e.Ctx.Supervisor })
        {
            var lista = await ListarSinVerificarAsync(cliente, e.Ctx.IdEmpresa);
            var fila = Assert.Single(lista.Items);
            Assert.False(lista.Truncado);
            Assert.Equal(manual.Id, fila.IdComprobanteVenta);
            Assert.Equal(manual.NumeroVisible, fila.NumeroVisible);
            Assert.Equal(e.IdPuntoVenta, fila.IdPuntoVenta);
            Assert.Equal(e.IdMedio, fila.IdMedioPago);
            Assert.Equal(PrecioDelArticulo, fila.Importe);
            Assert.Equal("OP-9", fila.Referencia);
            Assert.False(fila.Anulada);
            Assert.Empty(fila.IdsCobroEnConflicto ?? []);
            Assert.False(string.IsNullOrEmpty(fila.Empleado));
        }

        var deLaOtra = await ListarSinVerificarAsync(e.Ctx.Admin, idOtraEmpresa);
        Assert.Equal(deLaOtraEmpresa.Id, Assert.Single(deLaOtra.Items).IdComprobanteVenta);

        Assert.Empty((await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, desde: Dia(1), hasta: Dia(3))).Items);
        Assert.Empty((await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, desde: Dia(-3), hasta: Dia(-1))).Items);

        Assert.Equal(HttpStatusCode.Forbidden, (await e.Ctx.Vendedor.GetAsync(UrlSinVerificar(e.Ctx.IdEmpresa))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.Cajero.GetAsync(UrlSinVerificar(e.Ctx.IdEmpresa))).StatusCode);
    }

    // Clausula: desde y hasta son dias de la empresa, inclusivos: una venta a las 23:30 locales del dia `hasta` entra, una a las 00:10 del
    // dia siguiente no; una a las 00:10 locales del dia `desde` entra y una a las 23:50 del dia anterior no.
    [Fact]
    public async Task ElListadoAcotaPorDiaDeLaEmpresaConLosDosExtremosInclusivos()
    {
        var e = await PrepararAsync("mp-sin-verificar-dias");
        var zona = TimeZoneInfo.FindSystemTimeZoneById(
            JsonSerializer.Deserialize<string>(ParametroConocido.ZonaHoraria.ValorPorDefecto)!);
        var dia = new DateOnly(2026, 3, 10);

        DateTimeOffset Local(DateOnly fecha, int hora, int minuto)
        {
            var local = fecha.ToDateTime(new TimeOnly(hora, minuto), DateTimeKind.Unspecified);
            return new DateTimeOffset(local, zona.GetUtcOffset(local)).ToUniversalTime();
        }

        var antes = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e))), HttpStatusCode.Created);
        var primeroDelDia = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde + 1, PagoManual(e))), HttpStatusCode.Created);
        var ultimoDelDia = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde + 2, PagoManual(e))), HttpStatusCode.Created);
        var despues = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde + 3, PagoManual(e))), HttpStatusCode.Created);
        foreach (var (venta, fecha) in new[]
        {
            (antes, Local(dia.AddDays(-1), 23, 50)), (primeroDelDia, Local(dia, 0, 10)),
            (ultimoDelDia, Local(dia, 23, 30)), (despues, Local(dia.AddDays(1), 0, 10))
        })
        {
            await EjecutarAsync("UPDATE comprobantes_venta SET fecha = $1 WHERE id_comprobante_venta = $2", fecha, venta.Id);
        }

        var del = await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, desde: "2026-03-10", hasta: "2026-03-10");
        Assert.Equal(new[] { ultimoDelDia.Id, primeroDelDia.Id }, del.Items.Select(f => f.IdComprobanteVenta));

        var hastaElDiaAnterior = await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, desde: "2026-03-09", hasta: "2026-03-09");
        Assert.Equal(antes.Id, Assert.Single(hastaElDiaAnterior.Items).IdComprobanteVenta);

        var todo = await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, desde: "2026-03-09", hasta: "2026-03-11");
        Assert.Equal(
            new[] { despues.Id, ultimoDelDia.Id, primeroDelDia.Id, antes.Id }, todo.Items.Select(f => f.IdComprobanteVenta));
    }

    // Clausula: el rango es obligatorio, hasta no precede a desde y no supera 366 dias.
    [Fact]
    public async Task ElListadoExigeElRangoYLoAcotaA366Dias()
    {
        var e = await PrepararAsync("mp-sin-verificar-rango");
        var url = $"{RutaSinVerificar}?idEmpresa={e.Ctx.IdEmpresa}";

        Assert.Equal(HttpStatusCode.BadRequest, (await e.Ctx.Admin.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await e.Ctx.Admin.GetAsync($"{url}&desde=2026-03-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await e.Ctx.Admin.GetAsync($"{url}&hasta=2026-03-01")).StatusCode);
        await AssertCodigoAsync(
            await e.Ctx.Admin.GetAsync(UrlSinVerificar(e.Ctx.IdEmpresa, "2026-03-02", "2026-03-01")),
            HttpStatusCode.BadRequest, "rango_invalido");
        await AssertCodigoAsync(
            await e.Ctx.Admin.GetAsync(UrlSinVerificar(e.Ctx.IdEmpresa, "2025-01-01", "2026-01-03")),
            HttpStatusCode.BadRequest, "rango_demasiado_amplio");
        Assert.Equal(
            HttpStatusCode.OK, (await e.Ctx.Admin.GetAsync(UrlSinVerificar(e.Ctx.IdEmpresa, "2025-01-01", "2026-01-02"))).StatusCode);
    }

    // Clausula: limite (200 por defecto, maximo 500), lo mas nuevo primero, y `truncado` avisa que quedaron filas afuera.
    [Fact]
    public async Task ElListadoRespetaElLimiteLoOrdenaDelMasNuevoAlMasViejoYAvisaCuandoTrunca()
    {
        var e = await PrepararAsync("mp-sin-verificar-limite");
        var ids = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await LeerAsync<ComprobanteEmitido>(
                await VenderAsync(e, Venta(e, e.Desde + i, PagoManual(e))), HttpStatusCode.Created)).Id);
        }

        var dos = await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, limite: 2);
        var tres = await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, limite: 3);
        var porDefecto = await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa);

        Assert.True(dos.Truncado);
        Assert.Equal(new[] { ids[2], ids[1] }, dos.Items.Select(f => f.IdComprobanteVenta));
        Assert.False(tres.Truncado);
        Assert.Equal(new[] { ids[2], ids[1], ids[0] }, tres.Items.Select(f => f.IdComprobanteVenta));
        Assert.False(porDefecto.Truncado);
        Assert.Equal(3, porDefecto.Items.Count);
        await AssertCodigoAsync(
            await e.Ctx.Admin.GetAsync(UrlSinVerificar(e.Ctx.IdEmpresa, limite: 0)), HttpStatusCode.BadRequest, "limite_invalido");
        await AssertCodigoAsync(
            await e.Ctx.Admin.GetAsync(UrlSinVerificar(e.Ctx.IdEmpresa, limite: 501)), HttpStatusCode.BadRequest, "limite_invalido");
        Assert.Equal(HttpStatusCode.OK, (await e.Ctx.Admin.GetAsync(UrlSinVerificar(e.Ctx.IdEmpresa, limite: 500))).StatusCode);
    }

    // Clausula: una empresa de otro tenant es 404 y los pagos de otro tenant no aparecen por RLS.
    [Fact]
    public async Task ElListadoDePagosSinVerificarDeUnaEmpresaDeOtroTenantEs404()
    {
        var a = await PrepararAsync("mp-sin-verificar-tenant-a");
        var b = await PrepararAsync("mp-sin-verificar-tenant-b");
        await LeerAsync<ComprobanteEmitido>(await VenderAsync(b, Venta(b, b.Desde, PagoManual(b))), HttpStatusCode.Created);

        await AssertCodigoAsync(
            await a.Ctx.Admin.GetAsync(UrlSinVerificar(b.Ctx.IdEmpresa)), HttpStatusCode.NotFound, "no_encontrado");
        Assert.Empty((await ListarSinVerificarAsync(a.Ctx.Admin, a.Ctx.IdEmpresa)).Items);
        Assert.Single((await ListarSinVerificarAsync(b.Ctx.Admin, b.Ctx.IdEmpresa)).Items);
    }

    // Clausula: un pago de una venta anulada sigue en el listado, marcado como anulada.
    [Fact]
    public async Task ElListadoMarcaLaVentaAnulada()
    {
        var e = await PrepararAsync("mp-sin-verificar-anulada");
        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e))), HttpStatusCode.Created);
        await EjecutarAsync("UPDATE comprobantes_venta SET estado = 'anulado'::estado_comprobante WHERE id_comprobante_venta = $1", emitido.Id);

        var fila = Assert.Single((await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa)).Items);

        Assert.True(fila.Anulada);
    }

    // ---- E5 (judgment-day ronda 1): la aprobacion manual frente a los cobros del mismo numero -----------------------------------

    private async Task<List<Dictionary<string, object?>>> AuditoriaDeLaVentaAsync(int idVenta, string accion) =>
        await ConsultarAsync(
            "SELECT valor_nuevo::text AS valor FROM auditoria WHERE accion = $1 AND entidad = 'comprobante_venta' AND id_entidad = $2",
            accion, idVenta);

    private static JsonElement Payload(Dictionary<string, object?> fila) => JsonDocument.Parse((string)fila["valor"]!).RootElement;

    // Clausula: el dinero ya entro por Mercado Pago (cobro aprobado del mismo numero, medio e importe): la aprobacion manual liga ese
    // cobro en lugar de registrar el pago dos veces; el pago es `mercadopago`, la referencia sale del pago de Mercado Pago, la venta
    // queda fuera de la lista de sin verificar y el rastro dice que ligo el cobro y no hay aviso de doble cobro.
    [Fact]
    public async Task UnaAprobacionManualLigaElCobroAprobadoDelMismoNumeroEnLugarDeRegistrarElPagoDosVeces()
    {
        var e = await PrepararAsync("mp-venta-manual-liga");
        var cobro = await CobroAprobadoAsync(e, e.Desde);

        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e, "OP-tipeada"))), HttpStatusCode.Created);

        var pagoEmitido = Assert.Single(emitido.Pagos);
        Assert.Equal(cobro.IdCobro, pagoEmitido.IdCobroMercadoPago);
        Assert.Equal("mercadopago", pagoEmitido.VerificacionMercadoPago);
        Assert.Equal(emitido.Id, await VentaLigadaAsync(cobro.IdCobro));
        var pago = Assert.Single(await PagosDeLaVentaAsync(emitido.Id));
        Assert.Equal(cobro.IdCobro, pago["id_cobro_mercadopago"]);
        Assert.Equal("mercadopago", pago["verificacion_mercadopago"]);
        Assert.Equal($"PAY{Assert.Single(e.Ordenes.Estados).Key}", pago["referencia"]);
        Assert.Empty((await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa)).Items);

        var rastro = Payload(Assert.Single(await AuditoriaDeLaVentaAsync(emitido.Id, "venta.aprobacionmanualmercadopago")));
        Assert.Equal(cobro.IdCobro, rastro.GetProperty("id_cobro_mercadopago_ligado").GetInt32());
        Assert.Empty(await AuditoriaDeLaVentaAsync(emitido.Id, "venta.mercadopagodoblecobro"));
    }

    // Clausula: el reenvio de esa misma venta devuelve el comprobante: no liga dos veces ni audita dos veces.
    [Fact]
    public async Task ElReenvioDeUnaAprobacionManualQueLigoUnCobroNoLigaNiAuditaDeNuevo()
    {
        var e = await PrepararAsync("mp-venta-manual-liga-reenvio");
        var cobro = await CobroAprobadoAsync(e, e.Desde);
        var solicitud = Venta(e, e.Desde, PagoManual(e, "OP-tipeada"));
        var primera = await LeerAsync<ComprobanteEmitido>(await VenderAsync(e, solicitud), HttpStatusCode.Created);

        var segunda = await LeerAsync<ComprobanteEmitido>(await VenderAsync(e, solicitud), HttpStatusCode.Created);

        Assert.Equal(primera.Id, segunda.Id);
        Assert.Equal(1, await CantidadDeVentasAsync(e));
        Assert.Equal(primera.Id, await VentaLigadaAsync(cobro.IdCobro));
        Assert.Single(await PagosDeLaVentaAsync(primera.Id));
        Assert.Single(await AuditoriaDeLaVentaAsync(primera.Id, "venta.aprobacionmanualmercadopago"));
        Assert.Empty(await AuditoriaDeLaVentaAsync(primera.Id, "venta.mercadopagodoblecobro"));
    }

    // Clausula: un cobro aprobado con OTRO importe (o un cobro vivo todavia pendiente) no se liga: la venta se acepta sin verificar, el
    // cobro queda libre, se audita el doble cobro con su id y el listado lo senala en idCobroEnConflicto.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnaAprobacionManualConUnCobroEnConflictoSeAceptaSinVerificarYAuditaElDobleCobro(bool aprobadoConOtroImporte)
    {
        var e = await PrepararAsync($"mp-venta-manual-conflicto-{aprobadoConOtroImporte}");
        var cobro = aprobadoConOtroImporte
            ? await CobroAprobadoAsync(e, e.Desde, importe: 50m)
            : await CobroPendienteAsync(e, e.Desde);

        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e, "OP-1"))), HttpStatusCode.Created);

        var pagoEmitido = Assert.Single(emitido.Pagos);
        Assert.Null(pagoEmitido.IdCobroMercadoPago);
        Assert.Equal("sin_verificar", pagoEmitido.VerificacionMercadoPago);
        Assert.Null(await VentaLigadaAsync(cobro.IdCobro));
        var aviso = Payload(Assert.Single(await AuditoriaDeLaVentaAsync(emitido.Id, "venta.mercadopagodoblecobro")));
        Assert.Equal(new[] { cobro.IdCobro }, aviso.GetProperty("ids_cobro_mercadopago").EnumerateArray().Select(i => i.GetInt32()));
        Assert.DoesNotContain("token", aviso.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var fila = Assert.Single((await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa)).Items);
        Assert.Equal(new[] { cobro.IdCobro }, fila.IdsCobroEnConflicto);

        var reenvio = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e, "OP-1"))), HttpStatusCode.Created);
        Assert.Equal(emitido.Id, reenvio.Id);
        Assert.Single(await AuditoriaDeLaVentaAsync(emitido.Id, "venta.mercadopagodoblecobro"));
        Assert.Single(await AuditoriaDeLaVentaAsync(emitido.Id, "venta.aprobacionmanualmercadopago"));
    }

    // Clausula: un cobro cancelado del mismo numero no choca con nada (no ocupa el numero ni trae dinero).
    [Fact]
    public async Task UnCobroCanceladoDelMismoNumeroNoGeneraAvisoDeDobleCobro()
    {
        var e = await PrepararAsync("mp-venta-manual-cancelado");
        await InsertarCobroNoVivoAsync(e, e.Desde, 1);

        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e))), HttpStatusCode.Created);

        Assert.Empty(await AuditoriaDeLaVentaAsync(emitido.Id, "venta.mercadopagodoblecobro"));
        var fila = Assert.Single((await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa)).Items);
        Assert.Empty(fila.IdsCobroEnConflicto ?? []);
    }

    // Clausula (G2): el listado trae TODOS los cobros auditados como en conflicto, sin repetir y en orden ascendente.
    [Fact]
    public async Task ElListadoSenalaTodosLosCobrosEnConflictoEnOrdenAscendente()
    {
        var e = await PrepararAsync("mp-venta-manual-conflictos-varios");
        var apartado = await InsertarCobroNoVivoAsync(e, e.Desde, 1);
        await EjecutarAsync(
            "UPDATE cobros_mercadopago SET estado = 'fallido', fecha_procesada_mercadopago = now() WHERE id_cobro_mercadopago = $1", apartado);
        var pendiente = await CobroPendienteAsync(e, e.Desde);

        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e, "OP-2"))), HttpStatusCode.Created);

        Assert.True(apartado < pendiente.IdCobro);
        var fila = Assert.Single((await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa)).Items);
        Assert.Equal(emitido.Id, fila.IdComprobanteVenta);
        Assert.Equal(new[] { apartado, pendiente.IdCobro }, fila.IdsCobroEnConflicto);
    }

    private async Task<string> OrdenDelCobroAsync(int idCobro) =>
        (string)(await ConsultarAsync("SELECT id_orden_mercadopago FROM cobros_mercadopago WHERE id_cobro_mercadopago = $1", idCobro))
            .Single()["id_orden_mercadopago"]!;

    private async Task AssertCobroTardioSinVentaAsync(Escenario e, CobroDeMercadoPago cobro, ComprobanteEmitido venta)
    {
        var fila = (await ConsultarAsync(
            "SELECT estado, fecha_apartado, id_pago_mercadopago, importe_pagado, id_comprobante_venta FROM cobros_mercadopago " +
            "WHERE id_cobro_mercadopago = $1", cobro.IdCobro)).Single();
        Assert.Equal("sin_venta", fila["estado"]);
        Assert.NotNull(fila["fecha_apartado"]);
        Assert.NotNull(fila["id_pago_mercadopago"]);
        Assert.Equal(PrecioDelArticulo, fila["importe_pagado"]);
        Assert.Null(fila["id_comprobante_venta"]);

        var tardio = Assert.Single(
            await AuditoriaDeLaVentaAsync(venta.Id, "venta.mercadopagodoblecobro"),
            a => Payload(a).TryGetProperty("origen", out _));
        Assert.Equal(new[] { cobro.IdCobro }, Payload(tardio).GetProperty("ids_cobro_mercadopago").EnumerateArray().Select(i => i.GetInt32()));

        var listado = await LeerAsync<List<CobroSinVentaListado>>(
            await e.Ctx.Admin.GetAsync($"/api/mercadopago/cobros-sin-venta?idEmpresa={e.Ctx.IdEmpresa}"));
        Assert.Contains(listado, c => c.IdCobro == cobro.IdCobro);
        var sinVerificar = Assert.Single((await ListarSinVerificarAsync(e.Ctx.Admin, e.Ctx.IdEmpresa)).Items);
        Assert.Equal(new[] { cobro.IdCobro }, sinVerificar.IdsCobroEnConflicto);
    }

    // Clausula (G3): una venta con un cobro explicito y una aprobacion manual bloquea los explicitos y los del numero en UN solo orden
    // ascendente. Con el cobro de menor id retenido por otra transaccion, la venta espera ahi SIN haber tomado antes el explicito (de
    // mayor id): si lo hubiera tomado primero, dos ventas del mismo numero podrian esperarse en ciclo.
    [Fact]
    public async Task UnaVentaConCobroExplicitoYAprobacionManualBloqueaEnUnSoloOrdenAscendente()
    {
        var e = await PrepararAsync("mp-venta-manual-orden-de-locks");
        var apartado = await InsertarCobroNoVivoAsync(e, e.Desde, 1);
        var explicito = await CobroAprobadoAsync(e, e.Desde, importe: 50m);
        Assert.True(apartado < explicito.IdCobro);

        await using var retenedora = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var transaccionRetenedora = await retenedora.BeginTransactionAsync();
        await using (var retiene = retenedora.CreateCommand())
        {
            retiene.Transaction = transaccionRetenedora;
            retiene.CommandText = "SELECT 1 FROM cobros_mercadopago WHERE id_cobro_mercadopago = $1 FOR UPDATE";
            retiene.Parameters.Add(new NpgsqlParameter { Value = apartado });
            await retiene.ExecuteScalarAsync();
        }

        var venta = VenderAsync(e, Venta(e, e.Desde, PagoMp(e, explicito.IdCobro, importe: 50m), PagoManual(e, "OP-6", importe: 50m)));
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(venta.IsCompleted);

        await using (var sonda = await fixture.AbrirConexionCrudaAsync("plataforma", null))
        await using (var intento = sonda.CreateCommand())
        {
            intento.CommandText = "SELECT 1 FROM cobros_mercadopago WHERE id_cobro_mercadopago = $1 FOR UPDATE NOWAIT";
            intento.Parameters.Add(new NpgsqlParameter { Value = explicito.IdCobro });
            await using var transaccionSonda = await sonda.BeginTransactionAsync();
            intento.Transaction = transaccionSonda;
            await intento.ExecuteScalarAsync();
            await transaccionSonda.RollbackAsync();
        }

        await transaccionRetenedora.RollbackAsync();
        var emitido = await LeerAsync<ComprobanteEmitido>(await venta, HttpStatusCode.Created);
        Assert.Equal(2, (await PagosDeLaVentaAsync(emitido.Id)).Count);
        Assert.Equal(emitido.Id, await VentaLigadaAsync(explicito.IdCobro));
    }

    // Clausula (G1): un cobro pendiente cuando la aprobacion manual vendio su numero y que el sondeo aprueba DESPUES no queda aprobado
    // sin venta para siempre: queda sin_venta (visible en la conciliacion y cerrado para el POS) y se audita el doble cobro con la venta.
    [Fact]
    public async Task UnCobroPendienteQueElSondeoApruebaDespuesDeUnaVentaManualConSuNumeroQuedaSinVentaYAuditado()
    {
        var e = await PrepararAsync("mp-venta-manual-cobro-tardio-sondeo");
        var cobro = await CobroPendienteAsync(e, e.Desde);
        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e, "OP-3"))), HttpStatusCode.Created);

        e.Ordenes.Estados[e.Ordenes.Estados.Single(kv => kv.Value == "created").Key] = "processed";
        var visto = await LeerAsync<CobroDeMercadoPago>(await e.Cajero.GetAsync($"{Ruta}/{cobro.IdCobro}"));

        Assert.Equal("sin_venta", visto.Estado);
        await AssertCobroTardioSinVentaAsync(e, cobro, emitido);
    }

    // Clausula (G1): lo mismo cuando quien aprueba el cobro es el aviso firmado de Mercado Pago, que no tiene usuario autenticado.
    [Fact]
    public async Task UnCobroPendienteQueElAvisoApruebaDespuesDeUnaVentaManualConSuNumeroQuedaSinVentaYAuditado()
    {
        var e = await PrepararAsync("mp-venta-manual-cobro-tardio-aviso");
        var cobro = await CobroPendienteAsync(e, e.Desde);
        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e, "OP-4"))), HttpStatusCode.Created);
        var orden = await OrdenDelCobroAsync(cobro.IdCobro);
        e.Ordenes.Estados[orden] = "processed";

        const string secreto = "whsec-de-prueba-g1-que-no-es-real";
        const string idDeSolicitud = "req-g1-0001";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var manifiesto = $"id:{orden.ToLowerInvariant()};request-id:{idDeSolicitud};ts:{ts};";
        var firma = Convert.ToHexString(
            new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secreto)).ComputeHash(Encoding.UTF8.GetBytes(manifiesto)))
            .ToLowerInvariant();
        var cliente = e.Ctx.Host.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.Configure<OpcionesDeMercadoPago>(o => o.SecretoWebhook = secreto))).CreateClient();
        using var aviso = new HttpRequestMessage(
            HttpMethod.Post, $"/api/integraciones/mercadopago/webhook?data.id={Uri.EscapeDataString(orden)}&type=order")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        aviso.Headers.Add("x-signature", $"ts={ts},v1={firma}");
        aviso.Headers.Add("x-request-id", idDeSolicitud);

        Assert.Equal(HttpStatusCode.OK, (await cliente.SendAsync(aviso)).StatusCode);

        await AssertCobroTardioSinVentaAsync(e, cobro, emitido);
    }

    // Clausula (G1): una venta anulada no ocupa el numero: el cobro aprobado despues sigue aprobado y sin aviso.
    [Fact]
    public async Task UnCobroAprobadoDespuesDeUnaVentaAnuladaDelNumeroSigueAprobado()
    {
        var e = await PrepararAsync("mp-venta-manual-cobro-tardio-anulada");
        var cobro = await CobroPendienteAsync(e, e.Desde);
        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e, "OP-5"))), HttpStatusCode.Created);
        await EjecutarAsync("UPDATE comprobantes_venta SET estado = 'anulado'::estado_comprobante WHERE id_comprobante_venta = $1", emitido.Id);

        e.Ordenes.Estados[e.Ordenes.Estados.Single(kv => kv.Value == "created").Key] = "processed";
        var visto = await LeerAsync<CobroDeMercadoPago>(await e.Cajero.GetAsync($"{Ruta}/{cobro.IdCobro}"));

        Assert.Equal("aprobado", visto.Estado);
        Assert.DoesNotContain(
            await AuditoriaDeLaVentaAsync(emitido.Id, "venta.mercadopagodoblecobro"), a => Payload(a).TryGetProperty("origen", out _));
    }

    // Clausula: toda aprobacion manual deja un rastro con la venta, el punto de venta, el importe, la referencia y el dispositivo.
    [Fact]
    public async Task TodaAprobacionManualDejaSuRastroDeAuditoria()
    {
        var e = await PrepararAsync("mp-venta-manual-auditoria");

        var emitido = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoManual(e, "OP-77"))), HttpStatusCode.Created);

        var filas = await ConsultarAsync(
            "SELECT id_punto_venta, valor_nuevo::text AS valor FROM auditoria WHERE accion = 'venta.aprobacionmanualmercadopago' " +
            "AND id_entidad = $1", emitido.Id);
        var fila = Assert.Single(filas);
        Assert.Equal(e.IdPuntoVenta, fila["id_punto_venta"]);
        var rastro = Payload(fila);
        Assert.Equal(emitido.NumeroVisible, rastro.GetProperty("numero_visible").GetString());
        Assert.Equal(e.IdMedio, rastro.GetProperty("id_medio_pago").GetInt32());
        Assert.Equal(PrecioDelArticulo, rastro.GetProperty("importe").GetDecimal());
        Assert.Equal("OP-77", rastro.GetProperty("referencia").GetString());
        Assert.Equal(e.IdDispositivo, rastro.GetProperty("id_dispositivo").GetInt32());
        Assert.Equal(JsonValueKind.Null, rastro.GetProperty("id_cobro_mercadopago_ligado").ValueKind);
        Assert.Empty(await AuditoriaDeLaVentaAsync(emitido.Id, "venta.mercadopagodoblecobro"));
    }

    // ---- E5: respaldos de la base ------------------------------------------------------------------------------------------------

    // Clausula: ck_cobros_verificacion_mercadopago_cobro: el pago con cobro lleva `mercadopago` y solo ese lleva cobro (23514 -> 400).
    [Fact]
    public async Task LaBaseRechazaUnaVerificacionQueContradiceAlCobro()
    {
        var e = await PrepararAsync("mp-venta-ck-verificacion");
        var cobro = await CobroAprobadoAsync(e, e.Desde);
        var conCobro = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde, PagoMp(e, cobro.IdCobro))), HttpStatusCode.Created);
        var manual = await LeerAsync<ComprobanteEmitido>(
            await VenderAsync(e, Venta(e, e.Desde + 1, PagoManual(e))), HttpStatusCode.Created);

        var cobroSinMarca = await Assert.ThrowsAsync<PostgresException>(() =>
            EjecutarAsync("UPDATE cobros SET verificacion_mercadopago = 'sin_verificar' WHERE id_comprobante_venta = $1", conCobro.Id));
        var marcaSinCobro = await Assert.ThrowsAsync<PostgresException>(() =>
            EjecutarAsync("UPDATE cobros SET verificacion_mercadopago = 'mercadopago' WHERE id_comprobante_venta = $1", manual.Id));
        var fueraDeCatalogo = await Assert.ThrowsAsync<PostgresException>(() =>
            EjecutarAsync("UPDATE cobros SET verificacion_mercadopago = 'verificado' WHERE id_comprobante_venta = $1", manual.Id));

        foreach (var (rechazo, restriccion) in new[]
        {
            (cobroSinMarca, "ck_cobros_verificacion_mercadopago_cobro"),
            (marcaSinCobro, "ck_cobros_verificacion_mercadopago_cobro"),
            (fueraDeCatalogo, "ck_cobros_verificacion_mercadopago")
        })
        {
            Assert.Equal("23514", rechazo.SqlState);
            Assert.Equal(restriccion, rechazo.ConstraintName);
            var (estado, codigo) = await ManejarAsync(rechazo);
            Assert.Equal(StatusCodes.Status400BadRequest, estado);
            Assert.Equal("verificacion_mercadopago_invalida", codigo);
        }
    }

    // ---- Traducción de errores (sin base) -----------------------------------------------------------------------------------------------

    private sealed class ServicioDeProblemDetailsFalso : IProblemDetailsService
    {
        public ProblemDetailsContext? Ultimo { get; private set; }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Ultimo = context;
            return ValueTask.FromResult(true);
        }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Ultimo = context;
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<(int Estado, string? Codigo)> ManejarAsync(Exception excepcion)
    {
        var servicio = new ServicioDeProblemDetailsFalso();
        var manejador = new ManejadorDeErrores(servicio, NullLogger<ManejadorDeErrores>.Instance);
        var contexto = new DefaultHttpContext();

        Assert.True(await manejador.TryHandleAsync(contexto, excepcion, CancellationToken.None));

        return (contexto.Response.StatusCode, servicio.Ultimo!.ProblemDetails.Extensions["codigo"] as string);
    }
}
