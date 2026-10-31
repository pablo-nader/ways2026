using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Domain.Articulos;
using Ways.Domain.Auditoria;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;

namespace Ways.Application.Stock;

/// <summary>
/// Ajuste manual de stock (design decisión 10: dedicado, no una extensión de
/// <c>ServicioDeArticulos</c> — autorización admin-only y forma de escritura son propias de esta
/// operación, mismo criterio que <c>ServicioDeVentas</c>/<c>ServicioDeEscaneo</c>). El endpoint
/// (<c>Politicas.GestionDeCatalogo</c>) es quien bloquea al Vendedor — este servicio no repite
/// ese chequeo, mismo criterio que el resto de los ABM del proyecto (autorización vive en la capa
/// de API, nunca duplicada en Application).
///
/// Etapa 12, slice 10 (design: Write site 3): <see cref="ServicioDeLotes"/> se inyecta para el
/// mismo trío de primitivas que <c>ServicioDeVentas</c> ya consume — <c>LeerSaldosAsync</c> (fase
/// de resolución de la transferencia) y <c>ResolverSinIdentificarAsync</c> (get-or-create
/// perezoso, statement crudo, cuando ningún lote del artículo tiene saldo positivo en el origen).
///
/// Etapa 12, slice 11 (design: Write site 3, proposal decisión 9): <c>AjustarAsync</c> gana la
/// dimensión de lote (<c>idLote</c> requerido/rechazado según <c>EsLoteEfectivo</c>) y
/// <c>DecomisarAsync</c> nace como motivo de primera clase — estructuralmente el mismo camino de
/// <c>AjustarAsync</c>, con la cantidad positiva del cliente negada server-side (disciplina de
/// <c>ContarAsync</c>) y el único rechazo de negatividad que este servicio conoce
/// (<c>409 stock_insuficiente_para_decomiso</c>). No restringido a lotes vencidos.
///
/// Etapa 13, slice 1 (design decisión 10/11): <c>EscribirMinimosAsync</c> gana el par
/// <c>minimo</c>/<c>reposicion</c> de <c>PUT /api/stock/minimos</c> — a diferencia de todo lo de
/// arriba, NO abre transacción ni escribe <c>movimientos_stock</c>: un parámetro de reposición
/// no es un hecho del ledger, es un umbral configurado sobre la misma fila de caché.
/// </summary>
public class ServicioDeStock(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeLotes servicioDeLotes,
    ServicioDeAuditoria auditoria)
{
    /// <summary>El saldo que ve el punto de venta: el de su sucursal, compartido con las otras cajas de ella.</summary>
    public async Task<decimal> ObtenerCantidadAsync(int idPuntoVenta, int idArticulo, CancellationToken ct = default) =>
        await db.Stock
            .Where(s => s.IdArticulo == idArticulo
                && db.PuntosVenta.Any(pv => pv.Id == idPuntoVenta && pv.IdSucursal == s.IdSucursal))
            .Select(s => s.Cantidad)
            .FirstOrDefaultAsync(ct);

    /// <summary>Design: API Surface — <c>POST /api/stock/ajustes</c>, <c>motivo = ajuste</c>, una
    /// única transacción (movimiento + upsert del caché, spec: Manual Ajuste Path Is
    /// Admin-Only).</summary>
    public async Task<decimal> AjustarAsync(SolicitudDeAjusteDeStock solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var cantidad = ExigirCantidadValida(solicitud.Cantidad);
        var observaciones = ExigirObservaciones(solicitud.Observaciones);

        // Pre-checks de existencia/tenant ANTES de la transacción (mismo criterio que
        // ServicioDeVentas: la referencia se valida sobre una lectura simple, nunca dejando que
        // el FK real de la base la rechace con un 500 crudo dentro del INSERT crudo de abajo).
        var articulo = await ResolverArticuloAsync(solicitud.IdArticulo, ct);
        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        // Etapa 12, slice 11 (design: Write site 3 — "IdLote required when lot-effective"):
        // resuelto/validado ANTES de la transacción, mismo criterio que la fase de resolución de
        // TransferirAsync.
        var idLote = await ResolverIdLoteEfectivoAsync(puntoVenta.IdEmpresa, puntoVenta.Id, articulo, solicitud.IdLote, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarAjusteAsync(
                idTenant, idEmpleado, solicitud.IdArticulo, solicitud.IdPuntoVenta, cantidad, observaciones, idLote,
                momento, ct));
    }

    private async Task<decimal> EjecutarAjusteAsync(
        int idTenant, int idEmpleado, int idArticulo, int idPuntoVenta, decimal cantidad, string observaciones,
        int? idLote, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var idSucursal = await EscritorDeStock.ResolverSucursalAsync(conexion, transaccionCruda, idPuntoVenta, ct);

        // El agregado siempre antes que el lote (orden de locks de EscritorDeStock); el lote solo cuando el artículo es
        // lote-efectivo. Sin rechazo de negatividad: el ajuste es la operación que CORRIGE un saldo negativo.
        var aplicado = (await EscritorDeStock.AsentarAsync(
            conexion, transaccionCruda,
            [
                new AsientoDeStock(idTenant, idSucursal, idPuntoVenta, idArticulo, cantidad, MotivoStock.Ajuste, idEmpleado, momento)
                {
                    IdLote = idLote,
                    Observaciones = observaciones
                }
            ],
            ct))[0];
        var idMovimientoStock = aplicado.IdMovimiento;
        var nuevaCantidad = aplicado.CantidadAgregada;

        // Etapa 14, slice 4 (design decisión 9, call site 9): before-image derivado del RETURNING
        // autoritativo del upsert (nueva − delta), JAMÁS un segundo SELECT — mutation target del
        // slice (design mutation-targets, fila 1).
        var (anteriorAjuste, nuevoAjuste) = PayloadDeAuditoria.AjusteDeStock(
            nuevaCantidad - cantidad, nuevaCantidad, idMovimientoStock, observaciones);
        await auditoria.RegistrarAsync(
            conexion, transaccionCruda,
            new RegistroDeAuditoria(idTenant, idPuntoVenta, AccionAuditada.StockAjuste, idArticulo, anteriorAjuste, nuevoAjuste),
            ct);

        await transaccion.CommitAsync(ct);

        return nuevaCantidad;
    }

    // ---- mínimos de reposición (stage-13-stock-inteligente, Slice 1; design decisión 10/11) -----

    /// <summary>Design decisión 10/11: <c>PUT /api/stock/minimos</c> — validación EN MEMORIA
    /// primero, dos pre-checks de existencia/tenant, y UN statement crudo (<see
    /// cref="UpsertParametrosDeReposicionAsync"/>) que crea la fila en <c>cantidad = 0</c> si
    /// falta y jamás la toca si existe. Sin transacción, sin lock explícito, sin fila de
    /// <c>movimientos_stock</c> — un parámetro de reposición no es un hecho del ledger.</summary>
    public async Task<MinimosDeStock> EscribirMinimosAsync(SolicitudDeMinimos solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();

        ExigirUmbralValido(solicitud.Minimo, "mínimo");
        ExigirUmbralValido(solicitud.Reposicion, "reposición");

        if (solicitud.Minimo is { } minimo && solicitud.Reposicion is { } reposicion && reposicion < minimo)
        {
            throw new ErrorDominio(
                "reposicion_menor_que_minimo",
                "La reposición no puede ser menor que el mínimo cuando ambos están definidos.",
                400);
        }

        // Pre-checks de existencia/tenant ANTES del statement — mismo criterio que AjustarAsync:
        // la referencia se valida sobre una lectura simple, nunca dejando que la FK real de la
        // base la rechace con un 500 crudo dentro del INSERT de abajo.
        await ResolverArticuloAsync(solicitud.IdArticulo, ct);
        await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var fila = await UpsertParametrosDeReposicionAsync(conexion, idTenant, solicitud, ct);

        return new MinimosDeStock(
            solicitud.IdPuntoVenta, solicitud.IdArticulo, fila.Cantidad, fila.Minimo, fila.Reposicion,
            ReglaDeReposicion.Clasificar(fila.Cantidad, fila.Minimo));
    }

    /// <summary>Design decisión 10 — el mutation target nombrado del slice: <c>cantidad</c> ESTÁ
    /// en el <c>VALUES</c> (crea la fila en <c>0</c> cuando falta) y está AUSENTE del <c>SET</c>
    /// (jamás pisa un saldo existente) — la asimetría es la decisión entera, no una omisión. Sin
    /// transacción: un único statement con <c>RETURNING</c> ya es atómico por sí mismo.</summary>
    private static async Task<(decimal Cantidad, decimal? Minimo, decimal? Reposicion)> UpsertParametrosDeReposicionAsync(
        DbConnection conexion, int idTenant, SolicitudDeMinimos solicitud, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        // Los umbrales son de la sucursal del punto de venta: la fila de stock que comparten sus cajas.
        comando.CommandText =
            "INSERT INTO stock (id_articulo, id_sucursal, id_tenant, cantidad, minimo, reposicion) " +
            "SELECT $1, pv.id_sucursal, $3, 0, $4::numeric, $5::numeric FROM puntos_venta pv WHERE pv.id_punto_venta = $2 " +
            "ON CONFLICT (id_articulo, id_sucursal) DO UPDATE " +
            "SET minimo = EXCLUDED.minimo, reposicion = EXCLUDED.reposicion " +
            "RETURNING cantidad, minimo, reposicion";

        ParametrosDeComando.Agregar(comando, solicitud.IdArticulo);
        ParametrosDeComando.Agregar(comando, solicitud.IdPuntoVenta);
        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.AgregarNulo(comando, solicitud.Minimo);
        ParametrosDeComando.AgregarNulo(comando, solicitud.Reposicion);

        await using var lector = await comando.ExecuteReaderAsync(ct);
        if (!await lector.ReadAsync(ct))
        {
            throw new InvalidOperationException("El upsert de mínimos de reposición no devolvió ninguna fila.");
        }

        return (
            lector.GetDecimal(0),
            lector.IsDBNull(1) ? null : lector.GetDecimal(1),
            lector.IsDBNull(2) ? null : lector.GetDecimal(2));
    }

    /// <summary>Design: New Domain error codes — familia <c>minimo_negativo</c>/<c>
    /// minimo_invalido</c> (un código, el mensaje nombra el campo ofendido — mismo criterio que
    /// <c>cantidad_de_ajuste_invalida</c> en ajuste/decomiso). Valida EN MEMORIA, antes de
    /// resolver cualquier referencia: <c>&gt;= 0</c> y hasta 3 decimales — sin el segundo
    /// chequeo, Postgres redondearía en silencio el valor que el operador tipeó al truncar a
    /// <c>numeric(12,3)</c>. <c>null</c> pasa sin validar (campo sin gestionar).</summary>
    private static void ExigirUmbralValido(decimal? valor, string campo)
    {
        if (valor is not { } numero)
        {
            return;
        }

        if (numero < 0m)
        {
            throw new ErrorDominio("minimo_negativo", $"El {campo} no puede ser negativo.", 400);
        }

        if (decimal.Round(numero, 3, MidpointRounding.AwayFromZero) != numero)
        {
            throw new ErrorDominio("minimo_invalido", $"El {campo} admite hasta 3 decimales.", 400);
        }
    }

    // ---- decomiso (design: Write site 3 — "EjecutarDecomisoAsync, structurally EjecutarAjusteAsync
    // with three deltas"; proposal decisión 9) --------------------------------------------------

    /// <summary>Design: API Surface — <c>POST /api/stock/decomiso</c>, <c>motivo = decomiso</c>,
    /// primera clase (proposal decisión 9: NO una bandera de <c>ajuste</c>). Tres diferencias con
    /// <see cref="AjustarAsync"/>: (1) <see cref="SolicitudDeDecomiso.Cantidad"/> llega SIEMPRE
    /// positiva y se niega acá, nunca en el servicio de abajo (misma disciplina que
    /// <c>ContarAsync</c> — el cliente nunca manda un delta con signo); (2) el único rechazo de
    /// negatividad de este servicio (<c>409 stock_insuficiente_para_decomiso</c>) sobre el saldo
    /// OPERATIVO — el del lote si es lote-efectivo, si no el agregado (spec: "the target balance");
    /// (3) <c>observaciones</c> obligatoria igual que un ajuste. NO restringido a lotes vencidos
    /// (decisión 9 del proposal) — la merma real entra en el mismo cajón que la vencida.</summary>
    public async Task<decimal> DecomisarAsync(SolicitudDeDecomiso solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var cantidadPositiva = ExigirCantidadDeDecomisoValida(solicitud.Cantidad);
        var observaciones = ExigirObservaciones(solicitud.Observaciones);

        var articulo = await ResolverArticuloAsync(solicitud.IdArticulo, ct);
        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        var idLote = await ResolverIdLoteEfectivoAsync(puntoVenta.IdEmpresa, puntoVenta.Id, articulo, solicitud.IdLote, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarDecomisoAsync(
                idTenant, idEmpleado, solicitud.IdArticulo, solicitud.IdPuntoVenta, cantidadPositiva, observaciones,
                idLote, momento, ct));
    }

    private async Task<decimal> EjecutarDecomisoAsync(
        int idTenant, int idEmpleado, int idArticulo, int idPuntoVenta, decimal cantidadPositiva, string observaciones,
        int? idLote, DateTimeOffset momento, CancellationToken ct)
    {
        // Disciplina de ContarAsync: nunca un delta con signo provisto por el cliente — acá es
        // donde el "positivo" del contrato se convierte en la baja real.
        var delta = -cantidadPositiva;

        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var idSucursal = await EscritorDeStock.ResolverSucursalAsync(conexion, transaccionCruda, idPuntoVenta, ct);

        var aplicado = (await EscritorDeStock.AsentarAsync(
            conexion, transaccionCruda,
            [
                new AsientoDeStock(idTenant, idSucursal, idPuntoVenta, idArticulo, delta, MotivoStock.Decomiso, idEmpleado, momento)
                {
                    IdLote = idLote,
                    Observaciones = observaciones
                }
            ],
            ct))[0];
        var idMovimientoStock = aplicado.IdMovimiento;
        var nuevaAgregada = aplicado.CantidadAgregada;

        if (idLote is { } idLoteEfectivo)
        {
            // spec lotes-y-vencimientos: "the target balance (the lot's stock_lotes.cantidad when
            // lot-effective, otherwise stock.cantidad)" — SOLO el lote decide cuando es
            // lote-efectivo, el agregado nunca se vuelve a chequear acá (a diferencia de una
            // transferencia, que chequea ambos).
            if (aplicado.CantidadDelLote < 0m)
            {
                throw new ErrorDominio(
                    "stock_insuficiente_para_decomiso",
                    $"No hay stock suficiente del lote {idLoteEfectivo} del artículo {idArticulo} para decomisar.",
                    409);
            }
        }
        else if (nuevaAgregada < 0m)
        {
            throw new ErrorDominio(
                "stock_insuficiente_para_decomiso",
                $"No hay stock suficiente del artículo {idArticulo} para decomisar.",
                409);
        }

        // Etapa 14, slice 4 (design call site 10): recién DESPUÉS de los dos rechazos de
        // negatividad — un decomiso rechazado no deja fila de auditoría. Before-image sobre el
        // AGREGADO (nueva − delta), mismo criterio de decisión 9 que el ajuste.
        var (anteriorDecomiso, nuevoDecomiso) = PayloadDeAuditoria.DecomisoDeStock(
            nuevaAgregada - delta, nuevaAgregada, idMovimientoStock, observaciones, idLote);
        await auditoria.RegistrarAsync(
            conexion, transaccionCruda,
            new RegistroDeAuditoria(idTenant, idPuntoVenta, AccionAuditada.StockDecomiso, idArticulo, anteriorDecomiso, nuevoDecomiso),
            ct);

        await transaccion.CommitAsync(ct);

        return nuevaAgregada;
    }

    // ---- transferencia entre puntos de venta (design: Transactions — TRANSFERENCIA; decisión 9) ----

    /// <summary>Design decisión 9: rechaza líneas vacías/inválidas en memoria, antes de cualquier
    /// consulta. Etapa 12, slice 10 (design: Write site 3): el lote viaja — cada línea
    /// lote-efectiva resuelve su <c>idLote</c> (explícito o FEFO-defaulted) en la fase de
    /// resolución, ANTES de que la transacción abra, y esa fase es también donde corren el
    /// rechazo de duplicados <c>(IdArticulo, IdLote)</c> post-defaulting (decisión 11) y el
    /// chequeo de <c>transferencia_lote_vencido</c>. El stock es de la sucursal: una transferencia entre cajas de la misma
    /// sucursal se rechaza (<c>409 transferencia_misma_sucursal</c>); dentro de la transacción se vuelven a leer las dos
    /// sucursales bajo lock, una caja que cambió de sucursal desde la lectura previa es
    /// <c>409 sucursal_cambio_durante_transferencia</c>, y las <c>2N</c> claves se escriben en el orden de locks de
    /// <see cref="EscritorDeStock"/> <c>(id_sucursal, id_articulo, id_lote NULLS FIRST)</c>, el que evita el deadlock
    /// contra una transferencia inversa simultánea y contra un checkout concurrente en cualquiera de las dos
    /// sucursales.</summary>
    public async Task<ResultadoTransferencia> TransferirAsync(SolicitudDeTransferencia solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var (idPuntoVentaOrigen, idPuntoVentaDestino) = await ResolverExtremosDeTransferenciaAsync(solicitud, ct);

        var observaciones = ExigirObservaciones(solicitud.Observaciones);
        var lineas = ExigirLineasDeTransferenciaValidas(solicitud.Lineas);

        // Pre-checks de existencia/tenant ANTES de la transacción (mismo criterio que
        // AjustarAsync): ResolverPuntoVentaAsync da el mismo 404 para "no existe" y "es de otro
        // tenant" (ADR-8), tanto para origen como para destino.
        var puntoVentaOrigen = await ResolverPuntoVentaAsync(idPuntoVentaOrigen, ct);
        var puntoVentaDestino = await ResolverPuntoVentaAsync(idPuntoVentaDestino, ct);

        // Dos cajas de una misma sucursal comparten el stock: se rechaza antes de resolver lotes, que no tiene
        // sentido hacer para un movimiento que no existe. Las sucursales esperadas son las pedidas o, por caja, las
        // leídas acá; EjecutarTransferenciaAsync las vuelve a exigir bajo lock.
        var sucursalesEsperadas = (
            Origen: solicitud.IdSucursalOrigen ?? puntoVentaOrigen.IdSucursal,
            Destino: solicitud.IdSucursalDestino ?? puntoVentaDestino.IdSucursal);

        if (puntoVentaOrigen.IdSucursal == puntoVentaDestino.IdSucursal)
        {
            throw ErrorTransferenciaMismaSucursal();
        }

        var articuloPorId = new Dictionary<int, Articulo>();
        foreach (var idArticulo in lineas.Select(l => l.IdArticulo).Distinct())
        {
            articuloPorId[idArticulo] = await ResolverArticuloAsync(idArticulo, ct);
        }

        // Etapa 12, slice 10 (design: Write site 3 — "lot resolution happens before the
        // transaction opens, same phase as the existing pre-checks"): FEFO-default de lotes
        // omitidos, chequeo de vencido y rechazo de duplicados post-defaulting.
        var lineasResueltas = await ResolverLineasDeTransferenciaAsync(
            idTenant, puntoVentaOrigen, articuloPorId, lineas, momento, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarTransferenciaAsync(
                idTenant, idEmpleado, idPuntoVentaOrigen, idPuntoVentaDestino, sucursalesEsperadas, lineasResueltas,
                observaciones, momento, ct));
    }

    /// <summary>Las dos cajas de la transferencia, vengan como puntos de venta o como sucursales (exactamente una de las
    /// dos formas, <c>400 transferencia_origen_destino_invalido</c> si no). Mismo origen y destino es
    /// <c>400 transferencia_origen_igual_destino</c> en cualquiera de las dos formas, en memoria y antes de consultar.
    /// Una sucursal se traduce a su caja viva de menor id: es solo la caja de trazabilidad del asiento, el stock se
    /// escribe en la sucursal. Una sucursal inexistente, ajena o dada de baja es 404 y una sin cajas vivas es
    /// <c>409 sucursal_sin_puntos_venta</c>.</summary>
    private async Task<(int Origen, int Destino)> ResolverExtremosDeTransferenciaAsync(
        SolicitudDeTransferencia solicitud, CancellationToken ct)
    {
        var porPuntoVenta = solicitud.IdPuntoVentaOrigen is not null || solicitud.IdPuntoVentaDestino is not null;
        var porSucursal = solicitud.IdSucursalOrigen is not null || solicitud.IdSucursalDestino is not null;

        if (porPuntoVenta == porSucursal
            || (porPuntoVenta && (solicitud.IdPuntoVentaOrigen is null || solicitud.IdPuntoVentaDestino is null))
            || (porSucursal && (solicitud.IdSucursalOrigen is null || solicitud.IdSucursalDestino is null)))
        {
            throw new ErrorDominio(
                "transferencia_origen_destino_invalido",
                "Indicá el origen y el destino de la transferencia como dos puntos de venta o como dos sucursales, no mezclados.",
                400);
        }

        var origenIgualDestino = porPuntoVenta
            ? solicitud.IdPuntoVentaOrigen == solicitud.IdPuntoVentaDestino
            : solicitud.IdSucursalOrigen == solicitud.IdSucursalDestino;

        if (origenIgualDestino)
        {
            throw new ErrorDominio(
                "transferencia_origen_igual_destino",
                porPuntoVenta
                    ? "El origen y el destino de una transferencia tienen que ser puntos de venta distintos."
                    : "El origen y el destino de una transferencia tienen que ser sucursales distintas.",
                400);
        }

        if (porPuntoVenta)
        {
            return (solicitud.IdPuntoVentaOrigen!.Value, solicitud.IdPuntoVentaDestino!.Value);
        }

        return (
            await ResolverCajaDeSucursalAsync(solicitud.IdSucursalOrigen!.Value, ct),
            await ResolverCajaDeSucursalAsync(solicitud.IdSucursalDestino!.Value, ct));
    }

    private async Task<int> ResolverCajaDeSucursalAsync(int idSucursal, CancellationToken ct)
    {
        if (!await db.Sucursales.AnyAsync(s => s.Id == idSucursal, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe la sucursal {idSucursal}.");
        }

        var idCaja = await db.PuntosVenta
            .Where(p => p.IdSucursal == idSucursal)
            .OrderBy(p => p.Id)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);

        return idCaja
            ?? throw ErrorDominio.Conflicto(
                "sucursal_sin_puntos_venta", $"La sucursal {idSucursal} no tiene puntos de venta vivos.");
    }

    private async Task<ResultadoTransferencia> EjecutarTransferenciaAsync(
        int idTenant, int idEmpleado, int idPuntoVentaOrigen, int idPuntoVentaDestino,
        (int Origen, int Destino) sucursalesEsperadas, IReadOnlyList<LineaDeTransferenciaResuelta> lineas,
        string observaciones, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // Las dos cajas se toman con FOR SHARE en orden de id, el mismo orden en que la fusión de sucursales las toma con
        // FOR UPDATE: tomarlas en orden origen/destino podría cruzarse con ella.
        var sucursalPorCaja = new Dictionary<int, int>();
        foreach (var idCaja in new[] { idPuntoVentaOrigen, idPuntoVentaDestino }.Order())
        {
            sucursalPorCaja[idCaja] = await EscritorDeStock.ResolverSucursalAsync(conexion, transaccionCruda, idCaja, ct);
        }

        var idSucursalOrigen = sucursalPorCaja[idPuntoVentaOrigen];
        var idSucursalDestino = sucursalPorCaja[idPuntoVentaDestino];

        // Una caja que cambió de sucursal después de que se la eligió (o de que se resolvieron sus lotes) se rechaza: la
        // transferencia saldría de, o llegaría a, una sucursal que nadie pidió. Como las esperadas ya son distintas,
        // esto también cubre el caso de dos cajas que terminaron en la misma sucursal.
        if (idSucursalOrigen != sucursalesEsperadas.Origen || idSucursalDestino != sucursalesEsperadas.Destino)
        {
            throw ErrorDominio.Conflicto(
                "sucursal_cambio_durante_transferencia",
                "Una de las cajas de la transferencia cambió de sucursal mientras se preparaba. Volvé a intentarlo.");
        }

        // Un asiento por línea y por lado: el movimiento (con su lote) y los upserts del agregado y del lote.
        // EscritorDeStock los escribe en el orden de locks (sucursal, artículo, lote) sobre las 2N claves, el mismo
        // orden que un checkout o una transferencia inversa simultánea, así que ninguna se deadlockea con esta.
        var asientos = lineas
            .SelectMany(l => new[]
            {
                AsientoDeTransferencia(idSucursalOrigen, idPuntoVentaOrigen, -l.Cantidad, l),
                AsientoDeTransferencia(idSucursalDestino, idPuntoVentaDestino, l.Cantidad, l)
            })
            .ToList();

        AsientoDeStock AsientoDeTransferencia(int idSucursal, int idPuntoVenta, decimal delta, LineaDeTransferenciaResuelta linea) =>
            new(idTenant, idSucursal, idPuntoVenta, linea.IdArticulo, delta, MotivoStock.Transferencia, idEmpleado, momento)
            {
                IdLote = linea.IdLote,
                Observaciones = observaciones,
                IdPuntoVentaDestino = idPuntoVentaDestino,
                IdSucursalDestino = idSucursalDestino
            };

        var aplicados = await EscritorDeStock.AsentarAsync(conexion, transaccionCruda, asientos, ct);

        // Los RETURNING de los upserts SON el chequeo de suficiencia: sin segunda consulta, sin TOCTOU. Solo el lado que
        // resta, y por lote además del agregado (un agregado suficiente puede esconder un lote que no alcanza).
        // Asimétrico a propósito respecto de una venta, que nunca bloquea.
        foreach (var aplicado in aplicados.Where(a => a.Asiento.Cantidad < 0m))
        {
            if (aplicado.CantidadAgregada < 0m)
            {
                throw new ErrorDominio(
                    "stock_insuficiente_para_transferencia",
                    $"No hay stock suficiente del artículo {aplicado.Asiento.IdArticulo} en el punto de venta de origen para transferir.",
                    409);
            }

            if (aplicado.CantidadDelLote < 0m)
            {
                throw new ErrorDominio(
                    "stock_insuficiente_para_transferencia",
                    $"No hay stock suficiente del lote {aplicado.Asiento.IdLote} del artículo {aplicado.Asiento.IdArticulo} en el punto de venta de origen para transferir.",
                    409);
            }
        }

        // Clave (IdArticulo, IdLote): dos líneas del mismo artículo con lotes distintos son aceptadas y no pueden
        // colapsar en una sola fila del response (dto-contract-honesty). El saldo informado es el agregado posterior.
        var resultadosPorArticuloYLote = new Dictionary<(int IdArticulo, int? IdLote), (decimal Origen, decimal Destino)>();
        foreach (var aplicado in aplicados)
        {
            var claveResultado = (aplicado.Asiento.IdArticulo, aplicado.Asiento.IdLote);
            var previo = resultadosPorArticuloYLote.TryGetValue(claveResultado, out var existente) ? existente : (Origen: 0m, Destino: 0m);
            resultadosPorArticuloYLote[claveResultado] = aplicado.Asiento.IdSucursal == idSucursalOrigen
                ? (aplicado.CantidadAgregada, previo.Destino)
                : (previo.Origen, aplicado.CantidadAgregada);
        }

        await transaccion.CommitAsync(ct);

        var lineasResultado = resultadosPorArticuloYLote
            .OrderBy(kv => kv.Key.IdArticulo)
            .ThenBy(kv => kv.Key.IdLote.HasValue)   // NULLS FIRST, mismo criterio que EscritorDeStock
            .ThenBy(kv => kv.Key.IdLote ?? 0)
            .Select(kv => new LineaTransferida(kv.Key.IdArticulo, kv.Key.IdLote, kv.Value.Origen, kv.Value.Destino))
            .ToList();

        return new ResultadoTransferencia(
            idPuntoVentaOrigen, idPuntoVentaDestino, lineasResultado, idSucursalOrigen, idSucursalDestino);
    }

    private static ErrorDominio ErrorTransferenciaMismaSucursal() =>
        new(
            "transferencia_misma_sucursal",
            "El origen y el destino pertenecen a la misma sucursal y comparten el stock; no hay nada que transferir.",
            409);

    /// <summary>Línea de transferencia YA resuelta (design: "LineaResuelta") — <see cref="IdLote"/>
    /// es <c>null</c> para un artículo sin control de lote efectivo, o el lote explícito/FEFO-defaulted
    /// para uno lote-efectivo. Producida por <see cref="ResolverLineasDeTransferenciaAsync"/>, fuera
    /// de la transacción: dentro de ella nunca se vuelve a tocar <c>lotes</c>.</summary>
    private readonly record struct LineaDeTransferenciaResuelta(int IdArticulo, decimal Cantidad, int? IdLote);

    /// <summary>Etapa 12, slice 10 (design: Write site 3, fase de resolución — "keeps lotes and
    /// reads out of the transaction entirely"): para cada línea lote-efectiva, resuelve su lote
    /// (explícito validado contra <see cref="ServicioDeLotes.LeerSaldosAsync"/>, o FEFO-defaulted,
    /// o el sin-identificar perezoso cuando ningún lote tiene saldo positivo — mismo camino que
    /// <c>ServicioDeVentas</c>), rechaza un vencido (<c>transferencia_lote_vencido</c> — a
    /// diferencia del checkout, acá SIEMPRE bloquea, sea el lote explícito o resuelto por FEFO) y,
    /// al final, rechaza duplicados <c>(IdArticulo, IdLote)</c> evaluados DESPUÉS del defaulting
    /// (decisión 11 — reusa el código <c>articulo_repetido</c>).</summary>
    private async Task<IReadOnlyList<LineaDeTransferenciaResuelta>> ResolverLineasDeTransferenciaAsync(
        int idTenant, PuntoVenta puntoVentaOrigen, IReadOnlyDictionary<int, Articulo> articuloPorId,
        IReadOnlyList<LineaDeTransferencia> lineas, DateTimeOffset momento, CancellationToken ct)
    {
        var lineasResueltas = lineas.Select(l => new LineaDeTransferenciaResuelta(l.IdArticulo, l.Cantidad, IdLote: null)).ToList();

        var indicesConArticuloControlaLote = lineas
            .Select((l, indice) => (Linea: l, Indice: indice))
            .Where(x => articuloPorId[x.Linea.IdArticulo].ControlaLote)
            .Select(x => x.Indice)
            .ToList();

        var lotesHabilitado = indicesConArticuloControlaLote.Count > 0
            && await ResolverLotesHabilitadoAsync(puntoVentaOrigen.IdEmpresa, puntoVentaOrigen.Id, ct);

        var indicesConLoteEfectivo = lotesHabilitado ? indicesConArticuloControlaLote : [];

        // dto-contract-honesty / mismo criterio que ServicioDeVentas: un idLote en una línea SIN
        // lote efectivo no tiene destino — se rechaza en vez de tragárselo en silencio.
        var indicesConLoteEfectivoSet = indicesConLoteEfectivo.ToHashSet();
        for (var indice = 0; indice < lineas.Count; indice++)
        {
            if (!indicesConLoteEfectivoSet.Contains(indice) && lineas[indice].IdLote is not null)
            {
                throw new ErrorDominio(
                    "lote_invalido",
                    $"El artículo {lineas[indice].IdArticulo} no tiene lote efectivo; no admite idLote.",
                    400);
            }
        }

        if (indicesConLoteEfectivo.Count > 0)
        {
            var idsArticuloConLote = indicesConLoteEfectivo.Select(i => lineas[i].IdArticulo).Distinct().ToList();
            var idsLotePedidos = indicesConLoteEfectivo
                .Select(i => lineas[i].IdLote)
                .Where(idLote => idLote is not null)
                .Select(idLote => idLote!.Value)
                .Distinct()
                .ToList();

            var saldos = await servicioDeLotes.LeerSaldosAsync(puntoVentaOrigen.Id, idsArticuloConLote, idsLotePedidos, ct);
            var saldosPorArticulo = saldos.ToLookup(s => s.IdArticulo);

            // Honestidad documental: "hoy" acá es UTC naive, mismo criterio interino que
            // ServicioDeVentas/ServicioDeCompras/ServicioDeLotes en esta etapa.
            var hoy = DateOnly.FromDateTime(momento.UtcDateTime);

            foreach (var indice in indicesConLoteEfectivo)
            {
                var linea = lineas[indice];
                var saldosDelArticulo = saldosPorArticulo[linea.IdArticulo].ToList();

                SaldoDeLote loteResuelto;
                if (linea.IdLote is { } idLote)
                {
                    var posicion = saldosDelArticulo.FindIndex(s => s.IdLote == idLote);
                    if (posicion < 0)
                    {
                        throw new ErrorDominio(
                            "lote_invalido",
                            $"El lote {idLote} no existe, no pertenece al artículo {linea.IdArticulo} o fue eliminado.",
                            400);
                    }

                    loteResuelto = saldosDelArticulo[posicion];
                }
                else if (ReglaDeLotes.ElegirFefo(saldosDelArticulo, hoy) is { } elegido)
                {
                    loteResuelto = elegido;
                }
                else
                {
                    // Ningún lote con saldo positivo (design decisión 7) — get-or-create perezoso
                    // del sin-identificar, statement crudo fuera de la transacción. En una
                    // transferencia (a diferencia del checkout) esto típicamente desemboca en
                    // stock_insuficiente_para_transferencia dentro de la transacción — el sin
                    // identificar arranca en 0, y transferir cualquier cantidad positiva lo deja
                    // negativo, exactamente el mismo camino de rechazo que un lote explícito
                    // insuficiente.
                    var conexionParaLotes = await ObtenerConexionAbiertaAsync(ct);
                    var idSinIdentificar = await ServicioDeLotes.ResolverSinIdentificarAsync(
                        conexionParaLotes, transaccion: null, idTenant, linea.IdArticulo, momento, ct);

                    loteResuelto = new SaldoDeLote(
                        linea.IdArticulo, idSinIdentificar, ReglaDeLotes.CodigoSinIdentificar,
                        EsSinIdentificar: true, FechaVencimiento: null, Cantidad: 0m);
                }

                // spec transferencias-de-stock: "Expired Lot Transfer Is Refused" — a diferencia
                // del checkout (decisión 12: solo un warning), una transferencia SIEMPRE bloquea
                // sobre un lote vencido, sea explícito o resuelto por FEFO.
                if (ReglaDeLotes.EstaVencido(loteResuelto.FechaVencimiento, hoy))
                {
                    throw new ErrorDominio(
                        "transferencia_lote_vencido",
                        $"El lote {loteResuelto.Codigo} del artículo {linea.IdArticulo} está vencido; no se puede transferir.",
                        409);
                }

                lineasResueltas[indice] = lineasResueltas[indice] with { IdLote = loteResuelto.IdLote };
            }
        }

        // spec transferencias-de-stock: "Duplicate-Line Detection Widens To (IdArticulo, IdLote),
        // Evaluated After FEFO Defaulting" (decisión 11) — reusa el código articulo_repetido,
        // evaluado sobre el resultado YA resuelto, nunca contra el input crudo del cliente.
        var repetida = lineasResueltas
            .GroupBy(l => (l.IdArticulo, l.IdLote))
            .FirstOrDefault(g => g.Count() > 1);
        if (repetida is not null)
        {
            throw new ErrorDominio(
                "articulo_repetido",
                $"El artículo {repetida.Key.IdArticulo} aparece más de una vez en la transferencia para el mismo lote.",
                400);
        }

        return lineasResueltas;
    }

    /// <summary>Mismo patrón que <see cref="ServicioDeLotes"/>'s resolución de un único parámetro
    /// de clave (<c>ResolverDiasAlertaAsync</c>): candidatos filtrados por <c>Clave</c> ANTES de
    /// <c>ResolucionDeParametros.Resolver</c> (design decisión 2 — nunca un candidato multi-clave
    /// sin filtrar).</summary>
    private async Task<bool> ResolverLotesHabilitadoAsync(int idEmpresa, int idPuntoVenta, CancellationToken ct)
    {
        var candidatos = await db.Parametros
            .Where(p => p.Clave == ParametroConocido.LotesHabilitado.Clave && p.IdEmpresa == idEmpresa
                && (p.IdPuntoVenta == null || p.IdPuntoVenta == idPuntoVenta))
            .ToListAsync(ct);

        var valorJson = ResolucionDeParametros.Resolver(ParametroConocido.LotesHabilitado.Clave, candidatos, idPuntoVenta);
        return JsonSerializer.Deserialize<bool>(valorJson);
    }

    /// <summary>Etapa 12, slice 11 (design: Write site 3 — "IdLote required when lot-effective
    /// (lote_requerido), refused when not (lote_no_aplica)"). Compartido por
    /// <see cref="AjustarAsync"/> y <see cref="DecomisarAsync"/> — ambos operan sobre UN solo
    /// artículo/punto de venta, a diferencia de <see cref="ResolverLineasDeTransferenciaAsync"/>
    /// (multi-línea, con FEFO-default). Acá NO hay FEFO-default: el ajuste/decomiso de un
    /// artículo lote-efectivo siempre exige el <c>idLote</c> explícito — no hay "línea de venta"
    /// que el operador esté surtiendo, así que no hay lote físico implícito que adivinar.
    /// <see cref="ServicioDeLotes.LeerSaldosAsync"/> valida que el <c>idLote</c> explícito existe,
    /// pertenece al artículo y no está borrado (<c>400 lote_invalido</c>) — mismo criterio de
    /// nunca dejar que la FK real de la base rechace con un 500 crudo.</summary>
    private async Task<int?> ResolverIdLoteEfectivoAsync(
        int idEmpresa, int idPuntoVenta, Articulo articulo, int? idLotePedido, CancellationToken ct)
    {
        var lotesHabilitado = await ResolverLotesHabilitadoAsync(idEmpresa, idPuntoVenta, ct);
        var esLoteEfectivo = ReglaDeLotes.ControlEfectivo(articulo.ControlaLote, lotesHabilitado);

        if (!esLoteEfectivo)
        {
            if (idLotePedido is not null)
            {
                throw new ErrorDominio(
                    "lote_no_aplica",
                    $"El artículo {articulo.Id} no tiene lote efectivo; no admite idLote.",
                    400);
            }

            return null;
        }

        if (idLotePedido is null)
        {
            throw new ErrorDominio(
                "lote_requerido",
                $"El artículo {articulo.Id} es lote-efectivo; requiere idLote.",
                400);
        }

        var saldos = await servicioDeLotes.LeerSaldosAsync(idPuntoVenta, [articulo.Id], [idLotePedido.Value], ct);
        if (!saldos.Any(s => s.IdLote == idLotePedido.Value))
        {
            throw new ErrorDominio(
                "lote_invalido",
                $"El lote {idLotePedido} no existe, no pertenece al artículo {articulo.Id} o fue eliminado.",
                400);
        }

        return idLotePedido;
    }

    // ---- conteo de inventario (design: Transactions — CONTEO DE INVENTARIO; decisión 10) ----------

    /// <summary>Design decisión 10: el cliente manda el TOTAL contado, nunca un delta. El delta
    /// se deriva del lado del servidor bajo el mismo lock de fila que <c>AjustarAsync</c> usa
    /// (el upsert no-op de <see cref="EscritorDeStock.BloquearYCrearSiFaltaAsync"/>), así que un conteo
    /// nunca puede pisar una venta que corrió entre el conteo físico y el submit.
    ///
    /// Etapa 12, slice 12 (design decisión 18 — exactly-one-of; decisión 12 — conteo por lote):
    /// <see cref="SolicitudDeConteo.Contada"/>/<see cref="SolicitudDeConteo.Lotes"/> son mutuamente
    /// excluyentes, validado ANTES de resolver referencias (<c>400 conteo_contada_y_lotes</c>, ni
    /// siquiera un SELECT si el request está mal formado). La degradación pre-aprobada del
    /// proposal (decisión 11 — <c>409 conteo_lote_no_soportado</c>) NO se implementa en este
    /// slice: el conteo por lote se entrega completo, así que esa rama queda documentada acá pero
    /// deliberadamente sin código muerto (design: "keep the 409 branch reachable only if a future
    /// regression removes per-lot support" — no aplica hoy).
    ///
    /// Etapa 12, slice 12, judgment-day fix (juez B, FIX 1): el <c>409 conteo_lote_no_soportado</c>
    /// de arriba cubre "el per-lot conteo no está implementado" — no cubre "está implementado pero
    /// el cliente igual mandó un total agregado para un artículo lote-efectivo". Ese segundo caso
    /// se rechaza acá, ANTES de cualquier lock, con el código honesto <c>400
    /// conteo_requiere_lotes</c> — aceptar el total agregado hubiera movido <c>stock.cantidad</c>
    /// sin tocar <c>stock_lotes</c>, rompiendo el invariante 3 en silencio. Simetría inversa:
    /// <see cref="SolicitudDeConteo.Lotes"/> para un artículo SIN lote efectivo no tiene destino
    /// (<c>400 conteo_no_aplica_lotes</c>), mismo criterio que <c>lote_no_aplica</c> en
    /// <see cref="ResolverIdLoteEfectivoAsync"/>. (Amended at slice-12 judgment-day.)</summary>
    public async Task<ResultadoConteo> ContarAsync(SolicitudDeConteo solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        ExigirExactamenteUnaFormaDeConteo(solicitud.Contada, solicitud.Lotes);
        var observaciones = ExigirObservaciones(solicitud.Observaciones);

        var articulo = await ResolverArticuloAsync(solicitud.IdArticulo, ct);
        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        var lotesHabilitado = await ResolverLotesHabilitadoAsync(puntoVenta.IdEmpresa, puntoVenta.Id, ct);
        var esLoteEfectivo = ReglaDeLotes.ControlEfectivo(articulo.ControlaLote, lotesHabilitado);
        ExigirFormaDeConteoCoincideConControlDeLote(esLoteEfectivo, solicitud.Contada, solicitud.Lotes, solicitud.IdArticulo);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        if (solicitud.Lotes is { Count: > 0 } lotes)
        {
            var lotesValidados = ExigirLotesDeConteoValidos(lotes);

            // Etapa 12, slice 12, judgment-day fix (juez B, FIX 2): SELECT-first estilo
            // ResolverIdLoteEfectivoAsync — nunca dejar que la FK real de stock_lotes/lotes
            // rechace un id_lote inexistente/ajeno con un 500 crudo dentro del upsert no-op de
            // EscritorDeStock.BloquearYCrearSiFaltaDeLoteAsync.
            await ExigirLotesDeConteoExistenAsync(solicitud.IdPuntoVenta, solicitud.IdArticulo, lotesValidados, ct);

            return await estrategia.ExecuteAsync(async () =>
                await EjecutarConteoPorLoteAsync(
                    idTenant, idEmpleado, solicitud.IdPuntoVenta, solicitud.IdArticulo, lotesValidados, observaciones,
                    momento, ct));
        }

        var contada = ExigirContadaValida(solicitud.Contada!.Value);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarConteoAsync(idTenant, idEmpleado, solicitud.IdPuntoVenta, solicitud.IdArticulo, contada, observaciones, momento, ct));
    }

    private async Task<ResultadoConteo> EjecutarConteoAsync(
        int idTenant, int idEmpleado, int idPuntoVenta, int idArticulo, decimal contada, string observaciones,
        DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var idSucursal = await EscritorDeStock.ResolverSucursalAsync(conexion, transaccionCruda, idPuntoVenta, ct);
        var actual = await EscritorDeStock.BloquearYCrearSiFaltaAsync(conexion, transaccionCruda, idTenant, idSucursal, idArticulo, ct);
        var delta = contada - actual;

        if (delta == 0m)
        {
            // spec: "Zero-Difference Conteo Writes No Ledger Row" — commit sin escribir nada, ni
            // ledger NI auditoría (tasks.md, Orchestrator Decision #1: la operación entera queda
            // muda) — mutation target del slice (design mutation-targets, fila 2); además evita
            // ck_movimientos_stock_cantidad_no_cero (nunca lo alcanza).
            await transaccion.CommitAsync(ct);
            return new ResultadoConteo(idPuntoVenta, idArticulo, actual, actual, 0m, MovimientoRegistrado: false);
        }

        var aplicado = (await EscritorDeStock.AsentarAsync(
            conexion, transaccionCruda,
            [
                new AsientoDeStock(idTenant, idSucursal, idPuntoVenta, idArticulo, delta, MotivoStock.Inventario, idEmpleado, momento)
                {
                    Observaciones = observaciones
                }
            ],
            ct))[0];
        var idMovimientoStock = aplicado.IdMovimiento;
        var final = aplicado.CantidadAgregada;

        if (final != contada)
        {
            // Defensa en profundidad (design decisión 5): bajo el lock tomado en el paso 1, esta
            // rama es inalcanzable en operación normal — nadie más pudo escribir esa fila entre
            // el lock y este upsert.
            throw new InvalidOperationException(
                $"El conteo de inventario produjo un resultado inconsistente: esperado {contada}, obtenido {final}.");
        }

        // Etapa 14, slice 4 (design call site 11; tasks.md Orchestrator Decision #1): UNA fila
        // por OPERACIÓN de conteo. El conteo agregado (sin desglose por lote) no toca ningún
        // lote — lotesAfectados = 0, el único movimiento es el agregado.
        var (anteriorConteo, nuevoConteo) = PayloadDeAuditoria.Conteo(
            actual, final, [idMovimientoStock], lotesAfectados: 0, delta);
        await auditoria.RegistrarAsync(
            conexion, transaccionCruda,
            new RegistroDeAuditoria(idTenant, idPuntoVenta, AccionAuditada.StockConteo, idArticulo, anteriorConteo, nuevoConteo),
            ct);

        await transaccion.CommitAsync(ct);

        return new ResultadoConteo(idPuntoVenta, idArticulo, final, actual, delta, MovimientoRegistrado: delta != 0m);
    }

    /// <summary>Etapa 12, slice 12 (design decisión 12 — "the per-lot conteo acquires all its locks
    /// first ..., derives every delta, and only then writes"): split ADQUISICIÓN/APLICACIÓN. El
    /// agregado no puede escribirse hasta conocer TODOS los deltas por lote (su delta es la SUMA de
    /// ellos) pero el orden pineado lo exige PRIMERO — la única salida sin inventar un segundo
    /// protocolo de lock es tomar cada lock (agregado, después cada lote ascendente) como un
    /// upsert no-op ANTES de escribir ningún delta, y recién ahí aplicar. Los locks ya tomados
    /// vuelven irrelevante el orden de la fase de aplicación para la concurrencia — se mantiene
    /// ascendente por determinismo del resultado, no por lock order.</summary>
    private async Task<ResultadoConteo> EjecutarConteoPorLoteAsync(
        int idTenant, int idEmpleado, int idPuntoVenta, int idArticulo, IReadOnlyList<ConteoDeLote> lotes,
        string observaciones, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // ---- ADQUISICIÓN: todos los locks, en el orden pineado (agregado, después cada lote
        // ascendente por id_lote) — ningún delta escrito todavía.
        var idSucursal = await EscritorDeStock.ResolverSucursalAsync(conexion, transaccionCruda, idPuntoVenta, ct);
        var actualAgregado = await EscritorDeStock.BloquearYCrearSiFaltaAsync(
            conexion, transaccionCruda, idTenant, idSucursal, idArticulo, ct);

        var lotesAscendentes = lotes.OrderBy(l => l.IdLote).ToList();
        var actualPorLote = new Dictionary<int, decimal>();
        foreach (var lote in lotesAscendentes)
        {
            actualPorLote[lote.IdLote] = await EscritorDeStock.BloquearYCrearSiFaltaDeLoteAsync(
                conexion, transaccionCruda, idTenant, idSucursal, idArticulo, lote.IdLote, ct);
        }

        // ---- APLICACIÓN: todos los locks ya están tomados. Un lote sin diferencia no escribe fila
        // (spec: "A lot with no difference writes no row even when a sibling lot differs") — y
        // nunca se fabrica saldo en el sin-identificar para absorber una diferencia (spec: "A
        // lot-effective conteo never writes into the sin-identificar lot"): el delta SIEMPRE se
        // escribe con el id_lote exacto que lo originó.
        var resultadosPorLote = new List<LoteContado>(lotesAscendentes.Count);
        var deltaAgregado = 0m;
        var cantidadAgregadaFinal = actualAgregado;

        // Etapa 14, slice 4 (tasks.md Orchestrator Decision #1, VINCULANTE — resuelve el
        // conflicto entre design.md y spec.md a favor del spec): acumula acá, a través del loop,
        // en vez de escribir una fila de auditoría por lote — la fila se escribe UNA sola vez,
        // después del loop, por OPERACIÓN de conteo.
        var movimientosGenerados = new List<int>();

        foreach (var lote in lotesAscendentes)
        {
            var actualDelLote = actualPorLote[lote.IdLote];
            var deltaDelLote = lote.Contada - actualDelLote;

            if (deltaDelLote == 0m)
            {
                resultadosPorLote.Add(new LoteContado(lote.IdLote, actualDelLote, actualDelLote, 0m, MovimientoRegistrado: false));
                continue;
            }

            // El agregado acumula la SUMA de los deltas por lote (design decisión 12): un asiento por lote con
            // diferencia, que re-bloquea filas que esta transacción ya tiene.
            var aplicado = (await EscritorDeStock.AsentarAsync(
                conexion, transaccionCruda,
                [
                    new AsientoDeStock(
                        idTenant, idSucursal, idPuntoVenta, idArticulo, deltaDelLote, MotivoStock.Inventario, idEmpleado, momento)
                    {
                        IdLote = lote.IdLote,
                        Observaciones = observaciones
                    }
                ],
                ct))[0];
            movimientosGenerados.Add(aplicado.IdMovimiento);

            var finalDelLote = aplicado.CantidadDelLote!.Value;
            cantidadAgregadaFinal = aplicado.CantidadAgregada;

            deltaAgregado += deltaDelLote;
            resultadosPorLote.Add(new LoteContado(lote.IdLote, finalDelLote, actualDelLote, deltaDelLote, MovimientoRegistrado: true));
        }

        // Ningún lote difirió ⇒ cero filas de ledger Y cero de auditoría para la operación entera
        // (mismo criterio que el early-return del conteo agregado, tasks.md Orchestrator Decision
        // #1). Al menos un lote difirió ⇒ UNA fila, con la lista completa de movimientos y el
        // conteo de lotes afectados — nunca una fila por lote.
        if (movimientosGenerados.Count > 0)
        {
            var (anteriorConteo, nuevoConteo) = PayloadDeAuditoria.Conteo(
                actualAgregado, cantidadAgregadaFinal, movimientosGenerados, movimientosGenerados.Count, deltaAgregado);
            await auditoria.RegistrarAsync(
                conexion, transaccionCruda,
                new RegistroDeAuditoria(idTenant, idPuntoVenta, AccionAuditada.StockConteo, idArticulo, anteriorConteo, nuevoConteo),
                ct);
        }

        await transaccion.CommitAsync(ct);

        return new ResultadoConteo(
            idPuntoVenta, idArticulo, cantidadAgregadaFinal, actualAgregado, deltaAgregado,
            MovimientoRegistrado: deltaAgregado != 0m, resultadosPorLote);
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

    // ---- validaciones -------------------------------------------------------------------------

    private async Task<Articulo> ResolverArticuloAsync(int idArticulo, CancellationToken ct) =>
        await db.Articulos.FirstOrDefaultAsync(a => a.Id == idArticulo, ct)
            // Mismo código que ServicioDeOfertas/ServicioDeVentas (referencia_invalida, 400): el
            // filtro de EF (+ RLS) ya deja invisible un artículo de otro tenant, así que "no
            // existe" y "es de otro tenant" caen en la misma rama.
            ?? throw new ErrorDominio("referencia_invalida", $"No existe el artículo {idArticulo}.", 400);

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — mismo criterio que
            // ServicioDeVentas.ResolverPuntoVentaAsync.
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private static decimal ExigirCantidadValida(decimal cantidad)
    {
        if (cantidad == 0)
        {
            throw new ErrorDominio("cantidad_de_ajuste_invalida", "La cantidad del ajuste no puede ser cero.", 400);
        }

        // Máximo 3 decimales (mismo código y criterio que ServicioDeVentas.ExigirLineasValidas —
        // doc 10: cantidad soporta fracción para UnidadVenta.Peso, pero sin precisión ilimitada).
        if (decimal.Round(cantidad, 3, MidpointRounding.AwayFromZero) != cantidad)
        {
            throw new ErrorDominio("cantidad_invalida", "La cantidad del ajuste admite hasta 3 decimales.", 400);
        }

        return cantidad;
    }

    /// <summary>Etapa 12, slice 11 (design: Write site 3 — "cantidad arrives positive and is
    /// negated server-side"; spec lotes-y-vencimientos: "the client MUST send a positive
    /// cantidad"). A diferencia de <see cref="ExigirCantidadValida"/> (que solo prohíbe cero,
    /// porque un ajuste carga o descarga con signo), un decomiso SIEMPRE resta — cero o negativo
    /// del cliente es un error de contrato, no una operación legítima. Reusa el mismo código de
    /// familia que <see cref="ExigirCantidadValida"/> (decomiso es estructuralmente un ajuste,
    /// design decisión 9 del proposal), sin un código nuevo en la lista de la etapa.</summary>
    private static decimal ExigirCantidadDeDecomisoValida(decimal cantidad)
    {
        if (cantidad <= 0)
        {
            throw new ErrorDominio("cantidad_de_ajuste_invalida", "La cantidad del decomiso tiene que ser mayor a cero.", 400);
        }

        if (decimal.Round(cantidad, 3, MidpointRounding.AwayFromZero) != cantidad)
        {
            throw new ErrorDominio("cantidad_invalida", "La cantidad del decomiso admite hasta 3 decimales.", 400);
        }

        return cantidad;
    }

    private static string ExigirObservaciones(string? observaciones)
    {
        var limpio = observaciones?.Trim();

        if (string.IsNullOrEmpty(limpio))
        {
            throw new ErrorDominio(
                "observaciones_requeridas", "El ajuste manual de stock requiere una observación/motivo.", 400);
        }

        return limpio;
    }

    /// <summary>Validación puramente en memoria, antes de resolver referencias o tocar la base.
    /// Etapa 12, slice 10: el rechazo de artículo repetido (<c>articulo_repetido</c>) se MUDÓ
    /// de acá a <see cref="ResolverLineasDeTransferenciaAsync"/> — la clave se ensanchó a
    /// <c>(IdArticulo, IdLote)</c> y decisión 11 exige evaluarla DESPUÉS del defaulting de FEFO,
    /// que solo corre una vez resueltos el punto de venta y los artículos.</summary>
    private static IReadOnlyList<LineaDeTransferencia> ExigirLineasDeTransferenciaValidas(
        IReadOnlyList<LineaDeTransferencia> lineas)
    {
        if (lineas is null || lineas.Count == 0)
        {
            throw new ErrorDominio(
                "transferencia_sin_lineas", "La transferencia no tiene líneas para procesar.", 400);
        }

        foreach (var linea in lineas)
        {
            if (linea.Cantidad <= 0)
            {
                throw new ErrorDominio(
                    "cantidad_de_transferencia_invalida", "La cantidad a transferir tiene que ser mayor a cero.", 400);
            }

            if (decimal.Round(linea.Cantidad, 3, MidpointRounding.AwayFromZero) != linea.Cantidad)
            {
                throw new ErrorDominio(
                    "cantidad_de_transferencia_invalida", "La cantidad a transferir admite hasta 3 decimales.", 400);
            }
        }

        return lineas;
    }

    /// <summary>Design: New Domain codes — <c>contada_invalida</c>. <see cref="SolicitudDeConteo.Contada"/>
    /// es el total físicamente contado: nunca negativo, hasta 3 decimales (mismo listón que
    /// <see cref="ExigirCantidadValida"/>, sin el chequeo de "distinto de cero" — un conteo que
    /// confirma el cero actual es un no-op legítimo, spec: Zero-Difference Conteo).</summary>
    private static decimal ExigirContadaValida(decimal contada)
    {
        if (contada < 0)
        {
            throw new ErrorDominio("contada_invalida", "La cantidad contada no puede ser negativa.", 400);
        }

        if (decimal.Round(contada, 3, MidpointRounding.AwayFromZero) != contada)
        {
            throw new ErrorDominio("contada_invalida", "La cantidad contada admite hasta 3 decimales.", 400);
        }

        return contada;
    }

    /// <summary>Etapa 12, slice 12 (design decisión 18 — exactly-one-of; dto-contract-honesty):
    /// validación puramente en memoria, ANTES de resolver referencias — un request mal formado no
    /// amerita ni un SELECT. Un <see cref="SolicitudDeConteo.Lotes"/> vacío (<c>[]</c>) cuenta como
    /// "ausente", mismo criterio que <c>null</c>: ninguna de las dos formas del conteo trajo un
    /// valor accionable.</summary>
    private static void ExigirExactamenteUnaFormaDeConteo(decimal? contada, IReadOnlyList<ConteoDeLote>? lotes)
    {
        var tieneContada = contada is not null;
        var tieneLotes = lotes is { Count: > 0 };

        if (tieneContada == tieneLotes)
        {
            throw new ErrorDominio(
                "conteo_contada_y_lotes",
                "El conteo tiene que traer exactamente uno de cantidad contada o desglose por lote, nunca ambos ni ninguno.",
                400);
        }
    }

    /// <summary>Etapa 12, slice 12, judgment-day fix (juez B, FIX 1): la forma del conteo tiene
    /// que coincidir con el control efectivo de lote del artículo — chequeado ANTES de cualquier
    /// lock, mismo criterio "en memoria primero" que <see cref="ExigirLineasDeTransferenciaValidas"/>.
    /// Un artículo lote-efectivo no admite <c>Contada</c> agregada (rompería el invariante 3 en
    /// silencio); uno SIN lote efectivo no admite <c>Lotes</c> (no tiene destino, mismo criterio
    /// que <c>lote_no_aplica</c> en <see cref="ResolverIdLoteEfectivoAsync"/>). El <c>409
    /// conteo_lote_no_soportado</c> documentado en <see cref="ContarAsync"/> es la degradación
    /// pre-aprobada para "el per-lot conteo no está implementado" — no aplica acá, donde SÍ está
    /// implementado; el rechazo honesto de un total agregado contra un artículo lote-efectivo es
    /// <c>400 conteo_requiere_lotes</c>.</summary>
    private static void ExigirFormaDeConteoCoincideConControlDeLote(
        bool esLoteEfectivo, decimal? contada, IReadOnlyList<ConteoDeLote>? lotes, int idArticulo)
    {
        if (esLoteEfectivo && contada is not null)
        {
            throw new ErrorDominio(
                "conteo_requiere_lotes",
                $"El artículo {idArticulo} es lote-efectivo; el conteo requiere desglose por lote, no un total agregado.",
                400);
        }

        if (!esLoteEfectivo && lotes is { Count: > 0 })
        {
            throw new ErrorDominio(
                "conteo_no_aplica_lotes",
                $"El artículo {idArticulo} no tiene lote efectivo; el conteo no admite desglose por lote.",
                400);
        }
    }

    /// <summary>Etapa 12, slice 12, judgment-day fix (juez B, FIX 2): SELECT-first contra
    /// <see cref="ServicioDeLotes.LeerSaldosAsync"/> — mismo criterio que
    /// <see cref="ResolverIdLoteEfectivoAsync"/>, ANTES de la transacción. Sin esto, un
    /// <c>idLote</c> inexistente/ajeno solo se descubre dentro de la FK cruda del upsert no-op de
    /// <see cref="EscritorDeStock.BloquearYCrearSiFaltaDeLoteAsync"/> — un 500, nunca un 400.</summary>
    private async Task ExigirLotesDeConteoExistenAsync(
        int idPuntoVenta, int idArticulo, IReadOnlyList<ConteoDeLote> lotes, CancellationToken ct)
    {
        var idsLotePedidos = lotes.Select(l => l.IdLote).Distinct().ToList();
        var saldos = await servicioDeLotes.LeerSaldosAsync(idPuntoVenta, [idArticulo], idsLotePedidos, ct);
        var idsLoteValidos = saldos.Select(s => s.IdLote).ToHashSet();

        foreach (var idLote in idsLotePedidos)
        {
            if (!idsLoteValidos.Contains(idLote))
            {
                throw new ErrorDominio(
                    "lote_invalido",
                    $"El lote {idLote} no existe, no pertenece al artículo {idArticulo} o fue eliminado.",
                    400);
            }
        }
    }

    /// <summary>Etapa 12, slice 12 (design decisión 12): <c>conteo_lote_repetido</c> se rechaza
    /// ANTES de cualquier lock — mismo criterio "en memoria primero" que <see cref="ExigirLineasDeTransferenciaValidas"/>.
    /// Reusa <see cref="ExigirContadaValida"/> por línea: el total contado de un lote es la MISMA
    /// magnitud física que el total agregado un nivel arriba, misma disciplina de signo/precisión.</summary>
    private static IReadOnlyList<ConteoDeLote> ExigirLotesDeConteoValidos(IReadOnlyList<ConteoDeLote> lotes)
    {
        foreach (var lote in lotes)
        {
            ExigirContadaValida(lote.Contada);
        }

        var repetido = lotes.GroupBy(l => l.IdLote).FirstOrDefault(g => g.Count() > 1);
        if (repetido is not null)
        {
            throw new ErrorDominio(
                "conteo_lote_repetido",
                $"El lote {repetido.Key} aparece más de una vez en el desglose del conteo.",
                400);
        }

        return lotes;
    }

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // GestionDeCatalogo (capa de API) ya exige un actor de tenant admin — un actor de
            // plataforma nunca llega hasta acá. Defensa en profundidad, no un camino alcanzable
            // en operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDeStock requiere un actor de tenant; GestionDeCatalogo no admite plataforma.");
}
