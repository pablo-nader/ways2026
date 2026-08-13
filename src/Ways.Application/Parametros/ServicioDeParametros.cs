using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Stock;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Application.Parametros;

/// <summary>
/// Resolución de <c>parametros</c> (ADR-13): punto de venta gana sobre empresa, empresa
/// gana sobre el default declarado. <see cref="ResolucionDeParametros"/> (Domain, pura) hace
/// la precedencia; acá solo se trae el puñado de filas candidatas (a lo sumo dos por clave)
/// y se valida la clave contra el registro tipado.
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
    public async Task<ParametroResuelto> ResolverAsync(
        string clave, int idEmpresa, int? idPuntoVenta, CancellationToken ct = default)
    {
        // Valida la clave antes de tocar la base: una clave desconocida no debería ni
        // disparar la query.
        ParametroConocido.Buscar(clave);

        if (idPuntoVenta is not null)
        {
            await ValidarPuntoVentaDeLaEmpresaAsync(idEmpresa, idPuntoVenta.Value, ct);
        }

        var candidatos = await db.Parametros
            .Where(p => p.Clave == clave && p.IdEmpresa == idEmpresa
                && (p.IdPuntoVenta == null || p.IdPuntoVenta == idPuntoVenta))
            .ToListAsync(ct);

        var valor = ResolucionDeParametros.Resolver(clave, candidatos, idPuntoVenta);
        return new ParametroResuelto(clave, valor);
    }

    public async Task<IReadOnlyList<ParametroListado>> ListarAsync(
        int idEmpresa, CancellationToken ct = default) =>
        await db.Parametros
            .Where(p => p.IdEmpresa == idEmpresa)
            .OrderBy(p => p.Clave).ThenBy(p => p.IdPuntoVenta)
            .Select(p => new ParametroListado(p.Id, p.Clave, p.Valor, p.IdPuntoVenta))
            .ToListAsync(ct);

    /// <summary>Alta o edición (upsert por <c>clave</c> + <c>idPuntoVenta</c>, mismo par que
    /// los índices únicos parciales de ADR-13).</summary>
    public async Task<ParametroListado> EstablecerAsync(
        int idEmpresa, ParametroAlta datos, CancellationToken ct = default)
    {
        var conocido = ParametroConocido.Buscar(datos.Clave);
        ValidarTipo(conocido, datos.Valor);

        if (datos.IdPuntoVenta is not null)
        {
            await ValidarPuntoVentaDeLaEmpresaAsync(idEmpresa, datos.IdPuntoVenta.Value, ct);
        }

        var existente = await db.Parametros.FirstOrDefaultAsync(
            p => p.IdEmpresa == idEmpresa && p.Clave == datos.Clave && p.IdPuntoVenta == datos.IdPuntoVenta, ct);

        // Task 4.3 (design: Reconciliation triggers): capturado ANTES de escribir — una fila
        // ausente vale el default declarado ("false" para lotes_habilitado), mismo criterio de
        // resolución que ResolucionDeParametros usa para cualquier otra clave.
        var esLotesHabilitado = string.Equals(datos.Clave, ParametroConocido.LotesHabilitado.Clave, StringComparison.OrdinalIgnoreCase);
        var valorAnteriorLotesHabilitado = esLotesHabilitado && existente is not null && DeserializarBool(existente.Valor);

        var ahora = reloj.Ahora;

        if (existente is null)
        {
            existente = new Parametro
            {
                IdEmpresa = idEmpresa,
                IdPuntoVenta = datos.IdPuntoVenta,
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
        // alcance = todo el tenant; ReconciliarAsync ya filtra en SQL a los artículos
        // controla_lote=true cuyo PV pertenece a una empresa con lotes_habilitado efectivo
        // true — un re-run que también toca otras empresas ya habilitadas es un no-op seguro
        // (design decisión 13), nunca un costo de corrección.
        if (esLotesHabilitado && !valorAnteriorLotesHabilitado && DeserializarBool(datos.Valor))
        {
            await servicioDeLotes.ReconciliarAsync(idArticulo: null, idPuntoVenta: null, ct);
        }

        return new ParametroListado(existente.Id, existente.Clave, existente.Valor, existente.IdPuntoVenta);
    }

    private static bool DeserializarBool(string valorJson) => JsonSerializer.Deserialize<bool>(valorJson);

    /// <summary>Sin cambio de esquema (decisión del usuario, judgment-day slice 3 ronda 1):
    /// <c>id_punto_venta</c> no tiene FK a <c>empresas</c> — solo a <c>puntos_venta</c> — así
    /// que nada en el esquema impide un punto de venta real pero de otra empresa del mismo
    /// tenant. Esta consulta, scopeada por tenant vía el filtro de EF, es el único lugar que
    /// lo valida.</summary>
    private async Task ValidarPuntoVentaDeLaEmpresaAsync(int idEmpresa, int idPuntoVenta, CancellationToken ct)
    {
        var pertenece = await db.PuntosVenta.AnyAsync(pv => pv.Id == idPuntoVenta && pv.IdEmpresa == idEmpresa, ct);

        if (!pertenece)
        {
            throw new ErrorDominio(
                "punto_venta_no_pertenece_a_la_empresa",
                "El punto de venta indicado no pertenece a la empresa declarada.",
                400);
        }
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
