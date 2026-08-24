using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// <see cref="AsignadorDeNumeroFiscal"/> — tasks.md Slice 4, targets 52-56 (U1/U3 conjuncts, I1,
/// D1's lock proof) más los dos escenarios propios de <c>specs/numeracion-fiscal/spec.md</c>
/// (rollback no consume, concurrencia N/N+1 — el mismo patrón exacto de
/// <c>AsignadorDeNumeroComprobanteConcurrenciaTests</c>, con la disciplina invertida: ACÁ no hay
/// <c>AsignarComprometidoAsync</c> que abra su propia transacción — el llamador es siempre quien la
/// abre y la comitea, design D1).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AsignadorDeNumeroFiscalTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const short CodigoAfipFa = 1;
    private const short CodigoAfipFb = 6;

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private async Task<(int IdTenant, int IdPuntoVenta)> SembrarPuntoVentaAsync(string nombre)
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

        return (tenant.Id, puntoVenta.Id);
    }

    /// <summary>Dos puntos de venta bajo el MISMO tenant — a diferencia de
    /// <see cref="SembrarPuntoVentaAsync"/> (que arma un tenant nuevo por llamada), los tests de
    /// U1/U3 conjunct (a) necesitan que ambos PV compartan tenant, así la FK compuesta
    /// <c>fk_numeraciones_fiscales_punto_venta (id_punto_venta, id_tenant)</c> los acepta a los
    /// dos.</summary>
    private async Task<(int IdTenant, int IdPuntoVentaA, int IdPuntoVentaB)> SembrarDosPuntosDeVentaAsync(string nombre)
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

        var puntoVentaA = new PuntoVenta
        {
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = $"{nombre}-A", CreatedAt = ahora, UpdatedAt = ahora
        };
        var puntoVentaB = new PuntoVenta
        {
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = $"{nombre}-B", CreatedAt = ahora, UpdatedAt = ahora
        };
        siembra.PuntosVenta.AddRange(puntoVentaA, puntoVentaB);
        await siembra.SaveChangesAsync();

        return (tenant.Id, puntoVentaA.Id, puntoVentaB.Id);
    }

    private async Task AsegurarAsync(int idTenant, int idPuntoVenta, short codigoAfip)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        await AsignadorDeNumeroFiscal.AsegurarContadorAsync(db, idTenant, idPuntoVenta, codigoAfip);
    }

    private async Task<long> AsignarComiteadoAsync(int idPuntoVenta, short codigoAfip)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        await using var transaccion = await db.Database.BeginTransactionAsync();

        var numero = await AsignadorDeNumeroFiscal.AsignarSiguienteAsync(db, idPuntoVenta, codigoAfip);

        await transaccion.CommitAsync();
        return numero;
    }

    /// <summary>Conexión cruda en modo <c>plataforma</c> (bypassea RLS, misma convención que
    /// <c>SembrarPuntoVentaAsync</c>) — SIN esto, una lectura raw contra una tabla <c>FORCE</c>
    /// RLS con <c>app.tenant_id</c> sin setear ve CERO filas y <c>ExecuteScalarAsync</c> devuelve
    /// <c>null</c> en silencio, no una excepción (el bug real detrás del primer intento de este
    /// archivo: "Expected 5, Actual null" no era la producción fallando, era el lector de la
    /// prueba leyendo sin contexto de tenant).</summary>
    private async Task<NpgsqlConnection> AbrirConexionPlataformaAsync()
    {
        var conexion = new NpgsqlConnection(fixture.AppConnectionString);
        await conexion.OpenAsync();
        await using var guc = new NpgsqlCommand("SELECT set_config('app.acceso', 'plataforma', false)", conexion);
        await guc.ExecuteNonQueryAsync();
        return conexion;
    }

    private async Task<long?> LeerProximoNumeroAsync(int idPuntoVenta, short codigoAfip)
    {
        await using var conexion = await AbrirConexionPlataformaAsync();
        await using var comando = new NpgsqlCommand(
            "SELECT proximo_numero FROM numeraciones_fiscales WHERE id_punto_venta = $1 AND codigo_afip = $2",
            conexion);
        comando.Parameters.AddWithValue(idPuntoVenta);
        comando.Parameters.AddWithValue(codigoAfip);

        var resultado = await comando.ExecuteScalarAsync();
        return resultado is null ? null : Convert.ToInt64(resultado);
    }

    private async Task<long?> LeerUltimoAutorizadoAsync(int idPuntoVenta, short codigoAfip)
    {
        await using var conexion = await AbrirConexionPlataformaAsync();
        await using var comando = new NpgsqlCommand(
            "SELECT ultimo_autorizado_arca FROM numeraciones_fiscales WHERE id_punto_venta = $1 AND codigo_afip = $2",
            conexion);
        comando.Parameters.AddWithValue(idPuntoVenta);
        comando.Parameters.AddWithValue(codigoAfip);

        var resultado = await comando.ExecuteScalarAsync();
        return resultado is null or DBNull ? null : Convert.ToInt64(resultado);
    }

    // --- Target 52: U1 conjunct (a) id_punto_venta ---

    [Fact]
    public async Task AsignarSobreUnPuntoDeVentaNoTocaElProximoNumeroDeUnPuntoDeVentaHermano()
    {
        var (idTenant, idPuntoVentaA, idPuntoVentaB) = await SembrarDosPuntosDeVentaAsync(
            nameof(AsignarSobreUnPuntoDeVentaNoTocaElProximoNumeroDeUnPuntoDeVentaHermano));

        await AsegurarAsync(idTenant, idPuntoVentaA, CodigoAfipFa);
        await AsegurarAsync(idTenant, idPuntoVentaB, CodigoAfipFa);

        await AsignarComiteadoAsync(idPuntoVentaA, CodigoAfipFa);

        Assert.Equal(2L, await LeerProximoNumeroAsync(idPuntoVentaA, CodigoAfipFa));
        Assert.Equal(1L, await LeerProximoNumeroAsync(idPuntoVentaB, CodigoAfipFa));
    }

    // --- Target 53: U1 conjunct (b) codigo_afip ---

    [Fact]
    public async Task AsignarSobreUnCodigoAfipNoTocaElProximoNumeroDeOtroCodigoAfipDelMismoPuntoDeVenta()
    {
        var (idTenant, idPuntoVenta) = await SembrarPuntoVentaAsync(
            nameof(AsignarSobreUnCodigoAfipNoTocaElProximoNumeroDeOtroCodigoAfipDelMismoPuntoDeVenta));

        await AsegurarAsync(idTenant, idPuntoVenta, CodigoAfipFa);
        await AsegurarAsync(idTenant, idPuntoVenta, CodigoAfipFb);

        await AsignarComiteadoAsync(idPuntoVenta, CodigoAfipFa);

        Assert.Equal(2L, await LeerProximoNumeroAsync(idPuntoVenta, CodigoAfipFa));
        Assert.Equal(1L, await LeerProximoNumeroAsync(idPuntoVenta, CodigoAfipFb));
    }

    // --- Target 54: I1 — un rechazo no libera el número ---

    /// <summary>I1: un número asignado y COMITEADO (aunque el comprobante que lo usó termine
    /// <c>rechazado</c>) no se libera — el release explícito del operador es 19c (T2). El único
    /// caso en el que un número no se consume es un ROLLBACK antes de comitear (spec: "A Rolled-Back
    /// Emission Does Not Consume The Fiscal Number", cubierto aparte, más abajo).</summary>
    [Fact]
    public async Task UnNumeroComiteadoPorUnaEmisionQueTerminaRechazadaNoSeLiberaYElSiguienteEsConsecutivo()
    {
        var (idTenant, idPuntoVenta) = await SembrarPuntoVentaAsync(
            nameof(UnNumeroComiteadoPorUnaEmisionQueTerminaRechazadaNoSeLiberaYElSiguienteEsConsecutivo));
        await AsegurarAsync(idTenant, idPuntoVenta, CodigoAfipFa);

        // Simula el número que quedó atado a un comprobante 'rechazado' — la transacción COMITEA
        // igual (el número se consume aunque el resultado de ARCA termine siendo un rechazo; solo
        // un rollback ANTES de comitear reusa el número, ver el test de más abajo).
        var numeroDelRechazo = await AsignarComiteadoAsync(idPuntoVenta, CodigoAfipFa);
        var numeroSiguiente = await AsignarComiteadoAsync(idPuntoVenta, CodigoAfipFa);

        Assert.Equal(numeroDelRechazo + 1, numeroSiguiente);
    }

    /// <summary>spec numeracion-fiscal: "A Rolled-Back Emission Does Not Consume The Fiscal
    /// Number" — un rollback ANTES de comitear (p. ej. la transacción entera de la emisión falla
    /// antes del `COMMIT`) no deja un hueco: el número queda disponible para el siguiente intento.</summary>
    [Fact]
    public async Task UnaAsignacionConRollbackAntesDeComitearReusaElNumeroEnVezDeDejarUnHueco()
    {
        var (idTenant, idPuntoVenta) = await SembrarPuntoVentaAsync(
            nameof(UnaAsignacionConRollbackAntesDeComitearReusaElNumeroEnVezDeDejarUnHueco));
        await AsegurarAsync(idTenant, idPuntoVenta, CodigoAfipFa);

        await using var dbRollback = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        await using var transaccionRollback = await dbRollback.Database.BeginTransactionAsync();
        var numeroDescartado = await AsignadorDeNumeroFiscal.AsignarSiguienteAsync(dbRollback, idPuntoVenta, CodigoAfipFa);
        await transaccionRollback.RollbackAsync();

        var numeroSiguiente = await AsignarComiteadoAsync(idPuntoVenta, CodigoAfipFa);

        Assert.Equal(numeroDescartado, numeroSiguiente);
    }

    // --- Target 55 [S]: D1's lock proof, ambos lados ---

    /// <summary>D1 (design.md, Lock order — arbitrated): mientras la transacción que asignó el
    /// número sigue abierta (sin comitear), <c>numeraciones_fiscales</c> tiene que aparecer entre
    /// los locks de relación de esa conexión (posición 0, prefijo singleton) y NINGUNA de las
    /// tablas de la serie contendida (<c>turnos_caja</c>, <c>stock</c>, <c>stock_lotes</c>,
    /// <c>clientes</c>) puede aparecer — la prueba obligatoria es de DOS lados (rule 13), no basta
    /// con confirmar que el lock esperado está.</summary>
    [Fact]
    public async Task LaTransaccionDeAsignacionSostieneSoloElLockDeNumeracionesFiscales()
    {
        var (idTenant, idPuntoVenta) = await SembrarPuntoVentaAsync(
            nameof(LaTransaccionDeAsignacionSostieneSoloElLockDeNumeracionesFiscales));
        await AsegurarAsync(idTenant, idPuntoVenta, CodigoAfipFa);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        await using var transaccion = await db.Database.BeginTransactionAsync();

        await AsignadorDeNumeroFiscal.AsignarSiguienteAsync(db, idPuntoVenta, CodigoAfipFa);

        var conexionBackend = db.Database.GetDbConnection();
        int pid;
        await using (var comandoPid = conexionBackend.CreateCommand())
        {
            comandoPid.CommandText = "SELECT pg_backend_pid()";
            comandoPid.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            pid = (int)(await comandoPid.ExecuteScalarAsync())!;
        }

        await using var conexionPoll = new NpgsqlConnection(fixture.AppConnectionString);
        await conexionPoll.OpenAsync();
        await using var comandoPoll = new NpgsqlCommand(
            "SELECT c.relname FROM pg_locks l JOIN pg_class c ON c.oid = l.relation " +
            "WHERE l.pid = $1 AND l.locktype = 'relation' AND c.relkind = 'r'",
            conexionPoll);
        comandoPoll.Parameters.AddWithValue(pid);

        var relacionesBloqueadas = new List<string>();
        await using (var lector = await comandoPoll.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
            {
                relacionesBloqueadas.Add(lector.GetString(0));
            }
        }

        await transaccion.RollbackAsync();

        Assert.Contains("numeraciones_fiscales", relacionesBloqueadas);
        Assert.DoesNotContain("turnos_caja", relacionesBloqueadas);
        Assert.DoesNotContain("stock", relacionesBloqueadas);
        Assert.DoesNotContain("stock_lotes", relacionesBloqueadas);
        Assert.DoesNotContain("clientes", relacionesBloqueadas);
    }

    // --- Target 56: U3 conjuncts (a)(b) ---

    [Fact]
    public async Task ReconciliarUnaSerieNoTocaElUltimoAutorizadoDeUnPuntoDeVentaHermano()
    {
        var (idTenant, idPuntoVentaA, idPuntoVentaB) = await SembrarDosPuntosDeVentaAsync(
            nameof(ReconciliarUnaSerieNoTocaElUltimoAutorizadoDeUnPuntoDeVentaHermano));

        await AsegurarAsync(idTenant, idPuntoVentaA, CodigoAfipFa);
        await AsegurarAsync(idTenant, idPuntoVentaB, CodigoAfipFa);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        await AsignadorDeNumeroFiscal.ReconciliarAsync(
            db, idPuntoVentaA, CodigoAfipFa, 5, new RelojFijo(DateTimeOffset.UtcNow));

        Assert.Equal(5L, await LeerUltimoAutorizadoAsync(idPuntoVentaA, CodigoAfipFa));
        Assert.Null(await LeerUltimoAutorizadoAsync(idPuntoVentaB, CodigoAfipFa));
    }

    [Fact]
    public async Task ReconciliarUnaSerieNoTocaElUltimoAutorizadoDeUnCodigoAfipHermano()
    {
        var (idTenant, idPuntoVenta) = await SembrarPuntoVentaAsync(
            nameof(ReconciliarUnaSerieNoTocaElUltimoAutorizadoDeUnCodigoAfipHermano));

        await AsegurarAsync(idTenant, idPuntoVenta, CodigoAfipFa);
        await AsegurarAsync(idTenant, idPuntoVenta, CodigoAfipFb);

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        await AsignadorDeNumeroFiscal.ReconciliarAsync(
            db, idPuntoVenta, CodigoAfipFa, 5, new RelojFijo(DateTimeOffset.UtcNow));

        Assert.Equal(5L, await LeerUltimoAutorizadoAsync(idPuntoVenta, CodigoAfipFa));
        Assert.Null(await LeerUltimoAutorizadoAsync(idPuntoVenta, CodigoAfipFb));
    }

    /// <summary>spec numeracion-fiscal: "Reconciling An Empty Series Records 0, Not NULL" — un
    /// <c>FECompUltimoAutorizado</c> de serie nunca usada responde <c>CbteNro = 0</c>, y eso es un
    /// valor LEGÍTIMO (CHECK 7 lo permite explícitamente para esta columna) — no hay que
    /// confundirlo con "todavía no reconciliada" (<c>NULL</c>).</summary>
    [Fact]
    public async Task ReconciliarUnaSerieNuncaUsadaRegistraCeroNoNull()
    {
        var (idTenant, idPuntoVenta) = await SembrarPuntoVentaAsync(
            nameof(ReconciliarUnaSerieNuncaUsadaRegistraCeroNoNull));
        await AsegurarAsync(idTenant, idPuntoVenta, CodigoAfipFa);

        var ahora = new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero);
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        await AsignadorDeNumeroFiscal.ReconciliarAsync(db, idPuntoVenta, CodigoAfipFa, 0, new RelojFijo(ahora));

        Assert.Equal(0L, await LeerUltimoAutorizadoAsync(idPuntoVenta, CodigoAfipFa));
    }

    // --- spec: concurrencia serializada (Requirement: Concurrent Emissions Are Serialized) ---

    [Fact]
    public async Task DosAsignacionesConcurrentesDeLaMismaSerieDanNumerosDistintosYConsecutivos()
    {
        var (idTenant, idPuntoVenta) = await SembrarPuntoVentaAsync(
            nameof(DosAsignacionesConcurrentesDeLaMismaSerieDanNumerosDistintosYConsecutivos));
        await AsegurarAsync(idTenant, idPuntoVenta, CodigoAfipFa);

        var tareaA = AsignarComiteadoAsync(idPuntoVenta, CodigoAfipFa);
        var tareaB = AsignarComiteadoAsync(idPuntoVenta, CodigoAfipFa);

        var numeros = await Task.WhenAll(tareaA, tareaB);

        Assert.NotEqual(numeros[0], numeros[1]);
        Assert.Equal([1L, 2L], numeros.OrderBy(n => n));
    }
}
