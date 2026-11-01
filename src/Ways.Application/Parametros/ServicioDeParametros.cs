using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Stock;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Pagos;
using Ways.Domain.Ventas;

namespace Ways.Application.Parametros;

/// <summary>
/// Resolución de <c>parametros</c> (ADR-13): en una clave de caja el punto de venta gana sobre la empresa; en una de
/// sucursal (zona horaria, lotes, días de alerta) gana la sucursal. La empresa gana siempre sobre el default declarado.
/// <see cref="ResolucionDeParametros"/> (Domain, pura) hace la precedencia; acá solo se trae el puñado de filas
/// candidatas y se valida la clave contra el registro tipado.
///
/// <see cref="ITenantActual"/>/ADR-10: no hay "empresa actual" en la sesión todavía (la
/// selección de empresa/punto de venta es una etapa operativa posterior), así que
/// <paramref name="idEmpresa"/>/<c>idPuntoVenta</c> viajan explícitos en cada llamada.
///
/// stage-12-lotes-vencimientos, Slice 4 (design: Reconciliation triggers): un flip de
/// <c>lotes_habilitado</c> <c>false → true</c> en <see cref="EstablecerAsync"/> dispara
/// <see cref="ServicioDeLotes.ReconciliarAsync"/> — la transición se detecta comparando el
/// valor CRUDO de la fila tocada (antes/después), no un valor "efectivo" resuelto por
/// jerarquía (design: Reconciliation — "Scope resolution").
/// </summary>
public class ServicioDeParametros(IWaysDbContext db, IRelojDelSistema reloj, ServicioDeLotes servicioDeLotes)
{
    /// <summary>Con <paramref name="idPuntoVenta"/>, una clave de sucursal se resuelve por la sucursal de ese
    /// punto de venta (las cajas de una misma sucursal ven el mismo valor).</summary>
    public async Task<ParametroResuelto> ResolverAsync(
        string clave, int idEmpresa, int? idPuntoVenta, CancellationToken ct = default)
    {
        // Valida la clave antes de tocar la base: una clave desconocida no debería ni
        // disparar la query.
        ParametroConocido.Buscar(clave);

        int? idSucursal = null;
        if (idPuntoVenta is not null)
        {
            idSucursal = await ValidarPuntoVentaDeLaEmpresaAsync(idEmpresa, idPuntoVenta.Value, ct);
        }

        var valor = await ConsultaDeParametros.ResolverAsync(db, clave, idEmpresa, idPuntoVenta, idSucursal, ct);
        return new ParametroResuelto(clave, valor);
    }

    /// <summary>Lectura del endpoint: por punto de venta (que una clave de sucursal resuelve por su sucursal), por
    /// sucursal o solo por empresa. Los dos ids juntos se rechazan.</summary>
    public Task<ParametroResuelto> ResolverEnAlcanceAsync(
        string clave, int idEmpresa, int? idPuntoVenta, int? idSucursal, CancellationToken ct = default)
    {
        if (idPuntoVenta is not null && idSucursal is not null)
        {
            throw new ErrorDominio(
                "parametro_alcance_ambiguo", "Indicá idPuntoVenta o idSucursal, no los dos.", 400);
        }

        return idSucursal is { } id
            ? ResolverDeSucursalAsync(clave, idEmpresa, id, ct)
            : ResolverAsync(clave, idEmpresa, idPuntoVenta, ct);
    }

    /// <summary>Resuelve una clave de sucursal para la sucursal misma (sin pasar por una caja): sucursal → empresa →
    /// default. Una clave de caja se rechaza (400 <c>parametro_de_caja</c>): no tiene valor por sucursal.</summary>
    public async Task<ParametroResuelto> ResolverDeSucursalAsync(
        string clave, int idEmpresa, int idSucursal, CancellationToken ct = default)
    {
        var conocido = ParametroConocido.Buscar(clave);
        ExigirAlcance(conocido, idPuntoVenta: null, idSucursal);
        await ValidarSucursalDeLaEmpresaAsync(idEmpresa, idSucursal, ct);

        var valor = await ConsultaDeParametros.ResolverAsync(db, clave, idEmpresa, idPuntoVenta: null, idSucursal, ct);
        return new ParametroResuelto(clave, valor);
    }

    /// <summary>Múltiplo de redondeo de las sugerencias de precio de la empresa
    /// (<c>redondeo_sugerencia_precio</c>, a nivel empresa): la fila de la empresa o el default declarado.</summary>
    public async Task<decimal> ResolverRedondeoDeSugerenciaAsync(int idEmpresa, CancellationToken ct = default)
    {
        var resuelto = await ResolverAsync(ParametroConocido.RedondeoSugerenciaPrecio.Clave, idEmpresa, null, ct);
        return JsonSerializer.Deserialize<decimal>(resuelto.Valor);
    }

    public async Task<IReadOnlyList<ParametroListado>> ListarAsync(
        int idEmpresa, CancellationToken ct = default) =>
        await db.Parametros
            .Where(p => p.IdEmpresa == idEmpresa)
            .OrderBy(p => p.Clave).ThenBy(p => p.IdPuntoVenta).ThenBy(p => p.IdSucursal)
            .Select(p => new ParametroListado(p.Id, p.Clave, p.Valor, p.IdPuntoVenta, p.IdSucursal))
            .ToListAsync(ct);

    /// <summary>Alta o edición (upsert por <c>clave</c> + nivel: empresa, <c>idPuntoVenta</c> o <c>idSucursal</c>,
    /// el mismo trío que los índices únicos parciales de ADR-13). Una clave de sucursal solo admite
    /// <c>idSucursal</c>; una de caja, solo <c>idPuntoVenta</c>.</summary>
    public async Task<ParametroListado> EstablecerAsync(
        int idEmpresa, ParametroAlta datos, CancellationToken ct = default)
    {
        var conocido = ParametroConocido.Buscar(datos.Clave);
        ValidarTipo(conocido, datos.Valor);
        ExigirAlcance(conocido, datos.IdPuntoVenta, datos.IdSucursal);

        if (datos.IdPuntoVenta is not null)
        {
            await ValidarPuntoVentaDeLaEmpresaAsync(idEmpresa, datos.IdPuntoVenta.Value, ct);
        }

        if (datos.IdSucursal is not null)
        {
            await ValidarSucursalDeLaEmpresaAsync(idEmpresa, datos.IdSucursal.Value, ct);
        }

        var existente = await db.Parametros.FirstOrDefaultAsync(
            p => p.IdEmpresa == idEmpresa && p.Clave == datos.Clave
                && p.IdPuntoVenta == datos.IdPuntoVenta && p.IdSucursal == datos.IdSucursal, ct);

        // Task 4.3 (design: Reconciliation triggers): capturado ANTES de escribir. Valor efectivo previo del nivel
        // escrito: su propia fila o, sin ella, el que heredaba (la fila de la empresa para uno de sucursal; el default
        // declarado en cualquier otro caso), mismo criterio que ResolucionDeParametros.
        var esLotesHabilitado = string.Equals(datos.Clave, ParametroConocido.LotesHabilitado.Clave, StringComparison.OrdinalIgnoreCase);
        var valorAnteriorLotesHabilitado = esLotesHabilitado
            && await ValorEfectivoPreviaDeLotesAsync(idEmpresa, datos.IdSucursal, existente, ct);

        var ahora = reloj.Ahora;

        if (existente is null)
        {
            existente = new Parametro
            {
                IdEmpresa = idEmpresa,
                IdPuntoVenta = datos.IdPuntoVenta,
                IdSucursal = datos.IdSucursal,
                Clave = datos.Clave,
                Valor = datos.Valor,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };
            db.Parametros.Add(existente);
        }
        else
        {
            existente.Valor = datos.Valor;
            existente.UpdatedAt = ahora;
        }

        await db.SaveChangesAsync(ct);

        // Task 4.3 (design: Reconciliation triggers — "lotes_habilitado flipped false → true"):
        // alcance = la sucursal escrita o, si se escribió el default de la empresa, las sucursales de la empresa;
        // ReconciliarAsync resuelve el valor efectivo de cada sucursal (una con fila propia en false no se toca) y
        // filtra en SQL a los artículos controla_lote=true. Un re-run sobre pares ya reconciliados es un no-op seguro
        // (design decisión 13), nunca un costo de corrección.
        //
        // Contrato de fallo parcial (diseño aceptado, sin transacción ambiente entre el
        // SaveChangesAsync de arriba y este ReconciliarAsync): el flip de lotes_habilitado ya
        // quedó COMMITEADO antes de este punto. Si ReconciliarAsync falla a mitad de camino
        // (algunos pares (artículo, PV) del tenant ya reconciliados, otros no), el request
        // devuelve un 500 genérico y el flip queda commiteado con reconciliación incompleta. No
        // hay rollback del flip ni señal adicional que distinga "reconciliación completa" de
        // "parcial" — la recuperación es el self-healing por construcción de ReconciliarParAsync
        // (residuo recomputado desde el estado actual en la próxima corrida) o un POST manual a
        // /api/stock/lotes/reconciliacion sobre el mismo alcance.
        if (esLotesHabilitado && !valorAnteriorLotesHabilitado && DeserializarBool(datos.Valor))
        {
            await servicioDeLotes.ReconciliarAsync(
                idArticulo: null, idPuntoVenta: null,
                idSucursal: datos.IdSucursal, idEmpresa: datos.IdSucursal is null ? idEmpresa : null, ct);
        }

        return new ParametroListado(
            existente.Id, existente.Clave, existente.Valor, existente.IdPuntoVenta, existente.IdSucursal);
    }

    /// <summary>Valor efectivo de <c>lotes_habilitado</c> en el nivel que se escribe, antes de escribirlo.</summary>
    private async Task<bool> ValorEfectivoPreviaDeLotesAsync(
        int idEmpresa, int? idSucursal, Parametro? existente, CancellationToken ct)
    {
        if (existente is not null)
        {
            return DeserializarBool(existente.Valor);
        }

        if (idSucursal is null)
        {
            return DeserializarBool(ParametroConocido.LotesHabilitado.ValorPorDefecto);
        }

        var deLaEmpresa = await db.Parametros.AsNoTracking()
            .Where(p => p.IdEmpresa == idEmpresa && p.Clave == ParametroConocido.LotesHabilitado.Clave
                && p.IdPuntoVenta == null && p.IdSucursal == null)
            .Select(p => p.Valor)
            .FirstOrDefaultAsync(ct);

        return DeserializarBool(deLaEmpresa ?? ParametroConocido.LotesHabilitado.ValorPorDefecto);
    }

    /// <summary>Una clave pertenece a un solo nivel: la de sucursal no se escribe ni se lee por punto de venta y la de
    /// caja no tiene valor por sucursal. Los dos ids a la vez nunca tienen sentido.</summary>
    private static void ExigirAlcance(ParametroConocido conocido, int? idPuntoVenta, int? idSucursal)
    {
        if (idPuntoVenta is not null && idSucursal is not null)
        {
            throw new ErrorDominio(
                "parametro_alcance_ambiguo", "Indicá idPuntoVenta o idSucursal, no los dos.", 400);
        }

        if (conocido.Alcance == AlcanceDeParametro.Sucursal && idPuntoVenta is not null)
        {
            throw new ErrorDominio(
                "parametro_de_sucursal",
                $"'{conocido.Clave}' es un parámetro de la sucursal: se configura con idSucursal, no con idPuntoVenta.",
                400);
        }

        if (conocido.Alcance == AlcanceDeParametro.Caja && idSucursal is not null)
        {
            throw new ErrorDominio(
                "parametro_de_caja",
                $"'{conocido.Clave}' es un parámetro de la caja: se configura con idPuntoVenta, no con idSucursal.",
                400);
        }
    }

    private async Task ValidarSucursalDeLaEmpresaAsync(int idEmpresa, int idSucursal, CancellationToken ct)
    {
        var pertenece = await db.Sucursales.AnyAsync(su => su.Id == idSucursal && su.IdEmpresa == idEmpresa, ct);

        if (!pertenece)
        {
            throw new ErrorDominio(
                "sucursal_no_pertenece_a_la_empresa",
                "La sucursal indicada no pertenece a la empresa declarada.",
                400);
        }
    }

    /// <summary>Tope de <c>numeric(14,2)</c>, la columna donde termina el importe de la línea Envío.</summary>
    private const decimal ImporteMaximoDeEnvio = 999_999_999_999.99m;

    private static bool DeserializarBool(string valorJson) => JsonSerializer.Deserialize<bool>(valorJson);

    /// <summary>Sin cambio de esquema (decisión del usuario, judgment-day slice 3 ronda 1):
    /// <c>id_punto_venta</c> no tiene FK a <c>empresas</c> — solo a <c>puntos_venta</c> — así
    /// que nada en el esquema impide un punto de venta real pero de otra empresa del mismo
    /// tenant. Esta consulta, scopeada por tenant vía el filtro de EF, es el único lugar que
    /// lo valida.</summary>
    private async Task<int> ValidarPuntoVentaDeLaEmpresaAsync(int idEmpresa, int idPuntoVenta, CancellationToken ct)
    {
        var idSucursal = await db.PuntosVenta
            .Where(pv => pv.Id == idPuntoVenta && pv.IdEmpresa == idEmpresa)
            .Select(pv => (int?)pv.IdSucursal)
            .FirstOrDefaultAsync(ct);

        if (idSucursal is null)
        {
            throw new ErrorDominio(
                "punto_venta_no_pertenece_a_la_empresa",
                "El punto de venta indicado no pertenece a la empresa declarada.",
                400);
        }

        return idSucursal.Value;
    }

    /// <summary>Valida el JSON contra el tipo CLR declarado (ADR-13) y, para
    /// <c>zona_horaria</c>, además contra el catálogo IANA (design decisión 12): un
    /// <c>null</c> deserializado (p.ej. <c>valorJson = "null"</c> para una clave string) se
    /// rechaza acá en vez de guardarse — sin este chequeo, <c>JsonSerializer.Deserialize</c>
    /// devuelve <c>null</c> sin tirar excepción y el valor inválido pasa. Una zona horaria
    /// inválida se rechaza en la escritura (400) en vez de llegar a <c>date_trunc</c> en un
    /// reporte y explotar como un 22023 de Postgres.</summary>
    private static void ValidarTipo(ParametroConocido conocido, string valorJson)
    {
        object? valor;
        try
        {
            valor = JsonSerializer.Deserialize(valorJson, conocido.TipoClr);
        }
        catch (JsonException)
        {
            throw new ErrorDominio(
                "parametro_tipo_invalido",
                $"El valor de '{conocido.Clave}' tiene que ser un {conocido.TipoClr.Name} válido.",
                400);
        }

        if (valor is null)
        {
            throw new ErrorDominio(
                "parametro_tipo_invalido",
                $"El valor de '{conocido.Clave}' tiene que ser un {conocido.TipoClr.Name} válido.",
                400);
        }

        if (conocido.Clave == ParametroConocido.ImporteEnvio.Clave)
        {
            var importe = (decimal)valor;
            if (!ReglaDeEnvio.ImporteEsValido(importe))
            {
                throw new ErrorDominio(
                    "parametro_importe_envio_invalido",
                    $"El importe del envío tiene que ser de 0 a {ReglaDeEnvio.ImporteMaximo:0.00} y tener como máximo 2 decimales.",
                    400);
            }
        }

        if (conocido.Clave == ParametroConocido.ToleranciaRedondeoPagoProveedor.Clave
            && !ReglaDeRedondeoDePago.ToleranciaEsValida((decimal)valor))
        {
            throw new ErrorDominio(
                "parametro_tolerancia_redondeo_invalida",
                $"La tolerancia de redondeo tiene que ser de 0 a {ReglaDeRedondeoDePago.ToleranciaMaxima:0.00} y tener como máximo 2 decimales.",
                400);
        }

        if (conocido.Clave == ParametroConocido.ZonaHoraria.Clave)
        {
            var zona = (string)valor;
            try
            {
                // HasIanaId frena los ids nativos de Windows ("Argentina Standard Time"),
                // que FindSystemTimeZoneById resuelve en ambos OS pero Postgres no entiende.
                if (!TimeZoneInfo.FindSystemTimeZoneById(zona).HasIanaId)
                {
                    throw new TimeZoneNotFoundException(zona);
                }
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new ErrorDominio(
                    "parametro_zona_horaria_invalida",
                    $"'{zona}' no es un identificador de zona horaria IANA válido.",
                    400);
            }
        }
    }
}
