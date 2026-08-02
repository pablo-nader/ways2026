using Ways.Domain.Common;

namespace Ways.Domain.Clientes;

/// <summary>
/// Protección del cliente Consumidor Final (design decision 4, spec: Consumidor Final
/// Protected Row). Regla de negocio pura, sin dependencias — se testea sin base de datos
/// (mirror de <see cref="Usuarios.PoliticaDeRoles"/>). El guard vive acá, no solo en la UI:
/// todo camino de edición/baja de <see cref="Cliente"/> tiene que pasar por acá antes de
/// tocar la fila. La constraint <c>ck_clientes_cf_protegido</c> es el backstop de esquema
/// para la baja (design decision 4) — esta regla cubre además la edición, que la constraint
/// no alcanza a bloquear.
///
/// Gap conocido, fuera de alcance de esta slice (judgment-day ronda 1, item de comentario):
/// <c>ck_clientes_cf_protegido</c> solo lee <c>numero</c> en el momento del UPDATE/DELETE —
/// un bypass de dos pasos (1: UPDATE que cambia <c>numero</c> de 1 a otro valor libre, 2:
/// DELETE de esa misma fila ya renumerada) esquiva tanto la constraint como esta regla, que
/// tampoco corre sobre un SQL directo. Ninguna de las dos escrituras de esa secuencia es en sí
/// misma la operación que la constraint prohíbe. El guard real contra ese bypass es el que va
/// a vivir en <c>ServicioDeClientes</c> (Slice 2, tasks.md 2A) — <see cref="ValidarNoConsumidorFinal"/>
/// ya se llama antes de cualquier UPDATE que la Slice 2 emita, así que el camino de servicio
/// nunca llega a habilitar el primer paso del bypass. Cerrarlo a nivel de esquema (p.ej. un
/// trigger que también valide el valor ANTERIOR de <c>numero</c>) queda fuera de esta slice.
/// </summary>
public static class ReglaDeClientes
{
    /// <summary><c>clientes.numero = 1</c> es, por construcción, siempre el Consumidor
    /// Final de su tenant (provisionado o backfilleado, nunca asignado por el flujo normal
    /// de alta — <see cref="Application.Clientes.AsignadorDeNumeroCliente"/> solo lo entrega
    /// una vez, a la primera asignación de un contador recién creado).</summary>
    public const int NumeroConsumidorFinal = 1;

    public static bool EsConsumidorFinal(int numero) => numero == NumeroConsumidorFinal;

    /// <summary>Rechaza editar o eliminar la fila Consumidor Final. Se llama antes de
    /// aplicar cualquier cambio de edición/baja sobre un cliente existente (alta queda
    /// afuera: un cliente recién creado nunca puede tener <c>numero = 1</c>, ese valor está
    /// reservado al bootstrap de aprovisionamiento/backfill).</summary>
    public static void ValidarNoConsumidorFinal(int numero)
    {
        if (EsConsumidorFinal(numero))
        {
            throw new ErrorDominio(
                "consumidor_final_protegido",
                "El cliente Consumidor Final no se puede editar ni eliminar.",
                409);
        }
    }
}
