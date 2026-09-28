using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Application.Gastos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-gasto-a-compra (PR4), owner requirement: <c>POST /api/gastos/{id}/vincular-compra</c> —
/// vínculo posterior a la creación, para un gasto que ya existe (POS o admin). Cubre los dos
/// modos (proveedor ya coincide → solo imputación, sin proveedor → conversión + Pago recién
/// escrito), el fallback de punto de venta, los rechazos (proveedor que no coincide, ya
/// vinculado, compra borrador/anulada, otro tenant, Vendedor sin GestionDeCatalogo) y la carrera
/// real contra una anulación de la misma compra. También cubre el judgment follow-up de PR3: un
/// <c>idProveedor</c> inexistente en el alta (POS y administración) devuelve 404, nunca el 500 de
/// <c>EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class GastosVincularCompraEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        int IdArticulo, int IdAlicuotaIva21, int IdTipoCFA, int IdMedioEfectivo, string MailAdmin, string PasswordAdmin);

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

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Gasto-a-compra-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();

        // MedioPago es tenant-scoped — bajo TenantActualFijo.Plataforma .FirstAsync() puede
        // devolver la fila de OTRO tenant (mismo criterio que GastosLigadosACompraTests).
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
            articulo.Id, idAlicuotaIva21, idTipoCFA, idMedioEfectivo, mailAdmin, resultado.PasswordTemporal);
    }

    private async Task<HttpClient> CrearVendedorAsync(Contexto ctx, string nombre)
    {
        var mailVendedor = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-gasto-a-compra", mailVendedor, (int)RolConocido.Vendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return vendedor;
    }

    private static SolicitudDeCompra SolicitudSimple(Contexto ctx, int idProveedor, decimal costoUnitario = 100m, string? numeroExterno = "0001-00000001") =>
        new(
            idProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, numeroExterno, DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item de prueba", 10m, null, null, costoUnitario, 0m, ctx.IdAlicuotaIva21, true)]);

    private static async Task<CompraDetalle> CrearYConfirmarCompraAsync(
        Contexto ctx, int? idProveedor = null, string? numeroExterno = "0001-00000001")
    {
        var respuestaCrear = await ctx.Admin.PostAsJsonAsync(
            "/api/compras", SolicitudSimple(ctx, idProveedor ?? ctx.IdProveedor, numeroExterno: numeroExterno));
        var cuerpoCrear = await respuestaCrear.Content.ReadAsStringAsync();
        Assert.True(respuestaCrear.StatusCode == HttpStatusCode.Created, cuerpoCrear);
        var creada = JsonSerializer.Deserialize<CompraDetalle>(cuerpoCrear, OpcionesJson)!;

        var respuestaConfirmar = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        var cuerpoConfirmar = await respuestaConfirmar.Content.ReadAsStringAsync();
        Assert.True(respuestaConfirmar.StatusCode == HttpStatusCode.OK, cuerpoConfirmar);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpoConfirmar, OpcionesJson)!;
    }

    private static async Task<GastoRegistrado> RegistrarGastoAdminAsync(
        Contexto ctx, CategoriaGasto categoria = CategoriaGasto.Otros, int? idProveedor = null,
        int? idPuntoVenta = null, decimal importe = 500m)
    {
        var solicitud = new SolicitudDeGastoDeAdministracion(
            DateOnly.FromDateTime(DateTime.UtcNow), ctx.IdEmpresa, idPuntoVenta, categoria, idProveedor, null,
            "Gasto de prueba", null, ctx.IdMedioEfectivo, null, importe);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/gastos/administracion", solicitud);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;
    }

    private static async Task<int> AbrirTurnoAsync(Contexto ctx)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 0m, "Apertura de soporte"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;
    }

    private async Task<decimal> SaldoDeAsync(int idTenant, int idProveedor)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        return await db.Proveedores.Where(p => p.Id == idProveedor).Select(p => p.Saldo).SingleAsync();
    }

    /// <summary>stage-tesoreria-por-empresa (PR5): segunda empresa + punto de venta del MISMO
    /// tenant, para probar el filtro <c>idEmpresa</c> de <c>GET /api/compras</c> — el picker
    /// "Vincular a compra" de <c>Gastos.tsx</c> nunca debe ofrecer una compra de otra empresa.</summary>
    private async Task<(int IdEmpresa, int IdPuntoVenta)> SembrarOtraEmpresaAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var empresa = new Ways.Domain.Organizacion.Empresa { IdTenant = ctx.IdTenant, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var puntoVenta = new Ways.Domain.Organizacion.PuntoVenta
        {
            IdTenant = ctx.IdTenant, IdEmpresa = empresa.Id, Nombre = $"{nombre} - Local", CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return (empresa.Id, puntoVenta.Id);
    }

    // ---- vínculo feliz: proveedor ya coincide → solo imputación, saldo sin cambios -------------

    [Fact]
    public async Task ElVinculoConProveedorQueYaCoincideSoloImputaElMovimientoSinTocarElSaldo()
    {
        var ctx = await PrepararAsync(nameof(ElVinculoConProveedorQueYaCoincideSoloImputaElMovimientoSinTocarElSaldo));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var gasto = await RegistrarGastoAdminAsync(ctx, CategoriaGasto.Proveedor, ctx.IdProveedor, ctx.IdPuntoVenta, importe: 300m);
        var saldoAntes = await SaldoDeAsync(ctx.IdTenant, ctx.IdProveedor);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/gastos/{gasto.Id}/vincular-compra", new SolicitudDeVincularCompra(compra.Id));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var actualizado = JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;
        Assert.Equal(compra.Id, actualizado.IdComprobanteCompra);
        Assert.Equal(ctx.IdProveedor, actualizado.IdProveedor);

        var saldoDespues = await SaldoDeAsync(ctx.IdTenant, ctx.IdProveedor);
        Assert.Equal(saldoAntes, saldoDespues);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdGasto == gasto.Id && m.Tipo == TipoMovimientoCcProveedor.Pago);
        Assert.Equal(compra.Id, movimiento.IdComprobanteCompra);
        Assert.Equal(1, await db.MovimientosCuentaCorrienteProveedor.CountAsync(m => m.IdGasto == gasto.Id));
    }

    // ---- conversión: gasto Otros sin proveedor → Proveedor + Pago recién escrito ----------------

    [Fact]
    public async Task ElVinculoDeUnGastoOtrosLoConvierteAProveedorYEscribeElPagoReduciendoElSaldo()
    {
        var ctx = await PrepararAsync(nameof(ElVinculoDeUnGastoOtrosLoConvierteAProveedorYEscribeElPagoReduciendoElSaldo));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var gasto = await RegistrarGastoAdminAsync(ctx, CategoriaGasto.Otros, idProveedor: null, idPuntoVenta: ctx.IdPuntoVenta, importe: 400m);
        var saldoAntes = await SaldoDeAsync(ctx.IdTenant, ctx.IdProveedor);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/gastos/{gasto.Id}/vincular-compra", new SolicitudDeVincularCompra(compra.Id));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var actualizado = JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;
        Assert.Equal(CategoriaGasto.Proveedor, actualizado.Categoria);
        Assert.Equal(ctx.IdProveedor, actualizado.IdProveedor);
        Assert.Equal(compra.Id, actualizado.IdComprobanteCompra);

        var saldoDespues = await SaldoDeAsync(ctx.IdTenant, ctx.IdProveedor);
        Assert.Equal(saldoAntes - 400m, saldoDespues);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdGasto == gasto.Id && m.Tipo == TipoMovimientoCcProveedor.Pago);
        Assert.Equal(compra.Id, movimiento.IdComprobanteCompra);
        Assert.Equal(-400m, movimiento.Importe);
    }

    // ---- fallback de punto de venta: gasto admin sin PV usa el de la compra ---------------------

    [Fact]
    public async Task ElVinculoDeUnGastoAdminSinPuntoDeVentaUsaElDeLaCompraParaElMovimientoDeCc()
    {
        var ctx = await PrepararAsync(nameof(ElVinculoDeUnGastoAdminSinPuntoDeVentaUsaElDeLaCompraParaElMovimientoDeCc));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var gasto = await RegistrarGastoAdminAsync(ctx, CategoriaGasto.Otros, idProveedor: null, idPuntoVenta: null, importe: 200m);
        Assert.Null(gasto.IdPuntoVenta);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/gastos/{gasto.Id}/vincular-compra", new SolicitudDeVincularCompra(compra.Id));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdGasto == gasto.Id && m.Tipo == TipoMovimientoCcProveedor.Pago);
        Assert.Equal(ctx.IdPuntoVenta, movimiento.IdPuntoVenta);
    }

    // ---- rechazos --------------------------------------------------------------------------------

    [Fact]
    public async Task UnProveedorQueNoCoincideConLaCompraEsRechazadoConCuatrocientos()
    {
        var ctx = await PrepararAsync(nameof(UnProveedorQueNoCoincideConLaCompraEsRechazadoConCuatrocientos));
        var compra = await CrearYConfirmarCompraAsync(ctx, idProveedor: ctx.IdProveedor);
        var gasto = await RegistrarGastoAdminAsync(ctx, CategoriaGasto.Proveedor, ctx.IdProveedor2, ctx.IdPuntoVenta);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/gastos/{gasto.Id}/vincular-compra", new SolicitudDeVincularCompra(compra.Id));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("proveedor_no_coincide_con_la_compra", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnGastoYaVinculadoEsRechazadoConNueveDeNueve()
    {
        var ctx = await PrepararAsync(nameof(UnGastoYaVinculadoEsRechazadoConNueveDeNueve));
        var compra1 = await CrearYConfirmarCompraAsync(ctx, numeroExterno: "0001-00000001");
        var compra2 = await CrearYConfirmarCompraAsync(ctx, numeroExterno: "0001-00000002");
        var gasto = await RegistrarGastoAdminAsync(ctx, CategoriaGasto.Otros, idProveedor: null, idPuntoVenta: ctx.IdPuntoVenta);

        var primero = await ctx.Admin.PostAsJsonAsync(
            $"/api/gastos/{gasto.Id}/vincular-compra", new SolicitudDeVincularCompra(compra1.Id));
        Assert.Equal(HttpStatusCode.OK, primero.StatusCode);

        var segundo = await ctx.Admin.PostAsJsonAsync(
            $"/api/gastos/{gasto.Id}/vincular-compra", new SolicitudDeVincularCompra(compra2.Id));
        Assert.Equal(HttpStatusCode.Conflict, segundo.StatusCode);
        var problema = await segundo.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gasto_ya_vinculado", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnaCompraEnBorradorEsRechazada()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraEnBorradorEsRechazada));
        var respuestaCrear = await ctx.Admin.PostAsJsonAsync("/api/compras", SolicitudSimple(ctx, ctx.IdProveedor));
        var borrador = JsonSerializer.Deserialize<CompraDetalle>(await respuestaCrear.Content.ReadAsStringAsync(), OpcionesJson)!;
        var gasto = await RegistrarGastoAdminAsync(ctx, CategoriaGasto.Otros, idProveedor: null, idPuntoVenta: ctx.IdPuntoVenta);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/gastos/{gasto.Id}/vincular-compra", new SolicitudDeVincularCompra(borrador.Id));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_no_confirmada", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnaCompraAnuladaEsRechazada()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraAnuladaEsRechazada));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{compra.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);
        var gasto = await RegistrarGastoAdminAsync(ctx, CategoriaGasto.Otros, idProveedor: null, idPuntoVenta: ctx.IdPuntoVenta);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/gastos/{gasto.Id}/vincular-compra", new SolicitudDeVincularCompra(compra.Id));

        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("compra_anulada", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnGastoDeOtroTenantDevuelveNoEncontrado()
    {
        var ctxA = await PrepararAsync(nameof(UnGastoDeOtroTenantDevuelveNoEncontrado) + "-A");
        var ctxB = await PrepararAsync(nameof(UnGastoDeOtroTenantDevuelveNoEncontrado) + "-B");
        var compraDeA = await CrearYConfirmarCompraAsync(ctxA);
        var gastoDeB = await RegistrarGastoAdminAsync(ctxB, CategoriaGasto.Otros, idProveedor: null, idPuntoVenta: ctxB.IdPuntoVenta);

        var respuesta = await ctxA.Admin.PostAsJsonAsync(
            $"/api/gastos/{gastoDeB.Id}/vincular-compra", new SolicitudDeVincularCompra(compraDeA.Id));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorEsRechazadoConForbidden()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoConForbidden));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var gasto = await RegistrarGastoAdminAsync(ctx, CategoriaGasto.Otros, idProveedor: null, idPuntoVenta: ctx.IdPuntoVenta);
        using var vendedor = await CrearVendedorAsync(ctx, nameof(UnVendedorEsRechazadoConForbidden));

        var respuesta = await vendedor.PostAsJsonAsync(
            $"/api/gastos/{gasto.Id}/vincular-compra", new SolicitudDeVincularCompra(compra.Id));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    // ---- judgment follow-up PR3: proveedor inexistente en el alta → 404, nunca 500 --------------

    [Fact]
    public async Task UnProveedorInexistenteEnElAltaAdministrativaDevuelveNoEncontrado()
    {
        var ctx = await PrepararAsync(nameof(UnProveedorInexistenteEnElAltaAdministrativaDevuelveNoEncontrado));

        var solicitud = new SolicitudDeGastoDeAdministracion(
            DateOnly.FromDateTime(DateTime.UtcNow), ctx.IdEmpresa, ctx.IdPuntoVenta, CategoriaGasto.Proveedor,
            999999, null, "Gasto con proveedor inexistente", null, ctx.IdMedioEfectivo, null, 100m);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/gastos/administracion", solicitud);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnProveedorInexistenteEnElAltaDelPosDevuelveNoEncontrado()
    {
        var ctx = await PrepararAsync(nameof(UnProveedorInexistenteEnElAltaDelPosDevuelveNoEncontrado));
        await AbrirTurnoAsync(ctx);

        var solicitud = new SolicitudDeGasto(
            ctx.IdPuntoVenta, CategoriaGasto.Proveedor, 999999, null, "Gasto con proveedor inexistente", null,
            ctx.IdMedioEfectivo, null, 100m);
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/gastos", solicitud);

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- carrera real contra una anulación de la misma compra -------------------------------------

    /// <summary>El vínculo toma la compra <c>FOR SHARE</c> como PRIMER lock — mismo criterio que
    /// <c>ServicioDeGastos.ExigirCompraLigableAsync</c> en el alta: la anulación gana la carrera y
    /// commitea ANTES de que el vínculo retome, que ve <c>anulada</c> ya comiteada y responde
    /// <c>409 compra_anulada</c>, nunca un vínculo corrupto.</summary>
    [Fact]
    public async Task LaAnulacionGanandoLaCarreraDejaAlVinculoRechazadoSinCorromperElGasto()
    {
        var ctx = await PrepararAsync(nameof(LaAnulacionGanandoLaCarreraDejaAlVinculoRechazadoSinCorromperElGasto));
        var compra = await CrearYConfirmarCompraAsync(ctx);
        var gasto = await RegistrarGastoAdminAsync(ctx, CategoriaGasto.Otros, idProveedor: null, idPuntoVenta: ctx.IdPuntoVenta);

        var transaccionIniciada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var puedeContinuar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interceptor = new InterceptorDePausaTrasIniciarLaTransaccion(transaccionIniciada, puedeContinuar);

        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(interceptor))));

        using var clienteVincular = factory.CreateClient();
        var login = await clienteVincular.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tareaVincular = clienteVincular.PostAsJsonAsync(
            $"/api/gastos/{gasto.Id}/vincular-compra", new SolicitudDeVincularCompra(compra.Id));

        await transaccionIniciada.Task;

        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{compra.Id}/anular", null);
        var cuerpoAnulacion = await anulacion.Content.ReadAsStringAsync();
        Assert.True(anulacion.StatusCode == HttpStatusCode.OK, cuerpoAnulacion);

        puedeContinuar.TrySetResult();

        var respuestaVincular = await tareaVincular;
        var cuerpoVincular = await respuestaVincular.Content.ReadAsStringAsync();
        Assert.True(respuestaVincular.StatusCode == HttpStatusCode.Conflict, cuerpoVincular);
        var problema = JsonSerializer.Deserialize<JsonElement>(cuerpoVincular, OpcionesJson);
        Assert.Equal("compra_anulada", problema.GetProperty("codigo").GetString());

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var persistido = await db.Gastos.SingleAsync(g => g.Id == gasto.Id);
        Assert.Null(persistido.IdComprobanteCompra);
        Assert.Equal(CategoriaGasto.Otros, persistido.Categoria);
    }

    // ---- stage-tesoreria-por-empresa (PR5): GET /api/compras?idEmpresa= filtra por la empresa
    // del punto de venta de la compra (join contra PuntosVenta), usado por el picker "Vincular a
    // compra" de Gastos.tsx para no ofrecer compras de otra empresa. ---------------------------

    /// <summary>mutation-proof-tests — la cláusula bajo prueba es el
    /// <c>Where(c => db.PuntosVenta.Any(pv => pv.Id == c.IdPuntoVenta &amp;&amp; pv.IdEmpresa ==
    /// idEmpresa))</c> agregado a <see cref="ServicioDeCompras"/>. Dos compras de la MISMA
    /// empresa+tenant, una en cada punto de venta — filtrar por <c>idEmpresa</c> tiene que EXCLUIR
    /// la compra de la otra empresa (mismo tenant) sin excluir la propia.</summary>
    [Fact]
    public async Task ElFiltroDeEmpresaExcluyeCompasDeOtraEmpresaDelMismoTenant()
    {
        var ctx = await PrepararAsync(nameof(ElFiltroDeEmpresaExcluyeCompasDeOtraEmpresaDelMismoTenant));
        var (idOtraEmpresa, idPuntoVentaDeOtraEmpresa) = await SembrarOtraEmpresaAsync(ctx, "Otra empresa");

        var compraPropia = await CrearYConfirmarCompraAsync(ctx, numeroExterno: "0001-00000001");

        var solicitudOtra = new SolicitudDeCompra(
            ctx.IdProveedor, ctx.IdTipoCFA, idPuntoVentaDeOtraEmpresa, "0002-00000001", DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item de otra empresa", 5m, null, null, 50m, 0m, ctx.IdAlicuotaIva21, true)]);
        var respuestaCrearOtra = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitudOtra);
        Assert.Equal(HttpStatusCode.Created, respuestaCrearOtra.StatusCode);
        var compraDeOtraEmpresa = JsonSerializer.Deserialize<CompraDetalle>(await respuestaCrearOtra.Content.ReadAsStringAsync(), OpcionesJson)!;
        var confirmarOtra = await ctx.Admin.PostAsync($"/api/compras/{compraDeOtraEmpresa.Id}/confirmar", null);
        Assert.Equal(HttpStatusCode.OK, confirmarOtra.StatusCode);

        var sinFiltro = await ctx.Admin.GetFromJsonAsync<PaginaDeCompras>("/api/compras?tamanio=200", OpcionesJson);
        Assert.NotNull(sinFiltro);
        Assert.Contains(sinFiltro!.Items, c => c.Id == compraPropia.Id);
        Assert.Contains(sinFiltro.Items, c => c.Id == compraDeOtraEmpresa.Id);

        var conFiltro = await ctx.Admin.GetFromJsonAsync<PaginaDeCompras>($"/api/compras?idEmpresa={ctx.IdEmpresa}&tamanio=200", OpcionesJson);
        Assert.NotNull(conFiltro);
        Assert.Contains(conFiltro!.Items, c => c.Id == compraPropia.Id);
        Assert.DoesNotContain(conFiltro.Items, c => c.Id == compraDeOtraEmpresa.Id);
    }

    /// <summary>judgment-day PR5, hallazgo confirmado #3 (parity contract): <c>GET
    /// /api/compras/export</c> tiene que aceptar y reenviar <c>idEmpresa</c> EXACTAMENTE igual que
    /// <c>GET /api/compras</c> — <c>ServicioDeCompras.ListarParaExportacionAsync</c> ya lo acepta,
    /// solo faltaba que el endpoint lo reenviara. Mutación aplicada (borrar el parámetro
    /// <c>idEmpresa</c> del lambda de <c>/export</c> en <c>ComprasEndpoints.cs</c>): esta prueba
    /// pasó de FALLAR (2 filas en el export filtrado, la de la otra empresa no se excluía) a pasar
    /// al revertir — evidencia registrada en el cuerpo de esta corrección.</summary>
    [Fact]
    public async Task ElExportDeComprasAceptaYReenviaElFiltroDeEmpresa()
    {
        var ctx = await PrepararAsync(nameof(ElExportDeComprasAceptaYReenviaElFiltroDeEmpresa));
        var (_, idPuntoVentaDeOtraEmpresa) = await SembrarOtraEmpresaAsync(ctx, "Otra empresa");

        await CrearYConfirmarCompraAsync(ctx, numeroExterno: "0001-00000001");

        var solicitudOtra = new SolicitudDeCompra(
            ctx.IdProveedor, ctx.IdTipoCFA, idPuntoVentaDeOtraEmpresa, "0002-00000001", DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item de otra empresa", 5m, null, null, 50m, 0m, ctx.IdAlicuotaIva21, true)]);
        var respuestaCrearOtra = await ctx.Admin.PostAsJsonAsync("/api/compras", solicitudOtra);
        var compraDeOtraEmpresa = JsonSerializer.Deserialize<CompraDetalle>(await respuestaCrearOtra.Content.ReadAsStringAsync(), OpcionesJson)!;
        await ctx.Admin.PostAsync($"/api/compras/{compraDeOtraEmpresa.Id}/confirmar", null);

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var rango = $"desde={hoy:yyyy-MM-dd}T00:00:00-03:00&hasta={hoy:yyyy-MM-dd}T23:59:59-03:00";

        // Mismo layout que ComprasListadoExportTests: encabezado fila 6, datos desde la 7.
        const int primeraFilaDeDatos = 7;

        var exportSinFiltro = await ctx.Admin.GetAsync($"/api/compras/export?{rango}&formato=xlsx");
        Assert.Equal(HttpStatusCode.OK, exportSinFiltro.StatusCode);
        using var libroSinFiltro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await exportSinFiltro.Content.ReadAsByteArrayAsync()));
        var hojaSinFiltro = libroSinFiltro.Worksheets.First();
        Assert.False(hojaSinFiltro.Row(primeraFilaDeDatos).IsEmpty());
        Assert.False(hojaSinFiltro.Row(primeraFilaDeDatos + 1).IsEmpty());
        Assert.True(hojaSinFiltro.Row(primeraFilaDeDatos + 2).IsEmpty());

        var exportConFiltro = await ctx.Admin.GetAsync($"/api/compras/export?idEmpresa={ctx.IdEmpresa}&{rango}&formato=xlsx");
        Assert.Equal(HttpStatusCode.OK, exportConFiltro.StatusCode);
        using var libroConFiltro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await exportConFiltro.Content.ReadAsByteArrayAsync()));
        var hojaConFiltro = libroConFiltro.Worksheets.First();
        Assert.False(hojaConFiltro.Row(primeraFilaDeDatos).IsEmpty());
        Assert.True(hojaConFiltro.Row(primeraFilaDeDatos + 1).IsEmpty());
    }
}
