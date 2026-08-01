using Ways.Domain.Common;

namespace Ways.Domain.Organizacion;

/// <summary>
/// El cliente que contrata el sistema. Es la unidad de aislamiento: sus datos jamás se
/// mezclan con los de otro tenant (doc 09).
///
/// No hereda de <see cref="EntidadTenant"/>: su propia <see cref="Id"/> ES el alcance de
/// tenant, no tiene un <c>IdTenant</c> que apunte a otro lado.
/// </summary>
public class Tenant : EntidadBase
{
    public int Id { get; set; }

    public required string Nombre { get; set; }

    public EstadoTenant Estado { get; set; } = EstadoTenant.Activo;

    public bool PuedeOperar => Estado == EstadoTenant.Activo && DeletedAt is null;
}
