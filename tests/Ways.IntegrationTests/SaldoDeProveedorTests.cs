using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Application.Gastos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-8-compras-transferencias-inventario, Slice 4: <c>GET /api/proveedores/{id}/saldo</c>
/// punta a punta (tasks 4.2, 4.3, 4.7, 4.8, 4.11, 4.12) — la matemática derivada, el estado de
/// pago por-compra, el punto de entrada, la autorización (incluida la prueba de regresión de la
/// AND-composition: un Vendedor tiene que poder leer el saldo pese a que
/// <c>/api/proveedores</c> es <c>GestionDeCatalogo</c>), el presupuesto de consultas y la prueba
/// del arqueo byte-intacto (task 4.9). El backstop de esquema del proveedor referenciado (task
/// 4.10) también vive acá — mismo agregado que el resto de esta slice.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class SaldoDeProveedorTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";
    private const string PasswordVendedor = "vendedor-password-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, HttpClient Admin, HttpClient Vendedor, int IdProveedor, int IdArticulo,
        int IdAlicuotaIva21, int IdTipoCFA, int IdMedioEfectivo, int IdCliente, int IdTipoComprobanteTx,
        int IdEmpleadoAdmin);

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

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = "Compras-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();
        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        // MedioPago y Cliente son tenant-scoped (CatalogoSimple/Cliente : EntidadTenant) — bajo
        // TenantActualFijo.Plataforma el filtro de EF no acota por tenant y .FirstAsync() puede
        // devolver una fila de OTRO tenant creado en paralelo por otra prueba de esta misma
        // colección, violando fk_gastos_medio_pago/fk_comprobantes_venta_cliente más adelante.
        // Contexto tenant-scoped propio, mismo criterio que
        // CajaCierreEndpointsTests/GastosEndpointsTests.PrepararAsync.
        await using var dbTenant = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await dbTenant.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
        var idCliente = await dbTenant.Clientes.Select(c => c.Id).FirstAsync();

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
            IdTenant = resultado.IdTenant, CodigoInterno = $"{nombre}-{Guid.NewGuid():N}", Nombre = "Articulo",
            IdArea = area.Id, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        var mailVendedor = $"{nombre.ToLowerInvariant()}-vend@ways.test";
        var altaVendedor = await admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-saldo", mailVendedor, (int)RolConocido.Vendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.Created, altaVendedor.StatusCode);

        var vendedor = fixture.CreateClient();
        var loginVendedor = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, loginVendedor.StatusCode);

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, admin, vendedor, proveedor.Id, articulo.Id, idAlicuotaIva21,
            idTipoCFA, idMedioEfectivo, idCliente, idTipoComprobanteTx, resultado.IdUsuarioAdmin);
    }

    private static SolicitudDeCompra SolicitudSimple(
        Contexto ctx, decimal costoUnitario, string numeroExterno) =>
        new(
            ctx.IdProveedor, ctx.IdTipoCFA, ctx.IdPuntoVenta, numeroExterno, DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(ctx.IdArticulo, "Item de prueba", 1m, null, null, costoUnitario, 0m, ctx.IdAlicuotaIva21, true)]);

    /// <summary>Crea y confirma una compra de <paramref name="total"/> exacto — costo unitario
    /// derivado hacia atrás de la fórmula C-FA (discrimina IVA 21%: <c>total = costo * 1.21</c>),
    /// para que las aserciones de saldo trabajen con números redondos.</summary>
    private static async Task<CompraDetalle> CrearYConfirmarCompraDeTotalAsync(Contexto ctx, decimal total, string numeroExterno)
    {
        var costoUnitario = Math.Round(total / 1.21m, 4);
        var respuestaCrear = await ctx.Admin.PostAsJsonAsync("/api/compras", SolicitudSimple(ctx, costoUnitario, numeroExterno));
        var cuerpoCrear = await respuestaCrear.Content.ReadAsStringAsync();
        Assert.True(respuestaCrear.StatusCode == HttpStatusCode.Created, cuerpoCrear);
        var creada = JsonSerializer.Deserialize<CompraDetalle>(cuerpoCrear, OpcionesJson)!;

        var respuestaConfirmar = await ctx.Admin.PostAsync($"/api/compras/{creada.Id}/confirmar", null);
        var cuerpoConfirmar = await respuestaConfirmar.Content.ReadAsStringAsync();
        Assert.True(respuestaConfirmar.StatusCode == HttpStatusCode.OK, cuerpoConfirmar);
        return JsonSerializer.Deserialize<CompraDetalle>(cuerpoConfirmar, OpcionesJson)!;
    }

    private static async Task<int> AbrirTurnoAsync(Contexto ctx, HttpClient? cliente = null)
    {
        var respuesta = await (cliente ?? ctx.Admin).PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 0m, "Apertura de soporte"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;
    }

    private static async Task<HttpResponseMessage> RegistrarGastoLigadoAsync(
        Contexto ctx, HttpClient cliente, int idComprobanteCompra, decimal importe) =>
        await cliente.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Proveedor, null, null, "Pago de compra", null, ctx.IdMedioEfectivo,
                null, importe, idComprobanteCompra));

    private static async Task<SaldoDeProveedor> ObtenerSaldoAsync(Contexto ctx, int? idProveedor = null)
    {
        var respuesta = await ctx.Admin.GetAsync($"/api/proveedores/{idProveedor ?? ctx.IdProveedor}/saldo");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonSerializer.Deserialize<SaldoDeProveedor>(cuerpo, OpcionesJson)!;
    }

    // ---- task 4.7: saldo derivado, nunca persistido --------------------------------------------

    [Fact]
    public async Task ElSaldoRefleneComprasConfirmadasNetoDeGastos()
    {
        var ctx = await PrepararAsync(nameof(ElSaldoRefleneComprasConfirmadasNetoDeGastos));
        var compra = await CrearYConfirmarCompraDeTotalAsync(ctx, 5000m, "saldo-0001");
        await AbrirTurnoAsync(ctx);
        var gasto = await RegistrarGastoLigadoAsync(ctx, ctx.Admin, compra.Id, 3000m);
        Assert.Equal(HttpStatusCode.Created, gasto.StatusCode);

        var saldo = await ObtenerSaldoAsync(ctx);

        Assert.Equal(2000m, saldo.Saldo);
    }

    [Fact]
    public async Task LosBorradoresYAnuladasQuedanExcluidosDelSaldo()
    {
        var ctx = await PrepararAsync(nameof(LosBorradoresYAnuladasQuedanExcluidosDelSaldo));

        var respuestaBorrador = await ctx.Admin.PostAsJsonAsync("/api/compras", SolicitudSimple(ctx, 826.45m, "saldo-borrador"));
        Assert.Equal(HttpStatusCode.Created, respuestaBorrador.StatusCode);

        await CrearYConfirmarCompraDeTotalAsync(ctx, 2000m, "saldo-confirmada");

        var paraAnular = await CrearYConfirmarCompraDeTotalAsync(ctx, 500m, "saldo-anulada");
        var anulacion = await ctx.Admin.PostAsync($"/api/compras/{paraAnular.Id}/anular", null);
        Assert.Equal(HttpStatusCode.OK, anulacion.StatusCode);

        var saldo = await ObtenerSaldoAsync(ctx);

        // Solo la confirmada de 2000 aporta — ni el borrador de ~1000 ni la anulada de 500.
        Assert.Equal(2000m, saldo.Saldo);
        Assert.Single(saldo.Compras);
        Assert.Equal(2000m, saldo.Compras[0].Total);
    }

    /// <summary>design decisión 6, la regla invertida: "annulling is allowed, the linked gastos
    /// stay linked (the link is history, not a claim of debt), the response reports how many
    /// payments the operator has left dangling, and the derived saldo keeps counting them as
    /// money paid — which they are". Backstop de regresión: si algún día alguien "arregla" esto
    /// para descontar el gasto colgante de la anulación, este test lo tiene que romper.</summary>
    [Fact]
    public async Task UnGastoLigadoAUnaCompraLuegoAnuladaSigueDescontandoDelSaldo()
    {
        var ctx = await PrepararAsync(nameof(UnGastoLigadoAUnaCompraLuegoAnuladaSigueDescontandoDelSaldo));
        var compra = await CrearYConfirmarCompraDeTotalAsync(ctx, 1000m, "saldo-dangling");
        await AbrirTurnoAsync(ctx);
        var gasto = await RegistrarGastoLigadoAsync(ctx, ctx.Admin, compra.Id, 100m);
        Assert.Equal(HttpStatusCode.Created, gasto.StatusCode);

        var respuestaAnulacion = await ctx.Admin.PostAsync($"/api/compras/{compra.Id}/anular", null);
        var cuerpoAnulacion = await respuestaAnulacion.Content.ReadAsStringAsync();
        Assert.True(respuestaAnulacion.StatusCode == HttpStatusCode.OK, cuerpoAnulacion);
        var resultadoAnulacion = JsonSerializer.Deserialize<ResultadoAnulacion>(cuerpoAnulacion, OpcionesJson)!;
        Assert.Equal(1, resultadoAnulacion.GastosLigados);

        var saldo = await ObtenerSaldoAsync(ctx);

        Assert.Equal(-100m, saldo.Saldo);
        Assert.Empty(saldo.Compras);
    }

    [Fact]
    public async Task UnProveedorSinActividadTieneSaldoCero()
    {
        var ctx = await PrepararAsync(nameof(UnProveedorSinActividadTieneSaldoCero));

        var saldo = await ObtenerSaldoAsync(ctx);

        Assert.Equal(0m, saldo.Saldo);
        Assert.Empty(saldo.Compras);
    }

    // ---- task 4.8: estado de pago por-compra, de gastos LIGADOS únicamente ---------------------

    [Fact]
    public async Task UnaCompraTotalmentePagadaQuedaPagada()
    {
        var ctx = await PrepararAsync(nameof(UnaCompraTotalmentePagadaQuedaPagada));
        var compra = await CrearYConfirmarCompraDeTotalAsync(ctx, 1000m, "saldo-pagada");
        await AbrirTurnoAsync(ctx);
        await RegistrarGastoLigadoAsync(ctx, ctx.Admin, compra.Id, 1000m);

        var saldo = await ObtenerSaldoAsync(ctx);

        var linea = Assert.Single(saldo.Compras);
        Assert.Equal(EstadoPago.Pagada, linea.EstadoPago);
        Assert.Equal(1000m, linea.Pagado);
    }

    [Fact]
    public async Task UnGastoSinLigarNoMarcaUnaCompraComoPagadaPeroReduceElSaldoTotal()
    {
        var ctx = await PrepararAsync(nameof(UnGastoSinLigarNoMarcaUnaCompraComoPagadaPeroReduceElSaldoTotal));
        var compra = await CrearYConfirmarCompraDeTotalAsync(ctx, 2000m, "saldo-sin-ligar");
        await AbrirTurnoAsync(ctx);

        // Gasto de categoría proveedor, mismo proveedor, SIN idComprobanteCompra.
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, CategoriaGasto.Proveedor, ctx.IdProveedor, null, "Pago suelto", null,
                ctx.IdMedioEfectivo, null, 500m));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);

        var saldo = await ObtenerSaldoAsync(ctx);

        Assert.Equal(1500m, saldo.Saldo);
        var linea = Assert.Single(saldo.Compras);
        Assert.Equal(compra.Id, linea.IdComprobanteCompra);
        Assert.Equal(0m, linea.Pagado);
        Assert.Equal(EstadoPago.Impaga, linea.EstadoPago);
    }

    [Fact]
    public async Task UnPagoParcialLigadoDaEstadoParcial()
    {
        var ctx = await PrepararAsync(nameof(UnPagoParcialLigadoDaEstadoParcial));
        var compra = await CrearYConfirmarCompraDeTotalAsync(ctx, 1000m, "saldo-parcial");
        await AbrirTurnoAsync(ctx);
        await RegistrarGastoLigadoAsync(ctx, ctx.Admin, compra.Id, 400m);

        var saldo = await ObtenerSaldoAsync(ctx);

        var linea = Assert.Single(saldo.Compras);
        Assert.Equal(EstadoPago.Parcial, linea.EstadoPago);
        Assert.Equal(400m, linea.Pagado);
        Assert.Equal(600m, saldo.Saldo);
    }

    [Fact]
    public async Task VariosGastosLigadosALaMismaCompraSeAcumulanHastaPagada()
    {
        var ctx = await PrepararAsync(nameof(VariosGastosLigadosALaMismaCompraSeAcumulanHastaPagada));
        var compra = await CrearYConfirmarCompraDeTotalAsync(ctx, 1000m, "saldo-multi-gasto");
        await AbrirTurnoAsync(ctx);
        await RegistrarGastoLigadoAsync(ctx, ctx.Admin, compra.Id, 300m);
        await RegistrarGastoLigadoAsync(ctx, ctx.Admin, compra.Id, 200m);

        var saldoParcial = await ObtenerSaldoAsync(ctx);

        var lineaParcial = Assert.Single(saldoParcial.Compras);
        Assert.Equal(500m, lineaParcial.Pagado);
        Assert.Equal(EstadoPago.Parcial, lineaParcial.EstadoPago);
        Assert.Equal(500m, saldoParcial.Saldo);

        await RegistrarGastoLigadoAsync(ctx, ctx.Admin, compra.Id, 500m);

        var saldoPagada = await ObtenerSaldoAsync(ctx);

        var lineaPagada = Assert.Single(saldoPagada.Compras);
        Assert.Equal(1000m, lineaPagada.Pagado);
        Assert.Equal(EstadoPago.Pagada, lineaPagada.EstadoPago);
    }

    // ---- task 4.11: proveedor detail expone el punto de entrada del saldo derivado -------------

    [Fact]
    public async Task LaCompraDelSaldoCoincideConLaQueApareceEnElListadoDeCompras()
    {
        var ctx = await PrepararAsync(nameof(LaCompraDelSaldoCoincideConLaQueApareceEnElListadoDeCompras));
        var compra = await CrearYConfirmarCompraDeTotalAsync(ctx, 1200m, "saldo-entry-point");

        var detalleProveedor = await ctx.Admin.GetAsync($"/api/proveedores/{ctx.IdProveedor}");
        Assert.Equal(HttpStatusCode.OK, detalleProveedor.StatusCode);

        var saldo = await ObtenerSaldoAsync(ctx);
        Assert.Equal(ctx.IdProveedor, saldo.IdProveedor);
        Assert.Contains(saldo.Compras, c => c.IdComprobanteCompra == compra.Id);
    }

    // ---- autorización: la prueba de regresión de la AND-composition (design: API Surface) ------

    /// <summary>El hallazgo del design (finding 4): apilar la ruta de saldo DENTRO del grupo
    /// <c>/api/proveedores</c> (<c>GestionDeCatalogo</c>) compondría con AND y dejaría la
    /// lectura Admin-only. Esta prueba es la que CAPTURARÍA esa regresión: un Vendedor tiene que
    /// poder leer el saldo.</summary>
    [Fact]
    public async Task UnVendedorLeeElSaldoDeUnProveedor()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorLeeElSaldoDeUnProveedor));
        await CrearYConfirmarCompraDeTotalAsync(ctx, 1000m, "saldo-vendedor");

        var respuesta = await ctx.Vendedor.GetAsync($"/api/proveedores/{ctx.IdProveedor}/saldo");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnVendedorEsRechazadoDelAbmDeProveedoresPeroNoDelSaldo()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoDelAbmDeProveedoresPeroNoDelSaldo));

        var listado = await ctx.Vendedor.GetAsync("/api/proveedores");
        Assert.Equal(HttpStatusCode.Forbidden, listado.StatusCode);

        var saldo = await ctx.Vendedor.GetAsync($"/api/proveedores/{ctx.IdProveedor}/saldo");
        Assert.Equal(HttpStatusCode.OK, saldo.StatusCode);
    }

    /// <summary>spec: operacion-de-pos delta / Paying A Compra Keeps The Existing Gastos Gate —
    /// un Vendedor con turno abierto sigue pudiendo pagar una compra, sin ningún tier nuevo.</summary>
    [Fact]
    public async Task UnVendedorPagaUnaCompraBajoElGateInalteradoDeGastos()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorPagaUnaCompraBajoElGateInalteradoDeGastos));
        var compra = await CrearYConfirmarCompraDeTotalAsync(ctx, 1000m, "saldo-pago-vendedor");
        await AbrirTurnoAsync(ctx, ctx.Vendedor);

        var respuesta = await RegistrarGastoLigadoAsync(ctx, ctx.Vendedor, compra.Id, 1000m);

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnProveedorDeOtroTenantDevuelve404()
    {
        var ctxA = await PrepararAsync(nameof(UnProveedorDeOtroTenantDevuelve404) + "-A");
        var ctxB = await PrepararAsync(nameof(UnProveedorDeOtroTenantDevuelve404) + "-B");

        var respuesta = await ctxA.Admin.GetAsync($"/api/proveedores/{ctxB.IdProveedor}/saldo");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- task 4.2/decisión 11: presupuesto de consultas, sin N+1 --------------------------------

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

    private WaysDbContext CrearContextoConContador(int idTenant, ContadorDeComandos contador)
    {
        var tenantActual = new TenantActualFijo(ModoDeAcceso.Tenant, idTenant);

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
                npgsql.MapEnum<Ways.Domain.Stock.MotivoStock>("motivo_stock");
                npgsql.MapEnum<Ways.Domain.CuentaCorriente.TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<EstadoTurno>("estado_turno");
                npgsql.MapEnum<TipoMovimientoCaja>("tipo_movimiento_caja");
                npgsql.MapEnum<TipoMovimientoTesoreria>("tipo_movimiento_tesoreria");
                npgsql.MapEnum<CategoriaGasto>("categoria_gasto");
                npgsql.MapEnum<EstadoCompra>("estado_compra");
            })
            .AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual), contador)
            .Options;

        return new WaysDbContext(opciones, tenantActual);
    }

    private async Task<int> MedirConsultasDelSaldoAsync(Contexto ctx, int cantidadDeCompras)
    {
        for (var i = 0; i < cantidadDeCompras; i++)
        {
            await CrearYConfirmarCompraDeTotalAsync(ctx, 100m, $"presupuesto-{Guid.NewGuid():N}");
        }

        var contador = new ContadorDeComandos();
        await using var db = CrearContextoConContador(ctx.IdTenant, contador);
        var servicio = new ServicioDeSaldoDeProveedor(db);

        await servicio.ObtenerAsync(ctx.IdProveedor);

        return contador.Consultas;
    }

    [Fact]
    public async Task ElSaldoEmiteUnaCantidadConstanteDeConsultasIndependienteDeLaCantidadDeCompras()
    {
        var ctx = await PrepararAsync(nameof(ElSaldoEmiteUnaCantidadConstanteDeConsultasIndependienteDeLaCantidadDeCompras));

        var consultasConPocasCompras = await MedirConsultasDelSaldoAsync(ctx, cantidadDeCompras: 2);
        var consultasConMuchasCompras = await MedirConsultasDelSaldoAsync(ctx, cantidadDeCompras: 10);

        Assert.Equal(consultasConPocasCompras, consultasConMuchasCompras);
    }

    // ---- task 4.9: la prueba del arqueo byte-intacto (spec: arqueo-de-cierre delta) -------------

    private long _numeroSecuencial = 1;

    /// <summary>Siembra directo un comprobante de venta emitido con UN pago — mismo criterio que
    /// <c>CajaCierreEndpointsTests.SembrarPagoAsync</c>: la derivación nunca toca
    /// <c>items_comprobante_venta</c>.</summary>
    private async Task SembrarPagoAsync(Contexto ctx, int idTurno, decimal importe)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = ctx.IdTipoComprobanteTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = ahora,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdTurnoCaja = idTurno,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            IdCliente = ctx.IdCliente,
            Subtotal = importe,
            DescuentoTotal = 0m,
            Total = importe,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        db.PagosComprobante.Add(new PagoComprobante
        {
            IdTenant = ctx.IdTenant,
            IdComprobanteVenta = comprobante.Id,
            IdMedioPago = ctx.IdMedioEfectivo,
            Importe = importe,
            Vuelto = 0m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    /// <summary>spec: arqueo-de-cierre / A Proveedor Gasto Linked To A Compra Introduces No New
    /// Derivation Term — el gasto ligado a una compra confirmada resta su importe exacto del
    /// <c>importe_esperado</c> del medio en el que se pagó, a través del MISMO término
    /// <c>SUM(gastos.importe on that medio)</c> que cualquier otro gasto — <c>CalculadorDeArqueo</c>
    /// no gana ninguna rama nueva (la prueba complementaria de que el archivo queda
    /// byte-idéntico corre por <c>git diff</c> al cierre de esta slice, no acá).</summary>
    [Fact]
    public async Task UnGastoDeProveedorLigadoAUnaCompraReduceElEsperadoAtravesDelTerminoExistente()
    {
        var ctx = await PrepararAsync(nameof(UnGastoDeProveedorLigadoAUnaCompraReduceElEsperadoAtravesDelTerminoExistente));
        var compra = await CrearYConfirmarCompraDeTotalAsync(ctx, 1000m, "saldo-arqueo");
        var idTurno = await AbrirTurnoAsync(ctx);

        await SembrarPagoAsync(ctx, idTurno, importe: 1500m);
        var gasto = await RegistrarGastoLigadoAsync(ctx, ctx.Admin, compra.Id, 400m);
        Assert.Equal(HttpStatusCode.Created, gasto.StatusCode);

        var resumen = await ctx.Admin.GetFromJsonAsync<ResumenDeTurno>($"/api/caja/turnos/{idTurno}/resumen", OpcionesJson);
        Assert.NotNull(resumen);
        var efectivo = resumen!.Medios.Single(m => m.IdMedioPago == ctx.IdMedioEfectivo);

        // 1500 (pago) - 400 (gasto de compra, mismo término que cualquier otro gasto) = 1100.
        Assert.Equal(1100m, efectivo.ImporteEsperado);
    }

    // ---- task 4.10: FK RESTRICT de comprobantes_compra.id_proveedor (db-error-backstops) --------

    /// <summary>spec: proveedores / Proveedor Referenced By A Comprobante Compra Cannot Be
    /// Removed — bypass directo del servicio (<c>ServicioDeProveedores.EliminarAsync</c> es baja
    /// LÓGICA), un DELETE físico por SQL crudo sobre la fila referenciada.</summary>
    [Fact]
    public async Task UnaEliminacionFisicaDeProveedorReferenciadoPorUnaCompraViolaLaFk()
    {
        var ctx = await PrepararAsync(nameof(UnaEliminacionFisicaDeProveedorReferenciadoPorUnaCompraViolaLaFk));
        await CrearYConfirmarCompraDeTotalAsync(ctx, 1000m, "saldo-fk-restrict");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);

        await using var comando = cruda.CreateCommand();
        comando.CommandText = "DELETE FROM proveedores WHERE id_proveedor = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = ctx.IdProveedor });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_comprobantes_compra_proveedor", excepcion.ConstraintName);
    }
}
