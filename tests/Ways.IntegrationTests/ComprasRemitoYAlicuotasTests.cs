using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Catalogos;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Remito / comprobante no fiscal (<c>C-RM</c>), <c>discrimina_iva</c> por comprobante y desglose
/// de IVA por alícuota con override del IVA impreso. Todo a través de la API real.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasRemitoYAlicuotasTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdArticulo,
        int IdAlicuota21, int IdAlicuota105, int IdAlicuotaExento,
        int IdTipoCFA, int IdTipoCFB, int IdTipoCFC, int IdTipoCRM);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Remito-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuota21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();
        var idAlicuota105 = await db.AlicuotasIva.Where(a => a.Nombre == "10.5%").Select(a => a.Id).FirstAsync();
        var idAlicuotaExento = await db.AlicuotasIva.Where(a => a.Nombre == "Exento").Select(a => a.Id).FirstAsync();

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
            IdArea = area.Id, IdAlicuotaIva = idAlicuota21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            IdProveedorHabitual = proveedor.Id, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        async Task<int> TipoAsync(string codigo) =>
            await db.TiposComprobante.Where(t => t.Codigo == codigo).Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, admin, proveedor.Id, articulo.Id,
            idAlicuota21, idAlicuota105, idAlicuotaExento,
            await TipoAsync("C-FA"), await TipoAsync("C-FB"), await TipoAsync("C-FC"), await TipoAsync("C-RM"));
    }

    private static LineaDeCompraSolicitada Concepto(
        Contexto ctx, decimal importe = 1000m, int? idAlicuota = null, string descripcion = "Remito proveedor") =>
        new(null, descripcion, 1m, null, null, importe, 0m, idAlicuota ?? ctx.IdAlicuota21);

    private static LineaDeCompraSolicitada DeArticulo(Contexto ctx, decimal unidades = 10m, decimal costo = 100m) =>
        new(ctx.IdArticulo, "Artículo", unidades, null, null, costo, 0m, ctx.IdAlicuota21);

    private static SolicitudDeCompra Solicitud(
        Contexto ctx, int idTipo, IReadOnlyList<LineaDeCompraSolicitada> items, bool? discriminaIva = null,
        IReadOnlyList<IvaImpresoSolicitado>? ivaImpreso = null, int? idOrdenCompra = null) =>
        new(ctx.IdProveedor, idTipo, ctx.IdPuntoVenta, DatosDePrueba.NumeroExternoUnico(), FechaDelNegocio.Hoy(), null, items,
            idOrdenCompra, discriminaIva, ivaImpreso);

    private static async Task<CompraDetalle> CrearBorradorAsync(Contexto ctx, SolicitudDeCompra solicitud)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<CompraDetalle> ActualizarAsync(Contexto ctx, int id, SolicitudDeCompra solicitud)
    {
        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/compras/{id}", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task<CompraDetalle> ConfirmarAsync(Contexto ctx, int id)
    {
        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!;
    }

    private static async Task AssertRechazoAsync(HttpResponseMessage respuesta, string codigoEsperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, cuerpo);
        Assert.Equal(codigoEsperado, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private WaysDbContext Db(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    private async Task<List<AlicuotaComprobanteCompra>> AlicuotasGuardadasAsync(Contexto ctx, int idCompra)
    {
        await using var db = Db(ctx);
        return await db.AlicuotasComprobanteCompra.AsNoTracking()
            .Where(a => a.IdComprobanteCompra == idCompra)
            .OrderBy(a => a.IdAlicuotaIva)
            .ToListAsync();
    }

    // ---- el tipo C-RM y registra_libro_iva ----------------------------------------------------------

    [Fact]
    public async Task ElTipoRemitoEsDeCompraNoFiscalYNoRegistraLibroIvaMientrasLasFacturasSi()
    {
        var ctx = await PrepararAsync(nameof(ElTipoRemitoEsDeCompraNoFiscalYNoRegistraLibroIvaMientrasLasFacturasSi));

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var remito = await db.TiposComprobante.AsNoTracking().SingleAsync(t => t.Id == ctx.IdTipoCRM);

        Assert.Equal(ClaseComprobante.Compra, remito.Clase);
        Assert.Equal("Remito / comprobante no fiscal", remito.Nombre);
        Assert.False(remito.RegistraLibroIva);
        Assert.False(remito.DiscriminaIva);
        Assert.False(remito.EsFiscal);
        Assert.True(remito.AfectaStock);
        Assert.True(remito.Activo);

        var conLibroIva = await db.TiposComprobante.AsNoTracking()
            .Where(t => t.RegistraLibroIva)
            .Select(t => t.Codigo)
            .OrderBy(c => c)
            .ToListAsync();
        Assert.Equal(["C-FA", "C-FB", "C-FC"], conLibroIva);
    }

    [Fact]
    public async Task ElCatalogoDeTiposExponeRegistraLibroIva()
    {
        var ctx = await PrepararAsync(nameof(ElCatalogoDeTiposExponeRegistraLibroIva));

        var tipos = (await ctx.Admin.GetFromJsonAsync<List<TipoComprobanteListado>>(
            "/api/catalogos-fiscales/tipos-comprobante", OpcionesJson))!;

        Assert.False(tipos.Single(t => t.Codigo == "C-RM").RegistraLibroIva);
        Assert.True(tipos.Single(t => t.Codigo == "C-FA").RegistraLibroIva);
        Assert.False(tipos.Single(t => t.Codigo == "FA").RegistraLibroIva);
    }

    // ---- remito sin IVA discriminado ------------------------------------------------------------------

    [Fact]
    public async Task UnRemitoSinDiscriminarIvaGuardaElTotalSinDesglose()
    {
        var ctx = await PrepararAsync(nameof(UnRemitoSinDiscriminarIvaGuardaElTotalSinDesglose));

        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, ctx.IdTipoCRM, [Concepto(ctx, 1234.56m)]));

        Assert.False(creada.DiscriminaIva);
        Assert.Null(creada.IvaTotal);
        Assert.Empty(creada.Alicuotas);
        Assert.Equal(1234.56m, creada.Total);

        await using var db = Db(ctx);
        Assert.False((await db.ComprobantesCompra.AsNoTracking().SingleAsync(c => c.Id == creada.Id)).DiscriminaIva);
        Assert.Empty(await AlicuotasGuardadasAsync(ctx, creada.Id));
    }

    [Fact]
    public async Task UnRemitoSinDiscriminarIvaConArticuloConfirmaConElCostoSinIvaYMueveElStock()
    {
        var ctx = await PrepararAsync(nameof(UnRemitoSinDiscriminarIvaConArticuloConfirmaConElCostoSinIvaYMueveElStock));
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, ctx.IdTipoCRM, [DeArticulo(ctx)], discriminaIva: false));

        var confirmada = await ConfirmarAsync(ctx, creada.Id);

        Assert.Equal(1000m, confirmada.Total);
        await using var db = Db(ctx);
        Assert.Equal(100m, (await db.Articulos.AsNoTracking().FirstAsync(a => a.Id == ctx.IdArticulo)).CostoNominal);
        Assert.Equal(10m, (await db.Stock.AsNoTracking().SingleAsync()).Cantidad);
        Assert.Equal(1000m, (await db.Proveedores.AsNoTracking().FirstAsync(p => p.Id == ctx.IdProveedor)).Saldo);
    }

    // ---- remito con IVA discriminado ------------------------------------------------------------------

    [Fact]
    public async Task UnRemitoQueDiscriminaIvaGuardaElDesglosePorAlicuotaYLoDevuelveEnElDetalle()
    {
        var ctx = await PrepararAsync(nameof(UnRemitoQueDiscriminaIvaGuardaElDesglosePorAlicuotaYLoDevuelveEnElDetalle));

        var creada = await CrearBorradorAsync(
            ctx,
            Solicitud(
                ctx, ctx.IdTipoCRM,
                [Concepto(ctx, 1000m), Concepto(ctx, 200m), Concepto(ctx, 400m, ctx.IdAlicuota105)],
                discriminaIva: true));

        Assert.True(creada.DiscriminaIva);
        Assert.Equal(2, creada.Alicuotas.Count);
        var ordenadas = creada.Alicuotas.OrderBy(a => a.IdAlicuotaIva).ToList();
        Assert.Equal(creada.Alicuotas, ordenadas);

        var a21 = creada.Alicuotas.Single(a => a.IdAlicuotaIva == ctx.IdAlicuota21);
        Assert.Equal(new AlicuotaDeCompra(ctx.IdAlicuota21, 21m, 1200m, 252m), a21);
        var a105 = creada.Alicuotas.Single(a => a.IdAlicuotaIva == ctx.IdAlicuota105);
        Assert.Equal(new AlicuotaDeCompra(ctx.IdAlicuota105, 10.5m, 400m, 42m), a105);
        Assert.Equal(294m, creada.IvaTotal);
        Assert.Equal(1894m, creada.Total);

        var guardadas = await AlicuotasGuardadasAsync(ctx, creada.Id);
        Assert.Equal(2, guardadas.Count);
        Assert.Equal(294m, guardadas.Sum(a => a.Iva));

        var releida = (await ctx.Admin.GetFromJsonAsync<CompraDetalle>($"/api/compras/{creada.Id}", OpcionesJson))!;
        Assert.True(releida.DiscriminaIva);
        Assert.Equal(creada.Alicuotas, releida.Alicuotas);
    }

    [Fact]
    public async Task ExentoYNoGravadoSonFilasDeAlicuotaCorrientesQueSalenConIvaCero()
    {
        var ctx = await PrepararAsync(nameof(ExentoYNoGravadoSonFilasDeAlicuotaCorrientesQueSalenConIvaCero));

        var creada = await CrearBorradorAsync(
            ctx,
            Solicitud(
                ctx, ctx.IdTipoCRM, [Concepto(ctx, 1000m), Concepto(ctx, 300m, ctx.IdAlicuotaExento)], discriminaIva: true));

        var exento = creada.Alicuotas.Single(a => a.IdAlicuotaIva == ctx.IdAlicuotaExento);
        Assert.Equal(300m, exento.Neto);
        Assert.Equal(0m, exento.Iva);
        Assert.Equal(210m, creada.IvaTotal);
        Assert.Equal(1510m, creada.Total);
    }

    [Fact]
    public async Task UnRemitoQueDiscriminaConfirmaConElCostoConIvaDelComprobanteNoElDelTipo()
    {
        var ctx = await PrepararAsync(nameof(UnRemitoQueDiscriminaConfirmaConElCostoConIvaDelComprobanteNoElDelTipo));
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, ctx.IdTipoCRM, [DeArticulo(ctx)], discriminaIva: true));
        Assert.Equal(1210m, creada.Total);

        await ConfirmarAsync(ctx, creada.Id);

        // El tipo C-RM no discrimina IVA (false): 121 solo sale del flag del comprobante.
        await using var db = Db(ctx);
        Assert.Equal(121m, (await db.Articulos.AsNoTracking().FirstAsync(a => a.Id == ctx.IdArticulo)).CostoNominal);
        var movimiento = await db.MovimientosCuentaCorrienteProveedor.AsNoTracking()
            .SingleAsync(m => m.IdComprobanteCompra == creada.Id);
        Assert.Equal(TipoMovimientoCcProveedor.Compra, movimiento.Tipo);
        Assert.Equal(1210m, movimiento.Importe);
    }

    [Fact]
    public async Task LaCoberturaDeLaOrdenCalculaElCostoRealConElFlagDelComprobanteDelRemito()
    {
        var ctx = await PrepararAsync(nameof(LaCoberturaDeLaOrdenCalculaElCostoRealConElFlagDelComprobanteDelRemito));
        var respuestaOrden = await ctx.Admin.PostAsJsonAsync(
            "/api/ordenes-compra",
            new SolicitudDeOrdenDeCompra(
                ctx.IdProveedor, ctx.IdPuntoVenta, null, null,
                [new LineaDeOrdenSolicitada(ctx.IdArticulo, "Item de orden", 10m, 100m)]));
        var orden = (await respuestaOrden.Content.ReadFromJsonAsync<OrdenDeCompraBorrador>(OpcionesJson))!;
        await ctx.Admin.PostAsync($"/api/ordenes-compra/{orden.Id}/enviar", null);

        var recepcion = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCRM, [DeArticulo(ctx, unidades: 4m)], discriminaIva: true, idOrdenCompra: orden.Id));
        await ConfirmarAsync(ctx, recepcion.Id);

        var detalle = (await ctx.Admin.GetFromJsonAsync<OrdenDeCompraDetalle>($"/api/ordenes-compra/{orden.Id}", OpcionesJson))!;
        Assert.Equal(121m, Assert.Single(detalle.Cobertura).CostoReal);
    }

    // ---- override del IVA impreso -----------------------------------------------------------------------

    [Fact]
    public async Task UnIvaImpresoDentroDeLaToleranciaSeGuardaYElIvaTotalYElTotalSalenDeLasFilasGuardadas()
    {
        var ctx = await PrepararAsync(nameof(UnIvaImpresoDentroDeLaToleranciaSeGuardaYElIvaTotalYElTotalSalenDeLasFilasGuardadas));

        var creada = await CrearBorradorAsync(
            ctx,
            Solicitud(
                ctx, ctx.IdTipoCRM, [Concepto(ctx, 1000m)], discriminaIva: true,
                ivaImpreso: [new IvaImpresoSolicitado(ctx.IdAlicuota21, 210.5m)]));

        Assert.Equal(210.5m, Assert.Single(creada.Alicuotas).Iva);
        Assert.Equal(210.5m, creada.IvaTotal);
        Assert.Equal(1210.5m, creada.Total);

        var guardada = Assert.Single(await AlicuotasGuardadasAsync(ctx, creada.Id));
        Assert.Equal(210.5m, guardada.Iva);
        await using (var db = Db(ctx))
        {
            var comprobante = await db.ComprobantesCompra.AsNoTracking().SingleAsync(c => c.Id == creada.Id);
            Assert.Equal(210.5m, comprobante.IvaTotal);
            Assert.Equal(1210.5m, comprobante.Total);
        }

        var confirmada = await ConfirmarAsync(ctx, creada.Id);
        Assert.Equal(1210.5m, confirmada.Total);
        await using var dbDespues = Db(ctx);
        var movimiento = await dbDespues.MovimientosCuentaCorrienteProveedor.AsNoTracking()
            .SingleAsync(m => m.IdComprobanteCompra == creada.Id);
        Assert.Equal(1210.5m, movimiento.Importe);
    }

    [Fact]
    public async Task UnIvaImpresoFueraDeLaToleranciaDa400YNoCreaNada()
    {
        var ctx = await PrepararAsync(nameof(UnIvaImpresoFueraDeLaToleranciaDa400YNoCreaNada));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/compras",
            Solicitud(
                ctx, ctx.IdTipoCRM, [Concepto(ctx, 1000m)], discriminaIva: true,
                ivaImpreso: [new IvaImpresoSolicitado(ctx.IdAlicuota21, 211.01m)]));

        await AssertRechazoAsync(respuesta, "iva_impreso_fuera_de_tolerancia");
        await using var db = Db(ctx);
        Assert.Equal(0, await db.ComprobantesCompra.CountAsync());
        Assert.Equal(0, await db.AlicuotasComprobanteCompra.CountAsync());
    }

    [Fact]
    public async Task UnIvaImpresoExactamenteAUnPesoDelCalculadoSeAcepta()
    {
        var ctx = await PrepararAsync(nameof(UnIvaImpresoExactamenteAUnPesoDelCalculadoSeAcepta));

        var creada = await CrearBorradorAsync(
            ctx,
            Solicitud(
                ctx, ctx.IdTipoCRM, [Concepto(ctx, 1000m)], discriminaIva: true,
                ivaImpreso: [new IvaImpresoSolicitado(ctx.IdAlicuota21, 211m)]));

        Assert.Equal(211m, creada.IvaTotal);
    }

    [Fact]
    public async Task EditarUnBorradorConUnIvaImpresoInvalidoDa400YConservaElDesgloseAnterior()
    {
        var ctx = await PrepararAsync(nameof(EditarUnBorradorConUnIvaImpresoInvalidoDa400YConservaElDesgloseAnterior));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCRM, [Concepto(ctx, 1000m)], discriminaIva: true));

        var respuesta = await ctx.Admin.PutAsJsonAsync(
            $"/api/compras/{creada.Id}",
            Solicitud(
                ctx, ctx.IdTipoCRM, [Concepto(ctx, 2000m)], discriminaIva: true,
                ivaImpreso: [new IvaImpresoSolicitado(ctx.IdAlicuota21, 500m)]));

        await AssertRechazoAsync(respuesta, "iva_impreso_fuera_de_tolerancia");
        var guardada = Assert.Single(await AlicuotasGuardadasAsync(ctx, creada.Id));
        Assert.Equal(1000m, guardada.Neto);
        Assert.Equal(210m, guardada.Iva);
    }

    [Fact]
    public async Task UnIvaImpresoDistintoDeCeroEnUnaAlicuotaExentaDa400PeroCeroSeAcepta()
    {
        var ctx = await PrepararAsync(nameof(UnIvaImpresoDistintoDeCeroEnUnaAlicuotaExentaDa400PeroCeroSeAcepta));

        var rechazada = await ctx.Admin.PostAsJsonAsync(
            "/api/compras",
            Solicitud(
                ctx, ctx.IdTipoCRM, [Concepto(ctx, 300m, ctx.IdAlicuotaExento)], discriminaIva: true,
                ivaImpreso: [new IvaImpresoSolicitado(ctx.IdAlicuotaExento, 0.5m)]));
        await AssertRechazoAsync(rechazada, "iva_impreso_en_alicuota_sin_iva");

        var aceptada = await CrearBorradorAsync(
            ctx,
            Solicitud(
                ctx, ctx.IdTipoCRM, [Concepto(ctx, 300m, ctx.IdAlicuotaExento)], discriminaIva: true,
                ivaImpreso: [new IvaImpresoSolicitado(ctx.IdAlicuotaExento, 0m)]));
        Assert.Equal(0m, Assert.Single(aceptada.Alicuotas).Iva);
    }

    [Fact]
    public async Task UnIvaImpresoDeUnaAlicuotaQueNoEstaEnLasLineasDa400()
    {
        var ctx = await PrepararAsync(nameof(UnIvaImpresoDeUnaAlicuotaQueNoEstaEnLasLineasDa400));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/compras",
            Solicitud(
                ctx, ctx.IdTipoCRM, [Concepto(ctx, 1000m)], discriminaIva: true,
                ivaImpreso: [new IvaImpresoSolicitado(ctx.IdAlicuota105, 10m)]));

        await AssertRechazoAsync(respuesta, "iva_impreso_alicuota_desconocida");
    }

    [Fact]
    public async Task UnaAlicuotaRepetidaEnElIvaImpresoDa400()
    {
        var ctx = await PrepararAsync(nameof(UnaAlicuotaRepetidaEnElIvaImpresoDa400));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/compras",
            Solicitud(
                ctx, ctx.IdTipoCRM, [Concepto(ctx, 1000m)], discriminaIva: true,
                ivaImpreso: [new IvaImpresoSolicitado(ctx.IdAlicuota21, 210m), new IvaImpresoSolicitado(ctx.IdAlicuota21, 210.5m)]));

        await AssertRechazoAsync(respuesta, "iva_impreso_duplicado");
    }

    [Fact]
    public async Task UnIvaImpresoEnUnComprobanteQueNoDiscriminaDa400EnVezDeDescartarse()
    {
        var ctx = await PrepararAsync(nameof(UnIvaImpresoEnUnComprobanteQueNoDiscriminaDa400EnVezDeDescartarse));
        var impreso = new[] { new IvaImpresoSolicitado(ctx.IdAlicuota21, 210m) };

        var remito = await ctx.Admin.PostAsJsonAsync(
            "/api/compras", Solicitud(ctx, ctx.IdTipoCRM, [Concepto(ctx, 1000m)], ivaImpreso: impreso));
        await AssertRechazoAsync(remito, "iva_impreso_sin_discriminar");

        var facturaB = await ctx.Admin.PostAsJsonAsync(
            "/api/compras", Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx, 1000m)], ivaImpreso: impreso));
        await AssertRechazoAsync(facturaB, "iva_impreso_sin_discriminar");
    }

    // ---- enforcement por tipo -------------------------------------------------------------------------

    [Fact]
    public async Task UnaFacturaAExigeDiscriminarIvaYSinPedidoLoDiscrimina()
    {
        var ctx = await PrepararAsync(nameof(UnaFacturaAExigeDiscriminarIvaYSinPedidoLoDiscrimina));

        var contradictoria = await ctx.Admin.PostAsJsonAsync(
            "/api/compras", Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)], discriminaIva: false));
        await AssertRechazoAsync(contradictoria, "discrimina_iva_incompatible");

        var sinPedido = await CrearBorradorAsync(ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)]));
        Assert.True(sinPedido.DiscriminaIva);
        Assert.Equal(210m, sinPedido.IvaTotal);

        var conforme = await CrearBorradorAsync(ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)], discriminaIva: true));
        Assert.True(conforme.DiscriminaIva);

        await using var db = Db(ctx);
        Assert.Equal(2, await db.ComprobantesCompra.CountAsync());
    }

    [Theory]
    [InlineData("B")]
    [InlineData("C")]
    public async Task UnaFacturaBOCNuncaDiscriminaIvaYPedirloDa400(string letra)
    {
        var ctx = await PrepararAsync($"{nameof(UnaFacturaBOCNuncaDiscriminaIvaYPedirloDa400)}{letra}");
        var idTipo = letra == "B" ? ctx.IdTipoCFB : ctx.IdTipoCFC;

        var contradictoria = await ctx.Admin.PostAsJsonAsync(
            "/api/compras", Solicitud(ctx, idTipo, [Concepto(ctx, 1000m)], discriminaIva: true));
        await AssertRechazoAsync(contradictoria, "discrimina_iva_incompatible");

        var sinPedido = await CrearBorradorAsync(ctx, Solicitud(ctx, idTipo, [Concepto(ctx, 1000m)]));
        Assert.False(sinPedido.DiscriminaIva);
        Assert.Null(sinPedido.IvaTotal);
        Assert.Empty(sinPedido.Alicuotas);

        var conforme = await CrearBorradorAsync(ctx, Solicitud(ctx, idTipo, [Concepto(ctx, 1000m)], discriminaIva: false));
        Assert.False(conforme.DiscriminaIva);
    }

    [Fact]
    public async Task EditarUnBorradorConUnDiscriminaIvaContradictorioDa400YNoTocaLoGuardado()
    {
        var ctx = await PrepararAsync(nameof(EditarUnBorradorConUnDiscriminaIvaContradictorioDa400YNoTocaLoGuardado));
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)]));

        var respuesta = await ctx.Admin.PutAsJsonAsync(
            $"/api/compras/{creada.Id}", Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 5000m)], discriminaIva: false));

        await AssertRechazoAsync(respuesta, "discrimina_iva_incompatible");
        var releida = (await ctx.Admin.GetFromJsonAsync<CompraDetalle>($"/api/compras/{creada.Id}", OpcionesJson))!;
        Assert.Equal(1210m, releida.Total);
        Assert.True(releida.DiscriminaIva);
    }

    // ---- reemplazo del desglose junto con los ítems -----------------------------------------------------

    [Fact]
    public async Task GuardarElBorradorReemplazaElDesgloseEntero()
    {
        var ctx = await PrepararAsync(nameof(GuardarElBorradorReemplazaElDesgloseEntero));
        var creada = await CrearBorradorAsync(
            ctx,
            Solicitud(ctx, ctx.IdTipoCRM, [Concepto(ctx, 1000m), Concepto(ctx, 400m, ctx.IdAlicuota105)], discriminaIva: true));
        Assert.Equal(2, (await AlicuotasGuardadasAsync(ctx, creada.Id)).Count);

        var soloUna = await ActualizarAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdTipoCRM, [Concepto(ctx, 500m)], discriminaIva: true));

        var guardadas = await AlicuotasGuardadasAsync(ctx, creada.Id);
        var fila = Assert.Single(guardadas);
        Assert.Equal(ctx.IdAlicuota21, fila.IdAlicuotaIva);
        Assert.Equal(500m, fila.Neto);
        Assert.Equal(105m, fila.Iva);
        Assert.Equal(105m, soloUna.IvaTotal);

        var sinDiscriminar = await ActualizarAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdTipoCRM, [Concepto(ctx, 500m)], discriminaIva: false));

        Assert.False(sinDiscriminar.DiscriminaIva);
        Assert.Null(sinDiscriminar.IvaTotal);
        Assert.Empty(sinDiscriminar.Alicuotas);
        Assert.Empty(await AlicuotasGuardadasAsync(ctx, creada.Id));
        await using var db = Db(ctx);
        var comprobante = await db.ComprobantesCompra.AsNoTracking().SingleAsync(c => c.Id == creada.Id);
        Assert.False(comprobante.DiscriminaIva);
        Assert.Null(comprobante.IvaTotal);
        Assert.Equal(500m, comprobante.Total);
    }

    [Fact]
    public async Task CambiarElTipoDeUnBorradorDeRemitoAFacturaAActualizaElFlagYElDesglose()
    {
        var ctx = await PrepararAsync(nameof(CambiarElTipoDeUnBorradorDeRemitoAFacturaAActualizaElFlagYElDesglose));
        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, ctx.IdTipoCRM, [Concepto(ctx, 1000m)]));
        Assert.Empty(creada.Alicuotas);

        var actualizada = await ActualizarAsync(ctx, creada.Id, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)]));

        Assert.True(actualizada.DiscriminaIva);
        Assert.Single(actualizada.Alicuotas);
        Assert.Single(await AlicuotasGuardadasAsync(ctx, creada.Id));
    }
}
