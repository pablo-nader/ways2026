using System.Globalization;
using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>Contra qué columna resuelve un <see cref="EntradaDeEscaneo.Codigo"/> (spec:
/// codigos-barra / Scan Resolution Rule).</summary>
public enum ObjetivoDeEscaneo
{
    CodigoInterno,
    CodigoBarra
}

/// <summary>Resultado puro de <see cref="ParserDeEscaneo.Parsear"/> — <see cref="Codigo"/> y
/// <see cref="Objetivo"/> le dicen a <see cref="Application.Ventas.ServicioDeEscaneo"/> qué
/// columna consultar, sin que este tipo sepa nada de persistencia.</summary>
public readonly record struct EntradaDeEscaneo(decimal Cantidad, string Codigo, ObjetivoDeEscaneo Objetivo);

/// <summary>
/// Parsea la entrada cruda de un escaneo de POS (design decision 7, spec: codigos-barra / Scan
/// Resolution Rule) — pura, sin acceso a base de datos: solo decide sintaxis y a qué columna
/// apunta el código, nunca si ese código existe.
/// </summary>
public static class ParserDeEscaneo
{
    /// <summary>Regla I.2: menos de 7 dígitos ⇒ <c>codigo_interno</c>, 7 o más ⇒
    /// <c>codigos_barra</c> — el mismo corte que <c>AsignadorDeCodigoInternoArticulo</c> ya
    /// documenta como restricción heredada por esta etapa.</summary>
    private const int LongitudMinimaCodigoBarra = 7;

    public static EntradaDeEscaneo Parsear(string? entrada)
    {
        var texto = entrada?.Trim();

        if (string.IsNullOrEmpty(texto))
        {
            throw new ErrorDominio("escaneo_invalido", "El código escaneado no puede estar vacío.", 400);
        }

        var (cantidad, codigo) = SepararCantidadYCodigo(texto);

        if (string.IsNullOrEmpty(codigo))
        {
            throw new ErrorDominio("escaneo_invalido", "El código escaneado no puede estar vacío.", 400);
        }

        var objetivo = codigo.Length < LongitudMinimaCodigoBarra
            ? ObjetivoDeEscaneo.CodigoInterno
            : ObjetivoDeEscaneo.CodigoBarra;

        return new EntradaDeEscaneo(cantidad, codigo, objetivo);
    }

    /// <summary>Sintaxis <c>&lt;cantidad&gt;*&lt;codigo&gt;</c> (p.ej. <c>"3*7790001"</c>).
    /// Un prefijo ausente, vacío, <c>"0"</c>, negativo o no numérico NUNCA invalida el
    /// escaneo — cae a cantidad 1 (spec: "defaults an empty or 0 cantidad to 1"; una entrada
    /// "rara" antes del <c>*</c> se trata igual que una ausente, nunca como error de parseo —
    /// el <c>*</c> es a lo sumo un separador opcional, no sintaxis obligatoria).</summary>
    private static (decimal Cantidad, string Codigo) SepararCantidadYCodigo(string texto)
    {
        var indice = texto.IndexOf('*');

        if (indice < 0)
        {
            return (1m, texto);
        }

        var prefijo = texto[..indice].Trim();
        var codigo = texto[(indice + 1)..].Trim();

        var cantidad = decimal.TryParse(prefijo, NumberStyles.Number, CultureInfo.InvariantCulture, out var parseada)
            && parseada > 0
                ? parseada
                : 1m;

        return (cantidad, codigo);
    }
}
