using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;
using ComprobanteEmitidoDto = Ways.Application.Ventas.ComprobanteEmitido;

namespace Ways.IntegrationTests;

/// <summary>
/// Venta con envío (<c>SolicitudDeVenta.ConEnvio</c>): el servidor agrega UNA línea de concepto libre "Envío"
/// (<c>id_articulo</c> NULL, 21 % de IVA, importe del parámetro <c>importe_envio</c>), exige un cliente identificado con
/// domicilio y llena la dirección de entrega. Mismo trámite de siembra que <c>VentaAjusteManualTests</c> — no se comparte
/// helper entre archivos (convención del repo).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentaConEnvioTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordCajero = "una-contraseña-de-cajero";
    private const string CookieDispositivo = "ways.dispositivo";
    private const string Domicilio = "Av. Corrientes 1234, CABA";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdTurno, HttpClient Cliente, int IdMedioEfectivo,
        int IdArea, int IdListaPrecio, int IdAlicuotaDe10Y5, int IdAlicuotaDe21);

    // ---- siembra ---------------------------------------------------------------------------

    private async Task<(int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdUsuarioAdmin, HttpClient Admin)> AprovisionarAsync(
        string nombre, ModoPuntoVenta modo)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants", new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, modo));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        return (resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin);
    }

    private async Task<(int IdTurno, int IdMedioEfectivo, int IdArea, int IdListaPrecio, int IdAlicuotaDe10Y5, int IdAlicuotaDe21)>
        SembrarBaseAsync(int idTenant, int idPuntoVenta, int idEmpleadoApertura)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;
        var turno = new TurnoCaja
        {
            IdTenant = idTenant,
            IdPuntoVenta = idPuntoVenta,
            IdEmpleadoApertura = idEmpleadoApertura,
            FechaApertura = ahora,
            FondoInicial = 0m,
            Estado = EstadoTurno.Abierto,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.TurnosCaja.Add(turno);

        var area = new Area
        {
            IdTenant = idTenant, Nombre = $"Ventas-{Guid.NewGuid():N}", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
        var idListaGeneral = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();
        var idAlicuotaDe10Y5 = await db.AlicuotasIva.Where(a => a.CodigoAfip == 4).Select(a => a.Id).SingleAsync();
        var idAlicuotaDe21 = await db.AlicuotasIva.Where(a => a.CodigoAfip == 5).Select(a => a.Id).SingleAsync();

        return (turno.Id, idMedioEfectivo, area.Id, idListaGeneral, idAlicuotaDe10Y5, idAlicuotaDe21);
    }

    private async Task<Contexto> PrepararOnlineAsync(string nombre)
    {
        var (idTenant, idEmpresa, idPuntoVenta, idUsuarioAdmin, admin) = await AprovisionarAsync(nombre, ModoPuntoVenta.Web);
        var (idTurno, idMedio, idArea, idLista, idAlicuota105, idAlicuota21) =
            await SembrarBaseAsync(idTenant, idPuntoVenta, idUsuarioAdmin);
        return new Contexto(idTenant, idEmpresa, idPuntoVenta, idTurno, admin, idMedio, idArea, idLista, idAlicuota105, idAlicuota21);
    }

    /// <summary>Tenant en modo Escritorio: vende un cajero de dispositivo, con un bloque de numeración ya reservado.</summary>
    private async Task<Contexto> PrepararOfflineAsync(string nombre)
    {
        var (idTenant, idEmpresa, idPuntoVenta, idUsuarioAdmin, admin) =
            await AprovisionarAsync(nombre, ModoPuntoVenta.Escritorio);
        var (idTurno, idMedio, idArea, idLista, idAlicuota105, idAlicuota21) =
            await SembrarBaseAsync(idTenant, idPuntoVenta, idUsuarioAdmin);

        var alta = await admin.PostAsJsonAsync(
            "/api/dispositivos", new Ways.Application.Dispositivos.AltaDispositivo(idPuntoVenta, $"Caja {nombre}"));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var prefijo = $"{CookieDispositivo}=";
        var setCookie = Assert.Single(
            alta.Headers.GetValues("Set-Cookie"), v => v.StartsWith(prefijo, StringComparison.Ordinal));
        var valorCookie = setCookie[prefijo.Length..];
        var cookieDispositivo = valorCookie[..valorCookie.IndexOf(';')];
        admin.Dispose();

        var hasheador = new HasheadorPbkdf2();
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            db.Usuarios.Add(new Usuario
            {
                IdTenant = idTenant,
                NombreUsuario = "cajero",
                Mail = $"cajero-{idTenant}@ways.test",
                RolId = (int)RolConocido.Vendedor,
                PasswordHash = hasheador.Hashear(PasswordCajero),
                PasswordAlgoritmo = hasheador.Algoritmo,
                PasswordActualizadoEl = ahora,
                CreatedAt = ahora,
                UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var cajero = fixture.CreateClient();
        using var solicitud = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login-dispositivo")
        {
            Content = JsonContent.Create(new SolicitudDeLoginDeDispositivo("cajero", PasswordCajero))
        };
        solicitud.Headers.Add("Cookie", $"{CookieDispositivo}={cookieDispositivo}");
        Assert.Equal(HttpStatusCode.OK, (await cajero.SendAsync(solicitud)).StatusCode);

        var reserva = await cajero.PostAsJsonAsync(
            "/api/ventas/reservas-numeracion", new SolicitudDeReservaDeNumeracion(idPuntoVenta, "TX", 10));
        Assert.Equal(HttpStatusCode.OK, reserva.StatusCode);

        return new Contexto(idTenant, idEmpresa, idPuntoVenta, idTurno, cajero, idMedio, idArea, idLista, idAlicuota105, idAlicuota21);
    }

    /// <summary>Artículo con precio vigente de lista HOY, a 10,5 % de IVA (para que el 21 % del envío no pueda ser
    /// herencia del artículo).</summary>
    private async Task<int> SembrarArticuloAsync(Contexto ctx, decimal precio, bool esProducto)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant,
            CodigoInterno = $"art-{Guid.NewGuid():N}",
            Nombre = "Artículo de prueba",
            IdArea = ctx.IdArea,
            IdAlicuotaIva = ctx.IdAlicuotaDe10Y5,
            UnidadVenta = UnidadVenta.Unidad,
            EsProducto = esProducto,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant,
            IdArticulo = articulo.Id,
            IdListaPrecio = ctx.IdListaPrecio,
            Monto = precio,
            VigenteDesde = ahora.AddDays(-1),
            VigenteHasta = null,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task<int> SembrarClienteAsync(Contexto ctx, string? domicilio)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant,
            Numero = 1000 + Random.Shared.Next(1, 100_000),
            Nombre = "Cliente con envío",
            IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(),
            IdListaPrecio = ctx.IdListaPrecio,
            Domicilio = domicilio,
            Telefono = "11 5555-0000",
            Activo = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();
        return cliente.Id;
    }

    /// <summary>Fila de <c>parametros</c> del importe del envío, de la empresa (<paramref name="idPuntoVenta"/> null) o
    /// de un punto de venta.</summary>
    private async Task SembrarImporteDeEnvioAsync(Contexto ctx, string importe, int? idPuntoVenta = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        db.Parametros.Add(new Parametro
        {
            IdTenant = ctx.IdTenant,
            IdEmpresa = ctx.IdEmpresa,
            IdPuntoVenta = idPuntoVenta,
            Clave = "importe_envio",
            Valor = importe,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private static SolicitudDeVenta VentaConEnvio(
        Contexto ctx, int? idCliente, decimal pagado, bool conEnvio, string? direccion, params LineaDeVenta[] lineas) =>
        new(ctx.IdPuntoVenta, idCliente, "TX", null, lineas,
            [new PagoDeVenta(ctx.IdMedioEfectivo, pagado, null, 0m)], direccion, null, ConEnvio: conEnvio);

    private static async Task<ComprobanteEmitidoDto> EmitirOkAsync(HttpClient cliente, SolicitudDeVenta solicitud)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<ComprobanteEmitidoDto>(cuerpo, OpcionesJson)!;
    }

    private static async Task<string> CodigoDeRechazoAsync(HttpClient cliente, SolicitudDeVenta solicitud)
    {
        var respuesta = await cliente.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, cuerpo);
        return (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString()!;
    }

    private async Task<(ComprobanteVenta Comprobante, List<ItemComprobanteVenta> Items)> LeerPersistidoAsync(
        int idTenant, int idComprobante)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var comprobante = await db.ComprobantesVenta.AsNoTracking().SingleAsync(c => c.Id == idComprobante);
        var items = await db.ItemsComprobanteVenta.AsNoTracking()
            .Where(i => i.IdComprobanteVenta == idComprobante).OrderBy(i => i.Orden).ToListAsync();
        return (comprobante, items);
    }

    private async Task<int> ContarComprobantesAsync(int idTenant)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.ComprobantesVenta.CountAsync();
    }

    private static string Rango()
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        return $"desde={hoy.AddDays(-1):yyyy-MM-dd}&hasta={hoy.AddDays(1):yyyy-MM-dd}";
    }

    // ---- la línea de envío ------------------------------------------------------------------

    /// <summary>Un producto de 100 (10,5 %) y un envío de 1500: subtotal y total 1600, el envío al 21 % (no al 10,5 % del
    /// artículo), sin artículo, sin costo y sin tocar stock. La misma verdad sale del emit, de la base y del detalle.</summary>
    [Fact]
    public async Task UnaVentaConEnvioAgregaLaLineaDeEnvioAl21PorCientoSinArticuloNiStockYSumaAlTotal()
    {
        var ctx = await PrepararOnlineAsync(nameof(UnaVentaConEnvioAgregaLaLineaDeEnvioAl21PorCientoSinArticuloNiStockYSumaAlTotal));
        await SembrarImporteDeEnvioAsync(ctx, "1500");
        var idArticulo = await SembrarArticuloAsync(ctx, 100m, esProducto: true);
        var idCliente = await SembrarClienteAsync(ctx, $"  {Domicilio}  ");

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaConEnvio(ctx, idCliente, 1600m, conEnvio: true, direccion: null, new LineaDeVenta(idArticulo, 1m, null)));

        Assert.Equal(1600m, emitido.Subtotal);
        Assert.Equal(0m, emitido.DescuentoTotal);
        Assert.Equal(1600m, emitido.Total);
        Assert.Equal(Domicilio, emitido.DireccionEntrega);

        Assert.Equal(2, emitido.Items.Count);
        var producto = emitido.Items[0];
        Assert.Equal(idArticulo, producto.IdArticulo);
        Assert.Equal(ctx.IdAlicuotaDe10Y5, producto.IdAlicuotaIva);

        var envio = emitido.Items[1];
        Assert.Equal(2, envio.Orden);
        Assert.Null(envio.IdArticulo);
        Assert.Equal("Envío", envio.Descripcion);
        Assert.Equal(1m, envio.Cantidad);
        Assert.Equal(1500m, envio.PrecioUnitario);
        Assert.Equal(0m, envio.Descuento);
        Assert.Equal(0m, envio.AjusteManual);
        Assert.Null(envio.AjusteManualPorcentaje);
        Assert.Equal(1500m, envio.Total);
        Assert.Equal(ctx.IdAlicuotaDe21, envio.IdAlicuotaIva);
        Assert.Equal(21m, envio.PorcentajeIva);
        Assert.Equal(ctx.IdArea, envio.IdArea);
        Assert.Equal(ctx.IdListaPrecio, envio.IdListaPrecio);
        Assert.Null(envio.IdOferta);
        Assert.Null(envio.IdCombo);
        Assert.Null(envio.IdLote);
        Assert.False(envio.PorImporte);

        var (comprobante, items) = await LeerPersistidoAsync(ctx.IdTenant, emitido.Id);
        Assert.Equal(1600m, comprobante.Subtotal);
        Assert.Equal(1600m, comprobante.Total);
        Assert.Equal(Domicilio, comprobante.DireccionEntrega);
        Assert.Equal(2, items.Count);
        var envioPersistido = items[1];
        Assert.Null(envioPersistido.IdArticulo);
        Assert.Equal(21m, envioPersistido.PorcentajeIva);
        Assert.Equal(ctx.IdAlicuotaDe21, envioPersistido.IdAlicuotaIva);
        Assert.Equal(1500m, envioPersistido.PrecioUnitario);
        Assert.Equal(1500m, envioPersistido.Total);
        Assert.Null(envioPersistido.CostoUnitario);
        Assert.False(envioPersistido.CostoEsEstimado);
        Assert.Null(envioPersistido.IdLote);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var movimientos = await db.MovimientosStock.Where(m => m.IdComprobanteVenta == emitido.Id).ToListAsync();
            Assert.Equal(idArticulo, Assert.Single(movimientos).IdArticulo);
        }

        var detalle = await ctx.Cliente.GetAsync($"/api/ventas/{emitido.Id}");
        Assert.Equal(HttpStatusCode.OK, detalle.StatusCode);
        var releido = (await detalle.Content.ReadFromJsonAsync<ComprobanteEmitidoDto>(OpcionesJson))!;
        Assert.Equal(2, releido.Items.Count);
        Assert.Null(releido.Items[1].IdArticulo);
        Assert.Equal("Envío", releido.Items[1].Descripcion);
        Assert.Equal(1600m, releido.Total);
    }

    /// <summary>La dirección que pide el operador manda sobre el domicilio del cliente.</summary>
    [Fact]
    public async Task LaDireccionDeEntregaPedidaManda()
    {
        var ctx = await PrepararOnlineAsync(nameof(LaDireccionDeEntregaPedidaManda));
        await SembrarImporteDeEnvioAsync(ctx, "1500");
        var idArticulo = await SembrarArticuloAsync(ctx, 100m, esProducto: false);
        var idCliente = await SembrarClienteAsync(ctx, Domicilio);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaConEnvio(ctx, idCliente, 1600m, true, "Otra calle 55", new LineaDeVenta(idArticulo, 1m, null)));

        Assert.Equal("Otra calle 55", emitido.DireccionEntrega);
        Assert.Equal("Otra calle 55", (await LeerPersistidoAsync(ctx.IdTenant, emitido.Id)).Comprobante.DireccionEntrega);
    }

    /// <summary>Con importe 0 (el default del parámetro) no hay línea, pero la validación y la dirección de entrega siguen.</summary>
    [Fact]
    public async Task ConImporteDeEnvioEnCeroNoHayLineaPeroLaDireccionDeEntregaSeLlena()
    {
        var ctx = await PrepararOnlineAsync(nameof(ConImporteDeEnvioEnCeroNoHayLineaPeroLaDireccionDeEntregaSeLlena));
        var idArticulo = await SembrarArticuloAsync(ctx, 100m, esProducto: false);
        var idCliente = await SembrarClienteAsync(ctx, Domicilio);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaConEnvio(ctx, idCliente, 100m, true, null, new LineaDeVenta(idArticulo, 1m, null)));

        Assert.Equal(100m, emitido.Total);
        Assert.Equal(idArticulo, Assert.Single(emitido.Items).IdArticulo);
        Assert.Equal(Domicilio, emitido.DireccionEntrega);

        var rechazo = await CodigoDeRechazoAsync(
            ctx.Cliente,
            VentaConEnvio(ctx, null, 100m, true, null, new LineaDeVenta(idArticulo, 1m, null)));
        Assert.Equal("envio_requiere_cliente", rechazo);
    }

    /// <summary>Sin ConEnvio nada cambia, aunque el parámetro valga 1500 y el cliente tenga domicilio.</summary>
    [Fact]
    public async Task SinConEnvioLaVentaNoLlevaLineaNiDireccion()
    {
        var ctx = await PrepararOnlineAsync(nameof(SinConEnvioLaVentaNoLlevaLineaNiDireccion));
        await SembrarImporteDeEnvioAsync(ctx, "1500");
        var idArticulo = await SembrarArticuloAsync(ctx, 100m, esProducto: false);
        var idCliente = await SembrarClienteAsync(ctx, Domicilio);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaConEnvio(ctx, idCliente, 100m, false, null, new LineaDeVenta(idArticulo, 1m, null)));

        Assert.Equal(100m, emitido.Total);
        Assert.Equal(idArticulo, Assert.Single(emitido.Items).IdArticulo);
        Assert.Null(emitido.DireccionEntrega);
    }

    /// <summary>El importe lo resuelve el servidor por punto de venta: el de PV (2500) gana sobre el de empresa (1000).</summary>
    [Fact]
    public async Task ElImporteDeEnvioDelPuntoDeVentaGanaSobreElDeLaEmpresa()
    {
        var ctx = await PrepararOnlineAsync(nameof(ElImporteDeEnvioDelPuntoDeVentaGanaSobreElDeLaEmpresa));
        await SembrarImporteDeEnvioAsync(ctx, "1000");
        await SembrarImporteDeEnvioAsync(ctx, "2500", ctx.IdPuntoVenta);
        var idArticulo = await SembrarArticuloAsync(ctx, 100m, esProducto: false);
        var idCliente = await SembrarClienteAsync(ctx, Domicilio);

        var emitido = await EmitirOkAsync(
            ctx.Cliente,
            VentaConEnvio(ctx, idCliente, 2600m, true, null, new LineaDeVenta(idArticulo, 1m, null)));

        Assert.Equal(2500m, emitido.Items[1].Total);
        Assert.Equal(2600m, emitido.Total);
    }

    // ---- validación -------------------------------------------------------------------------

    [Fact]
    public async Task ConEnvioSinClienteOConConsumidorFinalSeRechazaConEnvioRequiereClienteYNoEscribeNada()
    {
        var ctx = await PrepararOnlineAsync(nameof(ConEnvioSinClienteOConConsumidorFinalSeRechazaConEnvioRequiereClienteYNoEscribeNada));
        await SembrarImporteDeEnvioAsync(ctx, "1500");
        var idArticulo = await SembrarArticuloAsync(ctx, 100m, esProducto: true);

        int idConsumidorFinal;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var cf = await db.Clientes.SingleAsync(c => c.Numero == ReglaDeClientes.NumeroConsumidorFinal);
            cf.Domicilio = Domicilio;
            await db.SaveChangesAsync();
            idConsumidorFinal = cf.Id;
        }

        var linea = new LineaDeVenta(idArticulo, 1m, null);
        Assert.Equal(
            "envio_requiere_cliente",
            await CodigoDeRechazoAsync(ctx.Cliente, VentaConEnvio(ctx, null, 1600m, true, null, linea)));
        Assert.Equal(
            "envio_requiere_cliente",
            await CodigoDeRechazoAsync(ctx.Cliente, VentaConEnvio(ctx, idConsumidorFinal, 1600m, true, null, linea)));

        Assert.Equal(0, await ContarComprobantesAsync(ctx.IdTenant));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ConEnvioYUnClienteSinDomicilioSeRechazaConEnvioRequiereDomicilio(string? domicilio)
    {
        var ctx = await PrepararOnlineAsync(
            $"{nameof(ConEnvioYUnClienteSinDomicilioSeRechazaConEnvioRequiereDomicilio)}{domicilio?.Length ?? -1}");
        await SembrarImporteDeEnvioAsync(ctx, "1500");
        var idArticulo = await SembrarArticuloAsync(ctx, 100m, esProducto: true);
        var idCliente = await SembrarClienteAsync(ctx, domicilio);

        var codigo = await CodigoDeRechazoAsync(
            ctx.Cliente, VentaConEnvio(ctx, idCliente, 1600m, true, null, new LineaDeVenta(idArticulo, 1m, null)));

        Assert.Equal("envio_requiere_domicilio", codigo);
        Assert.Equal(0, await ContarComprobantesAsync(ctx.IdTenant));
    }

    /// <summary>ConEnvio no tiene destino en una nota de crédito ni en la conversión de un presupuesto: se rechaza, y el
    /// rechazo llega ANTES de resolver el presupuesto (un id inexistente da 400 y no 404).</summary>
    [Fact]
    public async Task ConEnvioEnUnaNotaDeCreditoOUnaConversionDePresupuestoSeRechazaConEnvioNoAdmitido()
    {
        var ctx = await PrepararOnlineAsync(nameof(ConEnvioEnUnaNotaDeCreditoOUnaConversionDePresupuestoSeRechazaConEnvioNoAdmitido));
        await SembrarImporteDeEnvioAsync(ctx, "1500");
        var idArticulo = await SembrarArticuloAsync(ctx, 100m, esProducto: true);
        var idCliente = await SembrarClienteAsync(ctx, Domicilio);
        var linea = new LineaDeVenta(idArticulo, 1m, null);

        var notaDeCredito = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "NCX", null, [linea], [], null, null, ConEnvio: true);
        Assert.Equal("envio_no_admitido", await CodigoDeRechazoAsync(ctx.Cliente, notaDeCredito));

        var conversion = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null, null, [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)], null, null,
            IdPresupuestoOrigen: 999_999, ConEnvio: true);
        Assert.Equal("envio_no_admitido", await CodigoDeRechazoAsync(ctx.Cliente, conversion));

        Assert.Equal(0, await ContarComprobantesAsync(ctx.IdTenant));
    }

    // ---- lo que viene después: devolución, anulación y lectores -------------------------------

    /// <summary>La nota de crédito de una venta con envío devuelve las líneas de artículo que se piden y no toca la línea de
    /// envío (no tiene artículo que elegir): no revienta con el NULL y el envío cobrado queda sin devolver.</summary>
    [Fact]
    public async Task UnaNotaDeCreditoAsociadaAUnaVentaConEnvioDevuelveElArticuloYNoElEnvio()
    {
        var ctx = await PrepararOnlineAsync(nameof(UnaNotaDeCreditoAsociadaAUnaVentaConEnvioDevuelveElArticuloYNoElEnvio));
        await SembrarImporteDeEnvioAsync(ctx, "1500");
        var idArticulo = await SembrarArticuloAsync(ctx, 100m, esProducto: true);
        var idCliente = await SembrarClienteAsync(ctx, Domicilio);

        var venta = await EmitirOkAsync(
            ctx.Cliente, VentaConEnvio(ctx, idCliente, 1600m, true, null, new LineaDeVenta(idArticulo, 1m, null)));

        var devolucion = await EmitirOkAsync(
            ctx.Cliente,
            new SolicitudDeVenta(
                ctx.IdPuntoVenta, idCliente, "NCX", venta.Id, [new LineaDeVenta(idArticulo, 1m, null)], [], null, null));

        Assert.Equal(venta.Id, devolucion.IdComprobanteAsociado);
        var itemDevuelto = Assert.Single(devolucion.Items);
        Assert.Equal(idArticulo, itemDevuelto.IdArticulo);
        Assert.Equal(-100m, itemDevuelto.Total);
        Assert.Equal(-100m, devolucion.Total);

        var (_, itemsDeLaVenta) = await LeerPersistidoAsync(ctx.IdTenant, venta.Id);
        Assert.Equal(2, itemsDeLaVenta.Count);
        Assert.Null(itemsDeLaVenta[1].IdArticulo);
    }

    /// <summary>Anular una venta con envío revierte el stock del producto desde los movimientos (no desde los items), así que la
    /// línea sin artículo no estorba.</summary>
    [Fact]
    public async Task AnularUnaVentaConEnvioRevierteElStockDelProducto()
    {
        var ctx = await PrepararOnlineAsync(nameof(AnularUnaVentaConEnvioRevierteElStockDelProducto));
        await SembrarImporteDeEnvioAsync(ctx, "1500");
        var idArticulo = await SembrarArticuloAsync(ctx, 100m, esProducto: true);
        var idCliente = await SembrarClienteAsync(ctx, Domicilio);

        var venta = await EmitirOkAsync(
            ctx.Cliente, VentaConEnvio(ctx, idCliente, 1700m, true, null, new LineaDeVenta(idArticulo, 2m, null)));
        Assert.Equal(1700m, venta.Total);

        var anulacion = await ctx.Cliente.PostAsync($"/api/ventas/{venta.Id}/anulacion", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var stock = await db.MovimientosStock.Where(m => m.IdArticulo == idArticulo).SumAsync(m => m.Cantidad);
        Assert.Equal(0m, stock);
        Assert.Equal(EstadoComprobante.Anulado, (await db.ComprobantesVenta.SingleAsync(c => c.Id == venta.Id)).Estado);
    }

    /// <summary>Los reportes que leen items siguen respondiendo con una venta con envío: ventas por artículo deja afuera la
    /// línea sin artículo, rentabilidad la trata como una línea sin costo (no entra al margen) y el resumen del turno y el de
    /// ventas la incluyen en el total.</summary>
    [Fact]
    public async Task LosReportesQueLeenItemsToleranUnaVentaConEnvio()
    {
        var ctx = await PrepararOnlineAsync(nameof(LosReportesQueLeenItemsToleranUnaVentaConEnvio));
        await SembrarImporteDeEnvioAsync(ctx, "1500");
        var idArticulo = await SembrarArticuloAsync(ctx, 100m, esProducto: true);
        var idCliente = await SembrarClienteAsync(ctx, Domicilio);

        var venta = await EmitirOkAsync(
            ctx.Cliente, VentaConEnvio(ctx, idCliente, 1600m, true, null, new LineaDeVenta(idArticulo, 1m, null)));

        var porArticulo = await ctx.Cliente.GetAsync(
            $"/api/reportes/ventas/por-articulo?idEmpresa={ctx.IdEmpresa}&{Rango()}");
        Assert.Equal(HttpStatusCode.OK, porArticulo.StatusCode);
        var reporte = (await porArticulo.Content.ReadFromJsonAsync<VentasPorArticulo>(OpcionesJson))!;
        var fila = Assert.Single(reporte.Articulos);
        Assert.Equal(idArticulo, fila.IdArticulo);
        Assert.Equal(100m, fila.Total);

        var rentabilidadHttp = await ctx.Cliente.GetAsync(
            $"/api/reportes/rentabilidad?idEmpresa={ctx.IdEmpresa}&{Rango()}");
        Assert.Equal(HttpStatusCode.OK, rentabilidadHttp.StatusCode);
        var rentabilidad = (await rentabilidadHttp.Content.ReadFromJsonAsync<Rentabilidad>(OpcionesJson))!;
        // El envío no tiene costo (NULL, nunca cero): cuenta como línea "sin costo" de la cobertura junto al artículo (que
        // tampoco tiene costo cargado), no entra al margen ni arma una fila propia de PorArticulo.
        Assert.Equal(2, rentabilidad.Cobertura.LineasTotales);
        Assert.Equal(2, rentabilidad.Cobertura.LineasSinCosto);
        Assert.Equal(1600m, rentabilidad.Cobertura.VentaSinCosto);
        Assert.Equal(0m, rentabilidad.VentaConsiderada);
        Assert.Empty(rentabilidad.PorArticulo);

        var resumenHttp = await ctx.Cliente.GetAsync(
            $"/api/reportes/ventas/resumen?idEmpresa={ctx.IdEmpresa}&{Rango()}&granularidad=Dia");
        Assert.Equal(HttpStatusCode.OK, resumenHttp.StatusCode);
        var resumen = (await resumenHttp.Content.ReadFromJsonAsync<ResumenDeVentas>(OpcionesJson))!;
        Assert.Equal(1600m, resumen.NetoVendido);

        var turnoHttp = await ctx.Cliente.GetAsync($"/api/caja/turnos/{ctx.IdTurno}/resumen");
        Assert.Equal(HttpStatusCode.OK, turnoHttp.StatusCode);
        var turno = (await turnoHttp.Content.ReadFromJsonAsync<Ways.Application.Caja.ResumenDeTurno>(OpcionesJson))!;
        Assert.Equal(1, turno.CantidadTickets);
        var ingresoDelArea = Assert.Single(turno.IngresosPorArea);
        Assert.Equal(ctx.IdArea, ingresoDelArea.IdArea);
        Assert.Equal(venta.Total, ingresoDelArea.Total);
    }

    // ---- venta offline (número pre-asignado) ---------------------------------------------------

    /// <summary>Una venta con número pre-asignado y ConEnvio usa por ahora el importe del servidor (la excepción del importe
    /// del dispositivo es de otra tarea), y reenviar el MISMO pedido con el mismo número devuelve el mismo comprobante: la
    /// línea de envío entra en la identidad que compara la guarda de idempotencia.</summary>
    [Fact]
    public async Task UnaVentaOfflineConEnvioUsaElImporteDelServidorYSuReenvioEsIdempotente()
    {
        var ctx = await PrepararOfflineAsync(nameof(UnaVentaOfflineConEnvioUsaElImporteDelServidorYSuReenvioEsIdempotente));
        await SembrarImporteDeEnvioAsync(ctx, "1500");
        var idArticulo = await SembrarArticuloAsync(ctx, 100m, esProducto: false);
        var idCliente = await SembrarClienteAsync(ctx, Domicilio);

        SolicitudDeVenta Pedido(bool conEnvio, decimal pagado) =>
            new(ctx.IdPuntoVenta, idCliente, "TX", null, [new LineaDeVenta(idArticulo, 1m, null)],
                [new PagoDeVenta(ctx.IdMedioEfectivo, pagado, null, 0m)], null, null,
                IdPresupuestoOrigen: null, NumeroPreasignado: 1L, ConEnvio: conEnvio);

        var primera = await EmitirOkAsync(ctx.Cliente, Pedido(true, 1600m));
        Assert.Equal(1600m, primera.Total);
        Assert.Equal(1500m, primera.Items[1].Total);
        Assert.Null(primera.Items[1].IdArticulo);
        Assert.Equal(Domicilio, primera.DireccionEntrega);

        var reenvio = await EmitirOkAsync(ctx.Cliente, Pedido(true, 1600m));
        Assert.Equal(primera.Id, reenvio.Id);
        Assert.Equal(1, await ContarComprobantesAsync(ctx.IdTenant));

        // El mismo número con el pedido SIN envío es otro contenido: la línea de envío entra en la identidad.
        var otroContenido = await ctx.Cliente.PostAsJsonAsync("/api/ventas", Pedido(false, 100m));
        Assert.Equal(HttpStatusCode.Conflict, otroContenido.StatusCode);
        Assert.Equal(
            "numero_preasignado_con_otro_contenido",
            (await otroContenido.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
    }
}
