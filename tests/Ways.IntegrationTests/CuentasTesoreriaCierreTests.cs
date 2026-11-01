using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Pagos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Pagos;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Lo cobrado con cada medio electrónico entra a su cuenta de tesorería, neto de comisión y retenciones, al cerrar el
/// turno: los dos modos de cierre, la cadena de la cuenta, los medios que no acreditan, la atomicidad con la tesorería de
/// efectivo, el recálculo de un turno cerrado, el lock de la cuenta y la idempotencia.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentasTesoreriaCierreTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdEmpleado, int IdCliente, int IdTipoComprobante,
        int IdMedioEfectivo, HttpClient Admin);

    private long _numero = 1;

    private async Task<Contexto> PrepararAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var alta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var resultado = (await alta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync(
            "/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, loginAdmin.StatusCode);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));
        var idEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
        var idCliente = await db.Clientes.Select(c => c.Id).FirstAsync();
        var idTipo = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idCliente, idTipo,
            idEfectivo, admin);
    }

    private WaysDbContext Db(Contexto ctx) =>
        fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));

    private async Task<int> CrearCuentaAsync(
        Contexto ctx, string nombre, decimal saldoInicial = 0m, bool activo = true, int? idEmpresa = null)
    {
        await using var db = Db(ctx);
        var ahora = DateTimeOffset.UtcNow;
        var cuenta = new CuentaTesoreria
        {
            IdTenant = ctx.IdTenant, IdEmpresa = idEmpresa ?? ctx.IdEmpresa, Nombre = nombre, SaldoInicial = saldoInicial,
            Activo = activo, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.CuentasTesoreria.Add(cuenta);
        await db.SaveChangesAsync();
        return cuenta.Id;
    }

    private async Task<int> CrearMedioAsync(
        Contexto ctx, string nombre, int? idCuenta, decimal comision = 0m, decimal iibb = 0m, decimal iva = 0m,
        decimal ganancias = 0m, ComportamientoMedioPago comportamiento = ComportamientoMedioPago.Electronico)
    {
        await using var db = Db(ctx);
        var ahora = DateTimeOffset.UtcNow;
        var medio = new MedioPago
        {
            IdTenant = ctx.IdTenant, Nombre = nombre, Orden = 60, Comportamiento = comportamiento,
            AdmiteVuelto = false, RequiereReferencia = false, IdCuentaTesoreria = idCuenta,
            ComisionPorcentaje = comision, RetencionIibbPorcentaje = iibb, RetencionIvaPorcentaje = iva,
            RetencionGananciasPorcentaje = ganancias, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medio);
        await db.SaveChangesAsync();
        return medio.Id;
    }

    private static async Task<int> AbrirTurnoAsync(Contexto ctx, int? idPuntoVenta = null)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(idPuntoVenta ?? ctx.IdPuntoVenta, 0m, "Apertura de prueba"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;
    }

    private async Task SembrarPagoAsync(Contexto ctx, int idTurno, int idMedioPago, decimal importe)
    {
        await using var db = Db(ctx);
        var ahora = DateTimeOffset.UtcNow;
        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant, IdTipoComprobante = ctx.IdTipoComprobante, Numero = Interlocked.Increment(ref _numero),
            Fecha = ahora, IdPuntoVenta = ctx.IdPuntoVenta, IdTurnoCaja = idTurno, IdEmpleado = ctx.IdEmpleado,
            IdCliente = ctx.IdCliente, Subtotal = importe, DescuentoTotal = 0m, Total = importe,
            Estado = EstadoComprobante.Emitido, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        db.Cobros.Add(new Cobro
        {
            IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, IdMedioPago = idMedioPago, Importe = importe,
            Vuelto = 0m, CreatedAt = ahora, UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Cierra el turno en el modo pedido y devuelve la respuesta: "clasico" declara el esperado de cada medio;
    /// "retiro" no cuenta nada.</summary>
    private static async Task<HttpResponseMessage> CerrarAsync(Contexto ctx, int idTurno, string modo)
    {
        if (modo == "retiro")
        {
            return await ctx.Admin.PostAsJsonAsync(
                $"/api/caja/turnos/{idTurno}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(0m, null));
        }

        var resumen = await ctx.Admin.GetFromJsonAsync<ResumenDeTurno>($"/api/caja/turnos/{idTurno}/resumen", OpcionesJson);
        var conteos = resumen!.Medios.Select(l => new ConteoDeclarado(l.IdMedioPago, l.ImporteEsperado)).ToList();
        return await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{idTurno}/cierre", new SolicitudDeCierre(conteos, null));
    }

    private static async Task CerrarOkAsync(Contexto ctx, int idTurno, string modo = "retiro")
    {
        var respuesta = await CerrarAsync(ctx, idTurno, modo);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
    }

    private async Task<List<MovimientoCuenta>> LibroAsync(Contexto ctx, int idCuenta)
    {
        await using var db = Db(ctx);
        return await db.MovimientosCuenta.Where(m => m.IdCuentaTesoreria == idCuenta).OrderBy(m => m.Id).ToListAsync();
    }

    private async Task<int> CantidadDeFilasDeCuentaAsync(Contexto ctx)
    {
        await using var db = Db(ctx);
        return await db.MovimientosCuenta.CountAsync();
    }

    private static void AfirmarFila(
        MovimientoCuenta fila, TipoMovimientoCuenta tipo, decimal inicio, decimal ingreso, decimal egreso, decimal final,
        decimal bruto, decimal? porcentaje)
    {
        Assert.Equal(tipo, fila.Tipo);
        Assert.Equal((inicio, ingreso, egreso, final), (fila.Inicio, fila.Ingreso, fila.Egreso, fila.Final));
        Assert.Equal(bruto, fila.BaseBruta);
        Assert.Equal(porcentaje, fila.PorcentajeAplicado);
    }

    private async Task EjecutarCrudoAsync(Contexto ctx, string sql)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var comando = new NpgsqlCommand(sql, conexion);
        await comando.ExecuteNonQueryAsync();
    }

    // ---- el caso completo, en los dos modos de cierre -------------------------------------------

    [Theory]
    [InlineData("clasico")]
    [InlineData("retiro")]
    public async Task UnMedioConLosCuatroPorcentajesAcreditaElCobroNetoEnLaCadenaDeLaCuenta(string modo)
    {
        var ctx = await PrepararAsync($"{nameof(UnMedioConLosCuatroPorcentajesAcreditaElCobroNetoEnLaCadenaDeLaCuenta)}{modo}");
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago", saldoInicial: 1000m);
        var medio = await CrearMedioAsync(ctx, "QR MP", cuenta, comision: 4.5m, iibb: 1.5m, iva: 0.5m, ganancias: 2m);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medio, 6000m);
        await SembrarPagoAsync(ctx, turno, medio, 4000m);
        await SembrarPagoAsync(ctx, turno, ctx.IdMedioEfectivo, 500m);

        await CerrarOkAsync(ctx, turno, modo);

        var libro = await LibroAsync(ctx, cuenta);
        Assert.Equal(5, libro.Count);
        AfirmarFila(libro[0], TipoMovimientoCuenta.Cobro, 1000m, 10000m, 0m, 11000m, 10000m, null);
        AfirmarFila(libro[1], TipoMovimientoCuenta.Comision, 11000m, 0m, 450m, 10550m, 10000m, 4.5m);
        AfirmarFila(libro[2], TipoMovimientoCuenta.RetencionIibb, 10550m, 0m, 150m, 10400m, 10000m, 1.5m);
        AfirmarFila(libro[3], TipoMovimientoCuenta.RetencionIva, 10400m, 0m, 50m, 10350m, 10000m, 0.5m);
        AfirmarFila(libro[4], TipoMovimientoCuenta.RetencionGanancias, 10350m, 0m, 200m, 10150m, 10000m, 2m);

        await using var db = Db(ctx);
        var turnoCerrado = await db.TurnosCaja.SingleAsync(t => t.Id == turno);
        var fechaDeLaTesoreria = await db.MovimientosTesoreria.Where(m => m.IdTurnoCaja == turno).Select(m => m.Fecha).SingleAsync();
        Assert.Equal(fechaDeLaTesoreria, turnoCerrado.FechaCierre!.Value);
        Assert.All(libro, fila =>
        {
            Assert.Equal(ctx.IdTenant, fila.IdTenant);
            Assert.Equal(cuenta, fila.IdCuentaTesoreria);
            Assert.Equal(turno, fila.IdTurnoCaja);
            Assert.Equal(medio, fila.IdMedioPago);
            Assert.Equal(ctx.IdEmpleado, fila.IdEmpleado);
            Assert.Equal(fechaDeLaTesoreria, fila.Fecha);
            Assert.Contains("QR MP", fila.Concepto);
            Assert.Contains($"turno {turno}", fila.Concepto);
        });
    }

    [Fact]
    public async Task LaCadenaContinuaEntreDosTurnosDesdeElSaldoInicialDeLaCuenta()
    {
        var ctx = await PrepararAsync(nameof(LaCadenaContinuaEntreDosTurnosDesdeElSaldoInicialDeLaCuenta));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Banco", saldoInicial: 250m);
        var medio = await CrearMedioAsync(ctx, "Banco Galicia", cuenta, comision: 10m);

        var primero = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, primero, medio, 1000m);
        await CerrarOkAsync(ctx, primero);

        var segundo = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, segundo, medio, 200m);
        await CerrarOkAsync(ctx, segundo, "clasico");

        var libro = await LibroAsync(ctx, cuenta);
        Assert.Equal(4, libro.Count);
        AfirmarFila(libro[0], TipoMovimientoCuenta.Cobro, 250m, 1000m, 0m, 1250m, 1000m, null);
        AfirmarFila(libro[1], TipoMovimientoCuenta.Comision, 1250m, 0m, 100m, 1150m, 1000m, 10m);
        AfirmarFila(libro[2], TipoMovimientoCuenta.Cobro, 1150m, 200m, 0m, 1350m, 200m, null);
        AfirmarFila(libro[3], TipoMovimientoCuenta.Comision, 1350m, 0m, 20m, 1330m, 200m, 10m);
        Assert.Equal([primero, primero, segundo, segundo], libro.Select(f => f.IdTurnoCaja!.Value));
    }

    [Fact]
    public async Task DosMediosDeLaMismaCuentaEncadenanEnOrdenAscendenteDeMedio()
    {
        var ctx = await PrepararAsync(nameof(DosMediosDeLaMismaCuentaEncadenanEnOrdenAscendenteDeMedio));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago", saldoInicial: 0m);
        var primero = await CrearMedioAsync(ctx, "Medio A", cuenta, comision: 5m);
        var segundo = await CrearMedioAsync(ctx, "Medio B", cuenta, iibb: 2m);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, segundo, 300m);
        await SembrarPagoAsync(ctx, turno, primero, 1000m);

        await CerrarOkAsync(ctx, turno);

        var libro = await LibroAsync(ctx, cuenta);
        Assert.Equal(4, libro.Count);
        Assert.Equal([primero, primero, segundo, segundo], libro.Select(f => f.IdMedioPago!.Value));
        AfirmarFila(libro[0], TipoMovimientoCuenta.Cobro, 0m, 1000m, 0m, 1000m, 1000m, null);
        AfirmarFila(libro[1], TipoMovimientoCuenta.Comision, 1000m, 0m, 50m, 950m, 1000m, 5m);
        AfirmarFila(libro[2], TipoMovimientoCuenta.Cobro, 950m, 300m, 0m, 1250m, 300m, null);
        AfirmarFila(libro[3], TipoMovimientoCuenta.RetencionIibb, 1250m, 0m, 6m, 1244m, 300m, 2m);
    }

    // ---- medios que no acreditan ----------------------------------------------------------------

    [Fact]
    public async Task UnMedioElectronicoSinCuentaNoEscribeNadaYElCierreSigue()
    {
        var ctx = await PrepararAsync(nameof(UnMedioElectronicoSinCuentaNoEscribeNadaYElCierreSigue));
        using var _ = ctx.Admin;
        var medio = await CrearMedioAsync(ctx, "Tarjeta sin cuenta", idCuenta: null);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medio, 700m);

        await CerrarOkAsync(ctx, turno, "clasico");

        Assert.Equal(0, await CantidadDeFilasDeCuentaAsync(ctx));
    }

    [Fact]
    public async Task UnaCuentaDadaDeBajaNoRecibeNadaYElCierreSigue()
    {
        var ctx = await PrepararAsync(nameof(UnaCuentaDadaDeBajaNoRecibeNadaYElCierreSigue));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Cuenta vieja");
        var medio = await CrearMedioAsync(ctx, "QR viejo", cuenta, comision: 3m);
        await EjecutarCrudoAsync(ctx, $"UPDATE cuentas_tesoreria SET deleted_at = now() WHERE id_cuenta_tesoreria = {cuenta}");
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medio, 700m);

        await CerrarOkAsync(ctx, turno);

        Assert.Equal(0, await CantidadDeFilasDeCuentaAsync(ctx));
    }

    [Fact]
    public async Task UnaCuentaInactivaIgualRecibeLosMovimientos()
    {
        var ctx = await PrepararAsync(nameof(UnaCuentaInactivaIgualRecibeLosMovimientos));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Cuenta inactiva", activo: false);
        var medio = await CrearMedioAsync(ctx, "QR inactivo", cuenta, comision: 10m);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medio, 100m);

        await CerrarOkAsync(ctx, turno);

        var libro = await LibroAsync(ctx, cuenta);
        Assert.Equal(2, libro.Count);
        Assert.Equal(90m, libro[^1].Final);
    }

    [Fact]
    public async Task UnaCuentaDeOtraEmpresaQueLaDelTurnoNoRecibeNadaYElCierreSigue()
    {
        var ctx = await PrepararAsync(nameof(UnaCuentaDeOtraEmpresaQueLaDelTurnoNoRecibeNadaYElCierreSigue));
        using var _ = ctx.Admin;
        int idOtraEmpresa;
        await using (var plataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            var otra = new Empresa { IdTenant = ctx.IdTenant, RazonSocial = "Otra empresa", CreatedAt = ahora, UpdatedAt = ahora };
            plataforma.Empresas.Add(otra);
            await plataforma.SaveChangesAsync();
            idOtraEmpresa = otra.Id;
        }

        var cuentaAjena = await CrearCuentaAsync(ctx, "Cuenta de la otra", idEmpresa: idOtraEmpresa);
        var cuentaPropia = await CrearCuentaAsync(ctx, "Cuenta propia");
        var medioAjeno = await CrearMedioAsync(ctx, "QR ajeno", cuentaAjena, comision: 3m);
        var medioPropio = await CrearMedioAsync(ctx, "QR propio", cuentaPropia);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medioAjeno, 700m);
        await SembrarPagoAsync(ctx, turno, medioPropio, 50m);

        await CerrarOkAsync(ctx, turno);

        Assert.Empty(await LibroAsync(ctx, cuentaAjena));
        var propia = Assert.Single(await LibroAsync(ctx, cuentaPropia));
        Assert.Equal(50m, propia.Final);
    }

    [Fact]
    public async Task EfectivoYCuentaCorrienteNuncaEscribenFilasDeCuenta()
    {
        var ctx = await PrepararAsync(nameof(EfectivoYCuentaCorrienteNuncaEscribenFilasDeCuenta));
        using var _ = ctx.Admin;
        var cuentaCorriente = await CrearMedioAsync(
            ctx, "Cuenta corriente de prueba", idCuenta: null, comportamiento: ComportamientoMedioPago.CuentaCorriente);
        await CrearCuentaAsync(ctx, "Cuenta sin uso");
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, ctx.IdMedioEfectivo, 400m);
        await SembrarPagoAsync(ctx, turno, cuentaCorriente, 900m);

        await CerrarOkAsync(ctx, turno);

        Assert.Equal(0, await CantidadDeFilasDeCuentaAsync(ctx));
    }

    // ---- bruto negativo, cero y sin actividad ---------------------------------------------------

    /// <summary>Un importe de pago nunca es negativo (<c>ck_cobros_importe_no_negativo</c>) y una nota de crédito no
    /// lleva pagos, así que el cierre no llega hoy a un bruto negativo: el escritor se ejercita directamente, con la
    /// actividad que daría un bruto neto negativo, dentro de una transacción real.</summary>
    [Fact]
    public async Task ElEscritorConUnBrutoNegativoDevuelveElCobroPorEgresoYLaComisionYLasRetencionesPorIngreso()
    {
        var ctx = await PrepararAsync(nameof(ElEscritorConUnBrutoNegativoDevuelveElCobroPorEgresoYLaComisionYLasRetencionesPorIngreso));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago", saldoInicial: 5000m);
        var medio = await CrearMedioAsync(ctx, "QR MP", cuenta, comision: 5m, iibb: 2m);
        var turno = await AbrirTurnoAsync(ctx);

        await using (var db = Db(ctx))
        {
            await using var transaccion = await db.Database.BeginTransactionAsync();
            await db.Database.OpenConnectionAsync();
            await EscriturasDeCuentaDeTesoreria.AcreditarAsync(
                db, db.Database.GetDbConnection(), transaccion.GetDbTransaction(), ctx.IdTenant, ctx.IdEmpresa, turno,
                DateTimeOffset.UtcNow, ctx.IdEmpleado, [new ActividadDeMedio(medio, ComportamientoMedioPago.Electronico, -1000m, 0m, 0m, true)],
                CancellationToken.None);
            await transaccion.CommitAsync();
        }

        var libro = await LibroAsync(ctx, cuenta);
        Assert.Equal(3, libro.Count);
        AfirmarFila(libro[0], TipoMovimientoCuenta.Cobro, 5000m, 0m, 1000m, 4000m, -1000m, null);
        AfirmarFila(libro[1], TipoMovimientoCuenta.Comision, 4000m, 50m, 0m, 4050m, -1000m, 5m);
        AfirmarFila(libro[2], TipoMovimientoCuenta.RetencionIibb, 4050m, 20m, 0m, 4070m, -1000m, 2m);
        Assert.StartsWith("Devoluciones netas", libro[0].Concepto);
        Assert.StartsWith("Devolución de comisión", libro[1].Concepto);
    }

    /// <summary>Una nota de crédito (total negativo) no lleva pagos: la API la rechaza con <c>pago_en_comprobante_negativo</c>, así
    /// que su dinero nunca llega al cierre. Sin pagos se acepta y el cierre no la cuenta ni en el arqueo ni en la cuenta.</summary>
    [Fact]
    public async Task UnaNotaDeCreditoConPagosSeRechazaYSinPagosNoTocaLaCuentaNiElArqueo()
    {
        var ctx = await PrepararAsync(nameof(UnaNotaDeCreditoConPagosSeRechazaYSinPagosNoTocaLaCuentaNiElArqueo));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago");
        var medio = await CrearMedioAsync(ctx, "QR MP", cuenta, comision: 10m);
        int idArticulo;
        await using (var db = Db(ctx))
        {
            var ahora = DateTimeOffset.UtcNow;
            var area = new Area { IdTenant = ctx.IdTenant, Nombre = "Area NCX", Orden = 1, CreatedAt = ahora, UpdatedAt = ahora };
            db.Areas.Add(area);
            await db.SaveChangesAsync();
            var articulo = new Articulo
            {
                IdTenant = ctx.IdTenant, CodigoInterno = $"ncx-{Guid.NewGuid():N}", Nombre = "Articulo NCX", IdArea = area.Id,
                IdAlicuotaIva = await db.AlicuotasIva.Select(a => a.Id).FirstAsync(), UnidadVenta = UnidadVenta.Unidad,
                EsProducto = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Articulos.Add(articulo);
            await db.SaveChangesAsync();
            var idLista = await db.Clientes.Where(c => c.Id == ctx.IdCliente).Select(c => c.IdListaPrecio).FirstAsync();
            db.Precios.Add(new Precio
            {
                IdTenant = ctx.IdTenant, IdArticulo = articulo.Id, IdListaPrecio = idLista, Monto = 100m,
                VigenteDesde = ahora.AddDays(-1), VigenteHasta = null, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
            idArticulo = articulo.Id;
        }

        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medio, 200m);

        var conPagos = await ctx.Admin.PostAsJsonAsync(
            "/api/ventas",
            new SolicitudDeVenta(
                ctx.IdPuntoVenta, ctx.IdCliente, "NCX", null, [new LineaDeVenta(idArticulo, 1m, null)],
                [new PagoDeVenta(medio, 100m, null, 0m)], null, null));
        Assert.Equal(HttpStatusCode.BadRequest, conPagos.StatusCode);
        Assert.Equal("pago_en_comprobante_negativo", await CodigoAsync(conPagos));

        var sinPagos = await ctx.Admin.PostAsJsonAsync(
            "/api/ventas",
            new SolicitudDeVenta(
                ctx.IdPuntoVenta, ctx.IdCliente, "NCX", null, [new LineaDeVenta(idArticulo, 1m, null)], [], null, null));
        Assert.Equal(HttpStatusCode.Created, sinPagos.StatusCode);

        await CerrarOkAsync(ctx, turno);

        var libro = await LibroAsync(ctx, cuenta);
        Assert.Equal(2, libro.Count);
        AfirmarFila(libro[0], TipoMovimientoCuenta.Cobro, 0m, 200m, 0m, 200m, 200m, null);
        AfirmarFila(libro[1], TipoMovimientoCuenta.Comision, 200m, 0m, 20m, 180m, 200m, 10m);

        await using var dbArqueo = Db(ctx);
        Assert.Equal(200m, await dbArqueo.ArqueosTurno
            .Where(a => a.IdTurnoCaja == turno && a.IdMedioPago == medio).Select(a => a.ImporteEsperado).SingleAsync());
    }

    private static async Task<string?> CodigoAsync(HttpResponseMessage respuesta) =>
        (await respuesta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString();

    [Fact]
    public async Task UnTurnoSinPagosDigitalesOConUnPagoDeCeroNoEscribeFilas()
    {
        var ctx = await PrepararAsync(nameof(UnTurnoSinPagosDigitalesOConUnPagoDeCeroNoEscribeFilas));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago");
        await CrearMedioAsync(ctx, "QR sin ventas", cuenta, comision: 5m);
        var conPagoCero = await CrearMedioAsync(ctx, "QR pago cero", cuenta, comision: 5m);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, conPagoCero, 0m);
        await SembrarPagoAsync(ctx, turno, ctx.IdMedioEfectivo, 100m);

        await CerrarOkAsync(ctx, turno);

        Assert.Empty(await LibroAsync(ctx, cuenta));
    }

    // ---- atomicidad, recálculo e idempotencia ---------------------------------------------------

    [Fact]
    public async Task SiFallaLaEscrituraDeLasFilasDeCuentaElTurnoSigueAbiertoYNoQuedaNadaEscrito()
    {
        var ctx = await PrepararAsync(nameof(SiFallaLaEscrituraDeLasFilasDeCuentaElTurnoSigueAbiertoYNoQuedaNadaEscrito));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago");
        var medio = await CrearMedioAsync(ctx, "QR MP", cuenta, comision: 5m);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medio, 1000m);

        var falla = new FallaAlInsertarEnMovimientosCuenta();
        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(falla))
        {
            respuesta = await CerrarAsync(ctx, turno, "retiro");
        }

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        // La falla es la inducida, no otra: se inyectó en el INSERT de la cuenta, y la fila de tesorería de efectivo ya se había
        // escrito en la misma transacción (el INSERT corrió) antes de que se deshiciera todo.
        Assert.True(falla.Inducida);
        Assert.True(falla.InsertoEnTesoreriaDeEfectivoAntes);
        await using var db = Db(ctx);
        Assert.Equal(EstadoTurno.Abierto, await db.TurnosCaja.Where(t => t.Id == turno).Select(t => t.Estado).SingleAsync());
        Assert.Equal(0, await db.ArqueosTurno.CountAsync(a => a.IdTurnoCaja == turno));
        Assert.Equal(0, await db.MovimientosTesoreria.CountAsync(m => m.IdTurnoCaja == turno));
        Assert.Equal(0, await db.MovimientosCuenta.CountAsync());
    }

    /// <summary>Falla el primer INSERT en <c>movimientos_cuenta</c> sin tocar la base (ni permisos globales) y registra que antes
    /// corrió el INSERT en <c>movimientos_tesoreria</c>.</summary>
    private sealed class FallaAlInsertarEnMovimientosCuenta : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public bool Inducida { get; private set; }

        public bool InsertoEnTesoreriaDeEfectivoAntes { get; private set; }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO movimientos_tesoreria (", StringComparison.Ordinal))
            {
                InsertoEnTesoreriaDeEfectivoAntes = true;
            }

            if (command.CommandText.Contains("INSERT INTO movimientos_cuenta (", StringComparison.Ordinal))
            {
                Inducida = true;
                throw new InvalidOperationException("Falla inducida por la prueba al insertar en movimientos_cuenta.");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task UnCierreCuyoMedioApuntaALaCuentaDeOtraEmpresaNoEsperaEseLock()
    {
        var ctx = await PrepararAsync(nameof(UnCierreCuyoMedioApuntaALaCuentaDeOtraEmpresaNoEsperaEseLock));
        using var _ = ctx.Admin;
        int idOtraEmpresa;
        await using (var plataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            var otra = new Empresa { IdTenant = ctx.IdTenant, RazonSocial = "Otra empresa", CreatedAt = ahora, UpdatedAt = ahora };
            plataforma.Empresas.Add(otra);
            await plataforma.SaveChangesAsync();
            idOtraEmpresa = otra.Id;
        }

        var cuentaAjena = await CrearCuentaAsync(ctx, "Cuenta de la otra", idEmpresa: idOtraEmpresa);
        var medio = await CrearMedioAsync(ctx, "QR ajeno", cuentaAjena, comision: 3m);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medio, 700m);

        await using var conexionAbm = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var transaccionAbm = await conexionAbm.BeginTransactionAsync();
        await using (var bloquear = new NpgsqlCommand(
            $"SELECT 1 FROM cuentas_tesoreria WHERE id_cuenta_tesoreria = {cuentaAjena} FOR UPDATE", conexionAbm, transaccionAbm))
        {
            await bloquear.ExecuteScalarAsync();
        }

        var cierre = CerrarAsync(ctx, turno, "retiro");
        var primero = await Task.WhenAny(cierre, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(cierre, primero);
        Assert.Equal(HttpStatusCode.OK, (await cierre).StatusCode);
        await transaccionAbm.RollbackAsync();
        Assert.Empty(await LibroAsync(ctx, cuentaAjena));
    }

    [Fact]
    public async Task ElRecalculoDeUnTurnoCerradoPorUnPagoNoTocaLasFilasDeCuenta()
    {
        var ctx = await PrepararAsync(nameof(ElRecalculoDeUnTurnoCerradoPorUnPagoNoTocaLasFilasDeCuenta));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago", saldoInicial: 100m);
        var medio = await CrearMedioAsync(ctx, "QR MP", cuenta, comision: 5m);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medio, 1000m);

        var pagoRespuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/pagos",
            PagoDeUnMedio.Pos(
                ctx.IdPuntoVenta, CategoriaPago.Otros, null, null, "Gasto original", null, medio, null, 40m,
                origenFondos: OrigenFondosPago.CajaTurno));
        var cuerpoPago = await pagoRespuesta.Content.ReadAsStringAsync();
        Assert.True(pagoRespuesta.StatusCode == HttpStatusCode.Created, cuerpoPago);
        var idPago = JsonDocument.Parse(cuerpoPago).RootElement.GetProperty("id").GetInt32();

        await CerrarOkAsync(ctx, turno, "clasico");
        var antes = await LibroAsync(ctx, cuenta);
        Assert.Equal(2, antes.Count);

        var edicion = await ctx.Admin.PutAsJsonAsync(
            $"/api/pagos/administracion/{idPago}",
            PagoDeUnMedio.Edicion(CategoriaPago.Otros, null, null, "Gasto editado", null, medio, null, 90m));
        Assert.True(edicion.StatusCode == HttpStatusCode.OK, await edicion.Content.ReadAsStringAsync());

        await using (var db = Db(ctx))
        {
            var arqueo = await db.ArqueosTurno.SingleAsync(a => a.IdTurnoCaja == turno && a.IdMedioPago == medio);
            Assert.Equal(910m, arqueo.ImporteEsperado);
        }

        var despues = await LibroAsync(ctx, cuenta);
        Assert.Equal(antes.Count, despues.Count);
        for (var i = 0; i < antes.Count; i++)
        {
            Assert.Equal(
                (antes[i].Id, antes[i].Tipo, antes[i].Inicio, antes[i].Ingreso, antes[i].Egreso, antes[i].Final, antes[i].BaseBruta),
                (despues[i].Id, despues[i].Tipo, despues[i].Inicio, despues[i].Ingreso, despues[i].Egreso, despues[i].Final, despues[i].BaseBruta));
        }
    }

    [Fact]
    public async Task DosCierresConcurrentesDelMismoTurnoAcreditanUnaSolaVezPorLaPrimeraDefensa()
    {
        var ctx = await PrepararAsync(nameof(DosCierresConcurrentesDelMismoTurnoAcreditanUnaSolaVezPorLaPrimeraDefensa));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago");
        var medio = await CrearMedioAsync(ctx, "QR MP", cuenta, comision: 5m);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medio, 1000m);

        var respuestas = await Task.WhenAll(CerrarAsync(ctx, turno, "retiro"), CerrarAsync(ctx, turno, "retiro"));

        Assert.Contains(HttpStatusCode.OK, respuestas.Select(r => r.StatusCode));
        var perdedora = respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problema = await perdedora.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("turno_ya_cerrado", problema.GetProperty("codigo").GetString());
        // El UPDATE guardado por estado rechaza al perdedor antes de que escriba filas de cuenta, así que el índice único
        // no llega a dispararse en esta carrera: lo prueba el test del backstop de más abajo.
        Assert.Equal(2, (await LibroAsync(ctx, cuenta)).Count);
    }

    [Fact]
    public async Task UnaFilaDeCierreRepetidaChocaConElIndiceUnicoYSeTraduceAlMismoConflictoDeTurnoYaCerrado()
    {
        var ctx = await PrepararAsync(nameof(UnaFilaDeCierreRepetidaChocaConElIndiceUnicoYSeTraduceAlMismoConflictoDeTurnoYaCerrado));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago");
        var medio = await CrearMedioAsync(ctx, "QR MP", cuenta);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medio, 1000m);
        await CerrarOkAsync(ctx, turno);

        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var comando = new NpgsqlCommand(
            "INSERT INTO movimientos_cuenta (id_tenant, id_cuenta_tesoreria, fecha, tipo, id_turno_caja, id_medio_pago, " +
            "concepto, inicio, ingreso, egreso, final, id_empleado) " +
            "SELECT id_tenant, id_cuenta_tesoreria, fecha, tipo, id_turno_caja, id_medio_pago, concepto, inicio, ingreso, " +
            "egreso, final, id_empleado FROM movimientos_cuenta WHERE id_turno_caja = $1 AND tipo = 'cobro'",
            conexion);
        comando.Parameters.Add(new NpgsqlParameter { Value = turno });
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());

        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_movimientos_cuenta_cierre", excepcion.ConstraintName);

        var falso = new ProblemDetailsFalso();
        var manejador = new ManejadorDeErrores(falso, NullLogger<ManejadorDeErrores>.Instance);
        var contexto = new DefaultHttpContext();
        var manejado = await manejador.TryHandleAsync(contexto, new DbUpdateException("duplicado", excepcion), CancellationToken.None);

        Assert.True(manejado);
        Assert.Equal(StatusCodes.Status409Conflict, contexto.Response.StatusCode);
        Assert.Equal("turno_ya_cerrado", falso.Ultimo!.ProblemDetails.Extensions["codigo"] as string);
    }

    // ---- locks ----------------------------------------------------------------------------------

    /// <summary>Espera a que haya una sesión bloqueada en un lock cuya consulta contiene el fragmento dado.</summary>
    private async Task EsperarUnaSesionBloqueadaAsync(string fragmentoDeConsulta)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND pid <> pg_backend_pid() " +
            "AND datname = current_database() AND query LIKE $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = $"%{fragmentoDeConsulta}%" });

        var limite = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < limite)
        {
            if ((long)(await comando.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Ninguna sesión con '{fragmentoDeConsulta}' llegó a quedar bloqueada esperando el lock.");
    }

    [Fact]
    public async Task ElCierreEsperaElForUpdateDeLaCuentaYArrancaDelSaldoInicialYaComiteado()
    {
        var ctx = await PrepararAsync(nameof(ElCierreEsperaElForUpdateDeLaCuentaYArrancaDelSaldoInicialYaComiteado));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago", saldoInicial: 0m);
        var medio = await CrearMedioAsync(ctx, "QR MP", cuenta);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medio, 100m);

        await using var conexionAbm = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var transaccionAbm = await conexionAbm.BeginTransactionAsync();
        await using (var bloquear = new NpgsqlCommand(
            $"SELECT 1 FROM cuentas_tesoreria WHERE id_cuenta_tesoreria = {cuenta} FOR UPDATE", conexionAbm, transaccionAbm))
        {
            await bloquear.ExecuteScalarAsync();
        }

        await using (var editar = new NpgsqlCommand(
            $"UPDATE cuentas_tesoreria SET saldo_inicial = 500 WHERE id_cuenta_tesoreria = {cuenta}", conexionAbm, transaccionAbm))
        {
            await editar.ExecuteNonQueryAsync();
        }

        var cierre = CerrarAsync(ctx, turno, "retiro");
        await EsperarUnaSesionBloqueadaAsync("FOR NO KEY UPDATE");
        Assert.False(cierre.IsCompleted);

        await transaccionAbm.CommitAsync();
        var respuesta = await cierre;
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());

        var cobro = Assert.Single(await LibroAsync(ctx, cuenta));
        AfirmarFila(cobro, TipoMovimientoCuenta.Cobro, 500m, 100m, 0m, 600m, 100m, null);
    }

    [Fact]
    public async Task UnCierreQueEsperaElLockNoFechaSusFilasAntesDelAjusteQueComiteaPrimeroYElLibroPorRangoCuadra()
    {
        var ctx = await PrepararAsync(nameof(UnCierreQueEsperaElLockNoFechaSusFilasAntesDelAjusteQueComiteaPrimeroYElLibroPorRangoCuadra));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago");
        var medio = await CrearMedioAsync(ctx, "QR MP", cuenta, comision: 10m);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medio, 100m);

        var ahora = DateTimeOffset.UtcNow;
        var futuro = ahora.AddHours(1);
        await InsertarFilaCrudaAsync(ctx, cuenta, ahora.AddHours(-2), "cobro", 0m, 50m, 0m, 50m, null);

        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var bloquear = new NpgsqlCommand(
            $"SELECT 1 FROM cuentas_tesoreria WHERE id_cuenta_tesoreria = {cuenta} FOR NO KEY UPDATE", conexion, transaccion))
        {
            await bloquear.ExecuteScalarAsync();
        }

        await InsertarFilaCrudaAsync(ctx, cuenta, futuro, "ajuste", 50m, 5m, 0m, 55m, (conexion, transaccion));

        var cierre = CerrarAsync(ctx, turno, "retiro");
        await EsperarUnaSesionBloqueadaAsync("FOR NO KEY UPDATE");
        await transaccion.CommitAsync();
        var respuesta = await cierre;
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());

        var filas = await LibroAsync(ctx, cuenta);
        Assert.Equal(4, filas.Count);
        Assert.Equal(filas.Select(f => f.Fecha).Order().ToList(), filas.Select(f => f.Fecha).ToList());
        Assert.All(filas.Skip(2), f => Assert.Equal(filas[1].Fecha, f.Fecha));
        for (var i = 1; i < filas.Count; i++)
        {
            Assert.Equal(filas[i - 1].Final, filas[i].Inicio);
        }

        var desde = ahora.AddMinutes(30);
        var delRango = filas.Where(f => f.Fecha >= desde).ToList();
        var apertura = filas.Last(f => f.Fecha < desde).Final;
        Assert.Equal(3, delRango.Count);
        Assert.Equal(50m, apertura);
        Assert.Equal(filas[^1].Final, apertura + delRango.Sum(f => f.Ingreso - f.Egreso));
        Assert.Equal(delRango[0].Final - delRango[0].Ingreso + delRango[0].Egreso, apertura);
    }

    private async Task InsertarFilaCrudaAsync(
        Contexto ctx, int idCuenta, DateTimeOffset fecha, string tipo, decimal inicio, decimal ingreso, decimal egreso,
        decimal final, (NpgsqlConnection Conexion, NpgsqlTransaction Transaccion)? enTransaccion)
    {
        var propia = enTransaccion is null ? await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant) : null;
        try
        {
            await using var comando = new NpgsqlCommand(
                "INSERT INTO movimientos_cuenta (id_tenant, id_cuenta_tesoreria, fecha, tipo, concepto, inicio, ingreso, egreso, " +
                "final, id_empleado) VALUES ($1, $2, $3, $4::tipo_movimiento_cuenta, 'previo', $5, $6, $7, $8, $9)",
                enTransaccion?.Conexion ?? propia, enTransaccion?.Transaccion);
            foreach (var valor in new object[] { ctx.IdTenant, idCuenta, fecha, tipo, inicio, ingreso, egreso, final, ctx.IdEmpleado })
            {
                comando.Parameters.Add(new NpgsqlParameter { Value = valor });
            }

            await comando.ExecuteNonQueryAsync();
        }
        finally
        {
            if (propia is not null)
            {
                await propia.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ElCierreEsperaLaEdicionDeUnMedioEnCursoYSnapshotteaSusPorcentajesYaComiteados()
    {
        var ctx = await PrepararAsync(nameof(ElCierreEsperaLaEdicionDeUnMedioEnCursoYSnapshotteaSusPorcentajesYaComiteados));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago");
        var medio = await CrearMedioAsync(ctx, "QR MP", cuenta, comision: 1m);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medio, 1000m);

        await using var conexionEdicion = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var transaccionEdicion = await conexionEdicion.BeginTransactionAsync();
        await using (var editar = new NpgsqlCommand(
            $"UPDATE medios_pago SET comision_porcentaje = 10 WHERE id_medio_pago = {medio}", conexionEdicion, transaccionEdicion))
        {
            await editar.ExecuteNonQueryAsync();
        }

        var cierre = CerrarAsync(ctx, turno, "retiro");
        await EsperarUnaSesionBloqueadaAsync("FOR SHARE");
        Assert.False(cierre.IsCompleted);

        await transaccionEdicion.CommitAsync();
        var respuesta = await cierre;
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());

        var libro = await LibroAsync(ctx, cuenta);
        AfirmarFila(libro[1], TipoMovimientoCuenta.Comision, 1000m, 0m, 100m, 900m, 1000m, 10m);
    }

    [Fact]
    public async Task UnCierreSinActividadDigitalNoEsperaElLockDeLaCuenta()
    {
        var ctx = await PrepararAsync(nameof(UnCierreSinActividadDigitalNoEsperaElLockDeLaCuenta));
        using var _ = ctx.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago");
        await CrearMedioAsync(ctx, "QR MP", cuenta, comision: 5m);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, ctx.IdMedioEfectivo, 100m);

        await using var conexionAbm = await fixture.AbrirConexionCrudaAsync("tenant", ctx.IdTenant);
        await using var transaccionAbm = await conexionAbm.BeginTransactionAsync();
        await using (var bloquear = new NpgsqlCommand(
            $"SELECT 1 FROM cuentas_tesoreria WHERE id_cuenta_tesoreria = {cuenta} FOR UPDATE", conexionAbm, transaccionAbm))
        {
            await bloquear.ExecuteScalarAsync();
        }

        var cierre = CerrarAsync(ctx, turno, "retiro");
        var primero = await Task.WhenAny(cierre, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(cierre, primero);
        Assert.Equal(HttpStatusCode.OK, (await cierre).StatusCode);
        await transaccionAbm.RollbackAsync();
    }

    [Fact]
    public async Task DosCierresConcurrentesDePuntosDeVentaDistintosDejanUnaCadenaContiguaEnLaMismaCuenta()
    {
        var ctx = await PrepararAsync(nameof(DosCierresConcurrentesDePuntosDeVentaDistintosDejanUnaCadenaContiguaEnLaMismaCuenta));
        using var _ = ctx.Admin;
        int idPuntoVenta2;
        await using (var db = Db(ctx))
        {
            var ahora = DateTimeOffset.UtcNow;
            var otro = new PuntoVenta
            {
                IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = "Local 2", CreatedAt = ahora, UpdatedAt = ahora
            };
            db.PuntosVenta.Add(otro);
            await db.SaveChangesAsync();
            idPuntoVenta2 = otro.Id;
        }

        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago", saldoInicial: 10m);
        var medio = await CrearMedioAsync(ctx, "QR MP", cuenta, comision: 10m);
        var turno1 = await AbrirTurnoAsync(ctx);
        var turno2 = await AbrirTurnoAsync(ctx, idPuntoVenta2);
        await SembrarPagoAsync(ctx, turno1, medio, 1000m);
        await SembrarPagoAsync(ctx, turno2, medio, 500m);

        var respuestas = await Task.WhenAll(CerrarAsync(ctx, turno1, "retiro"), CerrarAsync(ctx, turno2, "retiro"));

        Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var libro = await LibroAsync(ctx, cuenta);
        Assert.Equal(4, libro.Count);
        Assert.Equal(10m, libro[0].Inicio);
        for (var i = 1; i < libro.Count; i++)
        {
            Assert.Equal(libro[i - 1].Final, libro[i].Inicio);
        }

        Assert.Equal(10m + 900m + 450m, libro[^1].Final);
    }

    // ---- aislamiento ----------------------------------------------------------------------------

    [Fact]
    public async Task LasFilasLlevanElTenantDelTurnoYOtroTenantNoLasVe()
    {
        var ctx = await PrepararAsync(nameof(LasFilasLlevanElTenantDelTurnoYOtroTenantNoLasVe) + "A");
        var otro = await PrepararAsync(nameof(LasFilasLlevanElTenantDelTurnoYOtroTenantNoLasVe) + "B");
        using var _ = ctx.Admin;
        using var __ = otro.Admin;
        var cuenta = await CrearCuentaAsync(ctx, "Mercado Pago");
        var medio = await CrearMedioAsync(ctx, "QR MP", cuenta, comision: 5m);
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno, medio, 100m);

        await CerrarOkAsync(ctx, turno);

        Assert.All(await LibroAsync(ctx, cuenta), f => Assert.Equal(ctx.IdTenant, f.IdTenant));
        Assert.Equal(0, await CantidadDeFilasDeCuentaAsync(otro));
    }

    private sealed class ProblemDetailsFalso : IProblemDetailsService
    {
        public ProblemDetailsContext? Ultimo { get; private set; }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Ultimo = context;
            return ValueTask.FromResult(true);
        }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Ultimo = context;
            return ValueTask.CompletedTask;
        }
    }
}
