using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Caja;
using Ways.Application.Compras;
using Ways.Application.CuentaCorriente;
using Ways.Application.Parametros;
using Ways.Domain.Auditoria;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Compras;
using Ways.Domain.Pagos;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Proveedores;

namespace Ways.Application.Pagos;

/// <summary>
/// Captura de pagos contra un turno abierto (design: Table Shapes — write path C; tasks.md
/// Slice 3). Reutiliza <see cref="ServicioDeTurnos.ResolverTurnoAbiertoAsync"/> (tasks.md,
/// Orchestrator Decision 3) en vez de escribir su propia consulta de turno abierto — mismo
/// criterio que <c>ServicioDeVentas.EmitirAsync</c> (Slice 5).
///
/// stage-8-compras-transferencias-inventario, Slice 4 (design decisión 7): cuando la solicitud
/// trae <c>idComprobanteCompra</c>, un <c>SELECT ... FOR SHARE</c> crudo sobre el header de la
/// compra — DESPUÉS del lock de turno existente — cierra el TOCTOU contra una anulación
/// concurrente de la misma compra (el mismo statement crudo/sibling raw SQL que
/// <c>ServicioDeCompras</c>, ver su doc-comment de clase).
/// </summary>
public class ServicioDePagos(
    IWaysDbContext db, ServicioDeTurnos servicioDeTurnos, ServicioDeParametros servicioDeParametros,
    IRelojDelSistema reloj, IContextoDeUsuario contexto, LectorDeMovimientosDelTurno lectorDeMovimientos,
    ServicioDeAuditoria auditoria)
{
    /// <summary>Resuelve el punto de venta (404 ADR-8) antes que el turno abierto (spec: Pago
    /// Requires An Open Turno) — mismo orden que <c>ServicioDeVentas.EmitirAsync</c> (design
    /// decisión 11): un punto de venta apócrifo tiene que dar 404, nunca el 409 de "sin turno
    /// abierto" de un punto de venta que ni siquiera existe.</summary>
    public async Task<PagoRegistrado> RegistrarAsync(SolicitudDePago solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        ExigirImporteValido(solicitud.Importe);
        ExigirConceptoValido(solicitud.Concepto);
        // spec: pagos / A Comprobante Compra Link Requires Categoria Proveedor — "rejected
        // before reaching the database": chequeo de dominio puro, ANTES de cualquier consulta.
        ExigirCategoriaCoherenteConLaCompra(solicitud.Categoria, solicitud.IdComprobanteCompra);

        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);
        var turno = await servicioDeTurnos.ResolverTurnoAbiertoAsync(solicitud.IdPuntoVenta, ct);

        // judgment-day PR3 follow-up: sin compra ligada, id_proveedor es un input crudo que
        // ActualizarSaldoProveedorAsync solo descubre inválido con un InvalidOperationException
        // (500) — mismo criterio 404 que ResolverProveedorAsync de ServicioDeCompras, ANTES de
        // abrir la transacción de escritura. Con compra ligada, ExigirCompraLigableAsync (abajo,
        // ya bajo lock) deriva/valida el proveedor contra la compra — ese YA es un proveedor
        // existente (viene de la fila de comprobantes_compra), así que este chequeo solo aplica
        // cuando NO hay compra.
        if (solicitud.IdComprobanteCompra is null && solicitud.IdProveedor is { } idProveedorSolicitado)
        {
            await ResolverProveedorAsync(idProveedorSolicitado, ct);
        }

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        var pago = await estrategia.ExecuteAsync(async () =>
            await InsertarPagoAsync(
                idTenant, puntoVenta.IdEmpresa, turno.Id, solicitud, idEmpleado, momento, ct));

        return Proyectar(pago);
    }

    /// <summary>stage-gastos-admin-retroactivos (PR3), owner's use case 2: alta administrativa de
    /// un pago SIN turno, pagado directo de la tesorería de la empresa — puede llevar una
    /// <see cref="SolicitudDePagoDeAdministracion.Fecha"/> retroactiva. A diferencia de <see
    /// cref="RegistrarAsync"/> (que resuelve el punto de venta y DESPUÉS el turno abierto), acá no
    /// hay ningún turno que resolver: el orden de validación es empresa (404 ADR-8) → punto de
    /// venta opcional (404 si no existe, 400 si es de otra empresa) → medio de pago (excluye
    /// cuenta corriente, mismo criterio que el POS) → fecha (nunca futura, resuelta en la zona
    /// horaria de la empresa/punto de venta) — recién ahí se abre la transacción de
    /// escritura.</summary>
    public async Task<PagoRegistrado> RegistrarDeAdministracionAsync(
        SolicitudDePagoDeAdministracion solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        ExigirImporteValido(solicitud.Importe);
        ExigirConceptoValido(solicitud.Concepto);
        ExigirCategoriaCoherenteConLaCompra(solicitud.Categoria, solicitud.IdComprobanteCompra);
        ExigirPuntoVentaParaElPagoAProveedor(solicitud.Categoria, solicitud.IdPuntoVenta);

        // judgment-day PR3 follow-up: mismo guard 404 que RegistrarAsync arriba — ver ese
        // doc-comment para el análisis completo.
        if (solicitud.IdComprobanteCompra is null && solicitud.IdProveedor is { } idProveedorSolicitado)
        {
            await ResolverProveedorAsync(idProveedorSolicitado, ct);
        }

        var empresa = await ResolverEmpresaAsync(solicitud.IdEmpresa, ct);
        if (solicitud.IdPuntoVenta is { } idPuntoVenta)
        {
            await ExigirPuntoVentaDeLaEmpresaAsync(idPuntoVenta, empresa.Id, ct);
        }

        await ExigirMedioPagoValidoAsync(solicitud.IdMedioPago, ct);

        var zona = await ResolverZonaAsync(empresa.Id, solicitud.IdPuntoVenta, ct);
        ExigirFechaNoFutura(solicitud.Fecha, momento, zona);
        var fechaDeNegocio = FechaDeNegocioAInstante(solicitud.Fecha, zona);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        var pago = await estrategia.ExecuteAsync(async () =>
            await InsertarPagoDeAdministracionAsync(
                idTenant, empresa.Id, fechaDeNegocio, solicitud, idEmpleado, momento, ct));

        return Proyectar(pago);
    }

    /// <summary>Paga una compra confirmada en UNA transacción: crea el pago administrativo (categoría
    /// proveedor, origen tesorería, punto de venta y proveedor de la compra, fecha de negocio del
    /// pedido) y, por el mismo camino de escritura del alta administrativa, el movimiento
    /// <c>pago</c> de cuenta corriente imputado a la compra y el egreso de tesorería. El importe no
    /// puede superar el saldo pendiente de ESA compra.
    ///
    /// Forma (b) de ef-retry-safe-writes: <see cref="FabricaDeEstrategiaSinReintento"/>, porque el
    /// pago no tiene clave de idempotencia y un reintento sobre un commit ambiguo pagaría dos veces.
    ///
    /// Orden de locks: 1) header de la compra <c>FOR UPDATE</c> (lock y lectura en una sola
    /// sentencia, sin entidad: es la ÚNICA lectura del total y del estado) → 2) pagado de la compra,
    /// leído después del lock → 3) fila del pago (insert, sin lock) → 4) fila del proveedor
    /// (<c>UPDATE saldo</c>, último lock de fila) → 5) advisory de tesorería de la empresa, ÚLTIMO.
    /// Es el mismo orden que el alta administrativa (<see cref="InsertarPagoDeAdministracionAsync"/>)
    /// con el lock de la compra endurecido a exclusivo. Sin ciclo: la anulación toma el header
    /// exclusivo como primer lock; la confirmación lo toma exclusivo después del lock de membresía de
    /// familias compartido, que este método no pide; y los demás escritores que tocan la compra (alta
    /// ligada, vinculación) la toman <c>FOR SHARE</c>, así que todos se serializan contra el paso 1 y
    /// ninguno retiene algo que este método necesite antes de ese lock.</summary>
    public async Task<ResultadoDePagoDeCompra> PagarCompraAsync(
        int idComprobanteCompra, SolicitudDePagoDeCompra solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        ExigirImporteValido(solicitud.Importe);
        var concepto = string.IsNullOrWhiteSpace(solicitud.Concepto) ? null : solicitud.Concepto.Trim();

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarPagoDeCompraAsync(idTenant, idComprobanteCompra, solicitud, concepto, idEmpleado, momento, ct));
    }

    private async Task<ResultadoDePagoDeCompra> EjecutarPagoDeCompraAsync(
        int idTenant, int idComprobanteCompra, SolicitudDePagoDeCompra solicitud, string? concepto, int idEmpleado,
        DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var compra = await BloquearCompraAsync(idComprobanteCompra, idTenant, paraPagar: true, ct);

        var pagado = (await LectorDePagadoPorCompra.LeerAsync(db, [idComprobanteCompra], ct))
            .GetValueOrDefault(idComprobanteCompra, 0m);
        var saldoPendiente = ReglaDePagoDeCompra.SaldoPendiente(EstadoCompra.Confirmada, compra.Total, pagado);
        ReglaDePagoDeCompra.ExigirPagoValido(solicitud.Importe, saldoPendiente);

        var puntoVenta = await ResolverPuntoVentaAsync(compra.IdPuntoVenta, ct);
        await ExigirMedioPagoValidoAsync(solicitud.IdMedioPago, ct);

        var zona = await ResolverZonaAsync(puntoVenta.IdEmpresa, puntoVenta.Id, ct);
        ExigirFechaNoFutura(solicitud.Fecha, momento, zona);
        var fechaDeNegocio = FechaDeNegocioAInstante(solicitud.Fecha, zona);

        concepto ??= await ConceptoPorDefectoDelPagoAsync(idComprobanteCompra, ct);

        var pago = await PersistirPagoDeAdministracionAsync(
            idTenant, puntoVenta.IdEmpresa, fechaDeNegocio,
            new SolicitudDePagoDeAdministracion(
                solicitud.Fecha, puntoVenta.IdEmpresa, puntoVenta.Id, CategoriaPago.Proveedor, compra.IdProveedor,
                IdArea: null, concepto, Detalle: null, solicitud.IdMedioPago, NumeroFactura: null, solicitud.Importe,
                idComprobanteCompra),
            compra.IdProveedor, idEmpleado, momento, ct);

        await transaccion.CommitAsync(ct);

        return new ResultadoDePagoDeCompra(
            Proyectar(pago), pagado + solicitud.Importe, saldoPendiente - solicitud.Importe);
    }

    private async Task<string> ConceptoPorDefectoDelPagoAsync(int idComprobanteCompra, CancellationToken ct)
    {
        var datos = await db.ComprobantesCompra
            .Where(c => c.Id == idComprobanteCompra)
            .Select(c => new
            {
                c.NumeroExterno,
                NombreDelTipo = db.TiposComprobante
                    .Where(t => t.Id == c.IdTipoComprobante).Select(t => t.Nombre).FirstOrDefault()
            })
            .FirstAsync(ct);

        return ReglaDePagoDeCompra.ConceptoPorDefecto(datos.NombreDelTipo, datos.NumeroExterno, idComprobanteCompra);
    }

    /// <summary>stage-gasto-a-compra (PR4), owner requirement: cualquier pago (POS o admin) se
    /// puede ligar a una compra DESPUÉS de creado — a diferencia de
    /// <see cref="SolicitudDePago.IdComprobanteCompra"/>/<see
    /// cref="SolicitudDePagoDeAdministracion.IdComprobanteCompra"/>, que solo aplican en el alta.
    ///
    /// Orden de locks de la transacción (single-read-under-lock + deadlock-safe ordering):
    /// 1) compra <c>FOR SHARE</c> (mismo statement/orden relativo que <see
    ///    cref="ExigirCompraLigableAsync"/> en el alta: PRIMER lock, así que un alta concurrente
    ///    con <c>idComprobanteCompra</c> y esta vinculación nunca se ordenan al revés) →
    /// 2) pago <c>FOR UPDATE</c> (lock-only, mismo patrón que <c>ServicioDeOrganizacion.
    ///    TomarLockDePuntoVentaAsync</c> — la ÚNICA lectura EF de la fila nace DESPUÉS, bajo este
    ///    lock) → 3) fila de proveedor/movimiento de CC (adentro de <see
    ///    cref="EscribirPagoAProveedorAsync"/>/<see
    ///    cref="EscriturasDeCuentaCorrienteProveedor.ImputarMovimientoDePagoAsync"/>, ÚLTIMO lock,
    ///    mismo criterio "el ledger de proveedor es el último lock de fila" que el resto de esta
    ///    clase).
    ///
    /// Análisis de ciclo: la anulación de compra (<c>ServicioDeCompras.MarcarAnuladaAsync</c>) toma
    /// el header EXCLUSIVO como su ÚNICO y primer lock — nunca toca <c>pagos</c> ni el ledger de
    /// proveedor, así que no puede formar un ciclo con el orden 1→2→3 de acá (se serializa contra
    /// el paso 1, nunca espera detrás del 2 o el 3). La confirmación de compra
    /// (<c>ServicioDeCompras.ConfirmarAsync</c>) tampoco toca <c>pagos</c>: su único lock
    /// compartido con esta transacción es el propio header de la compra (paso 1 acá; allá viene
    /// justo después del lock de membresía de familias, que esta transacción no toma) — mismo
    /// orden relativo entre los locks que comparten, sin ciclo. El cierre de turno
    /// (<c>ServicioDeTurnos</c>) no lockea ni compras ni pagos existentes (solo el turno mismo y,
    /// al insertar, filas nuevas de tesorería/CC) — no comparte ningún recurso con este método,
    /// así que tampoco puede ciclar. Los caminos de alta de pago (<see cref="InsertarPagoAsync"/>/
    /// <see cref="InsertarPagoDeAdministracionAsync"/>) toman compra (paso 1, mismo orden) y DESPUÉS
    /// insertan una fila NUEVA de <c>pagos</c> (sin lock: es un insert) — nunca lockean una fila de
    /// <c>pagos</c> EXISTENTE, así que no compiten con el paso 2 de acá.</summary>
    public async Task<PagoRegistrado> VincularCompraAsync(
        int idPago, SolicitudDeVincularCompra solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        var pago = await estrategia.ExecuteAsync(async () =>
            await EjecutarVincularCompraAsync(idTenant, idPago, solicitud.IdComprobanteCompra, idEmpleado, momento, ct));

        return Proyectar(pago);
    }

    /// <summary>Ver el doc-comment de <see cref="VincularCompraAsync"/> para el orden de locks y el
    /// análisis de ciclo completos.</summary>
    private async Task<Pago> EjecutarVincularCompraAsync(
        int idTenant, int idPago, int idComprobanteCompra, int idEmpleado, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // Paso 1 — compra FOR SHARE, PRIMER lock (mismo statement que ExigirCompraLigableAsync).
        var (idProveedorDeLaCompra, idPuntoVentaDeLaCompra, _) =
            await BloquearCompraAsync(idComprobanteCompra, idTenant, paraPagar: false, ct);

        // Paso 2 — pago FOR UPDATE (lock-only), y la ÚNICA lectura EF de la fila, DESPUÉS del
        // lock (single-read-under-lock).
        await TomarLockDePagoAsync(idPago, idTenant, ct);
        var pago = await db.Pagos.FirstAsync(g => g.Id == idPago, ct);
        ExigirPagoSinPagoDePersonal(pago.IdMovimientoPersonal);

        if (pago.IdComprobanteCompra is not null)
        {
            throw new ErrorDominio(
                "gasto_ya_vinculado", "El gasto ya está vinculado a una compra.", 409);
        }

        // dangling-fk-read-models no aplica acá (no es un read model), pero la misma idea de
        // "el FK inmutable puede apuntar a una fila invisible" no corre: PuntosVenta no tiene baja
        // lógica sobre id_empresa, y toda compra confirmada YA validó su punto de venta al crearse.
        var idEmpresaDeLaCompra = await db.PuntosVenta
            .Where(pv => pv.Id == idPuntoVentaDeLaCompra)
            .Select(pv => pv.IdEmpresa)
            .FirstAsync(ct);

        if (idEmpresaDeLaCompra != pago.IdEmpresa)
        {
            throw new ErrorDominio(
                "compra_de_otra_empresa", "La compra indicada pertenece a otra empresa.", 400);
        }

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        if (pago.Categoria == CategoriaPago.Proveedor && pago.IdProveedor is { } idProveedorDelPago)
        {
            // El pago YA tiene proveedor: solo se admite si coincide con el de la compra —
            // reusa el mismo código que ExigirCompraLigableAsync para el mismo caso en el alta.
            if (idProveedorDelPago != idProveedorDeLaCompra)
            {
                throw new ErrorDominio(
                    "proveedor_no_coincide_con_la_compra",
                    "El proveedor indicado no coincide con el proveedor de la compra.", 400);
            }

            // El pago ya se escribió al crear el pago (saldo ya descontado) — solo se imputa
            // la compra en el movimiento existente, el saldo NO cambia.
            pago.IdComprobanteCompra = idComprobanteCompra;
            pago.UpdatedAt = momento;
            await db.SaveChangesAsync(ct);

            await EscriturasDeCuentaCorrienteProveedor.ImputarMovimientoDePagoAsync(
                conexion, transaccionCruda, idTenant, pago.Id, idComprobanteCompra, ct);
        }
        else
        {
            // Conversión: el pago (categoría distinta de Proveedor, o Proveedor sin proveedor
            // asignado) nunca escribió un Pago al crearse — se convierte y se escribe AHORA, con
            // el importe reduciendo el saldo del proveedor por primera vez.
            pago.Categoria = CategoriaPago.Proveedor;
            pago.IdProveedor = idProveedorDeLaCompra;
            pago.IdComprobanteCompra = idComprobanteCompra;
            pago.UpdatedAt = momento;
            await db.SaveChangesAsync(ct);

            // El ledger de CC exige un punto de venta (ValidarFormaPorTipo): un pago admin sin
            // punto de venta propio usa el de la compra (las compras siempre tienen uno) — nunca
            // al revés, el punto de venta DEL PAGO manda cuando existe (es el que realmente pagó).
            var idPuntoVentaDelPago = pago.IdPuntoVenta ?? idPuntoVentaDeLaCompra;

            await EscribirPagoAProveedorAsync(
                idTenant, idProveedorDeLaCompra, idPuntoVentaDelPago, pago.Importe, pago, idEmpleado, momento, ct);
        }

        await transaccion.CommitAsync(ct);

        return pago;
    }

    /// <summary>Lock del header de la compra, mismos códigos 404/409 que
    /// <see cref="ExigirCompraLigableAsync"/> (compra no encontrada/anulada/no confirmada), y la ÚNICA
    /// lectura de la fila: expone <c>id_punto_venta</c> para el fallback de PV y la resolución de
    /// empresa, y el total para el saldo pendiente. Vincular toma <c>FOR SHARE</c>; pagar toma
    /// <c>FOR UPDATE</c> (<paramref name="paraPagar"/>), porque dos pagos concurrentes tienen que
    /// verse el uno al otro: con <c>FOR SHARE</c> los dos leerían el mismo saldo pendiente.</summary>
    private async Task<(int IdProveedor, int IdPuntoVenta, decimal Total)> BloquearCompraAsync(
        int idComprobanteCompra, int idTenant, bool paraPagar, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccionCruda;
        comando.CommandText =
            "SELECT estado::text, id_proveedor, id_punto_venta, total FROM comprobantes_compra " +
            "WHERE id_comprobante_compra = $1 AND id_tenant = $2 " + (paraPagar ? "FOR UPDATE" : "FOR SHARE");
        ParametrosDeComando.Agregar(comando, idComprobanteCompra);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe la compra {idComprobanteCompra}.");
        }

        var estado = lector.GetString(0);
        var idProveedor = lector.GetInt32(1);
        var idPuntoVenta = lector.GetInt32(2);
        var total = lector.GetDecimal(3);

        if (estado == "anulada")
        {
            throw new ErrorDominio("compra_anulada", "La compra ligada está anulada.", 409);
        }

        if (estado != "confirmada")
        {
            throw new ErrorDominio(
                "compra_no_confirmada", "La compra ligada todavía no está confirmada.", 409);
        }

        return (idProveedor, idPuntoVenta, total);
    }

    /// <summary>Lock-only, mismo patrón que <c>ServicioDeOrganizacion.TomarLockDePuntoVentaAsync</c>
    /// (<c>SELECT 1 ... FOR UPDATE</c>): nunca materializa una entidad — la ÚNICA lectura EF de la
    /// fila nace DESPUÉS, en <see cref="EjecutarVincularCompraAsync"/> (single-read-under-lock). Filtra
    /// <c>deleted_at IS NULL</c> igual que el filtro global de EF: un pago dado de baja es 404.</summary>
    private async Task TomarLockDePagoAsync(int idPago, int idTenant, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccionCruda;
        comando.CommandText = "SELECT 1 FROM pagos WHERE id_pago = $1 AND id_tenant = $2 AND deleted_at IS NULL FOR UPDATE";
        ParametrosDeComando.Agregar(comando, idPago);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant".
            throw ErrorDominio.NoEncontrado($"No existe el gasto {idPago}.");
        }
    }

    // ---- edición y baja -----------------------------------------------------------------------

    /// <summary>Edición desde el POS: solo mientras el turno del pago siga abierto, decidido bajo
    /// el lock del turno (un pago sin turno, o con el turno ya cerrado, es <c>409
    /// gasto_turno_cerrado</c>). Ver <see cref="EjecutarEscrituraAsync"/> para el orden de locks y
    /// el análisis de ciclo.</summary>
    public async Task<PagoRegistrado> EditarAsync(
        int idPago, SolicitudDeEdicionDePago solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        ExigirImporteValido(solicitud.Importe);
        ExigirConceptoValido(solicitud.Concepto);

        var inmutables = await LeerDatosInmutablesAsync(idPago, ct);
        ExigirPagoSinPagoDePersonal(inmutables.IdMovimientoPersonal);
        if (inmutables.IdTurnoCaja is null)
        {
            throw PagoTurnoCerrado();
        }

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        var pago = await estrategia.ExecuteAsync(async () =>
            await EjecutarEscrituraAsync(
                idTenant, idPago, inmutables, solicitud, ModoDeEscritura.Pos, idEmpleado, momento, ct));

        return Proyectar(pago);
    }

    /// <summary>Edición administrativa de cualquier pago: con turno abierto, cerrado (recalcula el
    /// arqueo si cambia algo que el arqueo cuenta) o sin turno.</summary>
    public async Task<PagoDeAdministracionListado> EditarDeAdministracionAsync(
        int idPago, SolicitudDeEdicionDePago solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        ExigirImporteValido(solicitud.Importe);
        ExigirConceptoValido(solicitud.Concepto);

        var inmutables = await LeerDatosInmutablesAsync(idPago, ct);
        ExigirPagoSinPagoDePersonal(inmutables.IdMovimientoPersonal);
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
            await EjecutarEscrituraAsync(
                idTenant, idPago, inmutables, solicitud, ModoDeEscritura.Administracion, idEmpleado, momento, ct));

        return await ObtenerDeAdministracionAsync(idPago, ct);
    }

    /// <summary>Baja lógica administrativa de cualquier pago: revierte su pago a proveedor y su
    /// egreso de tesorería con ajustes, y recalcula el arqueo si era un pago de caja de un turno
    /// cerrado.</summary>
    public async Task EliminarDeAdministracionAsync(int idPago, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var inmutables = await LeerDatosInmutablesAsync(idPago, ct);
        ExigirPagoSinPagoDePersonal(inmutables.IdMovimientoPersonal);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
            await EjecutarEscrituraAsync(
                idTenant, idPago, inmutables, solicitud: null, ModoDeEscritura.Administracion, idEmpleado, momento, ct));
    }

    private enum ModoDeEscritura
    {
        Pos,
        Administracion
    }

    /// <summary>Columnas que ninguna escritura cambia después del alta, así que se pueden leer
    /// antes de los locks (single-read-under-lock regla 6): el turno es la clave del primer lock.
    /// <c>id_comprobante_compra</c> NO está acá: la vinculación posterior lo cambia.
    /// <c>id_movimiento_personal</c> sí: se escribe al insertar el pago y no hay escritor que lo cambie.</summary>
    private sealed record DatosInmutablesDePago(
        int? IdTurnoCaja, int IdEmpresa, OrigenFondosPago OrigenFondos, int? IdMovimientoPersonal);

    private async Task<DatosInmutablesDePago> LeerDatosInmutablesAsync(int idPago, CancellationToken ct) =>
        await db.Pagos
            .Where(g => g.Id == idPago)
            .Select(g => new DatosInmutablesDePago(g.IdTurnoCaja, g.IdEmpresa, g.OrigenFondos, g.IdMovimientoPersonal))
            .FirstOrDefaultAsync(ct)
        // ADR-8: mismo 404 para "no existe", "es de otro tenant" y "está dado de baja".
        ?? throw ErrorDominio.NoEncontrado($"No existe el gasto {idPago}.");

    /// <summary>Mismas validaciones de referencias que el alta, pero solo para las referencias NUEVAS
    /// (OD4: una referencia que el pago ya tenía puede estar dada de baja y la edición sigue siendo
    /// válida): medio de pago visible y que no sea cuenta corriente, proveedor visible (404). Se compara
    /// contra el pago leído bajo el lock. El área sigue el criterio del alta: sin pre-chequeo, la FK
    /// compuesta es el respaldo.</summary>
    private async Task ExigirReferenciasNuevasDeLaEdicionAsync(
        Pago pago, SolicitudDeEdicionDePago solicitud, CancellationToken ct)
    {
        if (solicitud.IdMedioPago != pago.IdMedioPago)
        {
            await ExigirMedioPagoValidoAsync(solicitud.IdMedioPago, ct);
        }

        if (solicitud.IdProveedor is { } idProveedor && idProveedor != pago.IdProveedor)
        {
            await ResolverProveedorAsync(idProveedor, ct);
        }
    }

    /// <summary>Un pago que nació de un pago de personal se mueve junto con los libros de ese pago (saldo del
    /// personal, cuenta corriente del cliente, tesorería o cajón): tocarlo por separado los desalinearía. Anular
    /// o revertir un pago es una operación aparte que todavía no existe.</summary>
    private static void ExigirPagoSinPagoDePersonal(int? idMovimientoPersonal)
    {
        if (idMovimientoPersonal is not null)
        {
            throw new ErrorDominio(
                "gasto_de_pago_de_personal",
                "El gasto pertenece a un pago de personal y no se puede modificar por separado.", 409);
        }
    }

    private static ErrorDominio PagoTurnoCerrado() =>
        new("gasto_turno_cerrado", "El gasto solo se puede editar mientras su turno siga abierto.", 409);

    /// <summary>
    /// Edición (<paramref name="solicitud"/> no nula) o baja de un pago, en una sola transacción.
    ///
    /// Orden de locks:
    /// 1) turno del pago (si tiene): POS <c>FOR SHARE</c> con el estado leído bajo el lock (mismo
    ///    criterio que <see cref="ServicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync"/> en el alta, con
    ///    su propio <c>409 gasto_turno_cerrado</c>); administración <c>FOR UPDATE</c>, que además
    ///    serializa entre sí los recálculos del arqueo de un turno cerrado →
    /// 2) pago <c>FOR UPDATE</c> (lock-only, <see cref="TomarLockDePagoAsync"/>) y la ÚNICA lectura
    ///    EF de la fila, después del lock →
    /// 3) filas de proveedor de los ajustes de cuenta corriente, en orden ascendente de id →
    /// 4) filas de <c>arqueos_turno</c> y la marca del turno (ya cubiertas por el lock 1) →
    /// 5) advisory de tesorería de la empresa, ÚLTIMO.
    ///
    /// Análisis de ciclo: el cierre (<see cref="ServicioDeTurnos"/>, los dos modos) toma el turno
    /// EXCLUSIVO como primer statement y después solo el advisory de tesorería; nunca lockea pagos
    /// ni proveedores, y su orden turno → advisory es el mismo relativo que el de acá. El alta de
    /// pago toma el turno <c>FOR SHARE</c> primero y después compra/proveedor/advisory, sin lockear
    /// ningún pago existente. <see cref="VincularCompraAsync"/> no toca turnos y toma pago →
    /// proveedor, el mismo orden relativo que los pasos 2 → 3. Dos ediciones que mueven pagos entre
    /// los mismos dos proveedores en sentidos opuestos no se cruzan porque las dos lockean en orden
    /// ascendente de id.
    ///
    /// Carrera con el cierre: si la edición del POS gana el <c>FOR SHARE</c>, el cierre espera y
    /// deriva el arqueo con el pago ya editado; si el cierre gana, la edición lee <c>cerrado</c> y
    /// responde 409. La edición administrativa: si gana el cierre, lee <c>cerrado</c> bajo su
    /// <c>FOR UPDATE</c> y recalcula; si gana la edición, el cierre deriva con el pago ya editado.
    /// En ningún orden queda un arqueo persistido que no refleje el pago comiteado.
    /// </summary>
    private async Task<Pago> EjecutarEscrituraAsync(
        int idTenant, int idPago, DatosInmutablesDePago inmutables, SolicitudDeEdicionDePago? solicitud,
        ModoDeEscritura modo, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        TurnoBloqueado? turnoCerrado = null;
        if (inmutables.IdTurnoCaja is { } idTurnoCaja)
        {
            var turno = await BloquearTurnoAsync(idTurnoCaja, idTenant, modo, ct);
            if (!turno.Abierto)
            {
                if (modo == ModoDeEscritura.Pos)
                {
                    throw PagoTurnoCerrado();
                }

                turnoCerrado = turno;
            }
        }

        await TomarLockDePagoAsync(idPago, idTenant, ct);
        var pago = await db.Pagos.FirstOrDefaultAsync(g => g.Id == idPago, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el gasto {idPago}.");

        var anterior = EstadoContable(pago);
        var valorAnterior = PayloadDeEdicion(pago);

        if (solicitud is not null)
        {
            await ExigirReferenciasNuevasDeLaEdicionAsync(pago, solicitud, ct);
            ExigirEdicionCoherenteConLaCompra(pago, solicitud);

            if (pago.IdComprobanteCompra is null)
            {
                ExigirPuntoVentaParaElPagoAProveedor(solicitud.Categoria, pago.IdPuntoVenta);
            }

            pago.Categoria = solicitud.Categoria;
            pago.IdProveedor = solicitud.IdProveedor;
            pago.IdArea = solicitud.IdArea;
            pago.Concepto = solicitud.Concepto;
            pago.Detalle = solicitud.Detalle;
            pago.IdMedioPago = solicitud.IdMedioPago;
            pago.NumeroFactura = solicitud.NumeroFactura;
            pago.Importe = solicitud.Importe;
        }
        else
        {
            pago.DeletedAt = momento;
        }

        pago.UpdatedAt = momento;
        await db.SaveChangesAsync(ct);

        var nuevo = solicitud is null ? null : EstadoContable(pago);
        var detalle = solicitud is null ? $"Baja del gasto #{pago.Id}" : $"Edición del gasto #{pago.Id}";

        await EscribirAjustesDeProveedorAsync(
            idTenant, pago, CalculadorDeAjustesDePago.AjustesDeProveedor(anterior, nuevo), detalle, idEmpleado,
            momento, ct);

        if (turnoCerrado is not null && CalculadorDeAjustesDePago.AfectaElArqueo(pago.OrigenFondos, anterior, nuevo))
        {
            await RecalcularArqueoAsync(idTenant, turnoCerrado, idEmpleado, momento, ct);
        }

        if (CalculadorDeAjustesDePago.AjusteDeTesoreriaPara(pago.OrigenFondos, anterior.Importe, nuevo?.Importe)
            is { } ajusteDeTesoreria)
        {
            var conexion = await ObtenerConexionAbiertaAsync(ct);
            var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();
            await EscriturasDeTesoreria.TomarLockDeEmpresaAsync(conexion, transaccionCruda, idTenant, pago.IdEmpresa, ct);

            await EscriturasDeTesoreria.ApendearAsync(
                db, idTenant, pago.IdEmpresa, pago.IdPuntoVenta, momento, TipoMovimientoTesoreria.Ajuste,
                pago.IdTurnoCaja, idPago: null, detalle, ajusteDeTesoreria.Ingreso, ajusteDeTesoreria.Egreso,
                idEmpleado, ct);
        }

        var valorNuevo = PayloadDeEdicion(pago);
        if (solicitud is null)
        {
            valorNuevo["deleted_at"] = pago.DeletedAt;
        }

        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, pago.IdPuntoVenta, solicitud is null ? AccionAuditada.PagoBaja : AccionAuditada.PagoEdicion,
            pago.Id, valorAnterior, valorNuevo));
        await db.SaveChangesAsync(ct);

        await transaccion.CommitAsync(ct);

        return pago;
    }

    private sealed record TurnoBloqueado(int Id, bool Abierto, int? IdMedioPagoEfectivo);

    /// <summary>POS: <c>FOR SHARE</c>, excluye al cierre sin serializar entre sí las ediciones del
    /// mismo turno. Administración: <c>FOR UPDATE</c>, porque sobre un turno cerrado lo que sigue es
    /// un recálculo del arqueo, y dos recálculos concurrentes tienen que verse uno al otro.</summary>
    private async Task<TurnoBloqueado> BloquearTurnoAsync(
        int idTurnoCaja, int idTenant, ModoDeEscritura modo, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccionCruda;
        comando.CommandText =
            "SELECT estado::text, id_medio_pago_efectivo FROM turnos_caja " +
            "WHERE id_turno_caja = $1 AND id_tenant = $2 " +
            (modo == ModoDeEscritura.Pos ? "FOR SHARE" : "FOR UPDATE");
        ParametrosDeComando.Agregar(comando, idTurnoCaja);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                $"El turno {idTurnoCaja} de un gasto existente no es visible — invariante de FK violado.");
        }

        return new TurnoBloqueado(
            idTurnoCaja, lector.GetString(0) == "abierto", lector.IsDBNull(1) ? null : lector.GetInt32(1));
    }

    /// <summary>Mismos códigos que el alta para un pago ligado a una compra: sigue siendo de
    /// categoría proveedor y con el proveedor de la compra.</summary>
    private static void ExigirEdicionCoherenteConLaCompra(Pago pago, SolicitudDeEdicionDePago solicitud)
    {
        ExigirCategoriaCoherenteConLaCompra(solicitud.Categoria, pago.IdComprobanteCompra);

        if (pago.IdComprobanteCompra is not null && solicitud.IdProveedor != pago.IdProveedor)
        {
            throw new ErrorDominio(
                "proveedor_no_coincide_con_la_compra",
                "El proveedor indicado no coincide con el proveedor de la compra.", 400);
        }
    }

    /// <summary>Ajustes de cuenta corriente de proveedor (<see cref="TipoMovimientoCcProveedor.Ajuste"/>
    /// no admite <c>id_pago</c>: el pago queda referenciado en el detalle). El punto de venta es el
    /// del pago; un pago administrativo sin punto de venta solo pudo pagar a un proveedor al
    /// vincularse a una compra, y ese pago usó el punto de venta de la compra.</summary>
    private async Task EscribirAjustesDeProveedorAsync(
        int idTenant, Pago pago, IReadOnlyList<AjusteDeSaldoDeProveedor> ajustes, string detalle, int idEmpleado,
        DateTimeOffset momento, CancellationToken ct)
    {
        if (ajustes.Count == 0)
        {
            return;
        }

        var idPuntoVenta = pago.IdPuntoVenta ?? await PuntoVentaDeLaCompraAsync(pago.IdComprobanteCompra, ct);
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        foreach (var ajuste in ajustes)
        {
            var nuevoSaldo = await EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(
                conexion, transaccionCruda, idTenant, ajuste.IdProveedor, ajuste.Importe, ct);

            await EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(
                conexion, transaccionCruda, idTenant, ajuste.IdProveedor, momento, idPuntoVenta, idEmpleado,
                TipoMovimientoCcProveedor.Ajuste, idComprobanteCompra: null, idPago: null, ajuste.Importe, nuevoSaldo,
                detalle, ct);
        }
    }

    private async Task<int?> PuntoVentaDeLaCompraAsync(int? idComprobanteCompra, CancellationToken ct) =>
        idComprobanteCompra is null
            ? null
            : await db.ComprobantesCompra
                .Where(c => c.Id == idComprobanteCompra)
                .Select(c => (int?)c.IdPuntoVenta)
                .FirstOrDefaultAsync(ct);

    /// <summary>Recalcula el arqueo persistido de un turno cerrado con la misma derivación que el
    /// cierre (<see cref="LectorDeMovimientosDelTurno"/> + <see cref="CalculadorDeArqueo"/>) y el
    /// ancla pineada al cierre. El modo de cierre (clásico o por retiro) no queda persistido, así
    /// que el declarado de las filas existentes no se toca y las filas nuevas se declaran en 0:
    /// nadie declaró nada para ese medio. La marca del turno se escribe solo si alguna fila
    /// cambió.</summary>
    private async Task RecalcularArqueoAsync(
        int idTenant, TurnoBloqueado turno, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        var insumos = await lectorDeMovimientos.LeerAsync(turno.Id, ct);
        var idAncla = turno.IdMedioPagoEfectivo ?? ResolvedorDeMedioDeCajaFisica.Resolver(insumos.Actividad);
        var lineas = CalculadorDeArqueo.Calcular(insumos, idAncla);

        var filas = await db.ArqueosTurno.Where(a => a.IdTurnoCaja == turno.Id).ToListAsync(ct);
        var cambios = RecalculadorDeArqueo.Planificar(
            lineas, insumos.Actividad,
            filas.Select(f => new ArqueoPersistido(f.IdMedioPago, f.ImporteEsperado, f.ImporteEsperadoOriginal)).ToList());

        if (cambios.Count == 0)
        {
            return;
        }

        foreach (var cambio in cambios)
        {
            if (cambio.EsNueva)
            {
                db.ArqueosTurno.Add(new ArqueoTurno
                {
                    IdTenant = idTenant,
                    IdTurnoCaja = turno.Id,
                    IdMedioPago = cambio.IdMedioPago,
                    ImporteEsperado = cambio.ImporteEsperado,
                    ImporteDeclarado = 0m,
                    ImporteEsperadoOriginal = cambio.ImporteEsperadoOriginal
                });
                continue;
            }

            var fila = filas.Single(f => f.IdMedioPago == cambio.IdMedioPago);
            fila.ImporteEsperado = cambio.ImporteEsperado;
            fila.ImporteEsperadoOriginal = cambio.ImporteEsperadoOriginal;
        }

        await db.SaveChangesAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText =
            "UPDATE turnos_caja SET fecha_recalculo = $1, id_empleado_recalculo = $2, updated_at = $1 " +
            "WHERE id_turno_caja = $3 AND id_tenant = $4";
        ParametrosDeComando.Agregar(comando, momento);
        ParametrosDeComando.Agregar(comando, idEmpleado);
        ParametrosDeComando.Agregar(comando, turno.Id);
        ParametrosDeComando.Agregar(comando, idTenant);
        await comando.ExecuteNonQueryAsync(ct);
    }

    private static EstadoContableDePago EstadoContable(Pago pago) =>
        new(pago.Categoria, pago.IdProveedor, pago.IdMedioPago, pago.Importe);

    private static Dictionary<string, object?> PayloadDeEdicion(Pago pago) => new()
    {
        ["categoria"] = pago.Categoria,
        ["id_proveedor"] = pago.IdProveedor,
        ["id_area"] = pago.IdArea,
        ["concepto"] = pago.Concepto,
        ["detalle"] = pago.Detalle,
        ["id_medio_pago"] = pago.IdMedioPago,
        ["numero_factura"] = pago.NumeroFactura,
        ["importe"] = pago.Importe
    };

    /// <summary>Historial paginado (design: API Surface, <c>GET /api/gastos</c>) — mismo criterio
    /// de paginado que <c>ServicioDeTurnos.ListarAsync</c>.</summary>
    public async Task<PaginaDePagos> ListarAsync(
        int? idPuntoVenta = null,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = db.Pagos.AsQueryable();

        if (idPuntoVenta is { } pv)
        {
            query = query.Where(g => g.IdPuntoVenta == pv);
        }

        if (desde is { } d)
        {
            query = query.Where(g => g.Fecha >= d);
        }

        if (hasta is { } h)
        {
            query = query.Where(g => g.Fecha <= h);
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(g => g.Fecha)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(ProyeccionesDePago.Listado(db))
            .ToListAsync(ct);

        return new PaginaDePagos(items, total, pagina, tamanio);
    }

    /// <summary>Historial de gestión (design: API Surface, <c>GET /api/gastos/administracion</c>)
    /// — filtros amplios (empresa, punto de venta, categoría, origen de fondos, proveedor) y
    /// proyección con nombres resueltos. <c>desde</c>/<c>hasta</c> viajan como <see
    /// cref="DateTimeOffset"/> (mismo shape que <see cref="ListarAsync"/>, el filtro sobre
    /// <c>Fecha</c> es directo) — el picker de fecha de negocio es cosa del formulario de alta,
    /// no de este filtro de rango.
    ///
    /// dangling-fk-read-models: proveedor/área/medio de pago se resuelven con una subquery
    /// correlacionada equivalente a un LEFT JOIN (nunca un <c>Include</c>/join que dependa de que
    /// la fila exista) — un catálogo dado de baja lógica nunca hace desaparecer el pago de
    /// <c>items</c> ni desalinea <c>total</c>; el nombre sale <c>null</c> y el filtro
    /// <c>idProveedor</c> exige la fila VISIBLE (regla 2) para no matchear un id ya invisible por
    /// baja lógica.</summary>
    public async Task<PaginaDePagosDeAdministracion> ListarDeAdministracionAsync(
        int? idEmpresa = null,
        int? idPuntoVenta = null,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        CategoriaPago? categoria = null,
        OrigenFondosPago? origenFondos = null,
        int? idProveedor = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = db.Pagos.AsQueryable();

        if (idEmpresa is { } e) query = query.Where(g => g.IdEmpresa == e);
        if (idPuntoVenta is { } pv) query = query.Where(g => g.IdPuntoVenta == pv);
        if (desde is { } d) query = query.Where(g => g.Fecha >= d);
        if (hasta is { } h) query = query.Where(g => g.Fecha <= h);
        if (categoria is { } c) query = query.Where(g => g.Categoria == c);
        if (origenFondos is { } o) query = query.Where(g => g.OrigenFondos == o);
        if (idProveedor is { } idp)
        {
            // regla 2 (dangling-fk-read-models): matchea solo si el proveedor sigue VISIBLE — un
            // id de un proveedor ya dado de baja no puede matchear nada.
            query = query.Where(g => g.IdProveedor == idp && db.Proveedores.Any(p => p.Id == idp));
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(g => g.Fecha).ThenByDescending(g => g.Id)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(ProyeccionDeAdministracion())
            .ToListAsync(ct);

        return new PaginaDePagosDeAdministracion(items, total, pagina, tamanio);
    }

    /// <summary>stage-gasto-a-compra (PR4), judgment follow-up: <c>GET
    /// /api/gastos/administracion/{id}</c> — mismo shape/joins que <see
    /// cref="ListarDeAdministracionAsync"/> (dangling-fk-read-models), pero de una sola fila. Nace
    /// para que <c>POST /api/gastos/administracion</c> tenga un <c>Location</c> que realmente
    /// resuelve (antes apuntaba a <c>/api/gastos/{id}</c>, que exige <c>OperacionDePos</c> pero
    /// devuelve el shape reducido de <see cref="PagoListado"/> — un mismatch de contrato para un
    /// cliente que siguiera el header).</summary>
    public async Task<PagoDeAdministracionListado> ObtenerDeAdministracionAsync(int id, CancellationToken ct = default) =>
        await db.Pagos.Where(g => g.Id == id).Select(ProyeccionDeAdministracion()).FirstOrDefaultAsync(ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant".
            ?? throw ErrorDominio.NoEncontrado($"No existe el gasto {id}.");

    private Expression<Func<Pago, PagoDeAdministracionListado>> ProyeccionDeAdministracion() => g =>
        new PagoDeAdministracionListado(
            g.Id,
            g.Fecha,
            g.IdEmpresa,
            g.IdPuntoVenta,
            g.IdTurnoCaja,
            g.Categoria,
            g.IdProveedor,
            g.IdProveedor == null ? null : db.Proveedores.Where(p => p.Id == g.IdProveedor).Select(p => p.RazonSocial).FirstOrDefault(),
            g.IdArea,
            g.IdArea == null ? null : db.Areas.Where(a => a.Id == g.IdArea).Select(a => a.Nombre).FirstOrDefault(),
            g.Concepto,
            g.Detalle,
            g.IdMedioPago,
            db.MediosPago.Where(m => m.Id == g.IdMedioPago).Select(m => m.Nombre).FirstOrDefault(),
            g.NumeroFactura,
            g.Importe,
            g.OrigenFondos,
            g.IdComprobanteCompra,
            g.IdTurnoCaja != null && db.TurnosCaja.Any(t => t.Id == g.IdTurnoCaja && t.Estado == EstadoTurno.Abierto),
            g.IdMovimientoPersonal);

    // ---- validación de dominio -------------------------------------------------------------

    /// <summary>Mismo código que la CHECK de esquema <c>ck_pagos_importe_positivo</c> (design:
    /// Backstop Map, Slice 1 task 1.7): esta validación de servicio es la UX rápida, la CHECK es
    /// el contrato real (db-error-backstops — nunca tratar el pre-check como la protección).
    /// (spec: Importe Must Be Positive).</summary>
    private static void ExigirImporteValido(decimal importe)
    {
        if (importe <= 0m)
        {
            throw new ErrorDominio("gasto_importe_invalido", "El importe del gasto tiene que ser positivo.", 400);
        }
    }

    /// <summary>stage-gastos-turno-carga-simple (web slice — Ways.Web va a mandar
    /// <c>concepto = observaciones.trim() || "Gasto del turno"</c>, así que un blanco/espacios
    /// nunca debería llegar desde el POS, pero el contrato HTTP no lo impide): chequeo de dominio
    /// puro, ANTES de cualquier consulta, mismo criterio que <see cref="ExigirImporteValido"/> —
    /// nunca dejar que un <c>concepto</c> en blanco/espacios llegue a persistirse.</summary>
    private static void ExigirConceptoValido(string concepto)
    {
        if (string.IsNullOrWhiteSpace(concepto))
        {
            throw new ErrorDominio(
                "gasto_concepto_requerido", "El concepto del gasto es obligatorio.", 400);
        }
    }

    /// <summary>spec: pagos / A Comprobante Compra Link Requires Categoria Proveedor — chequeo de
    /// dominio puro, sin DB: un pago ligado a una compra SIEMPRE tiene que ser de categoría
    /// proveedor (la compra la paga un proveedor, nunca sueldos/viáticos/etc.).</summary>
    private static void ExigirCategoriaCoherenteConLaCompra(CategoriaPago categoria, int? idComprobanteCompra)
    {
        if (idComprobanteCompra is not null && categoria != CategoriaPago.Proveedor)
        {
            throw new ErrorDominio(
                "gasto_de_compra_debe_ser_de_proveedor",
                "Un gasto ligado a una compra tiene que ser de categoría proveedor.", 400);
        }
    }

    /// <summary>stage-gastos-admin-retroactivos (PR3): <see
    /// cref="EscriturasDeCuentaCorrienteProveedor.ValidarFormaPorTipo"/> exige <c>id_punto_venta</c>
    /// no nulo para CUALQUIER movimiento de CC de proveedor que no sea <c>Apertura</c> — un pago
    /// administrativo de categoría <see cref="CategoriaPago.Proveedor"/> siempre escribe un
    /// <c>Pago</c> (<see cref="EscribirPagoAProveedorAsync"/>), así que el punto de venta deja de
    /// ser opcional en ESTE caso puntual (a diferencia del resto de las categorías, donde el
    /// pago administrativo nunca lo necesita). Chequeo de dominio puro, antes de tocar la base —
    /// mismo criterio que <see cref="ExigirCategoriaCoherenteConLaCompra"/>.</summary>
    private static void ExigirPuntoVentaParaElPagoAProveedor(CategoriaPago categoria, int? idPuntoVenta)
    {
        if (categoria == CategoriaPago.Proveedor && idPuntoVenta is null)
        {
            throw new ErrorDominio(
                "gasto_de_proveedor_requiere_punto_de_venta",
                "Un gasto de categoría proveedor tiene que indicar un punto de venta.", 400);
        }
    }

    /// <summary>stage-gastos-admin-retroactivos (PR3): "hoy" SIEMPRE resuelto en la zona horaria
    /// de la empresa/punto de venta (mismo criterio que <c>ServicioDePresupuestos.
    /// EjecutarEnvioAsync</c>, design decisión 10/11 de esa etapa) — jamás <c>reloj.Ahora.
    /// UtcDateTime</c> directo, que compararía contra el día UTC del servidor, no el día que el
    /// admin ve en su reloj local.</summary>
    private static void ExigirFechaNoFutura(DateOnly fecha, DateTimeOffset momento, TimeZoneInfo zona)
    {
        var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(momento, zona).DateTime);
        if (fecha > hoy)
        {
            throw new ErrorDominio(
                "gasto_fecha_futura", "La fecha del gasto no puede ser futura.", 400);
        }
    }

    /// <summary>Mismo criterio que <c>Ways.Domain.Stock.ReglaDeReposicion.InstanteLocal</c>
    /// (privado a esa clase, no reusable acá): medianoche LOCAL de la fecha de negocio, resuelta
    /// con <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> — nunca <c>ConvertTimeToUtc</c>, que
    /// tira <c>ArgumentException</c> sobre una medianoche local inválida (un salto de DST exacto a
    /// las 24:00). El resultado es el <see cref="Pago.Fecha"/> que persiste — la ÚNICA columna
    /// que lleva la fecha de negocio elegida por el admin; los movimientos de ledger (tesorería,
    /// pago a proveedor) llevan <c>momento</c> (ver <see cref="InsertarPagoDeAdministracionAsync"/>),
    /// nunca esta fecha retroactiva.</summary>
    private static DateTimeOffset FechaDeNegocioAInstante(DateOnly fecha, TimeZoneInfo zona)
    {
        var local = fecha.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var offset = zona.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>ADR-13: precedencia punto de venta &gt; empresa &gt; default declarado, misma
    /// resolución que <c>AlcanceDeListadoHttp.ResolverAsync</c> pero reusando <see
    /// cref="ServicioDeParametros"/> directo (esta clase ya conoce <paramref name="idEmpresa"/>,
    /// resuelto arriba con su propio 404 — no hace falta la variante HTTP que resuelve desde un
    /// punto de venta).</summary>
    private async Task<TimeZoneInfo> ResolverZonaAsync(int idEmpresa, int? idPuntoVenta, CancellationToken ct)
    {
        var resuelto = await servicioDeParametros.ResolverAsync(
            ParametroConocido.ZonaHoraria.Clave, idEmpresa, idPuntoVenta, ct);
        var zonaId = JsonSerializer.Deserialize<string>(resuelto.Valor)!;
        return TimeZoneInfo.FindSystemTimeZoneById(zonaId);
    }

    /// <summary>spec: "exclude cuenta-corriente medios like the POS does" — el POS (<see
    /// cref="RegistrarAsync"/>) solo lo filtra client-side (<c>mediosValidosParaPago</c>,
    /// Ways.Web); este es el primer camino de alta de pago expuesto directo a un cliente HTTP sin
    /// ese filtro de UI garantizado (un admin puede llamar la API a mano), así que acá el guard es
    /// de servidor: cuenta corriente es el crédito del CLIENTE en una venta, nunca una salida real
    /// de dinero de una caja/tesorería.</summary>
    private async Task<MedioPago> ExigirMedioPagoValidoAsync(int idMedioPago, CancellationToken ct)
    {
        var medioPago = await db.MediosPago.FirstOrDefaultAsync(m => m.Id == idMedioPago, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el medio de pago {idMedioPago}.");

        if (medioPago.Comportamiento == ComportamientoMedioPago.CuentaCorriente)
        {
            throw new ErrorDominio(
                "gasto_medio_pago_cuenta_corriente_invalido",
                "Un gasto no puede pagarse con un medio de cuenta corriente.", 400);
        }

        return medioPago;
    }

    private async Task<Empresa> ResolverEmpresaAsync(int idEmpresa, CancellationToken ct) =>
        await db.Empresas.FirstOrDefaultAsync(e => e.Id == idEmpresa, ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — mismo criterio que
            // ResolverPuntoVentaAsync.
            ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");

    /// <summary>400, no 404: el punto de venta SÍ existe (ya pasó <see
    /// cref="ResolverPuntoVentaAsync"/>, que da 404 si no) — lo que está mal es la combinación con
    /// la empresa del pago, mismo criterio 400 que <c>proveedor_no_coincide_con_la_compra</c> en
    /// <see cref="ExigirCompraLigableAsync"/>.</summary>
    private async Task ExigirPuntoVentaDeLaEmpresaAsync(int idPuntoVenta, int idEmpresa, CancellationToken ct)
    {
        var puntoVenta = await ResolverPuntoVentaAsync(idPuntoVenta, ct);
        if (puntoVenta.IdEmpresa != idEmpresa)
        {
            throw new ErrorDominio(
                "punto_venta_no_pertenece_a_la_empresa",
                "El punto de venta indicado no pertenece a la empresa del gasto.", 400);
        }
    }

    // ---- persistencia -------------------------------------------------------------------------

    /// <summary>task 4.17 (mismo guard que <c>ServicioDeTurnos.RegistrarMovimientoAsync</c>, reusado
    /// vía <see cref="ServicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync"/> como PRIMER statement
    /// de esta transacción de escritura): el turno ya vino resuelto como abierto (<see
    /// cref="ServicioDeTurnos.ResolverTurnoAbiertoAsync"/>, arriba, ANTES de abrir esta
    /// transacción) — sin este re-chequeo bajo <c>FOR SHARE</c>, un pago concurrente a un
    /// cierre podría comitear dentro de un turno cuyo arqueo YA se derivó (design decisión 1).
    ///
    /// Slice 4 (design decisión 7): el guard de la compra ligada corre DESPUÉS de ese lock de
    /// turno — mismo orden que el design pseudocódigo (Transactions — PAGO LIGADO A UNA
    /// COMPRA).
    ///
    /// stage-gastos-origen-fondos-pos (PR2): cuando <see cref="SolicitudDePago.OrigenFondos"/> es
    /// <see cref="OrigenFondosPago.Tesoreria"/>, <see cref="EscribirMovimientoDeTesoreriaAsync"/>
    /// corre COMO ÚLTIMO paso antes del commit — después del turno <c>FOR SHARE</c>, la compra
    /// ligada <c>FOR SHARE</c> (si aplica) y el pago a proveedor (si aplica): mismo orden relativo
    /// que <c>ServicioDeTurnos.InsertarArqueosYTesoreriaAsync</c> (locks de fila primero, advisory
    /// de tesorería de la empresa al final) — ver el doc-comment de clase de
    /// <see cref="EscriturasDeTesoreria"/> para el análisis completo de por qué esto no puede
    /// formar un ciclo con el cierre.</summary>
    private async Task<Pago> InsertarPagoAsync(
        int idTenant, int idEmpresa, int idTurnoCaja, SolicitudDePago solicitud, int idEmpleado,
        DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        await servicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync(idTurnoCaja, ct);

        var idProveedor = solicitud.IdProveedor;
        if (solicitud.IdComprobanteCompra is { } idComprobanteCompra)
        {
            idProveedor = await ExigirCompraLigableAsync(idComprobanteCompra, idTenant, solicitud.IdProveedor, ct);
        }

        var pago = new Pago
        {
            IdTenant = idTenant,
            IdEmpresa = idEmpresa,
            Fecha = momento,
            IdPuntoVenta = solicitud.IdPuntoVenta,
            IdTurnoCaja = idTurnoCaja,
            IdEmpleado = idEmpleado,
            Categoria = solicitud.Categoria,
            OrigenFondos = solicitud.OrigenFondos,
            IdProveedor = idProveedor,
            IdArea = solicitud.IdArea,
            Concepto = solicitud.Concepto,
            Detalle = solicitud.Detalle,
            IdMedioPago = solicitud.IdMedioPago,
            NumeroFactura = solicitud.NumeroFactura,
            Importe = solicitud.Importe,
            IdComprobanteCompra = solicitud.IdComprobanteCompra,
            CreatedAt = momento,
            UpdatedAt = momento
        };

        db.Pagos.Add(pago);
        await db.SaveChangesAsync(ct);

        // stage-15-cc-proveedores-ledger, Slice 3 (design decisión 7, tasks.md task 3.1): el
        // movimiento `pago` va DESPUÉS de SaveChangesAsync — id_pago es identity, recién existe
        // acá — y es el ÚLTIMO lock de fila (for update) antes del commit. El predicado es
        // ServicioDeSaldoDeProveedor.cs:39-43 VERBATIM (la fórmula retirada que este ledger
        // reemplaza): categoría proveedor Y id_proveedor no nulo. `idProveedor` ya es el valor
        // RESUELTO (derivado por ExigirCompraLigableAsync cuando la solicitud no lo trae) — el
        // movimiento usa el mismo valor que la fila guarda, nunca el crudo de la solicitud.
        if (solicitud.Categoria == CategoriaPago.Proveedor && idProveedor is { } idProveedorDelPago)
        {
            await EscribirPagoAProveedorAsync(
                idTenant, idProveedorDelPago, solicitud.IdPuntoVenta, solicitud.Importe, pago, idEmpleado, momento, ct);
        }

        if (solicitud.OrigenFondos == OrigenFondosPago.Tesoreria)
        {
            await EscribirMovimientoDeTesoreriaAsync(idTenant, idEmpresa, pago, idEmpleado, momento, ct);
        }

        await transaccion.CommitAsync(ct);

        return pago;
    }

    /// <summary>stage-gastos-admin-retroactivos (PR3): mismo protocolo exacto que <see
    /// cref="InsertarPagoAsync"/> (locks de fila primero, advisory de tesorería de la empresa al
    /// final) MENOS el turno — acá no hay ningún turno que lockear/re-chequear (owner's use case
    /// 2: el pago administrativo nunca pasa por un turno). Orden: compra ligada <c>FOR SHARE</c>
    /// (si aplica) → insert del pago → pago a proveedor (si aplica, su propio lock de fila) →
    /// movimiento de tesorería (advisory de empresa, ÚLTIMO) → commit. <see
    /// cref="Pago.OrigenFondos"/> es SIEMPRE <see cref="OrigenFondosPago.Tesoreria"/> acá (spec:
    /// "never from a POS drawer") — nunca condicional como en <see cref="InsertarPagoAsync"/>.
    ///
    /// <paramref name="fechaDeNegocio"/> (posiblemente retroactiva) es la ÚNICA que persiste en
    /// <c>Pago.Fecha</c>; el movimiento de tesorería y el pago a proveedor llevan <paramref
    /// name="momento"/> (el instante REAL del alta) — decisión documentada en el doc-comment de
    /// clase de este método más abajo y en <see cref="FechaDeNegocioAInstante"/>: los dos ledgers
    /// se LEEN por <c>fecha</c> (tesorería: por <c>id</c> de cadena, irrelevante; cuenta corriente
    /// de proveedor: <c>ORDER BY fecha DESC, id DESC</c>, <see
    /// cref="Ways.Application.CuentaCorriente.ServicioDeCuentaCorrienteDeProveedor"/>) y el
    /// <c>saldo</c>/<c>final</c> que cada fila graba es el acumulado en el momento REAL del
    /// insert, nunca recalculado para su posición cronológica — grabar una fecha retroactiva en
    /// esos ledgers dejaría un saldo/final "del futuro" apareciendo entre filas más viejas.
    /// <c>Pago.Fecha</c> no tiene ese problema (no es un ledger acumulado, es un documento de
    /// autoría) y por eso sí lleva la fecha real elegida por el admin.</summary>
    private async Task<Pago> InsertarPagoDeAdministracionAsync(
        int idTenant, int idEmpresa, DateTimeOffset fechaDeNegocio, SolicitudDePagoDeAdministracion solicitud,
        int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var idProveedor = solicitud.IdProveedor;
        if (solicitud.IdComprobanteCompra is { } idComprobanteCompra)
        {
            idProveedor = await ExigirCompraLigableAsync(idComprobanteCompra, idTenant, solicitud.IdProveedor, ct);
        }

        var pago = await PersistirPagoDeAdministracionAsync(
            idTenant, idEmpresa, fechaDeNegocio, solicitud, idProveedor, idEmpleado, momento, ct);

        await transaccion.CommitAsync(ct);

        return pago;
    }

    /// <summary>Escrituras del pago administrativo dentro de la transacción del llamador, que ya
    /// tomó el lock de la compra (si hay) y es quien comitea. Las comparten el alta administrativa y
    /// <see cref="PagarCompraAsync"/>: un solo camino de escritura del pago, del pago a proveedor y
    /// del egreso de tesorería. <paramref name="idProveedor"/> ya viene resuelto contra la
    /// compra.</summary>
    private async Task<Pago> PersistirPagoDeAdministracionAsync(
        int idTenant, int idEmpresa, DateTimeOffset fechaDeNegocio, SolicitudDePagoDeAdministracion solicitud,
        int? idProveedor, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        var pago = new Pago
        {
            IdTenant = idTenant,
            IdEmpresa = idEmpresa,
            Fecha = fechaDeNegocio,
            IdPuntoVenta = solicitud.IdPuntoVenta,
            IdTurnoCaja = null,
            IdEmpleado = idEmpleado,
            Categoria = solicitud.Categoria,
            OrigenFondos = OrigenFondosPago.Tesoreria,
            IdProveedor = idProveedor,
            IdArea = solicitud.IdArea,
            Concepto = solicitud.Concepto,
            Detalle = solicitud.Detalle,
            IdMedioPago = solicitud.IdMedioPago,
            NumeroFactura = solicitud.NumeroFactura,
            Importe = solicitud.Importe,
            IdComprobanteCompra = solicitud.IdComprobanteCompra,
            CreatedAt = momento,
            UpdatedAt = momento
        };

        db.Pagos.Add(pago);
        await db.SaveChangesAsync(ct);

        if (solicitud.Categoria == CategoriaPago.Proveedor && idProveedor is { } idProveedorDelPago)
        {
            await EscribirPagoAProveedorAsync(
                idTenant, idProveedorDelPago, solicitud.IdPuntoVenta, solicitud.Importe, pago, idEmpleado, momento, ct);
        }

        // ÚLTIMO lock de esta transacción (mismo criterio que InsertarPagoAsync) — acá no hay
        // ningún lock de turno antes: la compra ligada FOR SHARE (si aplica) y el pago a proveedor
        // (si aplica) son los únicos locks de fila que pueden preceder a este advisory.
        await EscribirMovimientoDeTesoreriaAsync(idTenant, idEmpresa, pago, idEmpleado, momento, ct);

        return pago;
    }

    /// <summary>stage-gastos-origen-fondos-pos (PR2): un pago de origen
    /// <see cref="OrigenFondosPago.Tesoreria"/> se paga DIRECTO del fondo de tesorería de la
    /// empresa, sin pasar por el efectivo del cajón — sigue atado al turno abierto
    /// (<c>pago.IdTurnoCaja</c>, trazabilidad) pero NO afecta ningún arqueo (<see
    /// cref="Ways.Application.Caja.LectorDeMovimientosDelTurno"/> excluye este origen de
    /// <c>pagosPorMedio</c>). Reusa <see cref="EscriturasDeTesoreria"/> — el mismo protocolo
    /// lock+lectura+insert que el cierre — para que la cadena de tesorería tenga un solo escritor
    /// real. <c>Ingreso = 0</c>/<c>Egreso = pago.Importe</c> (design: la tesorería se reduce en el
    /// momento del pago, no se difiere al cierre); <c>Tipo = Pago</c>, <c>IdPago = pago.Id</c>
    /// (único por <c>ux_movimientos_tesoreria_id_pago</c> — este es el ÚNICO call site que puebla
    /// esa columna).</summary>
    private async Task EscribirMovimientoDeTesoreriaAsync(
        int idTenant, int idEmpresa, Pago pago, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // Último lock de esta transacción, después del turno/compra/proveedor (ver el doc-comment
        // de InsertarPagoAsync) — mismo orden relativo que el cierre, nunca puede formar un ciclo.
        await EscriturasDeTesoreria.TomarLockDeEmpresaAsync(conexion, transaccionCruda, idTenant, idEmpresa, ct);

        await EscriturasDeTesoreria.ApendearAsync(
            db, idTenant, idEmpresa, pago.IdPuntoVenta, momento, TipoMovimientoTesoreria.Pago, pago.IdTurnoCaja,
            pago.Id, pago.Concepto, ingreso: 0m, egreso: pago.Importe, idEmpleado, ct);
    }

    /// <summary>stage-15-cc-proveedores-ledger, Slice 3: el ÚNICO call site de
    /// <see cref="EscriturasDeCuentaCorrienteProveedor"/> para pagos — <c>importe = −importe</c>
    /// (el pago REDUCE el saldo), <c>id_pago</c> = la fila recién flusheada,
    /// <c>id_comprobante_compra</c> = el vínculo del pago (puede ser null: la imputación es
    /// opcional, spec: An Unlinked Proveedor Pago Reduces The Saldo Without Imputación). Misma
    /// conexión/transacción cruda que <see cref="ExigirCompraLigableAsync"/> reutiliza — nunca una
    /// segunda transacción.
    ///
    /// stage-gastos-admin-retroactivos (PR3): parámetros primitivos (no el <c>SolicitudDePago</c>
    /// del POS) para que <see cref="InsertarPagoDeAdministracionAsync"/> lo reuse sin duplicar el
    /// SQL — <paramref name="idPuntoVenta"/> viaja nullable (el camino administrativo no siempre
    /// trae uno).</summary>
    private async Task EscribirPagoAProveedorAsync(
        int idTenant, int idProveedor, int? idPuntoVenta, decimal importe, Pago pago, int idEmpleado,
        DateTimeOffset momento, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var nuevoSaldo = await EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(
            conexion, transaccionCruda, idTenant, idProveedor, -importe, ct);

        await EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(
            conexion, transaccionCruda, idTenant, idProveedor, momento, idPuntoVenta, idEmpleado,
            TipoMovimientoCcProveedor.Pago, pago.IdComprobanteCompra, pago.Id, -importe, nuevoSaldo,
            detalle: null, ct);
    }

    /// <summary>design decisión 7: <c>SELECT ... FOR SHARE</c> crudo sobre el header de la compra
    /// — cierra el TOCTOU contra una anulación concurrente de la MISMA compra. La anulación toma
    /// el lock EXCLUSIVO del header como su propio primer statement (<c>ServicioDeCompras.
    /// MarcarAnuladaAsync</c>): este pago o bien bloquea y retoma viendo <c>anulada</c> ya
    /// comiteada (<c>409 compra_anulada</c>), o gana la carrera y queda simplemente visible para
    /// la anulación que llega después — ambos estados representables, ninguno corrupto. <c>estado
    /// ::text</c> en vez del enum nativo, mismo criterio cauteloso que
    /// <see cref="ServicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync"/>. Devuelve el
    /// <c>id_proveedor</c> de la compra: se usa para derivarlo cuando el request no lo trae (spec:
    /// pagos / A Comprobante Compra Link — <c>id_proveedor</c> ausente se deriva, distinto se
    /// rechaza).</summary>
    private async Task<int> ExigirCompraLigableAsync(
        int idComprobanteCompra, int idTenant, int? idProveedorSolicitado, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccionCruda;
        comando.CommandText =
            "SELECT estado::text, id_proveedor FROM comprobantes_compra " +
            "WHERE id_comprobante_compra = $1 AND id_tenant = $2 FOR SHARE";
        ParametrosDeComando.Agregar(comando, idComprobanteCompra);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant".
            throw ErrorDominio.NoEncontrado($"No existe la compra {idComprobanteCompra}.");
        }

        var estado = lector.GetString(0);
        var idProveedorDeLaCompra = lector.GetInt32(1);

        if (estado == "anulada")
        {
            throw new ErrorDominio("compra_anulada", "La compra ligada está anulada.", 409);
        }

        if (estado != "confirmada")
        {
            throw new ErrorDominio(
                "compra_no_confirmada", "La compra ligada todavía no está confirmada.", 409);
        }

        if (idProveedorSolicitado is { } solicitado && solicitado != idProveedorDeLaCompra)
        {
            throw new ErrorDominio(
                "proveedor_no_coincide_con_la_compra",
                "El proveedor indicado no coincide con el proveedor de la compra.", 400);
        }

        return idProveedorDeLaCompra;
    }

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    // ---- pagos de sueldo de un pago de personal -------------------------------------------

    /// <summary>Validaciones previas a la transacción de un pago de personal, con las mismas reglas y los
    /// mismos códigos que los pagos que el pago reemplaza: con origen tesorería, las del alta administrativa
    /// (empresa 404, punto de venta opcional de esa empresa, medio de pago visible y que no sea cuenta
    /// corriente, fecha no futura en la zona de la empresa); con origen cajón, las del alta del POS (punto de
    /// venta 404 y turno abierto 409). El pago solo de descuento no tiene origen y solo resuelve el punto de
    /// venta y la fecha. Este método no escribe nada.</summary>
    public async Task<EntornoDePagoDeSueldo> PrepararPagosDeSueldoAsync(
        OrigenFondosPago? origenFondos, int? idEmpresa, int? idPuntoVenta, DateOnly fecha,
        IReadOnlyCollection<int> idsMedioPago, CancellationToken ct = default)
    {
        var momento = reloj.Ahora;
        int idEmpresaResuelta;
        int? idPuntoVentaResuelto = null;

        if (origenFondos == OrigenFondosPago.Tesoreria)
        {
            idEmpresaResuelta = (await ResolverEmpresaAsync(idEmpresa!.Value, ct)).Id;
            if (idPuntoVenta is { } idPuntoVentaPedido)
            {
                await ExigirPuntoVentaDeLaEmpresaAsync(idPuntoVentaPedido, idEmpresaResuelta, ct);
                idPuntoVentaResuelto = idPuntoVentaPedido;
            }
        }
        else
        {
            var puntoVenta = await ResolverPuntoVentaAsync(idPuntoVenta!.Value, ct);
            idEmpresaResuelta = puntoVenta.IdEmpresa;
            idPuntoVentaResuelto = puntoVenta.Id;
        }

        int? idTurnoCaja = null;
        if (origenFondos == OrigenFondosPago.CajaTurno)
        {
            idTurnoCaja = (await servicioDeTurnos.ResolverTurnoAbiertoAsync(idPuntoVentaResuelto!.Value, ct)).Id;
        }

        var nombresDeMedios = new Dictionary<int, string>();
        foreach (var idMedioPago in idsMedioPago.Distinct())
        {
            nombresDeMedios[idMedioPago] = (await ExigirMedioPagoValidoAsync(idMedioPago, ct)).Nombre;
        }

        var zona = await ResolverZonaAsync(idEmpresaResuelta, idPuntoVentaResuelto, ct);
        ExigirFechaNoFutura(fecha, momento, zona);

        return new EntornoDePagoDeSueldo(
            idEmpresaResuelta, idPuntoVentaResuelto, idTurnoCaja, FechaDeNegocioAInstante(fecha, zona), nombresDeMedios);
    }

    /// <summary>Un pago de categoría sueldos por línea de dinero, dentro de la transacción del llamador, que ya
    /// tomó el lock del turno (origen cajón) y es quien comitea. Es el mismo camino de escritura que el alta del
    /// POS y el alta administrativa: el pago del cajón entra al arqueo del turno por su origen
    /// (<see cref="LectorDeMovimientosDelTurno"/>) y el de tesorería escribe su egreso encadenado con
    /// <see cref="EscribirMovimientoDeTesoreriaAsync"/>, cuyo advisory de la empresa es el ÚLTIMO lock de la
    /// transacción. El pago de cajón lleva el instante real como fecha, igual que el del POS; el de tesorería,
    /// la fecha de negocio elegida. Cada pago apunta al movimiento <c>pago</c> del libro de personal.</summary>
    public async Task<IReadOnlyList<Pago>> PersistirPagosDeSueldoAsync(
        int idTenant, EntornoDePagoDeSueldo entorno, OrigenFondosPago origenFondos, string nombreDelPersonal,
        int idMovimientoPersonal, IReadOnlyList<LineaDePagoDePersonal> lineas, int idEmpleado, DateTimeOffset momento,
        CancellationToken ct = default)
    {
        var pagos = lineas.Select(linea => new Pago
        {
            IdTenant = idTenant,
            IdEmpresa = entorno.IdEmpresa,
            Fecha = origenFondos == OrigenFondosPago.Tesoreria ? entorno.FechaDeNegocio : momento,
            IdPuntoVenta = entorno.IdPuntoVenta,
            IdTurnoCaja = entorno.IdTurnoCaja,
            IdEmpleado = idEmpleado,
            Categoria = CategoriaPago.Sueldos,
            OrigenFondos = origenFondos,
            Concepto = $"Pago de personal: {nombreDelPersonal}",
            Detalle = linea.Referencia,
            IdMedioPago = linea.IdMedioPago,
            Importe = linea.Importe,
            IdMovimientoPersonal = idMovimientoPersonal,
            CreatedAt = momento,
            UpdatedAt = momento
        }).ToList();

        db.Pagos.AddRange(pagos);
        await db.SaveChangesAsync(ct);

        if (origenFondos == OrigenFondosPago.Tesoreria)
        {
            foreach (var pago in pagos)
            {
                await EscribirMovimientoDeTesoreriaAsync(idTenant, entorno.IdEmpresa, pago, idEmpleado, momento, ct);
            }
        }

        return pagos;
    }

    // ---- resolución interna ---------------------------------------------------------------

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — mismo criterio que
            // ServicioDeTurnos.ResolverPuntoVentaAsync/ServicioDeStock.ResolverPuntoVentaAsync/
            // ServicioDeVentas.ResolverPuntoVentaAsync.
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    /// <summary>judgment-day PR3 follow-up: mismo criterio EXACTO que
    /// <c>ServicioDeCompras.ResolverProveedorAsync</c> — <c>db.Proveedores</c> ya trae el filtro
    /// global de baja lógica, así que un proveedor dado de baja también da 404 acá (OD4: una
    /// referencia NUEVA solo puede apuntar a una fila VISIBLE).</summary>
    private async Task<Proveedor> ResolverProveedorAsync(int idProveedor, CancellationToken ct) =>
        await db.Proveedores.FirstOrDefaultAsync(p => p.Id == idProveedor, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el proveedor {idProveedor}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // OperacionDePos (capa de API) ya exige un actor de tenant — un actor de plataforma
            // (root) nunca llega hasta acá. Defensa en profundidad, mismo criterio que
            // ServicioDeTurnos.ExigirTenantDeLaSesion.
            ?? throw new InvalidOperationException(
                "ServicioDePagos requiere un actor de tenant; OperacionDePos no admite plataforma.");

    // ---- proyecciones -----------------------------------------------------------------------

    private static PagoRegistrado Proyectar(Pago pago) => new(
        pago.Id,
        pago.IdTurnoCaja,
        pago.IdPuntoVenta,
        pago.Fecha,
        pago.Categoria,
        pago.IdProveedor,
        pago.IdArea,
        pago.Concepto,
        pago.Detalle,
        pago.IdMedioPago,
        pago.NumeroFactura,
        pago.Importe,
        pago.IdEmpleado,
        pago.IdComprobanteCompra,
        pago.OrigenFondos);
}
