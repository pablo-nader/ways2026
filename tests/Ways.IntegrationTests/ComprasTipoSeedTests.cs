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
/// stage-8-compras-transferencias-inventario, Slice 1 (task 1.14, spec: comprobantes-compra /
/// Compra-Clase Tipos Are Platform-Seeded; GATE condition (ii)): mismo patrón que
/// <c>CuentaCorrienteEtapa7BackstopTests</c> — prueba el seed dual-path en DOS bases distintas
/// en vez de asumirlo:
///
/// (1) una base fresca, donde el seeder de <c>InicializadorDeBaseDeDatos</c> puebla el catálogo
/// completo atómicamente (los tres <c>C-*</c> incluidos vía <c>TiposComprobanteBase</c>);
///
/// (2) una base migrada desde stage 7 con un catálogo pre-existente (tabla NO vacía) — el
/// escenario real que el guard <c>AND EXISTS</c> de la migración <c>ComprasYTransferenciasEtapa8</c>
/// existe para cubrir.
///
/// Las dos pruebas también fijan que los ONCE códigos de venta preexistentes quedan
/// <c>Clase = venta</c> sin tocar (GATE condition (ii): <c>ux_tipos_comprobante_codigo</c> es
/// UNIQUE sobre <c>codigo</c> SOLO, así que un choque de código real rompería el seed entero —
/// su ausencia es la prueba de que el prefijo <c>C-</c> cumple su propósito).
/// </summary>
[Collection("Ways.IntegrationTests secuencial")]
public class ComprasTipoSeedTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    private const string MigracionStage7 = "20260805050151_CuentaCorrienteEtapa7";

    private static readonly string[] CodigosDeVentaEsperados =
        ["FA", "FB", "FC", "NCA", "NCB", "NCC", "NDA", "TX", "NCX", "PRE", "RC"];

    private static readonly string[] CodigosDeCompraEsperados = ["C-FA", "C-FB", "C-FC"];

    [Fact]
    public async Task UnaBaseFrescaSiembraLosTresTiposDeCompraSinTocarElCatalogoDeVenta()
    {
        using var cliente = fixture.CreateClient(); // arranca el host: siembra el catálogo completo

        await using var db = fixture.CrearContextoDeAplicacion(TenantActualFijo.Plataforma);

        var venta = await db.TiposComprobante
            .Where(t => t.Clase == ClaseComprobante.Venta)
            .Select(t => t.Codigo)
            .OrderBy(c => c)
            .ToListAsync();

        var compra = await db.TiposComprobante
            .Where(t => t.Clase == ClaseComprobante.Compra)
            .Select(t => t.Codigo)
            .OrderBy(c => c)
            .ToListAsync();

        Assert.Equal(CodigosDeVentaEsperados.OrderBy(c => c), venta);
        Assert.Equal(CodigosDeCompraEsperados.OrderBy(c => c), compra);

        var todosActivos = await db.TiposComprobante.AllAsync(t => t.Activo);
        Assert.True(todosActivos);
    }

    [Fact]
    public async Task LosTiposDeCompraAterrizanEnUnaBaseYaMigradaDesdeStage7SinDuplicarYSinTocarVenta()
    {
        var nombreBase = $"ways_stage7_{Guid.NewGuid():N}";
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
                    // stage-15-cc-proveedores-ledger, Slice 2 (hallazgo registrado en tasks.md):
                    // sin este mapeo, migrar hasta HEAD dispara PendingModelChangesWarning — el
                    // modelo vivo de este contexto manualmente curado diverge del snapshot real
                    // (que sí conoce tipo_movimiento_cc_proveedor desde slice 1).
                    npgsql.MapEnum<Ways.Domain.CuentaCorriente.TipoMovimientoCcProveedor>("tipo_movimiento_cc_proveedor");
                    // stage-16-ordenes-de-compra, Slice 1: mismo gap, ahora con
                    // estado_orden_compra (slice 1).
                    npgsql.MapEnum<Ways.Domain.Compras.EstadoOrdenCompra>("estado_orden_compra");
                })
                .Options;

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(MigracionStage7);
            }

            // Simula el catálogo de una base real ya operando en stage 7 — los once códigos de
            // venta, ANTES de que la migración de stage 8 exista.
            await using (var conexion = new NpgsqlConnection(cadenaNueva))
            {
                await conexion.OpenAsync();

                foreach (var codigo in CodigosDeVentaEsperados)
                {
                    await using var comando = conexion.CreateCommand();
                    comando.CommandText =
                        "INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, " +
                        "es_fiscal, afecta_stock, activo, created_at, updated_at) " +
                        "VALUES ('venta', $1, $1, NULL, 1, false, false, true, true, now(), now())";
                    comando.Parameters.Add(new NpgsqlParameter { Value = codigo });
                    await comando.ExecuteNonQueryAsync();
                }
            }

            await using (var db = new WaysDbContext(opciones, TenantActualFijo.Plataforma))
            {
                var migrador = db.Database.GetInfrastructure().GetRequiredService<IMigrator>();
                await migrador.MigrateAsync(); // aplica ComprasYTransferenciasEtapa8, la única pendiente
            }

            await using var verificacion = new NpgsqlConnection(cadenaNueva);
            await verificacion.OpenAsync();

            async Task<List<string>> ListarCodigosAsync(string clase)
            {
                await using var comando = verificacion.CreateCommand();
                comando.CommandText = "SELECT codigo FROM tipos_comprobante WHERE clase = $1::clase_comprobante ORDER BY codigo";
                comando.Parameters.Add(new NpgsqlParameter { Value = clase });
                var resultado = new List<string>();
                await using var lector = await comando.ExecuteReaderAsync();
                while (await lector.ReadAsync())
                {
                    resultado.Add(lector.GetString(0));
                }
                return resultado;
            }

            Assert.Equal(CodigosDeCompraEsperados.OrderBy(c => c), await ListarCodigosAsync("compra"));
            Assert.Equal(CodigosDeVentaEsperados.OrderBy(c => c), await ListarCodigosAsync("venta"));

            // Re-ejecuta a mano el mismo INSERT idempotente de la migración (simula un reintento
            // de arranque) y confirma que sigue sin duplicar.
            await using (var comando = verificacion.CreateCommand())
            {
                comando.CommandText =
                    """
                    INSERT INTO tipos_comprobante (clase, codigo, nombre, letra, signo, discrimina_iva, es_fiscal, afecta_stock, activo, created_at, updated_at)
                    SELECT 'compra', v.codigo, v.nombre, v.letra, v.signo, v.discrimina_iva, false, true, true, now(), now()
                    FROM (VALUES
                        ('C-FA', 'Factura A de compra', 'A', 1::smallint, true),
                        ('C-FB', 'Factura B de compra', 'B', 1::smallint, false),
                        ('C-FC', 'Factura C de compra', 'C', 1::smallint, false)
                    ) AS v(codigo, nombre, letra, signo, discrimina_iva)
                    WHERE EXISTS (SELECT 1 FROM tipos_comprobante)
                      AND NOT EXISTS (SELECT 1 FROM tipos_comprobante WHERE codigo = v.codigo);
                    """;
                await comando.ExecuteNonQueryAsync();
            }

            Assert.Equal(CodigosDeCompraEsperados.OrderBy(c => c), await ListarCodigosAsync("compra"));
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
