using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Caja;
using Ways.Application.CuentaCorriente;
using Ways.Application.Parametros;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;

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
    IRelojDelSistema reloj, IContextoDeUsuario contexto)
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
            .Select(g => new GastoListado(g.Id, g.IdPuntoVenta, g.Fecha, g.Categoria, g.IdMedioPago, g.Importe, g.OrigenFondos))
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
            .Select(g => new GastoDeAdministracionListado(
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
                g.IdComprobanteCompra))
            .ToListAsync(ct);

        return new PaginaDeGastosDeAdministracion(items, total, pagina, tamanio);
    }

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

        await transaccion.CommitAsync(ct);

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
