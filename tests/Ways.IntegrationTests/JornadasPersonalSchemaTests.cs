using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Plantel;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Esquema de <c>jornadas_personal</c> con SQL crudo sobre la conexión <c>ways_app</c> (NOBYPASSRLS): el CHECK
/// de horas, la unicidad por personal y día (SQLSTATE 23505 y nombre del índice), las claves foráneas, el
/// aislamiento por tenant y el upsert concurrente que nunca viola la unicidad. Cada rechazo se aserta por
/// SQLSTATE y por nombre de constraint.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class JornadasPersonalSchemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
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
        Siembra s, string horas, string fecha = "2026-10-01", int? idTenant = null) =>
        ComandoAsync(
            idTenant ?? s.IdTenantA,
            "INSERT INTO jornadas_personal (id_tenant, id_personal, id_empleado, fecha, horas, created_at, updated_at) " +
            $"VALUES ($1, $2, $3, DATE '{fecha}', {horas}, now(), now())",
            idTenant ?? s.IdTenantA, s.IdPersonalDeA, s.IdUsuarioDeA);

    [Theory]
    [InlineData("0")]
    [InlineData("8.5")]
    [InlineData("24")]
    [InlineData("0.01")]
    public async Task LasHorasEntreCeroYVeinticuatroSeInsertan(string horas)
    {
        var s = await SembrarAsync(nameof(LasHorasEntreCeroYVeinticuatroSeInsertan) + horas);

        await using var insertar = await InsertarAsync(s, horas);

        Assert.Equal(1, await insertar.ExecuteNonQueryAsync());
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("24.01")]
    [InlineData("-8")]
    public async Task LasHorasFueraDeRangoViolanSuCheck(string horas)
    {
        var s = await SembrarAsync(nameof(LasHorasFueraDeRangoViolanSuCheck) + horas);

        await using var insertar = await InsertarAsync(s, horas);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => insertar.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_jornadas_personal_horas", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaSegundaJornadaDelMismoPersonalYDiaViolaLaUnicidad()
    {
        var s = await SembrarAsync(nameof(UnaSegundaJornadaDelMismoPersonalYDiaViolaLaUnicidad));
        await using (var primera = await InsertarAsync(s, "8"))
        {
            Assert.Equal(1, await primera.ExecuteNonQueryAsync());
        }

        await using var repetida = await InsertarAsync(s, "4");

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => repetida.ExecuteNonQueryAsync());
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_jornadas_personal_personal_fecha", excepcion.ConstraintName);

        await using var otroDia = await InsertarAsync(s, "4", fecha: "2026-10-02");
        Assert.Equal(1, await otroDia.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ElUpsertConcurrenteDelMismoDiaDejaUnaSolaFilaSinViolarLaUnicidad()
    {
        var s = await SembrarAsync(nameof(ElUpsertConcurrenteDelMismoDiaDejaUnaSolaFilaSinViolarLaUnicidad));
        var fecha = new DateOnly(2026, 10, 1);

        await Task.WhenAll(Enumerable.Range(1, 8).Select(async i =>
        {
            await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenantA);
            await EscriturasDePersonal.UpsertarJornadaAsync(
                conexion, null, s.IdTenantA, s.IdPersonalDeA, s.IdUsuarioDeA, fecha, i, DateTimeOffset.UtcNow, CancellationToken.None);
        }));

        await using var contar = await ComandoAsync(
            s.IdTenantA, "SELECT count(*) FROM jornadas_personal WHERE id_personal = $1", s.IdPersonalDeA);
        Assert.Equal(1L, await contar.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LaClaveForaneaCompuestaRechazaUnPersonalDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(LaClaveForaneaCompuestaRechazaUnPersonalDeOtroTenant));

        // Sesión y id_tenant del tenant B apuntando al personal del tenant A: pasa la política de filas y solo la
        // FK compuesta (id_personal, id_tenant) lo frena.
        await using var insertar = await InsertarAsync(s, "8", idTenant: s.IdTenantB);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => insertar.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_jornadas_personal_personal", excepcion.ConstraintName);
    }

    [Fact]
    public async Task LaClaveForaneaDelEmpleadoRechazaUnUsuarioInexistente()
    {
        var s = await SembrarAsync(nameof(LaClaveForaneaDelEmpleadoRechazaUnUsuarioInexistente));

        await using var insertar = await ComandoAsync(
            s.IdTenantA,
            "INSERT INTO jornadas_personal (id_tenant, id_personal, id_empleado, fecha, horas, created_at, updated_at) " +
            "VALUES ($1, $2, 999999, DATE '2026-10-01', 8, now(), now())",
            s.IdTenantA, s.IdPersonalDeA);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => insertar.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_jornadas_personal_empleado", excepcion.ConstraintName);
    }

    [Fact]
    public async Task SoloElTenantDuenoVeLasJornadasYOtroNoPuedeInsertarConSuIdTenant()
    {
        var s = await SembrarAsync(nameof(SoloElTenantDuenoVeLasJornadasYOtroNoPuedeInsertarConSuIdTenant));
        await using (var alta = await InsertarAsync(s, "8"))
        {
            Assert.Equal(1, await alta.ExecuteNonQueryAsync());
        }

        await using var deA = await ComandoAsync(
            s.IdTenantA, "SELECT count(*) FROM jornadas_personal WHERE id_personal = $1", s.IdPersonalDeA);
        await using var deB = await ComandoAsync(
            s.IdTenantB, "SELECT count(*) FROM jornadas_personal WHERE id_personal = $1", s.IdPersonalDeA);
        Assert.Equal(1L, await deA.ExecuteScalarAsync());
        Assert.Equal(0L, await deB.ExecuteScalarAsync());

        // La sesión del tenant A intenta escribir una fila con el id_tenant del B.
        await using var intruso = await ComandoAsync(
            s.IdTenantA,
            "INSERT INTO jornadas_personal (id_tenant, id_personal, id_empleado, fecha, horas, created_at, updated_at) " +
            "VALUES ($1, $2, $3, DATE '2026-10-05', 8, now(), now())",
            s.IdTenantB, s.IdPersonalDeA, s.IdUsuarioDeA);
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => intruso.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);

        // Una sesión ajena tampoco puede cambiar el total: la política filtra la fila y el UPDATE no toca nada.
        await using var ajeno = await ComandoAsync(
            s.IdTenantB, "UPDATE jornadas_personal SET horas = 0 WHERE id_personal = $1", s.IdPersonalDeA);
        Assert.Equal(0, await ajeno.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ElFiltroDeEfNoDevuelveLasJornadasDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(ElFiltroDeEfNoDevuelveLasJornadasDeOtroTenant));
        await using (var alta = await InsertarAsync(s, "8"))
        {
            await alta.ExecuteNonQueryAsync();
        }

        await using var sesionA = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, s.IdTenantA));
        await using var sesionB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, s.IdTenantB));

        Assert.Single(await sesionA.JornadasPersonal.ToListAsync());
        Assert.Empty(await sesionB.JornadasPersonal.ToListAsync());
    }
}
