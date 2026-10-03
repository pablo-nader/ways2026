using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Proveedores;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Modo "precio final" (<c>precios_incluyen_iva</c>), percepciones del comprobante de compra y las
/// alícuotas/flags de percepción de empresa y proveedor. Todo a través de la API real.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasPercepcionesYPrecioFinalTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordOperador = "una-contraseña-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdArticulo, int IdCondicionFiscal,
        int IdAlicuota21, int IdAlicuota105, int IdAlicuotaExento,
        int IdTipoCFA, int IdTipoCFB, int IdTipoCRM);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Percepcion-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
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
            CreatedAt = ahora, UpdatedAt = ahora
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
            resultado.IdTenant, resultado.IdPuntoVenta, admin, proveedor.Id, articulo.Id, condicionFiscal.Id,
            idAlicuota21, idAlicuota105, idAlicuotaExento,
            await TipoAsync("C-FA"), await TipoAsync("C-FB"), await TipoAsync("C-RM"));
    }

    private static LineaDeCompraSolicitada Concepto(Contexto ctx, decimal importe, int? idAlicuota = null) =>
        new(null, "Concepto", 1m, null, null, importe, 0m, idAlicuota ?? ctx.IdAlicuota21);

    private static LineaDeCompraSolicitada DeArticulo(Contexto ctx, decimal unidades, decimal costo) =>
        new(ctx.IdArticulo, "Artículo", unidades, null, null, costo, 0m, ctx.IdAlicuota21);

    private static PercepcionSolicitada Iibb(decimal importe = 30m, decimal baseImponible = 1000m, decimal alicuota = 3m) =>
        new(TiposDePercepcion.Iibb, baseImponible, alicuota, importe);

    private static PercepcionSolicitada Iva(decimal importe = 15m) => new(TiposDePercepcion.Iva, 1000m, 1.5m, importe);

    private static SolicitudDeCompra Solicitud(
        Contexto ctx, int idTipo, IReadOnlyList<LineaDeCompraSolicitada> items, bool? discriminaIva = null,
        IReadOnlyList<IvaImpresoSolicitado>? ivaImpreso = null, bool preciosIncluyenIva = false,
        IReadOnlyList<PercepcionSolicitada>? percepciones = null) =>
        new(ctx.IdProveedor, idTipo, ctx.IdPuntoVenta, DatosDePrueba.NumeroExternoUnico(), FechaDelNegocio.Hoy(), null, items,
            null, discriminaIva, ivaImpreso, preciosIncluyenIva, percepciones);

    private static async Task<T> LeerAsync<T>(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, cuerpo);
        return JsonSerializer.Deserialize<T>(cuerpo, OpcionesJson)!;
    }

    private static async Task<CompraDetalle> CrearBorradorAsync(Contexto ctx, SolicitudDeCompra solicitud) =>
        await LeerAsync<CompraDetalle>(await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud), HttpStatusCode.Created);

    private static async Task<CompraDetalle> ActualizarAsync(Contexto ctx, int id, SolicitudDeCompra solicitud) =>
        await LeerAsync<CompraDetalle>(await ctx.Admin.PutAsJsonAsync($"/api/compras/{id}", solicitud), HttpStatusCode.OK);

    private static async Task<CompraDetalle> ConfirmarAsync(Contexto ctx, int id) =>
        await LeerAsync<CompraDetalle>(await ctx.Admin.PostAsync($"/api/compras/{id}/confirmar", null), HttpStatusCode.OK);

    private static async Task AssertRechazoAsync(HttpResponseMessage respuesta, string codigoEsperado)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, cuerpo);
        Assert.Equal(codigoEsperado, JsonDocument.Parse(cuerpo).RootElement.GetProperty("codigo").GetString());
    }

    private WaysDbContext Db(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    private async Task<List<PercepcionComprobanteCompra>> PercepcionesGuardadasAsync(Contexto ctx, int idCompra)
    {
        await using var db = Db(ctx);
        return await db.PercepcionesComprobanteCompra.AsNoTracking()
            .Where(p => p.IdComprobanteCompra == idCompra)
            .OrderBy(p => p.Tipo)
            .ToListAsync();
    }

    // ---- empresa y proveedor ---------------------------------------------------------------------------

    private static AltaProveedor AltaDeProveedor(
        Contexto ctx, string razonSocial, bool percibeIibb = false, bool percibeIva = false, bool preciosIncluyenIva = false) =>
        new(razonSocial, null, null, ctx.IdCondicionFiscal, null, null, null, null, null, null, null, null, null,
            PercibeIibb: percibeIibb, PercibeIva: percibeIva, PreciosIncluyenIva: preciosIncluyenIva);

    [Fact]
    public async Task ElProveedorGuardaYDevuelveSusTresFlagsYPorDefectoSonFalsos()
    {
        var ctx = await PrepararAsync(nameof(ElProveedorGuardaYDevuelveSusTresFlagsYPorDefectoSonFalsos));

        var sinFlags = await LeerAsync<ProveedorListado>(
            await ctx.Admin.PostAsJsonAsync("/api/proveedores", AltaDeProveedor(ctx, "Sin flags")), HttpStatusCode.Created);
        Assert.False(sinFlags.PercibeIibb);
        Assert.False(sinFlags.PercibeIva);
        Assert.False(sinFlags.PreciosIncluyenIva);

        var conFlags = await LeerAsync<ProveedorListado>(
            await ctx.Admin.PostAsJsonAsync(
                "/api/proveedores", AltaDeProveedor(ctx, "Con flags", percibeIibb: true, percibeIva: true, preciosIncluyenIva: true)),
            HttpStatusCode.Created);
        Assert.True(conFlags.PercibeIibb);
        Assert.True(conFlags.PercibeIva);
        Assert.True(conFlags.PreciosIncluyenIva);

        var releido = await LeerAsync<ProveedorListado>(
            await ctx.Admin.GetAsync($"/api/proveedores/{conFlags.Id}"), HttpStatusCode.OK);
        Assert.True(releido.PercibeIibb && releido.PercibeIva && releido.PreciosIncluyenIva);

        var edicion = new EdicionProveedor(
            "Con flags", null, null, ctx.IdCondicionFiscal, null, null, null, null, null, null, null, null, null, null, true,
            PercibeIibb: false, PercibeIva: true, PreciosIncluyenIva: false);
        var editado = await LeerAsync<ProveedorListado>(
            await ctx.Admin.PutAsJsonAsync($"/api/proveedores/{conFlags.Id}", edicion), HttpStatusCode.OK);
        Assert.False(editado.PercibeIibb);
        Assert.True(editado.PercibeIva);
        Assert.False(editado.PreciosIncluyenIva);

        var listado = await LeerAsync<PaginaDe<ProveedorListado>>(
            await ctx.Admin.GetAsync("/api/proveedores?tamanio=200"), HttpStatusCode.OK);
        var delListado = listado.Items.Single(p => p.Id == conFlags.Id);
        Assert.False(delListado.PercibeIibb);
        Assert.True(delListado.PercibeIva);
    }

    private static async Task<EmpresaListado> EmpresaDelAdminAsync(HttpClient admin)
    {
        var empresas = await LeerAsync<List<EmpresaListado>>(await admin.GetAsync("/api/empresas"), HttpStatusCode.OK);
        return Assert.Single(empresas);
    }

    [Fact]
    public async Task LaEmpresaGuardaYDevuelveSusAlicuotasDePercepcionYAdmiteLimpiarlas()
    {
        var ctx = await PrepararAsync(nameof(LaEmpresaGuardaYDevuelveSusAlicuotasDePercepcionYAdmiteLimpiarlas));
        var empresa = await EmpresaDelAdminAsync(ctx.Admin);
        Assert.Null(empresa.AlicuotaPercepcionIibb);
        Assert.Null(empresa.AlicuotaPercepcionIva);

        var edicion = new EmpresaEdicion(empresa.RazonSocial, empresa.NombreFantasia, empresa.Cuit, 3.5m, 1.025m);
        var editada = await LeerAsync<EmpresaListado>(
            await ctx.Admin.PutAsJsonAsync($"/api/empresas/{empresa.Id}", edicion), HttpStatusCode.OK);
        Assert.Equal(3.5m, editada.AlicuotaPercepcionIibb);
        Assert.Equal(1.025m, editada.AlicuotaPercepcionIva);

        var releida = await EmpresaDelAdminAsync(ctx.Admin);
        Assert.Equal(3.5m, releida.AlicuotaPercepcionIibb);
        Assert.Equal(1.025m, releida.AlicuotaPercepcionIva);

        var limpiada = await LeerAsync<EmpresaListado>(
            await ctx.Admin.PutAsJsonAsync(
                $"/api/empresas/{empresa.Id}", new EmpresaEdicion(empresa.RazonSocial, empresa.NombreFantasia, empresa.Cuit)),
            HttpStatusCode.OK);
        Assert.Null(limpiada.AlicuotaPercepcionIibb);
        Assert.Null(limpiada.AlicuotaPercepcionIva);
    }

    [Theory]
    [InlineData(-0.001, null)]
    [InlineData(100.001, null)]
    [InlineData(null, 101.0)]
    [InlineData(2.0005, null)]
    public async Task UnaAlicuotaDePercepcionFueraDeRangoOConDemasiadosDecimalesSeRechaza(double? iibb, double? iva)
    {
        var ctx = await PrepararAsync($"{nameof(UnaAlicuotaDePercepcionFueraDeRangoOConDemasiadosDecimalesSeRechaza)}{iibb}{iva}".Replace(".", "p").Replace("-", "m"));
        var empresa = await EmpresaDelAdminAsync(ctx.Admin);

        var edicion = new EmpresaEdicion(
            empresa.RazonSocial, empresa.NombreFantasia, empresa.Cuit, (decimal?)iibb, (decimal?)iva);
        await AssertRechazoAsync(
            await ctx.Admin.PutAsJsonAsync($"/api/empresas/{empresa.Id}", edicion), "alicuota_percepcion_invalida");
    }

    [Fact]
    public async Task LosExtremosCeroYCienDeLaAlicuotaSonValidos()
    {
        var ctx = await PrepararAsync(nameof(LosExtremosCeroYCienDeLaAlicuotaSonValidos));
        var empresa = await EmpresaDelAdminAsync(ctx.Admin);

        var editada = await LeerAsync<EmpresaListado>(
            await ctx.Admin.PutAsJsonAsync(
                $"/api/empresas/{empresa.Id}",
                new EmpresaEdicion(empresa.RazonSocial, empresa.NombreFantasia, empresa.Cuit, 0m, 100m)),
            HttpStatusCode.OK);

        Assert.Equal(0m, editada.AlicuotaPercepcionIibb);
        Assert.Equal(100m, editada.AlicuotaPercepcionIva);
    }

    // ---- modo precio final ---------------------------------------------------------------------------------

    [Fact]
    public async Task UnComprobanteConPreciosConIvaIncluidoExtraeElIvaDelFinalYGuardaElSnapshot()
    {
        var ctx = await PrepararAsync(nameof(UnComprobanteConPreciosConIvaIncluidoExtraeElIvaDelFinalYGuardaElSnapshot));

        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1210m)], preciosIncluyenIva: true));

        Assert.True(creada.PreciosIncluyenIva);
        Assert.Equal(1210m, creada.Total);
        Assert.Equal(210m, creada.IvaTotal);
        Assert.Equal(new AlicuotaDeCompra(ctx.IdAlicuota21, 21m, 1000m, 210m), Assert.Single(creada.Alicuotas));

        await using var db = Db(ctx);
        Assert.True((await db.ComprobantesCompra.AsNoTracking().SingleAsync(c => c.Id == creada.Id)).PreciosIncluyenIva);
        var releida = await LeerAsync<CompraDetalle>(await ctx.Admin.GetAsync($"/api/compras/{creada.Id}"), HttpStatusCode.OK);
        Assert.True(releida.PreciosIncluyenIva);
    }

    [Fact]
    public async Task SinElFlagElComprobanteSigueEnPreciosNetos()
    {
        var ctx = await PrepararAsync(nameof(SinElFlagElComprobanteSigueEnPreciosNetos));

        var creada = await CrearBorradorAsync(ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)]));

        Assert.False(creada.PreciosIncluyenIva);
        Assert.Equal(1210m, creada.Total);
    }

    [Fact]
    public async Task AlicuotasMezcladasEnPrecioFinalSumanLoTipeadoAunConIvaImpreso()
    {
        var ctx = await PrepararAsync(nameof(AlicuotasMezcladasEnPrecioFinalSumanLoTipeadoAunConIvaImpreso));

        var creada = await CrearBorradorAsync(
            ctx,
            Solicitud(
                ctx, ctx.IdTipoCRM,
                [Concepto(ctx, 121m), Concepto(ctx, 110.5m, ctx.IdAlicuota105), Concepto(ctx, 50m, ctx.IdAlicuotaExento)],
                discriminaIva: true, ivaImpreso: [new IvaImpresoSolicitado(ctx.IdAlicuota21, 21.5m)],
                preciosIncluyenIva: true));

        Assert.Equal(281.5m, creada.Total);
        Assert.Equal(32m, creada.IvaTotal);
        Assert.Equal(new AlicuotaDeCompra(ctx.IdAlicuota21, 21m, 99.5m, 21.5m), creada.Alicuotas.Single(a => a.IdAlicuotaIva == ctx.IdAlicuota21));
        Assert.Equal(new AlicuotaDeCompra(ctx.IdAlicuota105, 10.5m, 100m, 10.5m), creada.Alicuotas.Single(a => a.IdAlicuotaIva == ctx.IdAlicuota105));
        Assert.Equal(new AlicuotaDeCompra(ctx.IdAlicuotaExento, 0m, 50m, 0m), creada.Alicuotas.Single(a => a.IdAlicuotaIva == ctx.IdAlicuotaExento));
        Assert.Equal(creada.Total, creada.Alicuotas.Sum(a => a.Neto!.Value + a.Iva!.Value));
    }

    [Fact]
    public async Task ConfirmarEnPrecioFinalGuardaElCostoComoFinalSobreCantidadYElTotalVaALaCuentaCorriente()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarEnPrecioFinalGuardaElCostoComoFinalSobreCantidadYElTotalVaALaCuentaCorriente));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [DeArticulo(ctx, unidades: 10m, costo: 121m)], preciosIncluyenIva: true));
        Assert.Equal(1210m, creada.Total);

        await ConfirmarAsync(ctx, creada.Id);

        // Con precios netos el mismo 121 tipeado daría 146.41: acá ya trae el IVA.
        await using var db = Db(ctx);
        Assert.Equal(121m, (await db.Articulos.AsNoTracking().FirstAsync(a => a.Id == ctx.IdArticulo)).CostoNominal);
        Assert.Equal(1210m, (await db.Proveedores.AsNoTracking().FirstAsync(p => p.Id == ctx.IdProveedor)).Saldo);
    }

    [Fact]
    public async Task ConfirmarEnPreciosNetosSigueSumandoElIvaAlCostoEfectivo()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarEnPreciosNetosSigueSumandoElIvaAlCostoEfectivo));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [DeArticulo(ctx, unidades: 10m, costo: 121m)]));

        await ConfirmarAsync(ctx, creada.Id);

        await using var db = Db(ctx);
        Assert.Equal(146.41m, (await db.Articulos.AsNoTracking().FirstAsync(a => a.Id == ctx.IdArticulo)).CostoNominal);
    }

    [Fact]
    public async Task UnPutPuedeCambiarElModoYLaConfirmacionUsaElDelUltimoGuardado()
    {
        var ctx = await PrepararAsync(nameof(UnPutPuedeCambiarElModoYLaConfirmacionUsaElDelUltimoGuardado));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [DeArticulo(ctx, unidades: 10m, costo: 121m)], preciosIncluyenIva: true));

        var cambiada = await ActualizarAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdTipoCFA, [DeArticulo(ctx, unidades: 10m, costo: 121m)]));
        Assert.False(cambiada.PreciosIncluyenIva);
        Assert.Equal(1464.1m, cambiada.Total);

        await ConfirmarAsync(ctx, creada.Id);

        await using var db = Db(ctx);
        Assert.Equal(146.41m, (await db.Articulos.AsNoTracking().FirstAsync(a => a.Id == ctx.IdArticulo)).CostoNominal);
    }

    [Fact]
    public async Task LaCoberturaDeLaOrdenCalculaElCostoRealConElModoDePreciosDelComprobante()
    {
        var ctx = await PrepararAsync(nameof(LaCoberturaDeLaOrdenCalculaElCostoRealConElModoDePreciosDelComprobante));
        var orden = await LeerAsync<OrdenDeCompraBorrador>(
            await ctx.Admin.PostAsJsonAsync(
                "/api/ordenes-compra",
                new SolicitudDeOrdenDeCompra(
                    ctx.IdProveedor, ctx.IdPuntoVenta, null, null,
                    [new LineaDeOrdenSolicitada(ctx.IdArticulo, "Item de orden", 10m, 100m)])),
            HttpStatusCode.Created);
        await ctx.Admin.PostAsync($"/api/ordenes-compra/{orden.Id}/enviar", null);

        var solicitud = Solicitud(ctx, ctx.IdTipoCFA, [DeArticulo(ctx, unidades: 4m, costo: 121m)], preciosIncluyenIva: true) with
        {
            IdOrdenCompra = orden.Id
        };
        var recepcion = await CrearBorradorAsync(ctx, solicitud);
        await ConfirmarAsync(ctx, recepcion.Id);

        var detalle = await LeerAsync<OrdenDeCompraDetalle>(
            await ctx.Admin.GetAsync($"/api/ordenes-compra/{orden.Id}"), HttpStatusCode.OK);
        Assert.Equal(121m, Assert.Single(detalle.Cobertura).CostoReal);
    }

    [Fact]
    public async Task PedirPreciosConIvaIncluidoEnUnaFacturaQueNoDiscriminaSeRechazaEnVezDeIgnorarse()
    {
        var ctx = await PrepararAsync(nameof(PedirPreciosConIvaIncluidoEnUnaFacturaQueNoDiscriminaSeRechazaEnVezDeIgnorarse));

        await AssertRechazoAsync(
            await ctx.Admin.PostAsJsonAsync(
                "/api/compras", Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx, 1000m)], preciosIncluyenIva: true)),
            "precios_incluyen_iva_sin_discriminar");
    }

    [Fact]
    public async Task PedirPreciosConIvaIncluidoEnUnRemitoQueNoDiscriminaSeRechazaYSiDiscriminaSeAcepta()
    {
        var ctx = await PrepararAsync(nameof(PedirPreciosConIvaIncluidoEnUnRemitoQueNoDiscriminaSeRechazaYSiDiscriminaSeAcepta));

        await AssertRechazoAsync(
            await ctx.Admin.PostAsJsonAsync(
                "/api/compras", Solicitud(ctx, ctx.IdTipoCRM, [Concepto(ctx, 1000m)], preciosIncluyenIva: true)),
            "precios_incluyen_iva_sin_discriminar");

        var aceptada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCRM, [Concepto(ctx, 1210m)], discriminaIva: true, preciosIncluyenIva: true));
        Assert.True(aceptada.PreciosIncluyenIva);
        Assert.Equal(1210m, aceptada.Total);
    }

    [Fact]
    public async Task PedirPreciosConIvaIncluidoEnUnPutQueDejaDeDiscriminarSeRechazaYNoPisaElBorrador()
    {
        var ctx = await PrepararAsync(nameof(PedirPreciosConIvaIncluidoEnUnPutQueDejaDeDiscriminarSeRechazaYNoPisaElBorrador));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCRM, [Concepto(ctx, 1210m)], discriminaIva: true, preciosIncluyenIva: true));

        await AssertRechazoAsync(
            await ctx.Admin.PutAsJsonAsync(
                $"/api/compras/{creada.Id}",
                Solicitud(ctx, ctx.IdTipoCRM, [Concepto(ctx, 1210m)], discriminaIva: false, preciosIncluyenIva: true)),
            "precios_incluyen_iva_sin_discriminar");

        var releida = await LeerAsync<CompraDetalle>(await ctx.Admin.GetAsync($"/api/compras/{creada.Id}"), HttpStatusCode.OK);
        Assert.True(releida.PreciosIncluyenIva);
        Assert.True(releida.DiscriminaIva);
    }

    // ---- percepciones ------------------------------------------------------------------------------------------

    [Fact]
    public async Task LasPercepcionesSumanAlTotalSeGuardanYSeDevuelvenEnElDetalle()
    {
        var ctx = await PrepararAsync(nameof(LasPercepcionesSumanAlTotalSeGuardanYSeDevuelvenEnElDetalle));

        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)], percepciones: [Iibb(), Iva()]));

        Assert.Equal(1255m, creada.Total);
        Assert.Equal(210m, creada.IvaTotal);
        Assert.Equal(
            [new PercepcionDeCompraDetalle("iibb", 3m, 1000m, 30m), new PercepcionDeCompraDetalle("iva", 1.5m, 1000m, 15m)],
            creada.Percepciones);

        var guardadas = await PercepcionesGuardadasAsync(ctx, creada.Id);
        Assert.Equal(2, guardadas.Count);
        Assert.Equal(45m, guardadas.Sum(p => p.Importe));

        var releida = await LeerAsync<CompraDetalle>(await ctx.Admin.GetAsync($"/api/compras/{creada.Id}"), HttpStatusCode.OK);
        Assert.Equal(creada.Percepciones, releida.Percepciones);
        Assert.Equal(1255m, releida.Total);
    }

    [Fact]
    public async Task ElImporteEsElDeLaFacturaYNoElQueSaldriaDeBaseYAlicuota()
    {
        var ctx = await PrepararAsync(nameof(ElImporteEsElDeLaFacturaYNoElQueSaldriaDeBaseYAlicuota));

        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)], percepciones: [Iibb(importe: 31.07m)]));

        Assert.Equal(1241.07m, creada.Total);
        Assert.Equal(31.07m, Assert.Single(creada.Percepciones).Importe);
    }

    [Fact]
    public async Task UnaFacturaQueNoDiscriminaAdmitePercepcionDeIibbPeroNoDeIva()
    {
        var ctx = await PrepararAsync(nameof(UnaFacturaQueNoDiscriminaAdmitePercepcionDeIibbPeroNoDeIva));

        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx, 1000m)], percepciones: [Iibb()]));
        Assert.Equal(1030m, creada.Total);

        await AssertRechazoAsync(
            await ctx.Admin.PostAsJsonAsync(
                "/api/compras", Solicitud(ctx, ctx.IdTipoCFB, [Concepto(ctx, 1000m)], percepciones: [Iva()])),
            "percepcion_iva_sin_discriminar");
    }

    [Fact]
    public async Task UnComprobanteQueNoRegistraLibroIvaRechazaLasPercepcionesAunSiDiscrimina()
    {
        var ctx = await PrepararAsync(nameof(UnComprobanteQueNoRegistraLibroIvaRechazaLasPercepcionesAunSiDiscrimina));

        await AssertRechazoAsync(
            await ctx.Admin.PostAsJsonAsync(
                "/api/compras",
                Solicitud(ctx, ctx.IdTipoCRM, [Concepto(ctx, 1000m)], discriminaIva: true, percepciones: [Iibb()])),
            "percepciones_sin_libro_iva");
    }

    [Fact]
    public async Task LasPercepcionesInvalidasSeRechazanConSuCodigoYNoGuardanNada()
    {
        var ctx = await PrepararAsync(nameof(LasPercepcionesInvalidasSeRechazanConSuCodigoYNoGuardanNada));

        async Task RechazaAsync(string codigo, params PercepcionSolicitada[] percepciones) =>
            await AssertRechazoAsync(
                await ctx.Admin.PostAsJsonAsync(
                    "/api/compras", Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)], percepciones: percepciones)),
                codigo);

        await RechazaAsync("percepcion_duplicada", Iibb(), Iibb(importe: 5m));
        await RechazaAsync("percepcion_importes_invalidos", Iibb(importe: -1m));
        await RechazaAsync("percepcion_alicuota_invalida", Iibb(alicuota: 100.5m));
        await RechazaAsync("percepcion_tipo_invalido", new PercepcionSolicitada("ganancias", 1000m, 1m, 10m));
        await RechazaAsync("percepcion_decimales_invalidos", Iibb(importe: 30.005m));

        await using var db = Db(ctx);
        Assert.Empty(await db.PercepcionesComprobanteCompra.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UnPutReemplazaElConjuntoDePercepcionesYSinElLasQuita()
    {
        var ctx = await PrepararAsync(nameof(UnPutReemplazaElConjuntoDePercepcionesYSinElLasQuita));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)], percepciones: [Iibb(), Iva()]));

        var soloIibb = await ActualizarAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)], percepciones: [Iibb(importe: 31m)]));
        Assert.Equal(1241m, soloIibb.Total);
        Assert.Equal("iibb", Assert.Single(soloIibb.Percepciones).Tipo);
        Assert.Equal(31m, Assert.Single(await PercepcionesGuardadasAsync(ctx, creada.Id)).Importe);

        var sinPercepciones = await ActualizarAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)]));
        Assert.Equal(1210m, sinPercepciones.Total);
        Assert.Empty(sinPercepciones.Percepciones);
        Assert.Empty(await PercepcionesGuardadasAsync(ctx, creada.Id));
    }

    [Fact]
    public async Task UnPutQueVuelveAUnTipoSinLibroIvaConPercepcionesSeRechazaYConservaElBorrador()
    {
        var ctx = await PrepararAsync(nameof(UnPutQueVuelveAUnTipoSinLibroIvaConPercepcionesSeRechazaYConservaElBorrador));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)], percepciones: [Iibb()]));

        await AssertRechazoAsync(
            await ctx.Admin.PutAsJsonAsync(
                $"/api/compras/{creada.Id}",
                Solicitud(ctx, ctx.IdTipoCRM, [Concepto(ctx, 1000m)], discriminaIva: true, percepciones: [Iibb()])),
            "percepciones_sin_libro_iva");

        Assert.Single(await PercepcionesGuardadasAsync(ctx, creada.Id));
    }

    [Fact]
    public async Task ConfirmarUnaCompraConPercepcionesDejaElTotalConPercepcionesEnLaCuentaCorrienteDelProveedor()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarUnaCompraConPercepcionesDejaElTotalConPercepcionesEnLaCuentaCorrienteDelProveedor));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)], percepciones: [Iibb(), Iva()]));

        var confirmada = await ConfirmarAsync(ctx, creada.Id);

        Assert.Equal(1255m, confirmada.Total);
        Assert.Equal(2, confirmada.Percepciones.Count);
        await using var db = Db(ctx);
        var movimiento = await db.MovimientosCuentaCorrienteProveedor.AsNoTracking()
            .SingleAsync(m => m.IdComprobanteCompra == creada.Id);
        Assert.Equal(TipoMovimientoCcProveedor.Compra, movimiento.Tipo);
        Assert.Equal(1255m, movimiento.Importe);
        Assert.Equal(1255m, (await db.Proveedores.AsNoTracking().FirstAsync(p => p.Id == ctx.IdProveedor)).Saldo);
    }

    [Fact]
    public async Task EnPrecioFinalLasPercepcionesSumanAlFinalSinContarElIvaDosVeces()
    {
        var ctx = await PrepararAsync(nameof(EnPrecioFinalLasPercepcionesSumanAlFinalSinContarElIvaDosVeces));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1210m)], preciosIncluyenIva: true, percepciones: [Iibb(), Iva()]));

        Assert.Equal(1255m, creada.Total);
        Assert.Equal(210m, creada.IvaTotal);

        var confirmada = await ConfirmarAsync(ctx, creada.Id);
        Assert.Equal(1255m, confirmada.Total);
        await using var db = Db(ctx);
        Assert.Equal(1255m, (await db.Proveedores.AsNoTracking().FirstAsync(p => p.Id == ctx.IdProveedor)).Saldo);
    }

    [Fact]
    public async Task AnularUnaCompraConPercepcionesRevierteElTotalCompletoDeLaCuentaCorriente()
    {
        var ctx = await PrepararAsync(nameof(AnularUnaCompraConPercepcionesRevierteElTotalCompletoDeLaCuentaCorriente));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)], percepciones: [Iibb()]));
        await ConfirmarAsync(ctx, creada.Id);

        var respuesta = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        await using var db = Db(ctx);
        Assert.Equal(0m, (await db.Proveedores.AsNoTracking().FirstAsync(p => p.Id == ctx.IdProveedor)).Saldo);
    }

    // ---- vendedor ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ElVendedorNoVeBaseNiImporteDeLasPercepcionesPeroConservaTipoAlicuotaYTotal()
    {
        var ctx = await PrepararAsync(nameof(ElVendedorNoVeBaseNiImporteDeLasPercepcionesPeroConservaTipoAlicuotaYTotal));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [Concepto(ctx, 1000m)], percepciones: [Iibb(), Iva()]));
        await ConfirmarAsync(ctx, creada.Id);

        var mail = $"vendedor-{Guid.NewGuid():N}@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario($"vend-{Guid.NewGuid():N}"[..16], mail, (int)RolConocido.Vendedor, PasswordOperador));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        using var vendedor = fixture.CreateClient();
        Assert.Equal(
            HttpStatusCode.OK,
            (await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordOperador))).StatusCode);

        var vista = await LeerAsync<CompraDetalle>(await vendedor.GetAsync($"/api/compras/{creada.Id}"), HttpStatusCode.OK);

        Assert.Equal(1255m, vista.Total);
        Assert.Equal(
            [new PercepcionDeCompraDetalle("iibb", 3m, null, null), new PercepcionDeCompraDetalle("iva", 1.5m, null, null)],
            vista.Percepciones);
        Assert.False(vista.PreciosIncluyenIva);

        var vistaDelAdmin = await LeerAsync<CompraDetalle>(await ctx.Admin.GetAsync($"/api/compras/{creada.Id}"), HttpStatusCode.OK);
        Assert.Equal(30m, vistaDelAdmin.Percepciones[0].Importe);
        Assert.Equal(1000m, vistaDelAdmin.Percepciones[0].BaseImponible);
    }
}
