using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Application.Ventas;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-desktop-pos ("Ventas del turno"): <c>GET /api/ventas/por-turno/{idTurno}</c> —
/// <see cref="ServicioDeVentas.ListarPorTurnoAsync"/>, un listado DEDICADO (no el paginado genérico
/// de <c>GET /api/ventas</c>, que no expone <c>idTurno</c> como filtro): filtra por
/// <c>id_turno_caja</c>, incluye anulados, y trae cliente/medios de pago vía dos consultas extra
/// indexadas (nunca N+1) porque el conjunto de un turno está acotado.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class VentasPorTurnoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static long _numeroSecuencial = 1;

    private sealed record Contexto(int IdTenant, int IdPuntoVenta, int IdEmpleadoAdmin, int IdTipoComprobanteTx, HttpClient Admin);

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

        var idTipoComprobanteTx = await fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma)
            .TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();

        return new Contexto(resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idTipoComprobanteTx, admin);
    }

    private static async Task<int> AbrirTurnoAsync(Contexto ctx)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, 0m, "Apertura de prueba"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);
        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!.Id;
    }

    private async Task<int> SembrarClienteAsync(Contexto ctx, string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;
        var idCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync();
        var idListaPrecio = await db.ListasPrecio.Where(l => l.EsDefault).Select(l => l.Id).FirstAsync();

        var cliente = new Cliente
        {
            IdTenant = ctx.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
            IdCondicionFiscal = idCondicionFiscal, IdListaPrecio = idListaPrecio, LimiteCredito = 0,
            Activo = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();
        return cliente.Id;
    }

    /// <summary>Comprobante + pagos sembrados directo (bypass <c>EmitirAsync</c>) — mismo criterio
    /// que <c>VentasTurnoWiringTests.SembrarPagoAsync</c>: el punto de estas pruebas es el listado,
    /// no el checkout.</summary>
    private async Task<int> SembrarComprobanteAsync(
        Contexto ctx, int? idTurno, int idCliente, decimal total, EstadoComprobante estado,
        DateTimeOffset fecha, params int[] idsMedioPago)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var ahora = DateTimeOffset.UtcNow;

        var comprobante = new ComprobanteVenta
        {
            IdTenant = ctx.IdTenant,
            IdTipoComprobante = ctx.IdTipoComprobanteTx,
            Numero = Interlocked.Increment(ref _numeroSecuencial),
            Fecha = fecha,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdTurnoCaja = idTurno,
            IdEmpleado = ctx.IdEmpleadoAdmin,
            IdCliente = idCliente,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = estado,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        foreach (var idMedioPago in idsMedioPago)
        {
            db.PagosComprobante.Add(new PagoComprobante
            {
                IdTenant = ctx.IdTenant, IdComprobanteVenta = comprobante.Id, IdMedioPago = idMedioPago,
                Importe = total / idsMedioPago.Length, Vuelto = 0m, CreatedAt = ahora, UpdatedAt = ahora
            });
        }
        await db.SaveChangesAsync();

        return comprobante.Id;
    }

    private async Task<(int Efectivo, int Tarjeta)> MediosDePagoAsync(Contexto ctx)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        var efectivo = await db.MediosPago.Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
        var tarjeta = await db.MediosPago.Where(m => m.Comportamiento != ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();
        return (efectivo, tarjeta);
    }

    [Fact]
    public async Task ListaLasVentasDelTurnoNewestFirstIncluyendoAnuladasConClienteYMedios()
    {
        var ctx = await PrepararAsync(nameof(ListaLasVentasDelTurnoNewestFirstIncluyendoAnuladasConClienteYMedios));
        var idTurno = await AbrirTurnoAsync(ctx);
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Reconciliación");
        var (efectivo, tarjeta) = await MediosDePagoAsync(ctx);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var idPrimera = await SembrarComprobanteAsync(ctx, idTurno, idCliente, 100m, EstadoComprobante.Emitido, t0, efectivo);
        var idSegunda = await SembrarComprobanteAsync(ctx, idTurno, idCliente, 200m, EstadoComprobante.Anulado, t0.AddMinutes(5), efectivo, tarjeta);

        var respuesta = await ctx.Admin.GetAsync($"/api/ventas/por-turno/{idTurno}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var filas = JsonSerializer.Deserialize<List<VentaDeTurnoListado>>(cuerpo, OpcionesJson)!;

        Assert.Equal(2, filas.Count);
        // newest-first: la anulada (más reciente) primero.
        Assert.Equal(idSegunda, filas[0].Id);
        Assert.Equal(EstadoComprobante.Anulado, filas[0].Estado);
        Assert.Equal("Cliente Reconciliación", filas[0].NombreCliente);
        Assert.Equal(2, filas[0].MediosDePago.Count);

        Assert.Equal(idPrimera, filas[1].Id);
        Assert.Equal(EstadoComprobante.Emitido, filas[1].Estado);
        Assert.Single(filas[1].MediosDePago);
    }

    [Fact]
    public async Task NoIncluyeVentasDeOtroTurnoDelMismoPuntoDeVenta()
    {
        var ctx = await PrepararAsync(nameof(NoIncluyeVentasDeOtroTurnoDelMismoPuntoDeVenta));
        var idCliente = await SembrarClienteAsync(ctx, "Cliente Turno Ajeno");
        var (efectivo, _) = await MediosDePagoAsync(ctx);

        var idTurnoA = await AbrirTurnoAsync(ctx);
        var idVentaTurnoA = await SembrarComprobanteAsync(
            ctx, idTurnoA, idCliente, 50m, EstadoComprobante.Emitido, DateTimeOffset.UtcNow, efectivo);

        // Turno A cerrado vía el endpoint real (no a mano): el punto de esta prueba es un
        // id_turno_caja DISTINTO en el mismo punto de venta, y el cierre de verdad es la única
        // forma de dejar el turno consistente con ck_turnos_caja_cierre_consistente.
        var respuestaCierre = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{idTurnoA}/cierre", new SolicitudDeCierre([new ConteoDeclarado(efectivo, 50m)], null));
        Assert.Equal(HttpStatusCode.OK, respuestaCierre.StatusCode);

        var idTurnoB = await AbrirTurnoAsync(ctx);
        var idVentaTurnoB = await SembrarComprobanteAsync(
            ctx, idTurnoB, idCliente, 75m, EstadoComprobante.Emitido, DateTimeOffset.UtcNow, efectivo);

        var respuesta = await ctx.Admin.GetAsync($"/api/ventas/por-turno/{idTurnoB}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var filas = JsonSerializer.Deserialize<List<VentaDeTurnoListado>>(cuerpo, OpcionesJson)!;

        // mutation-proof-tests: este Assert.Single + el Id exacto son el predicado bajo prueba
        // (c.IdTurnoCaja == idTurno). Evidencia de mutación (registrada en la memoria/reporte de la
        // tarea): comentar el .Where(...) en ServicioDeVentas.ListarPorTurnoAsync hace que este test
        // falle con 2 filas (trae también idVentaTurnoA) — revertido después de confirmar el fallo.
        var fila = Assert.Single(filas);
        Assert.Equal(idVentaTurnoB, fila.Id);
        Assert.NotEqual(idVentaTurnoA, fila.Id);
    }

    [Fact]
    public async Task VentaDeOtroTenantConElMismoIdDeTurnoNumericoNoAparece()
    {
        var ctxUno = await PrepararAsync(nameof(VentaDeOtroTenantConElMismoIdDeTurnoNumericoNoAparece) + "Uno");
        var ctxDos = await PrepararAsync(nameof(VentaDeOtroTenantConElMismoIdDeTurnoNumericoNoAparece) + "Dos");

        var idClienteUno = await SembrarClienteAsync(ctxUno, "Cliente Tenant Uno");
        var (efectivoUno, _) = await MediosDePagoAsync(ctxUno);
        var idTurnoUno = await AbrirTurnoAsync(ctxUno);
        await SembrarComprobanteAsync(ctxUno, idTurnoUno, idClienteUno, 10m, EstadoComprobante.Emitido, DateTimeOffset.UtcNow, efectivoUno);

        // El tenant Dos consulta por-turno con el id numérico del turno del tenant Uno — el filtro
        // de tenant de IWaysDbContext ya deja ese comprobante invisible, así que la respuesta es
        // 200 con lista vacía (no un turno "no encontrado": este endpoint no valida el turno en sí,
        // solo filtra comprobantes), nunca una fuga de datos de otro tenant.
        var respuesta = await ctxDos.Admin.GetAsync($"/api/ventas/por-turno/{idTurnoUno}");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();

        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);
        var filas = JsonSerializer.Deserialize<List<VentaDeTurnoListado>>(cuerpo, OpcionesJson)!;
        Assert.Empty(filas);
    }
}
