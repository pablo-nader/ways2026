using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ways.Domain.MercadoPago;

/// <summary>Una fila del reporte de transacciones leída sin errores.</summary>
public sealed record FilaDeReporteMercadoPago(
    int NumeroLinea,
    string SourceId,
    string TipoTransaccion,
    decimal Importe,
    DateTimeOffset FechaTransaccion,
    string? ExternalReference,
    decimal? Comision,
    decimal? Neto,
    DateTimeOffset? FechaLiberacion,
    string? ExternalPosId);

/// <summary>Una fila que no se pudo leer: su línea y por qué.</summary>
public sealed record FilaInvalidaDeReporteMercadoPago(int NumeroLinea, string Motivo);

/// <summary>
/// Lo que salió de leer un reporte: las filas válidas, las inválidas con su motivo, o un <see cref="ErrorDeArchivo"/> cuando el archivo
/// entero no sirve (sin encabezado, columnas obligatorias que faltan, demasiadas filas). Leer nunca lanza.
/// </summary>
public sealed record ResultadoDeLecturaDeReporteMercadoPago(
    IReadOnlyList<FilaDeReporteMercadoPago> Filas,
    IReadOnlyList<FilaInvalidaDeReporteMercadoPago> Invalidas,
    string? ErrorDeArchivo)
{
    public static ResultadoDeLecturaDeReporteMercadoPago ConError(string error) => new([], [], error);
}

/// <summary>
/// Lector del "Reporte de todas las transacciones" (<c>settlement_report</c>) de Mercado Pago en CSV. Las columnas se buscan por nombre
/// (sin distinguir mayúsculas ni espacios de los bordes), así que el orden no importa y las columnas de más se ignoran. El separador
/// (coma o punto y coma) se decide por el encabezado; los campos pueden ir entre comillas (con <c>""</c> para una comilla y saltos de línea
/// adentro). Obligatorias: <c>SOURCE_ID</c>, <c>TRANSACTION_TYPE</c>, <c>TRANSACTION_AMOUNT</c>, <c>TRANSACTION_DATE</c>. Opcionales:
/// <c>EXTERNAL_REFERENCE</c>, <c>FEE_AMOUNT</c>, <c>SETTLEMENT_NET_AMOUNT</c>, <c>MONEY_RELEASE_DATE</c>, <c>EXTERNAL_POS_ID</c>; una opcional
/// vacía es nula y una opcional mal formada invalida la fila. Los números llevan "." decimal y a lo sumo dos decimales (sin separador de
/// miles); las fechas son ISO 8601 con zona (<c>Z</c> o <c>±hh:mm</c>): una fecha sin zona no se adivina. Un <c>TRANSACTION_TYPE</c>
/// que Ways no conoce invalida la fila.
/// </summary>
public static partial class LectorDeReporteMercadoPago
{
    public const string ColumnaSourceId = "SOURCE_ID";

    public const string ColumnaTipo = "TRANSACTION_TYPE";

    public const string ColumnaImporte = "TRANSACTION_AMOUNT";

    public const string ColumnaFecha = "TRANSACTION_DATE";

    public const string ColumnaExternalReference = "EXTERNAL_REFERENCE";

    public const string ColumnaComision = "FEE_AMOUNT";

    public const string ColumnaNeto = "SETTLEMENT_NET_AMOUNT";

    public const string ColumnaLiberacion = "MONEY_RELEASE_DATE";

    public const string ColumnaPos = "EXTERNAL_POS_ID";

    /// <summary>Tope de filas de un reporte (sin contar el encabezado ni las líneas en blanco).</summary>
    public const int MaximoDeFilas = 100_000;

    public const int LargoMaximoDeSourceId = 64;

    public const int LargoMaximoDeExternalReference = 128;

    public const int LargoMaximoDeExternalPosId = 80;

    private static readonly string[] Obligatorias = [ColumnaSourceId, ColumnaTipo, ColumnaImporte, ColumnaFecha];

    private const decimal ImporteMaximo = 9_999_999_999_999_999m;

    [GeneratedRegex(@"^[+-]?\d+(\.\d+)?$")]
    private static partial Regex FormatoDeNumero();

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?)(Z|[+-]\d{2}(?::?\d{2})?)$", RegexOptions.IgnoreCase)]
    private static partial Regex FormatoDeFecha();

    public static ResultadoDeLecturaDeReporteMercadoPago Leer(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return ResultadoDeLecturaDeReporteMercadoPago.ConError("El archivo está vacío.");
        }

        texto = texto.TrimStart('﻿');
        var separador = ElegirSeparador(texto);
        var registros = Partir(texto, separador).Where(r => r.Campos.Any(c => c.Trim().Length > 0)).ToList();
        if (registros.Count == 0)
        {
            return ResultadoDeLecturaDeReporteMercadoPago.ConError("El archivo está vacío.");
        }

        var encabezado = registros[0].Campos.Select(c => c.Trim().ToUpperInvariant()).ToList();
        var columnas = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < encabezado.Count; i++)
        {
            if (encabezado[i].Length == 0)
            {
                continue;
            }

            if (!columnas.TryAdd(encabezado[i], i))
            {
                return ResultadoDeLecturaDeReporteMercadoPago.ConError($"El encabezado repite la columna {encabezado[i]}.");
            }
        }

        var faltan = Obligatorias.Where(c => !columnas.ContainsKey(c)).ToList();
        if (faltan.Count > 0)
        {
            return ResultadoDeLecturaDeReporteMercadoPago.ConError(
                $"Faltan las columnas obligatorias: {string.Join(", ", faltan)}.");
        }

        if (registros.Count - 1 > MaximoDeFilas)
        {
            return ResultadoDeLecturaDeReporteMercadoPago.ConError($"El reporte supera las {MaximoDeFilas} filas.");
        }

        var filas = new List<FilaDeReporteMercadoPago>();
        var invalidas = new List<FilaInvalidaDeReporteMercadoPago>();
        foreach (var registro in registros.Skip(1))
        {
            var (fila, motivo) = LeerFila(registro, columnas, encabezado.Count);
            if (fila is not null)
            {
                filas.Add(fila);
            }
            else
            {
                invalidas.Add(new FilaInvalidaDeReporteMercadoPago(registro.Linea, motivo!));
            }
        }

        return new ResultadoDeLecturaDeReporteMercadoPago(filas, invalidas, null);
    }

    private static (FilaDeReporteMercadoPago? Fila, string? Motivo) LeerFila(
        Registro registro, Dictionary<string, int> columnas, int cantidadDeColumnas)
    {
        if (registro.Campos.Count > cantidadDeColumnas && registro.Campos.Skip(cantidadDeColumnas).Any(c => c.Trim().Length > 0))
        {
            return (null, "La fila tiene más columnas que el encabezado.");
        }

        string? Celda(string columna) =>
            columnas.TryGetValue(columna, out var i) && i < registro.Campos.Count && registro.Campos[i].Trim() is { Length: > 0 } valor
                ? valor
                : null;

        var sourceId = Celda(ColumnaSourceId);
        if (sourceId is null)
        {
            return (null, $"Falta {ColumnaSourceId}.");
        }

        if (sourceId.Length > LargoMaximoDeSourceId)
        {
            return (null, $"{ColumnaSourceId} supera los {LargoMaximoDeSourceId} caracteres.");
        }

        var tipoCrudo = Celda(ColumnaTipo);
        if (tipoCrudo is null)
        {
            return (null, $"Falta {ColumnaTipo}.");
        }

        var tipo = tipoCrudo.ToUpperInvariant();
        if (!TiposDeTransaccionMercadoPago.Conocidos.Contains(tipo))
        {
            return (null, $"{ColumnaTipo} desconocido: {Recortar(tipoCrudo)}.");
        }

        var importeCrudo = Celda(ColumnaImporte);
        if (importeCrudo is null)
        {
            return (null, $"Falta {ColumnaImporte}.");
        }

        if (!TryLeerDinero(importeCrudo, out var importe))
        {
            return (null, $"{ColumnaImporte} no es un importe válido: {Recortar(importeCrudo)}.");
        }

        var fechaCruda = Celda(ColumnaFecha);
        if (fechaCruda is null)
        {
            return (null, $"Falta {ColumnaFecha}.");
        }

        if (!TryLeerFecha(fechaCruda, out var fecha))
        {
            return (null, $"{ColumnaFecha} no es una fecha ISO con zona: {Recortar(fechaCruda)}.");
        }

        var externalReference = Celda(ColumnaExternalReference);
        if (externalReference is { Length: > LargoMaximoDeExternalReference })
        {
            return (null, $"{ColumnaExternalReference} supera los {LargoMaximoDeExternalReference} caracteres.");
        }

        var pos = Celda(ColumnaPos);
        if (pos is { Length: > LargoMaximoDeExternalPosId })
        {
            return (null, $"{ColumnaPos} supera los {LargoMaximoDeExternalPosId} caracteres.");
        }

        decimal? comision = null;
        if (Celda(ColumnaComision) is { } comisionCruda)
        {
            if (!TryLeerDinero(comisionCruda, out var valor))
            {
                return (null, $"{ColumnaComision} no es un importe válido: {Recortar(comisionCruda)}.");
            }

            comision = valor;
        }

        decimal? neto = null;
        if (Celda(ColumnaNeto) is { } netoCrudo)
        {
            if (!TryLeerDinero(netoCrudo, out var valor))
            {
                return (null, $"{ColumnaNeto} no es un importe válido: {Recortar(netoCrudo)}.");
            }

            neto = valor;
        }

        DateTimeOffset? liberacion = null;
        if (Celda(ColumnaLiberacion) is { } liberacionCruda)
        {
            if (!TryLeerFecha(liberacionCruda, out var valor))
            {
                return (null, $"{ColumnaLiberacion} no es una fecha ISO con zona: {Recortar(liberacionCruda)}.");
            }

            liberacion = valor;
        }

        return (
            new FilaDeReporteMercadoPago(
                registro.Linea, sourceId, tipo, importe, fecha, externalReference, comision, neto, liberacion, pos),
            null);
    }

    private static string Recortar(string valor) => valor.Length <= 40 ? valor : valor[..40] + "…";

    private static bool TryLeerDinero(string texto, out decimal valor)
    {
        valor = 0m;
        if (!FormatoDeNumero().IsMatch(texto))
        {
            return false;
        }

        var punto = texto.IndexOf('.');
        if (punto >= 0 && texto.Length - punto - 1 > 2)
        {
            return false;
        }

        return decimal.TryParse(texto, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out valor)
            && Math.Abs(valor) <= ImporteMaximo;
    }

    private static bool TryLeerFecha(string texto, out DateTimeOffset fecha)
    {
        fecha = default;
        var coincidencia = FormatoDeFecha().Match(texto);
        if (!coincidencia.Success)
        {
            return false;
        }

        var zona = coincidencia.Groups[3].Value.ToUpperInvariant();
        if (zona != "Z")
        {
            var signo = zona[0];
            var digitos = zona[1..].Replace(":", string.Empty, StringComparison.Ordinal);
            zona = digitos.Length == 2 ? $"{signo}{digitos}:00" : $"{signo}{digitos[..2]}:{digitos[2..]}";
        }

        return DateTimeOffset.TryParse(
            $"{coincidencia.Groups[1].Value}T{coincidencia.Groups[2].Value}{zona}",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out fecha);
    }

    private static char ElegirSeparador(string texto)
    {
        var comas = 0;
        var puntosYComas = 0;
        var entreComillas = false;
        foreach (var c in texto)
        {
            if (c == '"')
            {
                entreComillas = !entreComillas;
            }
            else if (!entreComillas)
            {
                if (c is '\n' or '\r')
                {
                    if (comas + puntosYComas > 0)
                    {
                        break;
                    }
                }
                else if (c == ',')
                {
                    comas++;
                }
                else if (c == ';')
                {
                    puntosYComas++;
                }
            }
        }

        return puntosYComas > comas ? ';' : ',';
    }

    private sealed record Registro(int Linea, List<string> Campos);

    private static IEnumerable<Registro> Partir(string texto, char separador)
    {
        var linea = 1;
        var lineaDelRegistro = 1;
        var campos = new List<string>();
        var actual = new StringBuilder();
        var entreComillas = false;
        var campoConComillas = false;

        for (var i = 0; i < texto.Length; i++)
        {
            var c = texto[i];
            if (entreComillas)
            {
                if (c == '"')
                {
                    if (i + 1 < texto.Length && texto[i + 1] == '"')
                    {
                        actual.Append('"');
                        i++;
                    }
                    else
                    {
                        entreComillas = false;
                    }
                }
                else
                {
                    if (c == '\n')
                    {
                        linea++;
                    }

                    actual.Append(c);
                }

                continue;
            }

            if (c == '"' && actual.Length == 0 && !campoConComillas)
            {
                entreComillas = true;
                campoConComillas = true;
            }
            else if (c == separador)
            {
                campos.Add(actual.ToString());
                actual.Clear();
                campoConComillas = false;
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < texto.Length && texto[i + 1] == '\n')
                {
                    i++;
                }

                campos.Add(actual.ToString());
                actual.Clear();
                campoConComillas = false;
                yield return new Registro(lineaDelRegistro, campos);
                campos = [];
                linea++;
                lineaDelRegistro = linea;
            }
            else
            {
                actual.Append(c);
            }
        }

        if (actual.Length > 0 || campos.Count > 0 || campoConComillas)
        {
            campos.Add(actual.ToString());
            yield return new Registro(lineaDelRegistro, campos);
        }
    }
}
