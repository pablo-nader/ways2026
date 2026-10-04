using System.Text.RegularExpressions;
using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Familias;

/// <summary>
/// Quién escribe las filas que una familia de artículos tiene que mantener idénticas (doc 10 §3), afirmado
/// sobre el TEXTO FUENTE de <c>src/</c>: la lista de escritores que doc 10 da en "Estado (familias de
/// artículos)" es exacta mientras estas pruebas pasen. Un escritor nuevo —de campos compartidos de
/// <c>articulos</c>, de la pertenencia a una familia o de <c>precios</c>— rompe la prueba correspondiente, y
/// quien lo agrega tiene que hacerlo respetar el protocolo de locks y replicar a la familia (o decidir por
/// escrito por qué no), actualizar doc 10 y recién entonces agregarlo a la lista de acá.
///
/// <para>Los patrones son deliberadamente amplios (cualquier receptor, cualquier variable): un falso positivo
/// obliga a mirar el archivo, que es justamente el punto.</para>
/// </summary>
public class EscritoresDeFamiliasEstructuralesTests
{
    private static readonly string[] CarpetasIgnoradas = ["Migraciones", "obj", "bin"];

    private static IEnumerable<string> ArchivosFuente()
    {
        var src = Path.Combine(RaizDelRepositorio.Resolver(), "src");

        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(ruta => !ruta.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(parte => CarpetasIgnoradas.Contains(parte)));
    }

    /// <summary>Los archivos de <c>src/</c> (ruta relativa a <c>src/</c>, con <c>/</c>) cuyo texto SIN
    /// comentarios de línea contiene el patrón.</summary>
    private static List<string> ArchivosQueContienen(Regex patron)
    {
        var src = Path.Combine(RaizDelRepositorio.Resolver(), "src");

        return ArchivosFuente()
            .Where(ruta => patron.IsMatch(Regex.Replace(File.ReadAllText(ruta), @"//[^\r\n]*", string.Empty)))
            .Select(ruta => Path.GetRelativePath(src, ruta).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>La única clase que inserta, cierra o borra filas de <c>precios</c> es
    /// <c>ServicioDePrecios</c>: tanto la escritura de un precio como la copia del estado de precios al artículo
    /// que entra a una familia (<c>CopiarEstadoDePreciosAlNuevoMiembroAsync</c>) viven ahí, y ninguna otra clase
    /// toca la tabla.</summary>
    [Fact]
    public void ElUnicoEscritorDePreciosEsServicioDePrecios()
    {
        var escritores = ArchivosQueContienen(new Regex(
            @"\.Precios\.(Add|AddRange|Remove|RemoveRange|Update|Attach)\(|\b(INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+precios\b",
            RegexOptions.None, TimeSpan.FromSeconds(10)));

        Assert.Equal(["Ways.Application/Precios/ServicioDePrecios.cs"], escritores);
    }

    /// <summary>Los archivos que escriben filas de <c>articulos</c>: dan de alta artículos, escriben alguno de
    /// los campos compartidos o la pertenencia. Cada uno es un escritor que doc 10 enumera:
    /// <list type="bullet">
    /// <item><c>ServicioDeArticulos</c>: la edición (<c>ActualizarAsync</c>) y el alta
    /// (<c>CrearAsync</c>, con <c>idFamilia</c>) respetan el protocolo y replican a la familia.</item>
    /// <item><c>ServicioDePrecios</c>: la salida de la familia de "solo este" (<c>UPDATE articulos SET id_familia</c>).</item>
    /// <item><c>ServicioDeCompras</c>: la confirmación actualiza <c>costo_nominal</c>.</item>
    /// <item><c>ValoresCompartidosDeFamilia</c> (Domain): copia los trece campos sobre un artículo; la usa la
    /// edición.</item>
    /// </list></summary>
    [Fact]
    public void LosEscritoresDeCamposCompartidosYDePertenenciaDeArticulosSonLosQueDoc10Enumera()
    {
        var escritores = ArchivosQueContienen(new Regex(
            @"\b(INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+articulos\b|\bnew\s+Articulo\b|" +
            @"\.(CostoNominal|CostoLista|DescuentoProveedor|UnidadesPorBulto|IdProveedorHabitual|IdAlicuotaIva|UnidadVenta|EsProducto|ControlaLote|AcumulaEnVenta|IdFamilia)\s*=[^=>]",
            RegexOptions.None, TimeSpan.FromSeconds(10)));

        Assert.Equal(
            [
                "Ways.Application/Articulos/ServicioDeArticulos.cs",
                "Ways.Application/Compras/ServicioDeCompras.cs",
                "Ways.Application/Precios/ServicioDePrecios.cs",
                "Ways.Domain/Articulos/ValoresCompartidosDeFamilia.cs"
            ],
            escritores);
    }
}
