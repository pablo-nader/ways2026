namespace Ways.Domain.Auditoria;

/// <summary>
/// El catálogo de acciones auditadas (design decisión 4): un <c>sealed record</c> fija el PAR
/// <c>(Accion, Entidad)</c>, no solo el verbo — con argumentos sueltos un call site podría
/// emparejar <c>precio.cambio</c> con <c>entidad = "usuario"</c> y nada lo notaría. La base de
/// datos NO valida <see cref="Accion"/>/<see cref="Entidad"/> contra este catálogo (proposal
/// decisión 8, `accion text` + CHECK de no-vacío): la aplicación es la parte estricta.
///
/// <c>dto-contract-honesty</c>: cada constante documenta el par exacto que su call site (design,
/// tabla "Call sites") tiene permitido escribir — la convención del repo es usar siempre una de
/// estas 12 instancias; un call site nuevo que necesite una acción no listada tiene que agregarla
/// acá primero, nunca improvisar un <c>new AccionAuditada(...)</c> inline. El <c>record</c>
/// posicional público SÍ genera un constructor público (<c>new AccionAuditada("x", "y")</c>
/// compila): nada en el tipo lo impide, y la membresía al catálogo no se valida en runtime
/// (design decisión 15 — una acción retirada deja filas consultables cuyo <c>accion</c> ya no
/// tiene entrada acá, y eso es intencional). La garantía de "solo estas 12" es de convención +
/// test (<see cref="Ways.Domain.Tests.Auditoria.AccionAuditadaTests"/> congela el catálogo
/// exacto), no del tipo.
/// </summary>
public sealed record AccionAuditada(string Accion, string Entidad)
{
    /// <summary>Slice 2 — <c>Precios/ServicioDePrecios.cs</c>.</summary>
    public static readonly AccionAuditada PrecioCambio = new("precio.cambio", "articulo");

    /// <summary>Slice 3 — <c>Ventas/ServicioDeVentas.cs</c>.</summary>
    public static readonly AccionAuditada VentaAnulacion = new("venta.anulacion", "comprobante_venta");

    /// <summary>Slice 3 — <c>Compras/ServicioDeCompras.cs</c>.</summary>
    public static readonly AccionAuditada CompraAnulacion = new("compra.anulacion", "comprobante_compra");

    /// <summary>Slice 4 — <c>Stock/ServicioDeStock.cs</c>.</summary>
    public static readonly AccionAuditada StockAjuste = new("stock.ajuste", "articulo");

    /// <summary>Slice 4 — <c>Stock/ServicioDeStock.cs</c>.</summary>
    public static readonly AccionAuditada StockDecomiso = new("stock.decomiso", "articulo");

    /// <summary>Slice 4 — <c>Stock/ServicioDeStock.cs</c>. Una fila por OPERACIÓN de conteo, no
    /// por movimiento de ledger escrito (tasks.md, Orchestrator Decision #1).</summary>
    public static readonly AccionAuditada StockConteo = new("stock.conteo", "articulo");

    /// <summary>Slice 4 — <c>CuentaCorriente/ServicioDeReliquidacion.cs</c>.</summary>
    public static readonly AccionAuditada CcReliquidacion = new("cc.reliquidacion", "cliente");

    /// <summary>Slice 2 — <c>Usuarios/ServicioDeUsuarios.cs</c>, <c>CrearAsync</c>.</summary>
    public static readonly AccionAuditada UsuarioAlta = new("usuario.alta", "usuario");

    /// <summary>Slice 2 — <c>Usuarios/ServicioDeUsuarios.cs</c>, <c>ActualizarAsync</c>.</summary>
    public static readonly AccionAuditada UsuarioActualizacion = new("usuario.actualizacion", "usuario");

    /// <summary>Slice 2 — <c>Usuarios/ServicioDeUsuarios.cs</c>, <c>EliminarAsync</c> (baja
    /// lógica: <c>{deleted_at, estado}</c>, nunca <c>{estado:"eliminado"}</c> — tasks.md,
    /// Orchestrator Decision #2).</summary>
    public static readonly AccionAuditada UsuarioBaja = new("usuario.baja", "usuario");

    /// <summary>Slice 2 — <c>Usuarios/ServicioDeUsuarios.cs</c>, camino de desbloqueo.</summary>
    public static readonly AccionAuditada UsuarioDesbloqueo = new("usuario.desbloqueo", "usuario");

    /// <summary>Slice 2 — <c>Usuarios/ServicioDeUsuarios.cs</c>, camino de cambio de
    /// contraseña.</summary>
    public static readonly AccionAuditada UsuarioPassword = new("usuario.password", "usuario");

    /// <summary>Las 12 acciones de la primera pasada (proposal decisión 5) — usada por el
    /// catálogo genérico de tests (naming <c>&lt;dominio&gt;.&lt;operacion&gt;</c>, sin
    /// duplicados) y por cualquier consumidor que necesite iterarlas todas.</summary>
    public static readonly IReadOnlyList<AccionAuditada> Todas =
    [
        PrecioCambio,
        VentaAnulacion,
        CompraAnulacion,
        StockAjuste,
        StockDecomiso,
        StockConteo,
        CcReliquidacion,
        UsuarioAlta,
        UsuarioActualizacion,
        UsuarioBaja,
        UsuarioDesbloqueo,
        UsuarioPassword
    ];
}
