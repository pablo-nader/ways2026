using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Caja;
using Ways.Application.CuentaCorriente;
using Ways.Application.Plantel;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Plantel;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// Corrección de las líneas de un pago de personal (<c>PUT /api/pagos/administracion/{id}/lineas-de-personal</c>) y anulación
/// de un pago de personal (<c>POST /api/personal/{id}/pagos/{idMovimiento}/anular</c>) punta a punta contra Postgres real: el
/// arqueo de un turno cerrado, la tesorería y las cuentas, el libro del personal, la cuenta corriente del cliente, la
/// auditoría, los rechazos sin escrituras y la carrera entre dos anulaciones.
///
/// Escenario: la cuenta Banco recibe Transferencia; Efectivo no tiene cuenta.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CorreccionDePagosDePersonalTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Escenario(
        ContextoDeTesoreriaDePrueba Ctx, int IdPuntoVenta, int IdEfectivo, int IdTransferencia, int IdBanco);

    private async Task<Escenario> PrepararAsync(string nombre)
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, nombre);
        var ahora = DateTimeOffset.UtcNow;

        await using var db = ctx.Db();
        var idPuntoVenta = await db.PuntosVenta.Where(p => p.IdEmpresa == ctx.IdEmpresa).Select(p => p.Id).FirstAsync();
        var idEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        var banco = new CuentaTesoreria
        {
            IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = "Banco", SaldoInicial = 1000m, CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.CuentasTesoreria.Add(banco);
        await db.SaveChangesAsync();

        var transferencia = await db.MediosPago.FirstAsync(m => m.Nombre == "Transferencia");
        transferencia.IdCuentaTesoreria = banco.Id;
        await db.SaveChangesAsync();

        return new Escenario(ctx, idPuntoVenta, idEfectivo, transferencia.Id, banco.Id);
    }

    private static string Hoy() => FechaDelNegocio.Hoy().ToString("yyyy-MM-dd");

    private static object[] Lineas(params (int IdMedio, decimal Importe)[] lineas) =>
        lineas.Select(l => (object)new { idMedioPago = l.IdMedio, importe = l.Importe, referencia = (string?)null }).ToArray();

    private static async Task<JsonElement> CuerpoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado)
    {
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == esperado, $"{respuesta.StatusCode}: {texto}");
        return texto.Length == 0 ? default : JsonDocument.Parse(texto).RootElement.Clone();
    }

    private static async Task<string?> CodigoAsync(HttpResponseMessage respuesta, HttpStatusCode esperado) =>
        (await CuerpoAsync(respuesta, esperado)).GetProperty("codigo").GetString();

    private static async Task<int> CrearPersonalAsync(Escenario e, decimal saldoInicial, int? idCliente = null)
    {
        var alta = await e.Ctx.Admin.PostAsJsonAsync(
            "/api/personal", new DatosDePersonal("Ana", null, null, null, 1000m, null, null, idCliente));
        var id = (await CuerpoAsync(alta, HttpStatusCode.Created)).GetProperty("id").GetInt32();

        var ajuste = await e.Ctx.Admin.PostAsJsonAsync(
            $"/api/personal/{id}/ajustes", new { importe = saldoInicial, detalle = "Saldo inicial de la prueba", fecha = "2026-10-01" });
        Assert.Equal(HttpStatusCode.Created, ajuste.StatusCode);
        return id;
    }

    private static async Task<int> CrearClienteConDeudaAsync(Escenario e, decimal deuda)
    {
        int idCliente;
        await using (var db = e.Ctx.Db())
        {
            var ahora = DateTimeOffset.UtcNow;
            var cliente = new Cliente
            {
                IdTenant = e.Ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = "Cliente empleado",
                IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(),
                IdListaPrecio = await db.Clientes.Select(c => c.IdListaPrecio).FirstAsync(),
                LimiteCredito = 0m, CreditoIlimitado = true, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Clientes.Add(cliente);
            await db.SaveChangesAsync();
            idCliente = cliente.Id;
        }

        var ajuste = await e.Ctx.Admin.PostAsJsonAsync(
            $"/api/clientes/{idCliente}/cuenta-corriente/ajustes",
            new SolicitudDeAjuste(e.IdPuntoVenta, deuda, "Deuda inicial de la prueba"));
        Assert.Equal(HttpStatusCode.Created, ajuste.StatusCode);
        return idCliente;
    }

    /// <summary>Paga al personal y devuelve el asiento del libro.</summary>
    private static async Task<int> PagarAsync(
        Escenario e, int idPersonal, string origen, decimal? descuento, params (int IdMedio, decimal Importe)[] lineas)
    {
        var respuesta = await e.Ctx.Admin.PostAsJsonAsync($"/api/personal/{idPersonal}/pagos", new
        {
            fecha = Hoy(),
            idEmpresa = origen == "Tesoreria" && lineas.Length > 0 ? e.Ctx.IdEmpresa : (int?)null,
            idPuntoVenta = e.IdPuntoVenta,
            origenFondos = lineas.Length == 0 ? null : origen,
            descuentoDeCuentaCorriente = descuento,
            lineas = Lineas(lineas)
        });
        return (await CuerpoAsync(respuesta, HttpStatusCode.Created)).GetProperty("idMovimiento").GetInt32();
    }

    private static async Task<int> AbrirTurnoAsync(Escenario e, decimal fondo)
    {
        var respuesta = await e.Ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(e.IdPuntoVenta, fondo, null));
        return (await CuerpoAsync(respuesta, HttpStatusCode.Created)).GetProperty("id").GetInt32();
    }

    private static async Task CerrarTurnoAsync(Escenario e, int idTurno, decimal efectivoDeclarado)
    {
        var respuesta = await e.Ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurno}/cierre",
            new SolicitudDeCierre([new ConteoDeclarado(e.IdEfectivo, efectivoDeclarado)], null));
        await CuerpoAsync(respuesta, HttpStatusCode.OK);
    }

    private static async Task<int> IdDelPagoAsync(Escenario e, int idMovimiento)
    {
        await using var db = e.Ctx.Db();
        return await db.Pagos.Where(p => p.IdMovimientoPersonal == idMovimiento).Select(p => p.Id).SingleAsync();
    }

    private static Task<HttpResponseMessage> EditarLineasAsync(HttpClient cliente, int idPago, object cuerpo) =>
        cliente.PutAsJsonAsync($"/api/pagos/administracion/{idPago}/lineas-de-personal", cuerpo);

    private static Task<HttpResponseMessage> AnularAsync(HttpClient cliente, int idPersonal, int idMovimiento) =>
        cliente.PostAsync($"/api/personal/{idPersonal}/pagos/{idMovimiento}/anular", content: null);

    private static async Task<decimal> SaldoDelPersonalAsync(Escenario e, int idPersonal)
    {
        await using var db = e.Ctx.Db();
        return await db.Personal.Where(p => p.Id == idPersonal).Select(p => p.Saldo).SingleAsync();
    }

    private static async Task<decimal> SaldoDelClienteAsync(Escenario e, int idCliente)
    {
        await using var db = e.Ctx.Db();
        return await db.Clientes.IgnoreQueryFilters().Where(c => c.Id == idCliente).Select(c => c.Saldo).SingleAsync();
    }

    private static async Task<List<(int IdMedio, decimal Importe)>> ItemsVivosAsync(Escenario e, int idPago)
    {
        await using var db = e.Ctx.Db();
        var items = await db.ItemsPago.Where(i => i.IdPago == idPago).OrderBy(i => i.Id)
            .Select(i => new { i.IdMedioPago, i.Importe }).ToListAsync();
        return items.Select(i => (i.IdMedioPago, i.Importe)).ToList();
    }

    private static async Task<decimal> NetoDelBancoDelPagoAsync(Escenario e, int idPago)
    {
        await using var db = e.Ctx.Db();
        return await db.MovimientosCuenta
            .Where(m => m.IdCuentaTesoreria == e.IdBanco && m.IdPago == idPago)
            .SumAsync(m => m.Egreso - m.Ingreso);
    }

    /// <summary>Cantidad de filas de cada libro del tenant, para afirmar que un rechazo no escribió nada.</summary>
    private sealed record Foto(
        int AsientosDelPersonal, int AsientosDeCuentaCorriente, int Pagos, int ItemsVivos, int AsientosDeTesoreria,
        int MovimientosDeCuenta, int Auditoria);

    private static async Task<Foto> FotoAsync(Escenario e)
    {
        await using var db = e.Ctx.Db();
        return new Foto(
            await db.MovimientosPersonal.CountAsync(),
            await db.MovimientosCuentaCorriente.CountAsync(),
            await db.Pagos.CountAsync(),
            await db.ItemsPago.CountAsync(),
            await db.MovimientosTesoreria.CountAsync(),
            await db.MovimientosCuenta.CountAsync(),
            await db.Auditoria.CountAsync());
    }

    // ---- corrección de las líneas ----------------------------------------------------------------------------------

    [Fact]
    public async Task CorregirLasLineasConElTurnoAbiertoReemplazaLosItemsSinTocarElLibroNiElDescuento()
    {
        var e = await PrepararAsync(nameof(CorregirLasLineasConElTurnoAbiertoReemplazaLosItemsSinTocarElLibroNiElDescuento));
        var idCliente = await CrearClienteConDeudaAsync(e, 2000m);
        var idPersonal = await CrearPersonalAsync(e, 5000m, idCliente);
        var idTurno = await AbrirTurnoAsync(e, 1000m);
        var idMovimiento = await PagarAsync(e, idPersonal, "CajaTurno", 100m, (e.IdEfectivo, 300m));
        var idPago = await IdDelPagoAsync(e, idMovimiento);

        var respuesta = await CuerpoAsync(
            await EditarLineasAsync(
                e.Ctx.Admin, idPago, new { lineas = Lineas((e.IdEfectivo, 100m), (e.IdTransferencia, 200m)) }),
            HttpStatusCode.OK);

        Assert.Equal(idPago, respuesta.GetProperty("id").GetInt32());
        Assert.Equal(300m, respuesta.GetProperty("importe").GetDecimal());
        Assert.Equal(idMovimiento, respuesta.GetProperty("idMovimientoPersonal").GetInt32());
        Assert.Equal(2, respuesta.GetProperty("lineas").GetArrayLength());

        Assert.Equal([(e.IdEfectivo, 100m), (e.IdTransferencia, 200m)], await ItemsVivosAsync(e, idPago));
        Assert.Equal(200m, await NetoDelBancoDelPagoAsync(e, idPago));

        // El libro del personal y el descuento no se mueven.
        Assert.Equal(5000m - 400m, await SaldoDelPersonalAsync(e, idPersonal));
        Assert.Equal(2000m - 100m, await SaldoDelClienteAsync(e, idCliente));

        await using var db = e.Ctx.Db();
        Assert.Equal(2, await db.MovimientosPersonal.CountAsync(m => m.IdPersonal == idPersonal));
        var pago = await db.Pagos.SingleAsync(p => p.Id == idPago);
        Assert.Equal((300m, "Pago de personal: Ana"), (pago.Importe, pago.Concepto));
        Assert.Null(await db.TurnosCaja.Where(t => t.Id == idTurno).Select(t => t.FechaRecalculo).SingleAsync());

        var auditoria = await db.Auditoria.SingleAsync(a => a.Accion == "pago.edicion" && a.IdEntidad == idPago);
        using var anterior = JsonDocument.Parse(auditoria.ValorAnterior!);
        using var nuevo = JsonDocument.Parse(auditoria.ValorNuevo);
        Assert.Equal(1, anterior.RootElement.GetProperty("lineas").GetArrayLength());
        Assert.Equal(2, nuevo.RootElement.GetProperty("lineas").GetArrayLength());
    }

    [Fact]
    public async Task CorregirLasLineasDeUnTurnoCerradoRecalculaElArqueo()
    {
        var e = await PrepararAsync(nameof(CorregirLasLineasDeUnTurnoCerradoRecalculaElArqueo));
        var idPersonal = await CrearPersonalAsync(e, 5000m);
        var idTurno = await AbrirTurnoAsync(e, 1000m);
        var idPago = await IdDelPagoAsync(e, await PagarAsync(e, idPersonal, "CajaTurno", null, (e.IdEfectivo, 300m)));
        await CerrarTurnoAsync(e, idTurno, 700m);

        await CuerpoAsync(
            await EditarLineasAsync(
                e.Ctx.Admin, idPago, new { lineas = Lineas((e.IdEfectivo, 100m), (e.IdTransferencia, 200m)) }),
            HttpStatusCode.OK);

        await using var db = e.Ctx.Db();
        var filas = await db.ArqueosTurno.Where(a => a.IdTurnoCaja == idTurno).ToListAsync();
        var efectivo = filas.Single(a => a.IdMedioPago == e.IdEfectivo);
        Assert.Equal((900m, 700m, 700m), (efectivo.ImporteEsperado, efectivo.ImporteDeclarado, efectivo.ImporteEsperadoOriginal!.Value));
        var transferencia = filas.Single(a => a.IdMedioPago == e.IdTransferencia);
        Assert.Equal(-200m, transferencia.ImporteEsperado);

        var turno = await db.TurnosCaja.SingleAsync(t => t.Id == idTurno);
        Assert.NotNull(turno.FechaRecalculo);
        Assert.Equal(e.Ctx.IdUsuarioAdmin, turno.IdEmpleadoRecalculo);
        Assert.Equal(5000m - 300m, await SaldoDelPersonalAsync(e, idPersonal));
    }

    [Fact]
    public async Task CorregirLasLineasDeUnPagoDeTesoreriaNoMueveLaTesoreriaYPasaElDebitoALaCuenta()
    {
        var e = await PrepararAsync(nameof(CorregirLasLineasDeUnPagoDeTesoreriaNoMueveLaTesoreriaYPasaElDebitoALaCuenta));
        var idPersonal = await CrearPersonalAsync(e, 5000m);
        var idPago = await IdDelPagoAsync(e, await PagarAsync(e, idPersonal, "Tesoreria", null, (e.IdEfectivo, 500m)));

        await CuerpoAsync(
            await EditarLineasAsync(e.Ctx.Admin, idPago, new { lineas = Lineas((e.IdTransferencia, 500m)) }),
            HttpStatusCode.OK);

        await using var db = e.Ctx.Db();
        // El total no cambió: el egreso de tesorería del pago queda solo, sin ajuste.
        var tesoreria = await db.MovimientosTesoreria.Where(m => m.IdEmpresa == e.Ctx.IdEmpresa).ToListAsync();
        Assert.Equal((TipoMovimientoTesoreria.Pago, 500m), (Assert.Single(tesoreria).Tipo, tesoreria[0].Egreso));
        Assert.Equal(500m, await NetoDelBancoDelPagoAsync(e, idPago));
        Assert.Equal([(e.IdTransferencia, 500m)], await ItemsVivosAsync(e, idPago));
        Assert.Equal(4500m, await SaldoDelPersonalAsync(e, idPersonal));
    }

    /// <summary>LA CLÁUSULA: <c>ReglaDeLineasDePagoDePersonal.ExigirMismoTotal</c> sobre el pago leído bajo su lock. Las líneas
    /// rechazadas son válidas en todo lo demás (medio vivo, importes de dos decimales): la misma forma con el total correcto
    /// pasa, así que lo único que separa el 400 del 200 es el total. Evidencia de mutación: sin la llamada a
    /// <c>ExigirMismoTotal</c> el primer pedido responde 200 y la prueba falla.</summary>
    [Fact]
    public async Task UnasLineasQueNoSumanElImporteDelPagoSonRechazadasYNoEscribenNada()
    {
        var e = await PrepararAsync(nameof(UnasLineasQueNoSumanElImporteDelPagoSonRechazadasYNoEscribenNada));
        var idPersonal = await CrearPersonalAsync(e, 5000m);
        var idPago = await IdDelPagoAsync(e, await PagarAsync(e, idPersonal, "Tesoreria", null, (e.IdEfectivo, 300m)));
        var antes = await FotoAsync(e);

        var rechazo = await EditarLineasAsync(
            e.Ctx.Admin, idPago, new { lineas = Lineas((e.IdEfectivo, 100m), (e.IdTransferencia, 200.01m)) });
        var cuerpo = await CuerpoAsync(rechazo, HttpStatusCode.BadRequest);
        Assert.Equal("importe_distinto_del_pago", cuerpo.GetProperty("codigo").GetString());
        Assert.Contains("300.00", cuerpo.GetProperty("title").GetString());

        Assert.Equal(antes, await FotoAsync(e));
        Assert.Equal([(e.IdEfectivo, 300m)], await ItemsVivosAsync(e, idPago));

        await CuerpoAsync(
            await EditarLineasAsync(
                e.Ctx.Admin, idPago, new { lineas = Lineas((e.IdEfectivo, 100m), (e.IdTransferencia, 200m)) }),
            HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnPagoComunNoSeCorrigePorEstaRuta()
    {
        var e = await PrepararAsync(nameof(UnPagoComunNoSeCorrigePorEstaRuta));
        var alta = await e.Ctx.Admin.PostAsJsonAsync("/api/pagos/administracion", new
        {
            fecha = Hoy(), idEmpresa = e.Ctx.IdEmpresa, categoria = "Sueldos", concepto = "Sueldo sin asiento",
            lineas = Lineas((e.IdEfectivo, 300m))
        });
        var idPago = (await CuerpoAsync(alta, HttpStatusCode.Created)).GetProperty("id").GetInt32();

        Assert.Equal("pago_sin_movimiento_de_personal", await CodigoAsync(
            await EditarLineasAsync(e.Ctx.Admin, idPago, new { lineas = Lineas((e.IdTransferencia, 300m)) }),
            HttpStatusCode.Conflict));
        Assert.Equal([(e.IdEfectivo, 300m)], await ItemsVivosAsync(e, idPago));
    }

    [Fact]
    public async Task UnPagoInexistenteDeOtroTenantOAnuladoEs404AntesDeValidarElCuerpo()
    {
        var e = await PrepararAsync(nameof(UnPagoInexistenteDeOtroTenantOAnuladoEs404AntesDeValidarElCuerpo));
        var ajeno = await PrepararAsync(nameof(UnPagoInexistenteDeOtroTenantOAnuladoEs404AntesDeValidarElCuerpo) + "Ajeno");
        var idPersonalAjeno = await CrearPersonalAsync(ajeno, 1000m);
        var idPagoAjeno = await IdDelPagoAsync(
            ajeno, await PagarAsync(ajeno, idPersonalAjeno, "Tesoreria", null, (ajeno.IdEfectivo, 100m)));

        var idPersonal = await CrearPersonalAsync(e, 1000m);
        var idMovimiento = await PagarAsync(e, idPersonal, "Tesoreria", null, (e.IdEfectivo, 100m));
        var idPagoAnulado = await IdDelPagoAsync(e, idMovimiento);
        await CuerpoAsync(await AnularAsync(e.Ctx.Admin, idPersonal, idMovimiento), HttpStatusCode.OK);

        object[] cuerposInvalidos =
        [
            new { lineas = Array.Empty<object>() },
            new { lineas = Lineas((e.IdEfectivo, -5m)) },
            new { lineas = Lineas((e.IdEfectivo, 999m)) }
        ];
        foreach (var idPago in new[] { 999_999, idPagoAjeno, idPagoAnulado })
        {
            foreach (var cuerpo in cuerposInvalidos)
            {
                Assert.Equal(HttpStatusCode.NotFound, (await EditarLineasAsync(e.Ctx.Admin, idPago, cuerpo)).StatusCode);
            }

            var sinCuerpo = await e.Ctx.Admin.PutAsync(
                $"/api/pagos/administracion/{idPago}/lineas-de-personal", content: null);
            Assert.Equal(HttpStatusCode.NotFound, sinCuerpo.StatusCode);
        }

        Assert.Equal([(ajeno.IdEfectivo, 100m)], await ItemsVivosAsync(ajeno, idPagoAjeno));
    }

    // ---- anulación -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnularUnPagoDelCajonDeUnTurnoCerradoRecalculaElArqueoYDevuelveElSaldo()
    {
        var e = await PrepararAsync(nameof(AnularUnPagoDelCajonDeUnTurnoCerradoRecalculaElArqueoYDevuelveElSaldo));
        var idPersonal = await CrearPersonalAsync(e, 1000m);
        var idTurno = await AbrirTurnoAsync(e, 1000m);
        var idMovimiento = await PagarAsync(e, idPersonal, "CajaTurno", null, (e.IdEfectivo, 300m));
        var idPago = await IdDelPagoAsync(e, idMovimiento);
        await CerrarTurnoAsync(e, idTurno, 700m);

        var anulacion = await CuerpoAsync(await AnularAsync(e.Ctx.Admin, idPersonal, idMovimiento), HttpStatusCode.OK);

        Assert.Equal("ajuste", anulacion.GetProperty("tipo").GetString(), ignoreCase: true);
        Assert.Equal(300m, anulacion.GetProperty("importe").GetDecimal());
        Assert.Equal(1000m, anulacion.GetProperty("saldoResultante").GetDecimal());
        Assert.Equal(idMovimiento, anulacion.GetProperty("idMovimientoAnulado").GetInt32());
        Assert.Equal(Hoy(), anulacion.GetProperty("fecha").GetString());
        var idAnulacion = anulacion.GetProperty("idMovimiento").GetInt32();
        Assert.Equal(1000m, await SaldoDelPersonalAsync(e, idPersonal));

        await using (var db = e.Ctx.Db())
        {
            var asiento = await db.MovimientosPersonal.SingleAsync(m => m.Id == idAnulacion);
            Assert.Equal(
                (TipoMovimientoPersonal.Ajuste, 300m, 1000m, (int?)idMovimiento, FechaDelNegocio.Hoy()),
                (asiento.Tipo, asiento.Importe, asiento.SaldoResultante, asiento.IdMovimientoAnulado, asiento.Fecha));
            Assert.Equal($"Anulación del pago del {FechaDelNegocio.Hoy():dd/MM/yyyy}", asiento.Detalle);

            Assert.False(await db.Pagos.AnyAsync(p => p.Id == idPago));
            Assert.NotNull(await db.Pagos.IgnoreQueryFilters().Where(p => p.Id == idPago).Select(p => p.DeletedAt).SingleAsync());

            var efectivo = await db.ArqueosTurno.SingleAsync(a => a.IdTurnoCaja == idTurno && a.IdMedioPago == e.IdEfectivo);
            Assert.Equal((1000m, 700m), (efectivo.ImporteEsperado, efectivo.ImporteEsperadoOriginal!.Value));
            Assert.NotNull(await db.TurnosCaja.Where(t => t.Id == idTurno).Select(t => t.FechaRecalculo).SingleAsync());

            Assert.True(await db.Auditoria.AnyAsync(a => a.Accion == "pago.baja" && a.IdEntidad == idPago));
            var rastro = await db.Auditoria.SingleAsync(a => a.Accion == "pagopersonal.anulacion" && a.IdEntidad == idMovimiento);
            Assert.Equal("movimiento_personal", rastro.Entidad);
            using var anterior = JsonDocument.Parse(rastro.ValorAnterior!);
            using var nuevo = JsonDocument.Parse(rastro.ValorNuevo);
            Assert.Equal(700m, anterior.RootElement.GetProperty("saldo").GetDecimal());
            Assert.Equal([idPago], anterior.RootElement.GetProperty("id_pagos").EnumerateArray().Select(x => x.GetInt32()));
            Assert.Equal(JsonValueKind.Null, anterior.RootElement.GetProperty("descuento").ValueKind);
            Assert.Equal(1000m, nuevo.RootElement.GetProperty("saldo").GetDecimal());
            Assert.Equal(idAnulacion, nuevo.RootElement.GetProperty("id_movimiento_anulacion").GetInt32());
        }

        // El estado de cuenta dice quién anuló a quién; el pago anulado ya no tiene líneas vivas.
        var cuenta = await CuerpoAsync(
            await e.Ctx.Admin.GetAsync($"/api/personal/{idPersonal}/cuenta?historico=true"), HttpStatusCode.OK);
        var items = cuenta.GetProperty("items").EnumerateArray().ToList();
        var original = items.Single(i => i.GetProperty("idMovimiento").GetInt32() == idMovimiento);
        Assert.Equal(idAnulacion, original.GetProperty("anuladoPor").GetInt32());
        Assert.Equal(JsonValueKind.Null, original.GetProperty("idMovimientoAnulado").ValueKind);
        Assert.Equal(0, original.GetProperty("desglose").GetProperty("lineas").GetArrayLength());
        var reversa = items.Single(i => i.GetProperty("idMovimiento").GetInt32() == idAnulacion);
        Assert.Equal(idMovimiento, reversa.GetProperty("idMovimientoAnulado").GetInt32());
        Assert.Equal(JsonValueKind.Null, reversa.GetProperty("anuladoPor").ValueKind);
        Assert.All(
            items.Where(i => i.GetProperty("idMovimiento").GetInt32() != idMovimiento),
            i => Assert.Equal(JsonValueKind.Null, i.GetProperty("anuladoPor").ValueKind));
    }

    [Fact]
    public async Task AnularUnPagoDeTesoreriaCompensaLaTesoreriaYLaCuenta()
    {
        var e = await PrepararAsync(nameof(AnularUnPagoDeTesoreriaCompensaLaTesoreriaYLaCuenta));
        var idPersonal = await CrearPersonalAsync(e, 1000m);
        var idMovimiento = await PagarAsync(
            e, idPersonal, "Tesoreria", null, (e.IdTransferencia, 500m), (e.IdEfectivo, 200m));
        var idPago = await IdDelPagoAsync(e, idMovimiento);
        Assert.Equal(500m, await NetoDelBancoDelPagoAsync(e, idPago));

        await CuerpoAsync(await AnularAsync(e.Ctx.Admin, idPersonal, idMovimiento), HttpStatusCode.OK);

        await using var db = e.Ctx.Db();
        var tesoreria = await db.MovimientosTesoreria
            .Where(m => m.IdEmpresa == e.Ctx.IdEmpresa).OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(
            [(TipoMovimientoTesoreria.Pago, 0m, 700m), (TipoMovimientoTesoreria.Ajuste, 700m, 0m)],
            tesoreria.Select(m => (m.Tipo, m.Ingreso, m.Egreso)));
        Assert.Equal(0m, tesoreria[^1].Final);
        Assert.Equal(0m, await NetoDelBancoDelPagoAsync(e, idPago));
        Assert.Equal(1000m, await SaldoDelPersonalAsync(e, idPersonal));
    }

    [Fact]
    public async Task AnularUnPagoSoloDeDescuentoDevuelveLaDeudaAlCliente()
    {
        var e = await PrepararAsync(nameof(AnularUnPagoSoloDeDescuentoDevuelveLaDeudaAlCliente));
        var idCliente = await CrearClienteConDeudaAsync(e, 4000m);
        var idPersonal = await CrearPersonalAsync(e, 5000m, idCliente);
        var idMovimiento = await PagarAsync(e, idPersonal, "Tesoreria", 1000m);
        Assert.Equal(3000m, await SaldoDelClienteAsync(e, idCliente));

        await CuerpoAsync(await AnularAsync(e.Ctx.Admin, idPersonal, idMovimiento), HttpStatusCode.OK);

        Assert.Equal(4000m, await SaldoDelClienteAsync(e, idCliente));
        Assert.Equal(5000m, await SaldoDelPersonalAsync(e, idPersonal));

        await using var db = e.Ctx.Db();
        var ajuste = await db.MovimientosCuentaCorriente
            .Where(m => m.IdCliente == idCliente).OrderByDescending(m => m.Id).FirstAsync();
        Assert.Equal(
            (TipoMovimientoCc.Ajuste, 1000m, 4000m, e.IdPuntoVenta, (int?)null),
            (ajuste.Tipo, ajuste.Importe, ajuste.SaldoResultante, ajuste.IdPuntoVenta, ajuste.IdMovimientoPersonal));
        Assert.Equal("Anulación del descuento por pago de personal: Ana", ajuste.Detalle);
        Assert.Equal(0, await db.Pagos.IgnoreQueryFilters().CountAsync(p => p.IdMovimientoPersonal == idMovimiento));

        var rastro = await db.Auditoria.SingleAsync(a => a.Accion == "pagopersonal.anulacion" && a.IdEntidad == idMovimiento);
        using var anterior = JsonDocument.Parse(rastro.ValorAnterior!);
        Assert.Equal(1000m, anterior.RootElement.GetProperty("descuento").GetDecimal());
        Assert.Equal(idCliente, anterior.RootElement.GetProperty("id_cliente").GetInt32());
        Assert.Equal(0, anterior.RootElement.GetProperty("id_pagos").GetArrayLength());
    }

    [Fact]
    public async Task AnularUnPagoConDescuentoYLineasRevierteLosCuatroLibros()
    {
        var e = await PrepararAsync(nameof(AnularUnPagoConDescuentoYLineasRevierteLosCuatroLibros));
        var idCliente = await CrearClienteConDeudaAsync(e, 4000m);
        var idPersonal = await CrearPersonalAsync(e, 5000m, idCliente);
        var idMovimiento = await PagarAsync(e, idPersonal, "Tesoreria", 500m, (e.IdEfectivo, 300m));
        var idPago = await IdDelPagoAsync(e, idMovimiento);
        Assert.Equal(4200m, await SaldoDelPersonalAsync(e, idPersonal));

        var anulacion = await CuerpoAsync(await AnularAsync(e.Ctx.Admin, idPersonal, idMovimiento), HttpStatusCode.OK);

        Assert.Equal(800m, anulacion.GetProperty("importe").GetDecimal());
        Assert.Equal(5000m, await SaldoDelPersonalAsync(e, idPersonal));
        Assert.Equal(4000m, await SaldoDelClienteAsync(e, idCliente));

        await using var db = e.Ctx.Db();
        Assert.False(await db.Pagos.AnyAsync(p => p.Id == idPago));
        Assert.Equal(
            [300m, -300m],
            await db.MovimientosTesoreria.Where(m => m.IdEmpresa == e.Ctx.IdEmpresa).OrderBy(m => m.Id)
                .Select(m => m.Egreso - m.Ingreso).ToListAsync());
    }

    [Fact]
    public async Task UnPagoYaAnuladoNoSeVuelveAAnularYNoSeEscribeNada()
    {
        var e = await PrepararAsync(nameof(UnPagoYaAnuladoNoSeVuelveAAnularYNoSeEscribeNada));
        var idPersonal = await CrearPersonalAsync(e, 1000m);
        var idMovimiento = await PagarAsync(e, idPersonal, "Tesoreria", null, (e.IdEfectivo, 100m));
        await CuerpoAsync(await AnularAsync(e.Ctx.Admin, idPersonal, idMovimiento), HttpStatusCode.OK);
        var antes = await FotoAsync(e);

        Assert.Equal("pago_personal_ya_anulado", await CodigoAsync(
            await AnularAsync(e.Ctx.Admin, idPersonal, idMovimiento), HttpStatusCode.Conflict));

        Assert.Equal(antes, await FotoAsync(e));
        Assert.Equal(1000m, await SaldoDelPersonalAsync(e, idPersonal));
    }

    [Fact]
    public async Task UnMovimientoQueNoEsPagoNoSeAnula()
    {
        var e = await PrepararAsync(nameof(UnMovimientoQueNoEsPagoNoSeAnula));
        var idPersonal = await CrearPersonalAsync(e, 1000m);
        var idMovimiento = await PagarAsync(e, idPersonal, "Tesoreria", null, (e.IdEfectivo, 100m));
        var idAnulacion = (await CuerpoAsync(await AnularAsync(e.Ctx.Admin, idPersonal, idMovimiento), HttpStatusCode.OK))
            .GetProperty("idMovimiento").GetInt32();

        int idAjuste;
        await using (var db = e.Ctx.Db())
        {
            idAjuste = await db.MovimientosPersonal
                .Where(m => m.IdPersonal == idPersonal && m.Tipo == TipoMovimientoPersonal.Ajuste && m.IdMovimientoAnulado == null)
                .Select(m => m.Id).SingleAsync();
        }

        var antes = await FotoAsync(e);
        foreach (var id in new[] { idAjuste, idAnulacion })
        {
            Assert.Equal("movimiento_no_es_pago", await CodigoAsync(
                await AnularAsync(e.Ctx.Admin, idPersonal, id), HttpStatusCode.Conflict));
        }

        Assert.Equal(antes, await FotoAsync(e));
    }

    [Fact]
    public async Task UnPersonalOUnMovimientoInexistenteAjenoODeOtroPersonalEs404()
    {
        var e = await PrepararAsync(nameof(UnPersonalOUnMovimientoInexistenteAjenoODeOtroPersonalEs404));
        var ajeno = await PrepararAsync(nameof(UnPersonalOUnMovimientoInexistenteAjenoODeOtroPersonalEs404) + "Ajeno");
        var idPersonalAjeno = await CrearPersonalAsync(ajeno, 1000m);
        var idMovimientoAjeno = await PagarAsync(ajeno, idPersonalAjeno, "Tesoreria", null, (ajeno.IdEfectivo, 100m));

        var idPersonal = await CrearPersonalAsync(e, 1000m);
        var idOtro = await CrearPersonalAsync(e, 1000m);
        var idMovimiento = await PagarAsync(e, idPersonal, "Tesoreria", null, (e.IdEfectivo, 100m));
        var antes = await FotoAsync(e);

        (int IdPersonal, int IdMovimiento)[] casos =
        [
            (999_999, idMovimiento), (idPersonal, 999_999), (idOtro, idMovimiento), (idPersonalAjeno, idMovimientoAjeno),
            (idPersonal, idMovimientoAjeno)
        ];
        foreach (var (idP, idM) in casos)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await AnularAsync(e.Ctx.Admin, idP, idM)).StatusCode);
        }

        Assert.Equal(antes, await FotoAsync(e));
        Assert.Equal(900m, await SaldoDelPersonalAsync(ajeno, idPersonalAjeno));
    }

    [Fact]
    public async Task ConElClienteDelDescuentoDadoDeBajaLaAnulacionSeRechazaYNoEscribeNada()
    {
        var e = await PrepararAsync(nameof(ConElClienteDelDescuentoDadoDeBajaLaAnulacionSeRechazaYNoEscribeNada));
        var idCliente = await CrearClienteConDeudaAsync(e, 4000m);
        var idPersonal = await CrearPersonalAsync(e, 5000m, idCliente);
        var idMovimiento = await PagarAsync(e, idPersonal, "Tesoreria", 500m, (e.IdEfectivo, 300m));
        await using (var db = e.Ctx.Db())
        {
            var cliente = await db.Clientes.SingleAsync(c => c.Id == idCliente);
            cliente.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var antes = await FotoAsync(e);

        Assert.Equal("cliente_del_descuento_dado_de_baja", await CodigoAsync(
            await AnularAsync(e.Ctx.Admin, idPersonal, idMovimiento), HttpStatusCode.Conflict));

        Assert.Equal(antes, await FotoAsync(e));
        Assert.Equal(4200m, await SaldoDelPersonalAsync(e, idPersonal));
        Assert.Equal(3500m, await SaldoDelClienteAsync(e, idCliente));
    }

    /// <summary>Forma (b) de ef-retry-safe-writes con un fallo transitorio real: la primera escritura de auditoría de la
    /// anulación falla con 40001 y la anulación no se reintenta (un solo intento) ni deja nada escrito en ningún libro.</summary>
    [Fact]
    public async Task UnFalloTransitorioEnLaAnulacionNoSeReintentaYNoDejaNada()
    {
        var e = await PrepararAsync(nameof(UnFalloTransitorioEnLaAnulacionNoSeReintentaYNoDejaNada));
        var idPersonal = await CrearPersonalAsync(e, 1000m);
        var idMovimiento = await PagarAsync(e, idPersonal, "Tesoreria", null, (e.IdEfectivo, 100m));
        var antes = await FotoAsync(e);
        var interceptor = new InterceptorQueRompeLaPrimeraEscritura("auditoria", "40001");

        HttpResponseMessage respuesta;
        using (fixture.ConInterceptorEnElHost(interceptor))
        {
            respuesta = await AnularAsync(e.Ctx.Admin, idPersonal, idMovimiento);
        }

        Assert.Equal("resultado_incierto", await CodigoAsync(respuesta, HttpStatusCode.ServiceUnavailable));
        Assert.Equal(1, interceptor.Intentos);
        Assert.Equal(antes, await FotoAsync(e));
        Assert.Equal(900m, await SaldoDelPersonalAsync(e, idPersonal));
    }

    private static async Task<bool> EsperarBackendsBloqueadosAsync(NpgsqlConnection conexionPoll, int cantidad, params int[] excluidos)
    {
        var limite = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < limite)
        {
            await using var comando = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND NOT (pid = ANY($1))", conexionPoll);
            comando.Parameters.AddWithValue(excluidos);
            if ((long)(await comando.ExecuteScalarAsync())! >= cantidad)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private static async Task<int> BackendPidAsync(NpgsqlConnection conexion) =>
        (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", conexion).ExecuteScalarAsync())!;

    /// <summary>Las dos anulaciones pasan el chequeo previo mientras otra transacción retiene la fila del personal, y las dos se
    /// observan esperando ese lock. Al soltarlo, una anula y la otra ve la anulación ya comiteada bajo el lock del personal: una
    /// sola anulación, un solo ajuste y el saldo devuelto una vez.</summary>
    [Fact]
    public async Task DosAnulacionesConcurrentesDejanUnaSolaYLaOtraEsRechazada()
    {
        var e = await PrepararAsync(nameof(DosAnulacionesConcurrentesDejanUnaSolaYLaOtraEsRechazada));
        var idPersonal = await CrearPersonalAsync(e, 1000m);
        var idMovimiento = await PagarAsync(e, idPersonal, "Tesoreria", null, (e.IdEfectivo, 100m));

        await using var conexionBloqueo = await fixture.AbrirConexionCrudaAsync("tenant", e.Ctx.IdTenant);
        var pidBloqueo = await BackendPidAsync(conexionBloqueo);
        await using var transaccionBloqueo = await conexionBloqueo.BeginTransactionAsync();
        await using (var bloquear = new NpgsqlCommand(
            "SELECT 1 FROM personal WHERE id_personal = $1 FOR UPDATE", conexionBloqueo, transaccionBloqueo))
        {
            bloquear.Parameters.AddWithValue(idPersonal);
            await bloquear.ExecuteNonQueryAsync();
        }

        var primera = AnularAsync(e.Ctx.Admin, idPersonal, idMovimiento);
        var segunda = AnularAsync(e.Ctx.Admin, idPersonal, idMovimiento);

        await using var conexionPoll = await fixture.AbrirConexionCrudaAsync("tenant", e.Ctx.IdTenant);
        var pidPoll = await BackendPidAsync(conexionPoll);
        Assert.True(
            await EsperarBackendsBloqueadosAsync(conexionPoll, 2, pidBloqueo, pidPoll),
            "Las dos anulaciones no se observaron esperando el lock: la prueba no está probando la carrera.");

        await transaccionBloqueo.CommitAsync();

        var respuestas = await Task.WhenAll(primera, segunda);
        Assert.Single(respuestas, r => r.StatusCode == HttpStatusCode.OK);
        var rechazada = Assert.Single(respuestas, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("pago_personal_ya_anulado", await CodigoAsync(rechazada, HttpStatusCode.Conflict));

        await using var db = e.Ctx.Db();
        Assert.Equal(1, await db.MovimientosPersonal.CountAsync(m => m.IdMovimientoAnulado == idMovimiento));
        Assert.Equal(1000m, await SaldoDelPersonalAsync(e, idPersonal));
        Assert.Equal(1, await db.Auditoria.CountAsync(a => a.Accion == "pagopersonal.anulacion"));
    }

    /// <summary>El índice único parcial y el CHECK sin la aplicación delante: una segunda anulación del mismo pago choca con
    /// <c>ux_movimientos_personal_movimiento_anulado</c> (23505) y un asiento que no es ajuste no puede apuntar a otro
    /// (<c>ck_movimientos_personal_anulacion</c>, 23514).</summary>
    [Fact]
    public async Task ElEsquemaAdmiteUnaSolaAnulacionPorPagoYSoloComoAjuste()
    {
        var e = await PrepararAsync(nameof(ElEsquemaAdmiteUnaSolaAnulacionPorPagoYSoloComoAjuste));
        var idPersonal = await CrearPersonalAsync(e, 1000m);
        var idMovimiento = await PagarAsync(e, idPersonal, "Tesoreria", null, (e.IdEfectivo, 100m));
        await CuerpoAsync(await AnularAsync(e.Ctx.Admin, idPersonal, idMovimiento), HttpStatusCode.OK);

        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", e.Ctx.IdTenant);

        async Task<PostgresException> InsertarAsync(string tipo, decimal importe)
        {
            await using var comando = new NpgsqlCommand(
                "INSERT INTO movimientos_personal (id_tenant, id_personal, id_empleado, fecha, tipo, importe, saldo_resultante, " +
                "created_at, id_movimiento_anulado) VALUES ($1, $2, $3, current_date, $4::tipo_movimiento_personal, $5, 0, now(), $6)",
                conexion);
            comando.Parameters.AddWithValue(e.Ctx.IdTenant);
            comando.Parameters.AddWithValue(idPersonal);
            comando.Parameters.AddWithValue(e.Ctx.IdUsuarioAdmin);
            comando.Parameters.AddWithValue(tipo);
            comando.Parameters.AddWithValue(importe);
            comando.Parameters.AddWithValue(idMovimiento);
            return await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        }

        var duplicada = await InsertarAsync("ajuste", 100m);
        Assert.Equal(("23505", "ux_movimientos_personal_movimiento_anulado"), (duplicada.SqlState, duplicada.ConstraintName));

        var noAjuste = await InsertarAsync("pago", -100m);
        Assert.Equal(("23514", "ck_movimientos_personal_anulacion"), (noAjuste.SqlState, noAjuste.ConstraintName));
    }

    // ---- autorización ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SoloElAdminCorrigeYAnula()
    {
        var e = await PrepararAsync(nameof(SoloElAdminCorrigeYAnula));
        var idPersonal = await CrearPersonalAsync(e, 1000m);
        var idMovimiento = await PagarAsync(e, idPersonal, "Tesoreria", null, (e.IdEfectivo, 100m));
        var idPago = await IdDelPagoAsync(e, idMovimiento);
        var antes = await FotoAsync(e);

        foreach (var cliente in new[] { e.Ctx.Supervisor, e.Ctx.Vendedor })
        {
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await EditarLineasAsync(cliente, idPago, new { lineas = Lineas((e.IdTransferencia, 100m)) })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await AnularAsync(cliente, idPersonal, idMovimiento)).StatusCode);
        }

        Assert.Equal(antes, await FotoAsync(e));

        await CuerpoAsync(
            await EditarLineasAsync(e.Ctx.Admin, idPago, new { lineas = Lineas((e.IdTransferencia, 100m)) }),
            HttpStatusCode.OK);
        await CuerpoAsync(await AnularAsync(e.Ctx.Admin, idPersonal, idMovimiento), HttpStatusCode.OK);
    }
}
