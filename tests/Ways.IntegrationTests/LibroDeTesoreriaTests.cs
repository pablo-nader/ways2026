using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Caja;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;
using Ways.Domain.Proveedores;

namespace Ways.IntegrationTests;

/// <summary>
/// Libro de tesorería contra Postgres real. Un solo escenario con todas las fuentes (turnos cerrados con retiro, pago del cajón,
/// cobros digitales con comisión y retención, pagos de administración con medio digital y sin cuenta, ajustes manuales de efectivo y de
/// cuenta, y una segunda empresa del mismo tenant que no tiene que aparecer), más los 404, el rango y la política.
///
/// Evidencia de mutación (mutation-proof-tests), registrada al escribir las pruebas: (1) <c>ReglasDeLibroDeTesoreria.DiferenciaDeArqueoVisible</c>
/// con <c>&lt;=</c> cambiado a <c>&lt;</c> hace fallar <see cref="LaDiferenciaDeArqueoSeMuestraSoloSiSuperaLaToleranciaDelPuntoDeVenta"/> (el turno con
/// diferencia exactamente igual a la tolerancia pasa a mostrarse); (2) quitar el predicado <c>IdEmpresa == idEmpresa</c> de los pagos o de los
/// ajustes de efectivo en <c>ServicioDeLibroDeTesoreria</c> hace fallar <see cref="UnaSegundaEmpresaDelMismoTenantNoSeMezclaEnElLibro"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class LibroDeTesoreriaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Escenario(
        ContextoDeTesoreriaDePrueba Ctx, int IdPuntoVenta, int IdMedioEfectivo, int IdMedioTransferencia, int IdMedioCheque,
        int IdBanco, int IdMercadoPago, int IdTurnoConDiferencia, int IdTurnoCero, int IdTurnoIgualATolerancia,
        int IdTurnoSobreLaTolerancia, int IdPagoDelCajon, int IdPagoAProveedor, int IdPagoSinCuenta,
        DateTimeOffset Desde, DateTimeOffset Hasta, int IdProveedor);

    private static string Url(int idEmpresa, DateTimeOffset desde, DateTimeOffset hasta) =>
        $"/api/reportes/libro-tesoreria?idEmpresa={idEmpresa}&desde={Uri.EscapeDataString(desde.ToString("O"))}&hasta={Uri.EscapeDataString(hasta.ToString("O"))}";

    private static async Task<LibroDeTesoreria> LeerAsync(HttpClient cliente, int idEmpresa, DateTimeOffset desde, DateTimeOffset hasta)
    {
        var respuesta = await cliente.GetAsync(Url(idEmpresa, desde, hasta));
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());
        return (await respuesta.Content.ReadFromJsonAsync<LibroDeTesoreria>(OpcionesJson))!;
    }

    private static decimal DeCuenta(IReadOnlyList<ImporteDeCuenta> importes, int idCuenta) =>
        importes.Where(i => i.IdCuentaTesoreria == idCuenta).Sum(i => i.Importe);

    private static async Task<int> SembrarTurnoCerradoAsync(
        ContextoDeTesoreriaDePrueba ctx, int idPuntoVenta, int idMedioEfectivo, int idMedioTransferencia,
        DateTimeOffset cierre, decimal retiro, decimal? declaradoEfectivo, decimal esperadoEfectivo)
    {
        await using var db = ctx.Db();
        var turno = new TurnoCaja
        {
            IdTenant = ctx.IdTenant, IdPuntoVenta = idPuntoVenta, IdEmpleadoApertura = ctx.IdUsuarioAdmin,
            IdEmpleadoCierre = ctx.IdUsuarioAdmin, FechaApertura = cierre.AddHours(-8), FechaCierre = cierre, FondoInicial = 500m,
            Estado = EstadoTurno.Cerrado, IdMedioPagoEfectivo = idMedioEfectivo, CreatedAt = cierre, UpdatedAt = cierre
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();

        db.MovimientosCaja.Add(new MovimientoCaja
        {
            IdTenant = ctx.IdTenant, IdTurnoCaja = turno.Id, Tipo = TipoMovimientoCaja.Retiro, Importe = retiro,
            Motivo = "Retiro de prueba", IdEmpleado = ctx.IdUsuarioAdmin, CreadoEl = cierre.AddMinutes(-5)
        });
        if (declaradoEfectivo is { } declarado)
        {
            db.ArqueosTurno.Add(new ArqueoTurno
            {
                IdTenant = ctx.IdTenant, IdTurnoCaja = turno.Id, IdMedioPago = idMedioEfectivo,
                ImporteEsperado = esperadoEfectivo, ImporteDeclarado = declarado
            });
            db.ArqueosTurno.Add(new ArqueoTurno
            {
                IdTenant = ctx.IdTenant, IdTurnoCaja = turno.Id, IdMedioPago = idMedioTransferencia,
                ImporteEsperado = 100m, ImporteDeclarado = 100m
            });
        }

        await db.SaveChangesAsync();
        return turno.Id;
    }

    private static async Task<int> SembrarPagoAsync(
        ContextoDeTesoreriaDePrueba ctx, int idEmpresa, int? idPuntoVenta, int? idTurno, OrigenFondosPago origen, DateTimeOffset fecha,
        CategoriaPago categoria, string concepto, int? idProveedor, params (int IdMedio, decimal Importe)[] lineas)
    {
        await using var db = ctx.Db();
        var pago = new Pago
        {
            IdTenant = ctx.IdTenant, Fecha = fecha, IdEmpresa = idEmpresa, IdPuntoVenta = idPuntoVenta, IdTurnoCaja = idTurno,
            IdEmpleado = ctx.IdUsuarioAdmin, Categoria = categoria, OrigenFondos = origen, IdProveedor = idProveedor,
            Concepto = concepto, Importe = lineas.Sum(l => l.Importe), CreatedAt = fecha, UpdatedAt = fecha
        };
        db.Pagos.Add(pago);
        await db.SaveChangesAsync();
        foreach (var (idMedio, importe) in lineas)
        {
            db.ItemsPago.Add(new ItemPago
            {
                IdTenant = ctx.IdTenant, IdPago = pago.Id, IdMedioPago = idMedio, Importe = importe, CreatedAt = fecha, UpdatedAt = fecha
            });
        }

        await db.SaveChangesAsync();
        return pago.Id;
    }

    private static async Task SembrarCobroDigitalAsync(
        ContextoDeTesoreriaDePrueba ctx, int idTurno, int idCuenta, int idMedio, DateTimeOffset fecha, decimal bruto, decimal comision,
        decimal retencion)
    {
        await using var db = ctx.Db();
        var saldo = await db.MovimientosCuenta.Where(m => m.IdCuentaTesoreria == idCuenta).OrderByDescending(m => m.Id)
            .Select(m => (decimal?)m.Final).FirstOrDefaultAsync() ?? 0m;
        decimal Agregar(TipoMovimientoCuenta tipo, decimal ingreso, decimal egreso)
        {
            var final = saldo + ingreso - egreso;
            db.MovimientosCuenta.Add(new MovimientoCuenta
            {
                IdTenant = ctx.IdTenant, IdCuentaTesoreria = idCuenta, Fecha = fecha, Tipo = tipo, IdTurnoCaja = idTurno,
                IdMedioPago = idMedio, Concepto = "cierre", Inicio = saldo, Ingreso = ingreso, Egreso = egreso, Final = final,
                IdEmpleado = ctx.IdUsuarioAdmin
            });
            return final;
        }

        saldo = Agregar(TipoMovimientoCuenta.Cobro, bruto, 0m);
        saldo = Agregar(TipoMovimientoCuenta.Comision, 0m, comision);
        Agregar(TipoMovimientoCuenta.RetencionIibb, 0m, retencion);
        await db.SaveChangesAsync();
    }

    private async Task<Escenario> PrepararEscenarioAsync(string nombre)
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, nombre);
        var ahora = DateTimeOffset.UtcNow;

        await using var plataforma = fixture.CrearContextoDeAplicacion(Ways.Infrastructure.Multitenancy.TenantActualFijo.Plataforma);
        var condicion = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        plataforma.CondicionesFiscales.Add(condicion);
        await plataforma.SaveChangesAsync();

        int idPuntoVenta, idEfectivo, idTransferencia, idCheque, idBanco, idMp, idProveedor;
        await using (var db = ctx.Db())
        {
            idPuntoVenta = await db.PuntosVenta.Where(p => p.IdEmpresa == ctx.IdEmpresa).Select(p => p.Id).FirstAsync();
            idEfectivo = await db.MediosPago.Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
            idTransferencia = await db.MediosPago.Where(m => m.Nombre == "Transferencia").Select(m => m.Id).FirstAsync();

            var banco = new CuentaTesoreria
            {
                IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = "Banco", SaldoInicial = 1000m, CreatedAt = ahora, UpdatedAt = ahora
            };
            var mp = new CuentaTesoreria
            {
                IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = "Mercado Pago", SaldoInicial = 0m, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.CuentasTesoreria.AddRange(banco, mp);
            await db.SaveChangesAsync();
            idBanco = banco.Id;
            idMp = mp.Id;

            var transferencia = await db.MediosPago.FirstAsync(m => m.Id == idTransferencia);
            transferencia.IdCuentaTesoreria = idBanco;
            transferencia.ComisionPorcentaje = 3m;
            transferencia.RetencionIibbPorcentaje = 1m;

            var cheque = new MedioPago
            {
                IdTenant = ctx.IdTenant, Nombre = "Cheque", Orden = 9, Comportamiento = ComportamientoMedioPago.Electronico,
                AdmiteVuelto = false, RequiereReferencia = false, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.MediosPago.Add(cheque);

            var proveedor = new Proveedor
            {
                IdTenant = ctx.IdTenant, RazonSocial = "Distribuidora Norte", IdCondicionFiscal = condicion.Id, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Proveedores.Add(proveedor);

            db.Parametros.Add(new Parametro
            {
                IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Clave = "tolerancia_pago", Valor = "20", CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
            idCheque = cheque.Id;
            idProveedor = proveedor.Id;
        }

        var desde = ahora.AddDays(-5);
        var hasta = ahora.AddHours(1);

        // Antes del rango: un cierre con retiro y un pago de tesorería en efectivo.
        await SembrarTurnoCerradoAsync(ctx, idPuntoVenta, idEfectivo, idTransferencia, ahora.AddDays(-20), 700m, null, 0m);
        await SembrarPagoAsync(
            ctx, ctx.IdEmpresa, idPuntoVenta, null, OrigenFondosPago.Tesoreria, ahora.AddDays(-9), CategoriaPago.Servicios, "Luz anterior", null,
            (idEfectivo, 100m));

        // Dentro del rango. La tolerancia de la empresa es 20: -1500 se muestra, 0 y -20 (igual al límite) no, -20.01 sí.
        var t1 = await SembrarTurnoCerradoAsync(ctx, idPuntoVenta, idEfectivo, idTransferencia, ahora.AddDays(-3), 3000m, 500m, 2000m);
        await SembrarCobroDigitalAsync(ctx, t1, idBanco, idTransferencia, ahora.AddDays(-3), 2000m, 60m, 20m);
        var pagoDelCajon = await SembrarPagoAsync(
            ctx, ctx.IdEmpresa, idPuntoVenta, t1, OrigenFondosPago.CajaTurno, ahora.AddDays(-3).AddHours(-3), CategoriaPago.Proveedor,
            "Factura 55", idProveedor, (idEfectivo, 400m));
        var t2 = await SembrarTurnoCerradoAsync(ctx, idPuntoVenta, idEfectivo, idTransferencia, ahora.AddDays(-2).AddHours(-20), 100m, 1000m, 1000m);
        var t3 = await SembrarTurnoCerradoAsync(ctx, idPuntoVenta, idEfectivo, idTransferencia, ahora.AddDays(-2).AddHours(-10), 50m, 980m, 1000m);
        var t4 = await SembrarTurnoCerradoAsync(ctx, idPuntoVenta, idEfectivo, idTransferencia, ahora.AddDays(-2), 60m, 979.99m, 1000m);

        var pagoAProveedor = await SembrarPagoAsync(
            ctx, ctx.IdEmpresa, idPuntoVenta, null, OrigenFondosPago.Tesoreria, ahora.AddDays(-1), CategoriaPago.Proveedor, "Factura 56",
            idProveedor, (idTransferencia, 300m));
        var pagoSinCuenta = await SembrarPagoAsync(
            ctx, ctx.IdEmpresa, idPuntoVenta, null, OrigenFondosPago.Tesoreria, ahora.AddDays(-1).AddHours(1), CategoriaPago.Servicios,
            "Mixto", null, (idTransferencia, 100m), (idCheque, 30m));

        Assert.Equal(HttpStatusCode.Created, (await ctx.Admin.PostAsJsonAsync(
            "/api/tesoreria/efectivo/ajustes", new SolicitudDeAjusteDeEfectivo(ctx.IdEmpresa, "Efectivo hallado", 250m))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ctx.Admin.PostAsJsonAsync(
            $"/api/cuentas-tesoreria/{idMp}/ajustes", new SolicitudDeAjusteDeCuenta("Comisión no registrada", -40m, null))).StatusCode);

        return new Escenario(
            ctx, idPuntoVenta, idEfectivo, idTransferencia, idCheque, idBanco, idMp, t1, t2, t3, t4, pagoDelCajon, pagoAProveedor,
            pagoSinCuenta, desde, hasta, idProveedor);
    }

    [Fact]
    public async Task ElLibroTraeCadaFuenteConSuEfectivoSuCuentaYLosSaldosAnteriorYActual()
    {
        var e = await PrepararEscenarioAsync("LibroCompleto");

        var libro = await LeerAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, e.Desde, e.Hasta);

        Assert.Equal(["Banco", "Mercado Pago"], libro.Cuentas.Select(c => c.Nombre));
        Assert.Equal(600m, libro.SaldoAnterior.Efectivo);
        Assert.Equal(1000m, DeCuenta(libro.SaldoAnterior.PorCuenta, e.IdBanco));
        Assert.Equal(0m, DeCuenta(libro.SaldoAnterior.PorCuenta, e.IdMercadoPago));
        Assert.Equal(4060m, libro.SaldoActual.Efectivo);
        Assert.Equal(2520m, DeCuenta(libro.SaldoActual.PorCuenta, e.IdBanco));
        Assert.Equal(-40m, DeCuenta(libro.SaldoActual.PorCuenta, e.IdMercadoPago));

        Assert.Equal(
            [
                TipoDeFilaDelLibro.Pago, TipoDeFilaDelLibro.Caja, TipoDeFilaDelLibro.Caja, TipoDeFilaDelLibro.Caja, TipoDeFilaDelLibro.Caja,
                TipoDeFilaDelLibro.Pago, TipoDeFilaDelLibro.Pago, TipoDeFilaDelLibro.AjusteEfectivo, TipoDeFilaDelLibro.AjusteCuenta
            ],
            libro.Filas.Select(f => f.Tipo));
        Assert.Equal(libro.Filas.OrderBy(f => f.Fecha).Select(f => f.Fecha), libro.Filas.Select(f => f.Fecha));

        var delCajon = libro.Filas[0];
        Assert.Equal(-400m, delCajon.Efectivo);
        Assert.Empty(delCajon.Cuentas);
        Assert.Null(delCajon.SinCuenta);
        Assert.Equal(e.IdPagoDelCajon, delCajon.Pago!.IdPago);
        Assert.Equal("Proveedores — Factura 55 · Distribuidora Norte", delCajon.Pago.Detalle);

        var caja = libro.Filas[1];
        Assert.Equal(3400m, caja.Efectivo);
        var cuenta = Assert.Single(caja.Cuentas);
        Assert.Equal((e.IdBanco, 1920m), (cuenta.IdCuentaTesoreria, cuenta.Importe));
        Assert.Equal(e.IdTurnoConDiferencia, caja.Caja!.IdTurnoCaja);
        Assert.Equal(e.IdTurnoConDiferencia, caja.Caja.Numero);
        Assert.Equal(caja.Fecha, caja.Caja.FechaCierre);
        Assert.Equal(caja.Caja.FechaCierre.AddHours(-8), caja.Caja.FechaApertura);
        Assert.Equal(e.IdPuntoVenta, caja.Caja.IdPuntoVenta);
        Assert.False(string.IsNullOrEmpty(caja.Caja.PuntoVenta));
        var desgloseDeBanco = Assert.Single(caja.Caja.Desglose);
        Assert.Equal((e.IdBanco, 1920m), (desgloseDeBanco.IdCuentaTesoreria, desgloseDeBanco.Neto));
        var medio = Assert.Single(desgloseDeBanco.Medios);
        Assert.Equal((e.IdMedioTransferencia, "Transferencia", 2000m, 60m, 20m, 1920m),
            (medio.IdMedioPago, medio.Medio, medio.Importe, medio.Comision, medio.Retenciones, medio.Neto));

        var aProveedor = libro.Filas[5];
        Assert.Null(aProveedor.Efectivo);
        Assert.Equal((e.IdBanco, -300m), (aProveedor.Cuentas[0].IdCuentaTesoreria, aProveedor.Cuentas[0].Importe));
        Assert.Equal(e.IdPagoAProveedor, aProveedor.Pago!.IdPago);

        var mixto = libro.Filas[6];
        Assert.Equal(-100m, Assert.Single(mixto.Cuentas).Importe);
        Assert.Equal(-30m, mixto.SinCuenta);
        Assert.Equal(e.IdPagoSinCuenta, mixto.Pago!.IdPago);

        Assert.Equal(250m, libro.Filas[7].Efectivo);
        Assert.Equal("Efectivo hallado", libro.Filas[7].Ajuste!.Concepto);
        var ajusteDeCuenta = Assert.Single(libro.Filas[8].Cuentas);
        Assert.Equal((e.IdMercadoPago, -40m), (ajusteDeCuenta.IdCuentaTesoreria, ajusteDeCuenta.Importe));
        Assert.Null(libro.Filas[8].Efectivo);
    }

    [Fact]
    public async Task LaDiferenciaDeArqueoSeMuestraSoloSiSuperaLaToleranciaDelPuntoDeVenta()
    {
        var e = await PrepararEscenarioAsync("LibroTolerancia");

        var libro = await LeerAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, e.Desde, e.Hasta);

        decimal? Diferencia(int idTurno) => libro.Filas.Single(f => f.Caja?.IdTurnoCaja == idTurno).Caja!.DiferenciaArqueo;
        Assert.Equal(-1500m, Diferencia(e.IdTurnoConDiferencia));
        Assert.Null(Diferencia(e.IdTurnoCero));
        Assert.Null(Diferencia(e.IdTurnoIgualATolerancia));
        Assert.Equal(-20.01m, Diferencia(e.IdTurnoSobreLaTolerancia));
    }

    [Fact]
    public async Task UnProveedorDadoDeBajaSeMuestraComoNoDisponibleSinPerderLaFila()
    {
        var e = await PrepararEscenarioAsync("LibroProveedorBaja");
        await using (var db = e.Ctx.Db())
        {
            var proveedor = await db.Proveedores.FirstAsync(p => p.Id == e.IdProveedor);
            proveedor.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var libro = await LeerAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, e.Desde, e.Hasta);

        var fila = libro.Filas.Single(f => f.Pago?.IdPago == e.IdPagoAProveedor);
        Assert.Equal("Proveedores — Factura 56 · (proveedor no disponible)", fila.Pago!.Detalle);
        Assert.Equal(-300m, fila.Cuentas.Single().Importe);
    }

    [Fact]
    public async Task UnaSegundaEmpresaDelMismoTenantNoSeMezclaEnElLibro()
    {
        var e = await PrepararEscenarioAsync("LibroDosEmpresas");
        var base_ = await LeerAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, e.Desde, e.Hasta);

        int idOtraEmpresa;
        await using (var db = e.Ctx.Db())
        {
            var ahora = DateTimeOffset.UtcNow;
            var otra = new Empresa { IdTenant = e.Ctx.IdTenant, RazonSocial = "Otra empresa", CreatedAt = ahora, UpdatedAt = ahora };
            db.Empresas.Add(otra);
            await db.SaveChangesAsync();
            idOtraEmpresa = otra.Id;

            db.MovimientosTesoreria.Add(new MovimientoTesoreria
            {
                IdTenant = e.Ctx.IdTenant, IdEmpresa = idOtraEmpresa, Fecha = ahora.AddDays(-1), Tipo = TipoMovimientoTesoreria.AjusteManual,
                Concepto = "ajeno", Inicio = 0m, Ingreso = 9999m, Egreso = 0m, Final = 9999m, IdEmpleado = e.Ctx.IdUsuarioAdmin
            });
            await db.SaveChangesAsync();
        }

        await SembrarPagoAsync(
            e.Ctx, idOtraEmpresa, null, null, OrigenFondosPago.Tesoreria, DateTimeOffset.UtcNow.AddDays(-1), CategoriaPago.Otros, "ajeno", null,
            (e.IdMedioEfectivo, 777m));

        var libro = await LeerAsync(e.Ctx.Admin, e.Ctx.IdEmpresa, e.Desde, e.Hasta);
        var delaOtra = await LeerAsync(e.Ctx.Admin, idOtraEmpresa, e.Desde, e.Hasta);

        Assert.Equal(base_.SaldoActual.Efectivo, libro.SaldoActual.Efectivo);
        Assert.Equal(base_.Filas.Count, libro.Filas.Count);
        Assert.Equal(9999m - 777m, delaOtra.SaldoActual.Efectivo);
        Assert.Empty(delaOtra.Cuentas);
        Assert.Equal(2, delaOtra.Filas.Count);
    }

    [Fact]
    public async Task UnaEmpresaAjenaOInexistenteDa404AntesQueElRango()
    {
        var e = await PrepararEscenarioAsync("LibroAjena");
        var ajeno = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "LibroAjenaB");

        var delOtroTenant = await e.Ctx.Admin.GetAsync(Url(ajeno.IdEmpresa, e.Desde, e.Hasta));
        var inexistente = await e.Ctx.Admin.GetAsync(Url(999999, e.Desde, e.Desde.AddDays(900)));

        Assert.Equal(HttpStatusCode.NotFound, delOtroTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
    }

    [Fact]
    public async Task UnRangoInvalidoOSinFechasDa400()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "LibroRango");
        var desde = DateTimeOffset.UtcNow.AddDays(-400);

        var demasiado = await ctx.Admin.GetAsync(Url(ctx.IdEmpresa, desde, desde.AddDays(366).AddMinutes(1)));
        var alReves = await ctx.Admin.GetAsync(Url(ctx.IdEmpresa, desde.AddDays(2), desde));
        var sinFechas = await ctx.Admin.GetAsync($"/api/reportes/libro-tesoreria?idEmpresa={ctx.IdEmpresa}");
        var justo = await ctx.Admin.GetAsync(Url(ctx.IdEmpresa, desde, desde.AddDays(366)));

        Assert.Equal(HttpStatusCode.BadRequest, demasiado.StatusCode);
        Assert.Equal("rango_demasiado_amplio", (await demasiado.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, alReves.StatusCode);
        Assert.Equal("rango_invalido", (await alReves.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, sinFechas.StatusCode);
        Assert.Equal(HttpStatusCode.OK, justo.StatusCode);
    }

    [Fact]
    public async Task ElSupervisorLeeElLibroYElVendedorQuedaAfuera()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "LibroPolitica");
        var desde = DateTimeOffset.UtcNow.AddDays(-1);
        var hasta = DateTimeOffset.UtcNow;

        Assert.Equal(HttpStatusCode.OK, (await ctx.Supervisor.GetAsync(Url(ctx.IdEmpresa, desde, hasta))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ctx.Vendedor.GetAsync(Url(ctx.IdEmpresa, desde, hasta))).StatusCode);
        using var anonimo = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync(Url(ctx.IdEmpresa, desde, hasta))).StatusCode);
    }

    [Fact]
    public async Task UnLibroSinMovimientosTraeLasCuentasEnCeroYSaldoInicial()
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, "LibroVacio");
        var ahora = DateTimeOffset.UtcNow;
        await using (var db = ctx.Db())
        {
            db.CuentasTesoreria.Add(new CuentaTesoreria
            {
                IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = "Banco", SaldoInicial = 300m, CreatedAt = ahora, UpdatedAt = ahora
            });
            await db.SaveChangesAsync();
        }

        var libro = await LeerAsync(ctx.Admin, ctx.IdEmpresa, ahora.AddDays(-1), ahora);

        Assert.Empty(libro.Filas);
        Assert.Equal(0m, libro.SaldoActual.Efectivo);
        Assert.Equal(300m, Assert.Single(libro.SaldoActual.PorCuenta).Importe);
        Assert.Equal(300m, Assert.Single(libro.SaldoAnterior.PorCuenta).Importe);
    }
}
