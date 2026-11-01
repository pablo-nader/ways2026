namespace Ways.Domain.Caja;

/// <summary>
/// Marca de conciliación de la tesorería de una empresa (doc 10 §7): un Admin revisó la tesorería hasta <see cref="Hasta"/> y todo
/// lo que alimenta el libro de tesorería con fecha menor o igual queda congelado. Append-only: la marca vigente de una empresa es
/// la de mayor <see cref="Hasta"/>, y las anteriores quedan como historia. A propósito NO hereda de
/// <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/>, mismo criterio que <see cref="MovimientoTesoreria"/>, con
/// filtro de tenant escrito a mano en <c>WaysDbContext.AplicarFiltroDeTenantEnConciliacionTesoreria</c>.
/// </summary>
public class ConciliacionTesoreria
{
    public int Id { get; set; }
    public int IdTenant { get; set; }
    public int IdEmpresa { get; set; }

    /// <summary>Instante hasta el cual la tesorería está conciliada, inclusive.</summary>
    public DateTimeOffset Hasta { get; set; }

    public int IdUsuario { get; set; }
    public DateTimeOffset CreadoEl { get; set; }
    public string? Nota { get; set; }
}
