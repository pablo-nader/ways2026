using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Esquema del libro de personal con SQL crudo sobre la conexión <c>ways_app</c> (NOBYPASSRLS):
/// una fila que viola solo una cláusula por cada cláusula de los CHECK de ajuste y pago y por las
/// cláusulas de horas, valor hora, importe calculado y nulidad de <c>ck_movimientos_personal_horas</c>
/// (el importe no negativo de una carga lo implican esas cláusulas y no se prueba aparte), las claves
/// foráneas, el aislamiento por tenant y los valores del enum. Cada CHECK se aserta por SQLSTATE y por
/// nombre de constraint.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MovimientosPersonalSchemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Siembra(int IdTenantA, int IdTenantB, int IdPersonalDeA, int IdUsuarioDeA);

    private async Task<Siembra> SembrarAsync(string nombre)
    {
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenantA = new Tenant { Nombre = $"{nombre}-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = $"{nombre}-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        var hasheador = new HasheadorPbkdf2();
        var usuario = new Usuario
        {
            IdTenant = tenantA.Id, NombreUsuario = "uno", Mail = $"{nombre}-uno@ways.test",
            RolId = (int)RolConocido.Admin, PasswordHash = hasheador.Hashear("una-contraseña-larga"),
            PasswordAlgoritmo = hasheador.Algoritmo, PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();

        var personal = new Personal { IdTenant = tenantA.Id, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Personal.Add(personal);
        await db.SaveChangesAsync();

        return new Siembra(tenantA.Id, tenantB.Id, personal.Id, usuario.Id);
    }

    private async Task<NpgsqlCommand> ComandoAsync(int idTenant, string sql, params object[] parametros)
    {
        var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = valor });
        }
        return comando;
    }

    private Task<NpgsqlCommand> InsertarAsync(
        Siembra s, string tipo, string horas, string valorHora, string importe, int? idTenant = null) =>
        ComandoAsync(
            idTenant ?? s.IdTenantA,
            "INSERT INTO movimientos_personal " +
            "(id_tenant, id_personal, id_empleado, fecha, tipo, horas, valor_hora, importe, saldo_resultante, created_at) " +
            $"VALUES ($1, $2, $3, DATE '2026-10-01', '{tipo}', {horas}, {valorHora}, {importe}, 0, now())",
            idTenant ?? s.IdTenantA, s.IdPersonalDeA, s.IdUsuarioDeA);

    [Theory]
    [InlineData("horas", "8", "1500", "12000")]
    [InlineData("horas", "24", "100", "2400")]
    [InlineData("horas", "0.25", "1200.50", "300.13")]
    [InlineData("horas", "8", "0", "0")]
    [InlineData("ajuste", "NULL", "NULL", "-50")]
    [InlineData("ajuste", "NULL", "NULL", "50")]
    [InlineData("pago", "NULL", "NULL", "-0.01")]
    public async Task LasFormasValidasPorTipoSeInsertan(string tipo, string horas, string valorHora, string importe)
    {
        var s = await SembrarAsync(nameof(LasFormasValidasPorTipoSeInsertan) + tipo + horas + importe);

        await using var insertar = await InsertarAsync(s, tipo, horas, valorHora, importe);

        Assert.Equal(1, await insertar.ExecuteNonQueryAsync());
    }

    [Theory]
    [InlineData("horas", "0", "100", "0", "ck_movimientos_personal_horas")]
    [InlineData("horas", "24.01", "100", "2401", "ck_movimientos_personal_horas")]
    [InlineData("horas", "8", "-1", "-8", "ck_movimientos_personal_horas")]
    [InlineData("horas", "0.01", "-0.01", "0.00", "ck_movimientos_personal_horas")]
    [InlineData("horas", "8", "100", "799.99", "ck_movimientos_personal_horas")]
    [InlineData("horas", "NULL", "100", "0", "ck_movimientos_personal_horas")]
    [InlineData("horas", "8", "NULL", "0", "ck_movimientos_personal_horas")]
    [InlineData("ajuste", "1", "NULL", "50", "ck_movimientos_personal_ajuste")]
    [InlineData("ajuste", "NULL", "1", "50", "ck_movimientos_personal_ajuste")]
    [InlineData("ajuste", "NULL", "NULL", "0", "ck_movimientos_personal_ajuste")]
    [InlineData("pago", "NULL", "NULL", "0", "ck_movimientos_personal_pago")]
    [InlineData("pago", "NULL", "NULL", "10", "ck_movimientos_personal_pago")]
    [InlineData("pago", "8", "NULL", "-10", "ck_movimientos_personal_pago")]
    [InlineData("pago", "NULL", "5", "-10", "ck_movimientos_personal_pago")]
    public async Task UnaFormaInvalidaPorTipoViolaSuCheck(
        string tipo, string horas, string valorHora, string importe, string constraint)
    {
        var s = await SembrarAsync(nameof(UnaFormaInvalidaPorTipoViolaSuCheck) + tipo + horas + valorHora + importe);

        await using var insertar = await InsertarAsync(s, tipo, horas, valorHora, importe);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => insertar.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(constraint, excepcion.ConstraintName);
    }

    [Fact]
    public async Task ElEnumDeclaraLosTresTiposParaQueElPagoNoNecesiteOtraMigracion()
    {
        var s = await SembrarAsync(nameof(ElEnumDeclaraLosTresTiposParaQueElPagoNoNecesiteOtraMigracion));

        await using var comando = await ComandoAsync(
            s.IdTenantA, "SELECT string_agg(e::text, ',' ORDER BY e) FROM unnest(enum_range(NULL::tipo_movimiento_personal)) e");

        Assert.Equal("ajuste,horas,pago", await comando.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LaClaveForaneaCompuestaRechazaUnPersonalDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(LaClaveForaneaCompuestaRechazaUnPersonalDeOtroTenant));

        // Sesión y id_tenant del tenant B apuntando al personal del tenant A: pasa la política de
        // filas y solo la FK compuesta (id_personal, id_tenant) lo frena.
        await using var insertar = await InsertarAsync(s, "ajuste", "NULL", "NULL", "10", idTenant: s.IdTenantB);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => insertar.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_movimientos_personal_personal", excepcion.ConstraintName);
    }

    [Fact]
    public async Task LaClaveForaneaDelEmpleadoRechazaUnUsuarioInexistente()
    {
        var s = await SembrarAsync(nameof(LaClaveForaneaDelEmpleadoRechazaUnUsuarioInexistente));

        await using var insertar = await ComandoAsync(
            s.IdTenantA,
            "INSERT INTO movimientos_personal " +
            "(id_tenant, id_personal, id_empleado, fecha, tipo, importe, saldo_resultante, created_at) " +
            "VALUES ($1, $2, 999999, DATE '2026-10-01', 'ajuste', 10, 0, now())",
            s.IdTenantA, s.IdPersonalDeA);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => insertar.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_movimientos_personal_empleado", excepcion.ConstraintName);
    }

    [Fact]
    public async Task SoloElTenantDuenoVeLosMovimientosYOtroNoPuedeInsertarConSuIdTenant()
    {
        var s = await SembrarAsync(nameof(SoloElTenantDuenoVeLosMovimientosYOtroNoPuedeInsertarConSuIdTenant));
        await using (var alta = await InsertarAsync(s, "ajuste", "NULL", "NULL", "10"))
        {
            Assert.Equal(1, await alta.ExecuteNonQueryAsync());
        }

        await using var deA = await ComandoAsync(
            s.IdTenantA, "SELECT count(*) FROM movimientos_personal WHERE id_personal = $1", s.IdPersonalDeA);
        await using var deB = await ComandoAsync(
            s.IdTenantB, "SELECT count(*) FROM movimientos_personal WHERE id_personal = $1", s.IdPersonalDeA);
        Assert.Equal(1L, await deA.ExecuteScalarAsync());
        Assert.Equal(0L, await deB.ExecuteScalarAsync());

        // La sesión del tenant A intenta escribir una fila con el id_tenant del B.
        await using var intruso = await ComandoAsync(
            s.IdTenantA,
            "INSERT INTO movimientos_personal " +
            "(id_tenant, id_personal, id_empleado, fecha, tipo, importe, saldo_resultante, created_at) " +
            "VALUES ($1, $2, $3, DATE '2026-10-01', 'ajuste', 10, 0, now())",
            s.IdTenantB, s.IdPersonalDeA, s.IdUsuarioDeA);
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => intruso.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    [Fact]
    public async Task ElFiltroDeEfNoDevuelveLosMovimientosDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(ElFiltroDeEfNoDevuelveLosMovimientosDeOtroTenant));
        await using (var alta = await InsertarAsync(s, "ajuste", "NULL", "NULL", "10"))
        {
            await alta.ExecuteNonQueryAsync();
        }

        await using var sesionA = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, s.IdTenantA));
        await using var sesionB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, s.IdTenantB));

        Assert.Single(await sesionA.MovimientosPersonal.ToListAsync());
        Assert.Empty(await sesionB.MovimientosPersonal.ToListAsync());
    }
}
