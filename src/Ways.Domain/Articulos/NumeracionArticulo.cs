namespace Ways.Domain.Articulos;

/// <summary>
/// Contador atómico de <c>articulos.codigo_interno</c> por tenant (design decision 6, mismo
/// shape que <see cref="Clientes.NumeracionCliente"/>): <c>id_tenant</c> ES la PK, no una FK
/// opcional — exactamente un contador por tenant, nunca cero ni más de uno. No hereda de
/// <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/> a propósito: sin baja
/// lógica, sin PK separada. <see cref="Application.Articulos.AsignadorDeCodigoInternoArticulo"/>
/// es el único punto de escritura: SQL crudo dentro de la transacción de creación, nunca vía
/// <c>SaveChangesAsync</c>.
/// </summary>
public class NumeracionArticulo
{
    public int IdTenant { get; set; }

    public int ProximoNumero { get; set; } = 1;
}
