using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Auditoria;
using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.MercadoPago;
using Ways.Domain.Ventas;

namespace Ways.Application.MercadoPago;

/// <summary>
/// Conciliación con el reporte de transacciones de Mercado Pago (E6): el Admin sube el CSV, Ways lo lee por nombre de columna
/// (<see cref="LectorDeReporteMercadoPago"/>), cotea cada fila con los cobros de la empresa (<see cref="ConciliadorMercadoPago"/>) y guarda
/// la cabecera, cada fila con su resultado y, en los cobros conciliados, la comisión, el neto y la liberación reales.
///
/// <b>Importar.</b> Orden de los errores: 403 sin tenant, 400 sin archivo, 413 más de <see cref="TamanoMaximoDelArchivo"/>, 404 empresa,
/// 400 reporte ilegible (<c>conciliacion_reporte_invalido</c>), 409 mismo archivo ya importado en la empresa. Una fila que no se puede
/// leer no rechaza el archivo: queda guardada como <c>invalida</c> con su línea y su motivo. Todo ocurre en una transacción con el lock
/// de la empresa (<see cref="ClaveDeLockDeConciliacion"/>): dos subidas del mismo archivo no se cruzan, y el índice único de la huella
/// respalda el 409. Los cobros que alguna fila nombra se leen UNA vez, dentro de la transacción y después del lock de la empresa, con
/// <c>FOR UPDATE</c> en orden ascendente de id: ni un webhook ni otra escritura del cobro se cuela entre la lectura y la comisión real que
/// se escribe. Orden de locks: lock de la empresa, después las filas de los cobros; el servicio de cobros toma sus advisory (punto de venta,
/// cobro) y después la fila, y la importación no toma ningún advisory de cobro, así que un webhook solo espera por la fila y no hay ciclo.
/// Las filas SETTLEMENT conciliadas de un mismo cobro se suman: la comisión real es la suma de los valores absolutos de <c>FEE_AMOUNT</c>
/// (siempre positiva), el neto real la suma de <c>SETTLEMENT_NET_AMOUNT</c> tal como vienen y la liberación la más tardía. Una fila
/// idéntica a otra del archivo es <c>duplicada</c> (no se suma) y un cobro que un archivo anterior ya conciliaba deja la fila en
/// <c>ya_conciliado</c> (no se aplica; el detalle muestra lo guardado). Los totales del resumen no cuentan esas dos clases de fila.
///
/// Al final de la misma transacción nacen las propuestas de tesorería de la conciliación (<see cref="GeneradorDePropuestasDeTesoreriaMercadoPago"/>, E7).
///
/// <b>Sin reintento</b> (<c>ef-retry-safe-writes</c>, forma (b)): una importación no es idempotente más que por la huella, y un
/// reintento tras un commit ambiguo la volvería a insertar.
///
/// <b>Confirmar.</b> Une una fila <c>sin_cobro_en_ways</c> con un pago de venta <c>sin_verificar</c> de la misma empresa: el pago pasa a
/// <c>verificado_manualmente</c> y la fila lo recuerda. El pago tiene que ser de la empresa de la fila; los candidatos del listado
/// (importe, fecha y caja) son una sugerencia: confirmar exige que la venta no esté anulada (409 <c>venta_anulada</c>) y que el importe del pago
/// sea el de la fila (409 <c>importe_distinto</c>); la fecha y la caja las decide el Admin. Toma los locks fila → pago (<c>FOR UPDATE</c>) y lee cada uno una sola
/// vez después; un pago se confirma contra a lo sumo una fila (<c>ux_filas_conciliacion_mercadopago_cobro</c>).
/// </summary>
public class ServicioDeConciliacionMercadoPago(
    IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeAuditoria auditoria)
{
    /// <summary>Segunda clave del <c>pg_advisory_xact_lock(int, int)</c> por empresa; la primera es el id de la empresa.</summary>
    public const int ClaveDeLockDeConciliacion = 0x4D504353;

    public const int TamanoMaximoDelArchivo = 10 * 1024 * 1024;

    /// <summary>Más filas ilegibles que esto rechazan el archivo: casi seguro no es un reporte de transacciones.</summary>
    public const int MaximoDeInvalidas = LectorDeReporteMercadoPago.MaximoDeInvalidas;

    public const int MaximoDeInvalidasDevueltas = 200;

    public const int TamanoPorDefecto = 100;

    public const int TamanoMaximo = 500;

    private const int ConciliacionesListadas = 100;

    private static readonly List<string> OrdenDeResultados = [.. ResultadosDeConciliacionMercadoPago.Todos];

    public async Task<ConciliacionImportada> ImportarAsync(
        int idEmpresa, string? nombreArchivo, byte[] contenido, CancellationToken ct = default)
    {
        var idTenant = contexto.IdTenant
            ?? throw ErrorDominio.Prohibido("Importar un reporte requiere un usuario de un tenant.");

        if (contenido.Length == 0)
        {
            throw new ErrorDominio(CodigosDeErrorDeConciliacion.ArchivoRequerido, "Subí el reporte de transacciones en el campo archivo.", 400);
        }

        if (contenido.Length > TamanoMaximoDelArchivo)
        {
            throw new ErrorDominio(
                CodigosDeErrorDeConciliacion.ArchivoDemasiadoGrande,
                $"El archivo supera los {TamanoMaximoDelArchivo / (1024 * 1024)} MB.",
                413);
        }

        await ExigirEmpresaAsync(idEmpresa, ct);

        var lectura = LectorDeReporteMercadoPago.Leer(contenido);
        if (lectura.ErrorDeArchivo is { } error)
        {
            throw ReporteInvalido(error);
        }

        if (lectura.Filas.Count == 0)
        {
            throw ReporteInvalido(
                "El reporte no tiene ninguna fila válida" + (lectura.Invalidas.Count > 0 ? $": {lectura.Invalidas[0].Motivo}" : "."));
        }

        var huella = Convert.ToHexStringLower(SHA256.HashData(contenido));
        var nombre = LimpiarNombre(nombreArchivo);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);
            await ServicioDeCajasMercadoPago.TomarLockAsync(db, idEmpresa, ClaveDeLockDeConciliacion, ct);

            var resultado = await ImportarBajoLockAsync(idTenant, idEmpresa, nombre, huella, lectura, ct);
            await transaccion.CommitAsync(ct);
            return resultado;
        });
    }

    private async Task<ConciliacionImportada> ImportarBajoLockAsync(
        int idTenant, int idEmpresa, string? nombre, string huella, ResultadoDeLecturaDeReporteMercadoPago lectura, CancellationToken ct)
    {
        if (await db.ConciliacionesMercadoPago.IgnoreQueryFilters(["BajaLogica"]).AnyAsync(c => c.IdEmpresa == idEmpresa && c.HashArchivo == huella, ct))
        {
            throw YaImportada();
        }

        var cobros = await LeerCobrosAsync(idEmpresa, lectura.Filas, ct);
        var conciliador = new ConciliadorMercadoPago(cobros.Select(c => new CobroParaConciliar(
            c.Id, c.ExternalReference, c.ReferenciaPagoMercadoPago, c.IdPagoMercadoPago, c.Estado, c.IdComprobanteVenta,
            c.ImportePagado ?? c.Importe, c.IdFilaConciliacion is not null)));

        var ahora = reloj.Ahora;
        var cabecera = new ConciliacionMercadoPago
        {
            IdTenant = idTenant,
            IdEmpresa = idEmpresa,
            HashArchivo = huella,
            NombreArchivo = nombre,
            FechaDesde = lectura.Filas.Min(f => f.FechaTransaccion),
            FechaHasta = lectura.Filas.Max(f => f.FechaTransaccion),
            IdUsuario = contexto.UsuarioId,
            CantidadFilas = lectura.Filas.Count + lectura.Invalidas.Count,
            CantidadInvalidas = lectura.Invalidas.Count,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
        db.ConciliacionesMercadoPago.Add(cabecera);
        await db.SaveChangesAsync(ct);

        var filas = new List<(FilaConciliacionMercadoPago Fila, ClasificacionDeFilaMercadoPago? Clasificacion, FilaDeReporteMercadoPago? Origen)>(
            lectura.Filas.Count + lectura.Invalidas.Count);
        foreach (var origen in lectura.Filas)
        {
            var clasificacion = conciliador.Clasificar(origen);
            filas.Add((
                new FilaConciliacionMercadoPago
                {
                    IdTenant = idTenant,
                    IdEmpresa = idEmpresa,
                    IdConciliacion = cabecera.Id,
                    NumeroLinea = origen.NumeroLinea,
                    Resultado = clasificacion.Resultado,
                    SourceId = origen.SourceId,
                    ExternalReference = origen.ExternalReference,
                    TipoTransaccion = origen.TipoTransaccion,
                    Importe = origen.Importe,
                    Comision = origen.Comision,
                    Neto = origen.Neto,
                    FechaTransaccion = origen.FechaTransaccion,
                    FechaLiberacion = origen.FechaLiberacion,
                    ExternalPosId = origen.ExternalPosId,
                    IdCobroMercadoPago = clasificacion.IdCobroMercadoPago,
                    CreatedAt = ahora,
                    UpdatedAt = ahora
                },
                clasificacion,
                origen));
        }

        foreach (var invalida in lectura.Invalidas)
        {
            filas.Add((
                new FilaConciliacionMercadoPago
                {
                    IdTenant = idTenant,
                    IdEmpresa = idEmpresa,
                    IdConciliacion = cabecera.Id,
                    NumeroLinea = invalida.NumeroLinea,
                    Resultado = ResultadosDeConciliacionMercadoPago.Invalida,
                    Motivo = invalida.Motivo.Length <= 300 ? invalida.Motivo : invalida.Motivo[..300],
                    CreatedAt = ahora,
                    UpdatedAt = ahora
                },
                null,
                null));
        }

        filas = [.. filas.OrderBy(f => f.Fila.NumeroLinea)];
        db.FilasConciliacionMercadoPago.AddRange(filas.Select(f => f.Fila));
        await db.SaveChangesAsync(ct);

        var porId = cobros.ToDictionary(c => c.Id);
        var aplicados = filas
            .Where(f => f.Clasificacion is { AplicaAlCobro: true, IdCobroMercadoPago: not null })
            .GroupBy(f => f.Clasificacion!.IdCobroMercadoPago!.Value)
            .ToList();
        foreach (var grupo in aplicados)
        {
            var cobro = porId[grupo.Key];
            cobro.ComisionReal = grupo.Any(f => f.Origen!.Comision is not null) ? grupo.Sum(f => Math.Abs(f.Origen!.Comision ?? 0m)) : null;
            cobro.NetoReal = grupo.Any(f => f.Origen!.Neto is not null) ? grupo.Sum(f => f.Origen!.Neto ?? 0m) : null;
            cobro.FechaLiberacion = grupo.Max(f => f.Origen!.FechaLiberacion);
            cobro.IdFilaConciliacion = grupo.First().Fila.Id;
            cobro.UpdatedAt = ahora;
        }

        await db.SaveChangesAsync(ct);

        var propuestas = await GeneradorDePropuestasDeTesoreriaMercadoPago.GenerarAsync(
            db,
            idTenant,
            idEmpresa,
            cabecera.Id,
            aplicados.ToDictionary(g => g.Key, g => porId[g.Key].ComisionReal),
            filas.Select(f => f.Fila).Where(f => f.Resultado == ResultadosDeConciliacionMercadoPago.Retiro).ToList(),
            ahora,
            ct);

        var resumen = Resumir(filas.Select(f => f.Fila));
        var valorNuevo = new Dictionary<string, object?>
        {
            ["id_empresa"] = idEmpresa,
            ["huella_archivo"] = huella,
            ["cantidad_filas"] = cabecera.CantidadFilas,
            ["cantidad_invalidas"] = cabecera.CantidadInvalidas,
            ["cobros_conciliados"] = aplicados.Count,
            ["propuestas_diferencia_comision"] = propuestas.Diferencias,
            ["propuestas_retiro"] = propuestas.Retiros,
            ["cobros_sin_cierre"] = propuestas.CobrosSinCierre,
            ["cobros_sin_comision_real"] = propuestas.CobrosSinComisionReal
        };
        foreach (var r in resumen)
        {
            valorNuevo[$"filas_{r.Resultado}"] = r.Cantidad;
        }

        auditoria.Registrar(new RegistroDeAuditoria(
            idTenant, null, AccionAuditada.ConciliacionImportacion, cabecera.Id, valorAnterior: null, valorNuevo));
        await db.SaveChangesAsync(ct);

        var invalidas = lectura.Invalidas
            .Take(MaximoDeInvalidasDevueltas)
            .Select(i => new FilaInvalidaDeConciliacion(i.NumeroLinea, i.Motivo))
            .ToList();
        return new ConciliacionImportada(
            Mapear(cabecera, contexto.NombreUsuario, resumen), invalidas, lectura.Invalidas.Count > MaximoDeInvalidasDevueltas);
    }

    /// <summary>Los cobros de la empresa que alguna fila puede nombrar por referencia externa, id de pago o referencia numérica del pago,
    /// bloqueados con <c>FOR UPDATE</c> en orden ascendente de id: la ÚNICA lectura, dentro de la transacción y después del lock de la empresa.</summary>
    private async Task<List<CobroMercadoPago>> LeerCobrosAsync(
        int idEmpresa, IReadOnlyList<FilaDeReporteMercadoPago> filas, CancellationToken ct)
    {
        var cotejables = filas.Where(f => !TiposDeTransaccionMercadoPago.EsRetiro(f.TipoTransaccion)).ToList();
        var referencias = cotejables
            .Where(f => !string.IsNullOrEmpty(f.ExternalReference))
            .Select(f => f.ExternalReference!)
            .Distinct()
            .ToList();
        var ids = cotejables.Select(f => f.SourceId).Distinct().ToList();
        var numericos = ids
            .Select(id => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? (long?)n : null)
            .Where(n => n is not null)
            .Select(n => n!.Value)
            .Distinct()
            .ToList();

        var porReferencia = referencias.ToArray();
        var porId = ids.ToArray();
        var porNumero = numericos.ToArray();
        return await db.CobrosMercadoPago
            .FromSql($"""
                SELECT * FROM cobros_mercadopago
                WHERE id_empresa = {idEmpresa}
                    AND (external_reference = ANY({porReferencia})
                        OR id_pago_mercadopago = ANY({porId})
                        OR referencia_pago_mercadopago = ANY({porNumero}))
                ORDER BY id_cobro_mercadopago
                FOR UPDATE
                """)
            .ToListAsync(ct);
    }

    /// <summary>Las conciliaciones más recientes de la empresa (a lo sumo 100), de la más nueva a la más vieja, cada una con su resumen.</summary>
    public async Task<IReadOnlyList<ConciliacionListado>> ListarAsync(int idEmpresa, CancellationToken ct = default)
    {
        await ExigirEmpresaAsync(idEmpresa, ct);

        var conciliaciones = await db.ConciliacionesMercadoPago
            .AsNoTracking()
            .Where(c => c.IdEmpresa == idEmpresa)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(ConciliacionesListadas)
            .ToListAsync(ct);

        return await MapearAsync(conciliaciones, ct);
    }

    /// <summary>La conciliación con su resumen y una página de sus filas por número de línea, opcionalmente solo las de un resultado.
    /// 404 primero; después 400 por <paramref name="resultado"/> fuera de catálogo o paginación fuera de rango.</summary>
    public async Task<DetalleDeConciliacion> ObtenerAsync(
        int idConciliacion, string? resultado = null, int? pagina = null, int? tamano = null, CancellationToken ct = default)
    {
        var conciliacion = await db.ConciliacionesMercadoPago.AsNoTracking().FirstOrDefaultAsync(c => c.Id == idConciliacion, ct)
            ?? throw Inexistente(idConciliacion);

        if (resultado is not null && !ResultadosDeConciliacionMercadoPago.Todos.Contains(resultado))
        {
            throw new ErrorDominio(
                CodigosDeErrorDeConciliacion.ResultadoInvalido,
                $"El resultado tiene que ser uno de: {string.Join(", ", ResultadosDeConciliacionMercadoPago.Todos)}.",
                400);
        }

        var numeroDePagina = pagina ?? 1;
        var tamanoDePagina = tamano ?? TamanoPorDefecto;
        if (numeroDePagina < 1 || tamanoDePagina < 1 || tamanoDePagina > TamanoMaximo)
        {
            throw new ErrorDominio(
                CodigosDeErrorDeConciliacion.PaginacionInvalida,
                $"La página empieza en 1 y el tamaño va de 1 a {TamanoMaximo}.",
                400);
        }

        var consulta = db.FilasConciliacionMercadoPago.AsNoTracking().Where(f => f.IdConciliacion == idConciliacion);
        if (resultado is not null)
        {
            consulta = consulta.Where(f => f.Resultado == resultado);
        }

        var total = await consulta.CountAsync(ct);
        var filas = await consulta
            .OrderBy(f => f.NumeroLinea)
            .Skip((numeroDePagina - 1) * tamanoDePagina)
            .Take(tamanoDePagina)
            .ToListAsync(ct);

        var candidatos = await CandidatosDeLasFilasAsync(conciliacion.IdEmpresa, filas, ct);
        var guardados = await ValoresGuardadosAsync(filas, ct);
        var cabecera = (await MapearAsync([conciliacion], ct)).Single();

        return new DetalleDeConciliacion(
            cabecera,
            filas.Select(f => Mapear(f, candidatos.GetValueOrDefault(f.Id, []), guardados.GetValueOrDefault(f.Id))).ToList(),
            total,
            numeroDePagina,
            tamanoDePagina);
    }

    /// <summary>Para cada fila <c>ya_conciliado</c>, lo que el archivo anterior dejó guardado en el cobro, para compararlo con la fila.</summary>
    private async Task<Dictionary<int, (decimal? Comision, decimal? Neto)>> ValoresGuardadosAsync(
        IReadOnlyList<FilaConciliacionMercadoPago> filas, CancellationToken ct)
    {
        var ids = filas
            .Where(f => f.Resultado == ResultadosDeConciliacionMercadoPago.YaConciliado && f.IdCobroMercadoPago is not null)
            .Select(f => f.IdCobroMercadoPago!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var cobros = await db.CobrosMercadoPago
            .AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.ComisionReal, c.NetoReal })
            .ToDictionaryAsync(c => c.Id, ct);
        return filas
            .Where(f => f.Resultado == ResultadosDeConciliacionMercadoPago.YaConciliado && f.IdCobroMercadoPago is not null)
            .ToDictionary(f => f.Id, f => (cobros[f.IdCobroMercadoPago!.Value].ComisionReal, cobros[f.IdCobroMercadoPago!.Value].NetoReal));
    }

    /// <summary>Para cada fila <c>sin_cobro_en_ways</c> sin confirmar, los pagos de venta sin verificar de la empresa que podrían ser suyos
    /// (<see cref="CandidatosDeAprobacionManualMercadoPago"/>). Las ventas anuladas no son candidatas. Una sola lectura para toda la página.</summary>
    private async Task<Dictionary<int, IReadOnlyList<CandidatoDeConfirmacion>>> CandidatosDeLasFilasAsync(
        int idEmpresa, IReadOnlyList<FilaConciliacionMercadoPago> filas, CancellationToken ct)
    {
        var resultado = new Dictionary<int, IReadOnlyList<CandidatoDeConfirmacion>>();
        var sinCobro = filas
            .Where(f => f.Resultado == ResultadosDeConciliacionMercadoPago.SinCobroEnWays && f.IdCobro is null)
            .ToList();
        if (sinCobro.Count == 0)
        {
            return resultado;
        }

        var desde = sinCobro.Min(f => f.FechaTransaccion!.Value) - CandidatosDeAprobacionManualMercadoPago.Ventana;
        var hasta = sinCobro.Max(f => f.FechaTransaccion!.Value) + CandidatosDeAprobacionManualMercadoPago.Ventana;
        var importes = sinCobro.Select(f => f.Importe!.Value).Distinct().ToList();

        var pagos = await (
                from p in db.Cobros.AsNoTracking()
                where p.VerificacionMercadoPago == VerificacionDePagoMercadoPago.SinVerificar && p.DeletedAt == null
                    && importes.Contains(p.Importe)
                join c in db.ComprobantesVenta.AsNoTracking().SinEspejosFiscales() on p.IdComprobanteVenta equals c.Id
                join pv in db.PuntosVenta.IgnoreQueryFilters(["BajaLogica"]) on c.IdPuntoVenta equals pv.Id
                where pv.IdEmpresa == idEmpresa && c.Estado != EstadoComprobante.Anulado && c.Fecha >= desde && c.Fecha <= hasta
                select new
                {
                    p.Id,
                    IdComprobanteVenta = c.Id,
                    c.IdPuntoVenta,
                    c.Numero,
                    c.Fecha,
                    p.Importe,
                    p.Referencia,
                    ExternalIdDeLaCaja = db.CajasMercadoPago
                        .Where(k => k.IdPuntoVenta == c.IdPuntoVenta)
                        .OrderByDescending(k => k.Id)
                        .Select(k => k.ExternalId)
                        .FirstOrDefault()
                })
            .ToListAsync(ct);

        var paraConciliar = pagos
            .Select(p => new PagoSinVerificarParaConciliar(p.Id, p.IdComprobanteVenta, p.Fecha, p.Importe, p.ExternalIdDeLaCaja))
            .ToList();
        var detalle = pagos.ToDictionary(p => p.Id);

        foreach (var fila in sinCobro)
        {
            resultado[fila.Id] = CandidatosDeAprobacionManualMercadoPago
                .Buscar(fila.Importe!.Value, fila.FechaTransaccion!.Value, fila.ExternalPosId, paraConciliar)
                .Select(c =>
                {
                    var p = detalle[c.IdPago];
                    return new CandidatoDeConfirmacion(
                        p.Id, p.IdComprobanteVenta, NumeroDeComprobante.Formatear(p.IdPuntoVenta, p.Numero), p.Fecha, p.Importe, p.Referencia);
                })
                .ToList();
        }

        return resultado;
    }

    /// <summary>Confirma una fila <c>sin_cobro_en_ways</c> contra un pago de venta <c>sin_verificar</c>. 404 primero (conciliación, fila de esa
    /// conciliación, pago de la misma empresa); después 400 por el pago mal formado y 409 por una fila que no se confirma, ya confirmada, o un
    /// pago que no está sin verificar.</summary>
    public async Task<FilaDeConciliacion> ConfirmarAsync(
        int idConciliacion, int idFila, SolicitudDeConfirmacionDeFila solicitud, CancellationToken ct = default)
    {
        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM filas_conciliacion_mercadopago WHERE id_fila_conciliacion_mercadopago = {idFila} FOR UPDATE", ct);
            var fila = await db.FilasConciliacionMercadoPago
                .FirstOrDefaultAsync(f => f.Id == idFila && f.IdConciliacion == idConciliacion, ct)
                ?? throw Inexistente(idConciliacion, idFila);

            if (solicitud.IdCobro < 1)
            {
                throw new ErrorDominio(CodigosDeErrorDeConciliacion.PagoInvalido, "Indicá el pago de venta a confirmar.", 400);
            }

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM cobros WHERE id_cobro = {solicitud.IdCobro} FOR UPDATE", ct);
            var pago = await db.Cobros.FirstOrDefaultAsync(p => p.Id == solicitud.IdCobro && p.DeletedAt == null, ct);
            var venta = pago is null
                ? null
                : await (
                        from c in db.ComprobantesVenta.AsNoTracking().SinEspejosFiscales()
                        join pv in db.PuntosVenta.IgnoreQueryFilters(["BajaLogica"]) on c.IdPuntoVenta equals pv.Id
                        where c.Id == pago.IdComprobanteVenta && pv.IdEmpresa == fila.IdEmpresa
                        select new { c.Id, c.IdPuntoVenta, c.Estado, c.Fecha })
                    .FirstOrDefaultAsync(ct);
            if (pago is null || venta is null)
            {
                throw ErrorDominio.NoEncontrado($"No existe el pago {solicitud.IdCobro} en la empresa de la fila.");
            }

            if (fila.Resultado != ResultadosDeConciliacionMercadoPago.SinCobroEnWays)
            {
                throw ErrorDominio.Conflicto(
                    CodigosDeErrorDeConciliacion.FilaNoConfirmable, "Solo se confirma una fila que no tiene cobro en Ways.");
            }

            if (fila.IdCobro is not null)
            {
                throw ErrorDominio.Conflicto(CodigosDeErrorDeConciliacion.FilaYaConfirmada, "La fila ya se confirmó contra un pago.");
            }

            if (pago.VerificacionMercadoPago == VerificacionDePagoMercadoPago.VerificadoManualmente)
            {
                throw ErrorDominio.Conflicto(CodigosDeErrorDeConciliacion.PagoYaVerificado, "El pago ya se verificó contra otra fila.");
            }

            if (pago.VerificacionMercadoPago != VerificacionDePagoMercadoPago.SinVerificar)
            {
                throw ErrorDominio.Conflicto(
                    CodigosDeErrorDeConciliacion.PagoNoEstaSinVerificar, "El pago no es una aprobación manual sin verificar.");
            }

            if (venta.Estado == EstadoComprobante.Anulado)
            {
                throw ErrorDominio.Conflicto(CodigosDeErrorDeConciliacion.VentaAnulada, "La venta del pago está anulada.");
            }

            if (pago.Importe != fila.Importe)
            {
                throw ErrorDominio.Conflicto(
                    CodigosDeErrorDeConciliacion.ImporteDistinto, "El importe del pago no es el que informa la fila del reporte.");
            }

            var ahora = reloj.Ahora;
            var idFilaGuardada = fila.Id;
            var idPagoGuardado = pago.Id;
            var filaActualizada = await db.FilasConciliacionMercadoPago
                .Where(f => f.Id == idFilaGuardada && f.IdCobro == null)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.IdCobro, idPagoGuardado).SetProperty(f => f.UpdatedAt, ahora), ct);
            if (filaActualizada == 0)
            {
                throw ErrorDominio.Conflicto(CodigosDeErrorDeConciliacion.FilaYaConfirmada, "La fila ya se confirmó contra un pago.");
            }

            var pagoActualizado = await db.Cobros
                .Where(p => p.Id == idPagoGuardado && p.VerificacionMercadoPago == VerificacionDePagoMercadoPago.SinVerificar)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(p => p.VerificacionMercadoPago, VerificacionDePagoMercadoPago.VerificadoManualmente).SetProperty(p => p.UpdatedAt, ahora), ct);
            if (pagoActualizado == 0)
            {
                throw ErrorDominio.Conflicto(CodigosDeErrorDeConciliacion.PagoYaVerificado, "El pago ya se verificó contra otra fila.");
            }

            auditoria.Registrar(new RegistroDeAuditoria(
                fila.IdTenant,
                venta.IdPuntoVenta,
                AccionAuditada.ConciliacionConfirmacion,
                idConciliacion,
                valorAnterior: null,
                valorNuevo: new Dictionary<string, object?>
                {
                    ["id_fila"] = fila.Id,
                    ["numero_linea"] = fila.NumeroLinea,
                    ["id_cobro"] = pago.Id,
                    ["id_comprobante_venta"] = venta.Id,
                    ["importe_fila"] = fila.Importe,
                    ["fecha_transaccion"] = fila.FechaTransaccion,
                    ["source_id"] = fila.SourceId,
                    ["importe_pago"] = pago.Importe,
                    ["fecha_venta"] = venta.Fecha,
                    ["verificacion_mercadopago"] = VerificacionDePagoMercadoPago.VerificadoManualmente
                }));

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
            return Mapear(fila, []) with { IdCobro = idPagoGuardado };
        });
    }

    private async Task ExigirEmpresaAsync(int idEmpresa, CancellationToken ct)
    {
        if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
        {
            throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
        }
    }

    private async Task<List<ConciliacionListado>> MapearAsync(IReadOnlyList<ConciliacionMercadoPago> conciliaciones, CancellationToken ct)
    {
        if (conciliaciones.Count == 0)
        {
            return [];
        }

        var ids = conciliaciones.Select(c => c.Id).ToList();
        var idsDeUsuario = conciliaciones.Select(c => c.IdUsuario).Distinct().ToList();

        var sumas = await db.FilasConciliacionMercadoPago
            .AsNoTracking()
            .Where(f => ids.Contains(f.IdConciliacion))
            .GroupBy(f => new { f.IdConciliacion, f.Resultado })
            .Select(g => new
            {
                g.Key.IdConciliacion,
                g.Key.Resultado,
                Cantidad = g.Count(),
                Importe = g.Sum(f => f.Importe) ?? 0m,
                Comision = g.Sum(f => f.Comision) ?? 0m,
                Neto = g.Sum(f => f.Neto) ?? 0m
            })
            .ToListAsync(ct);

        var usuarios = await db.Usuarios
            .AsNoTracking()
            .Where(u => idsDeUsuario.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.NombreUsuario, ct);

        return conciliaciones
            .Select(c => Mapear(
                c,
                usuarios.GetValueOrDefault(c.IdUsuario),
                sumas
                    .Where(s => s.IdConciliacion == c.Id)
                    .Select(s => CuentaEnLosTotales(s.Resultado)
                        ? new ResumenDeResultadoDeConciliacion(s.Resultado, s.Cantidad, s.Importe, s.Comision, s.Neto)
                        : new ResumenDeResultadoDeConciliacion(s.Resultado, s.Cantidad, 0m, 0m, 0m))
                    .OrderBy(s => OrdenDeResultados.IndexOf(s.Resultado))
                    .ToList()))
            .ToList();
    }

    /// <summary>Una fila repetida o de un cobro que un archivo anterior ya conciliaba se cuenta, pero no suma importes: sumarla duplicaría el
    /// dinero que el reporte informa una sola vez.</summary>
    private static bool CuentaEnLosTotales(string resultado) =>
        resultado is not (ResultadosDeConciliacionMercadoPago.Duplicada or ResultadosDeConciliacionMercadoPago.YaConciliado);

    private static ConciliacionListado Mapear(
        ConciliacionMercadoPago c, string? usuario, IReadOnlyList<ResumenDeResultadoDeConciliacion> resumen) =>
        new(c.Id, c.IdEmpresa, c.NombreArchivo, c.FechaDesde, c.FechaHasta, c.IdUsuario, usuario, c.CreatedAt, c.CantidadFilas,
            c.CantidadInvalidas, resumen);

    private static FilaDeConciliacion Mapear(
        FilaConciliacionMercadoPago f, IReadOnlyList<CandidatoDeConfirmacion> candidatos, (decimal? Comision, decimal? Neto) guardado = default) =>
        new(f.Id, f.NumeroLinea, f.Resultado, f.SourceId, f.ExternalReference, f.TipoTransaccion, f.Importe, f.Comision, f.Neto,
            f.FechaTransaccion, f.FechaLiberacion, f.ExternalPosId, f.Motivo, f.IdCobroMercadoPago, f.IdCobro, candidatos,
            f.Resultado == ResultadosDeConciliacionMercadoPago.Conciliado, guardado.Comision, guardado.Neto);

    private static List<ResumenDeResultadoDeConciliacion> Resumir(IEnumerable<FilaConciliacionMercadoPago> filas) =>
        filas
            .GroupBy(f => f.Resultado)
            .Select(g => new ResumenDeResultadoDeConciliacion(
                g.Key,
                g.Count(),
                CuentaEnLosTotales(g.Key) ? g.Sum(f => f.Importe ?? 0m) : 0m,
                CuentaEnLosTotales(g.Key) ? g.Sum(f => f.Comision ?? 0m) : 0m,
                CuentaEnLosTotales(g.Key) ? g.Sum(f => f.Neto ?? 0m) : 0m))
            .OrderBy(r => OrdenDeResultados.IndexOf(r.Resultado))
            .ToList();

    private static string? LimpiarNombre(string? nombre)
    {
        var limpio = nombre?.Trim();
        if (string.IsNullOrEmpty(limpio))
        {
            return null;
        }

        limpio = limpio.Replace('\\', '/');
        limpio = limpio[(limpio.LastIndexOf('/') + 1)..];
        return limpio.Length == 0 ? null : limpio.Length <= 255 ? limpio : limpio[..255];
    }

    private static ErrorDominio ReporteInvalido(string motivo) =>
        new(CodigosDeErrorDeConciliacion.ReporteInvalido, motivo, 400);

    private static ErrorDominio YaImportada() =>
        ErrorDominio.Conflicto(CodigosDeErrorDeConciliacion.YaImportada, "Ese archivo ya se importó en esta empresa.");

    private static ErrorDominio Inexistente(int idConciliacion) =>
        ErrorDominio.NoEncontrado($"No existe la conciliación {idConciliacion}.");

    private static ErrorDominio Inexistente(int idConciliacion, int idFila) =>
        ErrorDominio.NoEncontrado($"No existe la fila {idFila} en la conciliación {idConciliacion}.");
}
