using System.Text.RegularExpressions;
using Ways.Application.Organizacion;
using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Organizacion;

/// <summary>
/// Etapa 20, slice 4 — las tres filas <c>[S]</c> del task list que se afirman ESTRUCTURALMENTE,
/// sobre el archivo/el estado/la definición, y no se disfrazan de kill en runtime
/// (<c>mutation-proof-tests</c> regla 13), más la unidad del diccionario de etiquetas.
///
/// Por qué estructural y no una prueba viva:
/// <list type="bullet">
/// <item>"cero borrados físicos" es una propiedad del REPOSITORIO — no hay request que la pueda
/// observar, porque el borrado físico que no existe no deja rastro que consultar;</item>
/// <item>"los conjuntos de locks son disjuntos" es una propiedad del ORDEN de adquisición, y una
/// carrera de un solo recurso es ciega al orden: un deadlock no se puede forzar a voluntad
/// desde ADO.</item>
/// </list>
/// </summary>
public class BajasEstructuralesTests
{
    /// <summary>
    /// Los únicos <c>RemoveRange</c> que el repositorio tiene derecho a tener hoy, congelados por
    /// receptor. Los seis son reemplazos de conjuntos de DETALLE (ítems de un comprobante, filas
    /// de junction), no bajas de entidades: ninguno toca <c>tenants</c>, <c>empresas</c>,
    /// <c>puntos_venta</c> ni <c>usuarios</c>. La lista está congelada a propósito — un borrado
    /// físico nuevo, sea donde sea, pone esta prueba en rojo y obliga a justificarlo.
    /// </summary>
    private static readonly string[] RemoveRangePermitidos =
    [
        "ArticulosEmpresas",
        "ItemsComprobanteCompra",
        "ItemsOrdenCompra",
        "ItemsPresupuesto",
        "ItemsRemito",
        "OfertasListas",
    ];

    /// <summary>Los cuatro DbSet de organización: nada de lo que esta etapa toca puede aparecer
    /// como receptor de un borrado físico.</summary>
    private static readonly string[] TablasDeOrganizacion = ["Tenants", "Empresas", "PuntosVenta", "Usuarios"];

    private static IReadOnlyList<(string Archivo, string Contenido)> LeerFuentesDeProduccion()
    {
        var src = Path.Combine(RaizDelRepositorio.Resolver(), "src");

        return
        [
            .. Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                .Where(archivo => !archivo.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    && !archivo.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Select(archivo => (Path.GetRelativePath(src, archivo), File.ReadAllText(archivo)))
        ];
    }

    /// <summary>
    /// Cláusula B1 / BO-R1 / criterio de verificación V4: CERO borrados físicos sobre
    /// <c>tenants</c>, <c>empresas</c>, <c>puntos_venta</c> y <c>usuarios</c>. La baja es un
    /// <c>UPDATE ... SET deleted_at</c> y nada más.
    ///
    /// El barrido es del repositorio entero y no solo del diff de esta slice: lo que hay que
    /// sostener es la propiedad, no el cambio. <c>ExecuteDelete</c>, <c>ExecuteDeleteAsync</c>,
    /// <c>.Remove(</c> y <c>DELETE FROM</c> tienen que estar ausentes por completo; los
    /// <c>RemoveRange</c> se comparan contra la lista congelada de receptores permitidos.
    /// </summary>
    [Fact]
    public void NingunCaminoDeProduccionBorraFisicamenteFilasDeOrganizacion()
    {
        var fuentes = LeerFuentesDeProduccion();

        Assert.NotEmpty(fuentes);

        foreach (var patron in new[] { "ExecuteDelete", "ExecuteDeleteAsync", ".Remove(" })
        {
            var encontrados = fuentes
                .Where(fuente => fuente.Contenido.Contains(patron, StringComparison.Ordinal))
                .Select(fuente => fuente.Archivo)
                .ToList();

            Assert.Empty(encontrados);
        }

        var conDeleteFrom = fuentes
            .Where(fuente => Regex.IsMatch(
                fuente.Contenido, @"DELETE\s+FROM", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)))
            .Select(fuente => fuente.Archivo)
            .ToList();

        Assert.Empty(conDeleteFrom);

        var receptores = fuentes
            .SelectMany(fuente => Regex
                .Matches(fuente.Contenido, @"db\.(\w+)\.RemoveRange\(", RegexOptions.None, TimeSpan.FromSeconds(5))
                .Select(coincidencia => coincidencia.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(RemoveRangePermitidos, receptores);
        Assert.Empty(receptores.Intersect(TablasDeOrganizacion, StringComparer.Ordinal));
    }

    /// <summary>
    /// Cláusula (design G): los conjuntos de locks de las bajas y del camino operativo son
    /// DISJUNTOS, así que no hay deadlock expresable contra una venta, una compra o un cierre de
    /// caja. Las bajas toman un <c>pg_advisory_xact_lock</c> más los locks de fila de las cuatro
    /// tablas de organización, y NINGUNA de esas cuatro aparece en el orden total del programa
    /// (<c>numeraciones_fiscales → turnos_caja → comprobantes_venta → presupuestos → remitos →
    /// lotes → stock/stock_lotes → clientes → INSERT del ledger</c>).
    ///
    /// Se afirma leyendo el CUERPO de los tres métodos de baja: todo <c>db.&lt;Set&gt;</c> que
    /// mencionan tiene que ser de organización. Si mañana una baja tocara <c>db.TurnosCaja</c>
    /// —el mecanismo por el que la propiedad se rompería— esta prueba se pone en rojo antes de
    /// que exista el deadlock.
    /// </summary>
    [Fact]
    public void LasBajasDeOrganizacionSoloTocanTablasDeOrganizacion()
    {
        var servicio = File.ReadAllText(Path.Combine(
            RaizDelRepositorio.Resolver(), "src", "Ways.Application", "Organizacion", "ServicioDeOrganizacion.cs"));

        string[] metodos = ["EliminarTenantAsync", "EliminarEmpresaAsync", "EliminarPuntoVentaAsync"];

        foreach (var metodo in metodos)
        {
            var cuerpo = CuerpoDelMetodo(servicio, metodo);

            var conjuntos = Regex
                .Matches(cuerpo, @"db\.(\w+)\.", RegexOptions.None, TimeSpan.FromSeconds(5))
                .Select(coincidencia => coincidencia.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .Where(conjunto => conjunto is not "Database" and not "Model")
                .Order(StringComparer.Ordinal)
                .ToList();

            Assert.NotEmpty(conjuntos);
            Assert.Empty(conjuntos.Except(TablasDeOrganizacion, StringComparer.Ordinal));
        }
    }

    /// <summary>Extrae el cuerpo de un método por conteo de llaves desde su firma. Alcanza y sobra
    /// para un archivo del repositorio, y evita meter un parser de C# en una prueba.</summary>
    private static string CuerpoDelMetodo(string fuente, string nombre)
    {
        var inicio = fuente.IndexOf($"public async Task {nombre}(", StringComparison.Ordinal);
        Assert.True(inicio >= 0, $"No se encontró el método {nombre}.");

        var apertura = fuente.IndexOf('{', inicio);
        var profundidad = 0;

        for (var i = apertura; i < fuente.Length; i++)
        {
            profundidad += fuente[i] switch { '{' => 1, '}' => -1, _ => 0 };

            if (profundidad == 0)
            {
                return fuente[apertura..(i + 1)];
            }
        }

        throw new InvalidOperationException($"El método {nombre} no cierra sus llaves.");
    }

    // ---- el diccionario de etiquetas (task 4.27, BO-R11) --------------------------------------

    /// <summary>Cláusula: una tabla mapeada rinde su palabra en castellano y una sin mapear
    /// degrada a la frase genérica — nunca al nombre físico de la tabla, que no le dice nada al
    /// operador.</summary>
    [Theory]
    [InlineData("comprobantes_venta", "ventas")]
    [InlineData("articulos", "artículos")]
    [InlineData("turnos_caja", "turnos de caja")]
    [InlineData("puntos_venta", "puntos de venta")]
    [InlineData("arqueos_turno", EtiquetasDeTablas.Generica)]
    [InlineData("numeraciones_articulos", EtiquetasDeTablas.Generica)]
    [InlineData("una_tabla_que_no_existe", EtiquetasDeTablas.Generica)]
    public void CadaTablaMapeadaRindeSuPalabraYLaNoMapeadaDegradaALaGenerica(string tabla, string esperado) =>
        Assert.Equal(esperado, EtiquetasDeTablas.Describir(tabla));

    /// <summary>
    /// Cláusula: una rama PUENTEADA no puede rendir la etiqueta pelada de la hoja (entrada de
    /// judgment-day de la slice 3, item 2). El inspector devuelve siempre la tabla HOJA, así que
    /// sin esto una empresa bloqueada por un turno de caja de su punto de venta le diría al
    /// operador "turnos de caja" y lo mandaría a buscarlos en la empresa, donde no están.
    ///
    /// Las tres asignaciones posibles se prueban por separado: todas puenteadas ⇒ se nombra el
    /// puente; ninguna ⇒ etiqueta pelada; MIXTA ⇒ etiqueta pelada, porque el inspector no dice
    /// cuál de las dos ramas matcheó y afirmar el puente sería inventar un origen.
    /// </summary>
    [Fact]
    public void UnaRamaPuenteadaNombraElPuenteYUnaMixtaNoLoAfirma()
    {
        var puente = new PuenteDeUso("public", "puntos_venta", ["id_punto_venta"], ["id_empresa"]);

        var puenteada = new RamaDeUso(
            "public", "turnos_caja", ["id_punto_venta"], ["Id"],
            ClasificacionDeDependiente.Marcado, puente);

        var directa = new RamaDeUso(
            "public", "marcas", ["id_empresa"], ["Id"], ClasificacionDeDependiente.Marcado);

        var directaDeLaMismaHoja = new RamaDeUso(
            "public", "turnos_caja", ["id_empresa"], ["Id"], ClasificacionDeDependiente.Marcado);

        Assert.Equal(
            "turnos de caja en sus puntos de venta",
            EtiquetasDeTablas.DescribirBloqueo("turnos_caja", [puenteada, directa]));

        Assert.Equal("marcas", EtiquetasDeTablas.DescribirBloqueo("marcas", [puenteada, directa]));

        Assert.Equal(
            "turnos de caja",
            EtiquetasDeTablas.DescribirBloqueo("turnos_caja", [puenteada, directaDeLaMismaHoja]));

        // Una hoja puenteada SIN etiqueta propia degrada la palabra pero conserva el puente: el
        // operador sigue sabiendo dónde buscar.
        var sinEtiqueta = new RamaDeUso(
            "public", "arqueos_turno", ["id_punto_venta"], ["Id"],
            ClasificacionDeDependiente.SinMarca, puente);

        Assert.Equal(
            $"{EtiquetasDeTablas.Generica} en sus puntos de venta",
            EtiquetasDeTablas.DescribirBloqueo("arqueos_turno", [sinEtiqueta]));
    }
}
