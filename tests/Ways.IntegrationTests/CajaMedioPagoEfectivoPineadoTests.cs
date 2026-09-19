using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.Catalogos;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// judgment-day JD-E5a-2 (DB CHANGE GATE aprobado): <c>turnos_caja.id_medio_pago_efectivo</c> —
/// el ancla PINEADA al cierre, para los dos modos, y por qué el resumen de cierre deja de
/// re-resolverla contra el catálogo actual de medios de pago (spec arqueo-de-cierre; ver el
/// doc-comment de <c>LectorDeResumenDeCierrePorRetiro.LeerAsync</c>).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CajaMedioPagoEfectivoPineadoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    private static readonly JsonSerializerOptions OpcionesJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private sealed record Contexto(
        int IdTenant, int IdPuntoVenta, int IdEmpleadoAdmin, int IdCliente, int IdTipoComprobanteTx,
        int IdMedioEfectivo, int IdMedioTarjeta, HttpClient Admin);

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

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant));

        var idMedioEfectivo = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo)
            .Select(m => m.Id).FirstAsync();
        var idMedioTarjeta = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Electronico)
            .Select(m => m.Id).FirstAsync();
        var idCliente = await db.Clientes.Select(c => c.Id).FirstAsync();
        var idTipoComprobanteTx = await db.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).FirstAsync();

        return new Contexto(
            resultado.IdTenant, resultado.IdPuntoVenta, resultado.IdUsuarioAdmin, idCliente, idTipoComprobanteTx,
            idMedioEfectivo, idMedioTarjeta, admin);
    }

    private static async Task<TurnoResumen> AbrirTurnoAsync(Contexto ctx, decimal fondoInicial = 0m)
    {
        var respuesta = await ctx.Admin.PostAsJsonAsync(
            "/api/caja/turnos", new SolicitudDeApertura(ctx.IdPuntoVenta, fondoInicial, "Apertura de prueba"));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.Created, cuerpo);

        return JsonSerializer.Deserialize<TurnoResumen>(cuerpo, OpcionesJson)!;
    }

    private long _numeroSecuencial = 1;

    private async Task SembrarPagoAsync(Contexto ctx, int idTurno, int idMedioPago, decimal importe)
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
            IdMedioPago = idMedioPago,
            Importe = importe,
            Vuelto = 0m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        });
        await db.SaveChangesAsync();
    }

    private async Task<int?> LeerAnclaPersistidaAsync(Contexto ctx, int idTurno)
    {
        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant));
        return await db.TurnosCaja.Where(t => t.Id == idTurno).Select(t => t.IdMedioPagoEfectivo).SingleAsync();
    }

    // ---- el cierre pinea el ancla, en los dos modos --------------------------------------------

    [Fact]
    public async Task ElCierreClasicoPineaElAnclaAlCerrar()
    {
        var ctx = await PrepararAsync(nameof(ElCierreClasicoPineaElAnclaAlCerrar));
        var turno = await AbrirTurnoAsync(ctx);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, 100m);

        var solicitud = new SolicitudDeCierre([new ConteoDeclarado(ctx.IdMedioEfectivo, 100m)], null);
        var respuesta = await ctx.Admin.PostAsJsonAsync($"/api/caja/turnos/{turno.Id}/cierre", solicitud);
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        Assert.Equal(ctx.IdMedioEfectivo, await LeerAnclaPersistidaAsync(ctx, turno.Id));
    }

    [Fact]
    public async Task ElCierrePorRetiroPineaElAnclaAlCerrar()
    {
        var ctx = await PrepararAsync(nameof(ElCierrePorRetiroPineaElAnclaAlCerrar));
        var turno = await AbrirTurnoAsync(ctx, fondoInicial: 100m);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, 200m);

        var respuesta = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(50m, null));
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);

        Assert.Equal(ctx.IdMedioEfectivo, await LeerAnclaPersistidaAsync(ctx, turno.Id));
    }

    // ---- LA CLÁUSULA: un cambio de catálogo DESPUÉS del cierre no mueve el resumen -------------

    /// <summary>LA CLÁUSULA del fix (judgment-day JD-E5a-2): se cierra un turno, se lee su
    /// resumen, y RECIÉN DESPUÉS se edita el catálogo — el medio que era efectivo pasa a
    /// electrónico y otro medio (antes electrónico) pasa a ser el nuevo efectivo. Sin el ancla
    /// pineada, <c>ResolvedorDeMedioDeCajaFisica.Resolver</c> re-resolvería contra ese catálogo
    /// nuevo y el resumen leería (o fallaría en encontrar) la fila de <c>arqueos_turno</c>
    /// equivocada. Con el ancla pineada, el resumen after-edit tiene que ser IDÉNTICO al
    /// before-edit.
    ///
    /// Mutation-proof-tests: se corrió la mutación de verdad — volver
    /// <c>LectorDeResumenDeCierrePorRetiro.LeerAsync</c> a re-resolver siempre contra el catálogo
    /// actual (<c>ResolvedorDeMedioDeCajaFisica.Resolver(insumos.Actividad)</c>, ignorando <see
    /// cref="TurnoCaja.IdMedioPagoEfectivo"/>) hizo fallar esta prueba (esperado <c>-850</c>,
    /// obtenido <c>0</c>): tras el swap el ancla re-resuelta es el medio que ERA "Transferencia"
    /// (ahora "efectivo" en el catálogo), cuyo arqueo persistido no-ancla tiene
    /// <c>diferencia = 0</c> (declarado = esperado, 400 = 400) — un número que no tiene ninguna
    /// relación con el ancla real del cierre; revertido después de confirmar el fallo.</summary>
    [Fact]
    public async Task ElResumenDeCierreIgnoraUnCambioDeCatalogoPosteriorAlCierre()
    {
        var ctx = await PrepararAsync(nameof(ElResumenDeCierreIgnoraUnCambioDeCatalogoPosteriorAlCierre));
        var turno = await AbrirTurnoAsync(ctx, fondoInicial: 300m);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, 1000m);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioTarjeta, 400m);

        var cierre = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(150m, null));
        Assert.Equal(HttpStatusCode.OK, cierre.StatusCode);

        var antesDelCambio = await ctx.Admin.GetFromJsonAsync<ResumenDeCierrePorRetiro>(
            $"/api/caja/turnos/{turno.Id}/resumen-de-cierre", OpcionesJson);
        Assert.NotNull(antesDelCambio);

        // El cajero/dueño edita el catálogo DESPUÉS de cerrar: el medio ancla ("Efectivo") pasa a
        // electrónico, y el que antes era "Transferencia" pasa a ser el nuevo efectivo — swap
        // completo, el peor caso. Mismos nombres que la plantilla de aprovisionamiento
        // (PlantillaDeAprovisionamiento.V1) — solo cambia Comportamiento, sin riesgo de colisión
        // de unicidad de nombre.
        var putEfectivo = await ctx.Admin.PutAsJsonAsync(
            $"/api/catalogos/medios-pago/{ctx.IdMedioEfectivo}",
            new MedioPagoAlta("Efectivo", null, 1, ComportamientoMedioPago.Electronico, false, false, null));
        Assert.Equal(HttpStatusCode.OK, putEfectivo.StatusCode);

        var putTarjeta = await ctx.Admin.PutAsJsonAsync(
            $"/api/catalogos/medios-pago/{ctx.IdMedioTarjeta}",
            new MedioPagoAlta("Transferencia", null, 2, ComportamientoMedioPago.Efectivo, true, false, null));
        Assert.Equal(HttpStatusCode.OK, putTarjeta.StatusCode);

        var despuesDelCambio = await ctx.Admin.GetFromJsonAsync<ResumenDeCierrePorRetiro>(
            $"/api/caja/turnos/{turno.Id}/resumen-de-cierre", OpcionesJson);
        Assert.NotNull(despuesDelCambio);

        Assert.Equal(antesDelCambio!.Diferencia, despuesDelCambio!.Diferencia);
        Assert.Equal(antesDelCambio.VentasEnEfectivoNetas, despuesDelCambio.VentasEnEfectivoNetas);
        Assert.Equal(antesDelCambio.GastosEnEfectivo, despuesDelCambio.GastosEnEfectivo);
        Assert.Equal(antesDelCambio.TotalVentas, despuesDelCambio.TotalVentas);
        Assert.Equal(antesDelCambio.VentasPorMedio.Count, despuesDelCambio.VentasPorMedio.Count);
        foreach (var (esperado, obtenido) in antesDelCambio.VentasPorMedio.Zip(despuesDelCambio.VentasPorMedio))
        {
            Assert.Equal(esperado, obtenido);
        }

        // Y las cifras concretas siguen siendo las de EFECTIVO (950 = 1000 pagos - 0 vuelto,
        // sobre el fondo 300 + 0 refuerzos - 150 retiro), nunca las de tarjeta (400).
        Assert.Equal(1000m, despuesDelCambio.VentasEnEfectivoNetas);
    }

    // ---- legado: NULL cae al catálogo actual ---------------------------------------------------

    /// <summary>Simula un turno cerrado ANTES de esta migración cuyo backfill no pudo resolverlo
    /// (o, más simple para esta prueba, cualquier turno legado con la columna en NULL): el lector
    /// tiene que caer a <c>ResolvedorDeMedioDeCajaFisica.Resolver</c> contra el catálogo actual, y
    /// seguir devolviendo un resumen coherente (nunca una excepción ni una diferencia inventada).</summary>
    [Fact]
    public async Task ElAnclaLegadaEnNuloCaeAlCatalogoActual()
    {
        var ctx = await PrepararAsync(nameof(ElAnclaLegadaEnNuloCaeAlCatalogoActual));
        var turno = await AbrirTurnoAsync(ctx, fondoInicial: 500m);
        await SembrarPagoAsync(ctx, turno.Id, ctx.IdMedioEfectivo, 1000m);

        var cierre = await ctx.Admin.PostAsJsonAsync(
            $"/api/caja/turnos/{turno.Id}/cierre-por-retiro", new SolicitudDeCierrePorRetiro(0m, null));
        Assert.Equal(HttpStatusCode.OK, cierre.StatusCode);

        // Simula el legado: vuelve la columna a NULL a mano (el UPDATE de cierre ya comiteó).
        // OpenConnectionAsync (no conexion.OpenAsync() directo) — es lo que dispara
        // InterceptorDeContextoDeTenant y setea el GUC de RLS; sin él, el UPDATE crudo corre sin
        // contexto de tenant y RLS lo deja en 0 filas afectadas, en silencio.
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, ctx.IdTenant)))
        {
            await db.Database.OpenConnectionAsync();
            var conexion = db.Database.GetDbConnection();
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "UPDATE turnos_caja SET id_medio_pago_efectivo = NULL WHERE id_turno_caja = $1";
            var parametro = comando.CreateParameter();
            parametro.Value = turno.Id;
            comando.Parameters.Add(parametro);
            var filasAfectadas = await comando.ExecuteNonQueryAsync();
            Assert.Equal(1, filasAfectadas);
        }

        Assert.Null(await LeerAnclaPersistidaAsync(ctx, turno.Id));

        var respuesta = await ctx.Admin.GetAsync($"/api/caja/turnos/{turno.Id}/resumen-de-cierre");
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, cuerpo);

        var resumen = JsonSerializer.Deserialize<ResumenDeCierrePorRetiro>(cuerpo, OpcionesJson)!;
        // Catálogo sin editar en esta prueba: la resolución de fallback contra el catálogo actual
        // coincide con la que hubiera pineado el cierre — mismas cifras que el caso feliz.
        Assert.Equal(1000m, resumen.VentasEnEfectivoNetas);
        // diferencia = 0 (retiros) - (1000 - 0 + 0) = -1000.
        Assert.Equal(-1000m, resumen.Diferencia);
    }
}
