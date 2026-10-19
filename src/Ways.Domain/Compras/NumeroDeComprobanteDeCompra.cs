namespace Ways.Domain.Compras;

/// <summary>
/// Número de una factura de proveedor (tipos que registran el libro IVA): forma canónica
/// <c>PPPPP-NNNNNNNN</c>, la vigente de ARCA (punto de venta de 5 dígitos, número de 8), y la
/// lectura numérica que usa el chequeo de duplicados. Regla pura, sin base de datos.
/// </summary>
public static class NumeroDeComprobanteDeCompra
{
    public const int DigitosPuntoDeVenta = 5;
    public const int DigitosNumero = 8;

    // Un valor guardado de más de 18 dígitos por lado no cabe en long; nunca puede coincidir con
    // uno canónico (5 y 8 dígitos), así que se trata como no comparable.
    private const int DigitosMaximosComparables = 18;

    /// <summary>Lleva a <c>PPPPP-NNNNNNNN</c> lo que escribe una persona o lee el asistente:
    /// <c>3-12345</c>, <c>0003-00012345</c>, <c>00003-00012345</c>, con espacios alrededor del guion, o
    /// 12 (4+8) o 13 (5+8) dígitos pegados. Rechaza vacío, letras o cualquier otro símbolo (un segundo
    /// guion incluido), un punto de venta de más de 5 dígitos o un número de más de 8 (ceros a la
    /// izquierda aparte), y el cero en cualquiera de los dos. Sin guion, otra cantidad de dígitos es ambigua y
    /// también se rechaza. Quien llama trata <c>null</c> y vacío como "sin número" antes de llamar.</summary>
    public static bool TryNormalizar(string? entrada, out string canonico)
    {
        canonico = string.Empty;

        var texto = entrada?.Trim();
        if (string.IsNullOrEmpty(texto))
        {
            return false;
        }

        string puntoDeVenta;
        string numero;

        var guion = texto.IndexOf('-');
        if (guion >= 0)
        {
            puntoDeVenta = texto[..guion].Trim();
            numero = texto[(guion + 1)..].Trim();
        }
        else
        {
            if (texto.Length is not (DigitosNumero + 4 or DigitosNumero + DigitosPuntoDeVenta))
            {
                return false;
            }

            puntoDeVenta = texto[..^DigitosNumero];
            numero = texto[^DigitosNumero..];
        }

        if (!TryLeerSegmento(puntoDeVenta, DigitosPuntoDeVenta, out var pv)
            || !TryLeerSegmento(numero, DigitosNumero, out var nro))
        {
            return false;
        }

        canonico = $"{pv}-{nro}";
        return true;
    }

    /// <summary>Par numérico (punto de venta, número) de CUALQUIER valor guardado, para comparar
    /// duplicados: parte en el ÚLTIMO guion y se queda con los dígitos de cada lado, así que
    /// <c>0003-00012345</c>, <c>3-12345</c> y <c>FA 0003-00012345</c> dan el mismo par. Un valor sin guion
    /// o sin dígitos de un lado (texto libre, por ejemplo <c>000300012345</c>) no es comparable y nunca
    /// coincide con nada: el chequeo no adivina dónde cortaría un humano.</summary>
    public static bool TryObtenerSegmentos(string? numeroGuardado, out (long PuntoDeVenta, long Numero) segmentos)
    {
        segmentos = default;

        var guion = numeroGuardado?.LastIndexOf('-') ?? -1;
        if (guion < 0)
        {
            return false;
        }

        if (!TryLeerDigitos(numeroGuardado![..guion], out var puntoDeVenta)
            || !TryLeerDigitos(numeroGuardado[(guion + 1)..], out var numero))
        {
            return false;
        }

        segmentos = (puntoDeVenta, numero);
        return true;
    }

    private static bool TryLeerSegmento(string texto, int digitosMaximos, out string relleno)
    {
        relleno = string.Empty;

        if (texto.Length == 0 || !texto.All(EsDigito))
        {
            return false;
        }

        var significativos = texto.TrimStart('0');
        if (significativos.Length == 0 || significativos.Length > digitosMaximos)
        {
            return false;
        }

        relleno = significativos.PadLeft(digitosMaximos, '0');
        return true;
    }

    private static bool TryLeerDigitos(string texto, out long valor)
    {
        valor = 0;

        var digitos = new string(texto.Where(EsDigito).ToArray());
        if (digitos.Length == 0 || digitos.Length > DigitosMaximosComparables)
        {
            return false;
        }

        valor = long.Parse(digitos, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static bool EsDigito(char c) => c is >= '0' and <= '9';
}
