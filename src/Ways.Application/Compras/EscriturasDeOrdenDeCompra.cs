using System.Data.Common;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.Application.Compras;

/// <summary>
/// stage-16-ordenes-de-compra, Slice 3 (design decisiones 1-2, Interfaces/Contracts — "Application
/// — the one projection authority"). Copia estructural de
/// <see cref="Ways.Application.CuentaCorriente.EscriturasDeCuentaCorrienteProveedor"/>: <c>static</c>,
/// misma postura de conexión/transacción del llamador (nunca abre/flushea/comitea nada), todos los
/// parámetros por <see cref="ParametrosDeComando"/>. La ÚNICA clase que escribe
/// <c>ordenes_compra.estado</c> a partir del libro de recepción — llamada desde
/// <c>ServicioDeCompras.EjecutarConfirmarAsync</c> y <c>EjecutarAnulacionAsync</c>, nunca desde
/// <c>ServicioDeOrdenesDeCompra</c> directamente (design decisión 1: la contención es el producto —
/// un DI seam acá solo invitaría a una segunda implementación, y <c>ServicioDeCompras</c> no puede
/// depender de <c>ServicioDeOrdenesDeCompra</c> sin ciclo).
///
/// TRES statements como mínimo, nunca uno (design decisión 2): bajo READ COMMITTED, un
/// <c>UPDATE ... FROM (SELECT ...)</c> que bloquea en la fila de la OC re-evalúa SOLO la fila
/// lockeada (EvalPlanQual) cuando el ganador comitea — su subconsulta conserva el snapshot que
/// tenía al arrancar el statement. Dos confirmaciones concurrentes de la MISMA OC proyectarían
/// desde un libro viejo y la perdedora sobreescribiría el estado de la ganadora. El fix es el lock
/// primero (<c>SELECT ... FOR UPDATE</c>), la derivación en un statement SEPARADO (snapshot nuevo,
/// ve el commit del ganador) y recién después el <c>UPDATE ... RETURNING</c>.
/// </summary>
public static class EscriturasDeOrdenDeCompra
{
    private readonly record struct EstadoLockeado(string Estado, bool CierreManual);

    /// <summary>Estado + <see cref="ProyectarEstadoAsync"/>/<see cref="BloquearYExigirNoAnuladaAsync"/>
    /// comparten esta lectura crudo bajo lock — el ÚNICO <c>SELECT ... FOR UPDATE</c> de esta clase
    /// (mutation target #18). 0 filas es un invariante roto (la FK
    /// <c>fk_comprobantes_compra_orden_compra</c> ya garantiza que la fila existe para este
    /// tenant): nunca un <see cref="ErrorDominio"/> — el 404 de "no existe la OC" vive aguas
    /// arriba, en <c>ExigirOrdenLigableAsync</c>, no acá.</summary>
    private static async Task<EstadoLockeado> BloquearYLeerAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idOrdenCompra, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "SELECT estado::text, (id_empleado_cierre IS NOT NULL) AS cierre_manual " +
            "FROM ordenes_compra WHERE id_orden_compra = $1 AND id_tenant = $2 FOR UPDATE";

        ParametrosDeComando.Agregar(comando, idOrdenCompra);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                $"La orden de compra {idOrdenCompra} no existe para el tenant {idTenant} — invariante de FK roto.");
        }

        return new EstadoLockeado(lector.GetString(0), lector.GetBoolean(1));
    }

    /// <summary>Statement 2 — la derivación, en su PROPIO statement (snapshot nuevo: ve el commit
    /// del ganador de una carrera). Agrupa por <c>id_articulo</c> en AMBOS lados (design decisión
    /// 3, proposal decisión 2: line-to-line es imposible, un artículo puede repetirse en cualquiera
    /// de las dos tablas) — <c>completa</c> exige que TODO artículo pedido esté cubierto;
    /// <c>algoRecibido</c> se toma del lado RECEPCIÓN, nunca del pedido (design decisión 3/T9: una
    /// entrega por sustitución — recibido no pedido — tiene que contar).</summary>
    private static async Task<(bool Completa, bool AlgoRecibido)> DerivarAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idOrdenCompra, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "WITH pedido AS (" +
            "    SELECT i.id_articulo, SUM(i.cantidad_pedida) AS pedida " +
            "    FROM items_orden_compra i " +
            "    WHERE i.id_orden_compra = $1 AND i.id_tenant = $2 AND i.deleted_at IS NULL " +
            "    GROUP BY i.id_articulo), " +
            "recibido AS (" +
            "    SELECT ic.id_articulo, SUM(ic.cantidad) AS recibida " +
            "    FROM items_comprobante_compra ic " +
            "    JOIN comprobantes_compra c " +
            "      ON c.id_comprobante_compra = ic.id_comprobante_compra AND c.id_tenant = ic.id_tenant " +
            "    WHERE c.id_orden_compra = $1 AND c.id_tenant = $2 " +
            "      AND c.estado = 'confirmada'::estado_compra " +
            "      AND c.deleted_at IS NULL AND ic.deleted_at IS NULL " +
            "    GROUP BY ic.id_articulo) " +
            "SELECT " +
            "    NOT EXISTS (SELECT 1 FROM pedido p " +
            "                LEFT JOIN recibido r ON r.id_articulo = p.id_articulo " +
            "                WHERE p.pedida > COALESCE(r.recibida, 0)) AS completa, " +
            "    COALESCE((SELECT SUM(recibida) FROM recibido), 0) > 0 AS algo_recibido";

        ParametrosDeComando.Agregar(comando, idOrdenCompra);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        await lector.ReadAsync(ct);
        return (lector.GetBoolean(0), lector.GetBoolean(1));
    }

    /// <summary>Statement 3 — la ÚNICA autoridad de transición de <c>ordenes_compra.estado</c>.
    /// <c>fecha_cierre</c> se limpia a NULL en cualquier destino que no sea <c>cerrada</c> — la
    /// regresión (cerrada→recibida_parcial/enviada) queda LIMPIA en el mismo statement (design
    /// decisión 5, mutation target #28); nunca toca <c>id_empleado_cierre</c> (el cierre manual ya
    /// cortó antes de llegar acá).</summary>
    private static async Task<EstadoOrdenCompra> ActualizarAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idOrdenCompra, DateTimeOffset momento,
        EstadoOrdenCompra estadoAnterior, EstadoOrdenCompra nuevoEstado, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "UPDATE ordenes_compra " +
            "SET estado = $3::estado_orden_compra, " +
            "    fecha_cierre = CASE WHEN $3 = 'cerrada' THEN $4 ELSE NULL END, " +
            "    updated_at = $4 " +
            "WHERE id_orden_compra = $1 AND id_tenant = $2 AND estado = $5::estado_orden_compra " +
            "RETURNING estado::text";

        ParametrosDeComando.Agregar(comando, idOrdenCompra);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, nuevoEstado);
        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, estadoAnterior);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException(
                $"La proyección de la orden de compra {idOrdenCompra} no afectó ninguna fila bajo el lock ya tomado.");

        return ParsearEstado((string)resultado);
    }

    /// <summary>Lock → cortocircuito → derivación → UPDATE condicional (design decisión 2). Llamada
    /// desde AMBOS caminos de escritura (confirmar y anular) — es la ÚNICA autoridad que muta
    /// <c>ordenes_compra.estado</c> desde el libro de recepción. Los cortocircuitos NO son una
    /// optimización: <c>anulada</c> terminal (mutation target #27) y el cierre MANUAL nunca
    /// revisitado (mutation target #26) son los dos hechos que el libro jamás puede pisar (design
    /// decisión 9/5, proposal decisión 3) — expresarlos como early-return BAJO EL LOCK los hace
    /// imposibles de esquivar, no una rama de <c>CASE</c> que alguien podría ensanchar. Saltear el
    /// UPDATE no-op hace la idempotencia observable: una re-proyección que no cambia nada no emite
    /// statement 3 (mutation target #19: si la derivación se pliega en un solo
    /// <c>UPDATE ... FROM (SELECT ...)</c>, la carrera de confirm×confirm vuelve a ser posible —
    /// ver el doc-comment de la clase).</summary>
    public static async Task<EstadoOrdenCompra> ProyectarEstadoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idOrdenCompra,
        DateTimeOffset momento, CancellationToken ct)
    {
        var lockeado = await BloquearYLeerAsync(conexion, transaccion, idTenant, idOrdenCompra, ct);
        var estadoActual = ParsearEstado(lockeado.Estado);

        // El lock de statement 1 ya está tomado — los cortocircuitos cortan ACÁ, sin statement 2 ni
        // 3 (mutation targets #26/#27).
        if (estadoActual == EstadoOrdenCompra.Anulada || lockeado.CierreManual)
        {
            return estadoActual;
        }

        var (completa, algoRecibido) = await DerivarAsync(conexion, transaccion, idTenant, idOrdenCompra, ct);
        var nuevoEstado = ProyectorDeEstadoDeOrden.Proyectar(estadoActual, lockeado.CierreManual, completa, algoRecibido);

        if (nuevoEstado == estadoActual)
        {
            // Idempotencia observable: cero statements extra cuando la proyección no cambia nada.
            return estadoActual;
        }

        return await ActualizarAsync(conexion, transaccion, idTenant, idOrdenCompra, momento, estadoActual, nuevoEstado, ct);
    }

    /// <summary>Guard de defensa en profundidad del camino de confirmación (design decisión 9,
    /// spec ordenes-de-compra: "Anulación Is Governed By The Book... independently, confirming a
    /// comprobante whose linked OC is anulada MUST be refused"). Toma el MISMO statement 1
    /// (<see cref="BloquearYLeerAsync"/>, compartido — mutation target #18 aplica igual acá) y
    /// rechaza ANTES de que <see cref="ProyectarEstadoAsync"/> corra — expuesta aparte para que
    /// <c>EjecutarConfirmarAsync</c> no tenga que interpretar el estado devuelto, solo llamarla y
    /// dejar que tire.</summary>
    public static async Task<EstadoOrdenCompra> BloquearYExigirNoAnuladaAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idOrdenCompra, CancellationToken ct)
    {
        var lockeado = await BloquearYLeerAsync(conexion, transaccion, idTenant, idOrdenCompra, ct);
        if (lockeado.Estado == "anulada")
        {
            throw new ErrorDominio("orden_compra_anulada", "La orden de compra ligada está anulada.", 409);
        }

        return ParsearEstado(lockeado.Estado);
    }

    private static EstadoOrdenCompra ParsearEstado(string estado) => estado switch
    {
        "borrador" => EstadoOrdenCompra.Borrador,
        "enviada" => EstadoOrdenCompra.Enviada,
        "recibida_parcial" => EstadoOrdenCompra.RecibidaParcial,
        "cerrada" => EstadoOrdenCompra.Cerrada,
        "anulada" => EstadoOrdenCompra.Anulada,
        _ => throw new InvalidOperationException($"Estado de orden de compra desconocido: '{estado}'.")
    };
}
