using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// fix(raw-ado): mismo patrón unit-style que <see cref="ManejadorDeErroresVentasTests"/>/
/// <see cref="ManejadorDeErroresComprasTests"/>/<see cref="ManejadorDeErroresLotesTests"/> — sin
/// <c>WaysApiFixture</c> ni Postgres real, la <see cref="PostgresException"/> se construye "a
/// mano". A diferencia de esos tres archivos (que siempre la envuelven en <c>DbUpdateException</c>,
/// el shape que produce <c>SaveChangesAsync</c>), acá se pasa PELADA — el shape que producen los
/// statements crudos de <c>ServicioDeVentas</c>/<c>ServicioDeCompras</c>/<c>ServicioDeStock</c>/
/// <c>ServicioDeLotes</c> (<c>conexion.CreateCommand()</c>, nunca <c>SaveChangesAsync</c>).
///
/// Antes de este fix, <c>ManejadorDeErrores</c> solo matcheaba
/// <c>DbUpdateException {{ InnerException: PostgresException }}</c> — una <c>PostgresException</c>
/// pelada caía directo al catch-all y salía como 500 <c>error_interno</c>, sin importar qué
/// constraint la disparó. Detectado dos veces en el judgment-day de la etapa 12:
/// <c>ck_movimientos_stock_cantidad_no_cero</c> (test <see cref="CkMovimientosStockCantidadNoCeroCrudaSeTraduceA400"/>)
/// y <c>fk_stock_lotes_lote</c> (test <see cref="FkStockLotesLoteCrudaSeTraduceA400"/>).
///
/// Honestidad de alcanzabilidad (db-error-backstops): bajo operación normal, los CUATRO servicios
/// de escritura raw-ADO de esta etapa validan sus referencias/cantidades ANTES de emitir el
/// statement crudo, y todo upsert sobre una columna con índice único usa
/// <c>INSERT ... ON CONFLICT DO UPDATE</c> (nunca un <c>INSERT</c> plano) — así que, HOY, ninguna
/// de las tres familias de esta clase es alcanzable por un request HTTP concurrente legítimo; las
/// tres son backstops de esquema puro (una escritura cruda/fuera de banda, o un guard de servicio
/// que se rompe en un cambio futuro — exactamente lo que <c>ReconciliacionTests</c>
/// documentó como evidencia de mutación para <c>ck_movimientos_stock_cantidad_no_cero</c>: borrar
/// el guard `residuo == 0` hace que <c>ServicioDeLotes.ReconciliarParAsync</c> SÍ la dispare). Por
/// eso las tres pruebas de abajo invocan <see cref="ManejadorDeErrores.TryHandleAsync"/>
/// directamente en vez de un round-trip HTTP — mismo criterio de exención documentada que
/// <c>pk_stock</c>/<c>pk_numeraciones_comprobante</c> en <see cref="ManejadorDeErrores"/>.
/// </summary>
public class ManejadorDeErroresRawAdoTests
{
    private sealed class ServicioDeProblemDetailsFalso : IProblemDetailsService
    {
        public ProblemDetailsContext? Ultimo { get; private set; }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Ultimo = context;
            return ValueTask.FromResult(true);
        }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Ultimo = context;
            return ValueTask.CompletedTask;
        }
    }

    private static PostgresException CrearExcepcion(string sqlState, string? constraintName) =>
        new(
            messageText: "mensaje de prueba",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: sqlState,
            detail: null,
            hint: null,
            position: 0,
            internalPosition: 0,
            internalQuery: null,
            where: null,
            schemaName: null,
            tableName: null,
            columnName: null,
            dataTypeName: null,
            constraintName: constraintName,
            file: null,
            line: null,
            routine: null);

    private static async Task<(int Estado, string? Codigo)> ManejarAsync(Exception excepcion)
    {
        var servicioDeProblemDetails = new ServicioDeProblemDetailsFalso();
        var manejador = new ManejadorDeErrores(servicioDeProblemDetails, NullLogger<ManejadorDeErrores>.Instance);
        var contexto = new DefaultHttpContext();

        var manejado = await manejador.TryHandleAsync(contexto, excepcion, CancellationToken.None);

        Assert.True(manejado);
        Assert.NotNull(servicioDeProblemDetails.Ultimo);

        var problema = servicioDeProblemDetails.Ultimo!.ProblemDetails;
        return (contexto.Response.StatusCode, problema.Extensions["codigo"] as string);
    }

    // ---- familia 23505 (unicidad) — pk_stock, crudo -------------------------------------------

    /// <summary>El único escritor de <c>stock</c> (<c>ServicioDeStock</c>/<c>ServicioDeCompras</c>/
    /// <c>ServicioDeVentas</c>) usa <c>INSERT ... ON CONFLICT (id_articulo, id_punto_venta) DO
    /// UPDATE</c> — nunca puede disparar <c>pk_stock</c> por construcción (mismo criterio que el
    /// doc-comment de esa rama en <c>ManejadorDeErrores</c>). Esta prueba no afirma que el camino
    /// HTTP la alcance: prueba que, SI un <c>INSERT</c> crudo fuera de banda (o un bug futuro que
    /// reemplace el <c>ON CONFLICT</c> por un <c>INSERT</c> plano) la dispara, la excepción PELADA
    /// que Npgsql tira en ese statement crudo ahora se traduce igual que su equivalente envuelto en
    /// <c>DbUpdateException</c> (ya cubierto en <see cref="ManejadorDeErroresVentasTests.PkStockSeTraduceA409StockDuplicado"/>).</summary>
    [Fact]
    public async Task PkStockCrudaSeTraduceA409StockDuplicado()
    {
        var postgres = CrearExcepcion("23505", "pk_stock");
        var (estado, codigo) = await ManejarAsync(postgres);

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("stock_duplicado", codigo);
    }

    // ---- familia 23514 (CHECK) — ck_movimientos_stock_cantidad_no_cero, crudo -----------------

    /// <summary>Judgment-day etapa 12, incidente 1: <c>InsertarMovimientoStockAsync</c> (raw-ADO,
    /// compartido por <c>ServicioDeVentas</c>/<c>ServicioDeCompras</c>/<c>ServicioDeStock</c>) es un
    /// <c>INSERT</c> plano sin <c>ON CONFLICT</c> — si algún llamador emite <c>cantidad = 0</c>
    /// (guard de servicio roto, p.ej. el mutation-target que <c>ReconciliacionTests</c> documenta
    /// para <c>ServicioDeLotes.ReconciliarParAsync</c>), Npgsql tira la <c>PostgresException</c>
    /// pelada de esta CHECK. Antes de este fix, esa excepción caía al catch-all como 500
    /// <c>error_interno</c> — el bug real que motivó el fix.</summary>
    [Fact]
    public async Task CkMovimientosStockCantidadNoCeroCrudaSeTraduceA400()
    {
        var postgres = CrearExcepcion("23514", "ck_movimientos_stock_cantidad_no_cero");
        var (estado, codigo) = await ManejarAsync(postgres);

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("movimiento_de_stock_sin_cantidad", codigo);
    }

    // ---- familia 23503 (FK) — fk_stock_lotes_lote, crudo ---------------------------------------

    /// <summary>Judgment-day etapa 12, incidente 2: <c>UpsertStockLoteAsync</c> (raw-ADO, mismo
    /// shape en <c>ServicioDeVentas</c>/<c>ServicioDeCompras</c>/<c>ServicioDeStock</c>/
    /// <c>ServicioDeLotes</c>) referencia <c>id_lote</c> por FK; <c>LotesBackstopTests</c> ya prueba
    /// que un INSERT crudo fuera de banda dispara <c>23503</c>/<c>fk_stock_lotes_lote</c> a nivel de
    /// esquema (SQLSTATE + ConstraintName), pero esa prueba no pasa por
    /// <see cref="ManejadorDeErrores"/> (no hay pipeline HTTP en ese test). Acá se prueba la mitad
    /// que faltaba: que ESA MISMA excepción pelada, cuando SÍ llega al exception handler (p.ej. si
    /// algún camino futuro deja de pre-validar el lote antes de la transacción), se traduce igual
    /// que el match genérico por prefijo "fk_" ya prueba para el camino EF.</summary>
    [Fact]
    public async Task FkStockLotesLoteCrudaSeTraduceA400()
    {
        var postgres = CrearExcepcion("23503", "fk_stock_lotes_lote");
        var (estado, codigo) = await ManejarAsync(postgres);

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("referencia_invalida", codigo);
    }

    // ---- regresión: una PostgresException pelada sin constraint mapeada sigue siendo 500 ------

    /// <summary>Cierra el arco opuesto: el brazo nuevo (<c>PostgresException pgCruda when
    /// ClasificarPostgresException(...) is {{ }} ...</c>) no puede convertirse en un catch-all
    /// silencioso — un SQLSTATE/ConstraintName que <c>ClasificarPostgresException</c> no reconoce
    /// tiene que seguir cayendo al 500 <c>error_interno</c> genérico, igual que antes del fix.</summary>
    [Fact]
    public async Task UnaPostgresExceptionCrudaSinConstraintMapeadaSigueSiendo500ErrorInterno()
    {
        var postgres = CrearExcepcion("XX000", "una_constraint_que_no_existe");
        var (estado, codigo) = await ManejarAsync(postgres);

        Assert.Equal(StatusCodes.Status500InternalServerError, estado);
        Assert.Equal("error_interno", codigo);
    }
}
