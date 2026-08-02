using Ways.Domain.Common;

namespace Ways.Domain.Catalogos;

/// <summary>
/// Base compartida de los catálogos auxiliares de tenant (ADR-11, design.md): áreas,
/// categorías, marcas, grupos y medios de pago. Ocho catálogos no deben convertirse en
/// ocho copias — acá vive lo que todos comparten (nombre, estado, el alcance opcional a
/// una empresa); cada catálogo agrega solo sus columnas propias en su propia clase.
///
/// <see cref="IdEmpresa"/> nullable es una regla de visibilidad, no de aislamiento
/// (ADR-10): <c>NULL</c> significa "compartido por todas las empresas del tenant", no
/// "sin tenant" — <see cref="Ways.Domain.Common.EntidadTenant.IdTenant"/> sigue siendo el
/// único filtro de aislamiento.
/// </summary>
public abstract class CatalogoSimple : EntidadTenant
{
    public int Id { get; set; }

    public required string Nombre { get; set; }

    public bool Activo { get; set; } = true;

    /// <summary><c>NULL</c> ⇒ compartido por todas las empresas del tenant. Un valor ⇒
    /// propio de esa empresa. FK compuesta opcional a <c>empresas</c> (ADR-9).</summary>
    public int? IdEmpresa { get; set; }
}
