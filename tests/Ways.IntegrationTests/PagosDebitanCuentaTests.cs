using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Application.Plantel;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Débitos de los pagos en las cuentas de tesorería (<see cref="DebitosDePagoEnCuenta"/>) contra Postgres real, por los endpoints de
/// cada escritor de ítems de pago. Escenario: la cuenta Banco (saldo inicial 1000) recibe Transferencia y la cuenta Billetera (saldo
/// inicial 0) recibe el medio Billetera; Efectivo y Cheque no tienen cuenta.
///
/// Evidencia de mutación (mutation-proof-tests), registrada al escribir las pruebas: (1) quitar el filtro de empresa del lock de
/// cuentas (<c>idEmpresa</c> → <c>null</c> en <see cref="DebitosDePagoEnCuenta"/>) hace fallar
/// <see cref="UnaCuentaDeOtraEmpresaNoSeDebita"/>; (2) restar lo ya debitado como cero hace fallar
/// <see cref="EditarElImporteEscribeSoloLaDiferencia"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PagosDebitanCuentaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Escenario(
        ContextoDeTesoreriaDePrueba Ctx, int IdPuntoVenta, int IdEfectivo, int IdTransferencia, int IdBilletera, int IdCheque,
        int IdBanco, int IdCuentaBilletera);

    private async Task<Escenario> PrepararAsync(string nombre)
    {
        var ctx = await ContextoDeTesoreriaDePrueba.PrepararAsync(fixture, nombre);
        var ahora = DateTimeOffset.UtcNow;

        await using var db = ctx.Db();
        var idPuntoVenta = await db.PuntosVenta.Where(p => p.IdEmpresa == ctx.IdEmpresa).Select(p => p.Id).FirstAsync();
        var idEfectivo = await db.MediosPago.Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        var banco = new CuentaTesoreria
        {
            IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = "Banco", SaldoInicial = 1000m, CreatedAt = ahora, UpdatedAt = ahora
        };
        var cuentaBilletera = new CuentaTesoreria
        {
            IdTenant = ctx.IdTenant, IdEmpresa = ctx.IdEmpresa, Nombre = "Billetera", SaldoInicial = 0m, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.CuentasTesoreria.AddRange(banco, cuentaBilletera);
        await db.SaveChangesAsync();

        var transferencia = await db.MediosPago.FirstAsync(m => m.Nombre == "Transferencia");
        transferencia.IdCuentaTesoreria = banco.Id;
        var billetera = NuevoMedioElectronico(ctx.IdTenant, "Billetera", cuentaBilletera.Id, ahora);
        var cheque = NuevoMedioElectronico(ctx.IdTenant, "Cheque", null, ahora);
        db.MediosPago.AddRange(billetera, cheque);
        await db.SaveChangesAsync();

        return new Escenario(ctx, idPuntoVenta, idEfectivo, transferencia.Id, billetera.Id, cheque.Id, banco.Id, cuentaBilletera.Id);
    }

    private static MedioPago NuevoMedioElectronico(int idTenant, string nombre, int? idCuenta, DateTimeOffset ahora) => new()
    {
        IdTenant = idTenant, Nombre = nombre, Orden = 20, Comportamiento = ComportamientoMedioPago.Electronico, AdmiteVuelto = false,
        RequiereReferencia = false, IdCuentaTesoreria = idCuenta, CreatedAt = ahora, UpdatedAt = ahora
    };

    private static object[] Lineas(params (int IdMedio, decimal Importe)[] lineas) =>
        lineas.Select(l => (object)new { idMedioPago = l.IdMedio, importe = l.Importe, referencia = (string?)null }).ToArray();

    private static async Task<int> IdCreadoAsync(HttpResponseMessage respuesta)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<JsonElement>(cuerpo).GetProperty("id").GetInt32();
    }

    private static async Task<int> AltaDeAdminAsync(Escenario e, params (int IdMedio, decimal Importe)[] lineas) =>
        await IdCreadoAsync(await e.Ctx.Admin.PostAsJsonAsync("/api/pagos/administracion", new
        {
            fecha = FechaDelNegocio.Hoy().ToString("yyyy-MM-dd"), idEmpresa = e.Ctx.IdEmpresa, categoria = "Servicios",
            concepto = "Luz", lineas = Lineas(lineas)
        }));

    private static object Edicion(params (int IdMedio, decimal Importe)[] lineas) =>
        new { categoria = "Servicios", concepto = "Luz", lineas = Lineas(lineas) };

    private static async Task EditarDeAdminAsync(Escenario e, int idPago, params (int IdMedio, decimal Importe)[] lineas)
    {
        var respuesta = await e.Ctx.Admin.PutAsJsonAsync($"/api/pagos/administracion/{idPago}", Edicion(lineas));
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, await respuesta.Content.ReadAsStringAsync());
    }

    private static async Task<List<MovimientoCuenta>> FilasAsync(Escenario e)
    {
        await using var db = e.Ctx.Db();
        return await db.MovimientosCuenta.AsNoTracking().OrderBy(m => m.Fecha).ThenBy(m => m.Id).ToListAsync();
    }

    private static async Task<decimal> SaldoDelAbmAsync(Escenario e, int idCuenta)
    {
        var cuenta = await e.Ctx.Admin.GetFromJsonAsync<CuentaTesoreriaListado>($"/api/cuentas-tesoreria/{idCuenta}", OpcionesJson);
        return cuenta!.SaldoActual;
    }

    private static decimal Neto(IEnumerable<MovimientoCuenta> filas, int idCuenta) =>
        filas.Where(f => f.IdCuentaTesoreria == idCuenta).Sum(f => f.Egreso - f.Ingreso);

    [Fact]
    public async Task UnAltaConMedioConCuentaDebitaSoloEsaCuentaConUnaFilaEncadenada()
    {
        var e = await PrepararAsync("DebAlta");

        var idPago = await AltaDeAdminAsync(e, (e.IdTransferencia, 300m), (e.IdEfectivo, 100m));

        var fila = Assert.Single(await FilasAsync(e));
        Assert.Equal(
            (e.IdBanco, TipoMovimientoCuenta.Pago, (int?)idPago, (int?)e.IdTransferencia, (int?)null, $"Pago {idPago}", 1000m, 0m, 300m, 700m,
                e.Ctx.IdUsuarioAdmin),
            (fila.IdCuentaTesoreria, fila.Tipo, fila.IdPago, fila.IdMedioPago, fila.IdTurnoCaja, fila.Concepto, fila.Inicio, fila.Ingreso,
                fila.Egreso, fila.Final, fila.IdEmpleado));
        Assert.Null(fila.BaseBruta);
        Assert.Null(fila.PorcentajeAplicado);
        Assert.Equal(700m, await SaldoDelAbmAsync(e, e.IdBanco));
        Assert.Equal(0m, await SaldoDelAbmAsync(e, e.IdCuentaBilletera));
    }

    [Fact]
    public async Task EditarElImporteEscribeSoloLaDiferencia()
    {
        var e = await PrepararAsync("DebEdicion");
        var idPago = await AltaDeAdminAsync(e, (e.IdTransferencia, 300m));

        await EditarDeAdminAsync(e, idPago, (e.IdTransferencia, 450m));
        await EditarDeAdminAsync(e, idPago, (e.IdTransferencia, 200m));
        await EditarDeAdminAsync(e, idPago, (e.IdTransferencia, 200m));

        var filas = await FilasAsync(e);
        Assert.Equal(
            [(0m, 300m, 1000m, 700m), (0m, 150m, 700m, 550m), (250m, 0m, 550m, 800m)],
            filas.Select(f => (f.Ingreso, f.Egreso, f.Inicio, f.Final)));
        Assert.All(filas, f => Assert.Equal(idPago, f.IdPago));
        Assert.Equal(800m, await SaldoDelAbmAsync(e, e.IdBanco));
    }

    [Fact]
    public async Task CambiarElMedioAOtroConOtraCuentaMueveElDebito()
    {
        var e = await PrepararAsync("DebCambioMedio");
        var idPago = await AltaDeAdminAsync(e, (e.IdTransferencia, 300m));

        await EditarDeAdminAsync(e, idPago, (e.IdBilletera, 300m));

        var filas = await FilasAsync(e);
        Assert.Equal(3, filas.Count);
        Assert.Equal(0m, Neto(filas, e.IdBanco));
        Assert.Equal(300m, Neto(filas, e.IdCuentaBilletera));
        var reversa = filas.Single(f => f.IdCuentaTesoreria == e.IdBanco && f.Ingreso > 0m);
        Assert.Equal((e.IdTransferencia, 300m), (reversa.IdMedioPago!.Value, reversa.Ingreso));
        var debito = filas.Single(f => f.IdCuentaTesoreria == e.IdCuentaBilletera);
        Assert.Equal((e.IdBilletera, 300m), (debito.IdMedioPago!.Value, debito.Egreso));
        Assert.Equal(1000m, await SaldoDelAbmAsync(e, e.IdBanco));
        Assert.Equal(-300m, await SaldoDelAbmAsync(e, e.IdCuentaBilletera));
    }

    [Fact]
    public async Task UnPagoEnEfectivoOConUnMedioSinCuentaNoEscribeNada()
    {
        var e = await PrepararAsync("DebSinCuenta");
        var idPago = await AltaDeAdminAsync(e, (e.IdEfectivo, 100m), (e.IdCheque, 50m));

        await EditarDeAdminAsync(e, idPago, (e.IdCheque, 80m));
        Assert.Equal(HttpStatusCode.NoContent, (await e.Ctx.Admin.DeleteAsync($"/api/pagos/administracion/{idPago}")).StatusCode);

        Assert.Empty(await FilasAsync(e));
    }

    [Fact]
    public async Task LaBajaDevuelveTodoLoDebitadoEnCadaCuenta()
    {
        var e = await PrepararAsync("DebBaja");
        var idPago = await AltaDeAdminAsync(e, (e.IdTransferencia, 300m), (e.IdBilletera, 80m), (e.IdEfectivo, 20m));

        Assert.Equal(HttpStatusCode.NoContent, (await e.Ctx.Admin.DeleteAsync($"/api/pagos/administracion/{idPago}")).StatusCode);

        var filas = await FilasAsync(e);
        Assert.Equal(4, filas.Count);
        Assert.Equal(0m, Neto(filas, e.IdBanco));
        Assert.Equal(0m, Neto(filas, e.IdCuentaBilletera));
        Assert.Equal(1000m, await SaldoDelAbmAsync(e, e.IdBanco));
        Assert.Equal(0m, await SaldoDelAbmAsync(e, e.IdCuentaBilletera));
    }

    [Fact]
    public async Task UnPagoDelPosDebitaAlAltaYEnSuEdicion()
    {
        var e = await PrepararAsync("DebPos");
        var apertura = await e.Ctx.Admin.PostAsJsonAsync("/api/caja/turnos", new SolicitudDeApertura(e.IdPuntoVenta, 0m, null));
        Assert.True(apertura.StatusCode == HttpStatusCode.Created, await apertura.Content.ReadAsStringAsync());

        var idPago = await IdCreadoAsync(await e.Ctx.Admin.PostAsJsonAsync("/api/pagos", new
        {
            idPuntoVenta = e.IdPuntoVenta, categoria = "Servicios", concepto = "Flete", lineas = Lineas((e.IdTransferencia, 120m))
        }));
        var edicion = await e.Ctx.Admin.PutAsJsonAsync($"/api/pagos/{idPago}", Edicion((e.IdTransferencia, 100m)));
        Assert.True(edicion.StatusCode == HttpStatusCode.OK, await edicion.Content.ReadAsStringAsync());

        var filas = await FilasAsync(e);
        Assert.Equal([(0m, 120m), (20m, 0m)], filas.Select(f => (f.Ingreso, f.Egreso)));
        Assert.Equal(900m, await SaldoDelAbmAsync(e, e.IdBanco));
    }

    [Fact]
    public async Task UnPagoDePersonalDeTesoreriaDebitaLaCuenta()
    {
        var e = await PrepararAsync("DebPersonal");
        var idPersonal = await IdCreadoAsync(await e.Ctx.Admin.PostAsJsonAsync(
            "/api/personal", new DatosDePersonal("Ana", null, null, null, 1000m, null, null, null)));

        var respuesta = await e.Ctx.Admin.PostAsJsonAsync($"/api/personal/{idPersonal}/pagos", new
        {
            fecha = FechaDelNegocio.Hoy().ToString("yyyy-MM-dd"), idEmpresa = e.Ctx.IdEmpresa, origenFondos = "Tesoreria",
            lineas = Lineas((e.IdBilletera, 500m))
        });
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, await respuesta.Content.ReadAsStringAsync());

        var fila = Assert.Single(await FilasAsync(e));
        Assert.Equal((e.IdCuentaBilletera, 500m), (fila.IdCuentaTesoreria, fila.Egreso));
        Assert.Equal(-500m, await SaldoDelAbmAsync(e, e.IdCuentaBilletera));
    }

    [Fact]
    public async Task PagarUnaCompraDebitaLaCuenta()
    {
        var e = await PrepararAsync("DebCompra");
        int idProveedor, idAlicuota, idTipo;
        await using (var plataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var ahora = DateTimeOffset.UtcNow;
            idAlicuota = await plataforma.AlicuotasIva.Where(a => a.Nombre == "21%").Select(a => a.Id).FirstAsync();
            idTipo = await plataforma.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();
            var condicion = new CondicionFiscal { Codigo = "DebCompra-CF", Nombre = "DebCompra", CreatedAt = ahora, UpdatedAt = ahora };
            plataforma.CondicionesFiscales.Add(condicion);
            await plataforma.SaveChangesAsync();
            var proveedor = new Proveedor
            {
                IdTenant = e.Ctx.IdTenant, RazonSocial = "Proveedor", IdCondicionFiscal = condicion.Id, CreatedAt = ahora, UpdatedAt = ahora
            };
            plataforma.Proveedores.Add(proveedor);
            await plataforma.SaveChangesAsync();
            idProveedor = proveedor.Id;
        }

        var alta = await e.Ctx.Admin.PostAsJsonAsync("/api/compras", new SolicitudDeCompra(
            idProveedor, idTipo, e.IdPuntoVenta, "0001-00000001", FechaDelNegocio.Hoy(), null,
            [new LineaDeCompraSolicitada(null, "Concepto", 1m, null, null, 1000m, 0m, idAlicuota)]));
        var idCompra = await IdCreadoAsync(alta);
        var confirmacion = await e.Ctx.Admin.PostAsync($"/api/compras/{idCompra}/confirmar", null);
        Assert.True(confirmacion.StatusCode == HttpStatusCode.OK, await confirmacion.Content.ReadAsStringAsync());

        var pago = await e.Ctx.Admin.PostAsJsonAsync(
            $"/api/compras/{idCompra}/pagos", new { fecha = FechaDelNegocio.Hoy().ToString("yyyy-MM-dd"), importe = 400m,
                idMedioPago = e.IdTransferencia });
        Assert.True(pago.StatusCode == HttpStatusCode.Created, await pago.Content.ReadAsStringAsync());

        var fila = Assert.Single(await FilasAsync(e));
        Assert.Equal((e.IdBanco, 400m, 600m), (fila.IdCuentaTesoreria, fila.Egreso, fila.Final));
    }

    [Fact]
    public async Task UnaCuentaDeOtraEmpresaNoSeDebita()
    {
        var e = await PrepararAsync("DebOtraEmpresa");
        int idAjena, idMedioAjeno;
        await using (var db = e.Ctx.Db())
        {
            var ahora = DateTimeOffset.UtcNow;
            var otra = new Empresa { IdTenant = e.Ctx.IdTenant, RazonSocial = "Otra empresa", CreatedAt = ahora, UpdatedAt = ahora };
            db.Empresas.Add(otra);
            await db.SaveChangesAsync();
            var ajena = new CuentaTesoreria
            {
                IdTenant = e.Ctx.IdTenant, IdEmpresa = otra.Id, Nombre = "Ajena", SaldoInicial = 50m, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.CuentasTesoreria.Add(ajena);
            await db.SaveChangesAsync();
            var medio = NuevoMedioElectronico(e.Ctx.IdTenant, "Billetera ajena", ajena.Id, ahora);
            db.MediosPago.Add(medio);
            await db.SaveChangesAsync();
            (idAjena, idMedioAjeno) = (ajena.Id, medio.Id);
        }

        await AltaDeAdminAsync(e, (idMedioAjeno, 300m));

        Assert.Empty(await FilasAsync(e));
        Assert.Equal(50m, await SaldoDelAbmAsync(e, idAjena));
    }

    [Fact]
    public async Task ElLibroDeTesoreriaNoCuentaDosVecesElPagoDebitado()
    {
        var e = await PrepararAsync("DebLibro");
        var hasta = DateTimeOffset.UtcNow.AddHours(1);
        var desde = hasta.AddDays(-2);
        var url = $"/api/reportes/libro-tesoreria?idEmpresa={e.Ctx.IdEmpresa}&desde={Uri.EscapeDataString(desde.ToString("O"))}" +
                  $"&hasta={Uri.EscapeDataString(hasta.ToString("O"))}";
        async Task<decimal> SaldoDelLibroAsync() =>
            (await e.Ctx.Admin.GetFromJsonAsync<LibroDeTesoreria>(url, OpcionesJson))!.SaldoActual.PorCuenta
            .Where(c => c.IdCuentaTesoreria == e.IdBanco).Sum(c => c.Importe);

        var antes = await SaldoDelLibroAsync();
        await AltaDeAdminAsync(e, (e.IdTransferencia, 300m));

        Assert.Single(await FilasAsync(e));
        Assert.Equal(antes - 300m, await SaldoDelLibroAsync());
    }

    [Fact]
    public async Task DosEdicionesConcurrentesDelMismoPagoDejanElDebitoIgualAlImporteFinal()
    {
        var e = await PrepararAsync("DebConcurrencia");
        var idPago = await AltaDeAdminAsync(e, (e.IdTransferencia, 300m));

        var respuestas = await Task.WhenAll(
            e.Ctx.Admin.PutAsJsonAsync($"/api/pagos/administracion/{idPago}", Edicion((e.IdTransferencia, 500m))),
            e.Ctx.Admin.PutAsJsonAsync($"/api/pagos/administracion/{idPago}", Edicion((e.IdBilletera, 100m))));
        Assert.All(respuestas, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        await using var db = e.Ctx.Db();
        var final = await db.ItemsPago.Where(i => i.IdPago == idPago && i.DeletedAt == null).SingleAsync();
        var filas = await FilasAsync(e);
        Assert.Equal(final.IdMedioPago == e.IdTransferencia ? 500m : 0m, Neto(filas, e.IdBanco));
        Assert.Equal(final.IdMedioPago == e.IdBilletera ? 100m : 0m, Neto(filas, e.IdCuentaBilletera));
        Assert.Equal(1000m - Neto(filas, e.IdBanco), await SaldoDelAbmAsync(e, e.IdBanco));
    }
}
