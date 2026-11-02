using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Application.Caja;
using Ways.Domain.Auditoria;
using Ways.Domain.Caja;
using Ways.Domain.Common;
using Ways.Domain.MercadoPago;

namespace Ways.Application.MercadoPago;

/// <summary>
/// Las propuestas de tesorería de una conciliación (E7): la diferencia de comisión, los retiros y las reversiones de retiros cancelados que la importación
/// dejó pendientes (<see cref="GeneradorDePropuestasDeTesoreriaMercadoPago"/>). Aplicar escribe en la tesorería; descartar y reabrir no escriben nada. Solo el
/// Admin aplica, descarta o reabre. Descartar no es irreversible: <see cref="ReabrirAsync"/> devuelve una descartada a pendiente (el único camino de vuelta para
/// un retiro, que la importación no vuelve a proponer).
///
/// <b>Aplicar.</b> Orden de los errores: 404 (conciliación, propuesta de esa conciliación), 400 por el cuerpo según el tipo (<c>cuenta_destino_requerida</c>
/// en un retiro, <c>cuenta_destino_no_aplica</c> en una diferencia; <c>cuenta_origen_requerida</c> en un retiro sin origen fijo,
/// <c>cuenta_origen_no_aplica</c> si la propuesta ya tiene origen o es una diferencia), y bajo los locks 409 <c>propuesta_ya_aplicada</c> /
/// <c>propuesta_descartada</c>, 409 <c>cuenta_origen_no_disponible</c> (el origen fijo ya no existe), 400 <c>cuenta_origen_invalida</c> (la elegida no es una cuenta
/// activa de los medios de Mercado Pago de la empresa), 400 <c>cuenta_destino_invalida</c> (no existe, es de otra empresa, está dada de baja o inactiva) y
/// <c>cuentas_iguales</c>, y 409 <c>periodo_conciliado</c> si la fecha cae en un período congelado de tesorería.
///
/// <b>Cómo se asienta.</b> La tesorería no tiene una transferencia entre cuentas: un retiro es un ajuste de salida de la cuenta de Mercado Pago y un ajuste
/// de entrada a la cuenta elegida (la reversión de un retiro cancelado hace lo inverso entre las dos cuentas del retiro original, que ya vienen fijas y no se
/// eligen: <c>cuenta_destino_no_aplica</c> / <c>cuenta_origen_no_aplica</c> si el cuerpo las trae), los dos con <see cref="EscriturasDeCuentaDeTesoreria.AgregarAjuste"/> (el escritor del ajuste manual) dentro de UNA
/// transacción, que es lo que los hace atómicos; la diferencia de comisión es un solo ajuste en la cuenta del medio, con el signo del cálculo. La propuesta
/// recuerda las filas escritas.
///
/// <b>Sin reintento</b> (<c>ef-retry-safe-writes</c>, forma (b)): un reintento tras un commit ambiguo escribiría dos veces en el libro.
///
/// <b>Locks</b>, en este orden: guarda de conciliación de tesorería (compartida; la primera de la transacción, como en el ajuste manual) → fila de la
/// propuesta (<c>FOR UPDATE</c>) → cuentas de la empresa (<c>FOR NO KEY UPDATE</c>, por id ascendente, el mismo lock con el que el cierre y el ajuste manual
/// serializan la cadena de una cuenta). Descartar y reabrir toman solo la fila de la propuesta. La propuesta se lee UNA vez, después de su lock: dos aplicaciones simultáneas se serializan en ese lock y la segunda
/// ve la propuesta aplicada (409), sin escribir nada. Ningún otro escritor toma la fila de la propuesta junto con una cuenta en otro orden.
/// </summary>
public class ServicioDePropuestasDeTesoreriaMercadoPago(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeAuditoria auditoria)
{
    /// <summary>Las propuestas de una conciliación: primero las diferencias de comisión y después los retiros, en el orden de las líneas del archivo.</summary>
    public async Task<IReadOnlyList<PropuestaDeTesoreria>> ListarAsync(int idConciliacion, CancellationToken ct = default)
    {
        if (!await db.ConciliacionesMercadoPago.AnyAsync(c => c.Id == idConciliacion, ct))
        {
            throw Inexistente(idConciliacion);
        }

        var propuestas = await db.PropuestasTesoreriaMercadoPago
            .AsNoTracking()
            .Where(p => p.IdConciliacion == idConciliacion)
            .OrderBy(p => p.Tipo)
            .ThenBy(p => p.Id)
            .ToListAsync(ct);
        if (propuestas.Count == 0)
        {
            return [];
        }

        var idsDeCuenta = propuestas
            .SelectMany(p => new[] { p.IdCuentaOrigen, p.IdCuentaDestino })
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var cuentas = await db.CuentasTesoreria
            .AsNoTracking()
            .IgnoreQueryFilters(["BajaLogica"])
            .Where(c => idsDeCuenta.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Nombre, ct);

        var idsDeFila = propuestas.Where(p => p.IdFila is not null).Select(p => p.IdFila!.Value).ToList();
        var filas = await db.FilasConciliacionMercadoPago
            .AsNoTracking()
            .Where(f => idsDeFila.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);

        var pideOrigen = propuestas.Any(p => p.Tipo == TiposDePropuestaDeTesoreriaMercadoPago.Retiro
                                             && p.Estado == EstadosDePropuestaDeTesoreriaMercadoPago.Pendiente && p.IdCuentaOrigen is null);
        IReadOnlyList<CuentaDeOrigenPosible> origenes = pideOrigen
            ? await GeneradorDePropuestasDeTesoreriaMercadoPago.CuentasDeOrigenAsync(db, propuestas[0].IdEmpresa, ct)
            : [];

        var idsDeUsuario = propuestas.Where(p => p.IdUsuarioResolucion is not null).Select(p => p.IdUsuarioResolucion!.Value).Distinct().ToList();
        var usuarios = await db.Usuarios
            .AsNoTracking()
            .Where(u => idsDeUsuario.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.NombreUsuario, ct);

        return propuestas
            .Select(p =>
            {
                var fila = p.IdFila is { } idFila ? filas.GetValueOrDefault(idFila) : null;
                return new PropuestaDeTesoreria(
                    p.Id,
                    p.Tipo,
                    p.Estado,
                    p.Importe,
                    p.IdCuentaOrigen,
                    p.IdCuentaOrigen is { } o ? cuentas.GetValueOrDefault(o) : null,
                    p.IdCuentaDestino,
                    p.IdCuentaDestino is { } d ? cuentas.GetValueOrDefault(d) : null,
                    fila?.NumeroLinea,
                    fila?.SourceId,
                    fila?.FechaTransaccion,
                    GeneradorDePropuestasDeTesoreriaMercadoPago.LeerDetalle(p.Detalle),
                    p.IdMovimientoCuenta,
                    p.IdMovimientoCuentaDestino,
                    p.FechaResolucion,
                    p.IdUsuarioResolucion is { } u ? usuarios.GetValueOrDefault(u) : null,
                    pideOrigen && p.Tipo == TiposDePropuestaDeTesoreriaMercadoPago.Retiro && p.Estado == EstadosDePropuestaDeTesoreriaMercadoPago.Pendiente
                    && p.IdCuentaOrigen is null
                        ? origenes
                        : [],
                    p.MotivoResolucion);
            })
            .ToList();
    }

    public async Task<PropuestaDeTesoreria> AplicarAsync(
        int idConciliacion, int idPropuesta, SolicitudDeAplicacionDePropuesta? solicitud, CancellationToken ct = default)
    {
        var idTenant = contexto.IdTenant
            ?? throw ErrorDominio.Prohibido("Aplicar una propuesta requiere un usuario de un tenant.");
        var idEmpleado = contexto.UsuarioId;

        var previa = await LeerPreviaAsync(idConciliacion, idPropuesta, ct);

        var idDestino = solicitud?.IdCuentaDestino;
        var idOrigenElegido = solicitud?.IdCuentaOrigen;
        if (previa.Tipo == TiposDePropuestaDeTesoreriaMercadoPago.Retiro)
        {
            if (idDestino is null or < 1)
            {
                throw new ErrorDominio(
                    CodigosDeErrorDePropuestaDeTesoreria.CuentaDestinoRequerida, "Elegí la cuenta que recibe el retiro.", 400);
            }

            if (previa.IdCuentaOrigen is null && idOrigenElegido is null or < 1)
            {
                throw new ErrorDominio(
                    CodigosDeErrorDePropuestaDeTesoreria.CuentaOrigenRequerida,
                    "Elegí la cuenta de Mercado Pago de la que sale el retiro.",
                    400);
            }

            if (previa.IdCuentaOrigen is not null && idOrigenElegido is not null)
            {
                throw new ErrorDominio(
                    CodigosDeErrorDePropuestaDeTesoreria.CuentaOrigenNoAplica, "Este retiro ya tiene cuenta de origen: no se elige.", 400);
            }
        }
        else if (idDestino is not null)
        {
            throw new ErrorDominio(
                CodigosDeErrorDePropuestaDeTesoreria.CuentaDestinoNoAplica, "Esta propuesta no lleva cuenta de destino a elegir.", 400);
        }
        else if (idOrigenElegido is not null)
        {
            throw new ErrorDominio(
                CodigosDeErrorDePropuestaDeTesoreria.CuentaOrigenNoAplica, "Esta propuesta no lleva cuenta de origen a elegir.", 400);
        }

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await GuardaDeConciliacion.TomarCompartidoAsync(db, idTenant, previa.IdEmpresa, ct);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM propuestas_tesoreria_mercadopago WHERE id_propuesta_tesoreria_mercadopago = {idPropuesta} FOR UPDATE", ct);
            var propuesta = await db.PropuestasTesoreriaMercadoPago
                .FirstOrDefaultAsync(p => p.Id == idPropuesta && p.IdConciliacion == idConciliacion, ct)
                ?? throw Inexistente(idConciliacion, idPropuesta);
            ExigirPendiente(propuesta);

            var esRetiro = propuesta.Tipo == TiposDePropuestaDeTesoreriaMercadoPago.Retiro;
            var esReversion = propuesta.Tipo == TiposDePropuestaDeTesoreriaMercadoPago.RetiroCancelado;
            var mueveDosCuentas = esRetiro || esReversion;
            var eligioOrigen = propuesta.IdCuentaOrigen is null;
            var idOrigen = propuesta.IdCuentaOrigen
                ?? (idOrigenElegido is > 0
                    ? idOrigenElegido.Value
                    : throw new ErrorDominio(
                        CodigosDeErrorDePropuestaDeTesoreria.CuentaOrigenRequerida,
                        "Elegí la cuenta de Mercado Pago de la que sale el retiro.",
                        400));

            var idDestinoFinal = esReversion ? propuesta.IdCuentaDestino : idDestino;
            if (esRetiro && idDestino == idOrigen)
            {
                throw new ErrorDominio(
                    CodigosDeErrorDePropuestaDeTesoreria.CuentasIguales, "La cuenta de destino es la misma de la que sale el retiro.", 400);
            }

            var idsABloquear = mueveDosCuentas ? new List<int> { idOrigen, idDestinoFinal!.Value }.Order().ToList() : [idOrigen];
            var conexion = db.Database.GetDbConnection();
            var transaccionCruda = transaccion.GetDbTransaction();
            var bloqueadas = await EscriturasDeCuentaDeTesoreria.LeerYBloquearCuentasAsync(
                conexion, transaccionCruda, idTenant, propuesta.IdEmpresa, idsABloquear, ct);

            if (eligioOrigen)
            {
                var posibles = await GeneradorDePropuestasDeTesoreriaMercadoPago.CuentasDeOrigenAsync(db, propuesta.IdEmpresa, ct);
                if (!bloqueadas.ContainsKey(idOrigen) || posibles.All(c => c.Id != idOrigen))
                {
                    throw new ErrorDominio(
                        CodigosDeErrorDePropuestaDeTesoreria.CuentaOrigenInvalida,
                        "La cuenta de origen tiene que ser una cuenta activa de la empresa a la que acreditan sus medios de pago de Mercado Pago.",
                        400);
                }
            }
            else if (!bloqueadas.ContainsKey(idOrigen) || (esReversion && !bloqueadas.ContainsKey(idDestinoFinal!.Value)))
            {
                throw ErrorDominio.Conflicto(
                    CodigosDeErrorDePropuestaDeTesoreria.CuentaOrigenNoDisponible,
                    esReversion ? "Una de las cuentas del retiro cancelado ya no está disponible." : "La cuenta de Mercado Pago ya no está disponible.");
            }

            string? nombreDestino = null;
            if (esRetiro)
            {
                var destino = bloqueadas.ContainsKey(idDestino!.Value)
                    ? await db.CuentasTesoreria.AsNoTracking()
                        .Where(c => c.Id == idDestino.Value)
                        .Select(c => new { c.Activo, c.Nombre })
                        .FirstOrDefaultAsync(ct)
                    : null;
                if (destino is not { Activo: true })
                {
                    throw new ErrorDominio(
                        CodigosDeErrorDePropuestaDeTesoreria.CuentaDestinoInvalida,
                        "La cuenta de destino tiene que ser una cuenta activa de la misma empresa.",
                        400);
                }

                nombreDestino = destino.Nombre;
            }

            var ahora = reloj.Ahora;
            var colaOrigen = await EscriturasDeCuentaDeTesoreria.LeerColaAsync(db, idTenant, idOrigen, bloqueadas[idOrigen].SaldoInicial, ct);
            var importeOrigen = esRetiro ? -propuesta.Importe : propuesta.Importe;
            var ajusteOrigen = ReglasDeAjusteDeCuentaTesoreria.Resolver(colaOrigen.Saldo, importeOrigen, null);
            var fechaOrigen = EscriturasDeCuentaDeTesoreria.FechaMonotona(ahora, colaOrigen.Fecha);
            await GuardaDeConciliacion.ExigirNoConciliadoAsync(db, idTenant, propuesta.IdEmpresa, fechaOrigen, ct);

            var concepto = esRetiro
                ? $"Retiro de Mercado Pago a {nombreDestino} - conciliación {idConciliacion}, operación {propuesta.ClaveOrigen}"
                : esReversion
                    ? $"Reversión del retiro cancelado en Mercado Pago - conciliación {idConciliacion}, operación {propuesta.ClaveOrigen}"
                    : $"Diferencia de comisión de Mercado Pago - conciliación {idConciliacion}";
            var filaOrigen = EscriturasDeCuentaDeTesoreria.AgregarAjuste(db, idTenant, idOrigen, ajusteOrigen, fechaOrigen, concepto, idEmpleado);

            MovimientoCuenta? filaDestino = null;
            if (mueveDosCuentas)
            {
                var idCuentaDestino = idDestinoFinal!.Value;
                var colaDestino = await EscriturasDeCuentaDeTesoreria.LeerColaAsync(
                    db, idTenant, idCuentaDestino, bloqueadas[idCuentaDestino].SaldoInicial, ct);
                var ajusteDestino = ReglasDeAjusteDeCuentaTesoreria.Resolver(
                    colaDestino.Saldo, esReversion ? -propuesta.Importe : propuesta.Importe, null);
                var fechaDestino = EscriturasDeCuentaDeTesoreria.FechaMonotona(ahora, colaDestino.Fecha);
                await GuardaDeConciliacion.ExigirNoConciliadoAsync(db, idTenant, propuesta.IdEmpresa, fechaDestino, ct);
                filaDestino = EscriturasDeCuentaDeTesoreria.AgregarAjuste(
                    db, idTenant, idCuentaDestino, ajusteDestino, fechaDestino,
                    esReversion
                        ? $"Reversión del retiro cancelado en Mercado Pago - conciliación {idConciliacion}, operación {propuesta.ClaveOrigen}"
                        : $"Retiro desde Mercado Pago - conciliación {idConciliacion}, operación {propuesta.ClaveOrigen}",
                    idEmpleado);
            }

            await db.SaveChangesAsync(ct);

            propuesta.Estado = EstadosDePropuestaDeTesoreriaMercadoPago.Aplicada;
            propuesta.IdCuentaOrigen = idOrigen;
            propuesta.IdCuentaDestino = mueveDosCuentas ? idDestinoFinal : null;
            propuesta.IdMovimientoCuenta = filaOrigen.Id;
            propuesta.IdMovimientoCuentaDestino = filaDestino?.Id;
            propuesta.IdUsuarioResolucion = idEmpleado;
            propuesta.FechaResolucion = ahora;
            propuesta.UpdatedAt = ahora;

            auditoria.Registrar(new RegistroDeAuditoria(
                idTenant,
                null,
                AccionAuditada.PropuestaDeTesoreriaAplicacion,
                propuesta.Id,
                valorAnterior: null,
                valorNuevo: new Dictionary<string, object?>
                {
                    ["id_conciliacion"] = idConciliacion,
                    ["tipo"] = propuesta.Tipo,
                    ["importe"] = propuesta.Importe,
                    ["id_cuenta_origen"] = idOrigen,
                    ["id_cuenta_destino"] = propuesta.IdCuentaDestino,
                    ["id_movimiento_cuenta"] = filaOrigen.Id,
                    ["id_movimiento_cuenta_destino"] = filaDestino?.Id,
                    ["estado"] = propuesta.Estado
                }));
            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
        });

        return await LeerUnaAsync(idConciliacion, idPropuesta, ct);
    }

    public async Task<PropuestaDeTesoreria> DescartarAsync(int idConciliacion, int idPropuesta, CancellationToken ct = default)
    {
        var idTenant = contexto.IdTenant
            ?? throw ErrorDominio.Prohibido("Descartar una propuesta requiere un usuario de un tenant.");

        await LeerPreviaAsync(idConciliacion, idPropuesta, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM propuestas_tesoreria_mercadopago WHERE id_propuesta_tesoreria_mercadopago = {idPropuesta} FOR UPDATE", ct);
            var propuesta = await db.PropuestasTesoreriaMercadoPago
                .FirstOrDefaultAsync(p => p.Id == idPropuesta && p.IdConciliacion == idConciliacion, ct)
                ?? throw Inexistente(idConciliacion, idPropuesta);

            if (propuesta.Estado == EstadosDePropuestaDeTesoreriaMercadoPago.Descartada)
            {
                throw ErrorDominio.Conflicto(CodigosDeErrorDePropuestaDeTesoreria.YaDescartada, "La propuesta ya se descartó.");
            }

            ExigirPendiente(propuesta);

            var ahora = reloj.Ahora;
            propuesta.Estado = EstadosDePropuestaDeTesoreriaMercadoPago.Descartada;
            propuesta.IdUsuarioResolucion = contexto.UsuarioId;
            propuesta.FechaResolucion = ahora;
            propuesta.UpdatedAt = ahora;

            auditoria.Registrar(new RegistroDeAuditoria(
                idTenant,
                null,
                AccionAuditada.PropuestaDeTesoreriaDescarte,
                propuesta.Id,
                valorAnterior: null,
                valorNuevo: new Dictionary<string, object?>
                {
                    ["id_conciliacion"] = idConciliacion,
                    ["tipo"] = propuesta.Tipo,
                    ["importe"] = propuesta.Importe,
                    ["estado"] = propuesta.Estado
                }));
            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
        });

        return await LeerUnaAsync(idConciliacion, idPropuesta, ct);
    }

    /// <summary>Devuelve una propuesta descartada a pendiente: borra quién y cuándo la resolvió y el motivo, sin escribir nada en la tesorería. Es el único
    /// camino de vuelta de un retiro descartado, que la importación no vuelve a proponer. El estado se lee UNA vez, bajo el lock de la fila, así que reabrir
    /// contra aplicar o descartar se serializa; 409 <c>propuesta_no_descartada</c> si no está descartada. Una propuesta que el reporte descartó por una cancelación
    /// también se puede reabrir: queda en manos del Admin.</summary>
    public async Task<PropuestaDeTesoreria> ReabrirAsync(int idConciliacion, int idPropuesta, CancellationToken ct = default)
    {
        var idTenant = contexto.IdTenant
            ?? throw ErrorDominio.Prohibido("Reabrir una propuesta requiere un usuario de un tenant.");

        await LeerPreviaAsync(idConciliacion, idPropuesta, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM propuestas_tesoreria_mercadopago WHERE id_propuesta_tesoreria_mercadopago = {idPropuesta} FOR UPDATE", ct);
            var propuesta = await db.PropuestasTesoreriaMercadoPago
                .FirstOrDefaultAsync(p => p.Id == idPropuesta && p.IdConciliacion == idConciliacion, ct)
                ?? throw Inexistente(idConciliacion, idPropuesta);

            if (propuesta.Estado != EstadosDePropuestaDeTesoreriaMercadoPago.Descartada)
            {
                throw ErrorDominio.Conflicto(
                    CodigosDeErrorDePropuestaDeTesoreria.NoDescartada, "Solo se puede reabrir una propuesta descartada.");
            }

            var ahora = reloj.Ahora;
            var motivoAnterior = propuesta.MotivoResolucion;
            propuesta.Estado = EstadosDePropuestaDeTesoreriaMercadoPago.Pendiente;
            propuesta.IdUsuarioResolucion = null;
            propuesta.FechaResolucion = null;
            propuesta.MotivoResolucion = null;
            propuesta.UpdatedAt = ahora;

            auditoria.Registrar(new RegistroDeAuditoria(
                idTenant,
                null,
                AccionAuditada.PropuestaDeTesoreriaReapertura,
                propuesta.Id,
                valorAnterior: new Dictionary<string, object?>
                {
                    ["estado"] = EstadosDePropuestaDeTesoreriaMercadoPago.Descartada,
                    ["motivo"] = motivoAnterior
                },
                valorNuevo: new Dictionary<string, object?>
                {
                    ["id_conciliacion"] = idConciliacion,
                    ["tipo"] = propuesta.Tipo,
                    ["importe"] = propuesta.Importe,
                    ["estado"] = propuesta.Estado,
                    ["motivo"] = propuesta.MotivoResolucion
                }));
            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
        });

        return await LeerUnaAsync(idConciliacion, idPropuesta, ct);
    }

    private async Task<PropuestaDeTesoreria> LeerUnaAsync(int idConciliacion, int idPropuesta, CancellationToken ct) =>
        (await ListarAsync(idConciliacion, ct)).Single(p => p.Id == idPropuesta);

    /// <summary>El 404 de antes de validar el cuerpo ni tomar locks: la propuesta tiene que ser de la conciliación. La empresa no cambia, así que leerla
    /// antes del lock es seguro; el estado se vuelve a leer bajo el lock.</summary>
    private async Task<(int IdEmpresa, string Tipo, int? IdCuentaOrigen)> LeerPreviaAsync(int idConciliacion, int idPropuesta, CancellationToken ct)
    {
        var previa = await db.PropuestasTesoreriaMercadoPago
            .AsNoTracking()
            .Where(p => p.Id == idPropuesta && p.IdConciliacion == idConciliacion)
            .Select(p => new { p.IdEmpresa, p.Tipo, p.IdCuentaOrigen })
            .FirstOrDefaultAsync(ct);
        if (previa is null)
        {
            throw await db.ConciliacionesMercadoPago.AnyAsync(c => c.Id == idConciliacion, ct)
                ? Inexistente(idConciliacion, idPropuesta)
                : Inexistente(idConciliacion);
        }

        return (previa.IdEmpresa, previa.Tipo, previa.IdCuentaOrigen);
    }

    private static void ExigirPendiente(PropuestaDeTesoreriaMercadoPago propuesta)
    {
        if (propuesta.Estado == EstadosDePropuestaDeTesoreriaMercadoPago.Aplicada)
        {
            throw ErrorDominio.Conflicto(CodigosDeErrorDePropuestaDeTesoreria.YaAplicada, "La propuesta ya se aplicó.");
        }

        if (propuesta.Estado == EstadosDePropuestaDeTesoreriaMercadoPago.Descartada)
        {
            throw ErrorDominio.Conflicto(CodigosDeErrorDePropuestaDeTesoreria.Descartada, "La propuesta se descartó: ya no se puede aplicar.");
        }
    }

    private static ErrorDominio Inexistente(int idConciliacion) =>
        ErrorDominio.NoEncontrado($"No existe la conciliación {idConciliacion}.");

    private static ErrorDominio Inexistente(int idConciliacion, int idPropuesta) =>
        ErrorDominio.NoEncontrado($"No existe la propuesta {idPropuesta} en la conciliación {idConciliacion}.");
}
