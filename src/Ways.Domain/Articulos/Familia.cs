using Ways.Domain.Common;

namespace Ways.Domain.Articulos;

/// <summary>
/// Familia de artículos (doc 10 §3): agrupa artículos que son SIEMPRE idénticos en sus campos
/// compartidos (<see cref="ValoresCompartidosDeFamilia"/>, incluido el estado de precios de las
/// listas fijas). La familia NO guarda ninguno de esos valores: sus miembros son la fuente de
/// verdad. Lo suyo es su identidad, su nombre, su estado (<see cref="Activo"/>) y las columnas
/// comunes de toda entidad (tenant, sellos de auditoría y baja lógica). Los lectores (listados,
/// POS, ventas, reportes) leen cada artículo tal cual; quienes escriben un campo compartido o un
/// precio deben replicar el cambio a todos los miembros en la misma transacción — el esquema no
/// fuerza la igualdad.
///
/// Es miembro de una familia todo artículo con <see cref="Articulo.IdFamilia"/> igual a
/// <see cref="Id"/> y sin baja lógica: un artículo dado de baja no cuenta como miembro para
/// ningún efecto. "Solo este" significa que el artículo SALE de la familia
/// (<see cref="Articulo.IdFamilia"/> en <c>null</c>); no hay excepciones dentro de una familia.
///
/// Tenant-wide, mismo alcance que <see cref="Articulo"/> (doc 09): <c>id_tenant</c>, sin
/// <c>id_empresa</c>.
/// </summary>
public class Familia : EntidadTenant
{
    public int Id { get; set; }

    public required string Nombre { get; set; }

    public bool Activo { get; set; } = true;
}
