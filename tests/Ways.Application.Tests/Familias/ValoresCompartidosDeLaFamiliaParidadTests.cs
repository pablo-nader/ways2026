using System.Reflection;
using Ways.Application.Familias;
using Ways.Domain.Articulos;

namespace Ways.Application.Tests.Familias;

/// <summary>
/// Paridad entre el DTO de lectura <see cref="ValoresCompartidosDeLaFamilia"/> (<c>GET /api/familias/{id}</c>) y el
/// registro de dominio <see cref="ValoresCompartidosDeFamilia"/> (los campos que los miembros de una familia
/// comparten). El DTO no se arma desde el registro sino campo por campo, así que ningún chequeo del compilador los
/// mantiene alineados: un campo compartido nuevo en el dominio —<c>TodaPropiedadDeArticuloEstaClasificada</c> obliga a
/// decidirlo— rompe estas pruebas hasta que el detalle de la familia lo exponga, y lo mismo un nombre o un tipo que
/// cambie de un solo lado. Comparan las propiedades públicas por reflexión, sin importar el orden de declaración.
/// </summary>
public class ValoresCompartidosDeLaFamiliaParidadTests
{
    /// <summary>Una propiedad pública: su nombre, su tipo sin el envoltorio <see cref="Nullable{T}"/> y si lo tenía.
    /// Todas las de los dos registros son de valor, así que la nulabilidad se lee del tipo.</summary>
    private sealed record Campo(string Nombre, Type Tipo, bool EsNulable);

    private static IReadOnlyList<Campo> CamposDe(Type tipo) =>
    [
        .. tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => new Campo(
                p.Name, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType,
                Nullable.GetUnderlyingType(p.PropertyType) is not null))
            .OrderBy(c => c.Nombre, StringComparer.Ordinal)
    ];

    /// <summary>Los mismos nombres de campo y, para cada uno, el mismo tipo (sin contar si admite <c>null</c>, que
    /// compara la prueba siguiente).</summary>
    [Fact]
    public void ElDtoDeLecturaTieneLosMismosCamposYTiposQueElRegistroDeDominio()
    {
        var dominio = CamposDe(typeof(ValoresCompartidosDeFamilia));
        var dto = CamposDe(typeof(ValoresCompartidosDeLaFamilia));

        Assert.Equal(dominio.Select(c => c.Nombre), dto.Select(c => c.Nombre));
        Assert.Equal(dominio.Select(c => (c.Nombre, c.Tipo)), dto.Select(c => (c.Nombre, c.Tipo)));
    }

    /// <summary>La única nulabilidad que difiere es la de <c>IdArea</c>: obligatoria en el artículo, el detalle la lee
    /// como <c>null</c> cuando el área está dada de baja. Los otros tres ids de catálogo ya admiten <c>null</c> en los
    /// dos lados, y ningún otro campo cambia de nulabilidad entre el registro y el DTO.</summary>
    [Fact]
    public void LaUnicaNulabilidadQueDifiereEntreElDtoYElRegistroEsLaDelArea()
    {
        var dominio = CamposDe(typeof(ValoresCompartidosDeFamilia));
        var dto = CamposDe(typeof(ValoresCompartidosDeLaFamilia));

        var distintos = dominio
            .Join(dto, c => c.Nombre, c => c.Nombre, (delDominio, delDto) =>
                (delDominio.Nombre, EnDominio: delDominio.EsNulable, EnDto: delDto.EsNulable))
            .Where(c => c.EnDominio != c.EnDto)
            .ToList();

        Assert.Equal([("IdArea", false, true)], distintos);
    }
}
