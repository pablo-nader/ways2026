using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ways.Api.ConectorMcp;
using Ways.Domain.Compras;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Persistencia;
using static Ways.IntegrationTests.ApoyoDeConectorMcp;
using static Ways.IntegrationTests.ApoyoDeHerramientasDeCompras;

namespace Ways.IntegrationTests;

/// <summary>
/// <c>crear_borrador_de_compra</c> llamada por <c>/mcp</c> con un token real, contra Postgres como
/// <c>ways_app</c> (RLS activo). Cada escritura, o su ausencia, se verifica leyendo la base después de la
/// llamada, no solo por la respuesta.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ConectorMcpCrearBorradorDeCompraTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Herramienta = "crear_borrador_de_compra";

    private readonly ApoyoDeConectorMcp _apoyo = new(fixture);
    private readonly ApoyoDeHerramientasDeCompras _compras = new(fixture);
    private readonly ApoyoDeBorradorDeCompraMcp _borrador = new(fixture);

    private sealed record Escenario(
        WebApplicationFactory<Program> Host,
        HttpClient Cliente,
        string AccessToken,
        UsuarioDeTenant Usuario,
        int IdProveedor,
        int IdPuntoVenta,
        int IdFacturaA,
        int IdRemito,
        int IdIva21,
        int IdIva105) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Cliente.Dispose();
            await Host.DisposeAsync();
        }
    }

    private async Task<Escenario> PrepararAsync(
        string nombre, RolConocido rol = RolConocido.Admin, Action<IWebHostBuilder>? configurarMas = null)
    {
        var usuario = await _apoyo.SembrarUsuarioDeTenantAsync(nombre);
        if (rol != RolConocido.Admin)
        {
            await _compras.CambiarRolAsync(usuario.IdUsuario, rol);
        }

        var (_, idPuntoVenta) = await _compras.SembrarEmpresaConPuntoDeVentaAsync(usuario.IdTenant, "Almacén Propio SA", "Local centro");
        var idProveedor = await _compras.SembrarProveedorAsync(usuario.IdTenant, "Distribuidora Norte SRL", cuit: "30-71234567-1");

        var host = _apoyo.HostConConector(usuario.Mail, configurarMas: configurarMas);
        var cliente = Cliente(host);
        var tokens = await ObtenerTokensAsync(cliente, usuario.Mail);

        return new Escenario(
            host,
            cliente,
            tokens.GetProperty("access_token").GetString()!,
            usuario,
            idProveedor,
            idPuntoVenta,
            await _borrador.IdTipoAsync("C-FA"),
            await _borrador.IdTipoAsync("C-RM"),
            await _borrador.IdAlicuotaAsync(21m),
            await _borrador.IdAlicuotaAsync(10.5m));
    }

    /// <summary>Una factura A válida de una línea de concepto: alcanza para que un Admin cree el borrador.</summary>
    private static object FacturaSimple(Escenario e, long puntoVentaFactura = 3, long numeroFactura = 12345, int? idProveedor = null) => new
    {
        idProveedor = idProveedor ?? e.IdProveedor,
        idTipoComprobante = e.IdFacturaA,
        idPuntoVenta = e.IdPuntoVenta,
        fechaComprobante = "2026-10-15",
        puntoVentaFactura,
        numeroFactura,
        lineas = new object[] { new { concepto = "Servicio de flete", unidades = 1, costoUnitario = 1000m, idAlicuotaIva = e.IdIva21 } }
    };

    private static string Link(int idCompra) => $"{UrlPublica}/admin/compras/{idCompra}";

    [Fact]
    public async Task ToolsListLaAnunciaComoEscrituraNoDestructivaNoIdempotenteDeMundoCerradoConLosArgumentosQueNombranSusMensajes()
    {
        await using var e = await PrepararAsync("mcp-borrador-anuncio");

        var (estado, mensaje) = await LlamarMcpAsync(e.Cliente, e.AccessToken, "tools/list");

        Assert.Equal(HttpStatusCode.OK, estado);
        var herramienta = mensaje!.Value.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(h => h.GetProperty("name").GetString() == Herramienta);

        var anotaciones = herramienta.GetProperty("annotations");
        Assert.False(anotaciones.GetProperty("readOnlyHint").GetBoolean());
        Assert.False(anotaciones.GetProperty("destructiveHint").GetBoolean());
        Assert.False(anotaciones.GetProperty("idempotentHint").GetBoolean());
        Assert.False(anotaciones.GetProperty("openWorldHint").GetBoolean());

        var esquema = herramienta.GetProperty("inputSchema");
        Assert.Equal(
            [
                "discriminaIva", "fechaComprobante", "idProveedor", "idPuntoVenta", "idTipoComprobante", "ivaImpreso",
                "lineas", "numeroFactura", "numeroRemito", "observaciones", "percepciones", "preciosIncluyenIva",
                "puntoVentaFactura", "totalImpreso"
            ],
            Propiedades(esquema).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["fechaComprobante", "idProveedor", "idPuntoVenta", "idTipoComprobante", "lineas"],
            esquema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "bultos", "codigoLote", "codigoProveedor", "concepto", "costoUnitario", "descuento", "fechaVencimiento",
                "idAlicuotaIva", "idArticulo", "unidades", "unidadesPorBulto"
            ],
            Propiedades(Elementos(esquema, "lineas")).Order(StringComparer.Ordinal));
        Assert.Equal(["idAlicuotaIva", "iva"], Propiedades(Elementos(esquema, "ivaImpreso")).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["alicuota", "baseImponible", "importe", "tipo"],
            Propiedades(Elementos(esquema, "percepciones")).Order(StringComparer.Ordinal));
    }

    /// <summary>Sobre el conjunto completo de herramientas: se anuncian sin solo lectura exactamente las tres de
    /// escritura, y las instrucciones del servidor nombran esas mismas tres. Una herramienta de escritura nueva, o
    /// una anotación equivocada, rompe esta prueba hasta que la lista y la prosa digan lo mismo.</summary>
    [Fact]
    public async Task SoloLasTresDeEscrituraSeAnuncianSinSoloLecturaYLasInstruccionesNombranEsasTres()
    {
        await using var e = await PrepararAsync("mcp-borrador-escritura");

        var (_, lista) = await LlamarMcpAsync(e.Cliente, e.AccessToken, "tools/list");
        var (_, inicio) = await LlamarMcpAsync(e.Cliente, e.AccessToken, "initialize", new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "pruebas", version = "1.0" }
        });

        var soloLecturaPorNombre = lista!.Value.GetProperty("result").GetProperty("tools").EnumerateArray()
            .ToDictionary(
                h => h.GetProperty("name").GetString()!,
                h => h.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal(
            ["asociar_codigo_de_barras", "asociar_codigo_de_proveedor", "crear_borrador_de_compra"],
            soloLecturaPorNombre.Where(h => !h.Value).Select(h => h.Key).Order(StringComparer.Ordinal));
        Assert.Contains("quien_soy", soloLecturaPorNombre.Keys);
        Assert.Contains(
            "todas sus herramientas son de solo lectura salvo crear_borrador_de_compra, asociar_codigo_de_barras y " +
            "asociar_codigo_de_proveedor.",
            inicio!.Value.GetProperty("result").GetProperty("instructions").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnaFacturaACreaElBorradorAsistidoConCadaCampoEnSuLugarYDevuelveElLinkLosTotalesYLaAdvertenciaDelIva()
    {
        await using var e = await PrepararAsync("mcp-borrador-factura");
        var yerba = (await _compras.SembrarArticuloAsync(e.Usuario.IdTenant, "Yerba Mate Suave 1 kg")).Id;
        var aceite = (await _compras.SembrarArticuloAsync(e.Usuario.IdTenant, "Aceite de Girasol 900 ml")).Id;
        var vencimiento = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1);

        var resultado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, new
        {
            idProveedor = e.IdProveedor,
            idTipoComprobante = e.IdFacturaA,
            idPuntoVenta = e.IdPuntoVenta,
            fechaComprobante = "2026-10-15",
            puntoVentaFactura = 3,
            numeroFactura = 12345,
            lineas = new object[]
            {
                new
                {
                    idArticulo = yerba, codigoProveedor = " YM-001 ", unidades = 2, bultos = 3, unidadesPorBulto = 10,
                    costoUnitario = 150.5m, descuento = 16m, idAlicuotaIva = e.IdIva21
                },
                new
                {
                    idArticulo = aceite, unidades = 5, costoUnitario = 200.25m, idAlicuotaIva = e.IdIva105, codigoLote = "L-2027-03",
                    fechaVencimiento = vencimiento.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                },
                new { concepto = "  Flete Rosario  ", unidades = 1, costoUnitario = 500m, idAlicuotaIva = e.IdIva21 }
            },
            ivaImpreso = new[] { new { idAlicuotaIva = e.IdIva21, iva = 1113.40m } },
            percepciones = new[] { new { tipo = "iibb", importe = 189.04m, baseImponible = 6301.25m, alicuota = 3m } },
            totalImpreso = 7708.82m,
            observaciones = "  Cargada desde la foto de la factura  "
        });

        Assert.False(resultado.EsError, resultado.Texto);
        var compra = Assert.Single(await _borrador.ComprasDelProveedorAsync(e.IdProveedor));
        Assert.Equal(e.Usuario.IdTenant, compra.IdTenant);
        Assert.Equal(e.IdFacturaA, compra.IdTipoComprobante);
        Assert.Equal(e.IdPuntoVenta, compra.IdPuntoVenta);
        Assert.Equal(e.Usuario.IdUsuario, compra.IdEmpleado);
        Assert.Equal("00003-00012345", compra.NumeroExterno);
        Assert.Equal(new DateOnly(2026, 10, 15), compra.FechaComprobante);
        Assert.Null(compra.FechaRecepcion);
        Assert.Equal(EstadoCompra.Borrador, compra.Estado);
        Assert.Equal(OrigenCompra.Asistente, compra.Origen);
        Assert.True(compra.DiscriminaIva);
        Assert.False(compra.PreciosIncluyenIva);
        Assert.Null(compra.IdOrdenCompra);
        Assert.Equal("Cargada desde la foto de la factura", compra.Observaciones);
        Assert.Equal(6317.25m, compra.Subtotal);
        Assert.Equal(16.00m, compra.DescuentoTotal);
        Assert.Equal(1218.53m, compra.IvaTotal);
        Assert.Equal(7708.82m, compra.Total);

        var items = await _borrador.ItemsAsync(compra.Id);
        Assert.Equal(3, items.Count);
        AssertItem(items[0], 1, yerba, "Yerba Mate Suave 1 kg", 32m, 3m, 10m, 150.5m, 16m, e.IdIva21, 21m, 4800m, true, "YM-001", null, null);
        AssertItem(items[1], 2, aceite, "Aceite de Girasol 900 ml", 5m, null, null, 200.25m, 0m, e.IdIva105, 10.5m, 1001.25m, true, null, "L-2027-03", vencimiento);
        AssertItem(items[2], 3, null, "Flete Rosario", 1m, null, null, 500m, 0m, e.IdIva21, 21m, 500m, false, null, null, null);

        var alicuotas = (await _borrador.AlicuotasAsync(compra.Id)).ToDictionary(a => a.IdAlicuotaIva);
        Assert.Equal(2, alicuotas.Count);
        Assert.Equal((21m, 5300m, 1113.40m), (alicuotas[e.IdIva21].Porcentaje, alicuotas[e.IdIva21].Neto, alicuotas[e.IdIva21].Iva));
        Assert.Equal((10.5m, 1001.25m, 105.13m), (alicuotas[e.IdIva105].Porcentaje, alicuotas[e.IdIva105].Neto, alicuotas[e.IdIva105].Iva));

        var percepcion = Assert.Single(await _borrador.PercepcionesAsync(compra.Id));
        Assert.Equal(("iibb", 6301.25m, 3m, 189.04m), (percepcion.Tipo, percepcion.BaseImponible, percepcion.Alicuota, percepcion.Importe));

        Assert.Equal(
            [
                $"Borrador de compra {compra.Id} creado con origen asistente: {Link(compra.Id)}",
                "Para confirmarlo, el usuario lo abre en ese link, lo revisa contra la factura y toca Guardar. Hasta " +
                "entonces no mueve stock, costos ni la cuenta corriente del proveedor.",
                "Comprobante: Factura A de compra 00003-00012345, fecha 2026-10-15",
                "Líneas: 2 de artículo y 1 de concepto",
                "Subtotal: 6317.25 | Descuentos: 16.00 | IVA: 1218.53 | Percepciones: 189.04 | Total: 7708.82",
                "Total impreso: 7708.82 | Diferencia (calculado - impreso): 0.00",
                "Advertencias:",
                "- Alícuota 21 %: quedó el IVA impreso (1113.40), que difiere del calculado (1113.00) en 0.40."
            ],
            resultado.Texto.Split(Environment.NewLine));
    }

    [Fact]
    public async Task UnRemitoGuardaSuNumeroLibreYLasDecisionesDeIvaQueRecibe()
    {
        await using var e = await PrepararAsync("mcp-borrador-remito");
        var articulo = (await _compras.SembrarArticuloAsync(e.Usuario.IdTenant, "Gaseosa Cola 2,25 l")).Id;

        var resultado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, new
        {
            idProveedor = e.IdProveedor,
            idTipoComprobante = e.IdRemito,
            idPuntoVenta = e.IdPuntoVenta,
            fechaComprobante = "2026-10-02",
            numeroRemito = "  R-0001-000555 ",
            discriminaIva = true,
            preciosIncluyenIva = true,
            lineas = new object[]
            {
                new { idArticulo = articulo, unidades = 1, bultos = 2, unidadesPorBulto = 6, costoUnitario = 121m, idAlicuotaIva = e.IdIva21 }
            }
        });

        Assert.False(resultado.EsError, resultado.Texto);
        var compra = Assert.Single(await _borrador.ComprasDelProveedorAsync(e.IdProveedor));
        Assert.Equal(e.IdRemito, compra.IdTipoComprobante);
        Assert.Equal("R-0001-000555", compra.NumeroExterno);
        Assert.Equal(new DateOnly(2026, 10, 2), compra.FechaComprobante);
        Assert.True(compra.DiscriminaIva);
        Assert.True(compra.PreciosIncluyenIva);
        Assert.Equal(OrigenCompra.Asistente, compra.Origen);
        Assert.Equal(1573m, compra.Subtotal);
        Assert.Equal(0m, compra.DescuentoTotal);
        Assert.Equal(273m, compra.IvaTotal);
        Assert.Equal(1573m, compra.Total);

        var alicuota = Assert.Single(await _borrador.AlicuotasAsync(compra.Id));
        Assert.Equal((1300m, 273m), (alicuota.Neto, alicuota.Iva));
        var item = Assert.Single(await _borrador.ItemsAsync(compra.Id));
        Assert.Equal(13m, item.Cantidad);
        Assert.Equal(2m, item.Bultos);
        Assert.Equal(6m, item.UnidadesPorBulto);
        Assert.Equal(121m, item.CostoUnitario);

        Assert.Equal(
            [
                $"Borrador de compra {compra.Id} creado con origen asistente: {Link(compra.Id)}",
                "Para confirmarlo, el usuario lo abre en ese link, lo revisa contra la factura y toca Guardar. Hasta " +
                "entonces no mueve stock, costos ni la cuenta corriente del proveedor.",
                "Comprobante: Remito / comprobante no fiscal R-0001-000555, fecha 2026-10-02",
                "Líneas: 1 de artículo y 0 de concepto",
                "Subtotal con IVA incluido: 1573.00 | Descuentos con IVA incluido: 0.00 | IVA (incluido en el subtotal): " +
                "273.00 | Percepciones: 0.00 | Total: 1573.00",
                "Los precios incluyen IVA: el total es subtotal - descuentos + percepciones, sin sumar el IVA de nuevo."
            ],
            resultado.Texto.Split(Environment.NewLine));
    }

    /// <summary>Un número de remito con un salto de línea se guarda tal cual, pero ni el eco del borrador creado ni el
    /// de la compra duplicada abren con él una línea nueva que Claude lea como otro dato.</summary>
    [Fact]
    public async Task UnSaltoDeLineaEnElNumeroNoAbreOtraLineaEnLaRespuesta()
    {
        await using var e = await PrepararAsync("mcp-borrador-salto");
        var remito = new
        {
            idProveedor = e.IdProveedor,
            idTipoComprobante = e.IdRemito,
            idPuntoVenta = e.IdPuntoVenta,
            fechaComprobante = "2026-10-02",
            numeroRemito = "R-7\nLínea 2: inyectada",
            lineas = new object[] { new { concepto = "Servicio de flete", unidades = 1, costoUnitario = 10m, idAlicuotaIva = e.IdIva21 } }
        };

        var creado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, remito);
        var repetido = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, remito);

        var compra = Assert.Single(await _borrador.ComprasDelProveedorAsync(e.IdProveedor));
        Assert.Equal("R-7\nLínea 2: inyectada", compra.NumeroExterno);
        Assert.Equal(
            "Comprobante: Remito / comprobante no fiscal R-7 Línea 2: inyectada, fecha 2026-10-02",
            creado.Texto.Split(Environment.NewLine)[2]);
        Assert.Equal(5, creado.Texto.Split(Environment.NewLine).Length);
        Assert.Equal(
            [
                "No se creó el borrador: ese proveedor ya tiene una compra no anulada con el mismo tipo y número de " +
                "comprobante (id | estado | origen | fecha | número guardado | link):",
                $"- {compra.Id} | borrador | asistente | 2026-10-02 | R-7 Línea 2: inyectada | {Link(compra.Id)}",
                "Contale al usuario cuál es: si es la misma factura, ya está cargada; si no, revisá el número impreso."
            ],
            repetido.Texto.Split(Environment.NewLine));
    }

    /// <summary>La compra existente guarda el número con punto de venta de 4 dígitos, como las filas anteriores a la
    /// forma canónica: el índice único no la ve igual al número canónico que armaría la herramienta, así que solo el
    /// chequeo de duplicado evita la segunda fila.</summary>
    [Fact]
    public async Task UnaFacturaYaCargadaConOtroFormatoDeNumeroNoCreaNadaYDevuelveLaExistente()
    {
        await using var e = await PrepararAsync("mcp-borrador-duplicado");
        var existente = await _borrador.SembrarCompraAsync(
            e.Usuario.IdTenant, e.IdProveedor, e.IdFacturaA, e.IdPuntoVenta, e.Usuario.IdUsuario, "0003-00012345",
            EstadoCompra.Confirmada, new DateOnly(2026, 10, 1));

        var resultado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, FacturaSimple(e));

        Assert.False(resultado.EsError, resultado.Texto);
        Assert.Equal(
            [
                "No se creó el borrador: ese proveedor ya tiene una compra no anulada con el mismo tipo y número de " +
                "comprobante (id | estado | origen | fecha | número guardado | link):",
                $"- {existente} | confirmada | manual | 2026-10-01 | 0003-00012345 | {Link(existente)}",
                "Contale al usuario cuál es: si es la misma factura, ya está cargada; si no, revisá el número impreso."
            ],
            resultado.Texto.Split(Environment.NewLine));
        Assert.Equal([existente], (await _borrador.ComprasDelProveedorAsync(e.IdProveedor)).Select(c => c.Id));
    }

    [Theory]
    [InlineData(123456, 1)]
    [InlineData(0, 12345)]
    [InlineData(-3, 12345)]
    [InlineData(3, 123456789)]
    public async Task UnNumeroDeFacturaQueNoEntraEnLaFormaCanonicaEsUnErrorEnLosArgumentosDeLaHerramientaYNoCreaNada(
        long puntoVentaFactura, long numeroFactura)
    {
        await using var e = await PrepararAsync("mcp-borrador-numero");

        var resultado = await LlamarHerramientaAsync(
            e.Cliente, e.AccessToken, Herramienta, FacturaSimple(e, puntoVentaFactura, numeroFactura));

        Assert.True(resultado.EsError);
        Assert.StartsWith(
            "No se creó el borrador. El número de la factura no es válido: puntoVentaFactura va de 1 a 99999 y " +
            "numeroFactura de 1 a 99999999",
            resultado.Texto,
            StringComparison.Ordinal);
        Assert.Equal(0, await _borrador.ContarComprasDelTenantAsync(e.Usuario.IdTenant));
    }

    [Fact]
    public async Task UnArticuloQueNoExisteEsUnErrorQueNombraLaLineaYNoCreaNada()
    {
        await using var e = await PrepararAsync("mcp-borrador-sin-articulo");

        var resultado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, new
        {
            idProveedor = e.IdProveedor,
            idTipoComprobante = e.IdFacturaA,
            idPuntoVenta = e.IdPuntoVenta,
            fechaComprobante = "2026-10-15",
            puntoVentaFactura = 3,
            numeroFactura = 12345,
            lineas = new object[]
            {
                new { concepto = "Servicio de flete", unidades = 1, costoUnitario = 1000m, idAlicuotaIva = e.IdIva21 },
                new { idArticulo = int.MaxValue, unidades = 1, costoUnitario = 10m, idAlicuotaIva = e.IdIva21 }
            }
        });

        Assert.True(resultado.EsError);
        Assert.Equal(
            $"Línea 2: no existe el artículo {int.MaxValue}. Buscalo con buscar_articulos o cargá la línea como concepto.",
            resultado.Texto);
        Assert.Equal(0, await _borrador.ContarComprasDelTenantAsync(e.Usuario.IdTenant));
    }

    [Fact]
    public async Task UnIvaImpresoFueraDeToleranciaEsUnErrorQuePideRevisarLasLineasYNoCreaNada()
    {
        await using var e = await PrepararAsync("mcp-borrador-iva");

        var resultado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, new
        {
            idProveedor = e.IdProveedor,
            idTipoComprobante = e.IdFacturaA,
            idPuntoVenta = e.IdPuntoVenta,
            fechaComprobante = "2026-10-15",
            puntoVentaFactura = 3,
            numeroFactura = 12345,
            lineas = new object[] { new { concepto = "Servicio de flete", unidades = 10, costoUnitario = 100m, idAlicuotaIva = e.IdIva21 } },
            ivaImpreso = new[] { new { idAlicuotaIva = e.IdIva21, iva = 215m } }
        });

        Assert.True(resultado.EsError);
        Assert.StartsWith("No se creó el borrador. El IVA impreso (", resultado.Texto, StringComparison.Ordinal);
        Assert.Contains("Las líneas no explican el IVA que imprime la factura", resultado.Texto, StringComparison.Ordinal);
        Assert.Equal(0, await _borrador.ContarComprasDelTenantAsync(e.Usuario.IdTenant));
    }

    [Fact]
    public async Task UnTipoDeComprobanteQueNoExisteEsUnErrorDeLaHerramientaYNoCreaNada()
    {
        await using var e = await PrepararAsync("mcp-borrador-sin-tipo");

        var resultado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, new
        {
            idProveedor = e.IdProveedor,
            idTipoComprobante = int.MaxValue,
            idPuntoVenta = e.IdPuntoVenta,
            fechaComprobante = "2026-10-15",
            numeroRemito = "R-1",
            lineas = new object[] { new { concepto = "Servicio de flete", unidades = 1, costoUnitario = 1000m, idAlicuotaIva = e.IdIva21 } }
        });

        Assert.True(resultado.EsError);
        Assert.Equal(
            $"No existe el tipo de comprobante {int.MaxValue}: usá uno de los tipos de compra de contexto_de_compras.",
            resultado.Texto);
        Assert.Equal(0, await _borrador.ContarComprasDelTenantAsync(e.Usuario.IdTenant));
    }

    /// <summary>El pedido es válido para un Admin: sin la guarda de permiso, el borrador se crearía.</summary>
    [Theory]
    [InlineData(RolConocido.Supervisor)]
    [InlineData(RolConocido.Vendedor)]
    public async Task UnUsuarioQueNoEsAdminRecibeElErrorDePermisoYNoSeCreaNada(RolConocido rol)
    {
        await using var e = await PrepararAsync($"mcp-borrador-rol-{rol}", rol);

        var resultado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, FacturaSimple(e));

        Assert.True(resultado.EsError);
        Assert.Equal(PermisoDeCompras.MensajeSinPermiso, resultado.Texto);
        Assert.Equal(0, await _borrador.ContarComprasDelTenantAsync(e.Usuario.IdTenant));
    }

    /// <summary>Verificado mutando a plataforma el modo de tenant que fija <c>ValidadorDeSesion</c>: la
    /// herramienta deja de responder que el proveedor no existe y Claude recibe el error genérico del SDK.</summary>
    [Fact]
    public async Task UnProveedorDeOtroTenantNoEsAlcanzableYNoSeCreaNada()
    {
        await using var e = await PrepararAsync("mcp-borrador-proveedor-ajeno");
        var otro = await _apoyo.SembrarUsuarioDeTenantAsync("mcp-borrador-proveedor-ajeno-otro");
        var ajeno = await _compras.SembrarProveedorAsync(otro.IdTenant, "Proveedor Ajeno SA", cuit: "30-71234567-1");

        var resultado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, FacturaSimple(e, idProveedor: ajeno));

        Assert.True(resultado.EsError);
        Assert.Equal(
            $"No se creó el borrador. No existe el proveedor {ajeno}. El proveedor sale de buscar_proveedor y el punto " +
            "de venta de contexto_de_compras.",
            resultado.Texto);
        Assert.Equal(0, await _borrador.ContarComprasDelTenantAsync(e.Usuario.IdTenant));
        Assert.Empty(await _borrador.ComprasDelProveedorAsync(ajeno));
    }

    /// <summary>Verificado igual que el del proveedor: con el modo de tenant en plataforma, la herramienta deja de
    /// responder que el artículo no existe.</summary>
    [Fact]
    public async Task UnArticuloDeOtroTenantNoEsAlcanzableYNoSeCreaNada()
    {
        await using var e = await PrepararAsync("mcp-borrador-articulo-ajeno");
        var otro = await _apoyo.SembrarUsuarioDeTenantAsync("mcp-borrador-articulo-ajeno-otro");
        var ajeno = (await _compras.SembrarArticuloAsync(otro.IdTenant, "Artículo Ajeno")).Id;

        var resultado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, new
        {
            idProveedor = e.IdProveedor,
            idTipoComprobante = e.IdFacturaA,
            idPuntoVenta = e.IdPuntoVenta,
            fechaComprobante = "2026-10-15",
            puntoVentaFactura = 3,
            numeroFactura = 12345,
            lineas = new object[] { new { idArticulo = ajeno, unidades = 1, costoUnitario = 10m, idAlicuotaIva = e.IdIva21 } }
        });

        Assert.True(resultado.EsError);
        Assert.Equal(
            $"Línea 1: no existe el artículo {ajeno}. Buscalo con buscar_articulos o cargá la línea como concepto.",
            resultado.Texto);
        Assert.Equal(0, await _borrador.ContarComprasDelTenantAsync(e.Usuario.IdTenant));
        Assert.Equal(0, await _borrador.ContarComprasDelTenantAsync(otro.IdTenant));
    }

    /// <summary>Un caso por familia de las reglas de forma, a través de la herramienta: sin la llamada a
    /// <see cref="ArmadoDelBorradorDeCompra.ValidarForma"/> cada uno escribe el borrador o devuelve otro error.</summary>
    [Theory]
    [InlineData("más de 100 líneas", "Pasá entre 1 y 100 líneas.")]
    [InlineData("costo faltante", "Línea 1: necesita costoUnitario e idAlicuotaIva.")]
    [InlineData(
        "descuento mayor al bruto",
        "Línea 1: descuento supera el importe bruto de la línea (cantidad × costoUnitario = 10.00): es la bonificación " +
        "en pesos, no un porcentaje.")]
    [InlineData("fecha del comprobante", "fechaComprobante tiene que ser una fecha yyyy-MM-dd, por ejemplo 2026-10-15.")]
    [InlineData("IVA impreso incompleto", "ivaImpreso 1: necesita idAlicuotaIva e iva.")]
    [InlineData("percepción sin importe", "Percepción 1: falta el importe.")]
    public async Task CadaFamiliaDeReglasDeFormaRechazaElPedidoSinEscribir(string caso, string mensaje)
    {
        await using var e = await PrepararAsync("mcp-borrador-forma");
        var flete = new { concepto = "Servicio de flete", unidades = 1, costoUnitario = 10m, idAlicuotaIva = e.IdIva21 };
        var argumentos = new Dictionary<string, object>
        {
            ["idProveedor"] = e.IdProveedor,
            ["idTipoComprobante"] = e.IdFacturaA,
            ["idPuntoVenta"] = e.IdPuntoVenta,
            ["fechaComprobante"] = "2026-10-15",
            ["puntoVentaFactura"] = 3,
            ["numeroFactura"] = 12345,
            ["lineas"] = new object[] { flete }
        };
        switch (caso)
        {
            case "más de 100 líneas":
                argumentos["lineas"] = Enumerable.Repeat<object>(flete, ArmadoDelBorradorDeCompra.MaximoDeLineas + 1).ToArray();
                break;
            case "costo faltante":
                argumentos["lineas"] = new object[] { new { concepto = "Servicio de flete", unidades = 1, idAlicuotaIva = e.IdIva21 } };
                break;
            case "descuento mayor al bruto":
                argumentos["lineas"] = new object[]
                {
                    new { concepto = "Servicio de flete", unidades = 1, costoUnitario = 10m, descuento = 11m, idAlicuotaIva = e.IdIva21 }
                };
                break;
            case "fecha del comprobante":
                argumentos["fechaComprobante"] = "15/10/2026";
                break;
            case "IVA impreso incompleto":
                argumentos["ivaImpreso"] = new object[] { new { idAlicuotaIva = e.IdIva21 } };
                break;
            default:
                argumentos["percepciones"] = new object[] { new { tipo = "iibb" } };
                break;
        }

        var resultado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, argumentos);

        Assert.True(resultado.EsError);
        Assert.Equal(mensaje, resultado.Texto);
        Assert.Equal(0, await _borrador.ContarComprasDelTenantAsync(e.Usuario.IdTenant));
    }

    /// <summary>Un número que no entra en su columna se rechaza antes de escribir: unas unidades que en realidad son
    /// un código de barras, y un total impreso que haría desbordar la resta contra el total calculado.</summary>
    [Theory]
    [InlineData("unidades de un código de barras")]
    [InlineData("total impreso mínimo de decimal")]
    public async Task UnNumeroQueNoEntraEnSuColumnaSeRechazaSinEscribir(string caso)
    {
        await using var e = await PrepararAsync("mcp-borrador-magnitud");
        var (argumentos, mensaje) = caso switch
        {
            "unidades de un código de barras" => (
                (object)new
                {
                    idProveedor = e.IdProveedor,
                    idTipoComprobante = e.IdFacturaA,
                    idPuntoVenta = e.IdPuntoVenta,
                    fechaComprobante = "2026-10-15",
                    puntoVentaFactura = 3,
                    numeroFactura = 12345,
                    lineas = new object[] { new { concepto = "Flete", unidades = 7790001234567m, costoUnitario = 10m, idAlicuotaIva = e.IdIva21 } }
                },
                "Línea 1: unidades admite hasta 9 dígitos enteros y 3 decimales."),
            _ => (
                new
                {
                    idProveedor = e.IdProveedor,
                    idTipoComprobante = e.IdFacturaA,
                    idPuntoVenta = e.IdPuntoVenta,
                    fechaComprobante = "2026-10-15",
                    puntoVentaFactura = 3,
                    numeroFactura = 12345,
                    lineas = new object[] { new { concepto = "Flete", unidades = 1, costoUnitario = 10m, idAlicuotaIva = e.IdIva21 } },
                    totalImpreso = decimal.MinValue
                },
                "totalImpreso admite hasta 12 dígitos enteros y 2 decimales.")
        };

        var resultado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, argumentos);

        Assert.True(resultado.EsError);
        Assert.Equal(mensaje, resultado.Texto);
        Assert.Equal(0, await _borrador.ContarComprasDelTenantAsync(e.Usuario.IdTenant));
    }

    /// <summary>Cada línea entra en sus columnas, pero el subtotal (2 × 600.000.000.000) no entra en el
    /// <c>numeric(14,2)</c> del encabezado: la base rechaza la fila con <c>22003</c> y la herramienta lo traduce con
    /// la clasificación de la API (<c>valor_fuera_de_rango</c>).</summary>
    [Fact]
    public async Task UnSubtotalQueNoEntraEnLaBaseVuelveComoValorFueraDeRangoSinEscribir()
    {
        await using var e = await PrepararAsync("mcp-borrador-fuera-de-rango");
        var linea = new { concepto = "Mercadería", unidades = 600_000_000m, costoUnitario = 1_000m, idAlicuotaIva = e.IdIva21 };

        var resultado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, new
        {
            idProveedor = e.IdProveedor,
            idTipoComprobante = e.IdFacturaA,
            idPuntoVenta = e.IdPuntoVenta,
            fechaComprobante = "2026-10-15",
            puntoVentaFactura = 3,
            numeroFactura = 12345,
            lineas = new object[] { linea, linea }
        });

        Assert.True(resultado.EsError);
        Assert.Equal(TextosDelBorradorDeCompra.ValorFueraDeRango, resultado.Texto);
        Assert.Equal(0, await _borrador.ContarComprasDelTenantAsync(e.Usuario.IdTenant));
    }

    /// <summary>El commit ambiguo de verdad: el borrador se comitea y recién después llega un <c>40001</c>
    /// (<see cref="InterceptorQueRompeElCommitAmbiguo"/>). La herramienta no puede saber si quedó creado: no
    /// reintenta, pide verificar el listado y deja el error en el log. El borrador existe, que es justo por qué un
    /// reintento a ciegas crearía otro.</summary>
    [Fact]
    public async Task UnFalloTransitorioAlComitearDiceQueElResultadoEsInciertoYNoReintenta()
    {
        var interceptor = new InterceptorQueRompeElCommitAmbiguo("comprobantes_compra", "40001");
        var captura = new CapturaDeLogs();
        await using var e = await PrepararAsync(
            "mcp-borrador-incierto",
            configurarMas: builder =>
            {
                builder.ConfigureServices(servicios =>
                    servicios.AddDbContext<WaysDbContext>((_, opciones) => opciones.AddInterceptors(interceptor.Interceptores)));
                builder.ConfigureLogging(logging => logging.AddProvider(captura));
            });

        var resultado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, FacturaSimple(e));

        Assert.True(resultado.EsError);
        Assert.Equal(TextosDelBorradorDeCompra.ResultadoIncierto, resultado.Texto);
        Assert.Equal(1, interceptor.CommitsRotos);
        Assert.Equal(1, interceptor.Inserciones);
        var creada = Assert.Single(await _borrador.ComprasDelProveedorAsync(e.IdProveedor));
        Assert.Equal(OrigenCompra.Asistente, creada.Origen);
        Assert.Contains(captura.Entradas, entrada =>
            entrada.Categoria == typeof(HerramientasDeCargaDeCompras).FullName
            && entrada.Nivel == LogLevel.Error
            && entrada.Mensaje == "Conector MCP: no se pudo confirmar si quedó creado el borrador de compra.");
    }

    /// <summary>La carrera que el chequeo previo no puede ver: otra carga guarda el mismo número entre el chequeo y
    /// el alta. El interceptor la inserta y comitea justo antes del INSERT del encabezado, que choca con
    /// <c>ux_comprobantes_compra_numero_externo</c>.</summary>
    [Fact]
    public async Task SiOtraCargaGuardaElMismoNumeroEntreElChequeoYElAltaLaHerramientaLoDiceYQuedaSoloLaOtra()
    {
        var interceptor = new InterceptorQueAdelantaOtraCarga();
        await using var e = await PrepararAsync(
            "mcp-borrador-carrera",
            configurarMas: builder => builder.ConfigureServices(servicios =>
                servicios.AddDbContext<WaysDbContext>((_, opciones) => opciones.AddInterceptors(interceptor))));
        int? otraCarga = null;
        interceptor.AntesDelAlta = async () => otraCarga = await _borrador.SembrarCompraAsync(
            e.Usuario.IdTenant, e.IdProveedor, e.IdFacturaA, e.IdPuntoVenta, e.Usuario.IdUsuario, "00003-00012345",
            EstadoCompra.Borrador, new DateOnly(2026, 10, 15));

        var resultado = await LlamarHerramientaAsync(e.Cliente, e.AccessToken, Herramienta, FacturaSimple(e));

        Assert.True(resultado.EsError);
        Assert.Equal(TextosDelBorradorDeCompra.ErrorDeCarrera, resultado.Texto);
        Assert.NotNull(otraCarga);
        Assert.Equal([otraCarga.Value], (await _borrador.ComprasDelProveedorAsync(e.IdProveedor)).Select(c => c.Id));
    }

    private static void AssertItem(
        ItemComprobanteCompra item, int orden, int? idArticulo, string descripcion, decimal cantidad, decimal? bultos,
        decimal? unidadesPorBulto, decimal costoUnitario, decimal descuento, int idAlicuotaIva, decimal porcentajeIva,
        decimal total, bool actualizaCosto, string? codigoProveedor, string? codigoLote, DateOnly? fechaVencimiento)
    {
        Assert.Equal(orden, item.Orden);
        Assert.Equal(idArticulo, item.IdArticulo);
        Assert.Equal(descripcion, item.Descripcion);
        Assert.Equal(cantidad, item.Cantidad);
        Assert.Equal(bultos, item.Bultos);
        Assert.Equal(unidadesPorBulto, item.UnidadesPorBulto);
        Assert.Equal(costoUnitario, item.CostoUnitario);
        Assert.Equal(descuento, item.Descuento);
        Assert.Equal(idAlicuotaIva, item.IdAlicuotaIva);
        Assert.Equal(porcentajeIva, item.PorcentajeIva);
        Assert.Equal(total, item.Total);
        Assert.Equal(actualizaCosto, item.ActualizaCosto);
        Assert.Equal(codigoProveedor, item.CodigoProveedor);
        Assert.Equal(codigoLote, item.CodigoLote);
        Assert.Equal(fechaVencimiento, item.FechaVencimiento);
    }

    private static IEnumerable<string> Propiedades(JsonElement esquema) =>
        esquema.GetProperty("properties").EnumerateObject().Select(p => p.Name);

    private static JsonElement Elementos(JsonElement esquema, string arreglo) =>
        esquema.GetProperty("properties").GetProperty(arreglo).GetProperty("items");

    /// <summary>Corre <see cref="AntesDelAlta"/> una sola vez, antes del primer INSERT de un encabezado de compra.</summary>
    private sealed class InterceptorQueAdelantaOtraCarga : DbCommandInterceptor
    {
        private int _disparado;

        public Func<Task> AntesDelAlta { get; set; } = () => Task.CompletedTask;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO comprobantes_compra (", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _disparado, 1) == 0)
            {
                await AntesDelAlta();
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
