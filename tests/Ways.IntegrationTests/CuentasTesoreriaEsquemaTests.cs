using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Entorno sembrado compartido por las pruebas de esquema de cuentas de tesorería: dos tenants con
/// una empresa, un usuario, una cuenta, un punto de venta con turnos y un medio de pago electrónico
/// cada uno (el tenant B solo lo necesario para las FK cruzadas).
/// </summary>
internal sealed record SiembraDeCuentas(
    int IdTenantA, int IdTenantB, int IdEmpresaA, int IdEmpresaB, int IdCuentaA, int IdCuentaB,
    int IdUsuarioA, int IdUsuarioB, int IdTurnoA, int IdTurnoA2, int IdTurnoB, int IdMedioA, int IdMedioA2,
    int IdMedioB);

internal static class SiembraDeCuentasHelper
{
    public static async Task<SiembraDeCuentas> SembrarAsync(WaysApiFixture fixture, string nombre)
    {
        using var _ = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenantA = new Tenant { Nombre = $"{nombre}-A", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        var tenantB = new Tenant { Nombre = $"{nombre}-B", Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.AddRange(tenantA, tenantB);
        await db.SaveChangesAsync();

        var empresaA = new Empresa { IdTenant = tenantA.Id, RazonSocial = $"{nombre}-A", CreatedAt = ahora, UpdatedAt = ahora };
        var empresaB = new Empresa { IdTenant = tenantB.Id, RazonSocial = $"{nombre}-B", CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.AddRange(empresaA, empresaB);
        await db.SaveChangesAsync();

        var puntoVentaA = new PuntoVenta { IdTenant = tenantA.Id, IdEmpresa = empresaA.Id, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        var puntoVentaB = new PuntoVenta { IdTenant = tenantB.Id, IdEmpresa = empresaB.Id, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.PuntosVenta.AddRange(puntoVentaA, puntoVentaB);
        await db.SaveChangesAsync();

        var hasheador = new HasheadorPbkdf2();
        Usuario NuevoUsuario(int idTenant, string sufijo) => new()
        {
            IdTenant = idTenant, NombreUsuario = $"u-{sufijo}", Mail = $"{nombre}-{sufijo}@ways.test",
            RolId = (int)RolConocido.Admin, PasswordHash = hasheador.Hashear("una-contraseña-larga"),
            PasswordAlgoritmo = hasheador.Algoritmo, PasswordActualizadoEl = ahora, CreatedAt = ahora, UpdatedAt = ahora
        };
        var usuarioA = NuevoUsuario(tenantA.Id, "a");
        var usuarioB = NuevoUsuario(tenantB.Id, "b");
        db.Usuarios.AddRange(usuarioA, usuarioB);
        await db.SaveChangesAsync();

        var cuentaA = new CuentaTesoreria { IdTenant = tenantA.Id, IdEmpresa = empresaA.Id, Nombre = "Mercado Pago", CreatedAt = ahora, UpdatedAt = ahora };
        var cuentaB = new CuentaTesoreria { IdTenant = tenantB.Id, IdEmpresa = empresaB.Id, Nombre = "Mercado Pago", CreatedAt = ahora, UpdatedAt = ahora };
        db.CuentasTesoreria.AddRange(cuentaA, cuentaB);
        await db.SaveChangesAsync();

        TurnoCaja NuevoTurno(int idTenant, int idPuntoVenta, int idEmpleado) => new()
        {
            IdTenant = idTenant, IdPuntoVenta = idPuntoVenta, IdEmpleadoApertura = idEmpleado, FechaApertura = ahora,
            FondoInicial = 0m, Estado = EstadoTurno.Abierto, CreatedAt = ahora, UpdatedAt = ahora
        };
        var turnoA = NuevoTurno(tenantA.Id, puntoVentaA.Id, usuarioA.Id);
        turnoA.Estado = EstadoTurno.Cerrado;
        turnoA.IdEmpleadoCierre = usuarioA.Id;
        turnoA.FechaCierre = ahora;
        var turnoA2 = NuevoTurno(tenantA.Id, puntoVentaA.Id, usuarioA.Id);
        var turnoB = NuevoTurno(tenantB.Id, puntoVentaB.Id, usuarioB.Id);
        db.TurnosCaja.AddRange(turnoA, turnoA2, turnoB);
        await db.SaveChangesAsync();

        MedioPago NuevoMedio(int idTenant, string sufijo) => new()
        {
            IdTenant = idTenant, Nombre = $"QR {sufijo}", Orden = 1, Comportamiento = ComportamientoMedioPago.Electronico,
            AdmiteVuelto = false, RequiereReferencia = true, CreatedAt = ahora, UpdatedAt = ahora
        };
        var medioA = NuevoMedio(tenantA.Id, "a");
        var medioA2 = NuevoMedio(tenantA.Id, "a2");
        var medioB = NuevoMedio(tenantB.Id, "b");
        db.MediosPago.AddRange(medioA, medioA2, medioB);
        await db.SaveChangesAsync();

        return new SiembraDeCuentas(
            tenantA.Id, tenantB.Id, empresaA.Id, empresaB.Id, cuentaA.Id, cuentaB.Id, usuarioA.Id, usuarioB.Id,
            turnoA.Id, turnoA2.Id, turnoB.Id, medioA.Id, medioA2.Id, medioB.Id);
    }

    /// <summary>Comando como <c>ways_app</c> (NOBYPASSRLS) con el GUC de tenant puesto a mano; al
    /// liberarlo se cierra también la conexión que abrió.</summary>
    public static async Task<ComandoDeSiembra> ComandoAsync(
        WaysApiFixture fixture, int idTenant, string sql, params object[] parametros)
    {
        var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = valor });
        }
        return new ComandoDeSiembra(conexion, comando);
    }
}

internal sealed class ComandoDeSiembra(NpgsqlConnection conexion, NpgsqlCommand comando) : IAsyncDisposable
{
    public NpgsqlParameterCollection Parameters => comando.Parameters;

    public Task<int> ExecuteNonQueryAsync() => comando.ExecuteNonQueryAsync();

    public Task<object?> ExecuteScalarAsync() => comando.ExecuteScalarAsync();

    public async ValueTask DisposeAsync()
    {
        await comando.DisposeAsync();
        await conexion.DisposeAsync();
    }
}

/// <summary>
/// Esquema de <c>cuentas_tesoreria</c> con SQL crudo sobre la conexión <c>ways_app</c>
/// (NOBYPASSRLS): aislamiento por tenant, unicidad del nombre por empresa entre las filas vivas, la
/// obligatoriedad de <c>id_empresa</c>, la FK compuesta a empresas y la clave alterna. Cada rechazo se
/// aserta por SQLSTATE y por nombre de constraint.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentasTesoreriaEsquemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private Task<SiembraDeCuentas> SembrarAsync(string nombre) => SiembraDeCuentasHelper.SembrarAsync(fixture, nombre);

    private Task<ComandoDeSiembra> ComandoAsync(int idTenant, string sql, params object[] parametros) =>
        SiembraDeCuentasHelper.ComandoAsync(fixture, idTenant, sql, parametros);

    private Task<ComandoDeSiembra> InsertarCuentaAsync(int idTenant, int idEmpresa, string nombre, string eliminada = "NULL") =>
        ComandoAsync(
            idTenant,
            "INSERT INTO cuentas_tesoreria (id_tenant, id_empresa, nombre, created_at, updated_at, deleted_at) " +
            $"VALUES ($1, $2, $3, now(), now(), {eliminada})",
            idTenant, idEmpresa, nombre);

    [Fact]
    public async Task SoloElTenantDuenoVeLasCuentasYOtroNoPuedeInsertarConSuIdTenant()
    {
        var s = await SembrarAsync(nameof(SoloElTenantDuenoVeLasCuentasYOtroNoPuedeInsertarConSuIdTenant));

        await using var deA = await ComandoAsync(
            s.IdTenantA, "SELECT count(*) FROM cuentas_tesoreria WHERE id_cuenta_tesoreria = $1", s.IdCuentaA);
        await using var deB = await ComandoAsync(
            s.IdTenantB, "SELECT count(*) FROM cuentas_tesoreria WHERE id_cuenta_tesoreria = $1", s.IdCuentaA);
        Assert.Equal(1L, await deA.ExecuteScalarAsync());
        Assert.Equal(0L, await deB.ExecuteScalarAsync());

        // La sesión del tenant A intenta escribir una fila con el id_tenant del B.
        await using var intruso = await InsertarCuentaAsync(s.IdTenantA, s.IdEmpresaA, "intruso");
        intruso.Parameters[0].Value = s.IdTenantB;
        intruso.Parameters[1].Value = s.IdEmpresaB;
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => intruso.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    [Fact]
    public async Task ElFiltroDeEfNoDevuelveLasCuentasDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(ElFiltroDeEfNoDevuelveLasCuentasDeOtroTenant));

        await using var sesionA = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, s.IdTenantA));
        await using var sesionB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, s.IdTenantB));

        Assert.Equal([s.IdCuentaA], await sesionA.CuentasTesoreria.Select(c => c.Id).ToListAsync());
        Assert.Equal([s.IdCuentaB], await sesionB.CuentasTesoreria.Select(c => c.Id).ToListAsync());
    }

    [Fact]
    public async Task DosCuentasVivasConElMismoNombreEnLaMismaEmpresaViolanElUnicoSinDistinguirMayusculas()
    {
        var s = await SembrarAsync(nameof(DosCuentasVivasConElMismoNombreEnLaMismaEmpresaViolanElUnicoSinDistinguirMayusculas));

        await using var duplicada = await InsertarCuentaAsync(s.IdTenantA, s.IdEmpresaA, "MERCADO PAGO");

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => duplicada.ExecuteNonQueryAsync());
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_cuentas_tesoreria_nombre_empresa", excepcion.ConstraintName);
    }

    [Fact]
    public async Task ElNombreSePuedeRepetirEnOtraEmpresaDelTenantYTrasLaBajaLogica()
    {
        var s = await SembrarAsync(nameof(ElNombreSePuedeRepetirEnOtraEmpresaDelTenantYTrasLaBajaLogica));
        var ahora = DateTimeOffset.UtcNow;
        int idOtraEmpresa;
        await using (var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma))
        {
            var otra = new Empresa { IdTenant = s.IdTenantA, RazonSocial = "otra", CreatedAt = ahora, UpdatedAt = ahora };
            db.Empresas.Add(otra);
            await db.SaveChangesAsync();
            idOtraEmpresa = otra.Id;
        }

        await using var enOtraEmpresa = await InsertarCuentaAsync(s.IdTenantA, idOtraEmpresa, "Mercado Pago");
        Assert.Equal(1, await enOtraEmpresa.ExecuteNonQueryAsync());

        await using var baja = await ComandoAsync(
            s.IdTenantA, "UPDATE cuentas_tesoreria SET deleted_at = now() WHERE id_cuenta_tesoreria = $1", s.IdCuentaA);
        Assert.Equal(1, await baja.ExecuteNonQueryAsync());

        await using var reutilizada = await InsertarCuentaAsync(s.IdTenantA, s.IdEmpresaA, "Mercado Pago");
        Assert.Equal(1, await reutilizada.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task ElNombreEsIndependienteEntreTenants()
    {
        var s = await SembrarAsync(nameof(ElNombreEsIndependienteEntreTenants));

        // La siembra ya creó una cuenta "Mercado Pago" por tenant: el único no cruza tenants.
        await using var plataforma = await fixture.AbrirConexionCrudaAsync("plataforma", null);
        await using var comando = plataforma.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM cuentas_tesoreria WHERE id_cuenta_tesoreria IN ($1, $2) AND nombre = 'Mercado Pago'";
        comando.Parameters.Add(new NpgsqlParameter { Value = s.IdCuentaA });
        comando.Parameters.Add(new NpgsqlParameter { Value = s.IdCuentaB });
        Assert.Equal(2L, await comando.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LaEmpresaEsObligatoria()
    {
        var s = await SembrarAsync(nameof(LaEmpresaEsObligatoria));

        await using var sinEmpresa = await ComandoAsync(
            s.IdTenantA,
            "INSERT INTO cuentas_tesoreria (id_tenant, id_empresa, nombre, created_at, updated_at) " +
            "VALUES ($1, NULL, 'sin empresa', now(), now())",
            s.IdTenantA);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => sinEmpresa.ExecuteNonQueryAsync());
        Assert.Equal("23502", excepcion.SqlState);
        Assert.Equal("id_empresa", excepcion.ColumnName);
    }

    [Fact]
    public async Task LaFkCompuestaRechazaUnaEmpresaDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(LaFkCompuestaRechazaUnaEmpresaDeOtroTenant));

        // Sesión e id_tenant del B apuntando a la empresa del A: pasa la política de filas y solo la
        // FK compuesta (id_empresa, id_tenant) lo frena.
        await using var cruzada = await InsertarCuentaAsync(s.IdTenantB, s.IdEmpresaA, "cruzada");

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => cruzada.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_cuentas_tesoreria_empresa", excepcion.ConstraintName);
    }

    [Fact]
    public async Task NingunIndiceDeCuentasTesoreriaTieneNombreImplicitoDeEf()
    {
        var s = await SembrarAsync(nameof(NingunIndiceDeCuentasTesoreriaTieneNombreImplicitoDeEf));

        await using var comando = await ComandoAsync(
            s.IdTenantA,
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'cuentas_tesoreria' AND indexname LIKE 'IX_%'");

        Assert.Equal(0L, await comando.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LaClaveAlternaConElTenantExisteComoUnicaParaQueLasFkSeanCompuestas()
    {
        var s = await SembrarAsync(nameof(LaClaveAlternaConElTenantExisteComoUnicaParaQueLasFkSeanCompuestas));

        await using var comando = await ComandoAsync(
            s.IdTenantA,
            "SELECT contype::text FROM pg_constraint " +
            "WHERE conname = 'ak_cuentas_tesoreria_id_cuenta_tesoreria_id_tenant' " +
            "AND conrelid = 'cuentas_tesoreria'::regclass");

        Assert.Equal("u", await comando.ExecuteScalarAsync());
    }
}
