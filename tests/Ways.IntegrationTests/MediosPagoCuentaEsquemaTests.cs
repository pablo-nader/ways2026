using Npgsql;

namespace Ways.IntegrationTests;

/// <summary>
/// Columnas de cuenta de tesorería, comisión y retenciones de <c>medios_pago</c> con SQL crudo sobre
/// la conexión <c>ways_app</c> (NOBYPASSRLS): los valores por defecto de una fila que no las nombra,
/// una fila que viola solo una cláusula por cada cláusula de las tres CHECK, la FK compuesta a la
/// cuenta y el rechazo al pasar un medio con cuenta o porcentajes a un comportamiento que no es
/// electrónico. Cada rechazo se aserta por SQLSTATE y por nombre de constraint.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class MediosPagoCuentaEsquemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private Task<SiembraDeCuentas> SembrarAsync(string nombre) => SiembraDeCuentasHelper.SembrarAsync(fixture, nombre);

    private Task<ComandoDeSiembra> ComandoAsync(int idTenant, string sql, params object[] parametros) =>
        SiembraDeCuentasHelper.ComandoAsync(fixture, idTenant, sql, parametros);

    private Task<ComandoDeSiembra> InsertarMedioAsync(
        int idTenant, string comportamiento, string comision = "0", string iibb = "0", string iva = "0",
        string ganancias = "0", int? idCuenta = null) =>
        ComandoAsync(
            idTenant,
            "INSERT INTO medios_pago (id_tenant, nombre, orden, comportamiento, admite_vuelto, requiere_referencia, " +
            "comision_porcentaje, retencion_iibb_porcentaje, retencion_iva_porcentaje, retencion_ganancias_porcentaje, " +
            "id_cuenta_tesoreria, created_at, updated_at) " +
            $"VALUES ($1, $2, 1, '{comportamiento}', false, false, {comision}, {iibb}, {iva}, {ganancias}, " +
            $"{(idCuenta is { } c ? c.ToString() : "NULL")}, now(), now())",
            idTenant, Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UnaFilaQueNoNombraLasColumnasNuevasQuedaConCeroYSinCuenta()
    {
        var s = await SembrarAsync(nameof(UnaFilaQueNoNombraLasColumnasNuevasQuedaConCeroYSinCuenta));

        await using var insertar = await ComandoAsync(
            s.IdTenantA,
            "INSERT INTO medios_pago (id_tenant, nombre, orden, comportamiento, admite_vuelto, requiere_referencia, " +
            "created_at, updated_at) VALUES ($1, 'viejo', 1, 'efectivo', true, false, now(), now()) RETURNING id_medio_pago",
            s.IdTenantA);
        var id = (int)(await insertar.ExecuteScalarAsync())!;

        await using var leer = await ComandoAsync(
            s.IdTenantA,
            "SELECT comision_porcentaje::text || '|' || retencion_iibb_porcentaje::text || '|' || " +
            "retencion_iva_porcentaje::text || '|' || retencion_ganancias_porcentaje::text || '|' || " +
            "coalesce(id_cuenta_tesoreria::text, 'null') FROM medios_pago WHERE id_medio_pago = $1",
            id);
        Assert.Equal("0.000|0.000|0.000|0.000|null", await leer.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("electronico", "3.5", "1", "0.5", "2", true)]
    [InlineData("electronico", "100", "0", "0", "0", true)]
    [InlineData("electronico", "40", "30", "20", "10", true)]
    [InlineData("electronico", "0", "0", "0", "0", true)]
    [InlineData("electronico", "0", "0", "0", "0", false)]
    [InlineData("efectivo", "0", "0", "0", "0", false)]
    [InlineData("cuenta_corriente", "0", "0", "0", "0", false)]
    public async Task LasFormasValidasSeInsertan(
        string comportamiento, string comision, string iibb, string iva, string ganancias, bool conCuenta)
    {
        var s = await SembrarAsync(nameof(LasFormasValidasSeInsertan) + comportamiento + comision + iibb + iva + ganancias + conCuenta);

        await using var insertar = await InsertarMedioAsync(
            s.IdTenantA, comportamiento, comision, iibb, iva, ganancias, conCuenta ? s.IdCuentaA : null);

        Assert.Equal(1, await insertar.ExecuteNonQueryAsync());
    }

    [Theory]
    // Fuera de rango: cada porcentaje por separado, por debajo de 0 y por encima de 100.
    [InlineData("-0.001", "0", "0", "0", "ck_medios_pago_porcentajes_cuenta_rango")]
    [InlineData("0", "-1", "0", "0", "ck_medios_pago_porcentajes_cuenta_rango")]
    [InlineData("0", "0", "-0.001", "0", "ck_medios_pago_porcentajes_cuenta_rango")]
    [InlineData("0", "0", "0", "-5", "ck_medios_pago_porcentajes_cuenta_rango")]
    [InlineData("100.001", "0", "0", "0", "ck_medios_pago_porcentajes_cuenta_rango")]
    [InlineData("0", "101", "0", "0", "ck_medios_pago_porcentajes_cuenta_rango")]
    [InlineData("0", "0", "150", "0", "ck_medios_pago_porcentajes_cuenta_rango")]
    [InlineData("0", "0", "0", "100.5", "ck_medios_pago_porcentajes_cuenta_rango")]
    // Cada uno en rango pero la suma pasa de 100.
    [InlineData("60", "41", "0", "0", "ck_medios_pago_porcentajes_cuenta_suma")]
    [InlineData("25", "25", "25", "25.001", "ck_medios_pago_porcentajes_cuenta_suma")]
    [InlineData("0", "0", "50.5", "50", "ck_medios_pago_porcentajes_cuenta_suma")]
    public async Task UnPorcentajeFueraDeRangoOUnaSumaMayorACienViolaSuCheck(
        string comision, string iibb, string iva, string ganancias, string constraint)
    {
        var s = await SembrarAsync(nameof(UnPorcentajeFueraDeRangoOUnaSumaMayorACienViolaSuCheck) + comision + iibb + iva + ganancias);

        await using var insertar = await InsertarMedioAsync(s.IdTenantA, "electronico", comision, iibb, iva, ganancias);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => insertar.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(constraint, excepcion.ConstraintName);
    }

    [Theory]
    [InlineData("efectivo", "1", "0", "0", "0", false)]
    [InlineData("efectivo", "0", "1", "0", "0", false)]
    [InlineData("cuenta_corriente", "0", "0", "1", "0", false)]
    [InlineData("cuenta_corriente", "0", "0", "0", "1", false)]
    [InlineData("efectivo", "0", "0", "0", "0", true)]
    [InlineData("cuenta_corriente", "0", "0", "0", "0", true)]
    [InlineData("efectivo", "5", "1", "1", "1", true)]
    public async Task UnMedioNoElectronicoNoAdmiteCuentaNiPorcentajes(
        string comportamiento, string comision, string iibb, string iva, string ganancias, bool conCuenta)
    {
        var s = await SembrarAsync(nameof(UnMedioNoElectronicoNoAdmiteCuentaNiPorcentajes) + comportamiento + comision + iibb + iva + ganancias + conCuenta);

        await using var insertar = await InsertarMedioAsync(
            s.IdTenantA, comportamiento, comision, iibb, iva, ganancias, conCuenta ? s.IdCuentaA : null);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => insertar.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_medios_pago_cuenta_solo_electronico", excepcion.ConstraintName);
    }

    [Fact]
    public async Task CambiarElComportamientoDeUnMedioConCuentaAUnoNoElectronicoEsRechazado()
    {
        var s = await SembrarAsync(nameof(CambiarElComportamientoDeUnMedioConCuentaAUnoNoElectronicoEsRechazado));
        await using (var alta = await InsertarMedioAsync(s.IdTenantA, "electronico", comision: "3", idCuenta: s.IdCuentaA))
        {
            Assert.Equal(1, await alta.ExecuteNonQueryAsync());
        }

        await using var cambiar = await ComandoAsync(
            s.IdTenantA,
            "UPDATE medios_pago SET comportamiento = 'efectivo' WHERE id_cuenta_tesoreria = $1", s.IdCuentaA);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => cambiar.ExecuteNonQueryAsync());
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_medios_pago_cuenta_solo_electronico", excepcion.ConstraintName);
    }

    [Fact]
    public async Task NingunIndiceNuevoDeMediosPagoTieneNombreImplicitoDeEf()
    {
        var s = await SembrarAsync(nameof(NingunIndiceNuevoDeMediosPagoTieneNombreImplicitoDeEf));

        await using var comando = await ComandoAsync(
            s.IdTenantA,
            "SELECT count(*) FROM pg_indexes WHERE tablename = 'medios_pago' AND indexname LIKE 'IX_%'");

        Assert.Equal(0L, await comando.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LaFkCompuestaRechazaUnaCuentaDeOtroTenant()
    {
        var s = await SembrarAsync(nameof(LaFkCompuestaRechazaUnaCuentaDeOtroTenant));

        await using var cruzada = await InsertarMedioAsync(s.IdTenantB, "electronico", idCuenta: s.IdCuentaA);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => cruzada.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_medios_pago_cuenta_tesoreria", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaCuentaReferenciadaPorUnMedioNoSePuedeBorrarFisicamente()
    {
        var s = await SembrarAsync(nameof(UnaCuentaReferenciadaPorUnMedioNoSePuedeBorrarFisicamente));
        await using (var alta = await InsertarMedioAsync(s.IdTenantA, "electronico", idCuenta: s.IdCuentaA))
        {
            Assert.Equal(1, await alta.ExecuteNonQueryAsync());
        }

        await using var borrar = await ComandoAsync(
            s.IdTenantA, "DELETE FROM cuentas_tesoreria WHERE id_cuenta_tesoreria = $1", s.IdCuentaA);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => borrar.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_medios_pago_cuenta_tesoreria", excepcion.ConstraintName);
    }
}
