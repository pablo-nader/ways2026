namespace Ways.IntegrationTests;

/// <summary>
/// Task 1.17 (tasks.md): la prueba de aislamiento de dos capas (ADR-4, ADR-15, ADR-17).
/// Todo el archivo queda <c>Skip</c> hasta que la migración 1 (Organización) esté
/// aprobada y generada — sin tablas, cualquier intento de sembrar datos falla con
/// "relation does not exist" en vez de probar o refutar el aislamiento.
///
/// Se deja el cuerpo de cada caso como lo describe el plan de pruebas de design.md para
/// no perder el detalle entre el DB CHANGE GATE y el momento de completarlos.
/// </summary>
public class AislamientoDeTenantTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string PendienteDeMigracion =
        "Requiere la migración 1 (Organización), pendiente de aprobación en el DB CHANGE GATE.";

    [Fact(Skip = PendienteDeMigracion)]
    public Task ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant()
    {
        // Sesión tenant A lista `areas` (o `empresas`, disponible desde este slice):
        // ninguna fila de tenant B aparece.
        _ = fixture;
        return Task.CompletedTask;
    }

    [Fact(Skip = PendienteDeMigracion)]
    public Task RlsBloqueaUnaLecturaQueSalteaElFiltroDeEf()
    {
        // Conexión NpgsqlConnection cruda como ways_app, app.acceso='tenant',
        // app.tenant_id=A: SELECT * FROM empresas trae solo A. Repetido con
        // IgnoreQueryFilters(["Tenant"]) a través de EF: sigue siendo solo A.
        return Task.CompletedTask;
    }

    [Fact(Skip = PendienteDeMigracion)]
    public Task WithCheckRechazaUnInsertConIdTenantAjeno()
    {
        // INSERT ... id_tenant = B bajo contexto de tenant A es rechazado por la policy.
        return Task.CompletedTask;
    }

    [Fact(Skip = PendienteDeMigracion)]
    public Task SinGucElResultadoEsCeroFilasNoUnError()
    {
        // GUC sin setear ⇒ cero filas (falla cerrado), no una excepción y no "todo".
        return Task.CompletedTask;
    }

    [Fact(Skip = PendienteDeMigracion)]
    public Task NoHayFugaDeGucEntreConexionesDelPool()
    {
        // Después de una request de tenant A, una conexión reciclada del pool reporta
        // current_setting('app.tenant_id', true) IS NULL (Npgsql DISCARD ALL).
        return Task.CompletedTask;
    }

    [Fact(Skip = PendienteDeMigracion)]
    public Task LaCoberturaDePoliciesEsCompleta()
    {
        // La query de pg_class/pg_policies de ADR-15 no devuelve ninguna fila.
        return Task.CompletedTask;
    }

    [Fact(Skip = PendienteDeMigracion)]
    public Task WaysAppNoTieneRolsuperNiRolbypassrls()
    {
        // SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = 'ways_app' — ambos false.
        return Task.CompletedTask;
    }
}
