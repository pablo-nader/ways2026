using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Plantel;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// Esquema del pago de personal con SQL crudo sobre la conexión <c>ways_app</c> (NOBYPASSRLS): el valor nuevo
/// del enum de cuenta corriente, cada cláusula de los CHECK del descuento (una fila que viola solo esa
/// cláusula), el vínculo de pagos y movimientos con el asiento del personal (FK compuesta, índice único de un
/// descuento por pago). Cada violación se aserta por SQLSTATE y por nombre de constraint. Postgres evalúa los CHECK
/// antes de las claves foráneas, así que una fila con ids inexistentes llega al CHECK que la viola.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PagoDePersonalSchemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private sealed record Siembra(
        int IdTenant, int IdEmpresa, int IdPuntoVenta, int IdUsuario, int IdCliente, int IdMedioPago, int IdPersonal,
        int IdMovimientoPersonal);

    private async Task<Siembra> SembrarAsync(string nombre)
    {
        using var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin("test@test.com", "root"));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var mailAdmin = $"{nombre.ToLowerInvariant()}@ways.test";
        var respuesta = await root.PostAsJsonAsync(
            "/api/plataforma/tenants",
            new SolicitudDeAprovisionamiento(nombre, $"{nombre} SA", "Local 1", mailAdmin, ModoPuntoVenta.Web));
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var resultado = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var admin = fixture.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(mailAdmin, resultado.PasswordTemporal));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var alta = await admin.PostAsJsonAsync(
            "/api/personal", new DatosDePersonal(nombre, null, null, null, 100m, null, null, null));
        var idPersonal = (await alta.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        int idCliente;
        int idMedioPago;
        await using (var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, resultado.IdTenant)))
        {
            var ahora = DateTimeOffset.UtcNow;
            var cliente = new Cliente
            {
                IdTenant = resultado.IdTenant, Numero = 1000 + Random.Shared.Next(1, 100_000), Nombre = nombre,
                IdCondicionFiscal = await db.CondicionesFiscales.Select(c => c.Id).FirstAsync(),
                IdListaPrecio = await db.Clientes.Select(c => c.IdListaPrecio).FirstAsync(),
                LimiteCredito = 0m, CreditoIlimitado = true, Activo = true, CreatedAt = ahora, UpdatedAt = ahora
            };
            db.Clientes.Add(cliente);
            await db.SaveChangesAsync();
            idCliente = cliente.Id;
            idMedioPago = await db.MediosPago.Select(m => m.Id).FirstAsync();
        }

        int idUsuario;
        await using (var conexion = new NpgsqlConnection(fixture.OwnerConnectionString))
        {
            await conexion.OpenAsync();
            await using var consulta = new NpgsqlCommand(
                "SELECT id_usuario FROM usuarios WHERE id_tenant = $1 ORDER BY id_usuario LIMIT 1", conexion);
            consulta.Parameters.AddWithValue(resultado.IdTenant);
            idUsuario = (int)(await consulta.ExecuteScalarAsync())!;
        }

        var siembra = new Siembra(
            resultado.IdTenant, resultado.IdEmpresa, resultado.IdPuntoVenta, idUsuario, idCliente, idMedioPago, idPersonal, 0);
        var idMovimiento = await InsertarEscalarAsync(
            siembra.IdTenant,
            "INSERT INTO movimientos_personal (id_tenant, id_personal, id_empleado, fecha, tipo, importe, saldo_resultante, created_at) " +
            "VALUES ($1, $2, $3, DATE '2026-10-01', 'pago', -10, -10, now()) RETURNING id_movimiento",
            siembra.IdTenant, idPersonal, idUsuario);

        return siembra with { IdMovimientoPersonal = idMovimiento };
    }

    private async Task<int> InsertarEscalarAsync(int idTenant, string sql, params object[] parametros)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = new NpgsqlCommand(sql, conexion);
        foreach (var valor in parametros)
        {
            comando.Parameters.AddWithValue(valor);
        }

        return (int)(await comando.ExecuteScalarAsync())!;
    }

    private async Task<int> EjecutarAsync(int idTenant, string sql, params object[] parametros)
    {
        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", idTenant);
        await using var comando = new NpgsqlCommand(sql, conexion);
        foreach (var valor in parametros)
        {
            comando.Parameters.AddWithValue(valor);
        }

        return await comando.ExecuteNonQueryAsync();
    }

    private Task<int> InsertarMovimientoCcAsync(
        Siembra s, string tipo, string importe, string idMovimientoPersonal, string idComprobanteVenta = "NULL",
        string idPagoComprobante = "NULL", int? idTenant = null) =>
        EjecutarAsync(
            idTenant ?? s.IdTenant,
            "INSERT INTO movimientos_cuenta_corriente " +
            "(id_tenant, id_cliente, fecha, id_punto_venta, id_empleado, tipo, id_comprobante_venta, id_pago_comprobante, " +
            " importe, saldo_resultante, id_movimiento_personal) " +
            $"VALUES ($1, $2, now(), $3, $4, '{tipo}', {idComprobanteVenta}, {idPagoComprobante}, {importe}, 0, {idMovimientoPersonal})",
            idTenant ?? s.IdTenant, s.IdCliente, s.IdPuntoVenta, s.IdUsuario);

    private Task<int> InsertarPagoAsync(Siembra s, string categoria, string idMovimientoPersonal, int? idTenant = null) =>
        EjecutarAsync(
            idTenant ?? s.IdTenant,
            "INSERT INTO pagos " +
            "(id_tenant, fecha, id_empresa, id_empleado, categoria, origen_fondos, concepto, id_medio_pago, importe, " +
            " id_movimiento_personal, created_at, updated_at) " +
            $"VALUES ($1, now(), $2, $3, '{categoria}', 'tesoreria', 'Pago de prueba', $4, 10, {idMovimientoPersonal}, now(), now())",
            idTenant ?? s.IdTenant, s.IdEmpresa, s.IdUsuario, s.IdMedioPago);

    private static async Task<PostgresException> ViolacionAsync(Task<int> escritura) =>
        await Assert.ThrowsAsync<PostgresException>(() => escritura);

    [Fact]
    public async Task ElEnumDeCuentaCorrienteDeclaraElDescuentoDePersonal()
    {
        var s = await SembrarAsync(nameof(ElEnumDeCuentaCorrienteDeclaraElDescuentoDePersonal));

        await using var conexion = await fixture.AbrirConexionCrudaAsync("tenant", s.IdTenant);
        await using var comando = new NpgsqlCommand(
            "SELECT string_agg(e::text, ',' ORDER BY e::text) FROM unnest(enum_range(NULL::tipo_movimiento_cc)) e", conexion);

        Assert.Equal("actualizacion_precios,ajuste,consumo,descuento_personal,pago", await comando.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UnDescuentoConLaFormaValidaSeInserta()
    {
        var s = await SembrarAsync(nameof(UnDescuentoConLaFormaValidaSeInserta));

        Assert.Equal(1, await InsertarMovimientoCcAsync(s, "descuento_personal", "-50", s.IdMovimientoPersonal.ToString()));
    }

    [Theory]
    [InlineData("descuento_personal", "-50", "NULL", "NULL", "NULL", "ck_movimientos_cuenta_corriente_descuento_personal")]
    [InlineData("descuento_personal", "0", "ID", "NULL", "NULL", "ck_movimientos_cuenta_corriente_descuento_personal")]
    [InlineData("descuento_personal", "50", "ID", "NULL", "NULL", "ck_movimientos_cuenta_corriente_descuento_personal")]
    [InlineData("descuento_personal", "-50", "ID", "999999", "NULL", "ck_movimientos_cuenta_corriente_descuento_personal")]
    [InlineData("descuento_personal", "-50", "ID", "NULL", "999999", "ck_movimientos_cuenta_corriente_descuento_personal")]
    [InlineData("ajuste", "-50", "ID", "NULL", "NULL", "ck_movimientos_cuenta_corriente_vinculo_personal")]
    [InlineData("pago", "-50", "ID", "NULL", "NULL", "ck_movimientos_cuenta_corriente_vinculo_personal")]
    public async Task UnaFormaInvalidaViolaSuCheck(
        string tipo, string importe, string movimientoPersonal, string comprobanteVenta, string pagoComprobante, string constraint)
    {
        var s = await SembrarAsync(nameof(UnaFormaInvalidaViolaSuCheck) + tipo + importe + movimientoPersonal + comprobanteVenta + pagoComprobante);
        var vinculo = movimientoPersonal == "ID" ? s.IdMovimientoPersonal.ToString() : movimientoPersonal;

        var excepcion = await ViolacionAsync(InsertarMovimientoCcAsync(s, tipo, importe, vinculo, comprobanteVenta, pagoComprobante));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(constraint, excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnPagoDePersonalAdmiteComoMuchoUnDescuento()
    {
        var s = await SembrarAsync(nameof(UnPagoDePersonalAdmiteComoMuchoUnDescuento));
        await InsertarMovimientoCcAsync(s, "descuento_personal", "-50", s.IdMovimientoPersonal.ToString());

        var excepcion = await ViolacionAsync(
            InsertarMovimientoCcAsync(s, "descuento_personal", "-20", s.IdMovimientoPersonal.ToString()));

        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_movimientos_cuenta_corriente_movimiento_personal", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnDescuentoSobreUnAsientoInexistenteViolaLaClaveForanea()
    {
        var s = await SembrarAsync(nameof(UnDescuentoSobreUnAsientoInexistenteViolaLaClaveForanea));

        var excepcion = await ViolacionAsync(InsertarMovimientoCcAsync(s, "descuento_personal", "-50", "999999"));

        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_movimientos_cuenta_corriente_movimiento_personal", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnPagoDeSueldosConAsientoSeInsertaYUnoDeOtraCategoriaViolaElCheck()
    {
        var s = await SembrarAsync(nameof(UnPagoDeSueldosConAsientoSeInsertaYUnoDeOtraCategoriaViolaElCheck));

        Assert.Equal(1, await InsertarPagoAsync(s, "sueldos", s.IdMovimientoPersonal.ToString()));
        Assert.Equal(1, await InsertarPagoAsync(s, "otros", "NULL"));

        var excepcion = await ViolacionAsync(InsertarPagoAsync(s, "otros", s.IdMovimientoPersonal.ToString()));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_pagos_movimiento_personal_es_sueldo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnPagoSobreUnAsientoInexistenteViolaLaClaveForanea()
    {
        var s = await SembrarAsync(nameof(UnPagoSobreUnAsientoInexistenteViolaLaClaveForanea));

        var excepcion = await ViolacionAsync(InsertarPagoAsync(s, "sueldos", "999999"));

        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_pagos_movimiento_personal", excepcion.ConstraintName);
    }

    /// <summary>Sesión y <c>id_tenant</c> del tenant B apuntando al asiento del tenant A: pasa la política de
    /// filas (la comprobación de integridad referencial no está sujeta a RLS) y solo la FK compuesta lo frena.</summary>
    [Fact]
    public async Task LasClavesForaneasCompuestasRechazanUnAsientoDeOtroTenant()
    {
        var a = await SembrarAsync(nameof(LasClavesForaneasCompuestasRechazanUnAsientoDeOtroTenant) + "A");
        var b = await SembrarAsync(nameof(LasClavesForaneasCompuestasRechazanUnAsientoDeOtroTenant) + "B");

        var delPago = await ViolacionAsync(InsertarPagoAsync(b, "sueldos", a.IdMovimientoPersonal.ToString()));
        var delDescuento = await ViolacionAsync(
            InsertarMovimientoCcAsync(b, "descuento_personal", "-50", a.IdMovimientoPersonal.ToString()));

        Assert.Equal(("23503", "fk_pagos_movimiento_personal"), (delPago.SqlState, delPago.ConstraintName));
        Assert.Equal(
            ("23503", "fk_movimientos_cuenta_corriente_movimiento_personal"),
            (delDescuento.SqlState, delDescuento.ConstraintName));
    }
}
