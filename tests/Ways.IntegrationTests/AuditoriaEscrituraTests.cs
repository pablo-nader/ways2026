using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Domain.Auditoria;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-14-auditoria-trazabilidad, Slice 1: la tabla + el writer contra Postgres real, sin
/// call sites todavía (nadie más los usa en esta slice) — RLS (tasks 1.22-1.24,
/// <c>mutation-proof-tests</c> regla 5, sobre <c>ways_app</c> NOSUPERUSER NOBYPASSRLS), reloj fijo
/// (tasks 1.25-1.26) y el backstop SQLSTATE 23503 de <c>db-error-backstops</c> (task 1.28, gate
/// §B). El guard de transacción null (tasks 1.20-1.21) vive en
/// <c>Ways.Application.Tests.Auditoria.ServicioDeAuditoriaGuardTests</c> — no toca Postgres, así
/// que no necesita este fixture.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AuditoriaEscrituraTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MailRoot = "test@test.com";

    private sealed class RelojFijo(DateTimeOffset ahora) : IRelojDelSistema
    {
        public DateTimeOffset Ahora { get; } = ahora;
    }

    private sealed class ContextoFijo(int? idTenant, int usuarioId) : IContextoDeUsuario
    {
        public bool EstaAutenticado => true;
        public int UsuarioId => usuarioId;
        public string NombreUsuario => "contexto-fijo";
        public Domain.Usuarios.RolConocido Rol => Domain.Usuarios.RolConocido.Admin;
        public int? IdTenant => idTenant;
    }

    private async Task<Tenant> SembrarTenantAsync(string nombre)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        return tenant;
    }

    /// <summary>El id de un usuario existente, válido como <c>id_actor</c> — root, sembrado por
    /// <c>InicializadorDeBaseDeDatos</c> al primer arranque del host.</summary>
    private async Task<int> ObtenerIdDeUsuarioRootAsync()
    {
        using var _ = fixture.CreateClient(); // arranca el host (siembra root)

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        return await db.Usuarios.Where(u => u.Mail == MailRoot).Select(u => u.Id).FirstAsync();
    }

    private async Task<long> SembrarFilaAsync(Tenant tenant, int idActor)
    {
        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var fila = new Ways.Domain.Auditoria.Auditoria
        {
            IdTenant = tenant.Id,
            IdPuntoVenta = null,
            IdActor = idActor,
            Accion = "precio.cambio",
            Entidad = "articulo",
            IdEntidad = 41,
            ValorAnterior = null,
            ValorNuevo = "{\"monto\":100}",
            CreadoEl = DateTimeOffset.UtcNow
        };
        db.Auditoria.Add(fila);
        await db.SaveChangesAsync();

        return fila.Id;
    }

    // ---- RLS (tasks 1.22-1.24) -----------------------------------------------------------------

    /// <summary>Mutation target (slice 1, row 6): borrar
    /// <c>migrationBuilder.HabilitarRlsDeTenant("auditoria")</c> hace fallar este test Y
    /// <see cref="UnInsertConIdTenantAjenoSeRechaza"/>. Corre sobre <c>ways_app</c>
    /// (<c>mutation-proof-tests</c> regla 5) — la única conexión bajo la cual una prueba de RLS
    /// prueba algo real.</summary>
    [Fact]
    public async Task RlsBloqueaLaLecturaCrossTenantSobreWaysApp()
    {
        var idActor = await ObtenerIdDeUsuarioRootAsync();
        var tenantA = await SembrarTenantAsync(nameof(RlsBloqueaLaLecturaCrossTenantSobreWaysApp) + "-A");
        var tenantB = await SembrarTenantAsync(nameof(RlsBloqueaLaLecturaCrossTenantSobreWaysApp) + "-B");

        var filaA = await SembrarFilaAsync(tenantA, idActor);

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantB.Id);
        await using var comando = cruda.CreateCommand();
        comando.CommandText = "SELECT count(*) FROM auditoria WHERE id_auditoria = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = filaA });

        var totalDesdeTenantAjeno = (long)(await comando.ExecuteScalarAsync())!;
        Assert.Equal(0, totalDesdeTenantAjeno);

        // Control positivo: la MISMA fila, leída con el GUC de su propio tenant, sí aparece —
        // sin esto, un 0 espurio (p.ej. un id mal armado) pasaría el test sin probar nada.
        await using var propia = await fixture.AbrirConexionCrudaAsync("tenant", tenantA.Id);
        await using var comandoPropio = propia.CreateCommand();
        comandoPropio.CommandText = "SELECT count(*) FROM auditoria WHERE id_auditoria = $1";
        comandoPropio.Parameters.Add(new NpgsqlParameter { Value = filaA });

        var totalDesdeSuPropioTenant = (long)(await comandoPropio.ExecuteScalarAsync())!;
        Assert.Equal(1, totalDesdeSuPropioTenant);
    }

    /// <summary>Mutation target (slice 1, row 6) — la mitad de escritura: un <c>INSERT</c> con
    /// <c>id_tenant</c> ajeno al GUC de la sesión se rechaza por <c>WITH CHECK</c>,
    /// <c>42501</c>.</summary>
    [Fact]
    public async Task UnInsertConIdTenantAjenoSeRechaza()
    {
        var idActor = await ObtenerIdDeUsuarioRootAsync();
        var tenantA = await SembrarTenantAsync(nameof(UnInsertConIdTenantAjenoSeRechaza) + "-A");
        var tenantB = await SembrarTenantAsync(nameof(UnInsertConIdTenantAjenoSeRechaza) + "-B");

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", tenantA.Id);
        var ahora = DateTimeOffset.UtcNow;

        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO auditoria (id_tenant, id_actor, accion, entidad, id_entidad, valor_nuevo, creado_el) " +
            "VALUES ($1, $2, 'precio.cambio', 'articulo', 41, '{}'::jsonb, $3)";
        comando.Parameters.Add(new NpgsqlParameter { Value = tenantB.Id }); // ajeno a la sesión (tenant A)
        comando.Parameters.Add(new NpgsqlParameter { Value = idActor });
        comando.Parameters.Add(new NpgsqlParameter { Value = ahora });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    // ---- reloj fijo (tasks 1.25-1.26) ------------------------------------------------------------

    /// <summary>Mutation target (slice 1, row 7): <c>creado_el = reloj.Ahora</c> → un
    /// <c>DateTimeOffset.UtcNow</c> hardcodeado haría fallar la igualdad exacta. Cubre el modo ADO
    /// (<see cref="ServicioDeAuditoria.RegistrarAsync"/>).</summary>
    [Fact]
    public async Task ElModoAdoEstampaCreadoElExactamenteIgualAlRelojFijo()
    {
        var idActor = await ObtenerIdDeUsuarioRootAsync();
        var tenant = await SembrarTenantAsync(nameof(ElModoAdoEstampaCreadoElExactamenteIgualAlRelojFijo));
        var momentoFijo = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenant.Id));
        var servicio = new ServicioDeAuditoria(db, new RelojFijo(momentoFijo), new ContextoFijo(tenant.Id, idActor));

        await using var transaccion = await db.Database.BeginTransactionAsync();
        var conexion = db.Database.GetDbConnection();
        var transaccionCruda = db.Database.CurrentTransaction!.GetDbTransaction();

        var registro = new RegistroDeAuditoria(
            tenant.Id, idPuntoVenta: null, AccionAuditada.PrecioCambio, idEntidad: 41,
            valorAnterior: null, valorNuevo: new Dictionary<string, object?> { ["monto"] = 100m });

        await servicio.RegistrarAsync(conexion, transaccionCruda, registro, CancellationToken.None);
        await transaccion.CommitAsync();

        var creadoEl = await db.Auditoria.Where(a => a.IdTenant == tenant.Id).Select(a => a.CreadoEl).SingleAsync();
        Assert.Equal(momentoFijo, creadoEl);
    }

    /// <summary>Mismo mutation target que el anterior, cubriendo el modo EF
    /// (<see cref="ServicioDeAuditoria.Registrar"/>) — los dos modos estampan por separado, así
    /// que cada uno necesita su propia evidencia.</summary>
    [Fact]
    public async Task ElModoEfEstampaCreadoElExactamenteIgualAlRelojFijo()
    {
        var idActor = await ObtenerIdDeUsuarioRootAsync();
        var tenant = await SembrarTenantAsync(nameof(ElModoEfEstampaCreadoElExactamenteIgualAlRelojFijo));
        var momentoFijo = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenant.Id));
        var servicio = new ServicioDeAuditoria(db, new RelojFijo(momentoFijo), new ContextoFijo(tenant.Id, idActor));

        var registro = new RegistroDeAuditoria(
            tenant.Id, idPuntoVenta: null, AccionAuditada.PrecioCambio, idEntidad: 41,
            valorAnterior: null, valorNuevo: new Dictionary<string, object?> { ["monto"] = 100m });

        servicio.Registrar(registro);
        await db.SaveChangesAsync();

        var creadoEl = await db.Auditoria.Where(a => a.IdTenant == tenant.Id).Select(a => a.CreadoEl).SingleAsync();
        Assert.Equal(momentoFijo, creadoEl);
    }

    // ---- db-error-backstops (task 1.28, gate §B) -------------------------------------------------

    /// <summary>Fail-closed por dato (design decisión 10, gate §B): un <c>contexto.UsuarioId</c>
    /// inexistente hace que <c>RegistrarAsync</c> dispare <c>fk_auditoria_actor</c>, SQLSTATE
    /// <c>23503</c> — y esa misma excepción, pasada por <see cref="ManejadorDeErrores"/>, confirma
    /// (no asume) el mapeo genérico <c>fk_</c>/<c>23503</c> → <c>400 referencia_invalida</c>
    /// (<c>ManejadorDeErrores.cs:224</c>, sin modificar).</summary>
    [Fact]
    public async Task FkAuditoriaActorRechazaUnActorInexistenteYSeMapeaA400ReferenciaInvalida()
    {
        var tenant = await SembrarTenantAsync(nameof(FkAuditoriaActorRechazaUnActorInexistenteYSeMapeaA400ReferenciaInvalida));
        const int idActorInexistente = int.MaxValue;

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenant.Id));
        var servicio = new ServicioDeAuditoria(
            db, new RelojFijo(DateTimeOffset.UtcNow), new ContextoFijo(tenant.Id, idActorInexistente));

        await using var transaccion = await db.Database.BeginTransactionAsync();
        var conexion = db.Database.GetDbConnection();
        var transaccionCruda = db.Database.CurrentTransaction!.GetDbTransaction();

        var registro = new RegistroDeAuditoria(
            tenant.Id, idPuntoVenta: null, AccionAuditada.PrecioCambio, idEntidad: 41,
            valorAnterior: null, valorNuevo: new Dictionary<string, object?> { ["monto"] = 100m });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() =>
            servicio.RegistrarAsync(conexion, transaccionCruda, registro, CancellationToken.None));

        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_auditoria_actor", excepcion.ConstraintName);

        // La transacción de negocio aborta enteramente — ninguna fila queda escrita.
        await transaccion.RollbackAsync();
        var totalDeFilas = await db.Auditoria.Where(a => a.IdTenant == tenant.Id).CountAsync();
        Assert.Equal(0, totalDeFilas);

        // Confirmado, no asumido: la MISMA excepción real, pasada por el manejador genérico.
        var servicioDeProblemDetails = new ServicioDeProblemDetailsFalso();
        var manejador = new ManejadorDeErrores(servicioDeProblemDetails, NullLogger<ManejadorDeErrores>.Instance);
        var contextoHttp = new DefaultHttpContext();

        var manejado = await manejador.TryHandleAsync(contextoHttp, excepcion, CancellationToken.None);

        Assert.True(manejado);
        Assert.NotNull(servicioDeProblemDetails.Ultimo);
        Assert.Equal(StatusCodes.Status400BadRequest, contextoHttp.Response.StatusCode);
        Assert.Equal("referencia_invalida", servicioDeProblemDetails.Ultimo!.ProblemDetails.Extensions["codigo"] as string);
    }

    private sealed class ServicioDeProblemDetailsFalso : IProblemDetailsService
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
