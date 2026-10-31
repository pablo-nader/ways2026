using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Application.Usuarios;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// La migración <c>RenombrarPagosComprobanteACobros</c> sobre datos reales: solo renombra, así que el
/// cobro y el movimiento de cuenta corriente que lo referencia tienen que reaparecer con el mismo
/// id y el mismo contenido bajo los nombres nuevos, ningún objeto de <c>cobros</c> puede conservar el
/// nombre viejo, y la CHECK de vuelto negativo sigue rechazando la fila con el nombre nuevo (el que
/// <c>ManejadorDeErrores</c> traduce a <c>vuelto_de_pago_negativo</c>). La reversión devuelve cada
/// cosa a su nombre anterior.
///
/// Corre sobre la base compartida del fixture: baja una migración, verifica, y la vuelve a subir en un
/// <c>finally</c> para que el resto de la suite siempre encuentre el esquema en HEAD. Cada migración
/// corre en su propia transacción, así que una falla a mitad de camino no deja el esquema partido.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class RenombrarPagosComprobanteACobrosMigracionTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20261029012800_JornadasDePersonal";
    private const string PasswordRoot = "root";
    private const string MailRoot = "test@test.com";

    [Fact]
    public async Task ElCobroYSuMovimientoSobrevivenAlRenombreYLaCheckSigueRespondiendoConElNombreNuevo()
    {
        var s = await SembrarAsync(nameof(ElCobroYSuMovimientoSobrevivenAlRenombreYLaCheckSigueRespondiendoConElNombreNuevo));

        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();

        // Estado de partida (HEAD): ya con los nombres nuevos.
        Assert.Equal(("cobros", 100.00m, 0m), await LeerCobroAsync(owner, "cobros", "id_cobro", s.IdCobro));

        try
        {
            // Reversión: cada cosa vuelve a su nombre anterior y los datos viajan intactos.
            await MigrarAsync(MigracionAnterior);

            Assert.Equal(("pagos_comprobante", 100.00m, 0m), await LeerCobroAsync(owner, "pagos_comprobante", "id_pago", s.IdCobro));
            Assert.Equal(
                s.IdCobro,
                await EscalarAsync<int>(
                    owner, "SELECT id_pago_comprobante FROM movimientos_cuenta_corriente WHERE id_movimiento = $1", s.IdMovimiento));
            Assert.Equal(
                1L,
                await EscalarAsync<long>(
                    owner, "SELECT count(*) FROM pg_policies WHERE policyname = 'pagos_comprobante_tenant' AND tablename = 'pagos_comprobante'"));
            Assert.Equal(
                3L,
                await EscalarAsync<long>(
                    owner, "SELECT count(*) FROM pg_constraint WHERE conname IN ('pk_pagos_comprobante', 'ck_pagos_comprobante_vuelto_no_negativo', 'fk_movimientos_cuenta_corriente_pago_comprobante')"));
        }
        finally
        {
            await MigrarAsync(null);
        }

        // Avance: el mismo cobro, el mismo movimiento, nombres nuevos.
        Assert.Null(await EscalarAsync<string?>(owner, "SELECT to_regclass('public.pagos_comprobante')::text"));
        Assert.Equal(("cobros", 100.00m, 0m), await LeerCobroAsync(owner, "cobros", "id_cobro", s.IdCobro));
        Assert.Equal(
            s.IdCobro,
            await EscalarAsync<int>(
                owner,
                "SELECT m.id_cobro FROM movimientos_cuenta_corriente m JOIN cobros c ON c.id_cobro = m.id_cobro AND c.id_tenant = m.id_tenant " +
                "WHERE m.id_movimiento = $1",
                s.IdMovimiento));

        // Lo único con "pagos_comprobante" en el nombre que puede quedar es lo de la tabla `pagos` (compras).
        foreach (var (consulta, descripcion) in new[]
        {
            ("SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
             "WHERE n.nspname = 'public' AND c.relname ILIKE '%pagos\\_comprobante%' AND c.relname <> 'ix_pagos_comprobante_compra'",
             "tablas, índices y secuencias"),
            ("SELECT count(*) FROM pg_constraint WHERE conname ILIKE '%pagos\\_comprobante%' AND conname <> 'fk_pagos_comprobante_compra'",
             "restricciones"),
            ("SELECT count(*) FROM pg_policies WHERE policyname ILIKE '%pagos\\_comprobante%'", "policies de RLS"),
            ("SELECT count(*) FROM information_schema.columns " +
             "WHERE table_schema = 'public' AND column_name ILIKE '%pago\\_comprobante%'", "columnas"),
        })
        {
            Assert.True(0L == await EscalarAsync<long>(owner, consulta), $"Quedaron {descripcion} con el nombre viejo.");
        }

        Assert.Equal(
            1L,
            await EscalarAsync<long>(owner, "SELECT count(*) FROM pg_policies WHERE policyname = 'cobros_tenant' AND tablename = 'cobros'"));
        Assert.Equal(
            1L,
            await EscalarAsync<long>(
                owner, "SELECT count(*) FROM pg_class WHERE relname = 'cobros_id_cobro_seq' AND relkind = 'S'"));

        // La CHECK de vuelto negativo sigue vigente y ahora se llama como lo espera el traductor de errores.
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => EjecutarAsync(
            owner,
            "INSERT INTO cobros (id_tenant, id_comprobante_venta, id_medio_pago, importe, vuelto, created_at, updated_at) " +
            "VALUES ($1, $2, $3, 100, -5, now(), now())",
            s.IdTenant, s.IdComprobanteVenta, s.IdMedioPago));
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_cobros_vuelto_no_negativo", excepcion.ConstraintName);
    }

    private sealed record Sembrado(
        int IdTenant, int IdComprobanteVenta, int IdMedioPago, int IdCobro, int IdMovimiento);

    private async Task<Sembrado> SembrarAsync(string nombre)
    {
        var root = fixture.CreateClient();
        var loginRoot = await root.PostAsJsonAsync("/api/auth/login", new SolicitudDeLogin(MailRoot, PasswordRoot));
        Assert.Equal(HttpStatusCode.OK, loginRoot.StatusCode);

        var solicitud = new SolicitudDeAprovisionamiento(
            nombre, $"{nombre} SA", "Local 1", $"{nombre.ToLowerInvariant()}@ways.test", ModoPuntoVenta.Web);
        var respuesta = await root.PostAsJsonAsync("/api/plataforma/tenants", solicitud);
        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        var tenant = (await respuesta.Content.ReadFromJsonAsync<ResultadoAprovisionamiento>())!;

        var ahora = DateTimeOffset.UtcNow;
        await using var dbPlataforma = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var idTipoTx = await dbPlataforma.TiposComprobante.Where(t => t.Codigo == "TX").Select(t => t.Id).SingleAsync();

        await using var db = fixture.CrearContextoDeAplicacion(new TenantActualFijo(ModoDeAcceso.Tenant, tenant.IdTenant));
        var idCliente = await db.Clientes.Select(c => c.Id).FirstAsync();
        var idMedioPago = await db.MediosPago
            .Where(m => m.Comportamiento == ComportamientoMedioPago.Efectivo).Select(m => m.Id).FirstAsync();

        var comprobante = new ComprobanteVenta
        {
            IdTenant = tenant.IdTenant,
            IdTipoComprobante = idTipoTx,
            Numero = 1,
            Fecha = ahora,
            IdPuntoVenta = tenant.IdPuntoVenta,
            IdEmpleado = tenant.IdUsuarioAdmin,
            IdCliente = idCliente,
            Subtotal = 100m,
            DescuentoTotal = 0m,
            Total = 100m,
            Estado = EstadoComprobante.Emitido,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ComprobantesVenta.Add(comprobante);
        await db.SaveChangesAsync();

        var cobro = new Cobro
        {
            IdTenant = tenant.IdTenant,
            IdComprobanteVenta = comprobante.Id,
            IdMedioPago = idMedioPago,
            Importe = 100m,
            Vuelto = 0m,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Cobros.Add(cobro);
        await db.SaveChangesAsync();

        // El movimiento que referencia al cobro (la FK compuesta que la migración renombra).
        await using var owner = new NpgsqlConnection(fixture.OwnerConnectionString);
        await owner.OpenAsync();
        var idMovimiento = await EscalarAsync<int>(
            owner,
            "INSERT INTO movimientos_cuenta_corriente (id_tenant, id_cliente, fecha, id_punto_venta, id_empleado, tipo, " +
            "id_comprobante_venta, id_cobro, importe, saldo_resultante) " +
            "VALUES ($1, $2, now(), $3, $4, 'consumo', $5, $6, 100, 100) RETURNING id_movimiento",
            tenant.IdTenant, idCliente, tenant.IdPuntoVenta, tenant.IdUsuarioAdmin, comprobante.Id, cobro.Id);

        return new Sembrado(tenant.IdTenant, comprobante.Id, idMedioPago, cobro.Id, idMovimiento);
    }

    /// <summary>Migra la base del fixture hasta <paramref name="destino"/> (o hasta HEAD si es <c>null</c>).</summary>
    private async Task MigrarAsync(string? destino)
    {
        await using var contexto = fixture.CrearContextoDeOwner(TenantActualFijo.Plataforma);
        var migrador = contexto.Database.GetInfrastructure().GetRequiredService<IMigrator>();
        if (destino is null)
        {
            await migrador.MigrateAsync();
        }
        else
        {
            await migrador.MigrateAsync(destino);
        }
    }

    private static async Task<(string Tabla, decimal Importe, decimal Vuelto)> LeerCobroAsync(
        NpgsqlConnection conexion, string tabla, string columnaId, int id)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"SELECT importe, vuelto FROM {tabla} WHERE {columnaId} = $1";
        comando.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var lector = await comando.ExecuteReaderAsync();
        Assert.True(await lector.ReadAsync());
        return (tabla, lector.GetDecimal(0), lector.GetDecimal(1));
    }

    private static async Task<T> EscalarAsync<T>(NpgsqlConnection conexion, string sql, params object?[] parametros)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = valor ?? DBNull.Value });
        }

        var resultado = await comando.ExecuteScalarAsync();
        return resultado is null or DBNull ? default! : (T)resultado;
    }

    private static async Task EjecutarAsync(NpgsqlConnection conexion, string sql, params object?[] parametros)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in parametros)
        {
            comando.Parameters.Add(new NpgsqlParameter { Value = valor });
        }

        await comando.ExecuteNonQueryAsync();
    }
}
