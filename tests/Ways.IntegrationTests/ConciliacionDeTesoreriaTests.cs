using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Caja;
using Ways.Application.Catalogos;
using Ways.Application.Pagos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;

namespace Ways.IntegrationTests;

/// <summary>
/// Conciliación de la tesorería y congelamiento del libro contra Postgres real: la marca (404, 400, 409, política), los escritores de
/// pagos y de medios que la respetan, el nuevo instante de un pago administrativo de hoy y el protocolo de locks.
///
/// Evidencia de mutación (mutation-proof-tests), registrada al escribir las pruebas: (1) <c>ReglasDeConciliacion.EstaCongelado</c> con
/// <c>&lt;=</c> cambiado a <c>&lt;</c> hace fallar <see cref="ElLimiteDeLaMarcaEsInclusivo"/> y la prueba de dominio equivalente; (2) quitar
/// el predicado <c>IdEmpresa == idEmpresa</c> de <c>GuardaDeConciliacion.LeerMarcaAsync</c> hace fallar
/// <see cref="LaMarcaDeUnaEmpresaNoCongelaLaDeOtraDelMismoTenant"/>; (3) tomar la fecha del pago en vez de la menor entre la del pago y la del
/// cierre de su turno en <c>ServicioDePagos.EjecutarEscrituraAsync</c> hace fallar <see cref="UnPagoConFechaPosteriorAlCierreDeSuTurnoConciliadoSeRechaza"/>;
/// (4) quitar el <c>FechaDeNegocioAInstante</c> de hoy (medianoche local otra vez) hace fallar <see cref="UnPagoAdministrativoDeHoyTomaLaHoraActualYSigueLibreDespuesDeLaMarca"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ConciliacionDeTesoreriaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string Ruta = "/api/tesoreria/conciliaciones";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Un instante redondeado a milisegundos: sobrevive intacto al ida y vuelta por JSON y por <c>timestamptz</c>.</summary>
    private static DateTimeOffset Instante(TimeSpan desfase)
    {
        var t = DateTimeOffset.UtcNow + desfase;
        return new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, t.Millisecond, TimeSpan.Zero);
    }

    private static Task<HttpResponseMessage> ConciliarAsync(HttpClient cliente, int idEmpresa, DateTimeOffset? hasta, string? nota = null) =>
        cliente.PostAsJsonAsync(Ruta, new SolicitudDeConciliacion(idEmpresa, hasta, nota));

    private static async Task<string?> CodigoAsync(HttpResponseMessage respuesta) =>
        (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString();

    private static async Task<List<ConciliacionTesoreria>> MarcasAsync(ContextoDeTesoreriaDePrueba ctx, int? idEmpresa = null)
    {
        await using var db = ctx.Db();
        var id = idEmpresa ?? ctx.IdEmpresa;
        return await db.ConciliacionesTesoreria.Where(c => c.IdEmpresa == id).OrderBy(c => c.Id).AsNoTracking().ToListAsync();
    }

    private static async Task<LibroDeTesoreria> LibroAsync(HttpClient cliente, int idEmpresa)
    {
        var hasta = DateTimeOffset.UtcNow.AddHours(1);
        var desde = hasta.AddDays(-30);
        var url = $"/api/reportes/libro-tesoreria?idEmpresa={idEmpresa}&desde={Uri.EscapeDataString(desde.ToString("O"))}&hasta={Uri.EscapeDataString(hasta.ToString("O"))}";
        var respuesta = await cliente.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<LibroDeTesoreria>(OpcionesJson))!;
    }

    private static async Task<int> MedioEfectivoAsync(ContextoDeTesoreriaDePrueba ctx)
    {
        await using var db = ctx.Db();
        return await db.MediosPago.Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
    }

    private static SolicitudDePagoDeAdministracionConLineas Alta(
        int idEmpresa, DateOnly fecha, int idMedio, decimal importe = 100m, string concepto = "Gasto") =>
        PagoDeUnMedio.Administracion(fecha, idEmpresa, null, CategoriaPago.Otros, null, null, concepto, null, idMedio, null, importe);

    private static async Task<int> AltaDeAdminAsync(ContextoDeTesoreriaDePrueba ctx, DateOnly fecha, int idMedio, int? idEmpresa = null)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync("/api/pagos/administracion", Alta(idEmpresa ?? ctx.IdEmpresa, fecha, idMedio));
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, await respuesta.Content.ReadAsStringAsync());
        return (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static SolicitudDeEdicionDePagoConLineas Edicion(int idMedio, decimal importe = 150m) =>
        new(CategoriaPago.Otros, null, null, "Editado", null, null, [new LineaDePago(idMedio, importe, null)]);

    private static async Task<int> SembrarPagoAsync(
        ContextoDeTesoreriaDePrueba ctx, int idEmpresa, int idMedio, DateTimeOffset fecha, OrigenFondosPago origen = OrigenFondosPago.Tesoreria,
        int? idTurno = null, int? idPuntoVenta = null)
    {
        await using var db = ctx.Db();
        var pago = new Pago
        {
            IdTenant = ctx.IdTenant, Fecha = fecha, IdEmpresa = idEmpresa, IdPuntoVenta = idPuntoVenta, IdTurnoCaja = idTurno,
            IdEmpleado = ctx.IdUsuarioAdmin, Categoria = CategoriaPago.Otros, OrigenFondos = origen, Concepto = "Sembrado", Importe = 100m,
            CreatedAt = fecha, UpdatedAt = fecha
        };
        db.Pagos.Add(pago);
        await db.SaveChangesAsync();
        db.ItemsPago.Add(new ItemPago
        {
            IdTenant = ctx.IdTenant, IdPago = pago.Id, IdMedioPago = idMedio, Importe = 100m, CreatedAt = fecha, UpdatedAt = fecha
        });
        await db.SaveChangesAsync();
        return pago.Id;
    }

    private static async Task<int> SembrarTurnoAsync(
        ContextoDeTesoreriaDePrueba ctx, int idPuntoVenta, DateTimeOffset apertura, DateTimeOffset? cierre, int idMedioEfectivo)
    {
        await using var db = ctx.Db();
        var turno = new TurnoCaja
        {
            IdTenant = ctx.IdTenant, IdPuntoVenta = idPuntoVenta, IdEmpleadoApertura = ctx.IdUsuarioAdmin,
            IdEmpleadoCierre = cierre is null ? null : ctx.IdUsuarioAdmin, FechaApertura = apertura, FechaCierre = cierre, FondoInicial = 0m,
            Estado = cierre is null ? EstadoTurno.Abierto : EstadoTurno.Cerrado,
            IdMedioPagoEfectivo = cierre is null ? null : idMedioEfectivo, CreatedAt = apertura, UpdatedAt = apertura
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();
        return turno.Id;
    }

    private static async Task<int> PuntoVentaAsync(ContextoDeTesoreriaDePrueba ctx)
    {
        await using var db = ctx.Db();
        return await db.PuntosVenta.Where(p => p.IdEmpresa == ctx.IdEmpresa).Select(p => p.Id).FirstAsync();
    }

    private static async Task<int> CrearEmpresaAsync(ContextoDeTesoreriaDePrueba ctx, string nombre)
    {
        await using var db = ctx.Db();
        var ahora = DateTimeOffset.UtcNow;
        var empresa = new Empresa { IdTenant = ctx.IdTenant, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();
        return empresa.Id;
    }

    // ---- la marca ----------------------------------------------------------------------------------

    [Fact]
    public async Task UnaMarcaValidaSeGuardaYResponde201ConElLibroQueLaInformaYUnaPosteriorLaReemplaza()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcHappy");
        Assert.Null((await LibroAsync(ctx.Admin, ctx.IdEmpresa)).ConciliadoHasta);
        var primera = Instante(TimeSpan.FromHours(-2));
        var segunda = Instante(TimeSpan.FromMinutes(-1));

        var respuesta = await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, primera, "  Revisado con el contador  ");

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoDeConciliacion>(OpcionesJson))!;
        var fila = Assert.Single(await MarcasAsync(ctx));
        Assert.Equal((fila.Id, ctx.IdEmpresa, primera, "Revisado con el contador"), (resultado.IdConciliacion, resultado.IdEmpresa, resultado.Hasta, resultado.Nota));
        Assert.Equal((ctx.IdUsuarioAdmin, primera), (fila.IdUsuario, fila.Hasta));
        Assert.Equal(primera, (await LibroAsync(ctx.Admin, ctx.IdEmpresa)).ConciliadoHasta);

        Assert.Equal(HttpStatusCode.Created, (await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, segunda)).StatusCode);
        Assert.Equal(segunda, (await LibroAsync(ctx.Supervisor, ctx.IdEmpresa)).ConciliadoHasta);
        Assert.Equal(2, (await MarcasAsync(ctx)).Count);
    }

    [Fact]
    public async Task UnaEmpresaAjenaOInexistenteDa404AntesQueLaValidacionDelCuerpo()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "Conc404A");
        var ajeno = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "Conc404B");

        var delOtroTenant = await ConciliarAsync(ctx.Admin, ajeno.IdEmpresa, null, new string('x', 600));
        var inexistente = await ctx.Admin.PostAsJsonAsync(Ruta, new { idEmpresa = 999999, nota = new string('x', 600) });

        Assert.Equal(HttpStatusCode.NotFound, delOtroTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
        Assert.Empty(await MarcasAsync(ajeno));
    }

    [Fact]
    public async Task UnCuerpoInvalidoDa400ConSuCodigoYNoEscribeNada()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "Conc400");

        var sinHasta = await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, null);
        var notaLarga = await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, Instante(TimeSpan.FromMinutes(-5)), new string('x', 501));
        var notaJusta = await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, Instante(TimeSpan.FromMinutes(-5)), new string('x', 500));

        Assert.Equal((HttpStatusCode.BadRequest, "hasta_requerido"), (sinHasta.StatusCode, await CodigoAsync(sinHasta)));
        Assert.Equal((HttpStatusCode.BadRequest, "nota_muy_larga"), (notaLarga.StatusCode, await CodigoAsync(notaLarga)));
        Assert.Equal(HttpStatusCode.Created, notaJusta.StatusCode);
        Assert.Single(await MarcasAsync(ctx));
    }

    [Fact]
    public async Task UnaMarcaFuturaOQueNoSuperaLaVigenteDa409YNoEscribeNada()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "Conc409");
        var vigente = Instante(TimeSpan.FromHours(-3));
        Assert.Equal(HttpStatusCode.Created, (await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, vigente)).StatusCode);

        var futura = await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, Instante(TimeSpan.FromMinutes(10)));
        var igual = await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, vigente);
        var anterior = await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, vigente.AddHours(-1));

        Assert.Equal((HttpStatusCode.Conflict, "conciliacion_futura"), (futura.StatusCode, await CodigoAsync(futura)));
        Assert.Equal((HttpStatusCode.Conflict, "conciliacion_anterior"), (igual.StatusCode, await CodigoAsync(igual)));
        Assert.Equal((HttpStatusCode.Conflict, "conciliacion_anterior"), (anterior.StatusCode, await CodigoAsync(anterior)));
        Assert.Single(await MarcasAsync(ctx));
    }

    [Fact]
    public async Task UnTurnoAbiertoQueEmpezoAntesDelInstanteImpideConciliarYUnoPosteriorNo()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcTurnoAbierto");
        await SembrarTurnoAsync(ctx, await PuntoVentaAsync(ctx), Instante(TimeSpan.FromHours(-2)), null, await MedioEfectivoAsync(ctx));

        var despuesDeAbrir = await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, Instante(TimeSpan.FromHours(-1)));
        var exactoAlAbrir = await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, Instante(TimeSpan.FromHours(-2)));
        var antesDeAbrir = await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, Instante(TimeSpan.FromHours(-3)));

        Assert.Equal((HttpStatusCode.Conflict, "conciliacion_con_turno_abierto"), (despuesDeAbrir.StatusCode, await CodigoAsync(despuesDeAbrir)));
        Assert.Equal(HttpStatusCode.Created, antesDeAbrir.StatusCode);
        Assert.NotEqual(HttpStatusCode.Created, exactoAlAbrir.StatusCode);
        Assert.Single(await MarcasAsync(ctx));
    }

    [Fact]
    public async Task UnTurnoAbiertoDeOtraEmpresaDelMismoTenantNoImpideConciliar()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcTurnoOtraEmpresa");
        var otra = await CrearEmpresaAsync(ctx, "Otra empresa");
        int idPuntoVentaAjeno;
        await using (var db = ctx.Db())
        {
            var ahora = DateTimeOffset.UtcNow;
            var pv = new PuntoVenta { IdTenant = ctx.IdTenant, IdEmpresa = otra, Nombre = "Local ajeno", CreatedAt = ahora, UpdatedAt = ahora };
            db.PuntosVenta.Add(pv);
            await db.SaveChangesAsync();
            idPuntoVentaAjeno = pv.Id;
        }

        await SembrarTurnoAsync(ctx, idPuntoVentaAjeno, Instante(TimeSpan.FromHours(-2)), null, await MedioEfectivoAsync(ctx));

        Assert.Equal(HttpStatusCode.Created, (await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, Instante(TimeSpan.FromHours(-1)))).StatusCode);
    }

    [Fact]
    public async Task SoloElAdminConciliaYSupervisorYVendedorRecibenElRechazoDeLaPolitica()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcPolitica");
        var hasta = Instante(TimeSpan.FromMinutes(-5));

        Assert.Equal(HttpStatusCode.Forbidden, (await ConciliarAsync(ctx.Supervisor, ctx.IdEmpresa, hasta)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ConciliarAsync(ctx.Vendedor, ctx.IdEmpresa, hasta)).StatusCode);
        using var anonimo = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await ConciliarAsync(anonimo, ctx.IdEmpresa, hasta)).StatusCode);
        Assert.Empty(await MarcasAsync(ctx));
    }

    // ---- pagos congelados ---------------------------------------------------------------------------

    [Fact]
    public async Task UnPagoAdministrativoDeAntesDeLaMarcaNoSeEditaNiSeDaDeBajaYUnoPosteriorSi()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcEdicion");
        var medio = await MedioEfectivoAsync(ctx);
        var viejo = await AltaDeAdminAsync(ctx, FechaDelNegocio.Hoy().AddDays(-5), medio);
        Assert.Equal(HttpStatusCode.Created, (await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, Instante(TimeSpan.FromMinutes(-2)))).StatusCode);
        var nuevo = await AltaDeAdminAsync(ctx, FechaDelNegocio.Hoy(), medio);

        var edicion = await ctx.Admin.PutAsJsonAsync($"/api/pagos/administracion/{viejo}", Edicion(medio));
        var baja = await ctx.Admin.DeleteAsync($"/api/pagos/administracion/{viejo}");

        Assert.Equal((HttpStatusCode.Conflict, "periodo_conciliado"), (edicion.StatusCode, await CodigoAsync(edicion)));
        Assert.Equal((HttpStatusCode.Conflict, "periodo_conciliado"), (baja.StatusCode, await CodigoAsync(baja)));
        await using (var db = ctx.Db())
        {
            var intacto = await db.Pagos.AsNoTracking().SingleAsync(p => p.Id == viejo);
            Assert.Equal(("Gasto", 100m), (intacto.Concepto, intacto.Importe));
        }

        Assert.Equal(HttpStatusCode.OK, (await ctx.Admin.PutAsJsonAsync($"/api/pagos/administracion/{nuevo}", Edicion(medio))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ctx.Admin.DeleteAsync($"/api/pagos/administracion/{nuevo}")).StatusCode);
    }

    [Fact]
    public async Task ElLimiteDeLaMarcaEsInclusivo()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcLimite");
        var medio = await MedioEfectivoAsync(ctx);
        var limite = Instante(TimeSpan.FromHours(-4));
        var enElLimite = await SembrarPagoAsync(ctx, ctx.IdEmpresa, medio, limite);
        var unMilisegundoDespues = await SembrarPagoAsync(ctx, ctx.IdEmpresa, medio, limite.AddMilliseconds(1));
        Assert.Equal(HttpStatusCode.Created, (await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, limite)).StatusCode);

        var congelado = await ctx.Admin.PutAsJsonAsync($"/api/pagos/administracion/{enElLimite}", Edicion(medio));
        var libre = await ctx.Admin.PutAsJsonAsync($"/api/pagos/administracion/{unMilisegundoDespues}", Edicion(medio));

        Assert.Equal((HttpStatusCode.Conflict, "periodo_conciliado"), (congelado.StatusCode, await CodigoAsync(congelado)));
        Assert.Equal(HttpStatusCode.OK, libre.StatusCode);
    }

    [Fact]
    public async Task UnPagoConFechaPosteriorAlCierreDeSuTurnoConciliadoSeRechaza()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcCierre");
        var medio = await MedioEfectivoAsync(ctx);
        var idPuntoVenta = await PuntoVentaAsync(ctx);
        var cierre = Instante(TimeSpan.FromHours(-3));
        var turno = await SembrarTurnoAsync(ctx, idPuntoVenta, cierre.AddHours(-8), cierre, medio);
        var pago = await SembrarPagoAsync(ctx, ctx.IdEmpresa, medio, cierre.AddHours(1), OrigenFondosPago.CajaTurno, turno, idPuntoVenta);
        Assert.Equal(HttpStatusCode.Created, (await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, cierre.AddMinutes(30))).StatusCode);

        var respuesta = await ctx.Admin.PutAsJsonAsync($"/api/pagos/administracion/{pago}", Edicion(medio));

        Assert.Equal((HttpStatusCode.Conflict, "periodo_conciliado"), (respuesta.StatusCode, await CodigoAsync(respuesta)));
    }

    [Fact]
    public async Task UnaAltaAdministrativaConFechaPasadaDespuesDeLaMarcaSeRechazaYNoEscribeNada()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcAltaPasada");
        var medio = await MedioEfectivoAsync(ctx);
        Assert.Equal(HttpStatusCode.Created, (await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, Instante(TimeSpan.FromMinutes(-2)))).StatusCode);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/pagos/administracion", Alta(ctx.IdEmpresa, FechaDelNegocio.Hoy().AddDays(-2), medio));

        Assert.Equal((HttpStatusCode.Conflict, "periodo_conciliado"), (respuesta.StatusCode, await CodigoAsync(respuesta)));
        await using var db = ctx.Db();
        Assert.Empty(await db.Pagos.Where(p => p.IdEmpresa == ctx.IdEmpresa).ToListAsync());
        Assert.Empty(await db.MovimientosTesoreria.Where(m => m.IdEmpresa == ctx.IdEmpresa).ToListAsync());
    }

    [Fact]
    public async Task UnPagoAdministrativoDeHoyTomaLaHoraActualYSigueLibreDespuesDeLaMarca()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcAltaHoy");
        var medio = await MedioEfectivoAsync(ctx);
        var marca = Instante(TimeSpan.FromMinutes(-2));
        Assert.Equal(HttpStatusCode.Created, (await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, marca)).StatusCode);
        var antes = DateTimeOffset.UtcNow.AddSeconds(-1);

        var id = await AltaDeAdminAsync(ctx, FechaDelNegocio.Hoy(), medio);

        await using var db = ctx.Db();
        var fecha = (await db.Pagos.AsNoTracking().SingleAsync(p => p.Id == id)).Fecha;
        Assert.InRange(fecha, antes, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(fecha > marca);
    }

    [Fact]
    public async Task UnPagoAdministrativoDeUnDiaAnteriorSigueFechadoALaMedianocheLocal()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcMedianoche");
        var ayer = FechaDelNegocio.Hoy().AddDays(-1);

        var id = await AltaDeAdminAsync(ctx, ayer, await MedioEfectivoAsync(ctx));

        await using var db = ctx.Db();
        var fecha = (await db.Pagos.AsNoTracking().SingleAsync(p => p.Id == id)).Fecha;
        var zona = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
        Assert.Equal(ayer.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.ConvertTime(fecha, zona).DateTime);
    }

    [Fact]
    public async Task LaMarcaDeUnaEmpresaNoCongelaLaDeOtraDelMismoTenant()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcDosEmpresas");
        var medio = await MedioEfectivoAsync(ctx);
        var otra = await CrearEmpresaAsync(ctx, "Otra empresa");
        var viejoDeOtra = await SembrarPagoAsync(ctx, otra, medio, Instante(TimeSpan.FromDays(-5)));
        Assert.Equal(HttpStatusCode.Created, (await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, Instante(TimeSpan.FromMinutes(-2)))).StatusCode);

        var edicion = await ctx.Admin.PutAsJsonAsync($"/api/pagos/administracion/{viejoDeOtra}", Edicion(medio));
        var altaPasada = await ctx.Admin.PostAsJsonAsync("/api/pagos/administracion", Alta(otra, FechaDelNegocio.Hoy().AddDays(-3), medio));

        Assert.Equal(HttpStatusCode.OK, edicion.StatusCode);
        Assert.Equal(HttpStatusCode.Created, altaPasada.StatusCode);
        Assert.Null((await LibroAsync(ctx.Admin, otra)).ConciliadoHasta);
    }

    // ---- el contrapeso: ajustes nuevos --------------------------------------------------------------

    [Fact]
    public async Task UnAjusteManualDeEfectivoYUnoDeCuentaDespuesDeLaMarcaSeEscribenYEntranAlLibro()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcAjustes");
        int idCuenta;
        await using (var db = ctx.Db())
        {
            var ahora = DateTimeOffset.UtcNow;
            var cuenta = new CuentaTesoreria { IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = "Banco", SaldoInicial = 0m, CreatedAt = ahora, UpdatedAt = ahora };
            db.CuentasTesoreria.Add(cuenta);
            await db.SaveChangesAsync();
            idCuenta = cuenta.Id;
        }

        Assert.Equal(HttpStatusCode.Created, (await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, Instante(TimeSpan.FromMinutes(-2)))).StatusCode);

        var efectivo = await ctx.Admin.PostAsJsonAsync("/api/tesoreria/efectivo/ajustes", new SolicitudDeAjusteDeEfectivo(ctx.IdEmpresa, "Corrección", 75m));
        var cuentaAjustada = await ctx.Admin.PostAsJsonAsync($"/api/cuentas-tesoreria/{idCuenta}/ajustes", new SolicitudDeAjusteDeCuenta("Corrección", -20m, null));

        Assert.Equal(HttpStatusCode.Created, efectivo.StatusCode);
        Assert.Equal(HttpStatusCode.Created, cuentaAjustada.StatusCode);
        var libro = await LibroAsync(ctx.Admin, ctx.IdEmpresa);
        Assert.Equal(75m, libro.SaldoActual.Efectivo);
        Assert.Equal(-20m, libro.SaldoActual.PorCuenta.Single(c => c.IdCuentaTesoreria == idCuenta).Importe);
        Assert.All(libro.Filas, f => Assert.True(f.Fecha > libro.ConciliadoHasta));
    }

    // ---- medios de pago -----------------------------------------------------------------------------

    private static async Task<(int IdMedio, int IdCuentaA, int IdCuentaB)> SembrarMedioConCuentaAsync(ContextoDeTesoreriaDePrueba ctx, string nombre)
    {
        await using var db = ctx.Db();
        var ahora = DateTimeOffset.UtcNow;
        var a = new CuentaTesoreria { IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = $"{nombre} A", CreatedAt = ahora, UpdatedAt = ahora };
        var b = new CuentaTesoreria { IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = $"{nombre} B", CreatedAt = ahora, UpdatedAt = ahora };
        db.CuentasTesoreria.AddRange(a, b);
        await db.SaveChangesAsync();
        var medio = new MedioPago
        {
            IdTenant = ctx.IdTenant, Nombre = nombre, Orden = 9, Comportamiento = ComportamientoMedioPago.Electronico, IdCuentaTesoreria = a.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medio);
        await db.SaveChangesAsync();
        return (medio.Id, a.Id, b.Id);
    }

    private static Dictionary<string, object?> CuerpoDeMedio(string nombre, int? idCuenta, string comportamiento = "Electronico")
    {
        var cuerpo = new Dictionary<string, object?>
        {
            ["nombre"] = nombre, ["idEmpresa"] = null, ["orden"] = 9, ["comportamiento"] = comportamiento, ["admiteVuelto"] = false,
            ["requiereReferencia"] = false, ["recargoPorcentaje"] = null
        };
        if (idCuenta is { } id)
        {
            cuerpo["idCuentaTesoreria"] = id;
        }

        return cuerpo;
    }

    [Fact]
    public async Task ReasignarLaCuentaDeUnMedioConPagosConciliadosSeRechazaYSinPagosConciliadosSePermite()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcMedios");
        var (usado, cuentaA, cuentaB) = await SembrarMedioConCuentaAsync(ctx, "Con historia");
        var (libre, _, _) = await SembrarMedioConCuentaAsync(ctx, "Sin historia");
        await SembrarPagoAsync(ctx, ctx.IdEmpresa, usado, Instante(TimeSpan.FromDays(-3)));
        await SembrarPagoAsync(ctx, ctx.IdEmpresa, libre, Instante(TimeSpan.FromMinutes(-1)));
        Assert.Equal(HttpStatusCode.Created, (await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, Instante(TimeSpan.FromMinutes(-5)))).StatusCode);

        var rechazado = await ctx.Admin.PutAsJsonAsync($"/api/catalogos/medios-pago/{usado}", CuerpoDeMedio("Con historia", cuentaB));
        var permitido = await ctx.Admin.PutAsJsonAsync($"/api/catalogos/medios-pago/{libre}", CuerpoDeMedio("Sin historia", cuentaB));

        Assert.Equal((HttpStatusCode.Conflict, "periodo_conciliado"), (rechazado.StatusCode, await CodigoAsync(rechazado)));
        Assert.Equal(HttpStatusCode.OK, permitido.StatusCode);
        await using var db = ctx.Db();
        Assert.Equal(cuentaA, (await db.MediosPago.AsNoTracking().SingleAsync(m => m.Id == usado)).IdCuentaTesoreria);
        Assert.Equal(cuentaB, (await db.MediosPago.AsNoTracking().SingleAsync(m => m.Id == libre)).IdCuentaTesoreria);
    }

    [Fact]
    public async Task LaMarcaDeUnaEmpresaSoloCuentaParaLosPagosDeEsaEmpresaEnUnMedioCompartido()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcMediosOtraEmpresa");
        var otra = await CrearEmpresaAsync(ctx, "Empresa sin marca");
        var (soloDeLaOtra, _, _) = await SembrarMedioConCuentaAsync(ctx, "Solo de la otra");
        var (tambienDeLaConciliada, _, _) = await SembrarMedioConCuentaAsync(ctx, "Tambien de la conciliada");
        await SembrarPagoAsync(ctx, otra, soloDeLaOtra, Instante(TimeSpan.FromDays(-3)));
        await SembrarPagoAsync(ctx, otra, tambienDeLaConciliada, Instante(TimeSpan.FromDays(-3)));
        await SembrarPagoAsync(ctx, ctx.IdEmpresa, tambienDeLaConciliada, Instante(TimeSpan.FromDays(-3)));
        Assert.Equal(HttpStatusCode.Created, (await ConciliarAsync(ctx.Admin, ctx.IdEmpresa, Instante(TimeSpan.FromMinutes(-5)))).StatusCode);

        var permitido = await ctx.Admin.PutAsJsonAsync(
            $"/api/catalogos/medios-pago/{soloDeLaOtra}", CuerpoDeMedio("Solo de la otra", null, "Efectivo"));
        var rechazado = await ctx.Admin.PutAsJsonAsync(
            $"/api/catalogos/medios-pago/{tambienDeLaConciliada}", CuerpoDeMedio("Tambien de la conciliada", null, "Efectivo"));

        Assert.Equal(HttpStatusCode.OK, permitido.StatusCode);
        Assert.Equal((HttpStatusCode.Conflict, "periodo_conciliado"), (rechazado.StatusCode, await CodigoAsync(rechazado)));
    }

    // ---- locks --------------------------------------------------------------------------------------

    private static async Task<(NpgsqlConnection Conexion, NpgsqlTransaction Transaccion)> SostenerLockAsync(
        WaysApiFixture fixture, int idTenant, int idEmpresa, bool exclusivo)
    {
        var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        var transaccion = await conexion.BeginTransactionAsync();
        await using var comando = new NpgsqlCommand(
            exclusivo ? "SELECT pg_advisory_xact_lock($1)" : "SELECT pg_advisory_xact_lock_shared($1)", conexion, transaccion);
        comando.Parameters.Add(new NpgsqlParameter { Value = GuardaDeConciliacion.ClaveDe(idTenant, idEmpresa) });
        await comando.ExecuteNonQueryAsync();
        return (conexion, transaccion);
    }

    private static async Task<bool> SigueEsperandoAsync(Task tarea)
    {
        var primera = await Task.WhenAny(tarea, Task.Delay(1500));
        return !ReferenceEquals(primera, tarea);
    }

    [Fact]
    public async Task ElEscritorDeLaMarcaEsperaAUnEscritorProtegidoEnCursoYContinuaCuandoTermina()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcLockMarca");
        var (conexion, transaccion) = await SostenerLockAsync(fixture, ctx.IdTenant, ctx.IdEmpresa, exclusivo: false);
        Task<HttpResponseMessage> conciliacion;
        await using (conexion)
        await using (transaccion)
        {
            conciliacion = ConciliarAsync(ctx.Admin, ctx.IdEmpresa, Instante(TimeSpan.FromMinutes(-1)));

            Assert.True(await SigueEsperandoAsync(conciliacion));
            await transaccion.RollbackAsync();
        }

        Assert.Equal(HttpStatusCode.Created, (await conciliacion).StatusCode);
    }

    [Fact]
    public async Task UnEscritorProtegidoEsperaAQueTermineElEscritorDeLaMarca()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcLockEscritor");
        var medio = await MedioEfectivoAsync(ctx);
        var (conexion, transaccion) = await SostenerLockAsync(fixture, ctx.IdTenant, ctx.IdEmpresa, exclusivo: true);
        Task<HttpResponseMessage> alta;
        await using (conexion)
        await using (transaccion)
        {
            alta = ctx.Admin.PostAsJsonAsync("/api/pagos/administracion", Alta(ctx.IdEmpresa, FechaDelNegocio.Hoy(), medio));

            Assert.True(await SigueEsperandoAsync(alta));
            await transaccion.RollbackAsync();
        }

        Assert.Equal(HttpStatusCode.Created, (await alta).StatusCode);
    }

    [Fact]
    public async Task UnLockDeConciliacionDeOtraEmpresaNoBloqueaAlEscritorDeEstaEmpresa()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "ConcLockAislado");
        var medio = await MedioEfectivoAsync(ctx);
        var otra = await CrearEmpresaAsync(ctx, "Otra empresa");
        var (conexion, transaccion) = await SostenerLockAsync(fixture, ctx.IdTenant, otra, exclusivo: true);
        await using (conexion)
        await using (transaccion)
        {
            var alta = await ctx.Admin.PostAsJsonAsync("/api/pagos/administracion", Alta(ctx.IdEmpresa, FechaDelNegocio.Hoy(), medio));

            Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
            await transaccion.RollbackAsync();
        }
    }
}
