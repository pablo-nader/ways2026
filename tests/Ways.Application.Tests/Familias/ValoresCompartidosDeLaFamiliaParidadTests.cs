using System.Reflection;
using Ways.Application.Familias;
using Ways.Domain.Articulos;

namespace Ways.Application.Tests.Familias;

/// <summary>
/// Paridad entre el DTO de lectura <see cref="ValoresCompartidosDeLaFamilia"/> (el detalle de una familia,
/// <c>GET /api/familias/{id}</c>, y lo que cambia al agrupar) y el registro de dominio
/// <see cref="ValoresCompartidosDeFamilia"/> (los campos que los miembros de una familia comparten). El DTO se arma campo
/// por campo —el detalle desde las columnas del artículo y agrupar desde el registro, con
/// <see cref="ValoresCompartidosDeLaFamilia.De"/>—, así que ningún chequeo del compilador los mantiene alineados: un
/// campo compartido nuevo en el dominio —<c>TodaPropiedadDeArticuloEstaClasificada</c> obliga a decidirlo— rompe estas
/// pruebas hasta que el DTO lo exponga, y lo mismo un nombre o un tipo que cambie de un solo lado. Las dos primeras
/// comparan las propiedades públicas por reflexión, sin importar el orden de declaración; la tercera, que <c>De</c> copia
/// cada campo en el suyo.
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

    /// <summary><see cref="ValoresCompartidosDeLaFamilia.De"/> copia cada campo del registro de dominio en el suyo. Las pruebas
    /// de agrupar arman lo que esperan con esta misma función, así que un cruce de dos campos ahí daría lo mismo de los dos
    /// lados: lo ve esta, con valores distintos entre sí en cada campo. Los tres booleanos no pueden serlo a la vez, así que
    /// hay una fila por cada uno con un único <c>true</c>: dos columnas cruzadas cambian la terna en alguna fila.</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void DeCopiaCadaCampoDelRegistroDeDominioEnSuPropioCampo(bool esProducto, bool controlaLote, bool acumulaEnVenta)
    {
        var dominio = new ValoresCompartidosDeFamilia(
            IdArea: 11, IdCategoria: 12, IdGrupo: 13, IdProveedorHabitual: 14, IdAlicuotaIva: 15,
            UnidadVenta: UnidadVenta.Peso, UnidadesPorBulto: 6.5m, EsProducto: esProducto, ControlaLote: controlaLote,
            AcumulaEnVenta: acumulaEnVenta, CostoLista: 50.25m, DescuentoProveedor: 10.5m, CostoNominal: 40.75m);

        var dto = ValoresCompartidosDeLaFamilia.De(dominio);

        Assert.Equal(
            ((int?)11, (int?)12, (int?)13, (int?)14, 15, UnidadVenta.Peso, (decimal?)6.5m, esProducto, controlaLote, acumulaEnVenta,
                (decimal?)50.25m, (decimal?)10.5m, (decimal?)40.75m),
            (dto.IdArea, dto.IdCategoria, dto.IdGrupo, dto.IdProveedorHabitual, dto.IdAlicuotaIva, dto.UnidadVenta,
                dto.UnidadesPorBulto, dto.EsProducto, dto.ControlaLote, dto.AcumulaEnVenta, dto.CostoLista, dto.DescuentoProveedor,
                dto.CostoNominal));
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
