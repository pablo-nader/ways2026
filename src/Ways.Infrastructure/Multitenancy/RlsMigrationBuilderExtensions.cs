using Microsoft.EntityFrameworkCore.Migrations;

namespace Ways.Infrastructure.Multitenancy;

/// <summary>
/// Helpers para escribir RLS dentro de una migración (ADR-15): la tabla que necesita
/// aislamiento la activa en la misma migración que la crea, nunca en una migración
/// separada — eso dejaría una ventana con la tabla scopeada y sin policy.
/// </summary>
public static class RlsMigrationBuilderExtensions
{
    /// <summary>
    /// Crea las funciones SQL que las policies usan para leer el contexto de tenant
    /// (ADR-4). Se llama una sola vez, en la primera migración que activa RLS.
    /// </summary>
    public static void CrearFuncionesDeContextoDeTenant(this MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE FUNCTION app_tenant_actual() RETURNS integer LANGUAGE sql STABLE AS
            $$ SELECT NULLIF(current_setting('app.tenant_id', true), '')::int $$;
            """);

        migrationBuilder.Sql(
            """
            CREATE FUNCTION app_modo() RETURNS text LANGUAGE sql STABLE AS
            $$ SELECT COALESCE(NULLIF(current_setting('app.acceso', true), ''), 'ninguno') $$;
            """);

        migrationBuilder.Sql(
            """
            CREATE FUNCTION app_es_plataforma() RETURNS boolean LANGUAGE sql STABLE AS
            $$ SELECT app_modo() = 'plataforma' $$;
            """);
    }

    /// <summary>
    /// Activa RLS con la policy estándar de aislamiento por tenant (ADR-4, ADR-5) sobre
    /// <paramref name="tabla"/>. Requiere que <paramref name="tabla"/> tenga una columna
    /// <c>id_tenant</c> y que <see cref="CrearFuncionesDeContextoDeTenant"/> ya se haya
    /// ejecutado en esta base.
    /// </summary>
    public static void HabilitarRlsDeTenant(this MigrationBuilder migrationBuilder, string tabla)
    {
        migrationBuilder.Sql($"ALTER TABLE {tabla} ENABLE ROW LEVEL SECURITY;");
        migrationBuilder.Sql($"ALTER TABLE {tabla} FORCE ROW LEVEL SECURITY;");
        migrationBuilder.Sql(
            $"""
            CREATE POLICY {tabla}_tenant ON {tabla}
                USING      (app_es_plataforma() OR id_tenant = app_tenant_actual())
                WITH CHECK (app_es_plataforma() OR id_tenant = app_tenant_actual());
            """);
    }
}
