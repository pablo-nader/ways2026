namespace Ways.Domain.Clientes;

/// <summary>
/// Contador atómico de <c>clientes.numero</c> por tenant (design decision 2, doc 09
/// <c>numeraciones_comprobante</c> family): <c>id_tenant</c> ES la PK, no una FK opcional —
/// exactamente un contador por tenant, nunca cero ni más de uno. No hereda de
/// <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/> a propósito: no tiene
/// baja lógica (el contador de un tenant vive mientras el tenant exista) y su identidad no
/// necesita una PK separada como el resto de las tablas de tenant.
/// <see cref="Application.Clientes.AsignadorDeNumeroCliente"/> es el único punto de escritura:
/// lee/actualiza esta fila con SQL crudo dentro de la transacción de creación (design
/// decision 3), nunca vía <c>SaveChangesAsync</c>.
/// </summary>
public class NumeracionCliente
{
    public int IdTenant { get; set; }

    public int ProximoNumero { get; set; } = 1;
}
