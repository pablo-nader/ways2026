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
    /// sostener es la propiedad, no el cambio. <c>ExecuteDelete</c>/<c>ExecuteDeleteAsync</c> son
    /// nombres propios de EF y se buscan como substring; <c>Remove(</c> y <c>DELETE FROM</c> van
    /// ANCLADOS (judgment-day ronda 1, hallazgo C6), porque el substring pelado convertía en
    /// borrado físico a cualquier <c>List.Remove</c>/<c>string.Remove</c> de la BCL y a cualquier
    /// <c>DELETE FROM</c> de una migración histórica — un trip-wire que se pone rojo por motivos
    /// que no son el suyo se desactiva solo. <c>Remove(</c> se ancla con el receptor congelado,
    /// igual que <c>RemoveRange</c> (<c>db.&lt;Set&gt;.Remove(</c>, cero receptores permitidos), y
    /// <c>DELETE FROM</c> se limita a lo que NO es una migración: una migración vieja es historia
    /// ya aplicada, no un camino de producción que alguien pueda ejecutar hoy.
    /// </summary>
    [Fact]
    public void NingunCaminoDeProduccionBorraFisicamenteFilasDeOrganizacion()
    {
        var fuentes = LeerFuentesDeProduccion();

        Assert.NotEmpty(fuentes);

        foreach (var patron in new[] { "ExecuteDelete", "ExecuteDeleteAsync" })
        {
            var encontrados = fuentes
                .Where(fuente => fuente.Contenido.Contains(patron, StringComparison.Ordinal))
                .Select(fuente => fuente.Archivo)
                .ToList();

            Assert.Empty(encontrados);
        }

        var receptoresDeRemove = ReceptoresDe(fuentes, @"db\.(\w+)\.Remove\(");

        Assert.Empty(receptoresDeRemove);

        var conDeleteFrom = fuentes
            .Where(fuente => !EsMigracion(fuente.Archivo))
            .Where(fuente => Regex.IsMatch(
                fuente.Contenido, @"DELETE\s+FROM", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)))
            .Select(fuente => fuente.Archivo)
            .ToList();

        Assert.Empty(conDeleteFrom);

        var receptores = ReceptoresDe(fuentes, @"db\.(\w+)\.RemoveRange\(");

        Assert.Equal(RemoveRangePermitidos, receptores);
        Assert.Empty(receptores.Intersect(TablasDeOrganizacion, StringComparer.Ordinal));
    }

    private static List<string> ReceptoresDe(
        IReadOnlyList<(string Archivo, string Contenido)> fuentes, string patron) =>
        [.. fuentes
            .SelectMany(fuente => Regex
                .Matches(fuente.Contenido, patron, RegexOptions.None, TimeSpan.FromSeconds(5))
                .Select(coincidencia => coincidencia.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static bool EsMigracion(string archivoRelativo) =>
        archivoRelativo.Contains($"{Path.DirectorySeparatorChar}Migraciones{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)
        || archivoRelativo.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal);

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
    ///
    /// LA CUARTA TABLA QUE LAS BAJAS TOCAN ES <c>auditoria</c>, y se declara acá para que el
    /// enunciado de arriba siga siendo verdadero (judgment-day ronda 1, hallazgo C1). No entra por
    /// <c>db.&lt;Set&gt;</c> —la escribe <c>ServicioDeAuditoria.Registrar</c>, encolada en el mismo
    /// <c>SaveChangesAsync</c>— y no rompe la disyunción: es un INSERT sobre una tabla que no
    /// aparece en el orden total del programa, y sus FKs <c>Restrict</c> toman <c>FOR KEY SHARE</c>
    /// sobre filas de organización que ESTA MISMA transacción ya tiene tomadas. Se afirma su
    /// presencia, no solo su inocuidad: los tres métodos tienen que registrar auditoría.
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

            // Las fábricas del rastro viven FUERA de los tres cuerpos, así que el enunciado "las
            // bajas escriben auditoría" se afirma en dos mitades: el cuerpo llama a una
            // `RegistrarBaja*` y el archivo declara al menos una que arma un RegistroDeAuditoria.
            Assert.Contains("RegistrarBaja", cuerpo, StringComparison.Ordinal);
            Assert.Contains("Registrar(new RegistroDeAuditoria(", servicio, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Cláusula (judgment-day ronda 1, hallazgo C2): en <c>ServicioDeUsuarios.EliminarAsync</c> el
    /// guard de uso corre BAJO el lock y DENTRO de la transacción, nunca como un SELECT suelto
    /// antes de abrirla. Es estructural y se dice que lo es (<c>mutation-proof-tests</c> regla 13):
    /// la carrera que cierra es que una venta o un turno del MISMO empleado se confirmen entre el
    /// guard y el estampado, y una prueba de un solo hilo no puede observar dónde está la línea.
    ///
    /// Se afirma el ORDEN de cuatro marcas dentro del método:
    /// <c>ValidarPuedeIntervenirSobre</c> (afuera, es dominio puro) →
    /// <c>CreateExecutionStrategy</c> → <c>TomarLockDeBajaAsync</c> →
    /// <c>PrimeraDependenciaEnUsoAsync</c> → <c>SaveChangesAsync</c>. Mover el guard afuera de la
    /// estrategia pone esto en rojo.
    /// </summary>
    [Fact]
    public void LaBajaDeUsuarioCorreSuGuardBajoElLockYDentroDeLaTransaccion()
    {
        var servicio = File.ReadAllText(Path.Combine(
            RaizDelRepositorio.Resolver(), "src", "Ways.Application", "Usuarios", "ServicioDeUsuarios.cs"));

        var cuerpo = CuerpoDelMetodo(servicio, "EliminarAsync");

        var politica = Posicion(cuerpo, "PoliticaDeRoles.ValidarPuedeIntervenirSobre(");
        var estrategia = Posicion(cuerpo, "CreateExecutionStrategy().ExecuteAsync(");
        var transaccion = Posicion(cuerpo, "BeginTransactionAsync(");
        var lock_ = Posicion(cuerpo, "TomarLockDeBajaAsync(");
        var guard = Posicion(cuerpo, "inspector.PrimeraDependenciaEnUsoAsync(");
        var estampado = Posicion(cuerpo, "usuario.DeletedAt = momento;");
        var guardado = Posicion(cuerpo, "db.SaveChangesAsync(ct)");

        Assert.True(politica < estrategia, "PoliticaDeRoles tiene que decidir antes de abrir nada.");
        Assert.True(estrategia < transaccion, "La transacción vive DENTRO de la estrategia de ejecución.");
        Assert.True(transaccion < lock_, "El lock se toma con la transacción ya abierta (xact scope).");
        Assert.True(lock_ < guard, "El guard de uso corre BAJO el lock, nunca antes.");
        Assert.True(guard < estampado, "El estampado va después del guard.");
        Assert.True(estampado < guardado, "El SaveChanges cierra la unidad completa.");
    }

    private static int Posicion(string cuerpo, string marca)
    {
        var indice = cuerpo.IndexOf(marca, StringComparison.Ordinal);
        Assert.True(indice >= 0, $"No se encontró '{marca}' en el cuerpo del método.");
        return indice;
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
    [InlineData("arqueos_turno", "arqueos de caja")]
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
    /// puente; ninguna ⇒ etiqueta pelada; MIXTA ⇒ TAMBIÉN se nombra el puente (judgment-day ronda
    /// 1, hallazgo C3). La regla anterior degradaba la mixta a la etiqueta pelada y esta prueba la
    /// congelaba como deliberada, cuando era justamente el caso que la entrada de la slice 3
    /// prohíbe: una hoja con rama directa Y puenteada —hoy <c>parametros</c>— perdía la única
    /// pista sobre dónde buscar. Nombrar el puente en la mixta no puede desorientar: la fila
    /// directa habría rendido la MISMA palabra de hoja por su propia rama.
    /// </summary>
    [Fact]
    public void UnaRamaPuenteadaNombraElPuenteInclusoCuandoLaHojaTambienLlegaDirecto()
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
            "turnos de caja en sus puntos de venta",
            EtiquetasDeTablas.DescribirBloqueo("turnos_caja", [puenteada, directaDeLaMismaHoja]));

        // Y también con la rama directa PRIMERO: la elección no puede depender del orden en que el
        // inventario devolvió las ramas.
        Assert.Equal(
            "turnos de caja en sus puntos de venta",
            EtiquetasDeTablas.DescribirBloqueo("turnos_caja", [directaDeLaMismaHoja, puenteada]));

        // Una hoja puenteada SIN etiqueta propia degrada la palabra pero conserva el puente: el
        // operador sigue sabiendo dónde buscar.
        var sinEtiqueta = new RamaDeUso(
            "public", "numeraciones_articulos", ["id_punto_venta"], ["Id"],
            ClasificacionDeDependiente.SinMarca, puente);

        Assert.Equal(
            $"{EtiquetasDeTablas.Generica} en sus puntos de venta",
            EtiquetasDeTablas.DescribirBloqueo("numeraciones_articulos", [sinEtiqueta]));
    }
}
