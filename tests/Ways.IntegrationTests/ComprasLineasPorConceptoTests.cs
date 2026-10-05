using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Líneas de compra por concepto (sin artículo): suman al total y a la cuenta corriente del
/// proveedor, pero nunca mueven stock, costo ni lotes, y no cuentan para la cobertura de una
/// orden de compra. Todo a través de la API real.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasLineasPorConceptoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdArticulo, int IdAlicuotaIva21,
        int IdTipoCFA, int IdTipoCFB);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var solicitud = new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Concepto-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            Margen = 50m, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var articulo = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-1-{Guid.NewGuid():N}", Nombre = "Articulo 1",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            IdProveedorHabitual = proveedor.Id, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();
        var idTipoCFB = await db.TiposComprobante.Where(t => t.Codigo == "C-FB").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, admin, proveedor.Id, articulo.Id, idAlicuotaIva21,
            idTipoCFA, idTipoCFB);
    }

    private static LineaDeCompraSolicitada Concepto(
        Contexto ctx, string descripcion = "Factura ferretería", decimal importe = 1234.56m,
        decimal? bultos = null, string? codigoLote = null, DateOnly? fechaVencimiento = null, bool? actualizaCosto = null) =>
        new(null, descripcion, 1m, bultos, null, importe, 0m, ctx.IdAlicuotaIva21, actualizaCosto, codigoLote, fechaVencimiento);

    private static LineaDeCompraSolicitada DeArticulo(Contexto ctx, decimal unidades = 10m, decimal costo = 100m) =>
        new(ctx.IdArticulo, "Artículo", unidades, null, null, costo, 0m, ctx.IdAlicuotaIva21);

    private static SolicitudDeCompra Solicitud(
        Contexto ctx, int idTipo, IReadOnlyList<LineaDeCompraSolicitada> items, int? idOrdenCompra = null) =>
        new(ctx.IdProveedor, idTipo, ctx.IdPuntoVenta, DatosDePrueba.NumeroExternoUnico(), FechaDelNegocio.Hoy(), null, items,
            idOrdenCompra);

    private static async Task<CompraDetalle> CrearBorradorAsync(Contexto ctx, SolicitudDeCompra solicitud)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<CompraDetalle> ConfirmarAsync(Contexto ctx, int id)
    {
        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task AnularAsync(Contexto ctx, int id)
    {
        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{id}/anular", null);
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());
    }

    private static async Task AssertRechazoAsync(HttpResponseMessage respuesta, string codigoEsperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, cuerpo);
        Assert.Equal(codigoEsperado, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private WaysDbContext Db(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    // ---- compra solo por concepto ------------------------------------------------------------------

    [Fact]
    public async Task UnaCompraSoloPorConceptoSeCreaYElItemVuelveSinArticuloNiPrecioSugerido()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraSoloPorConceptoSeCreaYElItemVuelveSinArticuloNiPrecioSugerido));

        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx)]));

        var item = Assert.Single(creada.Items);
        Assert.Null(item.IdArticulo);
        Assert.Equal("Factura ferretería", item.Descripcion);
        Assert.Equal(1m, item.Cantidad);
        Assert.Equal(1234.56m, item.CostoUnitario);
        Assert.Equal(1234.56m, item.Total);
        Assert.False(item.ActualizaCosto);
        Assert.Null(item.PrecioSugerido);
        Assert.Null(item.CodigoLote);
        Assert.Null(item.IdLote);
        Assert.Equal(1234.56m, creada.Total);

        var releida = await ctx.Admin.GetFromJsonAsync<CompraDetalle>($"/api/compras/{creada.Id}", OpcionesJson);
        Assert.Null(Assert.Single(releida!.Items).IdArticulo);
    }

    [Fact]
    public async Task ConfirmarYAnularUnaCompraSoloPorConceptoNoTocaStockNiCostoYMueveLaCuentaCorrienteCompleta()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarYAnularUnaCompraSoloPorConceptoNoTocaStockNiCostoYMueveLaCuentaCorrienteCompleta));
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx)]));

        var confirmada = await ConfirmarAsync(ctx, creada.Id);
        Assert.Equal(EstadoCompra.Confirmada, confirmada.Estado);

        await using (var db = Db(ctx))
        {
            Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id));
            Assert.Equal(0, await db.Stock.CountAsync());
            Assert.Null((await db.Articulos.AsNoTracking().FirstAsync(a => a.Id == ctx.IdArticulo)).CostoNominal);

            var compra = await db.MovimientosCuentaCorrienteProveedor.AsNoTracking()
                .SingleAsync(m => m.IdComprobanteCompra == creada.Id);
            Assert.Equal(TipoMovimientoCcProveedor.Compra, compra.Tipo);
            Assert.Equal(1234.56m, compra.Importe);
            Assert.Equal(1234.56m, compra.SaldoResultante);
            Assert.Equal(1234.56m, (await db.Proveedores.AsNoTracking().FirstAsync(p => p.Id == ctx.IdProveedor)).Saldo);
        }

        await AnularAsync(ctx, creada.Id);

        await using (var db = Db(ctx))
        {
            Assert.Equal(0, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id));
            Assert.Equal(0, await db.Stock.CountAsync());

            var ajuste = await db.MovimientosCuentaCorrienteProveedor.AsNoTracking()
                .SingleAsync(m => m.IdComprobanteCompra == creada.Id && m.Tipo == TipoMovimientoCcProveedor.Ajuste);
            Assert.Equal(-1234.56m, ajuste.Importe);
            Assert.Equal(0m, (await db.Proveedores.AsNoTracking().FirstAsync(p => p.Id == ctx.IdProveedor)).Saldo);
        }
    }

    // ---- compra mixta: artículo + concepto --------------------------------------------------------

    [Fact]
    public async Task UnaCompraMixtaSoloMueveStockYCostoDeLaLineaConArticuloYLaCuentaCorrienteRecibeElTotalCompleto()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraMixtaSoloMueveStockYCostoDeLaLineaConArticuloYLaCuentaCorrienteRecibeElTotalCompleto));

        // C-FA discrimina IVA: (10 x 100 + 500) = 1500 neto, 315 de IVA, total 1815.
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [DeArticulo(ctx), Concepto(ctx, "Flete", 500m)]));
        Assert.Equal(1815m, creada.Total);
        Assert.Equal(181.5m, creada.Items[0].PrecioSugerido);
        Assert.Null(creada.Items[1].PrecioSugerido);

        await ConfirmarAsync(ctx, creada.Id);

        await using (var db = Db(ctx))
        {
            var movimiento = await db.MovimientosStock.AsNoTracking().SingleAsync(m => m.IdComprobanteCompra == creada.Id);
            Assert.Equal(ctx.IdArticulo, movimiento.IdArticulo);
            Assert.Equal(10m, movimiento.Cantidad);

            var stock = await db.Stock.AsNoTracking().SingleAsync();
            Assert.Equal(ctx.IdArticulo, stock.IdArticulo);
            Assert.Equal(10m, stock.Cantidad);

            // costo efectivo del artículo = 100 + 21% de IVA; el flete no influye.
            Assert.Equal(121m, (await db.Articulos.AsNoTracking().FirstAsync(a => a.Id == ctx.IdArticulo)).CostoNominal);

            var compra = await db.MovimientosCuentaCorrienteProveedor.AsNoTracking()
                .SingleAsync(m => m.IdComprobanteCompra == creada.Id);
            Assert.Equal(1815m, compra.Importe);
        }

        await AnularAsync(ctx, creada.Id);

        await using (var db = Db(ctx))
        {
            Assert.Equal(0m, (await db.Stock.AsNoTracking().SingleAsync()).Cantidad);
            Assert.Equal(2, await db.MovimientosStock.CountAsync(m => m.IdComprobanteCompra == creada.Id));
            Assert.Equal(0m, (await db.Proveedores.AsNoTracking().FirstAsync(p => p.Id == ctx.IdProveedor)).Saldo);
        }
    }

    [Fact]
    public async Task AplicarPreciosSobreUnaCompraMixtaResuelveSoloLaLineaConArticulo()
    {
        var ctx = await PrepararAsync(nameof(AplicarPreciosSobreUnaCompraMixtaResuelveSoloLaLineaConArticulo));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, "Flete", 500m), DeArticulo(ctx)]));
        await ConfirmarAsync(ctx, creada.Id);

        int idLista;
        await using (var h = Db(ctx))
        {
            var ahora = DateTimeOffset.UtcNow;
            var lista = new ListaPrecio
            {
                IdTenant = ctx.IdTenant, Nombre = "Lista de prueba", EsDefault = false, Modo = ModoLista.Fija,
                Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            h.ListasPrecio.Add(lista);
            await h.SaveChangesAsync();
            idLista = lista.Id;
        }

        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/compras/{creada.Id}/precios", new SolicitudDeAplicarPrecios(idLista));
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var resultados = (await respuesta.Content.ReadFromJsonAsync<List<ResultadoAplicarPrecio>>(OpcionesJson))!;

        var resultado = Assert.Single(resultados);
        Assert.Equal(ctx.IdArticulo, resultado.IdArticulo);
        Assert.True(resultado.Aplicado);
    }

    /// <summary>Una compra admite dos líneas del mismo artículo, así que <c>IdArticulo</c> no identifica un
    /// resultado de aplicar precios: <c>Orden</c> sí. La línea por concepto va ANTES de ellas y no figura en
    /// la respuesta, de modo que el <c>Orden</c> de cada resultado (2 y 3) difiere de su posición en la
    /// lista (1 y 2). Las dos ramas que arman un resultado se ejercitan por separado: el rechazo (hay un
    /// precio pendiente y no se confirmó el reemplazo) y la aplicación (se confirmó).</summary>
    [Fact]
    public async Task AplicarPreciosConDosLineasDelMismoArticuloIdentificaCadaResultadoPorElOrdenDeSuLinea()
    {
        var ctx = await PrepararAsync(nameof(AplicarPreciosConDosLineasDelMismoArticuloIdentificaCadaResultadoPorElOrdenDeSuLinea));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, "Flete", 500m), DeArticulo(ctx), DeArticulo(ctx, costo: 200m)]));
        var confirmada = await ConfirmarAsync(ctx, creada.Id);

        Assert.Equal([1, 2, 3], confirmada.Items.Select(i => i.Orden));
        Assert.Null(confirmada.Items[0].IdArticulo);
        Assert.Equal(ctx.IdArticulo, confirmada.Items[1].IdArticulo);
        Assert.Equal(ctx.IdArticulo, confirmada.Items[2].IdArticulo);
        Assert.NotNull(confirmada.Items[1].PrecioSugerido);
        Assert.NotNull(confirmada.Items[2].PrecioSugerido);
        Assert.NotEqual(confirmada.Items[1].PrecioSugerido, confirmada.Items[2].PrecioSugerido);

        int idLista;
        await using (var h = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            var lista = new ListaPrecio
            {
                IdTenant = ctx.IdTenant, Nombre = "Lista de prueba", EsDefault = false, Modo = ModoLista.Fija,
                Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            h.ListasPrecio.Add(lista);
            await h.SaveChangesAsync();

            h.Precios.Add(new Precio
            {
                IdTenant = ctx.IdTenant, IdArticulo = ctx.IdArticulo, IdListaPrecio = lista.Id, Monto = 120m,
                VigenteDesde = ahora.AddDays(3), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
            await h.SaveChangesAsync();
            idLista = lista.Id;
        }

        async Task<List<ResultadoAplicarPrecio>> AplicarAsync(bool confirmarReemplazo)
        {
            var respuesta = await ctx.Admin.PostAsJsonAsync(
                $"/api/compras/{creada.Id}/precios", new SolicitudDeAplicarPrecios(idLista, confirmarReemplazo));
            var cuerpo = await respuesta.Content.ReadAsStringAsync();
            Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
            return JsonSerializer.Deserialize<List<ResultadoAplicarPrecio>>(cuerpo, OpcionesJson)!;
        }

        var rechazados = await AplicarAsync(confirmarReemplazo: false);

        Assert.Equal([2, 3], rechazados.Select(r => r.Orden));
        Assert.All(rechazados, r => Assert.Equal(ctx.IdArticulo, r.IdArticulo));
        Assert.All(rechazados, r => Assert.False(r.Aplicado, r.Error));
        Assert.All(rechazados, r => Assert.Null(r.Precio));
        Assert.All(rechazados, r => Assert.Contains("precio pendiente", r.Error, StringComparison.Ordinal));

        var aplicados = await AplicarAsync(confirmarReemplazo: true);

        Assert.Equal([2, 3], aplicados.Select(r => r.Orden));
        Assert.All(aplicados, r => Assert.Equal(ctx.IdArticulo, r.IdArticulo));
        Assert.All(aplicados, r => Assert.True(r.Aplicado, r.Error));
        Assert.All(aplicados, r => Assert.Null(r.Error));
        Assert.All(aplicados, r => Assert.Equal(confirmada.Items.Single(i => i.Orden == r.Orden).PrecioSugerido, r.Precio));
    }

    [Fact]
    public async Task ActualizaCostoOmitidoEnUnArticuloSigueSiendoVerdaderoPorDefecto()
    {
        var ctx = await PrepararAsync(nameof(ActualizaCostoOmitidoEnUnArticuloSigueSiendoVerdaderoPorDefecto));
        var cuerpo = new StringContent(
            "{\"idProveedor\":" + ctx.IdProveedor + ",\"idTipoComprobante\":" + ctx.IdTipoCFB +
            ",\"idPuntoVenta\":" + ctx.IdPuntoVenta + ",\"numeroExterno\":\"" + DatosDePrueba.NumeroExternoUnico() +
            "\",\"fechaComprobante\":\"" + FechaDelNegocio.Hoy().ToString("yyyy-MM-dd") + "\",\"items\":[{\"idArticulo\":" +
            ctx.IdArticulo + ",\"descripcion\":\"Articulo\",\"unidades\":2,\"costoUnitario\":50,\"descuento\":0," +
            "\"idAlicuotaIva\":" + ctx.IdAlicuotaIva21 + "}]}",
            Encoding.UTF8, "application/json");

        var respuesta = await ctx.Admin.PostAsync("/api/compras", cuerpo);
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, texto);
        var creada = JsonSerializer.Deserialize<CompraDetalle>(texto, OpcionesJson)!;
        Assert.True(Assert.Single(creada.Items).ActualizaCosto);

        await ConfirmarAsync(ctx, creada.Id);

        await using var h = Db(ctx);
        Assert.Equal(50m, (await h.Articulos.AsNoTracking().FirstAsync(a => a.Id == ctx.IdArticulo)).CostoNominal);
    }

    // ---- validación: nunca aceptar y descartar -------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnConceptoSinDescripcionEsRechazadoConConceptoSinDescripcion(string descripcion)
    {
        var ctx = await PrepararAsync($"{nameof(UnConceptoSinDescripcionEsRechazadoConConceptoSinDescripcion)}{descripcion.Length}");

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/compras", Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx, descripcion)]));

        await AssertRechazoAsync(respuesta, "concepto_sin_descripcion");
        await using var h = Db(ctx);
        Assert.Equal(0, await h.ComprobantesCompra.CountAsync());
    }

    [Fact]
    public async Task UnConceptoConLoteEsRechazadoConConceptoConLote()
    {
        var ctx = await PrepararAsync(nameof(UnConceptoConLoteEsRechazadoConConceptoConLote));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/compras",
            Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx, codigoLote: "L-1", fechaVencimiento: FechaDelNegocio.Hoy().AddDays(30))]));

        await AssertRechazoAsync(respuesta, "concepto_con_lote");
    }

    [Fact]
    public async Task UnConceptoConSoloFechaDeVencimientoEsRechazadoConConceptoConLote()
    {
        var ctx = await PrepararAsync(nameof(UnConceptoConSoloFechaDeVencimientoEsRechazadoConConceptoConLote));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/compras",
            Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx, fechaVencimiento: FechaDelNegocio.Hoy().AddDays(30))]));

        await AssertRechazoAsync(respuesta, "concepto_con_lote");
    }

    [Fact]
    public async Task UnConceptoConBultosEsRechazadoConConceptoConBultos()
    {
        var ctx = await PrepararAsync(nameof(UnConceptoConBultosEsRechazadoConConceptoConBultos));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/compras", Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx, bultos: 3m)]));

        await AssertRechazoAsync(respuesta, "concepto_con_bultos");
    }

    [Fact]
    public async Task UnConceptoConActualizaCostoVerdaderoEsRechazadoYFalsoEsAceptado()
    {
        var ctx = await PrepararAsync(nameof(UnConceptoConActualizaCostoVerdaderoEsRechazadoYFalsoEsAceptado));

        var rechazada = await ctx.Admin.PostAsJsonAsync(
            "/api/compras", Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx, actualizaCosto: true)]));
        await AssertRechazoAsync(rechazada, "concepto_actualiza_costo");

        var aceptada = await ctx.Admin.PostAsJsonAsync(
            "/api/compras", Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx, actualizaCosto: false)]));
        Assert.Equal(HttpStatusCode.Created, aceptada.StatusCode);
    }

    [Fact]
    public async Task EditarUnBorradorConUnConceptoInvalidoDa400YNoTocaLoGuardado()
    {
        var ctx = await PrepararAsync(nameof(EditarUnBorradorConUnConceptoInvalidoDa400YNoTocaLoGuardado));
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx)]));

        var respuesta = await ctx.Admin.PutAsJsonAsync(
            $"/api/compras/{creada.Id}", Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx, bultos: 2m)]));

        await AssertRechazoAsync(respuesta, "concepto_con_bultos");
        var releida = (await ctx.Admin.GetFromJsonAsync<CompraDetalle>($"/api/compras/{creada.Id}", OpcionesJson))!;
        Assert.Null(Assert.Single(releida.Items).Bultos);
    }

    [Fact]
    public async Task UnBorradorPuedeEditarseDeArticuloAConceptoYViceversa()
    {
        var ctx = await PrepararAsync(nameof(UnBorradorPuedeEditarseDeArticuloAConceptoYViceversa));
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, ctx.IdTipoCFB, [DeArticulo(ctx)]));

        var aConcepto = await ctx.Admin.PutAsJsonAsync(
            $"/api/compras/{creada.Id}", Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx)]));
        Assert.Equal(HttpStatusCode.OK, aConcepto.StatusCode);
        var comoConcepto = (await aConcepto.Content.ReadFromJsonAsync<CompraDetalle>(OpcionesJson))!;
        Assert.Null(Assert.Single(comoConcepto.Items).IdArticulo);

        var aArticulo = await ctx.Admin.PutAsJsonAsync(
            $"/api/compras/{creada.Id}", Solicitud(ctx, ctx.IdTipoCFB, [DeArticulo(ctx)]));
        Assert.Equal(HttpStatusCode.OK, aArticulo.StatusCode);
        var comoArticulo = (await aArticulo.Content.ReadFromJsonAsync<CompraDetalle>(OpcionesJson))!;
        Assert.Equal(ctx.IdArticulo, Assert.Single(comoArticulo.Items).IdArticulo);
    }

    // ---- orden de compra: el concepto no recibe mercadería -----------------------------------------

    [Fact]
    public async Task UnaRecepcionSoloPorConceptoLigadaAUnaOrdenNoLaMarcaComoRecibidaYNoBloqueaSuAnulacion()
    {
        var ctx = await PrepararAsync(nameof(UnaRecepcionSoloPorConceptoLigadaAUnaOrdenNoLaMarcaComoRecibidaYNoBloqueaSuAnulacion));

        var respuestaOrden = await ctx.Admin.PostAsJsonAsync(
            "/api/ordenes-compra",
            new SolicitudDeOrdenDeCompra(
                ctx.IdProveedor, ctx.IdPuntoVenta, null, null,
                [new LineaDeOrdenSolicitada(ctx.IdArticulo, "Item de orden", 10m, 100m)]));
        Assert.Equal(HttpStatusCode.Created, respuestaOrden.StatusCode);
        var orden = (await respuestaOrden.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/ordenes-compra/{orden.Id}/enviar", null)).StatusCode);

        var recepcion = await CrearBorradorAsync(ctx, Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx)], orden.Id));
        await ConfirmarAsync(ctx, recepcion.Id);

        await using (var h = Db(ctx))
        {
            var estado = (await h.OrdenesCompra.AsNoTracking().FirstAsync(o => o.Id == orden.Id)).Estado;
            Assert.Equal(EstadoOrdenCompra.Enviada, estado);
        }

        var anulacion = await ctx.Admin.PostAsync($"/api/ordenes-compra/{orden.Id}/anular", null);
        Assert.True(anulacion.StatusCode == HttpStatusCode.OK, await anulacion.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LaCoberturaDeLaOrdenIgnoraLasLineasPorConceptoDeLasRecepciones()
    {
        var ctx = await PrepararAsync(nameof(LaCoberturaDeLaOrdenIgnoraLasLineasPorConceptoDeLasRecepciones));

        var respuestaOrden = await ctx.Admin.PostAsJsonAsync(
            "/api/ordenes-compra",
            new SolicitudDeOrdenDeCompra(
                ctx.IdProveedor, ctx.IdPuntoVenta, null, null,
                [new LineaDeOrdenSolicitada(ctx.IdArticulo, "Item de orden", 10m, 100m)]));
        var orden = (await respuestaOrden.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;
        await ctx.Admin.PostAsync($"/api/ordenes-compra/{orden.Id}/enviar", null);

        var recepcion = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx, "Flete", 500m), DeArticulo(ctx, unidades: 4m)], orden.Id));
        await ConfirmarAsync(ctx, recepcion.Id);

        var detalle = (await ctx.Admin.GetFromJsonAsync<OrdenDeCompraDetalle>($"/api/ordenes-compra/{orden.Id}", OpcionesJson))!;
        var fila = Assert.Single(detalle.Cobertura);
        Assert.Equal(ctx.IdArticulo, fila.IdArticulo);
        Assert.Equal(4m, fila.Recibida);
    }
}
