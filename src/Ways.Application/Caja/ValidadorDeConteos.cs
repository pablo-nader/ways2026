using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Application.Caja;

/// <summary>
/// Compara los conteos declarados por el cajero contra los medios arqueables que el SERVIDOR
/// calculó (design: The Derivation — "Which medios get a row"; The Cierre Transaction, paso 4).
/// Contar cero es un acto deliberado del cajero, nunca un default que el servidor asuma: falta
/// un medio arqueable ⇒ <c>400 arqueo_incompleto</c>; sobra uno sin actividad ⇒ <c>400
/// medio_sin_actividad_en_el_turno</c>; sobra uno de cuenta corriente ⇒ <c>400
/// medio_no_arqueable</c>.
/// </summary>
public static class ValidadorDeConteos
{
    public static void Validar(
        IReadOnlyList<LineaDeArqueo> arqueables, IReadOnlyList<ActividadDeMedio> actividad,
        IReadOnlyList<ConteoDeclarado> declarados)
    {
        // Chequeos de forma del payload, ANTES de comparar contra los arqueables del servidor: un
        // idMedioPago duplicado reventaría con un ArgumentException genérico (500) al armar el
        // diccionario en ServicioDeTurnos.EjecutarCierreAsync si llegara hasta ahí sin control.
        var duplicado = declarados
            .GroupBy(d => d.IdMedioPago)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicado is not null)
        {
            throw new ErrorDominio(
                "conteo_duplicado",
                "Declaraste el conteo de un mismo medio más de una vez.",
                400);
        }

        if (declarados.Any(d => d.ImporteDeclarado < 0))
        {
            throw new ErrorDominio(
                "conteo_invalido",
                "El conteo declarado no puede ser negativo.",
                400);
        }

        var idsArqueables = arqueables.Select(a => a.IdMedioPago).ToHashSet();
        var idsDeclarados = declarados.Select(d => d.IdMedioPago).ToHashSet();

        if (idsArqueables.Any(id => !idsDeclarados.Contains(id)))
        {
            throw new ErrorDominio(
                "arqueo_incompleto",
                "Falta declarar el conteo de al menos un medio con actividad en este turno.",
                400);
        }

        foreach (var idDeclarado in idsDeclarados)
        {
            if (idsArqueables.Contains(idDeclarado))
            {
                continue;
            }

            var esCuentaCorriente = actividad.Any(
                a => a.IdMedioPago == idDeclarado && a.Comportamiento == ComportamientoMedioPago.CuentaCorriente);

            throw esCuentaCorriente
                ? new ErrorDominio("medio_no_arqueable", "La cuenta corriente no es un medio arqueable.", 400)
                : new ErrorDominio(
                    "medio_sin_actividad_en_el_turno",
                    "Declaraste un conteo para un medio sin actividad en este turno.",
                    400);
        }
    }
}
