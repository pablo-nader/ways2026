using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Listados;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Caja;
using Ways.Domain.Fiscal;
using Ways.Domain.Ventas;
using static Ways.Application.Busqueda.BusquedaSinAcentos;

namespace Ways.Application.Ventas;

/// <summary>
/// Grilla de comprobantes de venta del back office: un filtro y un orden por columna, paginados en SQL, para encontrar un ticket
/// y corregirle los medios de pago (<see cref="ServicioDeCambioDeMedios"/>). Los cobros de cada venta se leen con
/// <c>GET /api/ventas/{id}</c>; acá solo va el resumen de medios, agregado en la misma consulta (sin una lectura por fila).
///
/// Lee los tickets, no las facturas espejo (<c>SinEspejosFiscales</c>); el espejo se mira solo para el estado fiscal. El filtro de
/// baja lógica se apaga para toda la consulta (el punto de venta dado de baja sigue decidiendo la conciliación de su empresa y el
/// medio dado de baja conserva su nombre en el resumen), así que las bajas del comprobante, el cobro, el cliente, el empleado y
/// el punto de venta que se muestra se excluyen a mano: uno dado de baja se proyecta como <c>null</c> (dangling-fk-read-models).
/// </summary>
public class ServicioDeVentasDeAdministracion(IWaysDbContext db)
{
    public static readonly IReadOnlyDictionary<string, ColumnaDeOrdenDeVentasDeAdministracion> ClavesDeOrden =
        new Dictionary<string, ColumnaDeOrdenDeVentasDeAdministracion>(StringComparer.Ordinal)
        {
            ["fecha"] = ColumnaDeOrdenDeVentasDeAdministracion.Fecha,
            ["numero"] = ColumnaDeOrdenDeVentasDeAdministracion.Numero,
            ["tipo"] = ColumnaDeOrdenDeVentasDeAdministracion.Tipo,
            ["puntoVenta"] = ColumnaDeOrdenDeVentasDeAdministracion.PuntoVenta,
            ["turno"] = ColumnaDeOrdenDeVentasDeAdministracion.Turno,
            ["cliente"] = ColumnaDeOrdenDeVentasDeAdministracion.Cliente,
            ["empleado"] = ColumnaDeOrdenDeVentasDeAdministracion.Empleado,
            ["medios"] = ColumnaDeOrdenDeVentasDeAdministracion.Medios,
            ["total"] = ColumnaDeOrdenDeVentasDeAdministracion.Total,
            ["estado"] = ColumnaDeOrdenDeVentasDeAdministracion.Estado,
            ["estadoFiscal"] = ColumnaDeOrdenDeVentasDeAdministracion.EstadoFiscal,
            ["estadoTurno"] = ColumnaDeOrdenDeVentasDeAdministracion.EstadoTurno,
            ["conciliado"] = ColumnaDeOrdenDeVentasDeAdministracion.Conciliado,
        };

    /// <summary>Sin orden pedido: fecha descendente y, a igual fecha, id descendente. Con orden: la columna pedida y siempre el id
    /// ascendente como desempate. Un orden, una dirección o un estado fiscal desconocidos se rechazan con 400.</summary>
    public async Task<PaginaDeVentasDeAdministracion> ListarAsync(
        FiltrosDeVentasDeAdministracion filtros, int pagina = 1, int tamanio = 25, string? orden = null,
        string? direccion = null, CancellationToken ct = default)
    {
        var ordenPedido = OrdenDeListado.Interpretar(orden, direccion, ClavesDeOrden);
        var rangoFiscal = RangoFiscalDelFiltro(filtros.EstadoFiscal);
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = Filtrar(Proyectar(), filtros, rangoFiscal);
        var total = await query.CountAsync(ct);

        var filas = await Ordenar(query, ordenPedido)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .ToListAsync(ct);

        return new PaginaDeVentasDeAdministracion(filas.Select(ADto).ToList(), total, pagina, tamanio);
    }

    private static int? RangoFiscalDelFiltro(string? estadoFiscal)
    {
        if (estadoFiscal is null)
        {
            return null;
        }

        return EstadosFiscalesDeGrilla.Rangos.TryGetValue(estadoFiscal, out var rango)
            ? rango
            : throw new ErrorDominio(
                "estado_fiscal_invalido",
                $"estadoFiscal '{estadoFiscal}' no es válido; valores posibles: {string.Join(", ", EstadosFiscalesDeGrilla.Rangos.Keys)}.",
                400);
    }

    private sealed class FilaProyectada
    {
        public int Id { get; init; }
        public long Numero { get; init; }
        public int IdPuntoVentaDelNumero { get; init; }
        public DateTimeOffset Fecha { get; init; }
        public int IdTipoComprobante { get; init; }
        public required string CodigoTipo { get; init; }
        public required string Tipo { get; init; }
        public short Signo { get; init; }
        public int? IdPuntoVenta { get; init; }
        public string? PuntoVenta { get; init; }
        public int? IdTurnoCaja { get; init; }
        public bool? TurnoCerrado { get; init; }
        public int? IdCliente { get; init; }
        public string? Cliente { get; init; }
        public int? IdEmpleado { get; init; }
        public string? Empleado { get; init; }
        public decimal Total { get; init; }
        public EstadoComprobante Estado { get; init; }
        public ResultadoFiscal? ResultadoDeLaVenta { get; init; }
        public ResultadoFiscal? ResultadoDeLaFactura { get; init; }
        public int EstadoFiscal { get; init; }
        public string? Medios { get; init; }
        public bool Conciliado { get; init; }
        public bool CobradaConCuentaCorriente { get; init; }
    }

    private IQueryable<FilaProyectada> Proyectar() =>
        from c in db.ComprobantesVenta.IgnoreQueryFilters(["BajaLogica"]).SinEspejosFiscales()
        where c.DeletedAt == null
        join t in db.TiposComprobante.IgnoreQueryFilters(["BajaLogica"]) on c.IdTipoComprobante equals t.Id
        join pv in db.PuntosVenta.IgnoreQueryFilters(["BajaLogica"]) on c.IdPuntoVenta equals pv.Id
        let resultadoDeLaFactura = db.ComprobantesVenta
            .Where(e => e.IdVentaOrigen == c.Id && e.IdComprobanteAsociado == null && e.DeletedAt == null)
            .Select(e => e.ResultadoFiscal)
            .FirstOrDefault()
        let fechaDeCierre = db.TurnosCaja
            .Where(turno => turno.Id == c.IdTurnoCaja)
            .Select(turno => turno.FechaCierre)
            .FirstOrDefault()
        let conCae = c.ResultadoFiscal == ResultadoFiscal.Aprobado
            || c.ResultadoFiscal == ResultadoFiscal.AprobadoConObservaciones
            || resultadoDeLaFactura == ResultadoFiscal.Aprobado
            || resultadoDeLaFactura == ResultadoFiscal.AprobadoConObservaciones
        let medios = string.Join(
            ", ",
            (from co in db.Cobros
             where co.IdComprobanteVenta == c.Id && co.DeletedAt == null
             join m in db.MediosPago on co.IdMedioPago equals m.Id
             select m.Nombre).Distinct().OrderBy(nombre => nombre))
        let cobradaConCuentaCorriente = db.Cobros.Any(co =>
            co.IdComprobanteVenta == c.Id && co.DeletedAt == null
            && db.MediosPago.Any(m => m.Id == co.IdMedioPago && m.Comportamiento == ComportamientoMedioPago.CuentaCorriente))
        let enTramite = c.ResultadoFiscal == ResultadoFiscal.Pendiente || resultadoDeLaFactura == ResultadoFiscal.Pendiente
        select new FilaProyectada
        {
            Id = c.Id,
            Numero = c.Numero,
            IdPuntoVentaDelNumero = c.IdPuntoVenta,
            Fecha = c.Fecha,
            IdTipoComprobante = c.IdTipoComprobante,
            CodigoTipo = t.Codigo,
            Tipo = t.Nombre,
            Signo = t.Signo,
            IdPuntoVenta = pv.DeletedAt == null ? pv.Id : null,
            PuntoVenta = pv.DeletedAt == null ? pv.Nombre : null,
            IdTurnoCaja = c.IdTurnoCaja,
            TurnoCerrado = db.TurnosCaja
                .Where(turno => turno.Id == c.IdTurnoCaja)
                .Select(turno => (bool?)(turno.Estado == EstadoTurno.Cerrado))
                .FirstOrDefault(),
            IdCliente = db.Clientes.Where(cl => cl.Id == c.IdCliente && cl.DeletedAt == null).Select(cl => (int?)cl.Id).FirstOrDefault(),
            Cliente = db.Clientes
                .Where(cl => cl.Id == c.IdCliente && cl.DeletedAt == null)
                .Select(cl => cl.RazonSocial ?? (cl.Nombre + " " + (cl.Apellido ?? "")).Trim())
                .FirstOrDefault(),
            IdEmpleado = db.Usuarios.Where(u => u.Id == c.IdEmpleado && u.DeletedAt == null).Select(u => (int?)u.Id).FirstOrDefault(),
            Empleado = db.Usuarios.Where(u => u.Id == c.IdEmpleado && u.DeletedAt == null).Select(u => u.NombreUsuario).FirstOrDefault(),
            Total = c.Total,
            Estado = c.Estado,
            ResultadoDeLaVenta = c.ResultadoFiscal,
            ResultadoDeLaFactura = resultadoDeLaFactura,
            EstadoFiscal = conCae ? 2 : enTramite ? 1 : 0,
            Medios = medios,
            Conciliado = db.ConciliacionesTesoreria.Any(k =>
                k.IdEmpresa == pv.IdEmpresa
                && k.Hasta >= (fechaDeCierre != null && fechaDeCierre < c.Fecha ? fechaDeCierre : c.Fecha)),
            CobradaConCuentaCorriente = cobradaConCuentaCorriente
        };

    private IQueryable<FilaProyectada> Filtrar(IQueryable<FilaProyectada> query, FiltrosDeVentasDeAdministracion filtros, int? rangoFiscal)
    {
        if (filtros.Desde is { } desde)
        {
            query = query.Where(f => f.Fecha >= desde);
        }

        if (filtros.Hasta is { } hasta)
        {
            query = query.Where(f => f.Fecha <= hasta);
        }

        if (filtros.Numero is { } numero)
        {
            query = query.Where(f => f.Numero == numero);
        }

        if (filtros.IdTipoComprobante is { } idTipo)
        {
            query = query.Where(f => f.IdTipoComprobante == idTipo);
        }

        if (filtros.IdPuntoVenta is { } idPuntoVenta)
        {
            query = query.Where(f => f.IdPuntoVenta == idPuntoVenta);
        }

        if (filtros.IdTurnoCaja is { } idTurnoCaja)
        {
            query = query.Where(f => f.IdTurnoCaja == idTurnoCaja);
        }

        if (FiltrosDeListado.PatronONulo(filtros.Cliente) is { } patron)
        {
            query = query.Where(f => Coincide(f.Cliente, patron));
        }

        if (filtros.IdEmpleado is { } idEmpleado)
        {
            query = query.Where(f => f.IdEmpleado == idEmpleado);
        }

        if (filtros.IdMedioPago is { } idMedioPago)
        {
            query = query.Where(f => db.Cobros.Any(co =>
                co.IdComprobanteVenta == f.Id && co.DeletedAt == null && co.IdMedioPago == idMedioPago));
        }

        if (filtros.TotalDesde is { } totalDesde)
        {
            query = query.Where(f => f.Total >= totalDesde);
        }

        if (filtros.TotalHasta is { } totalHasta)
        {
            query = query.Where(f => f.Total <= totalHasta);
        }

        if (filtros.Estado is { } estado)
        {
            query = query.Where(f => f.Estado == estado);
        }

        if (rangoFiscal is { } rango)
        {
            query = query.Where(f => f.EstadoFiscal == rango);
        }

        if (filtros.TurnoCerrado is { } turnoCerrado)
        {
            query = query.Where(f => f.TurnoCerrado == turnoCerrado);
        }

        if (filtros.Conciliado is { } conciliado)
        {
            query = query.Where(f => f.Conciliado == conciliado);
        }

        return query;
    }

    private IQueryable<FilaProyectada> Ordenar(
        IQueryable<FilaProyectada> query, OrdenDeListado<ColumnaDeOrdenDeVentasDeAdministracion>? orden)
    {
        if (orden is not { } pedido)
        {
            return query.OrderByDescending(f => f.Fecha).ThenByDescending(f => f.Id);
        }

        var desc = pedido.Descendente;
        if (pedido.Columna == ColumnaDeOrdenDeVentasDeAdministracion.Medios)
        {
            return OrdenarPorMedios(query, desc);
        }

        var ordenado = pedido.Columna switch
        {
            ColumnaDeOrdenDeVentasDeAdministracion.Fecha => OrdenarPor.Clave(query, f => f.Fecha, desc),
            ColumnaDeOrdenDeVentasDeAdministracion.Numero => desc
                ? query.OrderByDescending(f => f.IdPuntoVentaDelNumero).ThenByDescending(f => f.Numero)
                : query.OrderBy(f => f.IdPuntoVentaDelNumero).ThenBy(f => f.Numero),
            ColumnaDeOrdenDeVentasDeAdministracion.Tipo => OrdenarPor.Clave(query, f => f.Tipo, desc),
            ColumnaDeOrdenDeVentasDeAdministracion.PuntoVenta => OrdenarPor.ClaveConVacios(query, f => f.PuntoVenta, desc),
            ColumnaDeOrdenDeVentasDeAdministracion.Turno => OrdenarPor.ClaveConVacios(query, f => f.IdTurnoCaja, desc),
            ColumnaDeOrdenDeVentasDeAdministracion.Cliente => OrdenarPor.ClaveConVacios(query, f => f.Cliente, desc),
            ColumnaDeOrdenDeVentasDeAdministracion.Empleado => OrdenarPor.ClaveConVacios(query, f => f.Empleado, desc),
            ColumnaDeOrdenDeVentasDeAdministracion.Total => OrdenarPor.Clave(query, f => f.Total, desc),
            ColumnaDeOrdenDeVentasDeAdministracion.Estado => OrdenarPor.Clave(query, f => f.Estado, desc),
            ColumnaDeOrdenDeVentasDeAdministracion.EstadoFiscal => OrdenarPor.Clave(query, f => f.EstadoFiscal, desc),
            ColumnaDeOrdenDeVentasDeAdministracion.EstadoTurno => OrdenarPor.ClaveConVacios(query, f => f.TurnoCerrado, desc),
            ColumnaDeOrdenDeVentasDeAdministracion.Conciliado => OrdenarPor.Clave(query, f => f.Conciliado, desc),
            _ => throw new ArgumentOutOfRangeException(nameof(orden), pedido.Columna, null),
        };

        return ordenado.ThenBy(f => f.Id);
    }

    /// <summary>El resumen que muestra la fila es un agregado correlacionado que EF no puede usar como clave de orden. Para ordenar
    /// se arma el mismo texto (nombres sin repetir, por nombre) en una subconsulta agrupada por venta y se une por id; solo esta
    /// ordenación paga esa agregación sobre todos los cobros. Las ventas sin cobros van al final en las dos direcciones, y el id
    /// ascendente desempata.</summary>
    private IQueryable<FilaProyectada> OrdenarPorMedios(IQueryable<FilaProyectada> query, bool descendente)
    {
        var mediosPorVenta = from co in db.Cobros
                             where co.DeletedAt == null
                             join m in db.MediosPago on co.IdMedioPago equals m.Id
                             group m.Nombre by co.IdComprobanteVenta into g
                             select new { IdComprobanteVenta = g.Key, Medios = string.Join(", ", g.Distinct().OrderBy(n => n)) };

        var conClave = from f in query
                       join mm in mediosPorVenta on f.Id equals mm.IdComprobanteVenta into delaVenta
                       from mm in delaVenta.DefaultIfEmpty()
                       select new { Fila = f, Clave = mm.Medios };

        var porVacio = conClave.OrderBy(x => x.Clave == null);
        var ordenado = descendente ? porVacio.ThenByDescending(x => x.Clave) : porVacio.ThenBy(x => x.Clave);

        return ordenado.ThenBy(x => x.Fila.Id).Select(x => x.Fila);
    }

    private static FilaDeVentaDeAdministracion ADto(FilaProyectada f)
    {
        var motivo = ReglaDeCambioDeMedios.MotivoDeBloqueo(new ReglaDeCambioDeMedios.SituacionDeLaVenta(
            f.Estado == EstadoComprobante.Anulado, f.CodigoTipo, f.Signo, f.ResultadoDeLaVenta, f.ResultadoDeLaFactura,
            f.Conciliado, f.CobradaConCuentaCorriente));

        return new FilaDeVentaDeAdministracion(
            f.Id, f.Numero, NumeroDeComprobante.Formatear(f.IdPuntoVentaDelNumero, f.Numero), f.Fecha, f.IdTipoComprobante,
            f.CodigoTipo, f.Tipo, f.IdPuntoVenta, f.PuntoVenta, f.IdTurnoCaja, f.TurnoCerrado, f.IdCliente, f.Cliente,
            f.IdEmpleado, f.Empleado, f.Total, f.Estado, EstadosFiscalesDeGrilla.De(f.EstadoFiscal),
            string.IsNullOrEmpty(f.Medios) ? null : f.Medios, f.Conciliado, motivo is null, motivo);
    }
}
