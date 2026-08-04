using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Ways.Application.Abstracciones;
using Ways.Application.Articulos;
using Ways.Application.Ofertas;
using Ways.Application.Organizacion;
using Ways.Application.Parametros;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-5-pos-ventas, Slice 4 (tasks 4.8-4.15 salvo atomicidad/concurrencia, que viven en
/// <see cref="VentasAtomicidadYConcurrenciaTests"/>): <c>POST /api/ventas</c> punta a punta
/// contra Postgres real — TX multi-línea/multi-pago/vuelto, NCX standalone y con asociado, la
/// mezcla de rechazos B6, cuenta corriente, el snapshot inmutable, y el guard de presupuesto de
/// consultas (design: Testing Strategy).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentasCheckoutTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    // Mismo motivo que ArticulosEndpointsTests.OpcionesJson: el server registra
    // JsonStringEnumConverter (Program.cs) pero ReadFromJsonAsync<T>()/GetFromJsonAsync<T>() sin
    // opciones usa las opciones DEFAULT del lado cliente, que no lo traen —
    // ComprobanteEmitido.Estado revienta la deserialización sin esto.
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant,
        int IdEmpresa,
        int IdPuntoVenta,
        HttpClient Admin,
        int IdArea,
        int IdAlicuotaIva,
        int IdListaPrecio,
        int IdMedioEfectivo,
        int IdMedioTransferencia,
        int IdMedioCuentaCorriente,
        int IdClienteConsumidorFinal,
        int IdListaPrecioConsumidorFinal);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area
        {
            IdTenant = resultado.IdTenant, Nombre = "Ventas-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync();

        var lista = new ListaPrecio
        {
            IdTenant = resultado.IdTenant, Nombre = "Lista de Prueba", EsDefault = false, Modo = ModoLista.Fija,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(lista);
        await db.SaveChangesAsync();

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();
        var idMedioTransferencia = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico)
            .Select(m => m.Id).FirstAsync();

        var medioCc = new MedioPago
        {
            IdTenant = resultado.IdTenant, Nombre = "Cuenta corriente", Orden = 3,
            Comportamiento = ComportamientoMedioPago.CuentaCorriente, AdmiteVuelto = false, RequiereReferencia = false,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioCc);
        await db.SaveChangesAsync();

        var cf = await db.Clientes.FirstAsync(c => c.Numero == ReglaDeClientes.NumeroConsumidorFinal);

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, area.Id, idAlicuotaIva,
            lista.Id, idMedioEfectivo, idMedioTransferencia, medioCc.Id, cf.Id, cf.IdListaPrecio);
    }

    private async Task<int> SembrarArticuloConPrecioAsync(
        Contexto ctx, string nombre, decimal precio, int? idListaPrecio = null, bool esProducto = true)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = nombre,
            IdArea = ctx.IdArea, IdAlicuotaIva = ctx.IdAlicuotaIva, UnidadVenta = UnidadVenta.Unidad,
            EsProducto = esProducto, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        db.Precios.Add(new Precio
        {
            IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = idListaPrecio ?? ctx.IdListaPrecio,
            Monto = precio, VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();

        return articulo.Id;
    }

    private async Task<(int Id, decimal LimiteCredito)> SembrarClienteAsync(
        Contexto ctx, string nombre, decimal limiteCredito = 0, bool creditoIlimitado = false, int? idListaPrecio = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();

        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = idListaPrecio ?? ctx.IdListaPrecio,
            LimiteCredito = limiteCredito, CreditoIlimitado = creditoIlimitado, Activo = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return (cliente.Id, limiteCredito);
    }

    private async Task<(decimal Cantidad, decimal Saldo)> LeerStockYSaldoAsync(
        Contexto ctx, int idArticulo, int idCliente)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var cantidad = await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == ctx.IdPuntoVenta)
            .Select(s => s.Cantidad)
            .FirstOrDefaultAsync();
        var saldo = await db.Clientes.Where(c => c.Id == idCliente).Select(c => c.Saldo).FirstAsync();
        return (cantidad, saldo);
    }

    // ---- task 4.8 (parcial, happy path): TX multi-línea/multi-pago/vuelto ---------------------

    [Fact]
    public async Task CheckoutTxMultiLineaMultiPagoConVueltoCommiteaTodoJunto()
    {
        var ctx = await PrepararAsync(nameof(CheckoutTxMultiLineaMultiPagoConVueltoCommiteaTodoJunto));
        var idArticuloA = await SembrarArticuloConPrecioAsync(ctx, "articulo-a", 100m);
        var idArticuloB = await SembrarArticuloConPrecioAsync(ctx, "articulo-b", 50m);
        var (idCliente, _) = await SembrarClienteAsync(ctx, "Juan Comprador", limiteCredito: 1000m);

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticuloA, 2m, null), new LineaDeVenta(idArticuloB, 3m, null)],
            [
                new PagoDeVenta(ctx.IdMedioEfectivo, 210m, null, 10m),
                new PagoDeVenta(ctx.IdMedioTransferencia, 150m, "OP-1", 0m)
            ],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(EstadoComprobante.Emitido, emitido.Estado);
        Assert.Equal(350m, emitido.Subtotal);
        Assert.Equal(0m, emitido.DescuentoTotal);
        Assert.Equal(350m, emitido.Total);
        Assert.Equal(2, emitido.Items.Count);
        Assert.Equal(2, emitido.Pagos.Count);
        Assert.StartsWith($"{ctx.IdPuntoVenta:D4}-", emitido.NumeroVisible);

        var (cantidadA, _) = await LeerStockYSaldoAsync(ctx, idArticuloA, idCliente);
        var (cantidadB, _) = await LeerStockYSaldoAsync(ctx, idArticuloB, idCliente);
        Assert.Equal(-2m, cantidadA);
        Assert.Equal(-3m, cantidadB);

        // GET /api/ventas/{id} — reprint del mismo comprobante.
        var reimpreso = await ctx.Admin.GetFromJsonAsync<ComprobanteEmitido>($"/api/ventas/{emitido.Id}", OpcionesJson);
        Assert.Equal(emitido.Total, reimpreso!.Total);
        Assert.Equal(emitido.Items.Count, reimpreso.Items.Count);
    }

    [Fact]
    public async Task CheckoutOmitiendoIdClienteQuedaAtribuidoAlConsumidorFinal()
    {
        var ctx = await PrepararAsync(nameof(CheckoutOmitiendoIdClienteQuedaAtribuidoAlConsumidorFinal));
        var idArticulo = await SembrarArticuloConPrecioAsync(
            ctx, "articulo-cf", 100m, idListaPrecio: ctx.IdListaPrecioConsumidorFinal);

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, null, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(ctx.IdClienteConsumidorFinal, emitido.IdCliente);
    }

    [Fact]
    public async Task CheckoutSinIdPuntoVentaEsRechazado()
    {
        var ctx = await PrepararAsync(nameof(CheckoutSinIdPuntoVentaEsRechazado));

        using var contenido = new StringContent(
            "{\"idCliente\":null,\"codigoTipoComprobante\":\"TX\",\"idComprobanteAsociado\":null," +
            "\"lineas\":[],\"pagos\":[]}",
            System.Text.Encoding.UTF8, "application/json");

        var respuesta = await ctx.Admin.PostAsync("/api/ventas", contenido);

        Assert.True(
            respuesta.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"Se esperaba un 4xx, se obtuvo {respuesta.StatusCode}.");
    }

    [Fact]
    public async Task UnVendedorPuedeEmitirUnaVenta()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorPuedeEmitirUnaVenta));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-vendedor", 50m);
        var (idCliente, _) = await SembrarClienteAsync(ctx, "Cliente del Vendedor");

        var mailVendedor = $"vendedor-{Guid.NewGuid():N}@ways.test";
        var altaVendedor = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor1", mailVendedor, (int)RolConocido.Vendedor, "una-contraseña-larga"));
        Assert.Equal(HttpStatusCode.Created, altaVendedor.StatusCode);

        using var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailVendedor, "una-contraseña-larga"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 50m, null, 0m)],
            null, null);

        var respuesta = await vendedor.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
    }

    // ---- task 4.15: devoluciones como NCX -------------------------------------------------------

    [Fact]
    public async Task DevolucionStandaloneSePersisteComoNcxSinAsociado()
    {
        var ctx = await PrepararAsync(nameof(DevolucionStandaloneSePersisteComoNcxSinAsociado));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-ncx", 100m);
        var (idCliente, _) = await SembrarClienteAsync(ctx, "Cliente NCX");

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "NCX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Null(emitido.IdComprobanteAsociado);
        Assert.Equal(-100m, emitido.Total);
        Assert.Equal(-1m, emitido.Items[0].Cantidad);

        var (cantidad, _) = await LeerStockYSaldoAsync(ctx, idArticulo, idCliente);
        Assert.Equal(1m, cantidad);
    }

    [Fact]
    public async Task DevolucionReferenciandoUnOriginalPropagaElIdComprobanteAsociado()
    {
        var ctx = await PrepararAsync(nameof(DevolucionReferenciandoUnOriginalPropagaElIdComprobanteAsociado));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-ncx-asoc", 100m);
        var (idCliente, _) = await SembrarClienteAsync(ctx, "Cliente NCX Asociado");

        var original = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 100m, null, 0m)],
            null, null);
        var respuestaOriginal = await ctx.Admin.PostAsJsonAsync("/api/ventas", original);
        Assert.Equal(HttpStatusCode.Created, respuestaOriginal.StatusCode);
        var emitidoOriginal = (await respuestaOriginal.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        var devolucion = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "NCX", emitidoOriginal.Id,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [],
            null, null);
        var respuestaDevolucion = await ctx.Admin.PostAsJsonAsync("/api/ventas", devolucion);
        Assert.Equal(HttpStatusCode.Created, respuestaDevolucion.StatusCode);

        var emitidoDevolucion = (await respuestaDevolucion.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;
        Assert.Equal(emitidoOriginal.Id, emitidoDevolucion.IdComprobanteAsociado);
    }

    [Fact]
    public async Task UnaTxNoPuedeAsociarseAOtroComprobante()
    {
        var ctx = await PrepararAsync(nameof(UnaTxNoPuedeAsociarseAOtroComprobante));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-tx-asoc-invalido", 50m);
        var (idCliente, _) = await SembrarClienteAsync(ctx, "Cliente TX asociado inválido");

        var original = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 50m, null, 0m)],
            null, null);
        var respuestaOriginal = await ctx.Admin.PostAsJsonAsync("/api/ventas", original);
        var emitidoOriginal = (await respuestaOriginal.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        var otraTx = original with { IdComprobanteAsociado = emitidoOriginal.Id };
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", otraTx);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("comprobante_asociado_no_permitido", problema.GetProperty("codigo").GetString());
    }

    // ---- task 4.14: paridad de rechazo B6 + guard de literales -------------------------------

    [Fact]
    public async Task UnPagoQueViolaToleranciaYVueltoALaVezReportaToleranciaPrimero()
    {
        var ctx = await PrepararAsync(nameof(UnPagoQueViolaToleranciaYVueltoALaVezReportaToleranciaPrimero));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-rechazo", 100m);
        var (idCliente, _) = await SembrarClienteAsync(ctx, "Cliente Rechazo");

        // total = 100; tolerancia default = 10 ⇒ 100 - 10 = 90 es el piso aceptado sin vuelto.
        // Con importe = 50 se viola la regla 2 (tolerancia) de entrada — un vuelto de 60 también
        // violaría la regla 3 (vuelto_excedido, máximo default 20) si se llegara a evaluar, pero
        // la regla 2 corta antes.
        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 50m, null, 60m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tolerancia_de_pago_superada", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task ElToleranciaYVueltoMaximoResuelvenPorPuntoDeVentaAntesQuePorDefault()
    {
        var ctx = await PrepararAsync(nameof(ElToleranciaYVueltoMaximoResuelvenPorPuntoDeVentaAntesQuePorDefault));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-parametro-pv", 50m);
        var (idCliente, _) = await SembrarClienteAsync(ctx, "Cliente parametro PV");

        var altaParametro = await ctx.Admin.PutAsJsonAsync(
            $"/api/parametros?idEmpresa={ctx.IdEmpresa}",
            new ParametroAlta("vuelto_maximo", "30", ctx.IdPuntoVenta));
        Assert.Equal(HttpStatusCode.OK, altaParametro.StatusCode);

        // vuelto = 25 > default (20) pero <= el override de punto de venta (30).
        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 75m, null, 25m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    [Fact]
    public void NingunLiteralDeToleranciaOVueltoHardcodeadoEnElCaminoDeCheckout()
    {
        // Spec: parametros-operativos / "No hardcoded tolerancia or vuelto value exists" — grep
        // sobre el código fuente del camino de checkout (ValidadorDePagos, ya cubierto en
        // Ways.Domain.Tests, más ServicioDeVentas, que agrega esta slice): ningún literal `10`/
        // `20` fuera de comentarios/doc-comments.
        var raiz = ResolverRaizDelRepositorio();
        var archivos = new[]
        {
            Path.Combine(raiz, "src", "Ways.Domain", "Ventas", "ValidadorDePagos.cs"),
            Path.Combine(raiz, "src", "Ways.Application", "Ventas", "ServicioDeVentas.cs")
        };

        foreach (var archivo in archivos)
        {
            Assert.True(File.Exists(archivo), $"No se encontró {archivo}.");

            var codigo = File.ReadAllLines(archivo)
                .Where(linea => !linea.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    && !linea.TrimStart().StartsWith("///", StringComparison.Ordinal)
                    && !linea.TrimStart().StartsWith("*", StringComparison.Ordinal))
                .ToList();

            foreach (var linea in codigo)
            {
                Assert.False(
                    System.Text.RegularExpressions.Regex.IsMatch(linea, @"(?<![\w.$])(10|20)(?![\w.])"),
                    $"Literal de tolerancia/vuelto sospechoso en {Path.GetFileName(archivo)}: '{linea.Trim()}'.");
            }
        }
    }

    private static string ResolverRaizDelRepositorio()
    {
        var directorio = AppContext.BaseDirectory;

        while (directorio is not null && !File.Exists(Path.Combine(directorio, "Ways.slnx")))
        {
            directorio = Path.GetDirectoryName(directorio.TrimEnd(Path.DirectorySeparatorChar));
        }

        return directorio ?? throw new InvalidOperationException("No se encontró la raíz del repositorio (Ways.slnx).");
    }

    // ---- cuenta corriente -----------------------------------------------------------------------

    [Fact]
    public async Task ConsumidorFinalNoPuedePagarConCuentaCorriente()
    {
        var ctx = await PrepararAsync(nameof(ConsumidorFinalNoPuedePagarConCuentaCorriente));
        var idArticulo = await SembrarArticuloConPrecioAsync(
            ctx, "articulo-cf-cc", 100m, idListaPrecio: ctx.IdListaPrecioConsumidorFinal);

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, ctx.IdClienteConsumidorFinal, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioCuentaCorriente, 100m, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cuenta_corriente_no_permitida", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnConsumoDeCuentaCorrienteEnElLimiteExactoEsAceptado()
    {
        var ctx = await PrepararAsync(nameof(UnConsumoDeCuentaCorrienteEnElLimiteExactoEsAceptado));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-cc-limite", 300m);
        var (idCliente, _) = await SembrarClienteAsync(ctx, "Cliente CC límite", limiteCredito: 1000m);

        // saldo arranca en 0: 0 + 300*? — armamos saldo previo de 700 con una primera venta CC,
        // y una segunda de 300 que cae justo en el límite (700 + 300 = 1000).
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var cliente = await db.Clientes.FirstAsync(c => c.Id == idCliente);
            cliente.Saldo = 700m;
            await db.SaveChangesAsync();
        }

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioCuentaCorriente, 300m, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        var (_, saldo) = await LeerStockYSaldoAsync(ctx, idArticulo, idCliente);
        Assert.Equal(1000m, saldo);
    }

    [Fact]
    public async Task UnPesoSobreElLimiteDeCreditoEsRechazadoSinEscribirNada()
    {
        var ctx = await PrepararAsync(nameof(UnPesoSobreElLimiteDeCreditoEsRechazadoSinEscribirNada));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-cc-sobre-limite", 300.01m);
        var (idCliente, _) = await SembrarClienteAsync(ctx, "Cliente CC sobre límite", limiteCredito: 1000m);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var cliente = await db.Clientes.FirstAsync(c => c.Id == idCliente);
            cliente.Saldo = 700m;
            await db.SaveChangesAsync();
        }

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioCuentaCorriente, 300.01m, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("limite_credito_excedido", problema.GetProperty("codigo").GetString());

        var (_, saldo) = await LeerStockYSaldoAsync(ctx, idArticulo, idCliente);
        Assert.Equal(700m, saldo);
    }

    [Fact]
    public async Task CreditoIlimitadoOmiteElLimite()
    {
        var ctx = await PrepararAsync(nameof(CreditoIlimitadoOmiteElLimite));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "articulo-cc-ilimitado", 2000m);
        var (idCliente, _) = await SembrarClienteAsync(
            ctx, "Cliente CC ilimitado", limiteCredito: 1000m, creditoIlimitado: true);

        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var cliente = await db.Clientes.FirstAsync(c => c.Id == idCliente);
            cliente.Saldo = 5000m;
            await db.SaveChangesAsync();
        }

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioCuentaCorriente, 2000m, null, 0m)],
            null, null);

        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    // ---- task 4.13: snapshot inmutable -----------------------------------------------------------

    [Fact]
    public async Task ReimprimirDespuesDeUnCambioDeCatalogoDevuelveElItemSinCambios()
    {
        var ctx = await PrepararAsync(nameof(ReimprimirDespuesDeUnCambioDeCatalogoDevuelveElItemSinCambios));
        var idArticulo = await SembrarArticuloConPrecioAsync(ctx, "nombre-original", 150m);
        var (idCliente, _) = await SembrarClienteAsync(ctx, "Cliente Snapshot");

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null,
            [new LineaDeVenta(idArticulo, 1m, null)],
            [new PagoDeVenta(ctx.IdMedioEfectivo, 150m, null, 0m)],
            null, null);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/ventas", solicitud);
        var emitido = (await respuesta.Content.ReadFromJsonAsync<ComprobanteEmitido>(OpcionesJson))!;

        Assert.Equal("nombre-original", emitido.Items[0].Descripcion);
        Assert.Equal(150m, emitido.Items[0].PrecioUnitario);

        // Cambiar el nombre y el precio vigente del artículo DESPUÉS de emitido.
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var articulo = await db.Articulos.FirstAsync(a => a.Id == idArticulo);
            articulo.Nombre = "nombre-cambiado";
            await db.SaveChangesAsync();

            var precio = await db.Precios.FirstAsync(p => p.IdArticulo == idArticulo);
            precio.Monto = 999m;
            await db.SaveChangesAsync();
        }

        var reimpreso = await ctx.Admin.GetFromJsonAsync<ComprobanteEmitido>($"/api/ventas/{emitido.Id}", OpcionesJson);

        Assert.Equal("nombre-original", reimpreso!.Items[0].Descripcion);
        Assert.Equal(150m, reimpreso.Items[0].PrecioUnitario);
    }

    // ---- task 4.12: guard de presupuesto de consultas --------------------------------------------

    /// <summary>Cuenta cada <c>SELECT</c> que EF Core emite a través de su propio pipeline —
    /// misma técnica que <c>OfertasResolucionTests.ContadorDeComandos</c>. Los statements crudos
    /// de la mitad transaccional (numeración/stock/cuenta corriente) usan
    /// <c>ExecuteNonQueryAsync</c>/<c>ExecuteScalarAsync</c>, nunca <c>ExecuteReaderAsync</c>, así
    /// que este contador aísla exactamente la mitad "decidir" que design acota a ≤ 16 lecturas.</summary>
    private sealed class ContadorDeComandos : DbCommandInterceptor
    {
        public int Consultas { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Consultas++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Consultas++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int idTenant, int usuarioId) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "actor-de-prueba";
        public RolConocido Rol => RolConocido.Admin;
        public int? IdTenant { get; } = idTenant;
    }

    [Fact]
    public async Task ElCheckoutEmiteUnaCantidadConstanteDeConsultasIndependienteDeLaCantidadDeLineas()
    {
        var ctx = await PrepararAsync(nameof(ElCheckoutEmiteUnaCantidadConstanteDeConsultasIndependienteDeLaCantidadDeLineas));
        var (idCliente, _) = await SembrarClienteAsync(ctx, "Cliente Presupuesto", limiteCredito: 1_000_000m);

        var consultasConPocasLineas = await EmitirYContarConsultasAsync(ctx, idCliente, cantidadDeLineas: 2);
        var consultasConMuchasLineas = await EmitirYContarConsultasAsync(ctx, idCliente, cantidadDeLineas: 20);

        Assert.Equal(consultasConPocasLineas, consultasConMuchasLineas);
        Assert.True(
            consultasConPocasLineas <= 16,
            $"Se esperaban a lo sumo 16 consultas (design: Technical Approach), se emitieron {consultasConPocasLineas}.");
    }

    private async Task<int> EmitirYContarConsultasAsync(Contexto ctx, int idCliente, int cantidadDeLineas)
    {
        var lineas = new List<LineaDeVenta>();
        var totalEsperado = 0m;

        for (var i = 0; i < cantidadDeLineas; i++)
        {
            var idArticulo = await SembrarArticuloConPrecioAsync(ctx, $"presupuesto-{Guid.NewGuid():N}", 10m);
            lineas.Add(new LineaDeVenta(idArticulo, 1m, null));
            totalEsperado += 10m;
        }

        var contador = new ContadorDeComandos();
        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant);

        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(fixture.AppConnectionString, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
                npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                npgsql.MapEnum<TipoDocumento>("tipo_documento");
                npgsql.MapEnum<ModoLista>("modo_lista");
                npgsql.MapEnum<UnidadVenta>("unidad_venta");
                npgsql.MapEnum<EstadoComprobante>("estado_comprobante");
                npgsql.MapEnum<MotivoStock>("motivo_stock");
                npgsql.MapEnum<Ways.Domain.CuentaCorriente.TipoMovimientoCc>("tipo_movimiento_cc");
            })
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual), contador)
            .Options;

        await using var db = new WaysDbContext(opciones, tenantActual);

        var reloj = new RelojFijo(DateTimeOffset.UtcNow);
        var contexto = new ContextoFijo(ctx.IdTenant, usuarioId: 1);
        var servicioDePrecios = new Ways.Application.Precios.ServicioDePrecios(db, reloj, contexto);
        var servicioDeOfertas = new ServicioDeOfertas(db, reloj, contexto, servicioDePrecios);
        var servicioDeVentas = new ServicioDeVentas(db, reloj, contexto, servicioDeOfertas);

        var solicitud = new SolicitudDeVenta(
            ctx.IdPuntoVenta, idCliente, "TX", null, lineas,
            [new PagoDeVenta(ctx.IdMedioEfectivo, totalEsperado, null, 0m)],
            null, null);

        await servicioDeVentas.EmitirAsync(solicitud);

        return contador.Consultas;
    }
}
