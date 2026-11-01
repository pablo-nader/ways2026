using Ways.Domain.Common;

namespace Ways.Domain.Caja;

/// <summary>
/// Reglas puras del ajuste manual del efectivo de tesorería de una empresa. A diferencia del ajuste de una cuenta digital,
/// la solicitud trae siempre un importe con signo: el fondo de efectivo no tiene un saldo de plataforma contra el cual
/// pedir un objetivo.
/// </summary>
public static class ReglasDeAjusteDeEfectivo
{
    public static decimal ValidarImporte(decimal? importe)
    {
        if (importe is null)
        {
            throw new ErrorDominio("importe_requerido", "El campo importe es obligatorio.", 400);
        }

        var valor = importe.Value;
        if (valor == 0m || Math.Abs(valor) > ReglasDeAjusteDeCuentaTesoreria.ImporteMaximo || decimal.Round(valor, 2) != valor)
        {
            throw new ErrorDominio(
                "importe_invalido",
                "El importe debe ser distinto de cero, hasta 999999999999.99 en valor absoluto y con hasta dos decimales.",
                400);
        }

        return valor;
    }

    public static (decimal Ingreso, decimal Egreso) Dividir(decimal importe) =>
        importe > 0m ? (importe, 0m) : (0m, -importe);
}
