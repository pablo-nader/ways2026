using Ways.Application.Ventas;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-5-pos-ventas, Slice 2 (task 2.13, design decisiones 1/2/8/9, spec: comprobantes-venta /
/// Numeración Allocation Is Atomic, ambos escenarios): mismo patrón que
/// <c>AsignadorDeNumeroClienteConcurrenciaTests</c> — el <c>UPDATE ... RETURNING</c>
/// incondicional sobre la fila del contador serializa las dos transacciones sin ayuda externa
/// (el propio row lock de Postgres alcanza, sin <c>InterceptorDeRendezVous</c>).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AsignadorDeNumeroComprobanteConcurrenciaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const int CantidadDeRondas = 3;
    private const string TipoComprobante = "TX";

    private async Task<(int IdTenant, int IdPuntoVenta)> SembrarTenantConPuntoVentaAsync(string nombre)
    {
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
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora
        };
        siembra.PuntosVenta.Add(puntoVenta);
        await siembra.SaveChangesAsync();

        await AsignadorDeNumeroComprobante.AsegurarContadorAsync(siembra, tenant.Id, puntoVenta.Id, TipoComprobante);

        return (tenant.Id, puntoVenta.Id);
    }

    /// <summary>Spec: "Concurrent sales at the same punto de venta get consecutive numbers" —
    /// dos asignaciones simultáneas sobre el MISMO contador (mismo punto de venta y tipo) no
    /// pueden repetir ni saltear un número.</summary>
    [Fact]
    public async Task DosAsignacionesConcurrentesDelMismoPuntoDeVentaYTipoDanNumerosDistintosYConsecutivos()
    {
        var (_, idPuntoVenta) = await SembrarTenantConPuntoVentaAsync(
            nameof(DosAsignacionesConcurrentesDelMismoPuntoDeVentaYTipoDanNumerosDistintosYConsecutivos));

        var todosLosNumeros = new List<long>();

        for (var ronda = 0; ronda < CantidadDeRondas; ronda++)
        {
            var tareaA = AsignarUnoAsync(idPuntoVenta);
            var tareaB = AsignarUnoAsync(idPuntoVenta);

            var numeros = await Task.WhenAll(tareaA, tareaB);

            Assert.NotEqual(numeros[0], numeros[1]);
            todosLosNumeros.AddRange(numeros);
        }

        var minimo = todosLosNumeros.Min();
        var esperados = Enumerable.Range(0, todosLosNumeros.Count).Select(i => minimo + i);

        Assert.Equal(esperados, todosLosNumeros.OrderBy(n => n));
        Assert.Equal(todosLosNumeros.Count, todosLosNumeros.Distinct().Count());
    }

    /// <summary>Hallazgo honesto sobre el mecanismo (contraparte del "a rolled-back sale
    /// leaves an accepted gap" de la spec, que describe la SALE completa de la Slice 4, no el
    /// asignador aislado): acá el UPDATE es transaccional de verdad — <c>ROLLBACK</c> deshace
    /// el incremento como cualquier otra fila, así que un rollback ANTES de comitear NO deja
    /// un hueco, REUSA el número. El "hueco aceptado" de la spec nace en la capa de la Sale
    /// (Slice 4): un retry del <c>CreateExecutionStrategy</c> ante una falla transitoria con
    /// estado de commit ambiguo puede reconsumir otro número sin que este asignador, por sí
    /// solo, pueda saltear ninguno — la garantía que SÍ da este nivel es la que importa acá:
    /// el contador nunca retrocede ante una operación EXITOSA (monótono no decreciente), que es
    /// la base sobre la que la Slice 4 construye el resto.</summary>
    [Fact]
    public async Task UnaAsignacionConRollbackAntesDeComitearReusaElNumeroEnVezDeDejarUnHueco()
    {
        var (_, idPuntoVenta) = await SembrarTenantConPuntoVentaAsync(
            nameof(UnaAsignacionConRollbackAntesDeComitearReusaElNumeroEnVezDeDejarUnHueco));

        await using var dbRollback = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        await using var transaccionRollback = await dbRollback.Database.BeginTransactionAsync();
        var numeroDescartado = await AsignadorDeNumeroComprobante.AsignarSiguienteAsync(
            dbRollback, idPuntoVenta, TipoComprobante);
        await transaccionRollback.RollbackAsync();

        var numeroSiguiente = await AsignarUnoAsync(idPuntoVenta);

        Assert.Equal(numeroDescartado, numeroSiguiente);
    }

    /// <summary>La otra cara del mismo hallazgo: una asignación que SÍ comitea nunca se
    /// "devuelve" — el contador es monótono no decreciente, sin importar qué le pase después al
    /// llamador con ese número (spec: "no gap, no duplicate" en su mitad "no duplicate").</summary>
    [Fact]
    public async Task UnaAsignacionYaComiteadaNuncaSeReusaEnUnaAsignacionPosterior()
    {
        var (_, idPuntoVenta) = await SembrarTenantConPuntoVentaAsync(
            nameof(UnaAsignacionYaComiteadaNuncaSeReusaEnUnaAsignacionPosterior));

        var primero = await AsignarUnoAsync(idPuntoVenta);
        var segundo = await AsignarUnoAsync(idPuntoVenta);

        Assert.Equal(primero + 1, segundo);
    }

    private async Task<long> AsignarUnoAsync(int idPuntoVenta)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        await using var transaccion = await db.Database.BeginTransactionAsync();

        var numero = await AsignadorDeNumeroComprobante.AsignarSiguienteAsync(db, idPuntoVenta, TipoComprobante);

        await transaccion.CommitAsync();
        return numero;
    }
}
