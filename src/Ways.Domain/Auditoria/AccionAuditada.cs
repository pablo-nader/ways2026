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
/// estas 27 instancias; un call site nuevo que necesite una acción no listada tiene que agregarla
/// acá primero, nunca improvisar un <c>new AccionAuditada(...)</c> inline. El <c>record</c>
/// posicional público SÍ genera un constructor público (<c>new AccionAuditada("x", "y")</c>
/// compila): nada en el tipo lo impide, y la membresía al catálogo no se valida en runtime
/// (design decisión 15 — una acción retirada deja filas consultables cuyo <c>accion</c> ya no
/// tiene entrada acá, y eso es intencional). La garantía de "solo estas 27" es de convención +
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

    /// <summary><c>Compras/ServicioDeCompras.cs</c>, <c>DescartarBorradorAsync</c>: el borrado físico de
    /// un borrador deja este rastro porque la fila ya no existe para consultarla.</summary>
    public static readonly AccionAuditada CompraDescarte = new("compra.descarte", "comprobante_compra");

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

    /// <summary>Etapa 20 slice 4 — <c>Organizacion/ServicioDeOrganizacion.cs</c>,
    /// <c>EliminarTenantAsync</c>. Baja lógica: <c>{deleted_at, estado}</c> en los dos lados, misma
    /// forma que <see cref="UsuarioBaja"/> — el tenant es el único de los tres que además cambia de
    /// estado (<c>EstadoTenant.Baja</c>).</summary>
    public static readonly AccionAuditada TenantBaja = new("tenant.baja", "tenant");

    /// <summary>Etapa 20 slice 4 — <c>Organizacion/ServicioDeOrganizacion.cs</c>,
    /// <c>EliminarEmpresaAsync</c> y la cascada de <c>EliminarTenantAsync</c>.</summary>
    public static readonly AccionAuditada EmpresaBaja = new("empresa.baja", "empresa");

    /// <summary>Etapa 20 slice 4 — <c>Organizacion/ServicioDeOrganizacion.cs</c>,
    /// <c>EliminarPuntoVentaAsync</c> y las cascadas de empresa y de tenant. El dominio se abrevia
    /// <c>pv</c> por la MISMA razón que <see cref="CcReliquidacion"/> abrevia cuenta corriente: el
    /// formato congelado del catálogo es <c>&lt;dominio&gt;.&lt;operacion&gt;</c> sin guiones bajos
    /// (<c>AccionAuditadaTests.CadaAccionRespetaElFormatoDominioPuntoOperacion</c>). La
    /// <c>Entidad</c> sí lleva el nombre completo en snake_case, como
    /// <c>comprobante_venta</c>.</summary>
    public static readonly AccionAuditada PuntoVentaBaja = new("pv.baja", "punto_venta");

    /// <summary>stage-desktop-pos (DB CHANGE GATE aprobado): flip administrativo de
    /// <c>puntos_venta.modo</c> — <c>Organizacion/ServicioDeOrganizacion.cs</c>,
    /// <c>ActualizarModoPuntoVentaAsync</c>. Mismo dominio abreviado <c>pv</c> que
    /// <see cref="PuntoVentaBaja"/>.</summary>
    public static readonly AccionAuditada PuntoVentaModo = new("pv.modo", "punto_venta");

    /// <summary>stage-pos-venta-offline-backend: <c>Ventas/ServicioDeVentas.cs</c>,
    /// <c>EjecutarTransaccionAsync</c> — una venta offline (<c>NumeroPreasignado</c> + precios
    /// congelados por el dispositivo, ver <see cref="Ways.Application.Ventas.Contratos"/>) cuyo
    /// precio recibido difiere del que <c>ServicioDeOfertas.ResolverAsync</c> hubiera cobrado al
    /// momento del sync. Warning, nunca bloqueo (mismo criterio que
    /// <c>ItemComprobanteVenta.LoteVencido</c>): la venta ya se cobró en el dispositivo con el
    /// precio que el ticket le imprimió al cliente — el servidor solo deja rastro auditable de la
    /// diferencia, nunca la rechaza ni la recalcula en el comprobante. Cierra/acota el ítem
    /// pendiente de docs/10-modelo-de-datos.md §9.2 (repricing al sincronizar).</summary>
    public static readonly AccionAuditada VentaDiscrepanciaDePrecio = new("venta.discrepancia", "comprobante_venta");

    /// <summary>Cierre de turno forzado por encima de la guarda de rendición de dispositivos —
    /// <c>Caja/ServicioDeTurnos.cs</c>, los DOS modos de cierre. Se escribe SIEMPRE que
    /// <c>ForzarSinRendicion</c> viene en true, incluso si la guarda no encontró nada pendiente: el
    /// motivo que el supervisor declaró nunca se descarta (dto-contract-honesty), y "apretó el
    /// override sin necesidad" también vale la pena saberlo. El <c>id_entidad</c> es el turno.</summary>
    public static readonly AccionAuditada CierreForzadoSinRendicion = new("caja.forzado", "turno_caja");

    /// <summary>Venta local con cuenta corriente que el dispositivo registró sin poder validar el
    /// límite de crédito contra el servidor (<c>SolicitudDeVenta.LimiteDeCreditoNoValidado</c>) y
    /// cuyo saldo resultante supera el límite — <c>Ventas/ServicioDeVentas.cs</c>,
    /// <c>EjecutarTransaccionAsync</c>. Nunca bloquea: la venta ya se entregó. El
    /// <c>id_entidad</c> es el comprobante; el payload lleva cliente, saldos y límite.</summary>
    public static readonly AccionAuditada VentaExcedioLimiteSinValidar = new("venta.sobrelimite", "comprobante_venta");

    /// <summary>Edición de un pago ya registrado — <c>Pagos/ServicioDePagos.cs</c>, los caminos
    /// del POS (turno abierto) y de administración. <c>valor_anterior</c>/<c>valor_nuevo</c> llevan
    /// los campos editables más el importe.</summary>
    public static readonly AccionAuditada PagoEdicion = new("pago.edicion", "pago");

    /// <summary>Baja lógica administrativa de un pago — <c>Pagos/ServicioDePagos.cs</c>. Mismos
    /// campos que <see cref="PagoEdicion"/> más <c>deleted_at</c> en <c>valor_nuevo</c>.</summary>
    public static readonly AccionAuditada PagoBaja = new("pago.baja", "pago");

    /// <summary>Aplicación de lo disponible de un pago a una o más compras (y adopción del proveedor de un pago sin
    /// proveedor) — <c>Pagos/ServicioDePagos.Aplicaciones.cs</c>. El <c>id_entidad</c> es el pago.</summary>
    public static readonly AccionAuditada PagoAplicacion = new("pago.aplicacion", "pago");

    /// <summary>Desaplicación de un pago de una compra — <c>Pagos/ServicioDePagos.Aplicaciones.cs</c>. El
    /// <c>id_entidad</c> es el pago.</summary>
    public static readonly AccionAuditada PagoDesaplicacion = new("pago.desaplicacion", "pago");

    /// <summary>Cierre en cero de una compra por redondeo (<c>POST /api/compras/{id}/ajuste-de-redondeo</c> y el pago de una
    /// compra con <c>cerrarEnCero</c>) — <c>Pagos/ServicioDePagos.Redondeo.cs</c>. El <c>id_entidad</c> es la compra.</summary>
    public static readonly AccionAuditada CompraRedondeo = new("compra.redondeo", "comprobante_compra");

    /// <summary>Cierre en cero de lo que queda disponible de un pago por redondeo (<c>POST /api/pagos/{id}/ajuste-de-redondeo</c> y
    /// el pago de una compra con <c>cerrarEnCero</c> que paga de más) — <c>Pagos/ServicioDePagos.Redondeo.cs</c>. El
    /// <c>id_entidad</c> es el pago.</summary>
    public static readonly AccionAuditada PagoRedondeo = new("pago.redondeo", "pago");

    /// <summary>Reasignación de un pago de sueldo a otro empleado (<c>POST /api/pagos/administracion/{id}/reasignar-personal</c>) —
    /// <c>Pagos/ServicioDePagos.Reasignacion.cs</c>. Única operación que corrige saldos resultantes ya asentados en el libro del
    /// personal. El <c>id_entidad</c> es el pago; el payload lleva el empleado y el concepto antes y después.</summary>
    public static readonly AccionAuditada PagoReasignacion = new("pago.reasignacion", "pago");

    /// <summary>Alta de un punto de venta sobre una empresa existente —
    /// <c>Organizacion/ServicioDeOrganizacion.cs</c>, <c>CrearPuntoVentaAsync</c>. Mismo dominio
    /// abreviado <c>pv</c> que <see cref="PuntoVentaBaja"/>. El aprovisionamiento de un tenant
    /// crea su primer punto de venta sin pasar por acá.</summary>
    public static readonly AccionAuditada PuntoVentaAlta = new("pv.alta", "punto_venta");

    /// <summary>Alta de una sucursal sobre una empresa existente (<c>POST /api/sucursales</c>) —
    /// <c>Organizacion/ServicioDeOrganizacion.cs</c>, <c>CrearSucursalAsync</c>. La sucursal que nace con un punto de
    /// venta no deja este rastro: forma parte del <see cref="PuntoVentaAlta"/>.</summary>
    public static readonly AccionAuditada SucursalAlta = new("sucursal.alta", "sucursal");

    /// <summary>Renombre de una sucursal (<c>PUT /api/sucursales/{id}</c>).</summary>
    public static readonly AccionAuditada SucursalEdicion = new("sucursal.edicion", "sucursal");

    /// <summary>Baja lógica de una sucursal: la que pidió el operador (<c>DELETE /api/sucursales/{id}</c>) y la que cae
    /// al mover su última caja a otra sucursal (<c>por_cascada</c> en el payload).</summary>
    public static readonly AccionAuditada SucursalBaja = new("sucursal.baja", "sucursal");

    /// <summary>Mover un punto de venta a otra sucursal de la misma empresa (<c>POST /api/puntos-venta/{id}/sucursal</c>).
    /// Mismo dominio abreviado <c>pv</c> que <see cref="PuntoVentaBaja"/>; el stock no se mueve con la caja.</summary>
    public static readonly AccionAuditada PuntoVentaSucursal = new("pv.sucursal", "punto_venta");

    /// <summary>Fusión de una sucursal en otra de su empresa (<c>POST /api/sucursales/{id}/fusionar</c>): sus cajas y su
    /// stock pasan al destino y la sucursal de origen (<c>id_entidad</c>) queda dada de baja. Un solo rastro por fusión.</summary>
    public static readonly AccionAuditada SucursalFusion = new("sucursal.fusion", "sucursal");

    /// <summary>Registro o actualización de una sucursal como local de Mercado Pago
    /// (<c>POST /api/sucursales/{id}/mercadopago</c>). El <c>id_entidad</c> es la sucursal; el payload dice la operación
    /// (alta, adopción de un local ya existente o actualización) y los ids de Mercado Pago, nunca un token.</summary>
    public static readonly AccionAuditada SucursalMercadoPago = new("sucursal.mercadopago", "sucursal");

    /// <summary>Edición de una compra confirmada (<c>PUT /api/compras/{id}</c>) — <c>Compras/ServicioDeCompras.cs</c>. El
    /// <c>id_entidad</c> es la compra; el valor nuevo lleva además las diferencias asentadas en stock y cuenta corriente.</summary>
    public static readonly AccionAuditada CompraEdicion = new("compra.edicion", "comprobante_compra");

    /// <summary>Aplicación de lo disponible de un cobro (recibo <c>RC</c>) a una o más ventas en cuenta corriente —
    /// <c>CuentaCorriente/ServicioDeCobros.cs</c>. El <c>id_entidad</c> es el recibo.</summary>
    public static readonly AccionAuditada CobroAplicacion = new("cobro.aplicacion", "comprobante_venta");

    /// <summary>Desaplicación de un cobro de una venta, a mano o porque se anuló el recibo o la venta —
    /// <c>CuentaCorriente/ServicioDeCobros.cs</c> y <c>Ventas/ServicioDeVentas.cs</c>. El <c>id_entidad</c> es el
    /// recibo.</summary>
    public static readonly AccionAuditada CobroDesaplicacion = new("cobro.desaplicacion", "comprobante_venta");

    /// <summary>Cambio de los medios de pago de una venta ya emitida — <c>Ventas/ServicioDeCambioDeMedios.cs</c>, desde la caja o
    /// desde administración. <c>valor_anterior</c> y <c>valor_nuevo</c> llevan los cobros vivos antes y después.</summary>
    public static readonly AccionAuditada VentaCambioDeMedios = new("venta.medios", "comprobante_venta");

    /// <summary>Edición administrativa del importe o el motivo de un retiro de caja — <c>Caja/ServicioDeRetiros.cs</c>.
    /// <c>valor_anterior</c> y <c>valor_nuevo</c> llevan importe y motivo, y el nuevo el turno cuyo arqueo se recalculó.</summary>
    public static readonly AccionAuditada RetiroEdicion = new("retiro.edicion", "movimiento_caja");

    /// <summary>Baja lógica administrativa de un retiro de caja — <c>Caja/ServicioDeRetiros.cs</c>. Mismos campos que
    /// <see cref="RetiroEdicion"/>, con <c>deleted_at</c> en <c>valor_nuevo</c>.</summary>
    public static readonly AccionAuditada RetiroBaja = new("retiro.baja", "movimiento_caja");

    /// <summary>Las acciones del catálogo (12 de la primera pasada, proposal decisión 5, las
    /// tres bajas de organización de la etapa 20 slice 4, el flip de modo de stage-desktop-pos, la
    /// discrepancia de precio offline de stage-pos-venta-offline-backend, el cierre forzado
    /// sobre la guarda de rendición, más la venta local que superó el límite de crédito sin
    /// validarlo y la edición/baja de pagos y el alta de puntos de venta, más el descarte de un borrador de compra, los cierres por redondeo, la edición de una compra confirmada y la aplicación/desaplicación de cobros) — usada por el catálogo genérico de tests (naming
    /// <c>&lt;dominio&gt;.&lt;operacion&gt;</c>, sin duplicados) y por cualquier consumidor que
    /// necesite iterarlas todas.</summary>
    public static readonly IReadOnlyList<AccionAuditada> Todas =
    [
        PrecioCambio,
        VentaAnulacion,
        CompraAnulacion,
        CompraDescarte,
        StockAjuste,
        StockDecomiso,
        StockConteo,
        CcReliquidacion,
        UsuarioAlta,
        UsuarioActualizacion,
        UsuarioBaja,
        UsuarioDesbloqueo,
        UsuarioPassword,
        TenantBaja,
        EmpresaBaja,
        PuntoVentaBaja,
        PuntoVentaModo,
        VentaDiscrepanciaDePrecio,
        CierreForzadoSinRendicion,
        VentaExcedioLimiteSinValidar,
        PagoEdicion,
        PagoBaja,
        PagoAplicacion,
        PagoDesaplicacion,
        CompraRedondeo,
        PagoRedondeo,
        PagoReasignacion,
        PuntoVentaAlta,
        CompraEdicion,
        CobroAplicacion,
        CobroDesaplicacion,
        SucursalAlta,
        SucursalEdicion,
        SucursalBaja,
        PuntoVentaSucursal,
        SucursalFusion,
        SucursalMercadoPago,
        VentaCambioDeMedios,
        RetiroEdicion,
        RetiroBaja
    ];
}
