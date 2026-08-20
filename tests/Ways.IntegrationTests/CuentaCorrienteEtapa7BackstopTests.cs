using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
/// stage-7-cuenta-corriente, Slice 1 (task 1.9/1.10, db-error-backstops, design: Backstop Map;
/// Table Shapes B — "the key gate finding"): dos escenarios que el resto de la suite no cubre.
///
/// (1) <c>fk_movimientos_cuenta_corriente_actualizacion</c> — el self-FK del marcador de
/// reliquidación es inalcanzable bajo operación normal (el id viene del <c>RETURNING</c> de la
/// misma transacción que la fila que apunta, Slice 3); un INSERT crudo lo fuerza, mismo patrón
/// que <c>TurnosCajaYGastosBackstopTests</c>.
///
/// (2) El INSERT idempotente de <c>RC</c> en la migración <c>CuentaCorrienteEtapa7</c> —
/// <c>InicializadorDeBaseDeDatos.EjecutarAsync</c> solo siembra <c>tipos_comprobante</c> cuando
/// la tabla está vacía (:417), así que una base real ya migrada a stage 6 NUNCA recibiría RC de
/// otro modo. Se prueba armando una base de datos aislada dentro del mismo contenedor,
/// migrada solo hasta <c>TurnosCajaYGastosEtapa6</c>, con un catálogo pre-existente (tabla NO
/// vacía) — el escenario real que el gate exige probar, no asumir.
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class CuentaCorrienteEtapa7BackstopTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionStage6 = "20260804222255_TurnosCajaYGastosEtapa6";

    private sealed record Prerequisitos(int IdTenant, int IdPuntoVenta, int IdEmpleado, int IdCliente);

    private async Task<Prerequisitos> SembrarPrerequisitosAsync(string nombre)
    {
        using var _ = fixture.CreateClient(); // arranca el host (siembra roles, alícuotas, tipos de comprobante)

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);
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

        var listaPrecio = new ListaPrecio
        {
            IdTenant = tenant.Id, Nombre = nombre, EsDefault = true, Modo = ModoLista.Fija, CreatedAt = ahora, UpdatedAt = ahora
        };
        db.ListasPrecio.Add(listaPrecio);
        await db.SaveChangesAsync();

        var condicionFiscal = new CondicionFiscal { Codigo = $"{nombre}-CF", Nombre = nombre, CreatedAt = ahora, UpdatedAt = ahora };
        db.CondicionesFiscales.Add(condicionFiscal);
        await db.SaveChangesAsync();

        var cliente = new Cliente
        {
            IdTenant = tenant.Id,
            Numero = 2,
            Nombre = nombre,
            IdCondicionFiscal = condicionFiscal.Id,
            IdListaPrecio = listaPrecio.Id,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        return new Prerequisitos(tenant.Id, puntoVenta.Id, usuario.Id, cliente.Id);
    }

    // ---- fk_movimientos_cuenta_corriente_actualizacion (task 1.10) ---------------------------

    [Fact]
    public async Task UnMovimientoConIdMovimientoActualizacionInexistenteViolaLaFkDeActualizacion()
    {
        var p = await SembrarPrerequisitosAsync(nameof(UnMovimientoConIdMovimientoActualizacionInexistenteViolaLaFkDeActualizacion));

        await using var cruda = await fixture.AbrirConexionCrudaAsync("tenant", p.IdTenant);
        await using var comando = cruda.CreateCommand();
        comando.CommandText =
            "INSERT INTO movimientos_cuenta_corriente (id_tenant, id_cliente, fecha, id_punto_venta, " +
            "id_empleado, tipo, importe, saldo_resultante, id_movimiento_actualizacion) " +
            "VALUES ($1, $2, now(), $3, $4, 'consumo', 10, 10, 999999)";
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdTenant });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdCliente });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdPuntoVenta });
        comando.Parameters.Add(new NpgsqlParameter { Value = p.IdEmpleado });

        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());
        Assert.Equal("23503", excepcion.SqlState);
        Assert.Equal("fk_movimientos_cuenta_corriente_actualizacion", excepcion.ConstraintName);
    }

    // Pin directo del guard AND EXISTS del seed de RC: en una base fresca la migración no debe
    // insertar nada (el seeder ve la tabla vacía y siembra el catálogo completo, RC incluido).
    // Sin este pin, quitar el guard solo se detectaba por fallas colaterales en otra suite.
    //
    // stage-8-compras-transferencias-inventario (Slice 1, task 1.14): el total pasa de 11 a 14 —
    // mismo motivo que RC en su momento: TiposComprobanteBase ahora también incluye C-FA/C-FB/
    // C-FC (design: Table Shapes — E), y el mismo guard AND EXISTS del seed de compra deja una
    // base fresca intacta para que este seeder la puebla completa y atómica.
    [Fact]
    public async Task UnaBaseFrescaTerminaConElCatalogoCompletoDeTiposIncluidoRc()
    {
        // El seeder corre en el arranque del host: hay que bootearlo antes de mirar el catálogo.
        using var cliente = fixture.CreateClient();

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var codigos = await db.TiposComprobante.Select(t => t.Codigo).OrderBy(c => c).ToListAsync();

        Assert.Equal(14, codigos.Count);
        Assert.Contains("RC", codigos);
        Assert.Contains("FA", codigos);
        Assert.Contains("TX", codigos);
        Assert.Contains("C-FA", codigos);
        Assert.Contains("C-FB", codigos);
        Assert.Contains("C-FC", codigos);
    }

    // ---- RC idempotente en una base ya migrada desde stage 6 (task 1.9) ----------------------

    [Fact]
    public async Task RcResuelveEnUnaBaseYaMigradaDesdeStage6SinDuplicar()
    {
        var nombreBase = $"ways_stage6_{Guid.NewGuid():N}";
        var cadenaAdmin = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = "postgres" }.ConnectionString;
        var cadenaNueva = new NpgsqlConnectionStringBuilder(fixture.OwnerConnectionString) { Database = nombreBase }.ConnectionString;

        await using (var admin = new NpgsqlConnection(cadenaAdmin))
        {
            await admin.OpenAsync();
            await using var crear = admin.CreateCommand();
            crear.CommandText = $"CREATE DATABASE \"{nombreBase}\"";
            await crear.ExecuteNonQueryAsync();
        }

        try
        {
            var opciones = new DbContextOptionsBuilder<WaysDbContext>()
                .UseNpgsql(cadenaNueva, npgsql =>
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
                    // stage-15-cc-proveedores-ledger, Slice 2 (hallazgo registrado en tasks.md,
                    // mismo gap que ComprasTipoSeedTests/ComprasAnulacionYConcurrenciaTests):
                    // migrar hasta HEAD sin este mapeo dispara PendingModelChangesWarning.
                    npgsql.MapEnum<Ways.Domain.CuentaCorriente.TipoMovimientoCcProveedor>("tipo_movimiento_cc_proveedor");
                    // stage-16-ordenes-de-compra, Slice 1 (mismo gap que la desviación de la
                    // etapa 15 documentada arriba): migrar hasta HEAD sin este mapeo dispara
                    // PendingModelChangesWarning.
                    npgsql.MapEnum<Ways.Domain.Compras.EstadoOrdenCompra>("estado_orden_compra");
                    npgsql.MapEnum<EstadoPresupuesto>("estado_presupuesto");
                    npgsql.MapEnum<EstadoRemito>("estado_remito");
                })
                .Options;

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionStage6);
            }

            // Simula el catálogo de una base real que ya pasó por InicializadorDeBaseDeDatos
            // ANTES de que RC existiera — el guard de :417 solo siembra si la tabla está vacía.
            await using (var conexion = new NpgsqlConnection(cadenaNueva))
            {
                await conexion.OpenAsync();
                await using var comando = conexion.CreateCommand();
                comando.CommandText =
                    "INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, " +
                    "es_fiscal, afecta_stock, activo, created_at, updated_at) " +
                    "VALUES ('venta', 'TX', 'Ticket X', 'X', 1, false, false, true, true, now(), now())";
                await comando.ExecuteNonQueryAsync();
            }

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(); // aplica CuentaCorrienteEtapa7, la única pendiente
            }

            await using var verificacion = new NpgsqlConnection(cadenaNueva);
            await verificacion.OpenAsync();

            async Task<int> ContarRcAsync()
            {
                await using var comando = verificacion.CreateCommand();
                comando.CommandText = "SELECT count(*) FROM tipos_comprobante WHERE codigo = 'RC'";
                return Convert.ToInt32(await comando.ExecuteScalarAsync());
            }

            Assert.Equal(1, await ContarRcAsync());

            // Re-ejecuta a mano el mismo INSERT idempotente de la migración (simula un reintento
            // de arranque) y confirma que sigue sin duplicar.
            await using (var comando = verificacion.CreateCommand())
            {
                comando.CommandText =
                    """
                    INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, es_fiscal, afecta_stock, activo, created_at, updated_at)
                    SELECT 'venta', 'RC', 'Recibo de cobranza', NULL, 1, false, false, false, true, now(), now()
                    WHERE NOT EXISTS (SELECT 1 FROM tipos_comprobante WHERE codigo = 'RC');
                    """;
                await comando.ExecuteNonQueryAsync();
            }

            Assert.Equal(1, await ContarRcAsync());
        }
        finally
        {
            await using var admin = new NpgsqlConnection(cadenaAdmin);
            await admin.OpenAsync();
            await using var dropear = admin.CreateCommand();
            dropear.CommandText = $"DROP DATABASE IF EXISTS \"{nombreBase}\" WITH (FORCE)";
            await dropear.ExecuteNonQueryAsync();
        }
    }
}
