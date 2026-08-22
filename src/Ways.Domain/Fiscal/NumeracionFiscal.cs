namespace Ways.Domain.Fiscal;

/// <summary>
/// Serie fiscal ARCA, keyed por <c>(id_punto_venta, codigo_afip)</c> — proposal.md §F, decisión
/// 13. PK-only, sin auditoría ni baja lógica, mismo criterio exacto que
/// <see cref="Ventas.NumeracionComprobante"/> (<c>NumeracionComprobante.cs:10-13</c>): no hereda
/// de <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/>, así que necesita
/// filtro de tenant escrito a mano (<c>WaysDbContext.AplicarFiltroDeTenantEnNumeracionFiscal</c>).
///
/// DISCIPLINA OPUESTA a <c>NumeracionComprobante</c>: <c>AsignadorDeNumeroFiscal</c> (slice 4)
/// toma el número DENTRO de la transacción de emisión, nunca en una transacción propia previa —
/// un número quemado en la serie interna abre un hueco legítimo, en la serie de ARCA DETIENE la
/// serie (error 10016). <c>codigo_afip</c> en vez de un string de tipo interno porque la clave de
/// serie de ARCA es el tipo NUMÉRICO — usar nuestro código pediría una traducción en el camino
/// caliente del invariante.
///
/// <see cref="UltimoAutorizadoArca"/>/<see cref="SincronizadoEn"/> son estado de reconciliación
/// contra <c>FECompUltimoAutorizado</c> que <c>NumeracionComprobante</c> no tiene concepto — la
/// segunda razón por la que esta es una tabla propia y no una más ancha (design D13: la
/// reconciliación NUNCA escribe <see cref="ProximoNumero"/>, solo estos dos campos juntos).
/// </summary>
public class NumeracionFiscal
{
    public int IdPuntoVenta { get; set; }

    /// <summary><c>CbteTipo</c> de ARCA (1, 3, 6, 8, 11, 13, …) — <c>tipos_comprobante.codigo_afip</c>.</summary>
    public short CodigoAfip { get; set; }

    public int IdTenant { get; set; }

    public long ProximoNumero { get; set; } = 1;

    /// <summary>De <c>FECompUltimoAutorizado</c> — <c>0</c> es una respuesta legítima ("serie
    /// nunca usada"), <c>NULL</c> es "todavía no reconciliada" (CHECK 8).</summary>
    public long? UltimoAutorizadoArca { get; set; }

    /// <summary>Par de <see cref="UltimoAutorizadoArca"/> (<c>IRelojDelSistema</c>) — arriban
    /// juntos o ninguno (CHECK 8).</summary>
    public DateTimeOffset? SincronizadoEn { get; set; }
}
