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
