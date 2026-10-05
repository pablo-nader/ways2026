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
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;

namespace Ways.Application.Gastos;

/// <summary>
/// Captura de gastos contra un turno abierto (design: Table Shapes — write path C; tasks.md
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
public class ServicioDeGastos(
    IWaysDbContext db, ServicioDeTurnos servicioDeTurnos, ServicioDeParametros servicioDeParametros,
    IRelojDelSistema reloj, IContextoDeUsuario contexto, LectorDeMovimientosDelTurno lectorDeMovimientos,
    ServicioDeAuditoria auditoria)
{
    /// <summary>Resuelve el punto de venta (404 ADR-8) antes que el turno abierto (spec: Gasto
    /// Requires An Open Turno) — mismo orden que <c>ServicioDeVentas.EmitirAsync</c> (design
    /// decisión 11): un punto de venta apócrifo tiene que dar 404, nunca el 409 de "sin turno
    /// abierto" de un punto de venta que ni siquiera existe.</summary>
    public async Task<GastoRegistrado> RegistrarAsync(SolicitudDeGasto solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        ExigirImporteValido(solicitud.Importe);
        ExigirConceptoValido(solicitud.Concepto);
        // spec: gastos / A Comprobante Compra Link Requires Categoria Proveedor — "rejected
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
        var gasto = await estrategia.ExecuteAsync(async () =>
            await InsertarGastoAsync(
                idTenant, puntoVenta.IdEmpresa, turno.Id, solicitud, idEmpleado, momento, ct));

        return Proyectar(gasto);
    }

    /// <summary>stage-gastos-admin-retroactivos (PR3), owner's use case 2: alta administrativa de
    /// un gasto SIN turno, pagado directo de la tesorería de la empresa — puede llevar una
    /// <see cref="SolicitudDeGastoDeAdministracion.Fecha"/> retroactiva. A diferencia de <see
    /// cref="RegistrarAsync"/> (que resuelve el punto de venta y DESPUÉS el turno abierto), acá no
    /// hay ningún turno que resolver: el orden de validación es empresa (404 ADR-8) → punto de
    /// venta opcional (404 si no existe, 400 si es de otra empresa) → medio de pago (excluye
    /// cuenta corriente, mismo criterio que el POS) → fecha (nunca futura, resuelta en la zona
    /// horaria de la empresa/punto de venta) — recién ahí se abre la transacción de
    /// escritura.</summary>
    public async Task<GastoRegistrado> RegistrarDeAdministracionAsync(
        SolicitudDeGastoDeAdministracion solicitud, CancellationToken ct = default)
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
        var gasto = await estrategia.ExecuteAsync(async () =>
            await InsertarGastoDeAdministracionAsync(
                idTenant, empresa.Id, fechaDeNegocio, solicitud, idEmpleado, momento, ct));

        return Proyectar(gasto);
    }

    /// <summary>Paga una compra confirmada en UNA transacción: crea el gasto administrativo (categoría
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
    /// leído después del lock → 3) fila del gasto (insert, sin lock) → 4) fila del proveedor
    /// (<c>UPDATE saldo</c>, último lock de fila) → 5) advisory de tesorería de la empresa, ÚLTIMO.
    /// Es el mismo orden que el alta administrativa (<see cref="InsertarGastoDeAdministracionAsync"/>)
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

        var gasto = await PersistirGastoDeAdministracionAsync(
            idTenant, puntoVenta.IdEmpresa, fechaDeNegocio,
            new SolicitudDeGastoDeAdministracion(
                solicitud.Fecha, puntoVenta.IdEmpresa, puntoVenta.Id, CategoriaGasto.Proveedor, compra.IdProveedor,
                IdArea: null, concepto, Detalle: null, solicitud.IdMedioPago, NumeroFactura: null, solicitud.Importe,
                idComprobanteCompra),
            compra.IdProveedor, idEmpleado, momento, ct);

        await transaccion.CommitAsync(ct);

        return new ResultadoDePagoDeCompra(
            Proyectar(gasto), pagado + solicitud.Importe, saldoPendiente - solicitud.Importe);
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

    /// <summary>stage-gasto-a-compra (PR4), owner requirement: cualquier gasto (POS o admin) se
    /// puede ligar a una compra DESPUÉS de creado — a diferencia de
    /// <see cref="SolicitudDeGasto.IdComprobanteCompra"/>/<see
    /// cref="SolicitudDeGastoDeAdministracion.IdComprobanteCompra"/>, que solo aplican en el alta.
    ///
    /// Orden de locks de la transacción (single-read-under-lock + deadlock-safe ordering):
    /// 1) compra <c>FOR SHARE</c> (mismo statement/orden relativo que <see
    ///    cref="ExigirCompraLigableAsync"/> en el alta: PRIMER lock, así que un alta concurrente
    ///    con <c>idComprobanteCompra</c> y esta vinculación nunca se ordenan al revés) →
    /// 2) gasto <c>FOR UPDATE</c> (lock-only, mismo patrón que <c>ServicioDeOrganizacion.
    ///    TomarLockDePuntoVentaAsync</c> — la ÚNICA lectura EF de la fila nace DESPUÉS, bajo este
    ///    lock) → 3) fila de proveedor/movimiento de CC (adentro de <see
    ///    cref="EscribirPagoAProveedorAsync"/>/<see
    ///    cref="EscriturasDeCuentaCorrienteProveedor.ImputarMovimientoDePagoAsync"/>, ÚLTIMO lock,
    ///    mismo criterio "el ledger de proveedor es el último lock de fila" que el resto de esta
    ///    clase).
    ///
    /// Análisis de ciclo: la anulación de compra (<c>ServicioDeCompras.MarcarAnuladaAsync</c>) toma
    /// el header EXCLUSIVO como su ÚNICO y primer lock — nunca toca <c>gastos</c> ni el ledger de
    /// proveedor, así que no puede formar un ciclo con el orden 1→2→3 de acá (se serializa contra
    /// el paso 1, nunca espera detrás del 2 o el 3). La confirmación de compra
    /// (<c>ServicioDeCompras.ConfirmarAsync</c>) tampoco toca <c>gastos</c>: su único lock
    /// compartido con esta transacción es el propio header de la compra (paso 1 acá; allá viene
    /// justo después del lock de membresía de familias, que esta transacción no toma) — mismo
    /// orden relativo entre los locks que comparten, sin ciclo. El cierre de turno
    /// (<c>ServicioDeTurnos</c>) no lockea ni compras ni gastos existentes (solo el turno mismo y,
    /// al insertar, filas nuevas de tesorería/CC) — no comparte ningún recurso con este método,
    /// así que tampoco puede ciclar. Los caminos de alta de gasto (<see cref="InsertarGastoAsync"/>/
    /// <see cref="InsertarGastoDeAdministracionAsync"/>) toman compra (paso 1, mismo orden) y DESPUÉS
    /// insertan una fila NUEVA de <c>gastos</c> (sin lock: es un insert) — nunca lockean una fila de
    /// <c>gastos</c> EXISTENTE, así que no compiten con el paso 2 de acá.</summary>
    public async Task<GastoRegistrado> VincularCompraAsync(
        int idGasto, SolicitudDeVincularCompra solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        var gasto = await estrategia.ExecuteAsync(async () =>
            await EjecutarVincularCompraAsync(idTenant, idGasto, solicitud.IdComprobanteCompra, idEmpleado, momento, ct));

        return Proyectar(gasto);
    }

    /// <summary>Ver el doc-comment de <see cref="VincularCompraAsync"/> para el orden de locks y el
    /// análisis de ciclo completos.</summary>
    private async Task<Gasto> EjecutarVincularCompraAsync(
        int idTenant, int idGasto, int idComprobanteCompra, int idEmpleado, DateTimeOffset momento,
        CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        // Paso 1 — compra FOR SHARE, PRIMER lock (mismo statement que ExigirCompraLigableAsync).
        var (idProveedorDeLaCompra, idPuntoVentaDeLaCompra, _) =
            await BloquearCompraAsync(idComprobanteCompra, idTenant, paraPagar: false, ct);

        // Paso 2 — gasto FOR UPDATE (lock-only), y la ÚNICA lectura EF de la fila, DESPUÉS del
        // lock (single-read-under-lock).
        await TomarLockDeGastoAsync(idGasto, idTenant, ct);
        var gasto = await db.Gastos.FirstAsync(g => g.Id == idGasto, ct);

        if (gasto.IdComprobanteCompra is not null)
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

        if (idEmpresaDeLaCompra != gasto.IdEmpresa)
        {
            throw new ErrorDominio(
                "compra_de_otra_empresa", "La compra indicada pertenece a otra empresa.", 400);
        }

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        if (gasto.Categoria == CategoriaGasto.Proveedor && gasto.IdProveedor is { } idProveedorDelGasto)
        {
            // El gasto YA tiene proveedor: solo se admite si coincide con el de la compra —
            // reusa el mismo código que ExigirCompraLigableAsync para el mismo caso en el alta.
            if (idProveedorDelGasto != idProveedorDeLaCompra)
            {
                throw new ErrorDominio(
                    "proveedor_no_coincide_con_la_compra",
                    "El proveedor indicado no coincide con el proveedor de la compra.", 400);
            }

            // El pago ya se escribió al crear el gasto (saldo ya descontado) — solo se imputa
            // la compra en el movimiento existente, el saldo NO cambia.
            gasto.IdComprobanteCompra = idComprobanteCompra;
            gasto.UpdatedAt = momento;
            await db.SaveChangesAsync(ct);

            await EscriturasDeCuentaCorrienteProveedor.ImputarMovimientoDePagoAsync(
                conexion, transaccionCruda, idTenant, gasto.Id, idComprobanteCompra, ct);
        }
        else
        {
            // Conversión: el gasto (categoría distinta de Proveedor, o Proveedor sin proveedor
            // asignado) nunca escribió un Pago al crearse — se convierte y se escribe AHORA, con
            // el importe reduciendo el saldo del proveedor por primera vez.
            gasto.Categoria = CategoriaGasto.Proveedor;
            gasto.IdProveedor = idProveedorDeLaCompra;
            gasto.IdComprobanteCompra = idComprobanteCompra;
            gasto.UpdatedAt = momento;
            await db.SaveChangesAsync(ct);

            // El ledger de CC exige un punto de venta (ValidarFormaPorTipo): un gasto admin sin
            // punto de venta propio usa el de la compra (las compras siempre tienen uno) — nunca
            // al revés, el punto de venta DEL GASTO manda cuando existe (es el que realmente pagó).
            var idPuntoVentaDelPago = gasto.IdPuntoVenta ?? idPuntoVentaDeLaCompra;

            await EscribirPagoAProveedorAsync(
                idTenant, idProveedorDeLaCompra, idPuntoVentaDelPago, gasto.Importe, gasto, idEmpleado, momento, ct);
        }

        await transaccion.CommitAsync(ct);

        return gasto;
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
    /// <c>deleted_at IS NULL</c> igual que el filtro global de EF: un gasto dado de baja es 404.</summary>
    private async Task TomarLockDeGastoAsync(int idGasto, int idTenant, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccionCruda;
        comando.CommandText = "SELECT 1 FROM gastos WHERE id_gasto = $1 AND id_tenant = $2 AND deleted_at IS NULL FOR UPDATE";
        ParametrosDeComando.Agregar(comando, idGasto);
        ParametrosDeComando.Agregar(comando, idTenant);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant".
            throw ErrorDominio.NoEncontrado($"No existe el gasto {idGasto}.");
        }
    }

    // ---- edición y baja -----------------------------------------------------------------------

    /// <summary>Edición desde el POS: solo mientras el turno del gasto siga abierto, decidido bajo
    /// el lock del turno (un gasto sin turno, o con el turno ya cerrado, es <c>409
    /// gasto_turno_cerrado</c>). Ver <see cref="EjecutarEscrituraAsync"/> para el orden de locks y
    /// el análisis de ciclo.</summary>
    public async Task<GastoRegistrado> EditarAsync(
        int idGasto, SolicitudDeEdicionDeGasto solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        ExigirImporteValido(solicitud.Importe);
        ExigirConceptoValido(solicitud.Concepto);

        var inmutables = await LeerDatosInmutablesAsync(idGasto, ct);
        if (inmutables.IdTurnoCaja is null)
        {
            throw GastoTurnoCerrado();
        }

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        var gasto = await estrategia.ExecuteAsync(async () =>
            await EjecutarEscrituraAsync(
                idTenant, idGasto, inmutables, solicitud, ModoDeEscritura.Pos, idEmpleado, momento, ct));

        return Proyectar(gasto);
    }

    /// <summary>Edición administrativa de cualquier gasto: con turno abierto, cerrado (recalcula el
    /// arqueo si cambia algo que el arqueo cuenta) o sin turno.</summary>
    public async Task<GastoDeAdministracionListado> EditarDeAdministracionAsync(
        int idGasto, SolicitudDeEdicionDeGasto solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        ExigirImporteValido(solicitud.Importe);
        ExigirConceptoValido(solicitud.Concepto);

        var inmutables = await LeerDatosInmutablesAsync(idGasto, ct);
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
            await EjecutarEscrituraAsync(
                idTenant, idGasto, inmutables, solicitud, ModoDeEscritura.Administracion, idEmpleado, momento, ct));

        return await ObtenerDeAdministracionAsync(idGasto, ct);
    }

    /// <summary>Baja lógica administrativa de cualquier gasto: revierte su pago a proveedor y su
    /// egreso de tesorería con ajustes, y recalcula el arqueo si era un gasto de caja de un turno
    /// cerrado.</summary>
    public async Task EliminarDeAdministracionAsync(int idGasto, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var inmutables = await LeerDatosInmutablesAsync(idGasto, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
            await EjecutarEscrituraAsync(
                idTenant, idGasto, inmutables, solicitud: null, ModoDeEscritura.Administracion, idEmpleado, momento, ct));
    }

    private enum ModoDeEscritura
    {
        Pos,
        Administracion
    }

    /// <summary>Columnas que ninguna escritura cambia después del alta, así que se pueden leer
    /// antes de los locks (single-read-under-lock regla 6): el turno es la clave del primer lock.
    /// <c>id_comprobante_compra</c> NO está acá: la vinculación posterior lo cambia.</summary>
    private sealed record DatosInmutablesDeGasto(int? IdTurnoCaja, int IdEmpresa, OrigenFondosGasto OrigenFondos);

    private async Task<DatosInmutablesDeGasto> LeerDatosInmutablesAsync(int idGasto, CancellationToken ct) =>
        await db.Gastos
            .Where(g => g.Id == idGasto)
            .Select(g => new DatosInmutablesDeGasto(g.IdTurnoCaja, g.IdEmpresa, g.OrigenFondos))
            .FirstOrDefaultAsync(ct)
        // ADR-8: mismo 404 para "no existe", "es de otro tenant" y "está dado de baja".
        ?? throw ErrorDominio.NoEncontrado($"No existe el gasto {idGasto}.");

    /// <summary>Mismas validaciones de referencias que el alta, pero solo para las referencias NUEVAS
    /// (OD4: una referencia que el gasto ya tenía puede estar dada de baja y la edición sigue siendo
    /// válida): medio de pago visible y que no sea cuenta corriente, proveedor visible (404). Se compara
    /// contra el gasto leído bajo el lock. El área sigue el criterio del alta: sin pre-chequeo, la FK
    /// compuesta es el respaldo.</summary>
    private async Task ExigirReferenciasNuevasDeLaEdicionAsync(
        Gasto gasto, SolicitudDeEdicionDeGasto solicitud, CancellationToken ct)
    {
        if (solicitud.IdMedioPago != gasto.IdMedioPago)
        {
            await ExigirMedioPagoValidoAsync(solicitud.IdMedioPago, ct);
        }

        if (solicitud.IdProveedor is { } idProveedor && idProveedor != gasto.IdProveedor)
        {
            await ResolverProveedorAsync(idProveedor, ct);
        }
    }

    private static ErrorDominio GastoTurnoCerrado() =>
        new("gasto_turno_cerrado", "El gasto solo se puede editar mientras su turno siga abierto.", 409);

    /// <summary>
    /// Edición (<paramref name="solicitud"/> no nula) o baja de un gasto, en una sola transacción.
    ///
    /// Orden de locks:
    /// 1) turno del gasto (si tiene): POS <c>FOR SHARE</c> con el estado leído bajo el lock (mismo
    ///    criterio que <see cref="ServicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync"/> en el alta, con
    ///    su propio <c>409 gasto_turno_cerrado</c>); administración <c>FOR UPDATE</c>, que además
    ///    serializa entre sí los recálculos del arqueo de un turno cerrado →
    /// 2) gasto <c>FOR UPDATE</c> (lock-only, <see cref="TomarLockDeGastoAsync"/>) y la ÚNICA lectura
    ///    EF de la fila, después del lock →
    /// 3) filas de proveedor de los ajustes de cuenta corriente, en orden ascendente de id →
    /// 4) filas de <c>arqueos_turno</c> y la marca del turno (ya cubiertas por el lock 1) →
    /// 5) advisory de tesorería de la empresa, ÚLTIMO.
    ///
    /// Análisis de ciclo: el cierre (<see cref="ServicioDeTurnos"/>, los dos modos) toma el turno
    /// EXCLUSIVO como primer statement y después solo el advisory de tesorería; nunca lockea gastos
    /// ni proveedores, y su orden turno → advisory es el mismo relativo que el de acá. El alta de
    /// gasto toma el turno <c>FOR SHARE</c> primero y después compra/proveedor/advisory, sin lockear
    /// ningún gasto existente. <see cref="VincularCompraAsync"/> no toca turnos y toma gasto →
    /// proveedor, el mismo orden relativo que los pasos 2 → 3. Dos ediciones que mueven pagos entre
    /// los mismos dos proveedores en sentidos opuestos no se cruzan porque las dos lockean en orden
    /// ascendente de id.
    ///
    /// Carrera con el cierre: si la edición del POS gana el <c>FOR SHARE</c>, el cierre espera y
    /// deriva el arqueo con el gasto ya editado; si el cierre gana, la edición lee <c>cerrado</c> y
    /// responde 409. La edición administrativa: si gana el cierre, lee <c>cerrado</c> bajo su
    /// <c>FOR UPDATE</c> y recalcula; si gana la edición, el cierre deriva con el gasto ya editado.
    /// En ningún orden queda un arqueo persistido que no refleje el gasto comiteado.
    /// </summary>
    private async Task<Gasto> EjecutarEscrituraAsync(
        int idTenant, int idGasto, DatosInmutablesDeGasto inmutables, SolicitudDeEdicionDeGasto? solicitud,
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
                    throw GastoTurnoCerrado();
                }

                turnoCerrado = turno;
            }
        }

        await TomarLockDeGastoAsync(idGasto, idTenant, ct);
        var gasto = await db.Gastos.FirstOrDefaultAsync(g => g.Id == idGasto, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el gasto {idGasto}.");

        var anterior = EstadoContable(gasto);
        var valorAnterior = PayloadDeEdicion(gasto);

        if (solicitud is not null)
        {
            await ExigirReferenciasNuevasDeLaEdicionAsync(gasto, solicitud, ct);
            ExigirEdicionCoherenteConLaCompra(gasto, solicitud);

            if (gasto.IdComprobanteCompra is null)
            {
                ExigirPuntoVentaParaElPagoAProveedor(solicitud.Categoria, gasto.IdPuntoVenta);
            }

            gasto.Categoria = solicitud.Categoria;
            gasto.IdProveedor = solicitud.IdProveedor;
            gasto.IdArea = solicitud.IdArea;
            gasto.Concepto = solicitud.Concepto;
            gasto.Detalle = solicitud.Detalle;
            gasto.IdMedioPago = solicitud.IdMedioPago;
            gasto.NumeroFactura = solicitud.NumeroFactura;
            gasto.Importe = solicitud.Importe;
        }
        else
        {
            gasto.DeletedAt = momento;
        }

        gasto.UpdatedAt = momento;
        await db.SaveChangesAsync(ct);

        var nuevo = solicitud is null ? null : EstadoContable(gasto);
        var detalle = solicitud is null ? $"Baja del gasto #{gasto.Id}" : $"Edición del gasto #{gasto.Id}";

        await EscribirAjustesDeProveedorAsync(
            idTenant, gasto, CalculadorDeAjustesDeGasto.AjustesDeProveedor(anterior, nuevo), detalle, idEmpleado,
            momento, ct);

        if (turnoCerrado is not null && CalculadorDeAjustesDeGasto.AfectaElArqueo(gasto.OrigenFondos, anterior, nuevo))
        {
            await RecalcularArqueoAsync(idTenant, turnoCerrado, idEmpleado, momento, ct);
        }

        if (CalculadorDeAjustesDeGasto.AjusteDeTesoreriaPara(gasto.OrigenFondos, anterior.Importe, nuevo?.Importe)
            is { } ajusteDeTesoreria)
        {
            var conexion = await ObtenerConexionAbiertaAsync(ct);
            var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();
            await EscriturasDeTesoreria.TomarLockDeEmpresaAsync(conexion, transaccionCruda, idTenant, gasto.IdEmpresa, ct);

            await EscriturasDeTesoreria.ApendearAsync(
                db, idTenant, gasto.IdEmpresa, gasto.IdPuntoVenta, momento, TipoMovimientoTesoreria.Ajuste,
                gasto.IdTurnoCaja, idGasto: null, detalle, ajusteDeTesoreria.Ingreso, ajusteDeTesoreria.Egreso,
                idEmpleado, ct);
        }

        var valorNuevo = PayloadDeEdicion(gasto);
        if (solicitud is null)
        {
            valorNuevo["deleted_at"] = gasto.DeletedAt;
        }

        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, gasto.IdPuntoVenta, solicitud is null ? AccionAuditada.GastoBaja : AccionAuditada.GastoEdicion,
            gasto.Id, valorAnterior, valorNuevo));
        await db.SaveChangesAsync(ct);

        await transaccion.CommitAsync(ct);

        return gasto;
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

    /// <summary>Mismos códigos que el alta para un gasto ligado a una compra: sigue siendo de
    /// categoría proveedor y con el proveedor de la compra.</summary>
    private static void ExigirEdicionCoherenteConLaCompra(Gasto gasto, SolicitudDeEdicionDeGasto solicitud)
    {
        ExigirCategoriaCoherenteConLaCompra(solicitud.Categoria, gasto.IdComprobanteCompra);

        if (gasto.IdComprobanteCompra is not null && solicitud.IdProveedor != gasto.IdProveedor)
        {
            throw new ErrorDominio(
                "proveedor_no_coincide_con_la_compra",
                "El proveedor indicado no coincide con el proveedor de la compra.", 400);
        }
    }

    /// <summary>Ajustes de cuenta corriente de proveedor (<see cref="TipoMovimientoCcProveedor.Ajuste"/>
    /// no admite <c>id_gasto</c>: el gasto queda referenciado en el detalle). El punto de venta es el
    /// del gasto; un gasto administrativo sin punto de venta solo pudo pagar a un proveedor al
    /// vincularse a una compra, y ese pago usó el punto de venta de la compra.</summary>
    private async Task EscribirAjustesDeProveedorAsync(
        int idTenant, Gasto gasto, IReadOnlyList<AjusteDeSaldoDeProveedor> ajustes, string detalle, int idEmpleado,
        DateTimeOffset momento, CancellationToken ct)
    {
        if (ajustes.Count == 0)
        {
            return;
        }

        var idPuntoVenta = gasto.IdPuntoVenta ?? await PuntoVentaDeLaCompraAsync(gasto.IdComprobanteCompra, ct);
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        foreach (var ajuste in ajustes)
        {
            var nuevoSaldo = await EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(
                conexion, transaccionCruda, idTenant, ajuste.IdProveedor, ajuste.Importe, ct);

            await EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(
                conexion, transaccionCruda, idTenant, ajuste.IdProveedor, momento, idPuntoVenta, idEmpleado,
                TipoMovimientoCcProveedor.Ajuste, idComprobanteCompra: null, idGasto: null, ajuste.Importe, nuevoSaldo,
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

    private static EstadoContableDeGasto EstadoContable(Gasto gasto) =>
        new(gasto.Categoria, gasto.IdProveedor, gasto.IdMedioPago, gasto.Importe);

    private static Dictionary<string, object?> PayloadDeEdicion(Gasto gasto) => new()
    {
        ["categoria"] = gasto.Categoria,
        ["id_proveedor"] = gasto.IdProveedor,
        ["id_area"] = gasto.IdArea,
        ["concepto"] = gasto.Concepto,
        ["detalle"] = gasto.Detalle,
        ["id_medio_pago"] = gasto.IdMedioPago,
        ["numero_factura"] = gasto.NumeroFactura,
        ["importe"] = gasto.Importe
    };

    /// <summary>Historial paginado (design: API Surface, <c>GET /api/gastos</c>) — mismo criterio
    /// de paginado que <c>ServicioDeTurnos.ListarAsync</c>.</summary>
    public async Task<PaginaDeGastos> ListarAsync(
        int? idPuntoVenta = null,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = db.Gastos.AsQueryable();

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
            .Select(ProyeccionesDeGasto.Listado(db))
            .ToListAsync(ct);

        return new PaginaDeGastos(items, total, pagina, tamanio);
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
    /// la fila exista) — un catálogo dado de baja lógica nunca hace desaparecer el gasto de
    /// <c>items</c> ni desalinea <c>total</c>; el nombre sale <c>null</c> y el filtro
    /// <c>idProveedor</c> exige la fila VISIBLE (regla 2) para no matchear un id ya invisible por
    /// baja lógica.</summary>
    public async Task<PaginaDeGastosDeAdministracion> ListarDeAdministracionAsync(
        int? idEmpresa = null,
        int? idPuntoVenta = null,
        DateTimeOffset? desde = null,
        DateTimeOffset? hasta = null,
        CategoriaGasto? categoria = null,
        OrigenFondosGasto? origenFondos = null,
        int? idProveedor = null,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = db.Gastos.AsQueryable();

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

        return new PaginaDeGastosDeAdministracion(items, total, pagina, tamanio);
    }

    /// <summary>stage-gasto-a-compra (PR4), judgment follow-up: <c>GET
    /// /api/gastos/administracion/{id}</c> — mismo shape/joins que <see
    /// cref="ListarDeAdministracionAsync"/> (dangling-fk-read-models), pero de una sola fila. Nace
    /// para que <c>POST /api/gastos/administracion</c> tenga un <c>Location</c> que realmente
    /// resuelve (antes apuntaba a <c>/api/gastos/{id}</c>, que exige <c>OperacionDePos</c> pero
    /// devuelve el shape reducido de <see cref="GastoListado"/> — un mismatch de contrato para un
    /// cliente que siguiera el header).</summary>
    public async Task<GastoDeAdministracionListado> ObtenerDeAdministracionAsync(int id, CancellationToken ct = default) =>
        await db.Gastos.Where(g => g.Id == id).Select(ProyeccionDeAdministracion()).FirstOrDefaultAsync(ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant".
            ?? throw ErrorDominio.NoEncontrado($"No existe el gasto {id}.");

    private Expression<Func<Gasto, GastoDeAdministracionListado>> ProyeccionDeAdministracion() => g =>
        new GastoDeAdministracionListado(
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
            g.IdTurnoCaja != null && db.TurnosCaja.Any(t => t.Id == g.IdTurnoCaja && t.Estado == EstadoTurno.Abierto));

    // ---- validación de dominio -------------------------------------------------------------

    /// <summary>Mismo código que la CHECK de esquema <c>ck_gastos_importe_positivo</c> (design:
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

    /// <summary>spec: gastos / A Comprobante Compra Link Requires Categoria Proveedor — chequeo de
    /// dominio puro, sin DB: un gasto ligado a una compra SIEMPRE tiene que ser de categoría
    /// proveedor (la compra la paga un proveedor, nunca sueldos/viáticos/etc.).</summary>
    private static void ExigirCategoriaCoherenteConLaCompra(CategoriaGasto categoria, int? idComprobanteCompra)
    {
        if (idComprobanteCompra is not null && categoria != CategoriaGasto.Proveedor)
        {
            throw new ErrorDominio(
                "gasto_de_compra_debe_ser_de_proveedor",
                "Un gasto ligado a una compra tiene que ser de categoría proveedor.", 400);
        }
    }

    /// <summary>stage-gastos-admin-retroactivos (PR3): <see
    /// cref="EscriturasDeCuentaCorrienteProveedor.ValidarFormaPorTipo"/> exige <c>id_punto_venta</c>
    /// no nulo para CUALQUIER movimiento de CC de proveedor que no sea <c>Apertura</c> — un gasto
    /// administrativo de categoría <see cref="CategoriaGasto.Proveedor"/> siempre escribe un
    /// <c>Pago</c> (<see cref="EscribirPagoAProveedorAsync"/>), así que el punto de venta deja de
    /// ser opcional en ESTE caso puntual (a diferencia del resto de las categorías, donde el
    /// gasto administrativo nunca lo necesita). Chequeo de dominio puro, antes de tocar la base —
    /// mismo criterio que <see cref="ExigirCategoriaCoherenteConLaCompra"/>.</summary>
    private static void ExigirPuntoVentaParaElPagoAProveedor(CategoriaGasto categoria, int? idPuntoVenta)
    {
        if (categoria == CategoriaGasto.Proveedor && idPuntoVenta is null)
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
    /// las 24:00). El resultado es el <see cref="Gasto.Fecha"/> que persiste — la ÚNICA columna
    /// que lleva la fecha de negocio elegida por el admin; los movimientos de ledger (tesorería,
    /// pago a proveedor) llevan <c>momento</c> (ver <see cref="InsertarGastoDeAdministracionAsync"/>),
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
    /// cref="RegistrarAsync"/>) solo lo filtra client-side (<c>mediosValidosParaGasto</c>,
    /// Ways.Web); este es el primer camino de alta de gasto expuesto directo a un cliente HTTP sin
    /// ese filtro de UI garantizado (un admin puede llamar la API a mano), así que acá el guard es
    /// de servidor: cuenta corriente es el crédito del CLIENTE en una venta, nunca una salida real
    /// de dinero de una caja/tesorería.</summary>
    private async Task ExigirMedioPagoValidoAsync(int idMedioPago, CancellationToken ct)
    {
        var medioPago = await db.MediosPago.FirstOrDefaultAsync(m => m.Id == idMedioPago, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el medio de pago {idMedioPago}.");

        if (medioPago.Comportamiento == ComportamientoMedioPago.CuentaCorriente)
        {
            throw new ErrorDominio(
                "gasto_medio_pago_cuenta_corriente_invalido",
                "Un gasto no puede pagarse con un medio de cuenta corriente.", 400);
        }
    }

    private async Task<Empresa> ResolverEmpresaAsync(int idEmpresa, CancellationToken ct) =>
        await db.Empresas.FirstOrDefaultAsync(e => e.Id == idEmpresa, ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — mismo criterio que
            // ResolverPuntoVentaAsync.
            ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");

    /// <summary>400, no 404: el punto de venta SÍ existe (ya pasó <see
    /// cref="ResolverPuntoVentaAsync"/>, que da 404 si no) — lo que está mal es la combinación con
    /// la empresa del gasto, mismo criterio 400 que <c>proveedor_no_coincide_con_la_compra</c> en
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
    /// transacción) — sin este re-chequeo bajo <c>FOR SHARE</c>, un gasto concurrente a un
    /// cierre podría comitear dentro de un turno cuyo arqueo YA se derivó (design decisión 1).
    ///
    /// Slice 4 (design decisión 7): el guard de la compra ligada corre DESPUÉS de ese lock de
    /// turno — mismo orden que el design pseudocódigo (Transactions — GASTO LIGADO A UNA
    /// COMPRA).
    ///
    /// stage-gastos-origen-fondos-pos (PR2): cuando <see cref="SolicitudDeGasto.OrigenFondos"/> es
    /// <see cref="OrigenFondosGasto.Tesoreria"/>, <see cref="EscribirMovimientoDeTesoreriaAsync"/>
    /// corre COMO ÚLTIMO paso antes del commit — después del turno <c>FOR SHARE</c>, la compra
    /// ligada <c>FOR SHARE</c> (si aplica) y el pago a proveedor (si aplica): mismo orden relativo
    /// que <c>ServicioDeTurnos.InsertarArqueosYTesoreriaAsync</c> (locks de fila primero, advisory
    /// de tesorería de la empresa al final) — ver el doc-comment de clase de
    /// <see cref="EscriturasDeTesoreria"/> para el análisis completo de por qué esto no puede
    /// formar un ciclo con el cierre.</summary>
    private async Task<Gasto> InsertarGastoAsync(
        int idTenant, int idEmpresa, int idTurnoCaja, SolicitudDeGasto solicitud, int idEmpleado,
        DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        await servicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync(idTurnoCaja, ct);

        var idProveedor = solicitud.IdProveedor;
        if (solicitud.IdComprobanteCompra is { } idComprobanteCompra)
        {
            idProveedor = await ExigirCompraLigableAsync(idComprobanteCompra, idTenant, solicitud.IdProveedor, ct);
        }

        var gasto = new Gasto
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

        db.Gastos.Add(gasto);
        await db.SaveChangesAsync(ct);

        // stage-15-cc-proveedores-ledger, Slice 3 (design decisión 7, tasks.md task 3.1): el
        // movimiento `pago` va DESPUÉS de SaveChangesAsync — id_gasto es identity, recién existe
        // acá — y es el ÚLTIMO lock de fila (for update) antes del commit. El predicado es
        // ServicioDeSaldoDeProveedor.cs:39-43 VERBATIM (la fórmula retirada que este ledger
        // reemplaza): categoría proveedor Y id_proveedor no nulo. `idProveedor` ya es el valor
        // RESUELTO (derivado por ExigirCompraLigableAsync cuando la solicitud no lo trae) — el
        // movimiento usa el mismo valor que la fila guarda, nunca el crudo de la solicitud.
        if (solicitud.Categoria == CategoriaGasto.Proveedor && idProveedor is { } idProveedorDelPago)
        {
            await EscribirPagoAProveedorAsync(
                idTenant, idProveedorDelPago, solicitud.IdPuntoVenta, solicitud.Importe, gasto, idEmpleado, momento, ct);
        }

        if (solicitud.OrigenFondos == OrigenFondosGasto.Tesoreria)
        {
            await EscribirMovimientoDeTesoreriaAsync(idTenant, idEmpresa, gasto, idEmpleado, momento, ct);
        }

        await transaccion.CommitAsync(ct);

        return gasto;
    }

    /// <summary>stage-gastos-admin-retroactivos (PR3): mismo protocolo exacto que <see
    /// cref="InsertarGastoAsync"/> (locks de fila primero, advisory de tesorería de la empresa al
    /// final) MENOS el turno — acá no hay ningún turno que lockear/re-chequear (owner's use case
    /// 2: el gasto administrativo nunca pasa por un turno). Orden: compra ligada <c>FOR SHARE</c>
    /// (si aplica) → insert del gasto → pago a proveedor (si aplica, su propio lock de fila) →
    /// movimiento de tesorería (advisory de empresa, ÚLTIMO) → commit. <see
    /// cref="Gasto.OrigenFondos"/> es SIEMPRE <see cref="OrigenFondosGasto.Tesoreria"/> acá (spec:
    /// "never from a POS drawer") — nunca condicional como en <see cref="InsertarGastoAsync"/>.
    ///
    /// <paramref name="fechaDeNegocio"/> (posiblemente retroactiva) es la ÚNICA que persiste en
    /// <c>Gasto.Fecha</c>; el movimiento de tesorería y el pago a proveedor llevan <paramref
    /// name="momento"/> (el instante REAL del alta) — decisión documentada en el doc-comment de
    /// clase de este método más abajo y en <see cref="FechaDeNegocioAInstante"/>: los dos ledgers
    /// se LEEN por <c>fecha</c> (tesorería: por <c>id</c> de cadena, irrelevante; cuenta corriente
    /// de proveedor: <c>ORDER BY fecha DESC, id DESC</c>, <see
    /// cref="Ways.Application.CuentaCorriente.ServicioDeCuentaCorrienteDeProveedor"/>) y el
    /// <c>saldo</c>/<c>final</c> que cada fila graba es el acumulado en el momento REAL del
    /// insert, nunca recalculado para su posición cronológica — grabar una fecha retroactiva en
    /// esos ledgers dejaría un saldo/final "del futuro" apareciendo entre filas más viejas.
    /// <c>Gasto.Fecha</c> no tiene ese problema (no es un ledger acumulado, es un documento de
    /// autoría) y por eso sí lleva la fecha real elegida por el admin.</summary>
    private async Task<Gasto> InsertarGastoDeAdministracionAsync(
        int idTenant, int idEmpresa, DateTimeOffset fechaDeNegocio, SolicitudDeGastoDeAdministracion solicitud,
        int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var idProveedor = solicitud.IdProveedor;
        if (solicitud.IdComprobanteCompra is { } idComprobanteCompra)
        {
            idProveedor = await ExigirCompraLigableAsync(idComprobanteCompra, idTenant, solicitud.IdProveedor, ct);
        }

        var gasto = await PersistirGastoDeAdministracionAsync(
            idTenant, idEmpresa, fechaDeNegocio, solicitud, idProveedor, idEmpleado, momento, ct);

        await transaccion.CommitAsync(ct);

        return gasto;
    }

    /// <summary>Escrituras del gasto administrativo dentro de la transacción del llamador, que ya
    /// tomó el lock de la compra (si hay) y es quien comitea. Las comparten el alta administrativa y
    /// <see cref="PagarCompraAsync"/>: un solo camino de escritura del gasto, del pago a proveedor y
    /// del egreso de tesorería. <paramref name="idProveedor"/> ya viene resuelto contra la
    /// compra.</summary>
    private async Task<Gasto> PersistirGastoDeAdministracionAsync(
        int idTenant, int idEmpresa, DateTimeOffset fechaDeNegocio, SolicitudDeGastoDeAdministracion solicitud,
        int? idProveedor, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        var gasto = new Gasto
        {
            IdTenant = idTenant,
            IdEmpresa = idEmpresa,
            Fecha = fechaDeNegocio,
            IdPuntoVenta = solicitud.IdPuntoVenta,
            IdTurnoCaja = null,
            IdEmpleado = idEmpleado,
            Categoria = solicitud.Categoria,
            OrigenFondos = OrigenFondosGasto.Tesoreria,
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

        db.Gastos.Add(gasto);
        await db.SaveChangesAsync(ct);

        if (solicitud.Categoria == CategoriaGasto.Proveedor && idProveedor is { } idProveedorDelPago)
        {
            await EscribirPagoAProveedorAsync(
                idTenant, idProveedorDelPago, solicitud.IdPuntoVenta, solicitud.Importe, gasto, idEmpleado, momento, ct);
        }

        // ÚLTIMO lock de esta transacción (mismo criterio que InsertarGastoAsync) — acá no hay
        // ningún lock de turno antes: la compra ligada FOR SHARE (si aplica) y el pago a proveedor
        // (si aplica) son los únicos locks de fila que pueden preceder a este advisory.
        await EscribirMovimientoDeTesoreriaAsync(idTenant, idEmpresa, gasto, idEmpleado, momento, ct);

        return gasto;
    }

    /// <summary>stage-gastos-origen-fondos-pos (PR2): un gasto de origen
    /// <see cref="OrigenFondosGasto.Tesoreria"/> se paga DIRECTO del fondo de tesorería de la
    /// empresa, sin pasar por el efectivo del cajón — sigue atado al turno abierto
    /// (<c>gasto.IdTurnoCaja</c>, trazabilidad) pero NO afecta ningún arqueo (<see
    /// cref="Ways.Application.Caja.LectorDeMovimientosDelTurno"/> excluye este origen de
    /// <c>gastosPorMedio</c>). Reusa <see cref="EscriturasDeTesoreria"/> — el mismo protocolo
    /// lock+lectura+insert que el cierre — para que la cadena de tesorería tenga un solo escritor
    /// real. <c>Ingreso = 0</c>/<c>Egreso = gasto.Importe</c> (design: la tesorería se reduce en el
    /// momento del gasto, no se difiere al cierre); <c>Tipo = Gasto</c>, <c>IdGasto = gasto.Id</c>
    /// (único por <c>ux_movimientos_tesoreria_id_gasto</c> — este es el ÚNICO call site que puebla
    /// esa columna).</summary>
    private async Task EscribirMovimientoDeTesoreriaAsync(
        int idTenant, int idEmpresa, Gasto gasto, int idEmpleado, DateTimeOffset momento, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // Último lock de esta transacción, después del turno/compra/proveedor (ver el doc-comment
        // de InsertarGastoAsync) — mismo orden relativo que el cierre, nunca puede formar un ciclo.
        await EscriturasDeTesoreria.TomarLockDeEmpresaAsync(conexion, transaccionCruda, idTenant, idEmpresa, ct);

        await EscriturasDeTesoreria.ApendearAsync(
            db, idTenant, idEmpresa, gasto.IdPuntoVenta, momento, TipoMovimientoTesoreria.Gasto, gasto.IdTurnoCaja,
            gasto.Id, gasto.Concepto, ingreso: 0m, egreso: gasto.Importe, idEmpleado, ct);
    }

    /// <summary>stage-15-cc-proveedores-ledger, Slice 3: el ÚNICO call site de
    /// <see cref="EscriturasDeCuentaCorrienteProveedor"/> para pagos — <c>importe = −importe</c>
    /// (el pago REDUCE el saldo), <c>id_gasto</c> = la fila recién flusheada,
    /// <c>id_comprobante_compra</c> = el vínculo del gasto (puede ser null: la imputación es
    /// opcional, spec: An Unlinked Proveedor Gasto Reduces The Saldo Without Imputación). Misma
    /// conexión/transacción cruda que <see cref="ExigirCompraLigableAsync"/> reutiliza — nunca una
    /// segunda transacción.
    ///
    /// stage-gastos-admin-retroactivos (PR3): parámetros primitivos (no el <c>SolicitudDeGasto</c>
    /// del POS) para que <see cref="InsertarGastoDeAdministracionAsync"/> lo reuse sin duplicar el
    /// SQL — <paramref name="idPuntoVenta"/> viaja nullable (el camino administrativo no siempre
    /// trae uno).</summary>
    private async Task EscribirPagoAProveedorAsync(
        int idTenant, int idProveedor, int? idPuntoVenta, decimal importe, Gasto gasto, int idEmpleado,
        DateTimeOffset momento, CancellationToken ct)
    {
        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var nuevoSaldo = await EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(
            conexion, transaccionCruda, idTenant, idProveedor, -importe, ct);

        await EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(
            conexion, transaccionCruda, idTenant, idProveedor, momento, idPuntoVenta, idEmpleado,
            TipoMovimientoCcProveedor.Pago, gasto.IdComprobanteCompra, gasto.Id, -importe, nuevoSaldo,
            detalle: null, ct);
    }

    /// <summary>design decisión 7: <c>SELECT ... FOR SHARE</c> crudo sobre el header de la compra
    /// — cierra el TOCTOU contra una anulación concurrente de la MISMA compra. La anulación toma
    /// el lock EXCLUSIVO del header como su propio primer statement (<c>ServicioDeCompras.
    /// MarcarAnuladaAsync</c>): este gasto o bien bloquea y retoma viendo <c>anulada</c> ya
    /// comiteada (<c>409 compra_anulada</c>), o gana la carrera y queda simplemente visible para
    /// la anulación que llega después — ambos estados representables, ninguno corrupto. <c>estado
    /// ::text</c> en vez del enum nativo, mismo criterio cauteloso que
    /// <see cref="ServicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync"/>. Devuelve el
    /// <c>id_proveedor</c> de la compra: se usa para derivarlo cuando el request no lo trae (spec:
    /// gastos / A Comprobante Compra Link — <c>id_proveedor</c> ausente se deriva, distinto se
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
                "ServicioDeGastos requiere un actor de tenant; OperacionDePos no admite plataforma.");

    // ---- proyecciones -----------------------------------------------------------------------

    private static GastoRegistrado Proyectar(Gasto gasto) => new(
        gasto.Id,
        gasto.IdTurnoCaja,
        gasto.IdPuntoVenta,
        gasto.Fecha,
        gasto.Categoria,
        gasto.IdProveedor,
        gasto.IdArea,
        gasto.Concepto,
        gasto.Detalle,
        gasto.IdMedioPago,
        gasto.NumeroFactura,
        gasto.Importe,
        gasto.IdEmpleado,
        gasto.IdComprobanteCompra,
        gasto.OrigenFondos);
}
