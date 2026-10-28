using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Application.Pagos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Pagos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-gastos-admin-retroactivos (PR3), owner's use case 2: <c>POST/GET
/// /api/gastos/administracion</c> punta a punta — alta administrativa SIN turno, pagada de la
/// tesorería de la empresa, con fecha de negocio potencialmente retroactiva. Mismo patrón de
/// fixture/siembra que <c>PagosOrigenFondosTesoreriaTests</c>/
/// <c>CuentaCorrienteProveedorPagoPorPagoTests</c>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PagosDeAdministracionEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordVendedor = "una-contraseña-larga";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdMedioEfectivo, int IdMedioCuentaCorriente,
        HttpClient Admin, string MailAdmin, string PasswordAdmin);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));
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

        await using var db = fixture.CrearContextoDeAplicacion(
            new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();

        // La plantilla de aprovisionamiento (PlantillaDeAprovisionamiento.V1) NO incluye ningún
        // medio de cuenta corriente — se siembra acá, mismo criterio que
        // CajaCierreEndpointsTests.SembrarMedioAsync.
        var ahoraMedio = DateTimeOffset.UtcNow;
        var medioCuentaCorriente = new MedioPago
        {
            Nombre = "Cuenta corriente", Orden = 99, Comportamiento = ComportamientoMedioPago.CuentaCorriente,
            AdmiteVuelto = false, RequiereReferencia = false, Activo = true, CreatedAt = ahoraMedio, UpdatedAt = ahoraMedio
        };
        db.MediosPago.Add(medioCuentaCorriente);
        await db.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, idMedioEfectivo,
            medioCuentaCorriente.Id, admin, mailAdmin, resultado.PasswordTemporal);
    }

    private async Task<HttpClient> CrearVendedorAsync(Contexto ctx, string nombre)
    {
        var mailVendedor = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-pagos-admin", mailVendedor, (int)RolConocido.Vendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);

        var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return vendedor;
    }

    private async Task<(int IdEmpresa, int IdPuntoVenta)> SembrarOtraEmpresaAsync(int idTenant, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, idTenant));
        var ahora = DateTimeOffset.UtcNow;

        var empresa = new Empresa { IdTenant = idTenant, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = idTenant, IdEmpresa = empresa.Id, Nombre = $"{nombre} - Local", CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        return (empresa.Id, puntoVenta.Id);
    }

    private static SolicitudDePagoDeAdministracion Solicitud(
        Contexto ctx,
        DateOnly fecha,
        decimal importe = 100m,
        CategoriaPago categoria = CategoriaPago.Otros,
        int? idProveedor = null,
        int? idPuntoVenta = null,
        int? idMedioPago = null,
        int? idComprobanteCompra = null,
        string concepto = "Gasto administrativo") =>
        new(
            fecha, ctx.IdEmpresa, idPuntoVenta, categoria, idProveedor, null, concepto, null,
            idMedioPago ?? ctx.IdMedioEfectivo, null, importe, idComprobanteCompra);

    private static DateOnly Hoy() => FechaDelNegocio.Hoy();

    // ---- happy path: sin turno, origen tesorería, fecha retroactiva persistida ------------------

    [Fact]
    public async Task UnPagoAdministrativoPersisteSinTurnoConOrigenTesoreriaYLaFechaRetroactivaElegida()
    {
        var ctx = await PrepararAsync(nameof(UnPagoAdministrativoPersisteSinTurnoConOrigenTesoreriaYLaFechaRetroactivaElegida));
        var fechaRetroactiva = Hoy().AddDays(-10);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion", Solicitud(ctx, fechaRetroactiva, importe: 500m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        var pago = JsonSerializer.Deserialize<PagoRegistrado>(cuerpo, OpcionesJson)!;
        Assert.Null(pago.IdTurnoCaja);
        Assert.Equal(OrigenFondosPago.Tesoreria, pago.OrigenFondos);
        Assert.Equal(500m, pago.Importe);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var fila = await db.Pagos.SingleAsync(g => g.Id == pago.Id);
        Assert.Equal(ctx.IdEmpresa, fila.IdEmpresa);
        Assert.Null(fila.IdTurnoCaja);
        // La fecha de negocio elegida (retroactiva) es exactamente la que persiste en `Fecha` —
        // convertida a UTC desde la medianoche local de esa fecha (zona por defecto del tenant).
        Assert.Equal(fechaRetroactiva, DateOnly.FromDateTime(fila.Fecha.UtcDateTime));

        // E2-S1: escritura doble, exactamente un ítem con el medio y el importe de la cabecera.
        var item = await db.ItemsPago.SingleAsync(i => i.IdPago == pago.Id);
        Assert.Equal((ctx.IdMedioEfectivo, 500m), (item.IdMedioPago, item.Importe));
    }

    // ---- el movimiento de tesorería que escribe el pago lleva `momento`, no la fecha retroactiva --

    [Fact]
    public async Task UnPagoAdministrativoRetroactivoEscribeSuMovimientoDeTesoreriaConLaFechaRealDelAltaYEncadenaDespuesDeLoQueYaExiste()
    {
        var ctx = await PrepararAsync(nameof(UnPagoAdministrativoRetroactivoEscribeSuMovimientoDeTesoreriaConLaFechaRealDelAltaYEncadenaDespuesDeLoQueYaExiste));

        // Un primer movimiento de tesorería YA existente en la cadena de la empresa (pago de HOY).
        var primero = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion", Solicitud(ctx, Hoy(), importe: 100m));
        Assert.True(primero.StatusCode == HttpStatusCode.Created, await primero.Content.ReadAsStringAsync());

        var antesDelAlta = DateTimeOffset.UtcNow;
        var fechaRetroactiva = Hoy().AddDays(-30);
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion", Solicitud(ctx, fechaRetroactiva, importe: 60m));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var pago = JsonSerializer.Deserialize<PagoRegistrado>(cuerpo, OpcionesJson)!;
        var despuesDelAlta = DateTimeOffset.UtcNow;

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await db.MovimientosTesoreria.SingleAsync(m => m.IdPago == pago.Id);

        Assert.Equal(TipoMovimientoTesoreria.Pago, movimiento.Tipo);
        Assert.Null(movimiento.IdTurnoCaja);
        Assert.Equal(-100m, movimiento.Inicio); // encadena DESPUÉS del primer pago, nunca desde 0.
        Assert.Equal(-160m, movimiento.Final);
        // La aserción discriminante de la decisión de diseño: el movimiento de tesorería lleva el
        // instante REAL del alta (momento), nunca la fecha de negocio retroactiva elegida.
        Assert.InRange(movimiento.Fecha, antesDelAlta, despuesDelAlta);
        Assert.NotEqual(fechaRetroactiva, DateOnly.FromDateTime(movimiento.Fecha.UtcDateTime));
    }

    // ---- fecha futura rechazada (mutation-proof-tests: ExigirFechaNoFutura) -----------------------

    /// <summary>Cláusula bajo prueba: <c>ServicioDePagos.ExigirFechaNoFutura</c>. Mutación
    /// verificada (mutation-proof-tests): comentando el <c>throw</c> del guard, este test pasa a
    /// recibir 201 en vez del 400 esperado; restaurado el guard, vuelve a verde.</summary>
    [Fact]
    public async Task UnPagoAdministrativoConFechaFuturaSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(UnPagoAdministrativoConFechaFuturaSeRechaza));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion", Solicitud(ctx, Hoy().AddDays(1)));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gasto_fecha_futura", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnPagoAdministrativoConFechaDeHoyEsAceptado()
    {
        var ctx = await PrepararAsync(nameof(UnPagoAdministrativoConFechaDeHoyEsAceptado));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion", Solicitud(ctx, Hoy()));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
    }

    // ---- importe / concepto (mismos guards que RegistrarAsync, compartidos) -----------------------

    [Fact]
    public async Task UnPagoAdministrativoConImporteCeroSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(UnPagoAdministrativoConImporteCeroSeRechaza));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion", Solicitud(ctx, Hoy(), importe: 0m));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gasto_importe_invalido", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnPagoAdministrativoConConceptoEnBlancoSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(UnPagoAdministrativoConConceptoEnBlancoSeRechaza));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion", Solicitud(ctx, Hoy(), concepto: "   "));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gasto_concepto_requerido", problema.GetProperty("codigo").GetString());
    }

    // ---- medio de pago cuenta corriente rechazado ---------------------------------------------------

    [Fact]
    public async Task UnPagoAdministrativoConMedioDePagoDeCuentaCorrienteSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(UnPagoAdministrativoConMedioDePagoDeCuentaCorrienteSeRechaza));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion", Solicitud(ctx, Hoy(), idMedioPago: ctx.IdMedioCuentaCorriente));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gasto_medio_pago_cuenta_corriente_invalido", problema.GetProperty("codigo").GetString());
    }

    // ---- empresa/punto de venta ------------------------------------------------------------------

    [Fact]
    public async Task UnPagoAdministrativoContraUnaEmpresaDeOtroTenantDevuelve404()
    {
        var ctx = await PrepararAsync(nameof(UnPagoAdministrativoContraUnaEmpresaDeOtroTenantDevuelve404));
        var otro = await PrepararAsync(nameof(UnPagoAdministrativoContraUnaEmpresaDeOtroTenantDevuelve404) + "-otro");

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion",
            new SolicitudDePagoDeAdministracion(
                Hoy(), otro.IdEmpresa, null, CategoriaPago.Otros, null, null, "Empresa ajena", null,
                ctx.IdMedioEfectivo, null, 100m));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    [Fact]
    public async Task UnPagoAdministrativoConPuntoDeVentaDeOtraEmpresaDelMismoTenantSeRechazaCon400()
    {
        var ctx = await PrepararAsync(nameof(UnPagoAdministrativoConPuntoDeVentaDeOtraEmpresaDelMismoTenantSeRechazaCon400));
        var (_, idPuntoVentaDeOtraEmpresa) = await SembrarOtraEmpresaAsync(ctx.IdTenant, "Sucursal ajena");

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion", Solicitud(ctx, Hoy(), idPuntoVenta: idPuntoVentaDeOtraEmpresa));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("punto_venta_no_pertenece_a_la_empresa", problema.GetProperty("codigo").GetString());
    }

    [Fact]
    public async Task UnPagoAdministrativoConPuntoDeVentaInexistenteDevuelve404()
    {
        var ctx = await PrepararAsync(nameof(UnPagoAdministrativoConPuntoDeVentaInexistenteDevuelve404));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion", Solicitud(ctx, Hoy(), idPuntoVenta: 999999));

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
    }

    // ---- proveedor: pago a cuenta corriente escrito --------------------------------------------------

    [Fact]
    public async Task UnPagoAdministrativoDeCategoriaProveedorEscribeElPagoEnLaCuentaCorriente()
    {
        var ctx = await PrepararAsync(nameof(UnPagoAdministrativoDeCategoriaProveedorEscribeElPagoEnLaCuentaCorriente));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var condicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var proveedor = new Proveedor
        {
            IdTenant = ctx.IdTenant, RazonSocial = "Proveedor de prueba", IdCondicionFiscal = condicionFiscal,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion",
            Solicitud(
                ctx, Hoy().AddDays(-5), importe: 250m, categoria: CategoriaPago.Proveedor, idProveedor: proveedor.Id,
                idPuntoVenta: ctx.IdPuntoVenta));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var pago = JsonSerializer.Deserialize<PagoRegistrado>(cuerpo, OpcionesJson)!;

        await using var verificacion = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var movimiento = await verificacion.MovimientosCuentaCorrienteProveedor.SingleAsync(m => m.IdPago == pago.Id);
        Assert.Equal(TipoMovimientoCcProveedor.Pago, movimiento.Tipo);
        Assert.Equal(-250m, movimiento.Importe);

        var proveedorActualizado = await verificacion.Proveedores.SingleAsync(p => p.Id == proveedor.Id);
        Assert.Equal(-250m, proveedorActualizado.Saldo);
    }

    /// <summary>Cláusula bajo prueba: <c>ServicioDePagos.ExigirPuntoVentaParaElPagoAProveedor</c> —
    /// descubierta al escribir esta suite: <c>EscriturasDeCuentaCorrienteProveedor.
    /// ValidarFormaPorTipo</c> exige <c>id_punto_venta</c> no nulo para cualquier movimiento de CC
    /// que no sea Apertura, y un pago de categoría proveedor siempre escribe un Pago. Mutación
    /// verificada (mutation-proof-tests): comentando el guard, este test pasa de 400 a un 500 sin
    /// controlar (la excepción de invariante de <c>ValidarFormaPorTipo</c> escapa como error
    /// interno) — restaurado el guard, vuelve a 400 limpio.</summary>
    [Fact]
    public async Task UnPagoAdministrativoDeCategoriaProveedorSinPuntoDeVentaSeRechazaCon400()
    {
        var ctx = await PrepararAsync(nameof(UnPagoAdministrativoDeCategoriaProveedorSinPuntoDeVentaSeRechazaCon400));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var condicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var proveedor = new Proveedor
        {
            IdTenant = ctx.IdTenant, RazonSocial = "Proveedor sin PV", IdCondicionFiscal = condicionFiscal,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion",
            Solicitud(ctx, Hoy(), categoria: CategoriaPago.Proveedor, idProveedor: proveedor.Id, idPuntoVenta: null));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gasto_de_proveedor_requiere_punto_de_venta", problema.GetProperty("codigo").GetString());
    }

    // ---- policy: solo GestionDeCatalogo (admin) -----------------------------------------------------

    [Fact]
    public async Task UnVendedorEsRechazadoConForbiddenDelEndpointAdministrativo()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEsRechazadoConForbiddenDelEndpointAdministrativo));
        using var vendedor = await CrearVendedorAsync(ctx, nameof(UnVendedorEsRechazadoConForbiddenDelEndpointAdministrativo));

        var respuesta = await vendedor.PostAsJsonAsync(
            "/api/gastos/administracion", Solicitud(ctx, Hoy()));

        Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
    }

    [Fact]
    public async Task SinTokenElEndpointAdministrativoDevuelve401()
    {
        using var cliente = fixture.CreateClient();

        var respuesta = await cliente.GetAsync("/api/gastos/administracion");

        Assert.Equal(HttpStatusCode.Unauthorized, respuesta.StatusCode);
    }

    // ---- el pago administrativo no afecta el arqueo de un turno abierto del mismo punto de venta --

    [Fact]
    public async Task UnPagoAdministrativoNoAfectaElArqueoDeUnTurnoAbiertoEnElMismoPuntoDeVenta()
    {
        var ctx = await PrepararAsync(nameof(UnPagoAdministrativoNoAfectaElArqueoDeUnTurnoAbiertoEnElMismoPuntoDeVenta));

        var apertura = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 500m, "Apertura de soporte"));
        var cuerpoApertura = await apertura.Content.ReadAsStringAsync();
        Assert.True(apertura.StatusCode == HttpStatusCode.Created, cuerpoApertura);
        var turno = JsonSerializer.Deserialize<TurnoResumen>(cuerpoApertura, OpcionesJson)!;

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion", Solicitud(ctx, Hoy(), importe: 300m, idPuntoVenta: ctx.IdPuntoVenta));
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, await respuesta.Content.ReadAsStringAsync());

        var resumen = await ctx.Admin.GetFromJsonAsync<ResumenDeTurno>(
            $"/api/caja/turnos/{turno.Id}/resumen", OpcionesJson);
        Assert.NotNull(resumen);

        // El fondo inicial (500) es el único componente de "esperado" para efectivo — el pago
        // administrativo, sin turno, no puede pesar en ningún cálculo de arqueo.
        var efectivo = resumen!.Medios.SingleOrDefault(m => m.IdMedioPago == ctx.IdMedioEfectivo);
        if (efectivo is not null)
        {
            Assert.Equal(500m, efectivo.ImporteEsperado);
        }
    }

    // ---- listado de administración: filtros, incluidos origen y PV nulo ----------------------------

    [Fact]
    public async Task ElListadoDeAdministracionFiltraPorEmpresaOrigenYProveedorEIncluyeFilasSinPuntoDeVenta()
    {
        var ctx = await PrepararAsync(nameof(ElListadoDeAdministracionFiltraPorEmpresaOrigenYProveedorEIncluyeFilasSinPuntoDeVenta));

        var sinPv = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion", Solicitud(ctx, Hoy(), importe: 111m, idPuntoVenta: null));
        Assert.True(sinPv.StatusCode == HttpStatusCode.Created, await sinPv.Content.ReadAsStringAsync());

        var conPv = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion", Solicitud(ctx, Hoy(), importe: 222m, idPuntoVenta: ctx.IdPuntoVenta));
        Assert.True(conPv.StatusCode == HttpStatusCode.Created, await conPv.Content.ReadAsStringAsync());

        var pagina = await ctx.Admin.GetFromJsonAsync<PaginaDePagosDeAdministracion>(
            $"/api/gastos/administracion?idEmpresa={ctx.IdEmpresa}&origenFondos=Tesoreria", OpcionesJson);
        Assert.NotNull(pagina);
        Assert.Equal(2, pagina!.Total);
        Assert.Contains(pagina.Items, i => i.Importe == 111m && i.IdPuntoVenta == null);
        Assert.Contains(pagina.Items, i => i.Importe == 222m && i.IdPuntoVenta == ctx.IdPuntoVenta);
        Assert.All(pagina.Items, i => Assert.Equal(OrigenFondosPago.Tesoreria, i.OrigenFondos));
        Assert.All(pagina.Items, i => Assert.Null(i.IdTurnoCaja));
    }

    // ---- dangling-fk-read-models: proveedor dado de baja lógica sigue apareciendo en el listado ----

    [Fact]
    public async Task ElListadoDeAdministracionSobreviveAUnProveedorDadoDeBajaLogicaSinPerderLaFila()
    {
        var ctx = await PrepararAsync(nameof(ElListadoDeAdministracionSobreviveAUnProveedorDadoDeBajaLogicaSinPerderLaFila));

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var condicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var proveedor = new Proveedor
        {
            IdTenant = ctx.IdTenant, RazonSocial = "Proveedor a dar de baja", IdCondicionFiscal = condicionFiscal,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync();

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion",
            Solicitud(
                ctx, Hoy(), importe: 400m, categoria: CategoriaPago.Proveedor, idProveedor: proveedor.Id,
                idPuntoVenta: ctx.IdPuntoVenta));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        var pago = JsonSerializer.Deserialize<PagoRegistrado>(cuerpo, OpcionesJson)!;

        // Baja lógica directa sobre la fila existente (regla 5 de dangling-fk-read-models: nunca
        // un id apócrifo — el FK del pago sigue apuntando a esta fila, que ahora es invisible). El
        // filtro global "BajaLogica" (WaysDbContext.AplicarFiltroDeBajaLogica) compara
        // DeletedAt == null — NO el flag Activo, que es un concepto de negocio aparte.
        await using (var baja = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            await baja.Proveedores.IgnoreQueryFilters()
                .Where(p => p.Id == proveedor.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, DateTimeOffset.UtcNow));
        }

        var pagina = await ctx.Admin.GetFromJsonAsync<PaginaDePagosDeAdministracion>(
            $"/api/gastos/administracion?idEmpresa={ctx.IdEmpresa}", OpcionesJson);
        Assert.NotNull(pagina);

        var fila = Assert.Single(pagina!.Items, i => i.Id == pago.Id);
        Assert.Equal(proveedor.Id, fila.IdProveedor);
        // regla 1: el catálogo dado de baja es invisible — el nombre resuelto sale null, la fila
        // NUNCA desaparece de items ni descuadra total (total ya incluye esta fila arriba).
        Assert.Null(fila.NombreProveedor);

        // regla 2: un filtro `idProveedor` contra un id ya invisible no matchea nada.
        var filtrado = await ctx.Admin.GetFromJsonAsync<PaginaDePagosDeAdministracion>(
            $"/api/gastos/administracion?idProveedor={proveedor.Id}", OpcionesJson);
        Assert.Equal(0, filtrado!.Total);
    }

    // ---- ligadura a compra (mismas reglas que el POS) -----------------------------------------------

    [Fact]
    public async Task UnPagoAdministrativoLigadoAUnaCompraQueNoEsDeProveedorSeRechaza()
    {
        var ctx = await PrepararAsync(nameof(UnPagoAdministrativoLigadoAUnaCompraQueNoEsDeProveedorSeRechaza));

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion",
            Solicitud(ctx, Hoy(), categoria: CategoriaPago.Otros, idComprobanteCompra: 1));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await respuesta.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gasto_de_compra_debe_ser_de_proveedor", problema.GetProperty("codigo").GetString());
    }
}
