using System.Text.RegularExpressions;
using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Familias;

/// <summary>
/// Una red de MEJOR ESFUERZO sobre el TEXTO FUENTE de <c>src/</c> para quién escribe las filas que una familia de
/// artículos tiene que mantener idénticas (doc 10 §3): la lista de escritores que doc 10 da en "Estado (familias de
/// artículos)" es la que esta red ve, no una garantía de que no haya otro. Un escritor nuevo que use alguna de las
/// formas de abajo rompe la prueba correspondiente, y quien lo agrega tiene que hacerlo respetar el protocolo de
/// locks y replicar a la familia (o decidir por escrito por qué no), actualizar doc 10 y recién entonces agregarlo
/// a la lista de acá.
///
/// <para><b>Qué ve.</b> Cada archivo <c>.cs</c> de <c>src/</c> —sin <c>bin</c>, <c>obj</c> ni <c>Migraciones</c> en
/// su ruta relativa a la raíz del repositorio— sin sus comentarios, con estas formas:
/// <list type="bullet">
/// <item>sentencias SQL <c>INSERT INTO</c>, <c>UPDATE</c> y <c>DELETE FROM</c> sobre <c>articulos</c> o
/// <c>precios</c>, con cualquier combinación de mayúsculas y con el nombre entre comillas;</item>
/// <item>las operaciones de escritura de <c>DbSet</c> sobre <c>Articulos</c> y <c>Precios</c> (<c>Add</c>,
/// <c>Remove</c>, <c>Update</c>, <c>Attach</c> y sus variantes <c>Range</c>), <c>Set&lt;Articulo&gt;()</c>,
/// <c>Set&lt;Precio&gt;()</c>, <c>new Articulo</c> y <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> encadenados a esos
/// <c>DbSet</c> en la misma sentencia;</item>
/// <item>un <c>SetProperty</c> sobre un campo compartido de <c>articulos</c>;</item>
/// <item>la asignación de <c>CostoNominal</c>, <c>CostoLista</c>, <c>DescuentoProveedor</c>,
/// <c>UnidadesPorBulto</c>, <c>IdProveedorHabitual</c>, <c>IdAlicuotaIva</c>, <c>UnidadVenta</c>,
/// <c>EsProducto</c>, <c>ControlaLote</c>, <c>AcumulaEnVenta</c> o <c>IdFamilia</c> sobre cualquier receptor.</item>
/// </list>
/// Los patrones son amplios a propósito (cualquier receptor, cualquier variable): un falso positivo obliga a
/// mirar el archivo, que es justamente el punto.</para>
///
/// <para><b>Qué NO ve.</b> Una escritura armada de otra manera: SQL cuyo nombre de tabla se compone por
/// concatenación o interpolación, <c>new()</c> con el tipo inferido, <c>ExecuteUpdate</c> sobre una consulta
/// guardada antes en una variable, un recurso que no sea un <c>.cs</c> de <c>src/</c> y cualquier código fuera de
/// <c>src/</c>. Tampoco ve la baja lógica de un artículo (<c>DeletedAt</c>), que cambia quién es miembro y no usa
/// ninguna de estas formas, ni la asignación suelta de <c>IdArea</c>, <c>IdCategoria</c> e <c>IdGrupo</c>: son
/// nombres que comparten ofertas y gastos, y las pruebas de <c>ValoresCompartidosDeFamilia</c> son las que
/// anclan el mapeo de las trece columnas. Un <c>//</c> dentro de un literal de cadena le quita a la red el resto de
/// esa línea.</para>
/// </summary>
public class EscritoresDeFamiliasEstructuralesTests
{
    private static readonly string[] CarpetasIgnoradas = ["Migraciones", "obj", "bin"];

    private static readonly TimeSpan LimiteDeTiempo = TimeSpan.FromSeconds(10);

    private const string OperacionesDeEscrituraDeDbSet = "Add|AddRange|Remove|RemoveRange|Update|UpdateRange|Attach|AttachRange";

    private const string CamposDeArticulosConOtrosUsos =
        "IdArea|IdCategoria|IdGrupo";

    private const string CamposDeArticulosSinOtrosUsos =
        "CostoNominal|CostoLista|DescuentoProveedor|UnidadesPorBulto|IdProveedorHabitual|IdAlicuotaIva|UnidadVenta|" +
        "EsProducto|ControlaLote|AcumulaEnVenta|IdFamilia";

    /// <summary>Una sentencia SQL de escritura sobre <paramref name="tabla"/>: sin distinguir mayúsculas y con el
    /// nombre, si se quiere, entre comillas (en una cadena común las comillas llegan al texto como <c>\"</c>, y en
    /// una textual como <c>""</c>). El <c>\b</c> final exige que el nombre termine ahí: <c>precios</c> no coincide
    /// con <c>precios_historial</c>.</summary>
    private static string SqlSobre(string tabla) =>
        $@"(?i:\b(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM)\s+[\\""]*{tabla}\b)";

    private static string OperacionDeDbSet(string conjunto) =>
        $@"\.{conjunto}\.(?:{OperacionesDeEscrituraDeDbSet})\(";

    private static string ConjuntoGenerico(string entidad) =>
        $@"\.Set<\s*{entidad}\s*>\s*\(";

    private static string EjecucionMasivaSobre(string conjunto) =>
        $@"\.{conjunto}\b[^;]*?\.Execute(?:Update|Delete)(?:Async)?\(";

    private static readonly string SetPropertySobreUnCampoCompartido =
        $@"\.SetProperty\(\s*\w+\s*=>\s*\w+\.(?:{CamposDeArticulosConOtrosUsos}|{CamposDeArticulosSinOtrosUsos})\b";

    private static readonly string AsignacionDeUnCampoCompartido =
        $@"\.(?:{CamposDeArticulosSinOtrosUsos})\s*=[^=>]";

    internal static readonly Regex EscritoresDePrecios = new(
        string.Join(
            "|", SqlSobre("precios"), OperacionDeDbSet("Precios"), ConjuntoGenerico("Precio"), EjecucionMasivaSobre("Precios")),
        RegexOptions.None, LimiteDeTiempo);

    internal static readonly Regex EscritoresDeArticulos = new(
        string.Join(
            "|", SqlSobre("articulos"), OperacionDeDbSet("Articulos"), ConjuntoGenerico("Articulo"),
            EjecucionMasivaSobre("Articulos"), @"\bnew\s+Articulo\b", SetPropertySobreUnCampoCompartido,
            AsignacionDeUnCampoCompartido),
        RegexOptions.None, LimiteDeTiempo);

    /// <summary>La ruta relativa a la raíz del repositorio es la que se compara con
    /// <see cref="CarpetasIgnoradas"/>: una raíz clonada bajo un directorio que se llame <c>bin</c> u <c>obj</c>
    /// no tiene que dejar la red sin archivos que mirar.</summary>
    internal static IEnumerable<string> ArchivosFuente(string raiz)
    {
        var src = Path.Combine(raiz, "src");

        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(ruta => !Path.GetRelativePath(raiz, ruta)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(parte => CarpetasIgnoradas.Contains(parte)));
    }

    private static string SinComentarios(string fuente) =>
        Regex.Replace(fuente, @"//[^\r\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline, LimiteDeTiempo);

    /// <summary>Los archivos de <c>src/</c> (ruta relativa a <c>src/</c>, con <c>/</c>) cuyo texto SIN
    /// comentarios contiene el patrón.</summary>
    private static List<string> ArchivosQueContienen(Regex patron)
    {
        var raiz = RaizDelRepositorio.Resolver();
        var src = Path.Combine(raiz, "src");

        return ArchivosFuente(raiz)
            .Where(ruta => patron.IsMatch(SinComentarios(File.ReadAllText(ruta))))
            .Select(ruta => Path.GetRelativePath(src, ruta).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>La única clase de <c>src/</c> en la que la red ve una escritura de <c>precios</c> es
    /// <c>ServicioDePrecios</c>: tanto la escritura de un precio como la copia del estado de precios al artículo
    /// que entra a una familia (<c>CopiarEstadoDePreciosAlNuevoMiembroAsync</c>) viven ahí.</summary>
    [Fact]
    public void ElUnicoEscritorDePreciosQueVeLaRedEsServicioDePrecios()
    {
        var escritores = ArchivosQueContienen(EscritoresDePrecios);

        Assert.Equal(["Ways.Application/Precios/ServicioDePrecios.cs"], escritores);
    }

    /// <summary>Los archivos en los que la red ve una escritura de filas de <c>articulos</c>: altas de artículos,
    /// escrituras de alguno de los campos compartidos o de la pertenencia. Cada uno es un escritor que doc 10
    /// enumera:
    /// <list type="bullet">
    /// <item><c>ServicioDeArticulos</c>: la edición (<c>ActualizarAsync</c>) y el alta
    /// (<c>CrearAsync</c>, con <c>idFamilia</c>) respetan el protocolo y replican a la familia.</item>
    /// <item><c>ServicioDePrecios</c>: la salida de la familia de "solo este" (<c>UPDATE articulos SET id_familia</c>).</item>
    /// <item><c>ServicioDeCompras</c>: la confirmación actualiza <c>costo_nominal</c>.</item>
    /// <item><c>ValoresCompartidosDeFamilia</c> (Domain): copia los trece campos sobre un artículo; la usa la
    /// edición.</item>
    /// </list></summary>
    [Fact]
    public void LosEscritoresDeArticulosQueVeLaRedSonLosQueDoc10Enumera()
    {
        var escritores = ArchivosQueContienen(EscritoresDeArticulos);

        Assert.Equal(
            [
                "Ways.Application/Articulos/ServicioDeArticulos.cs",
                "Ways.Application/Compras/ServicioDeCompras.cs",
                "Ways.Application/Precios/ServicioDePrecios.cs",
                "Ways.Domain/Articulos/ValoresCompartidosDeFamilia.cs"
            ],
            escritores);
    }

    // =================================================================================================
    // Qué ve cada patrón: una fila por forma declarada en la documentación de la clase
    // =================================================================================================

    [Theory]
    [InlineData("comando.CommandText = \"UPDATE precios SET precio = $1\";")]
    [InlineData("comando.CommandText = \"update precios set precio = $1\";")]
    [InlineData("comando.CommandText = \"Delete   From precios WHERE id_precio = $1\";")]
    [InlineData("comando.CommandText = \"INSERT INTO \\\"precios\\\" (precio) VALUES ($1)\";")]
    [InlineData("comando.CommandText = @\"INSERT INTO \"\"precios\"\" (precio) VALUES ($1)\";")]
    [InlineData("db.Precios.Add(precio);")]
    [InlineData("db.Precios.RemoveRange(filas);")]
    [InlineData("db.Precios.UpdateRange(filas);")]
    [InlineData("db.Set<Precio>().Add(precio);")]
    [InlineData("await db.Precios.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Monto, 1m), ct);")]
    [InlineData("await db.Precios\n    .Where(p => p.Id == id)\n    .ExecuteDeleteAsync(ct);")]
    public void ElPatronDePreciosVeCadaFormaDeEscrituraQueDeclara(string fuente)
    {
        Assert.Matches(EscritoresDePrecios, fuente);
    }

    [Theory]
    [InlineData("comando.CommandText = \"UPDATE precios_historial SET precio = $1\";")]
    [InlineData("comando.CommandText = \"SELECT precio FROM precios WHERE id_precio = $1\";")]
    [InlineData("var filas = await db.Precios.Where(p => p.Id == id).ToListAsync(ct);")]
    [InlineData("db.PreciosVigentes.Add(precio);")]
    public void ElPatronDePreciosNoVeLecturasNiOtrasTablas(string fuente)
    {
        Assert.DoesNotMatch(EscritoresDePrecios, fuente);
    }

    [Theory]
    [InlineData("comando.CommandText = \"UPDATE articulos SET costo_nominal = $1\";")]
    [InlineData("comando.CommandText = \"update articulos set costo_nominal = $1\";")]
    [InlineData("comando.CommandText = \"Insert   Into articulos (nombre) VALUES ($1)\";")]
    [InlineData("comando.CommandText = \"DELETE FROM \\\"articulos\\\" WHERE id_articulo = $1\";")]
    [InlineData("comando.CommandText = @\"UPDATE \"\"articulos\"\" SET id_familia = NULL\";")]
    [InlineData("db.Articulos.Add(articulo);")]
    [InlineData("db.Articulos.Attach(articulo);")]
    [InlineData("db.Articulos.RemoveRange(filas);")]
    [InlineData("db.Set<Articulo>().Add(articulo);")]
    [InlineData("var articulo = new Articulo { Nombre = nombre };")]
    [InlineData("await db.Articulos.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Nombre, n), ct);")]
    [InlineData("await db.Articulos\n    .Where(a => a.Id == id)\n    .ExecuteDeleteAsync(ct);")]
    [InlineData("await consulta.ExecuteUpdateAsync(s => s.SetProperty(a => a.IdCategoria, 3), ct);")]
    [InlineData("await consulta.ExecuteUpdateAsync(s => s.SetProperty(a => a.CostoNominal, 3m), ct);")]
    [InlineData("await consulta.ExecuteUpdateAsync(s => s.SetProperty(a => a.AcumulaEnVenta, false), ct);")]
    [InlineData("articulo.CostoNominal = costo;")]
    [InlineData("articulo.AcumulaEnVenta = false;")]
    [InlineData("articulo.IdFamilia = null;")]
    public void ElPatronDeArticulosVeCadaFormaDeEscrituraQueDeclara(string fuente)
    {
        Assert.Matches(EscritoresDeArticulos, fuente);
    }

    [Theory]
    [InlineData("comando.CommandText = \"INSERT INTO articulos_empresas (id_articulo) VALUES ($1)\";")]
    [InlineData("comando.CommandText = \"SELECT id_articulo FROM articulos WHERE id_familia = $1\";")]
    [InlineData("var filas = await db.Articulos.Where(a => a.IdFamilia == id).ToListAsync(ct);")]
    [InlineData("var vigente = a.CostoNominal == costo;")]
    [InlineData("db.ArticulosEmpresas.Add(fila);")]
    [InlineData("var fila = new ArticuloEmpresa { IdArticulo = id };")]
    public void ElPatronDeArticulosNoVeLecturasNiOtrasTablas(string fuente)
    {
        Assert.DoesNotMatch(EscritoresDeArticulos, fuente);
    }

    /// <summary>El filtro de carpetas ignoradas se aplica a la ruta RELATIVA a la raíz del repositorio. Una raíz
    /// clonada bajo un directorio que se llama <c>obj</c> conserva sus archivos de <c>src/</c>; los de una carpeta
    /// <c>obj</c>, <c>bin</c> o <c>Migraciones</c> dentro de <c>src/</c> siguen ignorados.</summary>
    [Fact]
    public void LasCarpetasIgnoradasSeMidenDesdeLaRaizDelRepositorioYNoSobreLaRutaAbsoluta()
    {
        var contenedor = Path.Combine(Path.GetTempPath(), $"escritores-{Guid.NewGuid():N}");
        var raiz = Path.Combine(contenedor, "obj", "repo");

        try
        {
            var archivos = new[]
            {
                Path.Combine(raiz, "src", "Proyecto", "Escritor.cs"),
                Path.Combine(raiz, "src", "Proyecto", "obj", "Ignorado.cs"),
                Path.Combine(raiz, "src", "Proyecto", "bin", "Ignorado.cs"),
                Path.Combine(raiz, "src", "Proyecto", "Migraciones", "Ignorado.cs")
            };

            foreach (var archivo in archivos)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(archivo)!);
                File.WriteAllText(archivo, "class C { }");
            }

            Assert.Equal([archivos[0]], ArchivosFuente(raiz).ToList());
        }
        finally
        {
            Directory.Delete(contenedor, recursive: true);
        }
    }

    [Theory]
    [InlineData("var a = 1; // db.Precios.Add(x);")]
    [InlineData("/* db.Precios.Add(x); */ var a = 1;")]
    [InlineData("/*\n   UPDATE precios SET precio = 1\n*/")]
    public void LosComentariosNoCuentanComoEscrituras(string fuente)
    {
        Assert.DoesNotMatch(EscritoresDePrecios, SinComentarios(fuente));
    }
}
