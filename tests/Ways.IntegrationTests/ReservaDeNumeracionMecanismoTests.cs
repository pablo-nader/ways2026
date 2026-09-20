using Microsoft.EntityFrameworkCore;
using Ways.Application.Ventas;
using Ways.Domain.Dispositivos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-pos-reserva-de-numeracion (DB CHANGE GATE aprobado): mecanismo puro de
/// <see cref="AsignadorDeNumeroComprobante.ReservarBloqueAsync"/> — sin HTTP, sin
/// <c>ServicioDeReservasDeNumeracion</c> (esa capa de autorización/validación tiene su propia
/// suite, <c>ReservaDeNumeracionEndpointsTests</c>). Mismo patrón de siembra directa por EF que
/// <see cref="AsignadorDeNumeroComprobanteConcurrenciaTests"/>.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ReservaDeNumeracionMecanismoTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string TipoComprobante = "TX";

    /// <summary>Un solo dispositivo por punto de venta a propósito:
    /// <c>ux_dispositivos_punto_venta_activo</c> (invariante "una PC-caja = un punto de venta")
    /// hace estructuralmente imposible que dos dispositivos convivan sobre el mismo punto de
    /// venta — así que nunca hay dos dispositivos compitiendo por la MISMA serie
    /// (<c>numeraciones_comprobante</c> está keyed por punto de venta). La única concurrencia
    /// real de esta tabla es el MISMO dispositivo pidiendo dos veces (ver el test de la carrera,
    /// más abajo).</summary>
    private async Task<(int IdTenant, int IdPuntoVenta, int IdDispositivo)> SembrarEscenarioAsync(string nombre)
    {
        // Fuerza el arranque del host (seed de roles, catálogos globales, etc.) antes de
        // insertar por EF directo — mismo trámite que AsignadorDeNumeroComprobanteConcurrenciaTests.
        using var _ = fixture.CreateClient();

        await using var siembra = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        siembra.Tenants.Add(tenant);
        await siembra.SaveChangesAsync();

        var empresa = new Empresa { IdTenant = tenant.Id, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        siembra.Empresas.Add(empresa);
        await siembra.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id,
            IdEmpresa = empresa.Id,
            Nombre = nombre,
            Modo = ModoPuntoVenta.Escritorio,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        siembra.PuntosVenta.Add(puntoVenta);
        await siembra.SaveChangesAsync();

        var usuario = new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = $"admin-{nombre}",
            Mail = $"{nombre.ToLowerInvariant()}@ways.test",
            RolId = (int)RolConocido.Admin,
            PasswordHash = "x",
            PasswordAlgoritmo = "pbkdf2",
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        siembra.Usuarios.Add(usuario);
        await siembra.SaveChangesAsync();

        var dispositivo = new Dispositivo
        {
            IdTenant = tenant.Id,
            IdPuntoVenta = puntoVenta.Id,
            Nombre = "Caja",
            TokenHash = Guid.NewGuid().ToString("N").PadRight(64, '0'),
            IdUsuarioAlta = usuario.Id,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        siembra.Dispositivos.Add(dispositivo);
        await siembra.SaveChangesAsync();

        return (tenant.Id, puntoVenta.Id, dispositivo.Id);
    }

    private async Task<(long Desde, long Hasta)> ReservarAsync(
        int idTenant, int idPuntoVenta, int idDispositivo, int cantidad, DateTimeOffset momento)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await AsignadorDeNumeroComprobante.ReservarBloqueAsync(
            db, idTenant, idPuntoVenta, TipoComprobante, idDispositivo, cantidad, momento);
    }

    private async Task<List<ReservaNumeracion>> LeerReservasAsync(int idTenant, int idDispositivo)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.ReservasNumeracion
            .Where(r => r.IdTenant == idTenant && r.IdDispositivo == idDispositivo)
            .OrderBy(r => r.Id)
            .ToListAsync();
    }

    /// <summary>Happy path: el bloque devuelto es exactamente <c>[1, cantidad]</c> (contador nuevo)
    /// y avanza <c>numeraciones_comprobante.proximo_numero</c> en <c>cantidad</c> — nunca en 1.</summary>
    [Fact]
    public async Task ReservarUnBloqueAsignaElRangoCorrectoYAvanzaElContadorEnCantidad()
    {
        var (idTenant, idPuntoVenta, idDispositivo) = await SembrarEscenarioAsync(
            nameof(ReservarUnBloqueAsignaElRangoCorrectoYAvanzaElContadorEnCantidad));

        var (desde, hasta) = await ReservarAsync(idTenant, idPuntoVenta, idDispositivo, 10, DateTimeOffset.UtcNow);

        Assert.Equal(1, desde);
        Assert.Equal(10, hasta);

        var siguienteFueraDelBloque = await AsignadorDeNumeroComprobante.AsignarSiguienteAsync(
            fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma), idPuntoVenta, TipoComprobante);
        Assert.Equal(11, siguienteFueraDelBloque);

        var reservas = await LeerReservasAsync(idTenant, idDispositivo);
        var vivas = reservas.Where(r => r.AbandonadaAt is null).ToList();
        Assert.Single(vivas);
        Assert.Equal(1, vivas[0].Desde);
        Assert.Equal(10, vivas[0].Hasta);
    }

    /// <summary>LA CLÁUSULA: pedir un bloque nuevo con uno vigente lo ABANDONA (spec punto 2 —
    /// pérdida de almacenamiento local), nunca lo reactiva ni retoma su cola. Mutación probada:
    /// borrar el paso de abandono en <c>ReservarBloqueAsync</c> deja DOS filas vivas, violando
    /// <c>ux_reservas_numeracion_dispositivo_activo</c> — ver la nota de evidencia en el reporte
    /// final de la tarea.</summary>
    [Fact]
    public async Task PedirUnBloqueNuevoConUnoVigenteLoAbandonaYEntregaUnoFresco()
    {
        var (idTenant, idPuntoVenta, idDispositivo) = await SembrarEscenarioAsync(
            nameof(PedirUnBloqueNuevoConUnoVigenteLoAbandonaYEntregaUnoFresco));

        var primero = await ReservarAsync(idTenant, idPuntoVenta, idDispositivo, 5, DateTimeOffset.UtcNow);
        var segundo = await ReservarAsync(idTenant, idPuntoVenta, idDispositivo, 5, DateTimeOffset.UtcNow);

        Assert.Equal((1L, 5L), primero);
        Assert.Equal((6L, 10L), segundo);

        var reservas = await LeerReservasAsync(idTenant, idDispositivo);
        Assert.Equal(2, reservas.Count);
        Assert.NotNull(reservas[0].AbandonadaAt);
        Assert.Null(reservas[1].AbandonadaAt);
        Assert.Equal(1, reservas[0].Desde);
        Assert.Equal(6, reservas[1].Desde);
    }

    // NOTA HONESTA (claims-match-code): se intentó probar acá tanto la carrera real de
    // ux_reservas_numeracion_dispositivo_activo (dos ReservarBloqueAsync concurrentes del mismo
    // dispositivo, sobre dos DbContext/conexión separados) como el commit ambiguo del INSERT de
    // reservas_numeracion vía InterceptorQueRompeElCommitAmbiguo. Ninguna de las dos se pudo forzar
    // de forma confiable: (a) el row lock del UPDATE...RETURNING sobre numeraciones_comprobante
    // (AsignarBloqueAsync) alcanza a serializar las dos transacciones con el margen suficiente para
    // que el abandono de la segunda SIEMPRE encuentre ya comiteada la fila de la primera y la
    // abandone en limpio, sin tocar nunca el índice único bajo Task.WhenAll — el mismo patrón que
    // ArticulosEndpointsTests SÍ reproduce de forma confiable no aplica acá porque ahí no hay un
    // punto de serialización intermedio; (b) DbCommandInterceptor solo ve comandos que pasan por el
    // pipeline relacional de EF (LINQ/SaveChangesAsync) — AsignadorDeNumeroComprobante es ADO crudo
    // de punta a punta (ver el doc-comment de la clase) y sus statements nunca llegan a ese
    // interceptor, así que el 40001 inyectado no se dispara nunca. La correctitud del backstop
    // (ux_reservas_numeracion_dispositivo_activo traduce a 409, ver ManejadorDeErrores) queda
    // probada por bypass crudo en ReservaDeNumeracionBackstopTests, mismo criterio documentado que
    // pk_numeraciones_comprobante/pk_stock ("exención de prueba de carrera" — la razón acá es
    // distinta: no es una imposibilidad estructural sino una ventana de carrera demasiado angosta
    // para forzar sin un rendezvous dedicado, que no existe hoy en el repo).
}
