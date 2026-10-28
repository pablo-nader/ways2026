using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;

namespace Ways.IntegrationTests;

/// <summary>
/// E3-S1a (aplicaciones de pagos a compras), <c>db-error-backstops</c>: el esquema de <c>aplicaciones_pago</c> probado con
/// SQL crudo, sin pasar por ningún servicio (todavía no hay escritor). Cubre la CHECK, las FKs compuestas, la unicidad
/// parcial y los constraint triggers diferidos contra el pago y contra la compra. El backfill de la migración se prueba en
/// <see cref="AplicacionesDePagoMigracionTests"/>.
///
/// Honesto sobre alcanzabilidad: ninguna de estas ramas es alcanzable por un cliente HTTP hoy; prueban el backstop de
/// esquema que S1b va a empezar a ejercer.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class AplicacionesPagoEsquemaTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string TriggerPago = "tg_aplicaciones_pago_no_excede_pago";
    private const string TriggerCompra = "tg_aplicaciones_pago_no_excede_compra";

    private sealed record Prerequisitos(
        int IdTenant, int IdEmpresa, int IdEmpresaOtra, int IdProveedor, int IdProveedorOtro, int IdPuntoVenta,
        int IdEmpleado, int IdTipoCompra);

    private async Task<Prerequisitos> SembrarPrerequisitosAsync(string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host (siembra roles, alícuotas, tipos de comprobante)

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var empresa = new Empresa { IdTenant = tenant.Id, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        var empresaOtra = new Empresa { IdTenant = tenant.Id, RazonSocial = nombre + "-otra", CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.AddRange(empresa, empresaOtra);
        await db.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var proveedor = new Proveedor
        {
            IdTenant = tenant.Id, RazonSocial = nombre, IdCondicionFiscal = condicionFiscal.Id, CreatedAt = ahora, UpdatedAt = ahora
        };
        var proveedorOtro = new Proveedor
        {
            IdTenant = tenant.Id, RazonSocial = nombre + "-otro", IdCondicionFiscal = condicionFiscal.Id,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.Proveedores.AddRange(proveedor, proveedorOtro);
        await db.SaveChangesAsync();

        var usuario = new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = "vendedor",
            Mail = $"{nombre.ToLowerInvariant()}@ways.test",
            RolId = (int)RolConocido.Vendedor,
            PasswordHash = "hash-de-prueba",
            PasswordAlgoritmo = "test",
            PasswordActualizadoEl = ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();

        var idTipoCompra = await db.TiposComprobante.Where(t => t.Codigo == "C-FA").Select(t => t.Id).SingleAsync();

        return new Prerequisitos(
            tenant.Id, empresa.Id, empresaOtra.Id, proveedor.Id, proveedorOtro.Id, puntoVenta.Id, usuario.Id, idTipoCompra);
    }

    private static NpgsqlParameter P(object? valor) => new() { Value = valor ?? DBNull.Value };

    private static async Task<int> EjecutarAsync(NpgsqlConnection conexion, NpgsqlTransaction? tx, string sql, params object?[] valores)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = tx;
        comando.CommandText = sql;
        foreach (var valor in valores)
        {
            comando.Parameters.Add(P(valor));
        }

        return await comando.ExecuteNonQueryAsync();
    }

    private static async Task<T> EscalarAsync<T>(NpgsqlConnection conexion, string sql, params object?[] valores)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        foreach (var valor in valores)
        {
            comando.Parameters.Add(P(valor));
        }

        return (T)(await comando.ExecuteScalarAsync())!;
    }

    private static Task<int> InsertarCompraAsync(
        NpgsqlConnection conexion, Prerequisitos p, decimal total, string estado = "confirmada", int? idProveedor = null) =>
        EscalarAsync<int>(
            conexion,
            "INSERT INTO comprobantes_compra (id_tenant, id_proveedor, id_tipo_comprobante, numero_externo, " +
            "fecha_comprobante, fecha_recepcion, id_punto_venta, id_empleado, subtotal, descuento_total, total, estado, " +
            "discrimina_iva, created_at, updated_at) " +
            "VALUES ($1, $2, $3, $4::citext, $5, $6, $7, $8, $9, 0, $9, $10::estado_compra, false, now(), now()) " +
            "RETURNING id_comprobante_compra",
            p.IdTenant, idProveedor ?? p.IdProveedor, p.IdTipoCompra, Guid.NewGuid().ToString("N")[..16],
            new DateOnly(2026, 10, 1), DateTimeOffset.UtcNow, p.IdPuntoVenta, p.IdEmpleado, total, estado);

    private static Task<int> InsertarPagoAsync(
        NpgsqlConnection conexion, Prerequisitos p, decimal importe, string categoria = "proveedor", int? idProveedor = null,
        int? idEmpresa = null) =>
        EscalarAsync<int>(
            conexion,
            "INSERT INTO pagos (id_tenant, fecha, id_empresa, id_punto_venta, id_turno_caja, id_empleado, categoria, " +
            "origen_fondos, concepto, id_proveedor, importe, created_at, updated_at) " +
            "VALUES ($1, now(), $2, NULL, NULL, $3, $4::categoria_pago, 'tesoreria', 'pago de prueba', $5, $6, now(), now()) " +
            "RETURNING id_pago",
            p.IdTenant, idEmpresa ?? p.IdEmpresa, p.IdEmpleado, categoria, idProveedor ?? p.IdProveedor, importe);

    private static Task<int> InsertarAplicacionAsync(
        NpgsqlConnection conexion, NpgsqlTransaction? tx, Prerequisitos p, int idPago, int idCompra, decimal importe,
        DateTimeOffset? baja = null) =>
        EjecutarAsync(
            conexion, tx,
            "INSERT INTO aplicaciones_pago (id_tenant, id_pago, id_comprobante_compra, importe, origen, id_empleado, fecha, " +
            "created_at, updated_at, deleted_at) VALUES ($1, $2, $3, $4, 'aplicacion', $5, now(), now(), now(), $6)",
            p.IdTenant, idPago, idCompra, importe, p.IdEmpleado, baja);

    private static async Task<PostgresException> LaTransaccionFallaAlComitearAsync(
        NpgsqlConnection conexion, Func<NpgsqlTransaction, Task> escribir)
    {
        await using var tx = await conexion.BeginTransactionAsync();
        await escribir(tx);
        return await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
    }

    private static async Task LaTransaccionComiteaAsync(NpgsqlConnection conexion, Func<NpgsqlTransaction, Task> escribir)
    {
        await using var tx = await conexion.BeginTransactionAsync();
        await escribir(tx);
        await tx.CommitAsync();
    }

    // ---- CHECK, FKs y unicidad -----------------------------------------------------------------

    [Fact]
    public async Task UnaAplicacionConImporteCeroViolaLaCheckDeImportePositivo()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnaAplicacionConImporteCeroViolaLaCheckDeImportePositivo));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 100m);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarAplicacionAsync(cruda, null, p, idPago, idCompra, 0m));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal("ck_aplicaciones_pago_importe_positivo", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaAplicacionDeUnPagoInexistenteViolaLaFkDelPago()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnaAplicacionDeUnPagoInexistenteViolaLaFkDelPago));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 100m);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarAplicacionAsync(cruda, null, p, 999999, idCompra, 10m));

        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_aplicaciones_pago_pago", excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaAplicacionAUnaCompraDeOtroTenantViolaLaFkDeLaCompra()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnaAplicacionAUnaCompraDeOtroTenantViolaLaFkDeLaCompra));
        var ajeno = await SembrarPrerequisitosAsync(nameof(UnaAplicacionAUnaCompraDeOtroTenantViolaLaFkDeLaCompra) + "Ajeno");
        int idCompraAjena;
        await using (var crudaAjena = await fixture.AbrirConexionCrudaAsync("tenant", ajeno.IdTenant))
        {
            idCompraAjena = await InsertarCompraAsync(crudaAjena, ajeno, 100m);
        }

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);

        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarAplicacionAsync(cruda, null, p, idPago, idCompraAjena, 10m));

        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_aplicaciones_pago_compra", excepcion.ConstraintName);
    }

    [Fact]
    public async Task DosAplicacionesVivasDelMismoParViolanLaUnicidadParcialYUnaDadaDeBajaNo()
    {
        var p = await SembrarPrerequisitosAsync(nameof(DosAplicacionesVivasDelMismoParViolanLaUnicidadParcialYUnaDadaDeBajaNo));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 100m);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);
        await LaTransaccionComiteaAsync(cruda, tx => InsertarAplicacionAsync(cruda, tx, p, idPago, idCompra, 30m));

        var excepcion = await Assert.ThrowsAsync<PostgresException>(
            () => InsertarAplicacionAsync(cruda, null, p, idPago, idCompra, 20m));
        Assert.Equal("23505", excepcion.SqlState);
        Assert.Equal("ux_aplicaciones_pago_pago_compra", excepcion.ConstraintName);

        // Una fila dada de baja del mismo par no choca: la unicidad es solo entre las vivas.
        await LaTransaccionComiteaAsync(
            cruda, tx => InsertarAplicacionAsync(cruda, tx, p, idPago, idCompra, 20m, baja: DateTimeOffset.UtcNow));
    }

    // ---- constraint trigger contra el pago ------------------------------------------------------

    /// <summary>LA CLÁUSULA: lo aplicado de un pago no puede superar su importe; falla AL COMITEAR (el trigger es diferido)
    /// con 23514 y el nombre del trigger como constraint, que es lo que traduce <c>ManejadorDeErrores</c>.</summary>
    [Fact]
    public async Task AplicarMasQueElImporteDelPagoFallaAlComitearConElNombreDelTrigger()
    {
        var p = await SembrarPrerequisitosAsync(nameof(AplicarMasQueElImporteDelPagoFallaAlComitearConElNombreDelTrigger));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 500m);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);

        var excepcion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => InsertarAplicacionAsync(cruda, tx, p, idPago, idCompra, 120m));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(TriggerPago, excepcion.ConstraintName);
        Assert.Equal(0L, await EscalarAsync<long>(cruda, "SELECT count(*) FROM aplicaciones_pago WHERE id_pago = $1", idPago));
    }

    [Fact]
    public async Task VariasAplicacionesQueSumanExactamenteElImporteDelPagoComitean()
    {
        var p = await SembrarPrerequisitosAsync(nameof(VariasAplicacionesQueSumanExactamenteElImporteDelPagoComitean));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var compraA = await InsertarCompraAsync(cruda, p, 70m);
        var compraB = await InsertarCompraAsync(cruda, p, 200m);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);

        await LaTransaccionComiteaAsync(cruda, async tx =>
        {
            await InsertarAplicacionAsync(cruda, tx, p, idPago, compraA, 70m);
            await InsertarAplicacionAsync(cruda, tx, p, idPago, compraB, 30m);
        });

        Assert.Equal(100m, await EscalarAsync<decimal>(cruda, "SELECT sum(importe) FROM aplicaciones_pago WHERE id_pago = $1", idPago));
    }

    [Fact]
    public async Task BajarElImporteDelPagoPorDebajoDeLoAplicadoFallaAlComitear()
    {
        var p = await SembrarPrerequisitosAsync(nameof(BajarElImporteDelPagoPorDebajoDeLoAplicadoFallaAlComitear));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 500m);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);
        await LaTransaccionComiteaAsync(cruda, tx => InsertarAplicacionAsync(cruda, tx, p, idPago, idCompra, 80m));

        var excepcion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => EjecutarAsync(cruda, tx, "UPDATE pagos SET importe = 50 WHERE id_pago = $1", idPago));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(TriggerPago, excepcion.ConstraintName);
        Assert.Equal(100m, await EscalarAsync<decimal>(cruda, "SELECT importe FROM pagos WHERE id_pago = $1", idPago));
    }

    [Fact]
    public async Task DarDeBajaUnPagoConAplicacionesVivasFallaAlComitearYSiSeLiberanPrimeroComitea()
    {
        var p = await SembrarPrerequisitosAsync(nameof(DarDeBajaUnPagoConAplicacionesVivasFallaAlComitearYSiSeLiberanPrimeroComitea));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 500m);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);
        await LaTransaccionComiteaAsync(cruda, tx => InsertarAplicacionAsync(cruda, tx, p, idPago, idCompra, 80m));

        var excepcion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => EjecutarAsync(cruda, tx, "UPDATE pagos SET deleted_at = now() WHERE id_pago = $1", idPago));
        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(TriggerPago, excepcion.ConstraintName);

        // Liberar la aplicación y dar de baja el pago en la misma transacción, en cualquier orden, comitea.
        await LaTransaccionComiteaAsync(cruda, async tx =>
        {
            await EjecutarAsync(cruda, tx, "UPDATE pagos SET deleted_at = now() WHERE id_pago = $1", idPago);
            await EjecutarAsync(cruda, tx, "UPDATE aplicaciones_pago SET deleted_at = now() WHERE id_pago = $1", idPago);
        });
    }

    [Fact]
    public async Task MoverUnaAplicacionAOtroPagoControlaLosDosPagos()
    {
        var p = await SembrarPrerequisitosAsync(nameof(MoverUnaAplicacionAOtroPagoControlaLosDosPagos));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 500m);
        var origen = await InsertarPagoAsync(cruda, p, 100m);
        var destino = await InsertarPagoAsync(cruda, p, 100m);
        await LaTransaccionComiteaAsync(cruda, tx => InsertarAplicacionAsync(cruda, tx, p, origen, idCompra, 100m));
        var otraCompra = await InsertarCompraAsync(cruda, p, 500m);
        await LaTransaccionComiteaAsync(cruda, tx => InsertarAplicacionAsync(cruda, tx, p, destino, otraCompra, 60m));

        var excepcion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => EjecutarAsync(cruda, tx, "UPDATE aplicaciones_pago SET id_pago = $1 WHERE id_pago = $2", destino, origen));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(TriggerPago, excepcion.ConstraintName);
    }

    // ---- constraint trigger contra la compra ----------------------------------------------------

    [Fact]
    public async Task AplicarMasQueElTotalDeLaCompraFallaAlComitearConElNombreDelTrigger()
    {
        var p = await SembrarPrerequisitosAsync(nameof(AplicarMasQueElTotalDeLaCompraFallaAlComitearConElNombreDelTrigger));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 100m);
        var idPago = await InsertarPagoAsync(cruda, p, 500m);

        var excepcion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => InsertarAplicacionAsync(cruda, tx, p, idPago, idCompra, 150m));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(TriggerCompra, excepcion.ConstraintName);
    }

    [Fact]
    public async Task DosPagosQueEnConjuntoSuperanElTotalDeLaCompraFallanAlComitear()
    {
        var p = await SembrarPrerequisitosAsync(nameof(DosPagosQueEnConjuntoSuperanElTotalDeLaCompraFallanAlComitear));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 100m);
        var pagoUno = await InsertarPagoAsync(cruda, p, 60m);
        var pagoDos = await InsertarPagoAsync(cruda, p, 60m);

        var excepcion = await LaTransaccionFallaAlComitearAsync(cruda, async tx =>
        {
            await InsertarAplicacionAsync(cruda, tx, p, pagoUno, idCompra, 60m);
            await InsertarAplicacionAsync(cruda, tx, p, pagoDos, idCompra, 60m);
        });

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(TriggerCompra, excepcion.ConstraintName);
    }

    [Theory]
    [InlineData("borrador")]
    [InlineData("anulada")]
    public async Task AplicarAUnaCompraQueNoEstaConfirmadaFallaAlComitear(string estado)
    {
        var p = await SembrarPrerequisitosAsync(nameof(AplicarAUnaCompraQueNoEstaConfirmadaFallaAlComitear) + estado);
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 100m, estado);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);

        var excepcion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => InsertarAplicacionAsync(cruda, tx, p, idPago, idCompra, 50m));

        Assert.Equal("23514", excepcion.SqlState);
        Assert.Equal(TriggerCompra, excepcion.ConstraintName);
    }

    [Fact]
    public async Task UnaAplicacionDadaDeBajaNoCuentaNiExigeUnaCompraConfirmada()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnaAplicacionDadaDeBajaNoCuentaNiExigeUnaCompraConfirmada));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 100m, "anulada");
        var idPago = await InsertarPagoAsync(cruda, p, 100m);

        await LaTransaccionComiteaAsync(
            cruda, tx => InsertarAplicacionAsync(cruda, tx, p, idPago, idCompra, 500m, baja: DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task UnPagoDeOtroProveedorOUnPagoQueNoEsDeProveedorFallanAlComitear()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnPagoDeOtroProveedorOUnPagoQueNoEsDeProveedorFallanAlComitear));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 100m);

        var deOtroProveedor = await InsertarPagoAsync(cruda, p, 50m, idProveedor: p.IdProveedorOtro);
        var noEsDeProveedor = await InsertarPagoAsync(cruda, p, 50m, categoria: "otros");

        foreach (var idPago in new[] { deOtroProveedor, noEsDeProveedor })
        {
            var excepcion = await LaTransaccionFallaAlComitearAsync(
                cruda, tx => InsertarAplicacionAsync(cruda, tx, p, idPago, idCompra, 50m));

            Assert.Equal("23514", excepcion.SqlState);
            Assert.Equal(TriggerCompra, excepcion.ConstraintName);
        }
    }

    /// <summary>Desde la migración AplicacionesEntreEmpresas el trigger ya no exige la misma empresa: los vínculos
    /// heredados entre empresas siguen contando. Los vínculos nuevos los rechaza el servicio.</summary>
    [Fact]
    public async Task UnPagoDeOtraEmpresaDelMismoProveedorComitea()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnPagoDeOtraEmpresaDelMismoProveedorComitea));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 100m);
        var deOtraEmpresa = await InsertarPagoAsync(cruda, p, 50m, idEmpresa: p.IdEmpresaOtra);

        await LaTransaccionComiteaAsync(cruda, tx => InsertarAplicacionAsync(cruda, tx, p, deOtraEmpresa, idCompra, 50m));
    }

    [Fact]
    public async Task BajarElTotalOAnularUnaCompraConAplicacionesVivasFallaAlComitear()
    {
        var p = await SembrarPrerequisitosAsync(nameof(BajarElTotalOAnularUnaCompraConAplicacionesVivasFallaAlComitear));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 100m);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);
        await LaTransaccionComiteaAsync(cruda, tx => InsertarAplicacionAsync(cruda, tx, p, idPago, idCompra, 80m));

        var bajaDelTotal = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => EjecutarAsync(cruda, tx, "UPDATE comprobantes_compra SET total = 50 WHERE id_comprobante_compra = $1", idCompra));
        Assert.Equal("23514", bajaDelTotal.SqlState);
        Assert.Equal(TriggerCompra, bajaDelTotal.ConstraintName);

        var anulacion = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => EjecutarAsync(
                cruda, tx, "UPDATE comprobantes_compra SET estado = 'anulada' WHERE id_comprobante_compra = $1", idCompra));
        Assert.Equal("23514", anulacion.SqlState);
        Assert.Equal(TriggerCompra, anulacion.ConstraintName);

        // Liberar las aplicaciones y anular en la misma transacción comitea.
        await LaTransaccionComiteaAsync(cruda, async tx =>
        {
            await EjecutarAsync(cruda, tx, "UPDATE comprobantes_compra SET estado = 'anulada' WHERE id_comprobante_compra = $1", idCompra);
            await EjecutarAsync(cruda, tx, "UPDATE aplicaciones_pago SET deleted_at = now() WHERE id_comprobante_compra = $1", idCompra);
        });
    }

    [Fact]
    public async Task CambiarElProveedorOLaCategoriaDeUnPagoAplicadoFallaAlComitear()
    {
        var p = await SembrarPrerequisitosAsync(nameof(CambiarElProveedorOLaCategoriaDeUnPagoAplicadoFallaAlComitear));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var idCompra = await InsertarCompraAsync(cruda, p, 100m);
        var idPago = await InsertarPagoAsync(cruda, p, 100m);
        await LaTransaccionComiteaAsync(cruda, tx => InsertarAplicacionAsync(cruda, tx, p, idPago, idCompra, 80m));

        var porProveedor = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => EjecutarAsync(cruda, tx, "UPDATE pagos SET id_proveedor = $1 WHERE id_pago = $2", p.IdProveedorOtro, idPago));
        Assert.Equal("23514", porProveedor.SqlState);
        Assert.Equal(TriggerCompra, porProveedor.ConstraintName);

        var porCategoria = await LaTransaccionFallaAlComitearAsync(
            cruda, tx => EjecutarAsync(cruda, tx, "UPDATE pagos SET categoria = 'otros' WHERE id_pago = $1", idPago));
        Assert.Equal("23514", porCategoria.SqlState);
        Assert.Equal(TriggerCompra, porCategoria.ConstraintName);
    }

    /// <summary>El caso feliz de punta a punta: un pago repartido entre dos compras y una compra que recibe dos pagos
    /// comitean, y cambiar un dato de la cabecera que no entra en las invariantes no dispara nada.</summary>
    [Fact]
    public async Task UnConjuntoValidoDeAplicacionesComitea()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnConjuntoValidoDeAplicacionesComitea));
        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        var compraA = await InsertarCompraAsync(cruda, p, 100m);
        var compraB = await InsertarCompraAsync(cruda, p, 50m);
        var pagoUno = await InsertarPagoAsync(cruda, p, 120m);
        var pagoDos = await InsertarPagoAsync(cruda, p, 30m);

        await LaTransaccionComiteaAsync(cruda, async tx =>
        {
            await InsertarAplicacionAsync(cruda, tx, p, pagoUno, compraA, 100m);
            await InsertarAplicacionAsync(cruda, tx, p, pagoUno, compraB, 20m);
            await InsertarAplicacionAsync(cruda, tx, p, pagoDos, compraB, 30m);
        });

        await LaTransaccionComiteaAsync(
            cruda, tx => EjecutarAsync(cruda, tx, "UPDATE pagos SET concepto = 'otro concepto' WHERE id_pago = $1", pagoUno));

        Assert.Equal(3L, await EscalarAsync<long>(cruda, "SELECT count(*) FROM aplicaciones_pago WHERE deleted_at IS NULL"));
    }
}
