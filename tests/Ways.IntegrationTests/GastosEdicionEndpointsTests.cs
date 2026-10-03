using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
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
/// Edición y baja de gastos: <c>PUT /api/gastos/{id}</c> (POS, turno abierto),
/// <c>PUT /api/gastos/administracion/{id}</c> y <c>DELETE /api/gastos/administracion/{id}</c>
/// (admin, cualquier gasto), con sus ajustes de cuenta corriente de proveedor y de tesorería, el
/// recálculo del arqueo de un turno cerrado y la auditoría.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class GastosEdicionEndpointsTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdAdmin, HttpClient Admin, int IdProveedor,
        int IdProveedor2, int IdArea, int IdMedioEfectivo, int IdMedioElectronico, string MailAdmin,
        string PasswordAdmin);

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

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
        var idMedioElectronico = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico).Select(m => m.Id).FirstAsync();

        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var area = new Area { IdTenant = resultado.IdTenant, Nombre = $"{nombre}-area", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
        dbPlataforma.Areas.Add(area);

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        dbPlataforma.CondicionesFiscales.Add(condicionFiscal);
        await dbPlataforma.SaveChangesAsync();

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
        dbPlataforma.Proveedores.AddRange(proveedor, proveedor2);
        await dbPlataforma.SaveChangesAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, admin,
            proveedor.Id, proveedor2.Id, area.Id, idMedioEfectivo, idMedioElectronico, mailAdmin,
            resultado.PasswordTemporal);
    }

    private async Task<(HttpClient Cliente, int IdUsuario)> CrearVendedorAsync(Contexto ctx, string nombre)
    {
        var mailVendedor = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/usuarios", new CrearUsuario("vendedor-edicion", mailVendedor, (int)RolConocido.Vendedor, PasswordVendedor));
        var cuerpo = await alta.Content.ReadAsStringAsync();
        Assert.True(alta.StatusCode == HttpStatusCode.Created, cuerpo);
        var idUsuario = JsonDocument.Parse(cuerpo).RootElement.GetProperty("id").GetInt32();

        var vendedor = fixture.CreateClient();
        var login = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (vendedor, idUsuario);
    }

    private async Task<int> SembrarMedioAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var medio = new MedioPago
        {
            IdTenant = ctx.IdTenant, Nombre = nombre, Orden = 50, Comportamiento = ComportamientoMedioPago.Electronico,
            AdmiteVuelto = false, RequiereReferencia = false, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medio);
        await db.SaveChangesAsync();
        return medio.Id;
    }

    private static async Task<int> AbrirTurnoAsync(Contexto ctx, decimal fondoInicial)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, fondoInicial, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;
    }

    private static async Task CerrarTurnoAsync(HttpClient cliente, int idTurno, params ConteoDeclarado[] conteos)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurno}/cierre", new SolicitudDeCierre(conteos, null));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
    }

    private static async Task<GastoRegistrado> RegistrarGastoPosAsync(
        HttpClient cliente, Contexto ctx, decimal importe, int idMedioPago,
        CategoriaGasto categoria = CategoriaGasto.Otros, int? idProveedor = null,
        OrigenFondosGasto origen = OrigenFondosGasto.CajaTurno)
    {
        var respuesta = await cliente.PostAsJsonAsync(
            "/api/gastos",
            new SolicitudDeGasto(
                ctx.IdPuntoVenta, categoria, idProveedor, null, "Gasto original", null, idMedioPago, null, importe,
                OrigenFondos: origen));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;
    }

    private static async Task<GastoRegistrado> RegistrarGastoAdminAsync(
        Contexto ctx, decimal importe, CategoriaGasto categoria = CategoriaGasto.Otros, int? idProveedor = null,
        int? idPuntoVenta = null, int? idComprobanteCompra = null)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/gastos/administracion",
            new SolicitudDeGastoDeAdministracion(
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), ctx.IdEmpresa, idPuntoVenta, categoria, idProveedor,
                null, "Gasto admin", null, ctx.IdMedioEfectivo, null, importe, idComprobanteCompra));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;
    }

    private static SolicitudDeEdicionDeGasto Edicion(
        decimal importe, int idMedioPago, CategoriaGasto categoria = CategoriaGasto.Otros, int? idProveedor = null,
        int? idArea = null, string concepto = "Concepto editado", string? detalle = null, string? numeroFactura = null) =>
        new(categoria, idProveedor, idArea, concepto, detalle, idMedioPago, numeroFactura, importe);

    private static async Task<HttpResponseMessage> EditarPosAsync(HttpClient cliente, int idGasto, SolicitudDeEdicionDeGasto solicitud) =>
        await cliente.PutAsJsonAsync($"/api/gastos/{idGasto}", solicitud);

    private static async Task<HttpResponseMessage> EditarAdminAsync(HttpClient cliente, int idGasto, SolicitudDeEdicionDeGasto solicitud) =>
        await cliente.PutAsJsonAsync($"/api/gastos/administracion/{idGasto}", solicitud);

    private static async Task<string> CodigoDeErrorAsync(HttpResponseMessage respuesta) =>
        JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync()).RootElement.GetProperty("codigo").GetString()!;

    private WaysDbContext Db(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    // ---- POS ---------------------------------------------------------------------------------

    /// <summary>Prueba positiva por rol de la ruta del allowlist de
    /// <c>SuperficieDeAutorizacionTests</c>: un Vendedor edita el gasto de su turno abierto, la
    /// fila guarda cada campo editado, el pago al proveedor se ajusta por la diferencia sobre una
    /// deuda previa y la auditoría registra al Vendedor como actor.</summary>
    [Fact]
    public async Task UnVendedorEditaUnGastoDeSuTurnoAbierto()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorEditaUnGastoDeSuTurnoAbierto));
        var (vendedor, idVendedor) = await CrearVendedorAsync(ctx, nameof(UnVendedorEditaUnGastoDeSuTurnoAbierto));
        var idTurno = await AbrirTurnoAsync(ctx, 1000m);

        await RegistrarGastoPosAsync(vendedor, ctx, 1000m, ctx.IdMedioEfectivo, CategoriaGasto.Proveedor, ctx.IdProveedor);
        var gasto = await RegistrarGastoPosAsync(vendedor, ctx, 300m, ctx.IdMedioEfectivo, CategoriaGasto.Proveedor, ctx.IdProveedor);

        var respuesta = await EditarPosAsync(vendedor, gasto.Id, Edicion(
            120m, ctx.IdMedioElectronico, CategoriaGasto.Proveedor, ctx.IdProveedor, ctx.IdArea, "Flete corregido",
            "Detalle nuevo", "0001-00000077"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var editado = JsonSerializer.Deserialize<GastoRegistrado>(cuerpo, OpcionesJson)!;
        Assert.Equal(120m, editado.Importe);
        Assert.Equal(idTurno, editado.IdTurnoCaja);

        await using var db = Db(ctx);
        var fila = await db.Gastos.SingleAsync(g => g.Id == gasto.Id);
        Assert.Equal(CategoriaGasto.Proveedor, fila.Categoria);
        Assert.Equal(ctx.IdProveedor, fila.IdProveedor);
        Assert.Equal(ctx.IdArea, fila.IdArea);
        Assert.Equal("Flete corregido", fila.Concepto);
        Assert.Equal("Detalle nuevo", fila.Detalle);
        Assert.Equal(ctx.IdMedioElectronico, fila.IdMedioPago);
        Assert.Equal("0001-00000077", fila.NumeroFactura);
        Assert.Equal(120m, fila.Importe);
        Assert.Equal(OrigenFondosGasto.CajaTurno, fila.OrigenFondos);
        Assert.Equal(idTurno, fila.IdTurnoCaja);

        Assert.Equal(-1120m, await db.Proveedores.Where(p => p.Id == ctx.IdProveedor).Select(p => p.Saldo).SingleAsync());
        var ajuste = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdProveedor == ctx.IdProveedor && m.Tipo == TipoMovimientoCcProveedor.Ajuste);
        Assert.Equal(180m, ajuste.Importe);
        Assert.Equal(-1120m, ajuste.SaldoResultante);
        Assert.Equal($"Edición del gasto #{gasto.Id}", ajuste.Detalle);
        Assert.Null(ajuste.IdGasto);
        Assert.Equal(ctx.IdPuntoVenta, ajuste.IdPuntoVenta);
        Assert.Equal(idVendedor, ajuste.IdEmpleado);

        Assert.Equal(0, await db.MovimientosTesoreria.CountAsync(m => m.Tipo == TipoMovimientoTesoreria.Ajuste));

        var auditoria = await db.Auditoria.SingleAsync(a => a.Accion == "gasto.edicion" && a.IdEntidad == gasto.Id);
        Assert.Equal("gasto", auditoria.Entidad);
        Assert.Equal(idVendedor, auditoria.IdActor);
        var anterior = JsonDocument.Parse(auditoria.ValorAnterior!).RootElement;
        var nuevo = JsonDocument.Parse(auditoria.ValorNuevo).RootElement;
        Assert.Equal(300m, anterior.GetProperty("importe").GetDecimal());
        Assert.Equal("Gasto original", anterior.GetProperty("concepto").GetString());
        Assert.Equal(120m, nuevo.GetProperty("importe").GetDecimal());
        Assert.Equal("Flete corregido", nuevo.GetProperty("concepto").GetString());
        Assert.Equal(ctx.IdMedioElectronico, nuevo.GetProperty("id_medio_pago").GetInt32());
    }

    [Fact]
    public async Task EditarUnGastoDeTesoreriaEncadenaAjustesDeEgresoYDeIngreso()
    {
        var ctx = await PrepararAsync(nameof(EditarUnGastoDeTesoreriaEncadenaAjustesDeEgresoYDeIngreso));
        await AbrirTurnoAsync(ctx, 0m);
        var gasto = await RegistrarGastoPosAsync(
            ctx.Admin, ctx, 200m, ctx.IdMedioEfectivo, origen: OrigenFondosGasto.Tesoreria);

        Assert.Equal(HttpStatusCode.OK, (await EditarPosAsync(ctx.Admin, gasto.Id, Edicion(260m, ctx.IdMedioEfectivo))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EditarPosAsync(ctx.Admin, gasto.Id, Edicion(150m, ctx.IdMedioEfectivo))).StatusCode);

        await using var db = Db(ctx);
        var cadena = await db.MovimientosTesoreria
            .Where(m => m.IdEmpresa == ctx.IdEmpresa).OrderBy(m => m.Id).ToListAsync();

        Assert.Equal(3, cadena.Count);
        Assert.Equal((TipoMovimientoTesoreria.Gasto, 0m, 0m, 200m, -200m), Fila(cadena[0]));
        Assert.Equal((TipoMovimientoTesoreria.Ajuste, -200m, 0m, 60m, -260m), Fila(cadena[1]));
        Assert.Equal((TipoMovimientoTesoreria.Ajuste, -260m, 110m, 0m, -150m), Fila(cadena[2]));
        Assert.All(cadena.Skip(1), m => Assert.Equal($"Edición del gasto #{gasto.Id}", m.Concepto));
        Assert.All(cadena.Skip(1), m => Assert.Null(m.IdGasto));

        static (TipoMovimientoTesoreria, decimal, decimal, decimal, decimal) Fila(MovimientoTesoreria m) =>
            (m.Tipo, m.Inicio, m.Ingreso, m.Egreso, m.Final);
    }

    /// <summary>LA CLÁUSULA: el <c>409 gasto_turno_cerrado</c> del POS se decide con el estado
    /// leído BAJO el lock del turno — no hay otro chequeo de estado antes de la transacción, así
    /// que este test secuencial lo alcanza directo.</summary>
    [Fact]
    public async Task ElPosNoEditaUnGastoDeUnTurnoCerradoNiUnoSinTurno()
    {
        var ctx = await PrepararAsync(nameof(ElPosNoEditaUnGastoDeUnTurnoCerradoNiUnoSinTurno));
        var idTurno = await AbrirTurnoAsync(ctx, 500m);
        var gasto = await RegistrarGastoPosAsync(ctx.Admin, ctx, 100m, ctx.IdMedioEfectivo);
        await CerrarTurnoAsync(ctx.Admin, idTurno, new ConteoDeclarado(ctx.IdMedioEfectivo, 400m));
        var gastoAdmin = await RegistrarGastoAdminAsync(ctx, 70m);

        var respuesta = await EditarPosAsync(ctx.Admin, gasto.Id, Edicion(10m, ctx.IdMedioEfectivo));
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal("gasto_turno_cerrado", await CodigoDeErrorAsync(respuesta));

        var sinTurno = await EditarPosAsync(ctx.Admin, gastoAdmin.Id, Edicion(10m, ctx.IdMedioEfectivo));
        Assert.Equal(HttpStatusCode.Conflict, sinTurno.StatusCode);
        Assert.Equal("gasto_turno_cerrado", await CodigoDeErrorAsync(sinTurno));

        await using var db = Db(ctx);
        Assert.Equal(100m, await db.Gastos.Where(g => g.Id == gasto.Id).Select(g => g.Importe).SingleAsync());
        var arqueo = await db.ArqueosTurno.SingleAsync(a => a.IdTurnoCaja == idTurno);
        Assert.Equal(400m, arqueo.ImporteEsperado);
        Assert.Null(arqueo.ImporteEsperadoOriginal);
        Assert.Null(await db.TurnosCaja.Where(t => t.Id == idTurno).Select(t => t.FechaRecalculo).SingleAsync());
        Assert.Equal(0, await db.Auditoria.CountAsync(a => a.Accion == "gasto.edicion"));
    }

    [Fact]
    public async Task UnGastoInexistenteDeOtroTenantODadoDeBajaEs404()
    {
        var ctx = await PrepararAsync(nameof(UnGastoInexistenteDeOtroTenantODadoDeBajaEs404));
        var otro = await PrepararAsync(nameof(UnGastoInexistenteDeOtroTenantODadoDeBajaEs404) + "B");
        await AbrirTurnoAsync(otro, 0m);
        var ajeno = await RegistrarGastoPosAsync(otro.Admin, otro, 50m, otro.IdMedioEfectivo);

        Assert.Equal(HttpStatusCode.NotFound, (await EditarPosAsync(ctx.Admin, ajeno.Id, Edicion(10m, ctx.IdMedioEfectivo))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EditarAdminAsync(ctx.Admin, ajeno.Id, Edicion(10m, ctx.IdMedioEfectivo))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.DeleteAsync($"/api/gastos/administracion/{ajeno.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EditarPosAsync(ctx.Admin, int.MaxValue, Edicion(10m, ctx.IdMedioEfectivo))).StatusCode);

        await using var db = Db(otro);
        Assert.Equal(50m, await db.Gastos.Where(g => g.Id == ajeno.Id).Select(g => g.Importe).SingleAsync());
    }

    [Fact]
    public async Task LaEdicionReusaLasValidacionesDelAlta()
    {
        var ctx = await PrepararAsync(nameof(LaEdicionReusaLasValidacionesDelAlta));
        await AbrirTurnoAsync(ctx, 0m);
        var gasto = await RegistrarGastoPosAsync(ctx.Admin, ctx, 100m, ctx.IdMedioEfectivo);
        var idCuentaCorriente = await MedioDeCuentaCorrienteAsync(ctx);

        Assert.Equal("gasto_importe_invalido", await CodigoDeErrorAsync(await EditarPosAsync(ctx.Admin, gasto.Id, Edicion(0m, ctx.IdMedioEfectivo))));
        Assert.Equal("gasto_concepto_requerido", await CodigoDeErrorAsync(await EditarPosAsync(ctx.Admin, gasto.Id, Edicion(5m, ctx.IdMedioEfectivo, concepto: "  "))));
        Assert.Equal(HttpStatusCode.NotFound, (await EditarPosAsync(ctx.Admin, gasto.Id, Edicion(5m, int.MaxValue))).StatusCode);
        Assert.Equal("gasto_medio_pago_cuenta_corriente_invalido", await CodigoDeErrorAsync(await EditarPosAsync(ctx.Admin, gasto.Id, Edicion(5m, idCuentaCorriente))));
        Assert.Equal(HttpStatusCode.NotFound, (await EditarPosAsync(ctx.Admin, gasto.Id, Edicion(5m, ctx.IdMedioEfectivo, CategoriaGasto.Proveedor, int.MaxValue))).StatusCode);

        await using var db = Db(ctx);
        Assert.Equal(100m, await db.Gastos.Where(g => g.Id == gasto.Id).Select(g => g.Importe).SingleAsync());
    }

    private async Task<int> MedioDeCuentaCorrienteAsync(Contexto ctx)
    {
        await using var db = Db(ctx);
        var existente = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.CuentaCorriente).Select(m => (int?)m.Id).FirstOrDefaultAsync();
        if (existente is { } id)
        {
            return id;
        }

        var ahora = DateTimeOffset.UtcNow;
        var medio = new MedioPago
        {
            IdTenant = ctx.IdTenant, Nombre = "Cuenta corriente", Orden = 60, Comportamiento = ComportamientoMedioPago.CuentaCorriente,
            AdmiteVuelto = false, RequiereReferencia = false, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medio);
        await db.SaveChangesAsync();
        return medio.Id;
    }

    // ---- autorización ------------------------------------------------------------------------

    [Fact]
    public async Task UnVendedorNoUsaLasRutasDeAdministracion()
    {
        var ctx = await PrepararAsync(nameof(UnVendedorNoUsaLasRutasDeAdministracion));
        var (vendedor, _) = await CrearVendedorAsync(ctx, nameof(UnVendedorNoUsaLasRutasDeAdministracion));
        var gasto = await RegistrarGastoAdminAsync(ctx, 90m);

        Assert.Equal(HttpStatusCode.Forbidden, (await EditarAdminAsync(vendedor, gasto.Id, Edicion(10m, ctx.IdMedioEfectivo))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vendedor.DeleteAsync($"/api/gastos/administracion/{gasto.Id}")).StatusCode);

        await using var db = Db(ctx);
        Assert.Equal(90m, await db.Gastos.Where(g => g.Id == gasto.Id).Select(g => g.Importe).SingleAsync());
    }

    // ---- administración: turno cerrado --------------------------------------------------------

    /// <summary>Edición administrativa de un gasto de caja de un turno cerrado: recalcula el
    /// esperado con la derivación del cierre, guarda el original una sola vez, crea la fila de un
    /// medio que no la tenía y marca el turno. Un segundo turno cerrado del mismo tenant queda
    /// intacto.</summary>
    [Fact]
    public async Task LaEdicionAdministrativaDeUnTurnoCerradoRecalculaSuArqueo()
    {
        var ctx = await PrepararAsync(nameof(LaEdicionAdministrativaDeUnTurnoCerradoRecalculaSuArqueo));
        var idMedioNuevo = await SembrarMedioAsync(ctx, "Transferencia de prueba");

        var idTurno = await AbrirTurnoAsync(ctx, 1000m);
        var gasto = await RegistrarGastoPosAsync(ctx.Admin, ctx, 100m, ctx.IdMedioEfectivo);
        await RegistrarGastoPosAsync(ctx.Admin, ctx, 40m, ctx.IdMedioEfectivo);
        await CerrarTurnoAsync(ctx.Admin, idTurno, new ConteoDeclarado(ctx.IdMedioEfectivo, 850m));

        var idTurnoHermano = await AbrirTurnoAsync(ctx, 300m);
        await RegistrarGastoPosAsync(ctx.Admin, ctx, 25m, ctx.IdMedioEfectivo);
        await CerrarTurnoAsync(ctx.Admin, idTurnoHermano, new ConteoDeclarado(ctx.IdMedioEfectivo, 270m));

        var primera = await EditarAdminAsync(ctx.Admin, gasto.Id, Edicion(70m, ctx.IdMedioEfectivo, concepto: "Primera"));
        var cuerpo = await primera.Content.ReadAsStringAsync();
        Assert.True(primera.StatusCode == HttpStatusCode.OK, cuerpo);
        var listado = JsonSerializer.Deserialize<GastoDeAdministracionListado>(cuerpo, OpcionesJson)!;
        Assert.Equal(70m, listado.Importe);
        Assert.Equal("Primera", listado.Concepto);
        Assert.Equal(idTurno, listado.IdTurnoCaja);
        Assert.False(listado.TurnoAbierto);

        await using (var db = Db(ctx))
        {
            var efectivo = await db.ArqueosTurno.SingleAsync(a => a.IdTurnoCaja == idTurno);
            Assert.Equal(890m, efectivo.ImporteEsperado);
            Assert.Equal(850m, efectivo.ImporteDeclarado);
            Assert.Equal(40m, efectivo.Diferencia);
            Assert.Equal(860m, efectivo.ImporteEsperadoOriginal);
        }

        Assert.Equal(HttpStatusCode.OK, (await EditarAdminAsync(ctx.Admin, gasto.Id, Edicion(50m, ctx.IdMedioEfectivo))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EditarAdminAsync(ctx.Admin, gasto.Id, Edicion(50m, idMedioNuevo))).StatusCode);

        await using (var db = Db(ctx))
        {
            var filas = await db.ArqueosTurno.Where(a => a.IdTurnoCaja == idTurno).OrderBy(a => a.IdMedioPago).ToListAsync();
            Assert.Equal(2, filas.Count);

            var efectivo = filas.Single(a => a.IdMedioPago == ctx.IdMedioEfectivo);
            Assert.Equal(960m, efectivo.ImporteEsperado);
            Assert.Equal(850m, efectivo.ImporteDeclarado);
            Assert.Equal(110m, efectivo.Diferencia);
            Assert.Equal(860m, efectivo.ImporteEsperadoOriginal);

            var nueva = filas.Single(a => a.IdMedioPago == idMedioNuevo);
            Assert.Equal(-50m, nueva.ImporteEsperado);
            Assert.Equal(0m, nueva.ImporteDeclarado);
            Assert.Equal(-50m, nueva.Diferencia);
            Assert.Equal(0m, nueva.ImporteEsperadoOriginal);

            var turno = await db.TurnosCaja.SingleAsync(t => t.Id == idTurno);
            Assert.NotNull(turno.FechaRecalculo);
            Assert.Equal(ctx.IdAdmin, turno.IdEmpleadoRecalculo);

            var hermano = await db.ArqueosTurno.SingleAsync(a => a.IdTurnoCaja == idTurnoHermano);
            Assert.Equal(275m, hermano.ImporteEsperado);
            Assert.Null(hermano.ImporteEsperadoOriginal);
            var turnoHermano = await db.TurnosCaja.SingleAsync(t => t.Id == idTurnoHermano);
            Assert.Null(turnoHermano.FechaRecalculo);
            Assert.Null(turnoHermano.IdEmpleadoRecalculo);

            Assert.Equal(3, await db.Auditoria.CountAsync(a => a.Accion == "gasto.edicion" && a.IdEntidad == gasto.Id));
        }

        var detalle = await ctx.Admin.GetFromJsonAsync<TurnoConArqueos>($"/api/caja/turnos/{idTurno}", OpcionesJson);
        Assert.NotNull(detalle!.FechaRecalculo);
        Assert.Equal(ctx.IdAdmin, detalle.IdEmpleadoRecalculo);
        Assert.Equal(860m, detalle.Arqueos.Single(a => a.IdMedioPago == ctx.IdMedioEfectivo).ImporteEsperadoOriginal);
        Assert.Equal(0m, detalle.Arqueos.Single(a => a.IdMedioPago == idMedioNuevo).ImporteEsperadoOriginal);

        var historico = await ctx.Admin.GetFromJsonAsync<PaginaDeHistoricoDeCajas>(
            $"/api/reportes/cajas?idPuntoVenta={ctx.IdPuntoVenta}", OpcionesJson);
        var filaRecalculada = historico!.Items.Single(f => f.IdTurnoCaja == idTurno);
        Assert.Equal(detalle.FechaRecalculo, filaRecalculada.FechaRecalculo);
        Assert.Equal(ctx.IdAdmin, filaRecalculada.IdEmpleadoRecalculo);
        Assert.Equal(910m, filaRecalculada.Esperado);
        var filaHermana = historico.Items.Single(f => f.IdTurnoCaja == idTurnoHermano);
        Assert.Null(filaHermana.FechaRecalculo);
        Assert.Null(filaHermana.IdEmpleadoRecalculo);
    }

    /// <summary>Cambiar solo el concepto de un gasto de caja de un turno cerrado no toca el arqueo
    /// ni deja la marca de recálculo.</summary>
    [Fact]
    public async Task UnaEdicionQueNoCambiaImporteNiMedioNoRecalcula()
    {
        var ctx = await PrepararAsync(nameof(UnaEdicionQueNoCambiaImporteNiMedioNoRecalcula));
        var idTurno = await AbrirTurnoAsync(ctx, 200m);
        var gasto = await RegistrarGastoPosAsync(ctx.Admin, ctx, 30m, ctx.IdMedioEfectivo);
        await CerrarTurnoAsync(ctx.Admin, idTurno, new ConteoDeclarado(ctx.IdMedioEfectivo, 170m));

        Assert.Equal(HttpStatusCode.OK, (await EditarAdminAsync(ctx.Admin, gasto.Id, Edicion(30m, ctx.IdMedioEfectivo, concepto: "Otro texto"))).StatusCode);

        await using var db = Db(ctx);
        Assert.Null(await db.TurnosCaja.Where(t => t.Id == idTurno).Select(t => t.FechaRecalculo).SingleAsync());
        Assert.Null(await db.ArqueosTurno.Where(a => a.IdTurnoCaja == idTurno).Select(a => a.ImporteEsperadoOriginal).SingleAsync());
    }

    /// <summary>Baja de un gasto de caja de un turno cerrado: queda dado de baja (404 de ahí en
    /// más), el arqueo se recalcula y el medio que se queda sin actividad conserva su fila en 0.</summary>
    [Fact]
    public async Task LaBajaDeUnGastoDeUnTurnoCerradoRecalculaElArqueoYConservaLaFila()
    {
        var ctx = await PrepararAsync(nameof(LaBajaDeUnGastoDeUnTurnoCerradoRecalculaElArqueoYConservaLaFila));
        var idTurno = await AbrirTurnoAsync(ctx, 600m);
        var deEfectivo = await RegistrarGastoPosAsync(ctx.Admin, ctx, 45m, ctx.IdMedioEfectivo);
        var electronico = await RegistrarGastoPosAsync(ctx.Admin, ctx, 80m, ctx.IdMedioElectronico);
        await CerrarTurnoAsync(
            ctx.Admin, idTurno, new ConteoDeclarado(ctx.IdMedioEfectivo, 555m), new ConteoDeclarado(ctx.IdMedioElectronico, 0m));

        var respuesta = await ctx.Admin.DeleteAsync($"/api/gastos/administracion/{electronico.Id}");
        Assert.Equal(HttpStatusCode.NoContent, respuesta.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.GetAsync($"/api/gastos/administracion/{electronico.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ctx.Admin.DeleteAsync($"/api/gastos/administracion/{electronico.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EditarAdminAsync(ctx.Admin, electronico.Id, Edicion(5m, ctx.IdMedioEfectivo))).StatusCode);

        await using var db = Db(ctx);
        Assert.NotNull(await db.Gastos.IgnoreQueryFilters().Where(g => g.Id == electronico.Id).Select(g => g.DeletedAt).SingleAsync());
        Assert.False(await db.Gastos.AnyAsync(g => g.Id == electronico.Id));

        var filas = await db.ArqueosTurno.Where(a => a.IdTurnoCaja == idTurno).ToListAsync();
        Assert.Equal(2, filas.Count);
        var filaElectronica = filas.Single(a => a.IdMedioPago == ctx.IdMedioElectronico);
        Assert.Equal(0m, filaElectronica.ImporteEsperado);
        Assert.Equal(-80m, filaElectronica.ImporteEsperadoOriginal);
        Assert.Equal(0m, filaElectronica.Diferencia);
        var filaEfectivo = filas.Single(a => a.IdMedioPago == ctx.IdMedioEfectivo);
        Assert.Equal(555m, filaEfectivo.ImporteEsperado);
        Assert.Null(filaEfectivo.ImporteEsperadoOriginal);
        Assert.Equal(ctx.IdAdmin, await db.TurnosCaja.Where(t => t.Id == idTurno).Select(t => t.IdEmpleadoRecalculo).SingleAsync());

        var auditoria = await db.Auditoria.SingleAsync(a => a.Accion == "gasto.baja" && a.IdEntidad == electronico.Id);
        Assert.Equal(80m, JsonDocument.Parse(auditoria.ValorAnterior!).RootElement.GetProperty("importe").GetDecimal());
        Assert.NotEqual(JsonValueKind.Null, JsonDocument.Parse(auditoria.ValorNuevo).RootElement.GetProperty("deleted_at").ValueKind);
        Assert.Equal(45m, await db.Gastos.Where(g => g.Id == deEfectivo.Id).Select(g => g.Importe).SingleAsync());
    }

    // ---- administración: sin turno ------------------------------------------------------------

    [Fact]
    public async Task LaBajaAdministrativaDevuelveElPagoAlProveedorYReingresaATesoreria()
    {
        var ctx = await PrepararAsync(nameof(LaBajaAdministrativaDevuelveElPagoAlProveedorYReingresaATesoreria));
        await RegistrarGastoAdminAsync(ctx, 1000m, CategoriaGasto.Proveedor, ctx.IdProveedor, ctx.IdPuntoVenta);
        var gasto = await RegistrarGastoAdminAsync(ctx, 300m, CategoriaGasto.Proveedor, ctx.IdProveedor, ctx.IdPuntoVenta);

        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/gastos/administracion/{gasto.Id}")).StatusCode);

        await using var db = Db(ctx);
        Assert.Equal(-1000m, await db.Proveedores.Where(p => p.Id == ctx.IdProveedor).Select(p => p.Saldo).SingleAsync());
        var ajuste = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdProveedor == ctx.IdProveedor && m.Tipo == TipoMovimientoCcProveedor.Ajuste);
        Assert.Equal(300m, ajuste.Importe);
        Assert.Equal(-1000m, ajuste.SaldoResultante);
        Assert.Equal($"Baja del gasto #{gasto.Id}", ajuste.Detalle);

        var ultimo = await db.MovimientosTesoreria.Where(m => m.IdEmpresa == ctx.IdEmpresa).OrderByDescending(m => m.Id).FirstAsync();
        Assert.Equal(TipoMovimientoTesoreria.Ajuste, ultimo.Tipo);
        Assert.Equal(-1300m, ultimo.Inicio);
        Assert.Equal(300m, ultimo.Ingreso);
        Assert.Equal(0m, ultimo.Egreso);
        Assert.Equal(-1000m, ultimo.Final);
        Assert.Equal($"Baja del gasto #{gasto.Id}", ultimo.Concepto);
    }

    [Fact]
    public async Task CambiarDeProveedorDevuelveElPagoAlAnteriorYCobraAlNuevo()
    {
        var ctx = await PrepararAsync(nameof(CambiarDeProveedorDevuelveElPagoAlAnteriorYCobraAlNuevo));
        await RegistrarGastoAdminAsync(ctx, 500m, CategoriaGasto.Proveedor, ctx.IdProveedor2, ctx.IdPuntoVenta);
        var gasto = await RegistrarGastoAdminAsync(ctx, 300m, CategoriaGasto.Proveedor, ctx.IdProveedor, ctx.IdPuntoVenta);

        var respuesta = await EditarAdminAsync(
            ctx.Admin, gasto.Id, Edicion(120m, ctx.IdMedioEfectivo, CategoriaGasto.Proveedor, ctx.IdProveedor2));
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        await using var db = Db(ctx);
        Assert.Equal(0m, await db.Proveedores.Where(p => p.Id == ctx.IdProveedor).Select(p => p.Saldo).SingleAsync());
        Assert.Equal(-620m, await db.Proveedores.Where(p => p.Id == ctx.IdProveedor2).Select(p => p.Saldo).SingleAsync());

        var ajustes = await db.MovimientosCuentaCorrienteProveedor
            .Where(m => m.Tipo == TipoMovimientoCcProveedor.Ajuste).OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(2, ajustes.Count);
        Assert.Contains(ajustes, m => m.IdProveedor == ctx.IdProveedor && m.Importe == 300m && m.SaldoResultante == 0m);
        Assert.Contains(ajustes, m => m.IdProveedor == ctx.IdProveedor2 && m.Importe == -120m && m.SaldoResultante == -620m);

        var ultimo = await db.MovimientosTesoreria.Where(m => m.IdEmpresa == ctx.IdEmpresa).OrderByDescending(m => m.Id).FirstAsync();
        Assert.Equal((TipoMovimientoTesoreria.Ajuste, 180m, 0m), (ultimo.Tipo, ultimo.Ingreso, ultimo.Egreso));
    }

    [Fact]
    public async Task UnGastoAdministrativoSinPuntoDeVentaNoPasaAProveedor()
    {
        var ctx = await PrepararAsync(nameof(UnGastoAdministrativoSinPuntoDeVentaNoPasaAProveedor));
        var gasto = await RegistrarGastoAdminAsync(ctx, 60m);

        var respuesta = await EditarAdminAsync(
            ctx.Admin, gasto.Id, Edicion(60m, ctx.IdMedioEfectivo, CategoriaGasto.Proveedor, ctx.IdProveedor));
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal("gasto_de_proveedor_requiere_punto_de_venta", await CodigoDeErrorAsync(respuesta));

        await using var db = Db(ctx);
        Assert.Equal(CategoriaGasto.Otros, await db.Gastos.Where(g => g.Id == gasto.Id).Select(g => g.Categoria).SingleAsync());
        Assert.Equal(0m, await db.Proveedores.Where(p => p.Id == ctx.IdProveedor).Select(p => p.Saldo).SingleAsync());
    }

    [Fact]
    public async Task UnGastoLigadoAUnaCompraSigueSiendoDeSuProveedor()
    {
        var ctx = await PrepararAsync(nameof(UnGastoLigadoAUnaCompraSigueSiendoDeSuProveedor));
        var idCompra = await CrearYConfirmarCompraAsync(ctx);
        var gasto = await RegistrarGastoAdminAsync(
            ctx, 400m, CategoriaGasto.Proveedor, ctx.IdProveedor, ctx.IdPuntoVenta, idCompra);

        var otraCategoria = await EditarAdminAsync(ctx.Admin, gasto.Id, Edicion(400m, ctx.IdMedioEfectivo, CategoriaGasto.Otros));
        Assert.Equal(HttpStatusCode.BadRequest, otraCategoria.StatusCode);
        Assert.Equal("gasto_de_compra_debe_ser_de_proveedor", await CodigoDeErrorAsync(otraCategoria));

        var otroProveedor = await EditarAdminAsync(
            ctx.Admin, gasto.Id, Edicion(400m, ctx.IdMedioEfectivo, CategoriaGasto.Proveedor, ctx.IdProveedor2));
        Assert.Equal(HttpStatusCode.BadRequest, otroProveedor.StatusCode);
        Assert.Equal("proveedor_no_coincide_con_la_compra", await CodigoDeErrorAsync(otroProveedor));

        var mismoProveedor = await EditarAdminAsync(
            ctx.Admin, gasto.Id, Edicion(350m, ctx.IdMedioEfectivo, CategoriaGasto.Proveedor, ctx.IdProveedor));
        Assert.Equal(HttpStatusCode.OK, mismoProveedor.StatusCode);

        await using var db = Db(ctx);
        var fila = await db.Gastos.SingleAsync(g => g.Id == gasto.Id);
        Assert.Equal((350m, idCompra, ctx.IdProveedor), (fila.Importe, fila.IdComprobanteCompra, fila.IdProveedor));
        var ajuste = await db.MovimientosCuentaCorrienteProveedor
            .SingleAsync(m => m.IdProveedor == ctx.IdProveedor && m.Tipo == TipoMovimientoCcProveedor.Ajuste);
        Assert.Equal(50m, ajuste.Importe);
    }

    private async Task<int> CrearYConfirmarCompraAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;
        var idAlicuotaIva21 = await db.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();
        var idTipoCFA = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();
        var articulo = new Articulo
        {
            IdTenant = ctx.IdTenant, CodigoInterno = $"edicion-{Guid.NewGuid():N}", Nombre = "Articulo",
            IdArea = ctx.IdArea, IdAlicuotaIva = idAlicuotaIva21, UnidadVenta = UnidadVenta.Unidad, EsProducto = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Articulos.Add(articulo);
        await db.SaveChangesAsync();

        var alta = await ctx.Admin.PostAsJsonAsync("/api/compras", new SolicitudDeCompra(
            ctx.IdProveedor, idTipoCFA, ctx.IdPuntoVenta, "0001-00000001", DateOnly.FromDateTime(DateTime.UtcNow), null,
            [new LineaDeCompraSolicitada(articulo.Id, "Item", 10m, null, null, 100m, 0m, idAlicuotaIva21, true)]));
        var cuerpo = await alta.Content.ReadAsStringAsync();
        Assert.True(alta.StatusCode == HttpStatusCode.Created, cuerpo);
        var idCompra = JsonSerializer.Deserialize<CompraDetalle>(cuerpo, OpcionesJson)!.Id;

        var confirmar = await ctx.Admin.PostAsync($"/api/compras/{idCompra}/confirmar", null);
        Assert.Equal(HttpStatusCode.OK, confirmar.StatusCode);
        return idCompra;
    }

    // ---- read models ---------------------------------------------------------------------------

    [Fact]
    public async Task LosListadosDicenSiElTurnoDelGastoSigueAbiertoYTraenLosCamposEditables()
    {
        var ctx = await PrepararAsync(nameof(LosListadosDicenSiElTurnoDelGastoSigueAbiertoYTraenLosCamposEditables));
        var idTurnoCerrado = await AbrirTurnoAsync(ctx, 0m);
        var deTurnoCerrado = await RegistrarGastoPosAsync(ctx.Admin, ctx, 11m, ctx.IdMedioElectronico);
        await CerrarTurnoAsync(ctx.Admin, idTurnoCerrado, new ConteoDeclarado(ctx.IdMedioElectronico, 0m));

        var idTurnoAbierto = await AbrirTurnoAsync(ctx, 0m);
        var deTurnoAbierto = await RegistrarGastoPosAsync(ctx.Admin, ctx, 22m, ctx.IdMedioEfectivo);
        Assert.Equal(HttpStatusCode.OK, (await EditarPosAsync(ctx.Admin, deTurnoAbierto.Id, Edicion(
            23m, ctx.IdMedioEfectivo, CategoriaGasto.Proveedor, ctx.IdProveedor, ctx.IdArea, "Con todo", "Detalle", "FC-9"))).StatusCode);
        var sinTurno = await RegistrarGastoAdminAsync(ctx, 33m);

        var pos = await ctx.Admin.GetFromJsonAsync<PaginaDeGastos>("/api/gastos?tamanio=50", OpcionesJson);
        var abierto = pos!.Items.Single(g => g.Id == deTurnoAbierto.Id);
        Assert.Equal(
            (idTurnoAbierto, true, ctx.IdProveedor, ctx.IdArea, "Con todo", "Detalle", "FC-9"),
            (abierto.IdTurnoCaja, abierto.TurnoAbierto, abierto.IdProveedor, abierto.IdArea, abierto.Concepto, abierto.Detalle, abierto.NumeroFactura));
        var cerrado = pos.Items.Single(g => g.Id == deTurnoCerrado.Id);
        Assert.Equal((idTurnoCerrado, false), (cerrado.IdTurnoCaja, cerrado.TurnoAbierto));
        var administrativo = pos.Items.Single(g => g.Id == sinTurno.Id);
        Assert.Equal(((int?)null, false), (administrativo.IdTurnoCaja, administrativo.TurnoAbierto));

        var admin = await ctx.Admin.GetFromJsonAsync<PaginaDeGastosDeAdministracion>("/api/gastos/administracion?tamanio=50", OpcionesJson);
        Assert.True(admin!.Items.Single(g => g.Id == deTurnoAbierto.Id).TurnoAbierto);
        Assert.False(admin.Items.Single(g => g.Id == deTurnoCerrado.Id).TurnoAbierto);
        Assert.False(admin.Items.Single(g => g.Id == sinTurno.Id).TurnoAbierto);

        var detalle = await ctx.Admin.GetFromJsonAsync<DetalleDeTurno>($"/api/caja/turnos/{idTurnoAbierto}/detalle", OpcionesJson);
        var linea = Assert.Single(detalle!.Gastos);
        Assert.Equal(
            (deTurnoAbierto.Id, true, ctx.IdProveedor, ctx.IdArea, "Con todo", "Detalle", "FC-9", OrigenFondosGasto.CajaTurno),
            (linea.Id, linea.TurnoAbierto, linea.IdProveedor, linea.IdArea, linea.Concepto, linea.Detalle, linea.NumeroFactura, linea.OrigenFondos));
    }

    // ---- concurrencia ---------------------------------------------------------------------------

    /// <summary>Pausa la transacción de la edición justo antes de su fila de auditoría — ya con el
    /// turno <c>FOR SHARE</c> tomado y el gasto ya actualizado, sin comitear.</summary>
    private sealed class PausaAntesDeLaAuditoria : DbCommandInterceptor
    {
        private int _disparado;

        public TaskCompletionSource Alcanzado { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Liberar { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO auditoria", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _disparado, 1) == 0)
            {
                Alcanzado.SetResult();
                await Liberar.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>LA CLÁUSULA: el <c>FOR SHARE</c> sobre el turno en la edición del POS. Con la
    /// edición en vuelo (lock tomado, sin comitear), el cierre tiene que esperarla y derivar el
    /// arqueo con el importe editado; sin el lock, el cierre comitea primero con el importe viejo y
    /// el arqueo persistido queda desactualizado.</summary>
    [Fact]
    public async Task UnCierreConcurrenteEsperaALaEdicionDelPosYDerivaConElGastoEditado()
    {
        var ctx = await PrepararAsync(nameof(UnCierreConcurrenteEsperaALaEdicionDelPosYDerivaConElGastoEditado));
        var idTurno = await AbrirTurnoAsync(ctx, 1000m);
        var gasto = await RegistrarGastoPosAsync(ctx.Admin, ctx, 100m, ctx.IdMedioEfectivo);

        var pausa = new PausaAntesDeLaAuditoria();
        await using var factory = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<WaysDbContext>((_, options) => options.AddInterceptors(pausa))));
        using var editor = factory.CreateClient();
        var login = await editor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(ctx.MailAdmin, ctx.PasswordAdmin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var edicion = EditarPosAsync(editor, gasto.Id, Edicion(70m, ctx.IdMedioEfectivo));
        await pausa.Alcanzado.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var cierre = ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurno}/cierre",
            new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 930m)], null));

        await EsperarCierreBloqueadoOTerminadoAsync(cierre);
        pausa.Liberar.SetResult();

        Assert.Equal(HttpStatusCode.OK, (await edicion).StatusCode);
        var respuestaCierre = await cierre;
        Assert.True(respuestaCierre.StatusCode == HttpStatusCode.OK, await respuestaCierre.Content.ReadAsStringAsync());

        await using var db = Db(ctx);
        var arqueo = await db.ArqueosTurno.SingleAsync(a => a.IdTurnoCaja == idTurno);
        Assert.Equal(930m, arqueo.ImporteEsperado);
        Assert.Equal(0m, arqueo.Diferencia);
        Assert.Null(arqueo.ImporteEsperadoOriginal);
    }

    private async Task EsperarCierreBloqueadoOTerminadoAsync(Task cierre)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        var limite = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < limite && !cierre.IsCompleted)
        {
            await using var comando = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' " +
                "AND query LIKE 'UPDATE turnos_caja SET estado%'",
                conexion);
            if ((long)(await comando.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(25);
        }
    }

    // ---- sin reintento ----------------------------------------------------------------------------

    /// <summary>LA CLÁUSULA: la edición corre bajo <c>FabricaDeEstrategiaSinReintento</c>. Un fallo
    /// transitorio en la fila de auditoría (la última escritura, con el ajuste de cuenta corriente ya
    /// escrito en la misma transacción) llega como <c>503 resultado_incierto</c> tras UN solo
    /// intento y no deja nada: ni el gasto editado ni el ajuste.</summary>
    [Fact]
    public async Task UnFalloTransitorioEnLaEdicionNoSeReintentaYNoDejaAjustes()
    {
        var ctx = await PrepararAsync(nameof(UnFalloTransitorioEnLaEdicionNoSeReintentaYNoDejaAjustes));
        await AbrirTurnoAsync(ctx, 0m);
        var gasto = await RegistrarGastoPosAsync(ctx.Admin, ctx, 300m, ctx.IdMedioEfectivo, CategoriaGasto.Proveedor, ctx.IdProveedor);

        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("auditoria", "40001");
        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(interceptor))
        {
            respuesta = await EditarPosAsync(
                ctx.Admin, gasto.Id, Edicion(120m, ctx.IdMedioEfectivo, CategoriaGasto.Proveedor, ctx.IdProveedor));
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, respuesta.StatusCode);
        Assert.Equal("resultado_incierto", await CodigoDeErrorAsync(respuesta));
        Assert.Equal(1, interceptor.Intentos);

        await using var db = Db(ctx);
        Assert.Equal(300m, await db.Gastos.Where(g => g.Id == gasto.Id).Select(g => g.Importe).SingleAsync());
        Assert.Equal(-300m, await db.Proveedores.Where(p => p.Id == ctx.IdProveedor).Select(p => p.Saldo).SingleAsync());
        Assert.Equal(0, await db.MovimientosCuentaCorrienteProveedor.CountAsync(m => m.Tipo == TipoMovimientoCcProveedor.Ajuste));
        Assert.Equal(0, await db.Auditoria.CountAsync(a => a.Accion == "gasto.edicion"));
    }

    // ---- backstop de esquema --------------------------------------------------------------------

    [Fact]
    public async Task LaMarcaDeRecalculoSinEmpleadoViolaSuCheck()
    {
        var ctx = await PrepararAsync(nameof(LaMarcaDeRecalculoSinEmpleadoViolaSuCheck));
        var idTurno = await AbrirTurnoAsync(ctx, 0m);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);

        foreach (var asignacion in new[] { "fecha_recalculo = now()", $"id_empleado_recalculo = {ctx.IdAdmin}" })
        {
            await using var comando = cruda.CreateCommand();
            comando.CommandText = $"UPDATE turnos_caja SET {asignacion} WHERE id_turno_caja = $1";
            comando.Parameters.Add(new NpgsqlParameter { Value = idTurno });

            var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
            Assert.Equal("23514", excepcion.SqlState);
            Assert.Equal("ck_turnos_caja_recalculo_consistente", excepcion.ConstraintName);
        }
    }
}
