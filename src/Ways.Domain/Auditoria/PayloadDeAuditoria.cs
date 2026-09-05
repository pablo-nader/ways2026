using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;

namespace Ways.Domain.Auditoria;

/// <summary>
/// Una fábrica por acción (design decisión 5) — NINGUNA acepta una entidad: la defensa contra un
/// dump de fila es de TIPOS, no de convención (un <c>Usuario</c>/<c>ComprobanteVenta</c> completo
/// no puede llegar acá ni por accidente). El resultado es la tupla que
/// <see cref="RegistroDeAuditoria"/> valida en su constructor.
///
/// <c>dto-contract-honesty</c>: cada fábrica documenta su lista de campos contra la tabla de
/// payloads del proposal (decisión 2), con las dos correcciones de tasks.md ("Orchestrator
/// Decisions Recorded This Phase" #1 y #2) ya incorporadas — <see cref="Conteo"/> agrega
/// <c>movimientos_generados</c>/<c>lotes_afectados</c>/<c>delta_total</c> en vez de un
/// <c>id_movimiento_stock</c> singular, y <see cref="BajaDeUsuario"/> usa
/// <c>{deleted_at, estado}</c> en vez de <c>{estado:"eliminado"}</c> (que no es un valor de
/// <c>EstadoUsuario</c>).
///
/// <see cref="BajaDeTenant"/> y <see cref="BajaDeOrganizacion"/> NO salen de esa tabla: las agregó
/// la etapa 20 slice 4 (judgment-day ronda 1, hallazgo C1 — la acción más destructiva del sistema
/// no dejaba ninguna fila en <c>GET /api/auditoria</c>), y siguen la forma de
/// <see cref="BajaDeUsuario"/> porque son la misma operación sobre otro nivel de la jerarquía.
/// </summary>
public static class PayloadDeAuditoria
{
    /// <summary><c>precio.cambio</c> — call site 1. <paramref name="montoAnterior"/>/
    /// <paramref name="vigenteDesdeAnterior"/> ambos <c>null</c> ⇒ primer precio (valorAnterior
    /// completo es <c>null</c>).</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        CambioDePrecio(
            int idListaPrecio, decimal? montoAnterior, DateTimeOffset? vigenteDesdeAnterior,
            decimal montoNuevo, DateTimeOffset vigenteDesdeNuevo)
    {
        IReadOnlyDictionary<string, object?>? anterior = montoAnterior is null && vigenteDesdeAnterior is null
            ? null
            : new Dictionary<string, object?>
            {
                ["id_lista_precio"] = idListaPrecio,
                ["monto"] = montoAnterior,
                ["vigente_desde"] = vigenteDesdeAnterior
            };

        var nuevo = new Dictionary<string, object?>
        {
            ["id_lista_precio"] = idListaPrecio,
            ["monto"] = montoNuevo,
            ["vigente_desde"] = vigenteDesdeNuevo
        };

        return (anterior, nuevo);
    }

    /// <summary><c>usuario.alta</c> — call site 2. Sin estado previo por definición.</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        AltaDeUsuario(string usuario, string mail, int idRol, EstadoUsuario estado) => (
            null,
            new Dictionary<string, object?>
            {
                ["usuario"] = usuario,
                ["mail"] = mail,
                ["id_rol"] = idRol,
                ["estado"] = estado
            });

    /// <summary><c>usuario.actualizacion</c> — call site 3. Valores pre/post-mutación de las
    /// cuatro columnas editables.</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        ActualizacionDeUsuario(
            string usuarioAnterior, string mailAnterior, int idRolAnterior, EstadoUsuario estadoAnterior,
            string usuarioNuevo, string mailNuevo, int idRolNuevo, EstadoUsuario estadoNuevo) => (
            new Dictionary<string, object?>
            {
                ["usuario"] = usuarioAnterior,
                ["mail"] = mailAnterior,
                ["id_rol"] = idRolAnterior,
                ["estado"] = estadoAnterior
            },
            new Dictionary<string, object?>
            {
                ["usuario"] = usuarioNuevo,
                ["mail"] = mailNuevo,
                ["id_rol"] = idRolNuevo,
                ["estado"] = estadoNuevo
            });

    /// <summary><c>usuario.baja</c> — call site 4. <c>{deleted_at, estado}</c> en los dos lados
    /// (tasks.md, Orchestrator Decision #2) — NUNCA <c>{estado:"eliminado"}</c>, que no es un
    /// valor de <c>EstadoUsuario</c>.</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        BajaDeUsuario(EstadoUsuario estado, DateTimeOffset momento) => (
            new Dictionary<string, object?> { ["deleted_at"] = null, ["estado"] = estado },
            new Dictionary<string, object?> { ["deleted_at"] = momento, ["estado"] = estado });

    /// <summary><c>usuario.desbloqueo</c> — call site 5. <paramref name="estadoAnterior"/> es el
    /// valor REAL leído antes de <c>Desbloquear</c> (no asumido "bloqueado" — el método corre
    /// igual aunque la cuenta ya esté activa).</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        DesbloqueoDeUsuario(EstadoUsuario estadoAnterior, EstadoUsuario estadoNuevo) => (
            new Dictionary<string, object?> { ["estado"] = estadoAnterior },
            new Dictionary<string, object?> { ["estado"] = estadoNuevo });

    /// <summary><c>usuario.password</c> — call site 6. Jamás el hash: solo el hecho de quién lo
    /// cambió.</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        CambioDePassword(bool porElPropioUsuario) => (
            null,
            new Dictionary<string, object?> { ["por_el_propio_usuario"] = porElPropioUsuario });

    /// <summary><c>venta.anulacion</c> — call site 7. <paramref name="estadoAnterior"/> viaja como
    /// la MISMA constante que liga el <c>WHERE</c> del <c>UPDATE</c> (design decisión 8) — el call
    /// site nunca hardcodea un literal.</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        AnulacionDeVenta(EstadoComprobante estadoAnterior, EstadoComprobante estadoNuevo) => (
            new Dictionary<string, object?> { ["estado"] = estadoAnterior },
            new Dictionary<string, object?> { ["estado"] = estadoNuevo });

    /// <summary><c>compra.anulacion</c> — call site 8. <paramref name="estadoAnterior"/> está
    /// garantizado por el <c>WHERE</c> del <c>UPDATE</c> de <c>MarcarAnuladaAsync</c>.</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        AnulacionDeCompra(EstadoCompra estadoAnterior, EstadoCompra estadoNuevo) => (
            new Dictionary<string, object?> { ["estado"] = estadoAnterior },
            new Dictionary<string, object?> { ["estado"] = estadoNuevo });

    /// <summary><c>stock.ajuste</c> — call site 9. <paramref name="cantidadAnterior"/> se deriva
    /// de <c>nueva − delta</c> (design decisión 9), nunca de un <c>SELECT</c> extra.</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        AjusteDeStock(decimal cantidadAnterior, decimal cantidadNueva, int idMovimientoStock, string? observaciones) => (
            new Dictionary<string, object?> { ["cantidad"] = cantidadAnterior },
            new Dictionary<string, object?>
            {
                ["cantidad"] = cantidadNueva,
                ["id_movimiento_stock"] = idMovimientoStock,
                ["observaciones"] = observaciones
            });

    /// <summary><c>stock.decomiso</c> — call site 10. <paramref name="idLote"/> <c>null</c> = no
    /// lote-efectivo.</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        DecomisoDeStock(
            decimal cantidadAnterior, decimal cantidadNueva, int idMovimientoStock, string? observaciones,
            int? idLote) => (
            new Dictionary<string, object?> { ["cantidad"] = cantidadAnterior },
            new Dictionary<string, object?>
            {
                ["cantidad"] = cantidadNueva,
                ["id_movimiento_stock"] = idMovimientoStock,
                ["observaciones"] = observaciones,
                ["id_lote"] = idLote
            });

    /// <summary><c>stock.conteo</c> — call site 11, per tasks.md Orchestrator Decision #1: UNA
    /// fila por OPERACIÓN de conteo (no por movimiento de ledger). <paramref name="movimientosGenerados"/>
    /// acumula los <c>id_movimiento_stock</c> de todos los lotes/agregado con diferencia,
    /// <paramref name="lotesAfectados"/> es su cantidad y <paramref name="deltaTotal"/> la suma de
    /// los deltas — reemplaza el <c>id_movimiento_stock</c> singular del proposal, que un solo
    /// escalar no puede representar honestamente cuando N &gt; 1.</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        Conteo(
            decimal cantidadAlInicio, decimal cantidadFinal, IReadOnlyList<int> movimientosGenerados,
            int lotesAfectados, decimal deltaTotal) => (
            new Dictionary<string, object?> { ["cantidad"] = cantidadAlInicio },
            new Dictionary<string, object?>
            {
                ["cantidad"] = cantidadFinal,
                ["movimientos_generados"] = movimientosGenerados,
                ["lotes_afectados"] = lotesAfectados,
                ["delta_total"] = deltaTotal
            });

    /// <summary><c>cc.reliquidacion</c> — call site 12. <paramref name="saldoAnterior"/> sale del
    /// <c>SELECT … FOR UPDATE</c> ya tomado, nunca de un re-read.</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        ReliquidacionDeCc(
            decimal saldoAnterior, decimal saldoNuevo, int idMovimiento, int consumosActualizados, decimal diferencia) => (
            new Dictionary<string, object?> { ["saldo"] = saldoAnterior },
            new Dictionary<string, object?>
            {
                ["saldo"] = saldoNuevo,
                ["id_movimiento"] = idMovimiento,
                ["consumos_actualizados"] = consumosActualizados,
                ["diferencia"] = diferencia
            });

    /// <summary><c>tenant.baja</c> — call site 13 (etapa 20 slice 4). Misma forma que
    /// <see cref="BajaDeUsuario"/>: <c>{deleted_at, estado}</c> en los dos lados. El tenant es el
    /// único de las tres bajas de organización que además cambia de estado, y el estado nuevo
    /// viaja como valor y no como literal — el call site es el único escritor de
    /// <c>EstadoTenant.Baja</c> y lo pasa desde la misma entidad que acaba de estampar.</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        BajaDeTenant(EstadoTenant estadoAnterior, EstadoTenant estadoNuevo, DateTimeOffset momento) => (
            new Dictionary<string, object?> { ["deleted_at"] = null, ["estado"] = estadoAnterior },
            new Dictionary<string, object?> { ["deleted_at"] = momento, ["estado"] = estadoNuevo });

    /// <summary><c>empresa.baja</c> y <c>pv.baja</c> — call sites 14 y 15 (etapa 20 slice 4). Ni
    /// <c>empresas</c> ni <c>puntos_venta</c> tienen columna de estado, así que la baja es
    /// exactamente <c>deleted_at</c>. <paramref name="porCascada"/> es lo que distingue la baja
    /// que pidió el operador de la que arrastró la baja de su padre: sin ese campo el rastro no
    /// puede decir por qué cayó la fila, y las dos comparten instante justamente porque son la
    /// misma transacción.</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        BajaDeOrganizacion(DateTimeOffset momento, bool porCascada) => (
            new Dictionary<string, object?> { ["deleted_at"] = null },
            new Dictionary<string, object?> { ["deleted_at"] = momento, ["por_cascada"] = porCascada });

    /// <summary><c>usuario.baja</c> escrita por la CASCADA del tenant (etapa 20 slice 4,
    /// judgment-day ronda 2, hallazgo R2-8). Es <see cref="BajaDeUsuario"/> más el
    /// <c>por_cascada</c> que ya llevan <c>empresa.baja</c> y <c>pv.baja</c>: sin ese campo, la
    /// única de las cuatro filas de la cascada que no podía decir por qué cayó era justamente la
    /// de la cuenta de una persona. Constante <c>true</c> y no parámetro a propósito — el camino
    /// DIRECTO (<c>ServicioDeUsuarios.EliminarAsync</c>) sigue usando <see cref="BajaDeUsuario"/>,
    /// así que esta fábrica tiene un solo llamador y no admite mentir sobre su origen.</summary>
    public static (IReadOnlyDictionary<string, object?>? Anterior, IReadOnlyDictionary<string, object?> Nuevo)
        BajaDeUsuarioPorCascada(EstadoUsuario estado, DateTimeOffset momento) => (
            new Dictionary<string, object?> { ["deleted_at"] = null, ["estado"] = estado },
            new Dictionary<string, object?>
            {
                ["deleted_at"] = momento,
                ["estado"] = estado,
                ["por_cascada"] = true
            });
}
