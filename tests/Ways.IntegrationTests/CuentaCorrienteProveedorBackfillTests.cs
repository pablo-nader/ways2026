using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ways.Application.Compras;
using Ways.Domain.Articulos;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-15-cc-proveedores-ledger, Slice 1 (tasks 1.20, 1.21, mutation targets #1-#8): la
/// migración <c>CuentaCorrienteDeProveedoresEtapa15</c> corre UNA sola vez, al migrar el
/// contenedor — <see cref="WaysApiFixture"/> aplica TODAS las migraciones antes de que exista
/// ningún dato, así que el backfill de esta migración no tiene nada que leer contra esa base
/// compartida. Este archivo NO reusa <see cref="WaysApiFixture"/>: crea su PROPIA base dentro
/// del mismo contenedor (mismo patrón que <c>ComprasTipoSeedTests</c>/
/// <c>CuentaCorrienteEtapa7BackstopTests</c>), migra hasta la migración ANTERIOR
/// (<c>AuditoriaEtapa14</c>), siembra la fixture de datos ANTES de que el backfill exista, y
/// recién ahí aplica <c>CuentaCorrienteDeProveedoresEtapa15</c> — así el backfill sí tiene algo
/// que leer, exactamente como pasaría en una base real ya operando.
///
/// <c>ServicioDeSaldoDeProveedor.ObtenerAsync</c> (Application, SIN CAMBIOS en esta slice — la
/// re-derivación desde el ledger es tarea 4.5, Slice 4) es la fórmula retirada corriendo de
/// verdad contra la fixture ANTES de migrar: la captura "antes" contra el mismo código que
/// calculaba el saldo en producción, no una reimplementación de la fórmula en el test.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentaCorrienteProveedorBackfillTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionAnterior = "20260816044634_AuditoriaEtapa14";

    private static DbContextOptions<WaysDbContext> ConstruirOpciones(string cadena) =>
        new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(cadena, npgsql =>
            {
                npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                npgsql.MapEnum<EstadoTenant>("estado_tenant");
                npgsql.MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago");
                npgsql.MapEnum<ClaseComprobante>("clase_comprobante");
                npgsql.MapEnum<TipoDocumento>("tipo_documento");
                npgsql.MapEnum<ModoLista>("modo_lista");
                npgsql.MapEnum<UnidadVenta>("unidad_venta");
                npgsql.MapEnum<EstadoComprobante>("estado_comprobante");
                npgsql.MapEnum<MotivoStock>("motivo_stock");
                npgsql.MapEnum<TipoMovimientoCc>("tipo_movimiento_cc");
                npgsql.MapEnum<EstadoTurno>("estado_turno");
                npgsql.MapEnum<TipoMovimientoCaja>("tipo_movimiento_caja");
                npgsql.MapEnum<TipoMovimientoTesoreria>("tipo_movimiento_tesoreria");
                npgsql.MapEnum<CategoriaGasto>("categoria_gasto");
                npgsql.MapEnum<EstadoCompra>("estado_compra");
                npgsql.MapEnum<TipoMovimientoCcProveedor>("tipo_movimiento_cc_proveedor");
            })
            .Options;

    private sealed record Entorno(int IdTenant, int IdPuntoVenta, int IdEmpleado, int IdCondicionFiscal, int IdTipoComprobanteCompra, int IdMedioPago, int IdTurnoCaja);

    /// <summary>Arma la cadena completa de catálogos que <c>InicializadorDeBaseDeDatos</c>
    /// sembraría en un host real — acá a mano, porque esta prueba NO levanta el host completo
    /// (necesita controlar el momento exacto del migrate).</summary>
    private static async Task<Entorno> SembrarEntornoAsync(WaysDbContext db, string nombre)
    {
        var ahora = DateTimeOffset.UtcNow;

        var tenant = new Tenant { Nombre = nombre, Estado = EstadoTenant.Activo, CreatedAt = ahora, UpdatedAt = ahora };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var empresa = new Empresa { IdTenant = tenant.Id, RazonSocial = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.Empresas.Add(empresa);
        await db.SaveChangesAsync();

        var puntoVenta = new PuntoVenta
        {
            IdTenant = tenant.Id, IdEmpresa = empresa.Id, Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.PuntosVenta.Add(puntoVenta);
        await db.SaveChangesAsync();

        // Rol/CondicionFiscal/TipoComprobante son catálogos [global] que en producción siembra
        // InicializadorDeBaseDeDatos al arrancar el host — acá se siembran a mano una sola vez,
        // con IDs desincronizados de los de tenant/empresa/proveedor (regla 11: que id_entidad
        // discrimine, nunca coincida por casualidad con otra secuencia).
        if (!await db.Roles.AnyAsync(r => r.Id == (int)RolConocido.Vendedor))
        {
            db.Roles.Add(new Rol { Id = (int)RolConocido.Vendedor, Nombre = "Vendedor", CreatedAt = ahora, UpdatedAt = ahora });
            await db.SaveChangesAsync();
        }

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var usuario = new Usuario
        {
            IdTenant = tenant.Id,
            NombreUsuario = $"{nombre.ToLowerInvariant()}-empleado",
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

        var tipoComprobanteCompra = new TipoComprobante
        {
            Clase = ClaseComprobante.Compra, Codigo = $"{nombre}-CFA", Nombre = "Factura A de compra",
            Letra = 'A', Signo = 1, DiscriminaIva = true, EsFiscal = false, AfectaStock = true,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.TiposComprobante.Add(tipoComprobanteCompra);
        await db.SaveChangesAsync();

        var medioPago = new MedioPago
        {
            IdTenant = tenant.Id, Nombre = "Efectivo", Orden = 1, Comportamiento = ComportamientoMedioPago.Efectivo,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.MediosPago.Add(medioPago);
        await db.SaveChangesAsync();

        var turno = new TurnoCaja
        {
            IdTenant = tenant.Id, IdPuntoVenta = puntoVenta.Id, IdEmpleadoApertura = usuario.Id,
            FechaApertura = ahora, FondoInicial = 0m, Estado = EstadoTurno.Abierto,
            CreatedAt = ahora, UpdatedAt = ahora
        };
        db.TurnosCaja.Add(turno);
        await db.SaveChangesAsync();

        return new Entorno(tenant.Id, puntoVenta.Id, usuario.Id, condicionFiscal.Id, tipoComprobanteCompra.Id, medioPago.Id, turno.Id);
    }

    /// <summary>Proveedores.Saldo NO existe todavía en el esquema pre-migración — INSERT crudo
    /// con la lista de columnas de ANTES de esta etapa (nunca vía EF, que incluiría la columna
    /// nueva en el INSERT y rompería contra el esquema viejo).</summary>
    private static async Task<int> SembrarProveedorPreMigracionAsync(
        NpgsqlConnection cruda, int idTenant, int idCondicionFiscal, string nombre, bool eliminado)
    {
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO proveedores (id_tenant, razon_social, id_condicion_fiscal, created_at, updated_at, deleted_at) " +
            "VALUES ($1, $2, $3, now(), now(), $4) RETURNING id_proveedor";
        comando.Parameters.Add(new NpgsqlParameter { Value = idTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = nombre });
        comando.Parameters.Add(new NpgsqlParameter { Value = idCondicionFiscal });
        comando.Parameters.Add(new NpgsqlParameter { Value = (object?)(eliminado ? DateTimeOffset.UtcNow : null) ?? DBNull.Value });
        return (int)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<int> SembrarCompraAsync(
        WaysDbContext db, Entorno ctx, int idProveedor, decimal total, EstadoCompra estado, bool eliminada, string numeroExterno)
    {
        var ahora = DateTimeOffset.UtcNow;
        var compra = new ComprobanteCompra
        {
            IdTenant = ctx.IdTenant,
            IdProveedor = idProveedor,
            IdTipoComprobante = ctx.IdTipoComprobanteCompra,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdEmpleado = ctx.IdEmpleado,
            Subtotal = total,
            DescuentoTotal = 0m,
            Total = total,
            Estado = estado,
            // ck_comprobantes_compra_confirmada_completa: los tres campos completos salvo en borrador.
            NumeroExterno = estado == EstadoCompra.Borrador ? null : numeroExterno,
            FechaComprobante = estado == EstadoCompra.Borrador ? null : DateOnly.FromDateTime(DateTime.UtcNow),
            FechaRecepcion = estado == EstadoCompra.Borrador ? null : ahora,
            CreatedAt = ahora,
            UpdatedAt = ahora,
            DeletedAt = eliminada ? ahora : null
        };
        db.ComprobantesCompra.Add(compra);
        await db.SaveChangesAsync();
        return compra.Id;
    }

    private static async Task SembrarGastoAsync(
        WaysDbContext db, Entorno ctx, int? idProveedor, int? idComprobanteCompra, decimal importe, bool eliminado,
        CategoriaGasto categoria = CategoriaGasto.Proveedor)
    {
        var ahora = DateTimeOffset.UtcNow;
        var gasto = new Gasto
        {
            IdTenant = ctx.IdTenant,
            Fecha = ahora,
            IdPuntoVenta = ctx.IdPuntoVenta,
            IdTurnoCaja = ctx.IdTurnoCaja,
            IdEmpleado = ctx.IdEmpleado,
            Categoria = categoria,
            IdProveedor = idProveedor,
            IdComprobanteCompra = idComprobanteCompra,
            Concepto = "Gasto de prueba",
            IdMedioPago = ctx.IdMedioPago,
            Importe = importe,
            CreatedAt = ahora,
            UpdatedAt = ahora,
            DeletedAt = eliminado ? ahora : null
        };
        db.Gastos.Add(gasto);
        await db.SaveChangesAsync();
    }

    private sealed record Fixture(
        string NombreBase, string CadenaAdmin, string CadenaNueva,
        int IdConDeuda, int IdCompraConDeuda, decimal SaldoPrevioConDeuda,
        int IdSoftDeleteCompra,
        int IdSoftDeleteGasto,
        int IdSoftDeleteProveedor,
        int IdGastoHuerfano,
        int IdBorradorYAnulada,
        int IdSinHistoria);

    /// <summary>Crea la base dedicada, migra hasta <see cref="MigracionAnterior"/>, siembra los 7
    /// proveedores discriminantes (task 1.20) y captura el saldo PREVIO con
    /// <see cref="ServicioDeSaldoDeProveedor"/> (sin cambios en esta slice) — nunca migra la
    /// migración bajo prueba: eso lo hace el llamador, para poder decidir cuándo.</summary>
    private async Task<Fixture> PrepararFixtureSinMigrarAsync(string sufijo)
    {
        var nombreBase = $"ways_stage15_{sufijo}_{Guid.NewGuid():N}";
        var cadenaAdmin = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = "postgres" }.ConnectionString;
        var cadenaNueva = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = nombreBase }.ConnectionString;

        await using (var admin = new NpgsqlConnection(cadenaAdmin))
        {
            await admin.OpenAsync();
            await using var crear = admin.CreateCommand();
            crear.CommandText = $"CREATE DATABASE \"{nombreBase}\"";
            await crear.ExecuteNonQueryAsync();
        }

        var opciones = ConstruirOpciones(cadenaNueva);

        await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
        {
            var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync(MigracionAnterior);
        }

        await using var conexionCruda = new NpgsqlConnection(cadenaNueva);
        await conexionCruda.OpenAsync();

        await using var db2 = new WaysDbContext(opciones, TenantActualFijo.Plataforma);

        var ctx = await SembrarEntornoAsync(db2, sufijo);

        // P1 "ConDeuda" — los números EXACTOS del escenario de spec.md ("The backfill reproduces
        // the exact pre-migration saldo"): confirmada 2000, anulada 500, borrador 300, gasto
        // ligado 700, gasto sin ligar 200 ⇒ derivado = 2000 − 700 − 200 = 1100.
        var idConDeuda = await SembrarProveedorPreMigracionAsync(conexionCruda, ctx.IdTenant, ctx.IdCondicionFiscal, "ConDeuda", eliminado: false);
        var idCompraConDeuda = await SembrarCompraAsync(db2, ctx, idConDeuda, 2000m, EstadoCompra.Confirmada, eliminada: false, "F0001-00000001");
        await SembrarCompraAsync(db2, ctx, idConDeuda, 500m, EstadoCompra.Anulada, eliminada: false, "F0001-00000002");
        await SembrarCompraAsync(db2, ctx, idConDeuda, 300m, EstadoCompra.Borrador, eliminada: false, "F0001-00000003");
        await SembrarGastoAsync(db2, ctx, idConDeuda, idCompraConDeuda, 700m, eliminado: false);
        await SembrarGastoAsync(db2, ctx, idConDeuda, null, 200m, eliminado: false);

        // Target #1: deleted_at IS NULL en comprobantes_compra — una compra confirmada de 1000
        // pero soft-deleted no debe generar fila ni saldo.
        var idSoftDeleteCompra = await SembrarProveedorPreMigracionAsync(conexionCruda, ctx.IdTenant, ctx.IdCondicionFiscal, "SoftDeleteCompra", eliminado: false);
        await SembrarCompraAsync(db2, ctx, idSoftDeleteCompra, 1000m, EstadoCompra.Confirmada, eliminada: true, "F0001-00000004");

        // Target #2: deleted_at IS NULL en gastos — compra de 1000 vigente, gasto ligado de 1000
        // soft-deleted: si el filtro se respeta, el gasto NO resta y el saldo derivado es 1000.
        var idSoftDeleteGasto = await SembrarProveedorPreMigracionAsync(conexionCruda, ctx.IdTenant, ctx.IdCondicionFiscal, "SoftDeleteGasto", eliminado: false);
        var idCompraSoftDeleteGasto = await SembrarCompraAsync(db2, ctx, idSoftDeleteGasto, 1000m, EstadoCompra.Confirmada, eliminada: false, "F0001-00000005");
        await SembrarGastoAsync(db2, ctx, idSoftDeleteGasto, idCompraSoftDeleteGasto, 1000m, eliminado: true);

        // Target #3: deleted_at IS NULL en proveedores — el proveedor mismo está soft-deleted:
        // ninguna fila de apertura, sin importar que tenga una compra vigente de 1000.
        var idSoftDeleteProveedor = await SembrarProveedorPreMigracionAsync(conexionCruda, ctx.IdTenant, ctx.IdCondicionFiscal, "SoftDeleteProveedor", eliminado: true);
        await SembrarCompraAsync(db2, ctx, idSoftDeleteProveedor, 1000m, EstadoCompra.Confirmada, eliminada: false, "F0001-00000006");

        // Target #4: id_proveedor IS NOT NULL en el predicate de gastos — un gasto huérfano
        // (categoria=proveedor, id_proveedor NULL) no debe entrar en NINGÚN proveedor. Este
        // proveedor tiene su propia compra de 1000 sin gastos ligados: derivado = 1000.
        var idGastoHuerfano = await SembrarProveedorPreMigracionAsync(conexionCruda, ctx.IdTenant, ctx.IdCondicionFiscal, "GastoHuerfano", eliminado: false);
        await SembrarCompraAsync(db2, ctx, idGastoHuerfano, 1000m, EstadoCompra.Confirmada, eliminada: false, "F0001-00000007");
        await SembrarGastoAsync(db2, ctx, null, null, 9999m, eliminado: false);

        // Target #5: estado = 'confirmada' — solo borrador + anulada, ninguna confirmada:
        // derivado = 0, sin fila.
        var idBorradorYAnulada = await SembrarProveedorPreMigracionAsync(conexionCruda, ctx.IdTenant, ctx.IdCondicionFiscal, "BorradorYAnulada", eliminado: false);
        await SembrarCompraAsync(db2, ctx, idBorradorYAnulada, 300m, EstadoCompra.Borrador, eliminada: false, "F0001-00000008");
        await SembrarCompraAsync(db2, ctx, idBorradorYAnulada, 500m, EstadoCompra.Anulada, eliminada: false, "F0001-00000009");

        // Target #6: WHERE d.saldo <> 0 — sin ninguna actividad, derivado = 0, sin fila.
        var idSinHistoria = await SembrarProveedorPreMigracionAsync(conexionCruda, ctx.IdTenant, ctx.IdCondicionFiscal, "SinHistoria", eliminado: false);

        var saldoPrevioConDeuda = (await new ServicioDeSaldoDeProveedor(db2).ObtenerAsync(idConDeuda)).Saldo;

        return new Fixture(
            nombreBase, cadenaAdmin, cadenaNueva,
            idConDeuda, idCompraConDeuda, saldoPrevioConDeuda,
            idSoftDeleteCompra, idSoftDeleteGasto, idSoftDeleteProveedor, idGastoHuerfano,
            idBorradorYAnulada, idSinHistoria);
    }

    /// <summary>Extrae los dos statements crudos del backfill DIRECTO del archivo `.cs` de la
    /// migración — nunca una copia escrita a mano en el test (target #7/target #4 lesson: una
    /// copia hardcodeada no detecta ninguna mutación del archivo real). Ambos statements viven
    /// en literales de string crudo (<c>"""..."""</c>) dentro de <c>Up()</c>, statement 1
    /// primero.</summary>
    private static (string Statement1, string Statement2) LeerStatementsDelBackfillDesdeElArchivoDeLaMigracion()
    {
        var rutaMigracion = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "src", "Ways.Infrastructure", "Persistencia", "Migraciones",
            "20260817153958_CuentaCorrienteDeProveedoresEtapa15.cs");

        Assert.True(File.Exists(rutaMigracion), $"No se encontró la migración en {rutaMigracion}");

        var fuente = File.ReadAllText(rutaMigracion);
        const string delimitador = "\"\"\"";

        var inicio1 = fuente.IndexOf(delimitador, StringComparison.Ordinal);
        var finApertura1 = inicio1 + delimitador.Length;
        var fin1 = fuente.IndexOf(delimitador, finApertura1, StringComparison.Ordinal);
        var statement1 = fuente[finApertura1..fin1].Trim();

        var inicio2 = fuente.IndexOf(delimitador, fin1 + delimitador.Length, StringComparison.Ordinal);
        var finApertura2 = inicio2 + delimitador.Length;
        var fin2 = fuente.IndexOf(delimitador, finApertura2, StringComparison.Ordinal);
        var statement2 = fuente[finApertura2..fin2].Trim();

        Assert.Contains("INSERT INTO movimientos_cuenta_corriente_proveedor", statement1);
        Assert.Contains("UPDATE proveedores", statement2);

        return (statement1, statement2);
    }

    private static async Task EliminarBaseAsync(string cadenaAdmin, string nombreBase)
    {
        await using var admin = new NpgsqlConnection(cadenaAdmin);
        await admin.OpenAsync();
        await using var dropear = admin.CreateCommand();
        dropear.CommandText = $"DROP DATABASE IF EXISTS \"{nombreBase}\" WITH (FORCE)";
        await dropear.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ElBackfillReproduceElSaldoPrevioPorDatosYRespetaLosSieteDiscriminantes()
    {
        var f = await PrepararFixtureSinMigrarAsync("fidelidad");
        try
        {
            Assert.Equal(1100m, f.SaldoPrevioConDeuda);

            var opciones = ConstruirOpciones(f.CadenaNueva);
            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(); // aplica CuentaCorrienteDeProveedoresEtapa15, la única pendiente
            }

            await using var verificacion = new NpgsqlConnection(f.CadenaNueva);
            await verificacion.OpenAsync();

            async Task<(decimal? Importe, decimal? SaldoResultante)?> LeerAperturaAsync(int idProveedor)
            {
                await using var comando = verificacion.CreateCommand();
                comando.CommandText =
                    "SELECT importe, saldo_resultante FROM movimientos_cuenta_corriente_proveedor " +
                    "WHERE id_proveedor = $1 AND tipo = 'apertura'";
                comando.Parameters.Add(new NpgsqlParameter { Value = idProveedor });
                await using var lector = await comando.ExecuteReaderAsync();
                if (!await lector.ReadAsync())
                {
                    return null;
                }
                return (lector.GetDecimal(0), lector.GetDecimal(1));
            }

            async Task<decimal> LeerSaldoAsync(int idProveedor)
            {
                await using var comando = verificacion.CreateCommand();
                comando.CommandText = "SELECT saldo FROM proveedores WHERE id_proveedor = $1";
                comando.Parameters.Add(new NpgsqlParameter { Value = idProveedor });
                return (decimal)(await comando.ExecuteScalarAsync())!;
            }

            // ConDeuda (spec scenario): la fila de apertura Y el cache coinciden con el saldo
            // previo, calculado por el MISMO ServicioDeSaldoDeProveedor que corría antes de migrar.
            var aperturaConDeuda = await LeerAperturaAsync(f.IdConDeuda);
            Assert.NotNull(aperturaConDeuda);
            Assert.Equal(1100m, aperturaConDeuda!.Value.Importe);
            Assert.Equal(1100m, aperturaConDeuda.Value.SaldoResultante);
            var saldoConDeuda = await LeerSaldoAsync(f.IdConDeuda);
            Assert.Equal(1100m, saldoConDeuda);
            // target #8 (cross-check): el cache tiene que ser EXACTAMENTE el saldo_resultante de
            // la fila que el statement 1 escribió — nunca un recálculo aparte.
            Assert.Equal(aperturaConDeuda.Value.SaldoResultante, saldoConDeuda);

            // target #1: compra soft-deleted excluida ⇒ sin fila, saldo 0.
            Assert.Null(await LeerAperturaAsync(f.IdSoftDeleteCompra));
            Assert.Equal(0m, await LeerSaldoAsync(f.IdSoftDeleteCompra));

            // target #2: gasto soft-deleted excluido ⇒ derivado = 1000 (la compra sola).
            var aperturaSoftDeleteGasto = await LeerAperturaAsync(f.IdSoftDeleteGasto);
            Assert.NotNull(aperturaSoftDeleteGasto);
            Assert.Equal(1000m, aperturaSoftDeleteGasto!.Value.Importe);
            Assert.Equal(1000m, await LeerSaldoAsync(f.IdSoftDeleteGasto));

            // target #3: proveedor soft-deleted ⇒ sin fila pese a la compra vigente.
            Assert.Null(await LeerAperturaAsync(f.IdSoftDeleteProveedor));
            Assert.Equal(0m, await LeerSaldoAsync(f.IdSoftDeleteProveedor));

            // target #4: el gasto huérfano (id_proveedor NULL) no reduce a NADIE — este
            // proveedor solo tiene su propia compra de 1000, sin gasto ligado.
            var aperturaGastoHuerfano = await LeerAperturaAsync(f.IdGastoHuerfano);
            Assert.NotNull(aperturaGastoHuerfano);
            Assert.Equal(1000m, aperturaGastoHuerfano!.Value.Importe);
            Assert.Equal(1000m, await LeerSaldoAsync(f.IdGastoHuerfano));

            // target #5: borrador + anulada, ninguna confirmada ⇒ derivado 0, sin fila.
            Assert.Null(await LeerAperturaAsync(f.IdBorradorYAnulada));
            Assert.Equal(0m, await LeerSaldoAsync(f.IdBorradorYAnulada));

            // target #6: sin actividad ⇒ derivado 0, sin fila (WHERE d.saldo <> 0).
            Assert.Null(await LeerAperturaAsync(f.IdSinHistoria));
            Assert.Equal(0m, await LeerSaldoAsync(f.IdSinHistoria));
        }
        finally
        {
            await EliminarBaseAsync(f.CadenaAdmin, f.NombreBase);
        }
    }

    [Fact]
    public async Task ReejecutarElBackfillEsUnNoOpTarget7()
    {
        var f = await PrepararFixtureSinMigrarAsync("idempotencia");
        try
        {
            var opciones = ConstruirOpciones(f.CadenaNueva);
            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync();
            }

            await using var verificacion = new NpgsqlConnection(f.CadenaNueva);
            await verificacion.OpenAsync();

            async Task<int> ContarMovimientosAsync(int idProveedor)
            {
                await using var comando = verificacion.CreateCommand();
                comando.CommandText = "SELECT count(*) FROM movimientos_cuenta_corriente_proveedor WHERE id_proveedor = $1";
                comando.Parameters.Add(new NpgsqlParameter { Value = idProveedor });
                return Convert.ToInt32(await comando.ExecuteScalarAsync());
            }

            async Task<decimal> LeerSaldoAsync(int idProveedor)
            {
                await using var comando = verificacion.CreateCommand();
                comando.CommandText = "SELECT saldo FROM proveedores WHERE id_proveedor = $1";
                comando.Parameters.Add(new NpgsqlParameter { Value = idProveedor });
                return (decimal)(await comando.ExecuteScalarAsync())!;
            }

            Assert.Equal(1, await ContarMovimientosAsync(f.IdConDeuda));
            var saldoAntesDeReejecutar = await LeerSaldoAsync(f.IdConDeuda);
            Assert.Equal(0, await ContarMovimientosAsync(f.IdSinHistoria));

            // Re-ejecuta a mano el SQL idempotente de la migración — LEÍDO DEL ARCHIVO REAL de
            // la migración, nunca una copia escrita a mano en el test (la misma trampa que
            // target #4 expuso: una copia hardcodeada ejecuta el SQL CORRECTO sin importar lo
            // que la migración de verdad diga, y no detecta una mutación del guard NOT EXISTS
            // — target #7). Mismo patrón de reintento que ComprasTipoSeedTests/
            // CuentaCorrienteEtapa7BackstopTests, pero con la fuente de verdad correcta.
            var (statement1, statement2) = LeerStatementsDelBackfillDesdeElArchivoDeLaMigracion();

            await using (var comando = verificacion.CreateCommand())
            {
                comando.CommandText = statement1;
                await comando.ExecuteNonQueryAsync();
            }

            await using (var comando = verificacion.CreateCommand())
            {
                comando.CommandText = statement2;
                await comando.ExecuteNonQueryAsync();
            }

            Assert.Equal(1, await ContarMovimientosAsync(f.IdConDeuda));
            Assert.Equal(saldoAntesDeReejecutar, await LeerSaldoAsync(f.IdConDeuda));
            // Un proveedor con saldo derivado 0 sigue sin fila tras el reintento.
            Assert.Equal(0, await ContarMovimientosAsync(f.IdSinHistoria));
            Assert.Equal(0m, await LeerSaldoAsync(f.IdSinHistoria));
        }
        finally
        {
            await EliminarBaseAsync(f.CadenaAdmin, f.NombreBase);
        }
    }

    /// <summary>Target #4 — PROVABLY EQUIVALENT AT RUNTIME, prueba de TEXTO FUENTE en su lugar
    /// (mutation-proof-tests rule 3 agotada primero, finding registrado en tasks.md). La prueba
    /// de fidelidad de punta a punta NO puede matar el mutante que borra
    /// <c>id_proveedor IS NOT NULL</c> del predicate de gastos: la CTE <c>derivado</c> está
    /// enraizada en <c>proveedores</c> (<c>FROM proveedores p LEFT JOIN (...) g ON g.id_proveedor
    /// = p.id_proveedor</c>) y bajo semántica NULL de SQL ningún <c>p.id_proveedor</c> real
    /// (NOT NULL) puede emparejar con un grupo <c>g.id_proveedor IS NULL</c> — así que NINGÚN
    /// artefacto que la migración produce (fila del ledger, <c>proveedores.saldo</c>) puede
    /// diferir con o sin el filtro. Confirmado empíricamente DOS VECES: (1) la prueba de
    /// fidelidad de punta a punta pasa bajo la mutación; (2) una primera versión de esta prueba,
    /// que reconstruía el fragmento SQL a mano en el test en vez de leer el archivo real, también
    /// pasaba bajo la mutación — porque ejecutaba el SQL correcto, no el mutado. Sin diferencia
    /// observable en NINGÚN artefacto en tiempo de ejecución, no hay "debajo del confound" al
    /// que enrutar (la regla 3 asume un componente invocable por separado; acá el predicate vive
    /// dentro de un único literal SQL embebido, sin costura). La única prueba que SÍ detecta esta
    /// mutación es de texto fuente: lee el archivo de migración REAL y confirma que la cláusula
    /// sigue presente en el fragmento de gastos.</summary>
    [Fact]
    public void ElTextoFuenteDeLaMigracionConservaElFiltroIdProveedorNoNuloTarget4()
    {
        var rutaMigracion = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "src", "Ways.Infrastructure", "Persistencia", "Migraciones",
            "20260817153958_CuentaCorrienteDeProveedoresEtapa15.cs");

        Assert.True(File.Exists(rutaMigracion), $"No se encontró la migración en {rutaMigracion}");

        var fuente = File.ReadAllText(rutaMigracion);

        Assert.Contains(
            "WHERE categoria = 'proveedor' AND id_proveedor IS NOT NULL AND deleted_at IS NULL",
            fuente);
    }

    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;
}
