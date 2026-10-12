using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// Esquema de <c>movimientos_cuenta</c> con SQL crudo sobre la conexión <c>ways_app</c> (NOBYPASSRLS):
/// una fila que viola solo una CHECK por cada CHECK, el único parcial del cierre de turno, las FK
/// compuestas, el aislamiento por tenant y los valores del enum. Cada rechazo se aserta por SQLSTATE y
/// por nombre de constraint.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MovimientosCuentaEsquemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private Task<SiembraDeCuentas> SembrarAsync(string nombre) => SiembraDeCuentasHelper.SembrarAsync(fixture, nombre);

    private Task<NpgsqlCommand> ComandoAsync(int idTenant, string sql, params object[] parametros) =>
        SiembraDeCuentasHelper.ComandoAsync(fixture, idTenant, sql, parametros);

    private static string Id(int? valor) => valor?.ToString() ?? "NULL";

    private Task<NpgsqlCommand> InsertarAsync(
        int idTenant, int idCuenta, int idUsuario, string tipo, string inicio, string ingreso, string egreso, string final,
        int? idTurno = null, int? idMedio = null, string baseBruta = "NULL", string porcentaje = "NULL") =>
        ComandoAsync(
            idTenant,
            "INSERT INTO movimientos_cuenta (id_tenant, id_cuenta_tesoreria, fecha, tipo, id_turno_caja, id_medio_pago, " +
            "concepto, inicio, ingreso, egreso, final, id_empleado, base_bruta, porcentaje_aplicado) " +
            $"VALUES ($1, $2, now(), '{tipo}', {Id(idTurno)}, {Id(idMedio)}, 'prueba', {inicio}, {ingreso}, {egreso}, {final}, " +
            $"$3, {baseBruta}, {porcentaje})",
            idTenant, idCuenta, idUsuario);

    private async Task InsertarOkAsync(
        SiembraDeCuentas s, string tipo, string inicio, string ingreso, string egreso, string final,
        int? idTurno = null, int? idMedio = null)
    {
        await using var comando = await InsertarAsync(
            s.IdTenantA, s.IdCuentaA, s.IdUsuarioA, tipo, inicio, ingreso, egreso, final, idTurno, idMedio);
        Assert.Equal(1, await comando.ExecuteNonQueryAsync());
    }

    [Theory]
    [InlineData("cobro", "0", "1000", "0", "1000")]
    [InlineData("comision", "1000", "0", "35.50", "964.50")]
    [InlineData("retencion_iibb", "964.50", "0", "10", "954.50")]
    [InlineData("ajuste", "954.50", "0", "954.50", "0")]
    [InlineData("ajuste", "0", "0", "0", "0")]
    public async Task LasFormasValidasDeLaCadenaSeInsertan(string tipo, string inicio, string ingreso, string egreso, string final)
    {
        var s = await SembrarAsync(nameof(LasFormasValidasDeLaCadenaSeInsertan) + tipo + inicio + ingreso + egreso);

        await InsertarOkAsync(s, tipo, inicio, ingreso, egreso, final);
    }

    [Theory]
    // La cadena no cierra: final distinto de inicio + ingreso - egreso.
    [InlineData("cobro", "0", "1000", "0", "999", "ck_movimientos_cuenta_cadena")]
    [InlineData("comision", "1000", "0", "35", "900", "ck_movimientos_cuenta_cadena")]
    // Los dos lados distintos de cero con la cadena consistente: solo viola un_solo_lado.
    [InlineData("ajuste", "0", "10", "5", "5", "ck_movimientos_cuenta_un_solo_lado")]
    // Un lado negativo con la cadena consistente y el otro en cero: solo viola importes_no_negativos.
    [InlineData("ajuste", "100", "-10", "0", "90", "ck_movimientos_cuenta_importes_no_negativos")]
    [InlineData("ajuste", "100", "0", "-5", "105", "ck_movimientos_cuenta_importes_no_negativos")]
    public async Task UnaFilaQueViolaUnaCheckEsRechazadaConSuNombre(
        string tipo, string inicio, string ingreso, string egreso, string final, string constraint)
    {
        var s = await SembrarAsync(nameof(UnaFilaQueViolaUnaCheckEsRechazadaConSuNombre) + tipo + inicio + ingreso + egreso + final);

        await using var comando = await InsertarAsync(
            s.IdTenantA, s.IdCuentaA, s.IdUsuarioA, tipo, inicio, ingreso, egreso, final);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(constraint, excepcion.ConstraintName);
    }

    [Fact]
    public async Task LasColumnasDeSnapshotGuardanLaBaseYElPorcentajeConTresDecimales()
    {
        var s = await SembrarAsync(nameof(LasColumnasDeSnapshotGuardanLaBaseYElPorcentajeConTresDecimales));

        await using (var insertar = await InsertarAsync(
                         s.IdTenantA, s.IdCuentaA, s.IdUsuarioA, "comision", "1000", "0", "35.50", "964.50",
                         baseBruta: "1000", porcentaje: "3.55"))
        {
            Assert.Equal(1, await insertar.ExecuteNonQueryAsync());
        }

        await using var leer = await ComandoAsync(
            s.IdTenantA,
            "SELECT base_bruta::text || '|' || porcentaje_aplicado::text FROM movimientos_cuenta WHERE id_cuenta_tesoreria = $1",
            s.IdCuentaA);
        Assert.Equal("1000.00|3.550", await leer.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UnPorcentajeAplicadoDeMilOMasNoCabeEnLaColumna()
    {
        var s = await SembrarAsync(nameof(UnPorcentajeAplicadoDeMilOMasNoCabeEnLaColumna));

        await using var comando = await InsertarAsync(
            s.IdTenantA, s.IdCuentaA, s.IdUsuarioA, "comision", "0", "0", "0", "0", porcentaje: "1000");

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("22003", excepcion.SqlState);
    }

    // ---- ux_movimientos_cuenta_cierre ------------------------------------------------------

    [Fact]
    public async Task UnaSegundaFilaDelMismoTurnoMedioYTipoViolaElUnicoDelCierre()
    {
        var s = await SembrarAsync(nameof(UnaSegundaFilaDelMismoTurnoMedioYTipoViolaElUnicoDelCierre));
        await InsertarOkAsync(s, "cobro", "0", "100", "0", "100", s.IdTurnoA, s.IdMedioA);

        await using var duplicada = await InsertarAsync(
            s.IdTenantA, s.IdCuentaA, s.IdUsuarioA, "cobro", "100", "50", "0", "150", s.IdTurnoA, s.IdMedioA);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => duplicada.ExecuteNonQueryAsync());
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_movimientos_cuenta_cierre", excepcion.ConstraintName);
    }

    [Fact]
    public async Task CambiarElTipoElMedioOElTurnoEvitaElUnicoDelCierre()
    {
        var s = await SembrarAsync(nameof(CambiarElTipoElMedioOElTurnoEvitaElUnicoDelCierre));
        await InsertarOkAsync(s, "cobro", "0", "100", "0", "100", s.IdTurnoA, s.IdMedioA);

        await InsertarOkAsync(s, "comision", "100", "0", "3", "97", s.IdTurnoA, s.IdMedioA);
        await InsertarOkAsync(s, "cobro", "97", "10", "0", "107", s.IdTurnoA, s.IdMedioA2);
        await InsertarOkAsync(s, "cobro", "107", "10", "0", "117", s.IdTurnoA2, s.IdMedioA);
    }

    [Fact]
    public async Task LasFilasSinTurnoPuedenRepetirTipoYMedio()
    {
        var s = await SembrarAsync(nameof(LasFilasSinTurnoPuedenRepetirTipoYMedio));

        await InsertarOkAsync(s, "ajuste", "0", "10", "0", "10", idTurno: null, idMedio: s.IdMedioA);
        await InsertarOkAsync(s, "ajuste", "10", "10", "0", "20", idTurno: null, idMedio: s.IdMedioA);
    }

    [Fact]
    public async Task ElUnicoDelCierreNoCubreUnaFilaDeTurnoSinMedio()
    {
        var s = await SembrarAsync(nameof(ElUnicoDelCierreNoCubreUnaFilaDeTurnoSinMedio));

        // Un NULL en id_medio_pago hace distintas a las filas para el único: quien escriba el cierre
        // tiene que mandar siempre el medio, el índice no lo exige.
        await InsertarOkAsync(s, "cobro", "0", "10", "0", "10", s.IdTurnoA, idMedio: null);
        await InsertarOkAsync(s, "cobro", "10", "10", "0", "20", s.IdTurnoA, idMedio: null);
    }

    // ---- FK compuestas con el tenant -------------------------------------------------------

    [Fact]
    public async Task LaFkDeLaCuentaRechazaUnaCuentaDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(LaFkDeLaCuentaRechazaUnaCuentaDeOtroTenant));

        await using var cruzada = await InsertarAsync(
            s.IdTenantB, s.IdCuentaA, s.IdUsuarioB, "cobro", "0", "10", "0", "10");

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => cruzada.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_movimientos_cuenta_cuenta_tesoreria", excepcion.ConstraintName);
    }

    [Fact]
    public async Task LaFkDelTurnoRechazaUnTurnoDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(LaFkDelTurnoRechazaUnTurnoDeOtroTenant));

        await using var cruzada = await InsertarAsync(
            s.IdTenantB, s.IdCuentaB, s.IdUsuarioB, "cobro", "0", "10", "0", "10", idTurno: s.IdTurnoA);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => cruzada.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_movimientos_cuenta_turno", excepcion.ConstraintName);
    }

    [Fact]
    public async Task LaFkDelMedioDePagoRechazaUnMedioDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(LaFkDelMedioDePagoRechazaUnMedioDeOtroTenant));

        await using var cruzada = await InsertarAsync(
            s.IdTenantB, s.IdCuentaB, s.IdUsuarioB, "cobro", "0", "10", "0", "10", idMedio: s.IdMedioA);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => cruzada.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_movimientos_cuenta_medio_pago", excepcion.ConstraintName);
    }

    [Fact]
    public async Task LaFkDelEmpleadoRechazaUnUsuarioInexistente()
    {
        var s = await SembrarAsync(nameof(LaFkDelEmpleadoRechazaUnUsuarioInexistente));

        await using var comando = await InsertarAsync(
            s.IdTenantA, s.IdCuentaA, 999999, "cobro", "0", "10", "0", "10");

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_movimientos_cuenta_empleado", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaCuentaConMovimientosNoSePuedeBorrarFisicamente()
    {
        var s = await SembrarAsync(nameof(UnaCuentaConMovimientosNoSePuedeBorrarFisicamente));
        await InsertarOkAsync(s, "cobro", "0", "10", "0", "10");

        await using var borrar = await ComandoAsync(
            s.IdTenantA, "DELETE FROM cuentas_tesoreria WHERE id_cuenta_tesoreria = $1", s.IdCuentaA);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => borrar.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_movimientos_cuenta_cuenta_tesoreria", excepcion.ConstraintName);
    }

    // ---- aislamiento por tenant y enum -------------------------------------------------------

    [Fact]
    public async Task SoloElTenantDuenoVeLosMovimientosYOtroNoPuedeInsertarConSuIdTenant()
    {
        var s = await SembrarAsync(nameof(SoloElTenantDuenoVeLosMovimientosYOtroNoPuedeInsertarConSuIdTenant));
        await InsertarOkAsync(s, "cobro", "0", "10", "0", "10");

        await using var deA = await ComandoAsync(
            s.IdTenantA, "SELECT count(*) FROM movimientos_cuenta WHERE id_cuenta_tesoreria = $1", s.IdCuentaA);
        await using var deB = await ComandoAsync(
            s.IdTenantB, "SELECT count(*) FROM movimientos_cuenta WHERE id_cuenta_tesoreria = $1", s.IdCuentaA);
        Assert.Equal(1L, await deA.ExecuteScalarAsync());
        Assert.Equal(0L, await deB.ExecuteScalarAsync());

        // La sesión del tenant A intenta escribir una fila con el id_tenant del B.
        await using var intruso = await ComandoAsync(
            s.IdTenantA,
            "INSERT INTO movimientos_cuenta (id_tenant, id_cuenta_tesoreria, fecha, tipo, concepto, inicio, ingreso, egreso, final, id_empleado) " +
            "VALUES ($1, $2, now(), 'cobro', 'x', 0, 10, 0, 10, $3)",
            s.IdTenantB, s.IdCuentaB, s.IdUsuarioB);
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => intruso.ExecuteNonQueryAsync());
        Assert.Equal("42501", excepcion.SqlState);
    }

    [Fact]
    public async Task ElFiltroDeEfNoDevuelveLosMovimientosDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(ElFiltroDeEfNoDevuelveLosMovimientosDeOtroTenant));
        await InsertarOkAsync(s, "cobro", "0", "10", "0", "10");

        await using var sesionA = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, s.IdTenantA));
        await using var sesionB = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, s.IdTenantB));

        var delA = await sesionA.MovimientosCuenta.SingleAsync();
        Assert.Equal(TipoMovimientoCuenta.Cobro, delA.Tipo);
        Assert.Equal(10m, delA.Final);
        Assert.Empty(await sesionB.MovimientosCuenta.ToListAsync());
    }

    [Fact]
    public async Task ElEnumDeclaraLosNueveTipos()
    {
        var s = await SembrarAsync(nameof(ElEnumDeclaraLosNueveTipos));

        await using var comando = await ComandoAsync(
            s.IdTenantA, "SELECT string_agg(e::text, ',' ORDER BY e) FROM unnest(enum_range(NULL::tipo_movimiento_cuenta)) e");

        Assert.Equal(
            "ajuste,cobro,comision,deposito,retencion_ganancias,retencion_iibb,retencion_iva,retiro,transferencia",
            await comando.ExecuteScalarAsync());
    }
}
