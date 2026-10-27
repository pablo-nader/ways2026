using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Reportes;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Impuestos internos por línea de una compra contra Postgres real: se guardan fuera de la base del IVA, suman al
/// total del comprobante y al costo que la confirmación escribe en el artículo, cierran en el libro IVA y se rechazan
/// donde no corresponden. Las cuentas están en <c>CalculadorDeCompraImpuestosInternosTests</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasImpuestosInternosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

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

    private static LineaDeCompraSolicitada DeArticulo(Contexto ctx, decimal unidades, decimal costo, decimal impuestoInterno = 0m) =>
        new(ctx.IdArticulo, "Artículo", unidades, null, null, costo, 0m, ctx.IdAlicuota21, ImpuestoInterno: impuestoInterno);

    private async Task<decimal?> CostoNominalAsync(Contexto ctx)
    {
        await using var db = Db(ctx);
        return (await db.Articulos.AsNoTracking().FirstAsync(a => a.Id == ctx.IdArticulo)).CostoNominal;
    }

    [Fact]
    public async Task ElBorradorGuardaElImpuestoInternoFueraDeLaBaseDelIvaYLoDevuelveEnLineaYEncabezado()
    {
        var ctx = await PrepararAsync(nameof(ElBorradorGuardaElImpuestoInternoFueraDeLaBaseDelIvaYLoDevuelveEnLineaYEncabezado));

        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [DeArticulo(ctx, unidades: 10m, costo: 100m, impuestoInterno: 150m)]));

        Assert.Equal(150m, creada.ImpuestosInternos);
        Assert.Equal(1000m, creada.Subtotal);
        Assert.Equal(210m, creada.IvaTotal);
        Assert.Equal(1360m, creada.Total);
        var alicuota = Assert.Single(creada.Alicuotas);
        Assert.Equal((1000m, 210m), (alicuota.Neto, alicuota.Iva));
        var linea = Assert.Single(creada.Items);
        Assert.Equal((150m, 1000m), (linea.ImpuestoInterno, linea.Total));

        var releida = await LeerAsync<CompraDetalle>(await ctx.Admin.GetAsync($"/api/compras/{creada.Id}"), HttpStatusCode.OK);
        Assert.Equal(150m, releida.ImpuestosInternos);
        Assert.Equal(1360m, releida.Total);
        Assert.Equal(150m, Assert.Single(releida.Items).ImpuestoInterno);
    }

    [Fact]
    public async Task UnPutReemplazaLosImpuestosInternosYUnaLineaSinElCampoQuedaEnCero()
    {
        var ctx = await PrepararAsync(nameof(UnPutReemplazaLosImpuestosInternosYUnaLineaSinElCampoQuedaEnCero));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [DeArticulo(ctx, unidades: 10m, costo: 100m, impuestoInterno: 150m)]));

        var editada = await ActualizarAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdTipoCFA, [DeArticulo(ctx, unidades: 10m, costo: 100m, impuestoInterno: 80m)]));
        Assert.Equal(80m, editada.ImpuestosInternos);
        Assert.Equal(1290m, editada.Total);
        Assert.Equal(80m, Assert.Single(editada.Items).ImpuestoInterno);

        var sinCampo = await ActualizarAsync(
            ctx, creada.Id, Solicitud(ctx, ctx.IdTipoCFA, [DeArticulo(ctx, unidades: 10m, costo: 100m)]));
        Assert.Equal(0m, sinCampo.ImpuestosInternos);
        Assert.Equal(1210m, sinCampo.Total);
        Assert.Equal(0m, Assert.Single(sinCampo.Items).ImpuestoInterno);
    }

    [Fact]
    public async Task ConfirmarEscribeEnElArticuloUnCostoQueIncluyeElImpuestoInterno()
    {
        var ctx = await PrepararAsync(nameof(ConfirmarEscribeEnElArticuloUnCostoQueIncluyeElImpuestoInterno));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [DeArticulo(ctx, unidades: 10m, costo: 100m, impuestoInterno: 150m)]));

        var confirmada = await ConfirmarAsync(ctx, creada.Id);

        // (1000 neto x 1,21 + 150) / 10 unidades.
        Assert.Equal(136m, await CostoNominalAsync(ctx));
        Assert.Equal(1360m, confirmada.Total);
        await using var db = Db(ctx);
        Assert.Equal(1360m, (await db.Proveedores.AsNoTracking().FirstAsync(p => p.Id == ctx.IdProveedor)).Saldo);
    }

    [Fact]
    public async Task ConPreciosFinalesElImpuestoInternoTambienSumaAlTotalYAlCosto()
    {
        var ctx = await PrepararAsync(nameof(ConPreciosFinalesElImpuestoInternoTambienSumaAlTotalYAlCosto));
        var creada = await CrearBorradorAsync(
            ctx,
            Solicitud(
                ctx, ctx.IdTipoCFA, [DeArticulo(ctx, unidades: 10m, costo: 121m, impuestoInterno: 150m)],
                preciosIncluyenIva: true));
        Assert.Equal(1360m, creada.Total);
        Assert.Equal(210m, creada.IvaTotal);

        await ConfirmarAsync(ctx, creada.Id);

        Assert.Equal(136m, await CostoNominalAsync(ctx));
    }

    [Fact]
    public async Task ElLibroIvaDeComprasMuestraLaColumnaYLaDiferenciaDaCero()
    {
        var ctx = await PrepararAsync(nameof(ElLibroIvaDeComprasMuestraLaColumnaYLaDiferenciaDaCero));
        var creada = await CrearBorradorAsync(
            ctx, Solicitud(ctx, ctx.IdTipoCFA, [DeArticulo(ctx, unidades: 10m, costo: 100m, impuestoInterno: 150m)]));
        await ConfirmarAsync(ctx, creada.Id);

        var hoy = FechaDelNegocio.Hoy();
        var libro = await LeerAsync<LibroIva>(
            await ctx.Admin.GetAsync($"/api/reportes/libro-iva-compras?desde={hoy:yyyy-MM-dd}&hasta={hoy:yyyy-MM-dd}"),
            HttpStatusCode.OK);

        var fila = Assert.Single(libro.Filas);
        Assert.Equal((1360m, 150m, 0m), (fila.Total, fila.ImpuestosInternos, fila.Diferencia));
        Assert.Equal((150m, 0m), (libro.Totales.ImpuestosInternos, libro.Totales.Diferencia));
    }

    [Fact]
    public async Task UnImpuestoInternoEnUnComprobanteQueNoDiscriminaIvaEsUn400()
    {
        var ctx = await PrepararAsync(nameof(UnImpuestoInternoEnUnComprobanteQueNoDiscriminaIvaEsUn400));

        await AssertRechazoAsync(
            await ctx.Admin.PostAsJsonAsync(
                "/api/compras",
                Solicitud(ctx, ctx.IdTipoCFB, [DeArticulo(ctx, unidades: 1m, costo: 100m, impuestoInterno: 10m)])),
            "impuesto_interno_sin_discriminar");
    }

    [Fact]
    public async Task UnImpuestoInternoNegativoEsUn400()
    {
        var ctx = await PrepararAsync(nameof(UnImpuestoInternoNegativoEsUn400));

        await AssertRechazoAsync(
            await ctx.Admin.PostAsJsonAsync(
                "/api/compras",
                Solicitud(ctx, ctx.IdTipoCFA, [DeArticulo(ctx, unidades: 1m, costo: 100m, impuestoInterno: -1m)])),
            "impuesto_interno_invalido");
    }

    [Fact]
    public async Task UnConceptoNoAdmiteImpuestoInterno()
    {
        var ctx = await PrepararAsync(nameof(UnConceptoNoAdmiteImpuestoInterno));
        var concepto = new LineaDeCompraSolicitada(
            null, "Flete", 1m, null, null, 100m, 0m, ctx.IdAlicuota21, ImpuestoInterno: 10m);

        await AssertRechazoAsync(
            await ctx.Admin.PostAsJsonAsync("/api/compras", Solicitud(ctx, ctx.IdTipoCFA, [concepto])),
            "concepto_con_impuesto_interno");
    }
}
