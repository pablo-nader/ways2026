using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Organizacion;
using Ways.Application.Pagos;
using Ways.Application.Plantel;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Asignar un empleado a un pago de sueldos común lo convierte en un pago de personal dentro de la edición
/// (<c>PUT /api/pagos/administracion/{id}</c> y <c>PUT /api/pagos/{id}</c> con <c>idPersonal</c>): el pago y sus ítems
/// conservan medios, importes, origen, turno y fecha, así que el arqueo y la tesorería no se mueven; el libro del empleado
/// recibe el asiento de pago y su saldo baja por el total.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PagoDeSueldoConvertidoEnPersonalTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordVendedor = "vendedor-password-larga";

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdMedioEfectivo, HttpClient Admin, HttpClient Vendedor);

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));
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

        var mailVendedor = $"{nombre.ToLowerInvariant()}-vendedor@ways.test";
        var alta = await admin.PostAsJsonAsync(
            "/api/usuarios",
            new CrearUsuario($"vend-{Guid.NewGuid().ToString("N")[..8]}", mailVendedor, (int)RolConocido.Vendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var vendedor = fixture.CreateClient();
        var loginVendedor = await vendedor.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailVendedor, PasswordVendedor));
        Assert.Equal(HttpStatusCode.OK, loginVendedor.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        return new Contexto(resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, idMedioEfectivo, admin, vendedor);
    }

    private static async Task<JsonElement> CuerpoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {texto}");
        return JsonDocument.Parse(texto).RootElement.Clone();
    }

    private static async Task<string> CodigoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado) =>
        (await CuerpoAsync(respuesta, esperado)).GetProperty("codigo").GetString()!;

    private static async Task<int> CrearPersonalAsync(HttpClient admin, string nombre, decimal saldoInicial = 0m)
    {
        var respuesta = await admin.PostAsJsonAsync(
            "/api/personal", new DatosDePersonal(nombre, "30111222", "1155550000", "obs", 1234m, null, null, null));
        var id = (await CuerpoAsync(respuesta, HttpStatusCode.Created)).GetProperty("id").GetInt32();

        if (saldoInicial != 0m)
        {
            var ajuste = await admin.PostAsJsonAsync(
                $"/api/personal/{id}/ajustes", new { importe = saldoInicial, detalle = "Saldo inicial de la prueba", fecha = "2026-10-01" });
            Assert.Equal(HttpStatusCode.Created, ajuste.StatusCode);
        }

        return id;
    }

    private static async Task<int> AbrirTurnoAsync(Contexto ctx, decimal fondoInicial)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, fondoInicial, "Apertura de prueba"));
        return (await CuerpoAsync(respuesta, HttpStatusCode.Created)).GetProperty("id").GetInt32();
    }

    private static async Task<int> PagoDelPosAsync(Contexto ctx, decimal importe)
    {
        var respuesta = await ctx.Vendedor.PostAsJsonAsync(
            "/api/pagos",
            PagoDeUnMedio.Pos(
                ctx.IdPuntoVenta, CategoriaPago.Sueldos, null, null, "Sueldo suelto", null, ctx.IdMedioEfectivo, null, importe));
        return (await CuerpoAsync(respuesta, HttpStatusCode.Created)).GetProperty("id").GetInt32();
    }

    private static async Task<int> PagoDeTesoreriaAsync(
        Contexto ctx, decimal importe, CategoriaPago categoria = CategoriaPago.Sueldos)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/pagos/administracion",
            PagoDeUnMedio.Administracion(
                FechaDelNegocio.Hoy().AddDays(-1), ctx.IdEmpresa, null, categoria, null, null, "Sueldo suelto", null,
                ctx.IdMedioEfectivo, null, importe));
        return (await CuerpoAsync(respuesta, HttpStatusCode.Created)).GetProperty("id").GetInt32();
    }

    private static SolicitudDeEdicionDePagoConLineas Conversion(
        Contexto ctx, decimal importe, int? idPersonal, CategoriaPago categoria = CategoriaPago.Sueldos,
        string concepto = "") =>
        new(categoria, null, null, concepto, "Detalle conservado", null,
            [new LineaDePago(ctx.IdMedioEfectivo, importe, null)], idPersonal);

    private static Task<HttpResponseMessage> EditarAdminAsync(
        Contexto ctx, int idPago, SolicitudDeEdicionDePagoConLineas solicitud) =>
        ctx.Admin.PutAsJsonAsync($"/api/pagos/administracion/{idPago}", solicitud);

    private static Task<HttpResponseMessage> EditarPosAsync(
        Contexto ctx, int idPago, SolicitudDeEdicionDePagoConLineas solicitud) =>
        ctx.Vendedor.PutAsJsonAsync($"/api/pagos/{idPago}", solicitud);

    private sealed record Foto(
        decimal Saldo, int Asientos, int Pagos, int Items, int AsientosDeTesoreria, int Arqueos, decimal Esperado);

    private async Task<Foto> FotoAsync(Contexto ctx, int idPersonal, int? idTurno = null)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return new Foto(
            await db.Personal.Where(p => p.Id == idPersonal).Select(p => p.Saldo).SingleAsync(),
            await db.MovimientosPersonal.CountAsync(),
            await db.Pagos.CountAsync(),
            await db.ItemsPago.CountAsync(),
            await db.MovimientosTesoreria.CountAsync(),
            await db.ArqueosTurno.CountAsync(),
            idTurno is null
                ? 0m
                : await db.ArqueosTurno.Where(a => a.IdTurnoCaja == idTurno).SumAsync(a => (decimal?)a.ImporteEsperado) ?? 0m);
    }

    [Fact]
    public async Task ElAdminConvierteUnSueldoDeTesoreriaEnPagoDePersonalSinMoverTesoreriaNiItems()
    {
        var ctx = await PrepararAsync(nameof(ElAdminConvierteUnSueldoDeTesoreriaEnPagoDePersonalSinMoverTesoreriaNiItems));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);
        var idPago = await PagoDeTesoreriaAsync(ctx, 300m);
        var antes = await FotoAsync(ctx, idPersonal);

        var cuerpo = await CuerpoAsync(
            await EditarAdminAsync(ctx, idPago, Conversion(ctx, 300m, idPersonal)), HttpStatusCode.OK);
        Assert.Equal("Pago de personal: Ana", cuerpo.GetProperty("concepto").GetString());

        var despues = await FotoAsync(ctx, idPersonal);
        Assert.Equal(antes with { Saldo = 700m, Asientos = antes.Asientos + 1 }, despues);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var pago = await db.Pagos.SingleAsync(p => p.Id == idPago);
        var movimiento = await db.MovimientosPersonal.SingleAsync(m => m.Id == pago.IdMovimientoPersonal);
        Assert.Equal(idPersonal, movimiento.IdPersonal);
        Assert.Equal(TipoMovimientoPersonal.Pago, movimiento.Tipo);
        Assert.Equal(-300m, movimiento.Importe);
        Assert.Equal(700m, movimiento.SaldoResultante);
        Assert.Equal(CategoriaPago.Sueldos, pago.Categoria);
        Assert.Equal(300m, pago.Importe);
    }

    [Fact]
    public async Task ElCajeroConvierteUnSueldoDelCajonDeSuTurnoAbiertoYLaRespuestaNoLlevaSaldo()
    {
        var ctx = await PrepararAsync(nameof(ElCajeroConvierteUnSueldoDelCajonDeSuTurnoAbiertoYLaRespuestaNoLlevaSaldo));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);
        var idTurno = await AbrirTurnoAsync(ctx, fondoInicial: 500m);
        var idPago = await PagoDelPosAsync(ctx, 200m);
        var antes = await FotoAsync(ctx, idPersonal, idTurno);

        var respuesta = await EditarPosAsync(ctx, idPago, Conversion(ctx, 200m, idPersonal));
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, texto);
        Assert.DoesNotContain("saldo", texto, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Pago de personal: Ana", texto);

        var despues = await FotoAsync(ctx, idPersonal, idTurno);
        Assert.Equal(antes with { Saldo = 800m, Asientos = antes.Asientos + 1 }, despues);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var pago = await db.Pagos.SingleAsync(p => p.Id == idPago);
        Assert.NotNull(pago.IdMovimientoPersonal);
        Assert.Equal(idTurno, pago.IdTurnoCaja);
        Assert.Equal(OrigenFondosPago.CajaTurno, pago.OrigenFondos);
    }

    [Fact]
    public async Task UnaVezConvertidoElPagoDejaDeSerEditableYBorrableDesdeLosDosLados()
    {
        var ctx = await PrepararAsync(nameof(UnaVezConvertidoElPagoDejaDeSerEditableYBorrableDesdeLosDosLados));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);
        await AbrirTurnoAsync(ctx, fondoInicial: 500m);
        var idPago = await PagoDelPosAsync(ctx, 200m);
        await CuerpoAsync(await EditarPosAsync(ctx, idPago, Conversion(ctx, 200m, idPersonal)), HttpStatusCode.OK);
        var antes = await FotoAsync(ctx, idPersonal);

        Assert.Equal(
            "gasto_de_pago_de_personal",
            await CodigoAsync(await EditarPosAsync(ctx, idPago, Conversion(ctx, 200m, idPersonal)), HttpStatusCode.Conflict));
        Assert.Equal(
            "gasto_de_pago_de_personal",
            await CodigoAsync(await EditarAdminAsync(ctx, idPago, Conversion(ctx, 200m, idPersonal)), HttpStatusCode.Conflict));
        Assert.Equal(
            "gasto_de_pago_de_personal",
            await CodigoAsync(await EditarAdminAsync(ctx, idPago, Conversion(ctx, 50m, null, concepto: "x")), HttpStatusCode.Conflict));
        Assert.Equal(
            "gasto_de_pago_de_personal",
            await CodigoAsync(await ctx.Admin.DeleteAsync($"/api/pagos/administracion/{idPago}"), HttpStatusCode.Conflict));

        Assert.Equal(antes, await FotoAsync(ctx, idPersonal));
    }

    [Fact]
    public async Task ConvertirUnPagoQueNoEsDeSueldosOConConceptoPropioSeRechazaSinEscribirNada()
    {
        var ctx = await PrepararAsync(nameof(ConvertirUnPagoQueNoEsDeSueldosOConConceptoPropioSeRechazaSinEscribirNada));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);
        var idOtros = await PagoDeTesoreriaAsync(ctx, 100m, CategoriaPago.Otros);
        var idSueldos = await PagoDeTesoreriaAsync(ctx, 100m);
        var antes = await FotoAsync(ctx, idPersonal);

        var otraCategoria = await EditarAdminAsync(
            ctx, idOtros, Conversion(ctx, 100m, idPersonal, categoria: CategoriaPago.Otros));
        Assert.Equal("pago_personal_requiere_sueldos", await CodigoAsync(otraCategoria, HttpStatusCode.BadRequest));

        var conceptoPropio = await EditarAdminAsync(ctx, idSueldos, Conversion(ctx, 100m, idPersonal, concepto: "Mi concepto"));
        Assert.Equal("concepto_de_pago_de_personal", await CodigoAsync(conceptoPropio, HttpStatusCode.BadRequest));

        Assert.Equal(antes, await FotoAsync(ctx, idPersonal));
    }

    [Fact]
    public async Task ElPagoOElEmpleadoInexistentesOAjenosSonNotFoundAntesQueCualquierOtroError()
    {
        var ctx = await PrepararAsync(nameof(ElPagoOElEmpleadoInexistentesOAjenosSonNotFoundAntesQueCualquierOtroError));
        var otro = await PrepararAsync(nameof(ElPagoOElEmpleadoInexistentesOAjenosSonNotFoundAntesQueCualquierOtroError) + "Otro");
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);
        var idAjeno = await CrearPersonalAsync(otro.Admin, "Ajeno");
        var idBaja = await CrearPersonalAsync(ctx.Admin, "De baja");
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/personal/{idBaja}")).StatusCode);
        var idPago = await PagoDeTesoreriaAsync(ctx, 100m);
        var idPagoAjeno = await PagoDeTesoreriaAsync(otro, 100m);
        var antes = await FotoAsync(ctx, idPersonal);

        // Cuerpo inválido a propósito (categoría ajena a sueldos y concepto propio): el 404 va primero.
        var invalido = Conversion(ctx, 100m, idPersonal, categoria: CategoriaPago.Otros, concepto: "x");
        Assert.Equal(HttpStatusCode.NotFound, (await EditarAdminAsync(ctx, 999999, invalido)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await EditarAdminAsync(ctx, idPagoAjeno, invalido)).StatusCode);

        foreach (var idInexistente in new[] { 999999, idAjeno, idBaja })
        {
            var conEmpleadoMalo = Conversion(ctx, 100m, idInexistente, categoria: CategoriaPago.Otros, concepto: "x");
            Assert.Equal(HttpStatusCode.NotFound, (await EditarAdminAsync(ctx, idPago, conEmpleadoMalo)).StatusCode);
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await EditarAdminAsync(ctx, idPago, Conversion(ctx, 100m, idInexistente))).StatusCode);
        }

        Assert.Equal(antes, await FotoAsync(ctx, idPersonal));
    }

    [Fact]
    public async Task ElCajeroNoConvierteUnPagoDeUnTurnoCerradoYElAdminSiSinTocarElArqueo()
    {
        var ctx = await PrepararAsync(nameof(ElCajeroNoConvierteUnPagoDeUnTurnoCerradoYElAdminSiSinTocarElArqueo));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);
        var idTurno = await AbrirTurnoAsync(ctx, fondoInicial: 500m);
        var idPago = await PagoDelPosAsync(ctx, 200m);
        var cierre = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurno}/cierre",
            new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 300m)], null));
        Assert.Equal(HttpStatusCode.OK, cierre.StatusCode);
        var antes = await FotoAsync(ctx, idPersonal, idTurno);

        Assert.Equal(
            "gasto_turno_cerrado",
            await CodigoAsync(await EditarPosAsync(ctx, idPago, Conversion(ctx, 200m, idPersonal)), HttpStatusCode.Conflict));
        Assert.Equal(antes, await FotoAsync(ctx, idPersonal, idTurno));

        await CuerpoAsync(await EditarAdminAsync(ctx, idPago, Conversion(ctx, 200m, idPersonal)), HttpStatusCode.OK);
        Assert.Equal(antes with { Saldo = 800m, Asientos = antes.Asientos + 1 }, await FotoAsync(ctx, idPersonal, idTurno));
    }

    [Fact]
    public async Task ElCajeroNoAsignaUnEmpleadoDeOtroTenant()
    {
        var ctx = await PrepararAsync(nameof(ElCajeroNoAsignaUnEmpleadoDeOtroTenant));
        var otro = await PrepararAsync(nameof(ElCajeroNoAsignaUnEmpleadoDeOtroTenant) + "Otro");
        var idAjeno = await CrearPersonalAsync(otro.Admin, "Ajeno", saldoInicial: 500m);
        await AbrirTurnoAsync(ctx, fondoInicial: 500m);
        var idPago = await PagoDelPosAsync(ctx, 200m);
        var antesAjeno = await FotoAsync(otro, idAjeno);

        Assert.Equal(HttpStatusCode.NotFound, (await EditarPosAsync(ctx, idPago, Conversion(ctx, 200m, idAjeno))).StatusCode);
        Assert.Equal(antesAjeno, await FotoAsync(otro, idAjeno));
    }

    [Fact]
    public async Task ElAsientoDelEmpleadoLlevaLaFechaDeNegocioDelPagoEnLaZonaDeLaEmpresaYNoLaFechaUtc()
    {
        var ctx = await PrepararAsync(nameof(ElAsientoDelEmpleadoLlevaLaFechaDeNegocioDelPagoEnLaZonaDeLaEmpresaYNoLaFechaUtc));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);
        var idPago = await PagoDeTesoreriaAsync(ctx, 300m);

        // 01:00 UTC del día 10 es el día 9 a las 22:00 en la zona de la empresa (UTC-3).
        await using (var siembra = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var instante = new DateTimeOffset(2026, 10, 10, 1, 0, 0, TimeSpan.Zero);
            await siembra.Pagos.Where(p => p.Id == idPago).ExecuteUpdateAsync(s => s.SetProperty(p => p.Fecha, instante));
        }

        await CuerpoAsync(await EditarAdminAsync(ctx, idPago, Conversion(ctx, 300m, idPersonal)), HttpStatusCode.OK);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idMovimiento = await db.Pagos.Where(p => p.Id == idPago).Select(p => p.IdMovimientoPersonal).SingleAsync();
        var movimiento = await db.MovimientosPersonal.SingleAsync(m => m.Id == idMovimiento);
        Assert.Equal(new DateOnly(2026, 10, 9), movimiento.Fecha);
    }

    [Fact]
    public async Task UnPagoDeVariasLineasConvertidoBajaElSaldoYAsientaElTotal()
    {
        var ctx = await PrepararAsync(nameof(UnPagoDeVariasLineasConvertidoBajaElSaldoYAsientaElTotal));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);

        int idElectronico;
        await using (var siembra = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var medio = new MedioPago
            {
                IdTenant = ctx.IdTenant, Nombre = "Transferencia-sueldos", Orden = 50,
                Comportamiento = ComportamientoMedioPago.Electronico, AdmiteVuelto = false, RequiereReferencia = false,
                CreatedAt = ahora, UpdatedAt = ahora
            };
            siembra.MediosPago.Add(medio);
            await siembra.SaveChangesAsync();
            idElectronico = medio.Id;
        }

        LineaDePago[] lineas = [new(ctx.IdMedioEfectivo, 60m, null), new(idElectronico, 40m, null)];
        var alta = await ctx.Admin.PostAsJsonAsync(
            "/api/pagos/administracion",
            new SolicitudDePagoDeAdministracionConLineas(
                FechaDelNegocio.Hoy().AddDays(-1), ctx.IdEmpresa, null, CategoriaPago.Sueldos, null, null, "Sueldo suelto",
                null, null, lineas));
        var idPago = (await CuerpoAsync(alta, HttpStatusCode.Created)).GetProperty("id").GetInt32();

        var conversion = new SolicitudDeEdicionDePagoConLineas(
            CategoriaPago.Sueldos, null, null, "", null, null, lineas, idPersonal);
        await CuerpoAsync(await EditarAdminAsync(ctx, idPago, conversion), HttpStatusCode.OK);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var idMovimiento = await db.Pagos.Where(p => p.Id == idPago).Select(p => p.IdMovimientoPersonal).SingleAsync();
        var movimiento = await db.MovimientosPersonal.SingleAsync(m => m.Id == idMovimiento);
        Assert.Equal(-100m, movimiento.Importe);
        Assert.Equal(900m, movimiento.SaldoResultante);
        Assert.Equal(900m, await db.Personal.Where(p => p.Id == idPersonal).Select(p => p.Saldo).SingleAsync());
    }

    [Fact]
    public async Task ConservarElProveedorAlAsignarUnEmpleadoSeRechazaSinEscribirNada()
    {
        var ctx = await PrepararAsync(nameof(ConservarElProveedorAlAsignarUnEmpleadoSeRechazaSinEscribirNada));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);
        var idPago = await PagoDeTesoreriaAsync(ctx, 100m);
        var antes = await FotoAsync(ctx, idPersonal);

        var conProveedor = Conversion(ctx, 100m, idPersonal) with { IdProveedor = 1 };

        Assert.Equal("pago_personal_sin_proveedor", await CodigoAsync(await EditarAdminAsync(ctx, idPago, conProveedor), HttpStatusCode.BadRequest));
        Assert.Equal(antes, await FotoAsync(ctx, idPersonal));
    }

    [Fact]
    public async Task UnPagoEnUnPeriodoConciliadoNoSeConvierteYNoEscribeNada()
    {
        var ctx = await PrepararAsync(nameof(UnPagoEnUnPeriodoConciliadoNoSeConvierteYNoEscribeNada));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);
        var idPago = await PagoDeTesoreriaAsync(ctx, 100m);

        await using (var siembra = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var idUsuario = await siembra.Usuarios.Select(u => u.Id).FirstAsync();
            siembra.ConciliacionesTesoreria.Add(new Ways.Domain.Caja.ConciliacionTesoreria
            {
                IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Hasta = DateTimeOffset.UtcNow.AddMinutes(-1),
                IdUsuario = idUsuario, CreadoEl = DateTimeOffset.UtcNow
            });
            await siembra.SaveChangesAsync();
        }

        var antes = await FotoAsync(ctx, idPersonal);

        Assert.Equal(
            "periodo_conciliado",
            await CodigoAsync(await EditarAdminAsync(ctx, idPago, Conversion(ctx, 100m, idPersonal)), HttpStatusCode.Conflict));
        Assert.Equal(antes, await FotoAsync(ctx, idPersonal));
    }

    [Fact]
    public async Task ElMovimientoDeTesoreriaDelPagoTomaElConceptoDelPagoDePersonal()
    {
        var ctx = await PrepararAsync(nameof(ElMovimientoDeTesoreriaDelPagoTomaElConceptoDelPagoDePersonal));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 1000m);
        var idPago = await PagoDeTesoreriaAsync(ctx, 300m);

        await using (var antes = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            var original = await antes.MovimientosTesoreria.Where(m => m.IdPago == idPago).Select(m => m.Concepto).ToListAsync();
            Assert.NotEmpty(original);
            Assert.All(original, c => Assert.Equal("Sueldo suelto", c));
        }

        await CuerpoAsync(await EditarAdminAsync(ctx, idPago, Conversion(ctx, 300m, idPersonal)), HttpStatusCode.OK);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var conceptos = await db.MovimientosTesoreria.Where(m => m.IdPago == idPago).Select(m => m.Concepto).ToListAsync();
        Assert.NotEmpty(conceptos);
        Assert.All(conceptos, c => Assert.Equal("Pago de personal: Ana", c));
    }

    [Fact]
    public async Task DosConversionesSimultaneasDelMismoPagoDejanUnSoloAsientoYUnSoloDescuento()
    {
        var ctx = await PrepararAsync(nameof(DosConversionesSimultaneasDelMismoPagoDejanUnSoloAsientoYUnSoloDescuento));
        var idPersonal = await CrearPersonalAsync(ctx.Admin, "Ana", saldoInicial: 10_000m);
        var idOtra = await CrearPersonalAsync(ctx.Admin, "Beto");
        var antes = await FotoAsync(ctx, idPersonal);

        const int rondas = 6;
        for (var i = 0; i < rondas; i++)
        {
            var idPago = await PagoDeTesoreriaAsync(ctx, 100m);
            var respuestas = await Task.WhenAll(
                EditarAdminAsync(ctx, idPago, Conversion(ctx, 100m, idPersonal)),
                EditarAdminAsync(ctx, idPago, Conversion(ctx, 100m, idOtra)));

            Assert.Single(respuestas, r => r.StatusCode == HttpStatusCode.OK);
            var perdedora = respuestas.Single(r => r.StatusCode != HttpStatusCode.OK);
            Assert.Equal("gasto_de_pago_de_personal", await CodigoAsync(perdedora, HttpStatusCode.Conflict));
        }

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var descontado = await db.Personal.Where(p => p.Id == idPersonal || p.Id == idOtra).SumAsync(p => p.Saldo);
        Assert.Equal(antes.Saldo - (100m * rondas), descontado);
        Assert.Equal(antes.Asientos + rondas, await db.MovimientosPersonal.CountAsync());
        Assert.Equal(rondas, await db.Pagos.CountAsync(p => p.IdMovimientoPersonal != null));
    }
}
