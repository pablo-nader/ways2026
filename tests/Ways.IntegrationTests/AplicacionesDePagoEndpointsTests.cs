using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Application.Organizacion;
using Ways.Application.Pagos;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// E3-S3: aplicar lo disponible de un pago a compras, desaplicar, la adopción de un pago sin proveedor, la
/// reimplementación de <c>vincular-compra</c> sobre el disponible y las dos lecturas de pagos aplicables.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AplicacionesDePagoEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "una-contraseña-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, HttpClient Admin, int IdProveedor, int IdProveedor2,
        int IdArticulo, int IdAlicuotaIva21, int IdTipoCFA, int IdMedioEfectivo, string Nombre);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Aplicar-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        await using var dbTenant = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await dbTenant.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        var proveedor2 = new Proveedor
        {
            IdTenant = resultado.IdTenant, RazonSocial = $"{nombre}-otro", IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.AddRange(proveedor, proveedor2);
        await db.SaveChangesAsync();

        var articulo = new Articulo
        {
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = "Articulo",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, admin, proveedor.Id, proveedor2.Id,
            articulo.Id, idAlicuotaIva21, idTipoCFA, idMedioEfectivo, nombre);
    }

    private static async Task<CompraDetalle> CrearCompraAsync(
        Contexto ctx, int? idProveedor = null, bool confirmar = true, string numeroExterno = "0001-00000001")
    {
        var solicitud = new SolicitudDeCompra(
            idProveedor ?? ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, numeroExterno, FechaDelNegocio.Hoy(), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item de prueba", 10m, null, null, 100m, 0m, ctx.IdAlicuotaIva21, true)]);
        var respuestaCrear = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitud);
        var cuerpoCrear = await respuestaCrear.Content.ReadAsStringAsync();
        Assert.True(respuestaCrear.StatusCode == HttpStatusCode.Created, cuerpoCrear);
        var creada = JsonSerializer.Deserialize<CompraDetalle>(cuerpoCrear, OpcionesJson)!;
        if (!confirmar)
        {
            return creada;
        }

        var respuestaConfirmar = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        var cuerpoConfirmar = await respuestaConfirmar.Content.ReadAsStringAsync();
        Assert.True(respuestaConfirmar.StatusCode == HttpStatusCode.OK, cuerpoConfirmar);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpoConfirmar, OpcionesJson)!;
    }

    private static async Task<PagoRegistrado> AltaAdminAsync(
        Contexto ctx, decimal importe, int? idComprobanteCompra = null, CategoriaPago categoria = CategoriaPago.Proveedor,
        int? idProveedor = null)
    {
        var solicitud = new SolicitudDePagoDeAdministracion(
            FechaDelNegocio.Hoy(), ctx.IdEmpresa, ctx.IdPuntoVenta, categoria,
            idProveedor ?? (categoria == CategoriaPago.Proveedor ? ctx.IdProveedor : null), null,
            "Pago de prueba", null, ctx.IdMedioEfectivo, null, importe, idComprobanteCompra);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/gastos/administracion", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<PagoRegistrado>(cuerpo, OpcionesJson)!;
    }

    private static Task<HttpResponseMessage> AplicarAsync(
        Contexto ctx, int idPago, params (int IdCompra, decimal Importe)[] aplicaciones) =>
        ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/{idPago}/aplicaciones",
            new SolicitudDeAplicarPago(aplicaciones.Select(a => new AplicacionSolicitada(a.IdCompra, a.Importe)).ToList()));

    private static async Task<PagoDeAdministracionListado> AplicarOkAsync(
        Contexto ctx, int idPago, params (int IdCompra, decimal Importe)[] aplicaciones)
    {
        var respuesta = await AplicarAsync(ctx, idPago, aplicaciones);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<PagoDeAdministracionListado>(cuerpo, OpcionesJson)!;
    }

    private static async Task<string> CodigoDeErrorAsync(HttpResponseMessage respuesta) =>
        JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement.GetProperty("codigo").GetString()!;

    private static async Task<PagoDeAdministracionListado> LeerPagoAsync(Contexto ctx, int idPago) =>
        (await ctx.Admin.GetFromJsonAsync<PagoDeAdministracionListado>($"/api/pagos/administracion/{idPago}", OpcionesJson))!;

    private static async Task<CompraDetalle> LeerCompraAsync(Contexto ctx, int idCompra) =>
        (await ctx.Admin.GetFromJsonAsync<CompraDetalle>($"/api/compras/{idCompra}", OpcionesJson))!;

    private static async Task<decimal> SaldoDelProveedorAsync(Contexto ctx, int idProveedor) =>
        (await ctx.Admin.GetFromJsonAsync<SaldoDeProveedor>($"/api/proveedores/{idProveedor}/saldo", OpcionesJson))!.Saldo;

    private async Task<HttpClient> CrearVendedorAsync(Contexto ctx)
    {
        var mail = $"{ctx.Nombre.ToLowerInvariant()}-vendedor@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-aplicaciones", mail, (int)RolConocido.Vendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mail, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return vendedor;
    }

    // ---- aplicar -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AplicarParcialAgrandaLoPagadoYDejaElRestoDisponible()
    {
        var ctx = await PrepararAsync(nameof(AplicarParcialAgrandaLoPagadoYDejaElRestoDisponible));
        var compra = await CrearCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 400m);

        var resultado = await AplicarOkAsync(ctx, pago.Id, (compra.Id, 150m));

        Assert.Equal(150m, resultado.Aplicado);
        Assert.Equal(250m, resultado.Disponible);
        var aplicacion = Assert.Single(resultado.Aplicaciones!);
        Assert.Equal(compra.Id, aplicacion.IdComprobanteCompra);
        Assert.Equal(150m, (await LeerCompraAsync(ctx, compra.Id)).Pagado);

        // Aplicar de nuevo a la misma compra agranda la única aplicación viva del par.
        var otra = await AplicarOkAsync(ctx, pago.Id, (compra.Id, 50m));
        Assert.Equal(200m, Assert.Single(otra.Aplicaciones!).Importe);
        Assert.Equal(200m, otra.Disponible);
    }

    [Fact]
    public async Task AplicarAVariasComprasConsumeTodoElDisponible()
    {
        var ctx = await PrepararAsync(nameof(AplicarAVariasComprasConsumeTodoElDisponible));
        var primera = await CrearCompraAsync(ctx, numeroExterno: "0001-00000001");
        var segunda = await CrearCompraAsync(ctx, numeroExterno: "0001-00000002");
        var pago = await AltaAdminAsync(ctx, 1000m);

        var resultado = await AplicarOkAsync(ctx, pago.Id, (segunda.Id, 400m), (primera.Id, 600m));

        Assert.Equal(1000m, resultado.Aplicado);
        Assert.Equal(0m, resultado.Disponible);
        Assert.Equal(2, resultado.Aplicaciones!.Count);
        Assert.Equal(600m, (await LeerCompraAsync(ctx, primera.Id)).Pagado);
        Assert.Equal(400m, (await LeerCompraAsync(ctx, segunda.Id)).Pagado);
    }

    [Fact]
    public async Task AplicarMasQueLoDisponibleEsConflicto()
    {
        var ctx = await PrepararAsync(nameof(AplicarMasQueLoDisponibleEsConflicto));
        var compra = await CrearCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 100m);

        var respuesta = await AplicarAsync(ctx, pago.Id, (compra.Id, 100.01m));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("aplicacion_excede_disponible", await CodigoDeErrorAsync(respuesta));
    }

    [Fact]
    public async Task AplicarMasQueElSaldoDeLaCompraEsConflicto()
    {
        var ctx = await PrepararAsync(nameof(AplicarMasQueElSaldoDeLaCompraEsConflicto));
        var compra = await CrearCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, compra.Total + 500m);

        var respuesta = await AplicarAsync(ctx, pago.Id, (compra.Id, compra.Total + 1m));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("aplicacion_excede_saldo", await CodigoDeErrorAsync(respuesta));
    }

    [Fact]
    public async Task AplicarUnPagoDeUnProveedorAUnaCompraDeOtroEsConflicto()
    {
        var ctx = await PrepararAsync(nameof(AplicarUnPagoDeUnProveedorAUnaCompraDeOtroEsConflicto));
        var compraDelOtro = await CrearCompraAsync(ctx, ctx.IdProveedor2);
        var pago = await AltaAdminAsync(ctx, 100m);

        var respuesta = await AplicarAsync(ctx, pago.Id, (compraDelOtro.Id, 50m));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("compra_de_otro_proveedor", await CodigoDeErrorAsync(respuesta));
    }

    [Fact]
    public async Task ComprasDeDistintosProveedoresEnUnMismoPedidoSonInvalidas()
    {
        var ctx = await PrepararAsync(nameof(ComprasDeDistintosProveedoresEnUnMismoPedidoSonInvalidas));
        var propia = await CrearCompraAsync(ctx, numeroExterno: "0001-00000001");
        var ajena = await CrearCompraAsync(ctx, ctx.IdProveedor2, numeroExterno: "0001-00000002");
        var pago = await AltaAdminAsync(ctx, 100m);

        var respuesta = await AplicarAsync(ctx, pago.Id, (propia.Id, 10m), (ajena.Id, 10m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("compras_de_distintos_proveedores", await CodigoDeErrorAsync(respuesta));
    }

    [Fact]
    public async Task UnaCompraEnBorradorOAnuladaNoRecibeAplicaciones()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraEnBorradorOAnuladaNoRecibeAplicaciones));
        var borrador = await CrearCompraAsync(ctx, confirmar: false, numeroExterno: "0001-00000001");
        var anulada = await CrearCompraAsync(ctx, numeroExterno: "0001-00000002");
        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PostAsync($"/api/compras/{anulada.Id}/anular", null)).StatusCode);
        var pago = await AltaAdminAsync(ctx, 100m);

        var deBorrador = await AplicarAsync(ctx, pago.Id, (borrador.Id, 10m));
        var deAnulada = await AplicarAsync(ctx, pago.Id, (anulada.Id, 10m));

        Assert.Equal(HttpStatusCode.Conflict, deBorrador.StatusCode);
        Assert.Equal("compra_no_confirmada", await CodigoDeErrorAsync(deBorrador));
        Assert.Equal(HttpStatusCode.Conflict, deAnulada.StatusCode);
        Assert.Equal("compra_anulada", await CodigoDeErrorAsync(deAnulada));
    }

    [Fact]
    public async Task AplicarUnPagoDeSueldosEsConflicto()
    {
        var ctx = await PrepararAsync(nameof(AplicarUnPagoDeSueldosEsConflicto));
        var compra = await CrearCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 100m, categoria: CategoriaPago.Sueldos);

        var respuesta = await AplicarAsync(ctx, pago.Id, (compra.Id, 10m));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("pago_no_aplicable", await CodigoDeErrorAsync(respuesta));
    }

    [Fact]
    public async Task ElCuerpoInvalidoEsBadRequestPeroUnIdInexistenteEs404Primero()
    {
        var ctx = await PrepararAsync(nameof(ElCuerpoInvalidoEsBadRequestPeroUnIdInexistenteEs404Primero));
        var compra = await CrearCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 100m);

        // 404-first: el pago o la compra inexistentes ganan sobre un cuerpo inválido.
        var pagoInexistente = await ctx.Admin.PostAsJsonAsync(
            "/api/pagos/999999/aplicaciones", new SolicitudDeAplicarPago([]));
        var compraInexistente = await AplicarAsync(ctx, pago.Id, (999999, 0m));
        Assert.Equal(HttpStatusCode.NotFound, pagoInexistente.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, compraInexistente.StatusCode);

        var vacio = await ctx.Admin.PostAsJsonAsync($"/api/pagos/{pago.Id}/aplicaciones", new SolicitudDeAplicarPago([]));
        var enCero = await AplicarAsync(ctx, pago.Id, (compra.Id, 0m));
        var tresDecimales = await AplicarAsync(ctx, pago.Id, (compra.Id, 1.005m));
        var repetida = await AplicarAsync(ctx, pago.Id, (compra.Id, 1m), (compra.Id, 2m));

        Assert.Equal("aplicacion_sin_compras", await CodigoDeErrorAsync(vacio));
        Assert.Equal("aplicacion_importe_invalido", await CodigoDeErrorAsync(enCero));
        Assert.Equal("aplicacion_importe_invalido", await CodigoDeErrorAsync(tresDecimales));
        Assert.Equal("aplicacion_compra_repetida", await CodigoDeErrorAsync(repetida));
        Assert.All([vacio, enCero, tresDecimales, repetida], r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
    }

    [Fact]
    public async Task AplicarDejaAuditoria()
    {
        var ctx = await PrepararAsync(nameof(AplicarDejaAuditoria));
        var compra = await CrearCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 400m);

        await AplicarOkAsync(ctx, pago.Id, (compra.Id, 150m));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(1, await db.Auditoria.CountAsync(a => a.Accion == "pago.aplicacion" && a.IdEntidad == pago.Id));
    }

    // ---- adopción ------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnPagoSinProveedorAdoptaElDeLaCompraYEntraAlLibroPorSuImporteCompleto()
    {
        var ctx = await PrepararAsync(nameof(UnPagoSinProveedorAdoptaElDeLaCompraYEntraAlLibroPorSuImporteCompleto));
        var compra = await CrearCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 500m, categoria: CategoriaPago.Otros);
        var saldoAntes = await SaldoDelProveedorAsync(ctx, ctx.IdProveedor);

        var resultado = await AplicarOkAsync(ctx, pago.Id, (compra.Id, 300m));

        Assert.Equal(CategoriaPago.Proveedor, resultado.Categoria);
        Assert.Equal(ctx.IdProveedor, resultado.IdProveedor);
        Assert.Equal(compra.Id, resultado.IdComprobanteCompra);
        Assert.Equal(200m, resultado.Disponible);

        // El libro recibe el pago por los 500 completos (no solo los 300 aplicados): saldo = Σ ledger.
        Assert.Equal(saldoAntes - 500m, await SaldoDelProveedorAsync(ctx, ctx.IdProveedor));
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdPago == pago.Id && m.Tipo == TipoMovimientoCcProveedor.Pago);
        Assert.Equal(-500m, movimiento.Importe);
        Assert.Equal(300m, (await LeerCompraAsync(ctx, compra.Id)).Pagado);
    }

    [Fact]
    public async Task UnPagoSinProveedorTotalmenteAplicadoPagaLaCompra()
    {
        var ctx = await PrepararAsync(nameof(UnPagoSinProveedorTotalmenteAplicadoPagaLaCompra));
        var compra = await CrearCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, compra.Total, categoria: CategoriaPago.Otros);

        await AplicarOkAsync(ctx, pago.Id, (compra.Id, compra.Total));

        var despues = await LeerCompraAsync(ctx, compra.Id);
        Assert.Equal(0m, despues.SaldoPendiente);
        Assert.Equal(0m, await SaldoDelProveedorAsync(ctx, ctx.IdProveedor));
    }

    // ---- desaplicar ----------------------------------------------------------------------------------------

    [Fact]
    public async Task DesaplicarDevuelveElSaldoALaCompraYDejaElDisponibleEnElPago()
    {
        var ctx = await PrepararAsync(nameof(DesaplicarDevuelveElSaldoALaCompraYDejaElDisponibleEnElPago));
        var compra = await CrearCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 400m);
        var aplicado = await AplicarOkAsync(ctx, pago.Id, (compra.Id, 400m));
        var idAplicacion = Assert.Single(aplicado.Aplicaciones!).IdAplicacion;

        var respuesta = await ctx.Admin.DeleteAsync($"/api/pagos/{pago.Id}/aplicaciones/{idAplicacion}");

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);
        Assert.Equal(0m, (await LeerCompraAsync(ctx, compra.Id)).Pagado);
        var pagoDespues = await LeerPagoAsync(ctx, pago.Id);
        Assert.Equal(400m, pagoDespues.Disponible);
        Assert.Empty(pagoDespues.Aplicaciones!);

        // El proveedor adoptado o propio no cambia: el pago sigue siendo suyo.
        Assert.Equal(ctx.IdProveedor, pagoDespues.IdProveedor);
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Equal(1, await db.Auditoria.CountAsync(a => a.Accion == "pago.desaplicacion" && a.IdEntidad == pago.Id));
    }

    [Fact]
    public async Task DesaplicarNoReaplicaElLugarLiberadoYElOtroPagoSeAplicaConLaRutaExplicita()
    {
        var ctx = await PrepararAsync(nameof(DesaplicarNoReaplicaElLugarLiberadoYElOtroPagoSeAplicaConLaRutaExplicita));
        var compra = await CrearCompraAsync(ctx);
        var primero = await AltaAdminAsync(ctx, compra.Total, compra.Id);
        var segundo = await AltaAdminAsync(ctx, 500m, compra.Id);

        // El segundo quedó vinculado pero sin lugar: su aplicación no existe todavía.
        Assert.Equal(500m, (await LeerPagoAsync(ctx, segundo.Id)).Disponible);
        var idAplicacion = Assert.Single((await LeerPagoAsync(ctx, primero.Id)).Aplicaciones!).IdAplicacion;

        var respuesta = await ctx.Admin.DeleteAsync($"/api/pagos/{primero.Id}/aplicaciones/{idAplicacion}");

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);
        var primeroDespues = await LeerPagoAsync(ctx, primero.Id);
        Assert.Equal(compra.Total, primeroDespues.Disponible);
        Assert.Equal(compra.Id, primeroDespues.IdComprobanteCompra);
        var segundoDespues = await LeerPagoAsync(ctx, segundo.Id);
        Assert.Equal(500m, segundoDespues.Disponible);
        Assert.Empty(segundoDespues.Aplicaciones!);
        Assert.Equal(0m, (await LeerCompraAsync(ctx, compra.Id)).Pagado);

        var aplicado = await AplicarOkAsync(ctx, segundo.Id, (compra.Id, 500m));
        Assert.Equal(0m, aplicado.Disponible);
        Assert.Equal(500m, (await LeerCompraAsync(ctx, compra.Id)).Pagado);
    }

    [Fact]
    public async Task DesaplicarUnPagoAdoptadoSinPuntoDeVentaLiberaDeVerdadYLaAuditoriaCoincideConLaBase()
    {
        var ctx = await PrepararAsync(nameof(DesaplicarUnPagoAdoptadoSinPuntoDeVentaLiberaDeVerdadYLaAuditoriaCoincideConLaBase));
        var compra = await CrearCompraAsync(ctx);
        var solicitud = new SolicitudDePagoDeAdministracion(
            FechaDelNegocio.Hoy(), ctx.IdEmpresa, null, CategoriaPago.Otros, null, null,
            "Pago sin punto de venta", null, ctx.IdMedioEfectivo, null, 500m, null);
        var alta = await ctx.Admin.PostAsJsonAsync("/api/gastos/administracion", solicitud);
        var cuerpoAlta = await alta.Content.ReadAsStringAsync();
        Assert.True(alta.StatusCode == HttpStatusCode.Created, cuerpoAlta);
        var pago = JsonSerializer.Deserialize<PagoRegistrado>(cuerpoAlta, OpcionesJson)!;

        var aplicado = await AplicarOkAsync(ctx, pago.Id, (compra.Id, 300m));
        var idAplicacion = Assert.Single(aplicado.Aplicaciones!).IdAplicacion;

        var respuesta = await ctx.Admin.DeleteAsync($"/api/pagos/{pago.Id}/aplicaciones/{idAplicacion}");

        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);
        var despues = await LeerPagoAsync(ctx, pago.Id);
        Assert.Equal(500m, despues.Disponible);
        Assert.Empty(despues.Aplicaciones!);
        Assert.Equal(compra.Id, despues.IdComprobanteCompra);
        Assert.Equal(0m, (await LeerCompraAsync(ctx, compra.Id)).Pagado);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        Assert.Empty(await db.AplicacionesPago.Where(a => a.IdPago == pago.Id).ToListAsync());
        var auditoria = await db.Auditoria.SingleAsync(a => a.Accion == "pago.desaplicacion" && a.IdEntidad == pago.Id);
        using var valorNuevo = JsonDocument.Parse(auditoria.ValorNuevo);
        Assert.Equal(500m, valorNuevo.RootElement.GetProperty("disponible").GetDecimal());
        Assert.Equal(0, valorNuevo.RootElement.GetProperty("aplicaciones").GetArrayLength());
        Assert.Equal(compra.Id, valorNuevo.RootElement.GetProperty("id_comprobante_compra").GetInt32());
    }

    [Fact]
    public async Task DesaplicarEsNotFoundSiLaAplicacionNoEsDeEsePagoOElPagoNoExiste()
    {
        var ctx = await PrepararAsync(nameof(DesaplicarEsNotFoundSiLaAplicacionNoEsDeEsePagoOElPagoNoExiste));
        var compra = await CrearCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 100m);
        var otro = await AltaAdminAsync(ctx, 100m);
        var idAplicacion = Assert.Single((await AplicarOkAsync(ctx, pago.Id, (compra.Id, 50m))).Aplicaciones!).IdAplicacion;

        var deOtroPago = await ctx.Admin.DeleteAsync($"/api/pagos/{otro.Id}/aplicaciones/{idAplicacion}");
        var pagoInexistente = await ctx.Admin.DeleteAsync($"/api/pagos/999999/aplicaciones/{idAplicacion}");
        var aplicacionInexistente = await ctx.Admin.DeleteAsync($"/api/pagos/{pago.Id}/aplicaciones/999999");

        Assert.Equal(HttpStatusCode.NotFound, deOtroPago.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, pagoInexistente.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, aplicacionInexistente.StatusCode);
        Assert.Equal(50m, (await LeerPagoAsync(ctx, pago.Id)).Aplicado);
    }

    // ---- vincular-compra sobre el disponible --------------------------------------------------------------

    [Fact]
    public async Task VincularConservaSusCodigosYAplicaElMinimoEntreDisponibleYSaldo()
    {
        var ctx = await PrepararAsync(nameof(VincularConservaSusCodigosYAplicaElMinimoEntreDisponibleYSaldo));
        var compraA = await CrearCompraAsync(ctx, numeroExterno: "0001-00000001");
        var compraB = await CrearCompraAsync(ctx, numeroExterno: "0001-00000002");
        var pago = await AltaAdminAsync(ctx, compraA.Total + 300m);

        // Aplica el pago parcialmente a A por la ruta nueva: queda disponible 300 + lo no aplicado.
        await AplicarOkAsync(ctx, pago.Id, (compraA.Id, compraA.Total - 100m));
        var vinculado = await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/{pago.Id}/vincular-compra", new SolicitudDeVincularCompra(compraB.Id));
        Assert.Equal(HttpStatusCode.OK, vinculado.StatusCode);

        // Disponible era 400; el saldo de B es 1210: aplica 400 y no el importe completo del pago.
        var despues = await LeerPagoAsync(ctx, pago.Id);
        Assert.Equal(0m, despues.Disponible);
        Assert.Equal(400m, (await LeerCompraAsync(ctx, compraB.Id)).Pagado);

        var otraVez = await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/{pago.Id}/vincular-compra", new SolicitudDeVincularCompra(compraB.Id));
        Assert.Equal(HttpStatusCode.Conflict, otraVez.StatusCode);
        Assert.Equal("gasto_ya_vinculado", await CodigoDeErrorAsync(otraVez));
    }

    [Fact]
    public async Task VincularUnPagoSinDisponibleEsConflicto()
    {
        var ctx = await PrepararAsync(nameof(VincularUnPagoSinDisponibleEsConflicto));
        var compraA = await CrearCompraAsync(ctx, numeroExterno: "0001-00000001");
        var compraB = await CrearCompraAsync(ctx, numeroExterno: "0001-00000002");
        var pago = await AltaAdminAsync(ctx, 100m);
        await AplicarOkAsync(ctx, pago.Id, (compraA.Id, 100m));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/gastos/{pago.Id}/vincular-compra", new SolicitudDeVincularCompra(compraB.Id));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("pago_sin_disponible", await CodigoDeErrorAsync(respuesta));
    }

    [Fact]
    public async Task VincularUnPagoSinProveedorLoConvierteComoAntes()
    {
        var ctx = await PrepararAsync(nameof(VincularUnPagoSinProveedorLoConvierteComoAntes));
        var compra = await CrearCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 200m, categoria: CategoriaPago.Otros);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/pagos/{pago.Id}/vincular-compra", new SolicitudDeVincularCompra(compra.Id));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var despues = await LeerPagoAsync(ctx, pago.Id);
        Assert.Equal(CategoriaPago.Proveedor, despues.Categoria);
        Assert.Equal(ctx.IdProveedor, despues.IdProveedor);
        Assert.Equal(200m, (await LeerCompraAsync(ctx, compra.Id)).Pagado);
    }

    // ---- lecturas ------------------------------------------------------------------------------------------

    [Fact]
    public async Task LosPagosAplicablesDeUnaCompraIncluyenLosDelProveedorYLosSinProveedor()
    {
        var ctx = await PrepararAsync(nameof(LosPagosAplicablesDeUnaCompraIncluyenLosDelProveedorYLosSinProveedor));
        var compra = await CrearCompraAsync(ctx);
        var delProveedor = await AltaAdminAsync(ctx, 300m);
        var sinProveedor = await AltaAdminAsync(ctx, 200m, categoria: CategoriaPago.Otros);
        var delOtroProveedor = await AltaAdminAsync(ctx, 100m, idProveedor: ctx.IdProveedor2);
        var agotado = await AltaAdminAsync(ctx, 50m);
        await AplicarOkAsync(ctx, agotado.Id, (compra.Id, 50m));
        var parcial = await AltaAdminAsync(ctx, 80m);
        await AplicarOkAsync(ctx, parcial.Id, (compra.Id, 30m));

        var respuesta = await ctx.Admin.GetAsync($"/api/compras/{compra.Id}/pagos-aplicables");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var pagos = JsonSerializer.Deserialize<PagosAplicables>(await respuesta.Content.ReadAsStringAsync(), OpcionesJson)!.Pagos;
        Assert.Equal(
            new[] { delProveedor.Id, sinProveedor.Id, parcial.Id }.Order(), pagos.Select(p => p.IdPago).Order());
        Assert.DoesNotContain(pagos, p => p.IdPago == delOtroProveedor.Id || p.IdPago == agotado.Id);
        Assert.Equal(50m, pagos.Single(p => p.IdPago == parcial.Id).Disponible);
        Assert.Null(pagos.Single(p => p.IdPago == sinProveedor.Id).IdProveedor);
    }

    [Fact]
    public async Task LosPagosDisponiblesDeUnProveedorSonSuCreditoAFavor()
    {
        var ctx = await PrepararAsync(nameof(LosPagosDisponiblesDeUnProveedorSonSuCreditoAFavor));
        var compra = await CrearCompraAsync(ctx);
        var libre = await AltaAdminAsync(ctx, 300m);
        var agotado = await AltaAdminAsync(ctx, 50m);
        await AplicarOkAsync(ctx, agotado.Id, (compra.Id, 50m));
        await AltaAdminAsync(ctx, 200m, categoria: CategoriaPago.Otros);

        var respuesta = await ctx.Admin.GetAsync($"/api/proveedores/{ctx.IdProveedor}/pagos-disponibles");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var pagos = JsonSerializer.Deserialize<PagosAplicables>(await respuesta.Content.ReadAsStringAsync(), OpcionesJson)!.Pagos;
        var unico = Assert.Single(pagos);
        Assert.Equal(libre.Id, unico.IdPago);
        Assert.Equal(300m, unico.Disponible);

        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.GetAsync("/api/proveedores/999999/pagos-disponibles")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.GetAsync("/api/compras/999999/pagos-aplicables")).StatusCode);
    }

    // ---- autorización --------------------------------------------------------------------------------------

    [Fact]
    public async Task UnVendedorEsRechazadoEnLasCuatroRutas()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoEnLasCuatroRutas));
        var compra = await CrearCompraAsync(ctx);
        var pago = await AltaAdminAsync(ctx, 100m);
        var idAplicacion = Assert.Single((await AplicarOkAsync(ctx, pago.Id, (compra.Id, 10m))).Aplicaciones!).IdAplicacion;
        var vendedor = await CrearVendedorAsync(ctx);

        var aplicar = await vendedor.PostAsJsonAsync(
            $"/api/pagos/{pago.Id}/aplicaciones", new SolicitudDeAplicarPago([new AplicacionSolicitada(compra.Id, 5m)]));
        var desaplicar = await vendedor.DeleteAsync($"/api/pagos/{pago.Id}/aplicaciones/{idAplicacion}");
        var deCompra = await vendedor.GetAsync($"/api/compras/{compra.Id}/pagos-aplicables");
        var deProveedor = await vendedor.GetAsync($"/api/proveedores/{ctx.IdProveedor}/pagos-disponibles");

        Assert.Equal(HttpStatusCode.Forbidden, aplicar.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, desaplicar.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deCompra.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deProveedor.StatusCode);
        Assert.Equal(10m, (await LeerPagoAsync(ctx, pago.Id)).Aplicado);
    }
}
