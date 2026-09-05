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
    /// nombres propios de EF y se buscan como substring; <c>Remove(</c> va ANCLADO (judgment-day
    /// ronda 1, hallazgo C6), porque el substring pelado convertía en borrado físico a cualquier
    /// <c>List.Remove</c>/<c>string.Remove</c> de la BCL — un trip-wire que se pone rojo por
    /// motivos que no son el suyo se desactiva solo.
    ///
    /// El anclaje de la ronda 1 quedó DEMASIADO angosto y la ronda 2 lo corrige (hallazgo R2-5).
    /// <c>db\.(\w+)\.Remove\(</c> exigía que el receptor se llamara exactamente <c>db</c>, así
    /// que no veía <c>dbPlataforma.Usuarios.Remove(</c> —y <c>dbPlataforma</c> es un contexto REAL
    /// inyectado, <c>ServicioDeUsuarios.cs:30</c>—, ni <c>context.X.Remove(</c>, ni
    /// <c>db.Set&lt;T&gt;().Remove(</c>, ni <c>db.Remove(entidad)</c> a secas. Ahora son tres
    /// patrones, los tres con el receptor congelado en el conjunto vacío: cualquier identificador
    /// que contenga <c>db</c>/<c>Db</c>, la forma <c>Set&lt;T&gt;()</c> y el <c>Remove</c> no
    /// tipado del propio contexto.
    ///
    /// Y <c>DELETE FROM</c> vuelve a barrer TODOS los archivos, migraciones incluidas: una
    /// migración NUEVA es un camino de producción que se va a ejecutar, no historia ya aplicada.
    /// El carve-out de la ronda 1 no costaba nada porque hoy hay CERO ocurrencias en todo
    /// <c>src/</c> —migraciones incluidas—, así que no hay ninguna lista que congelar.
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

        // Los tres anclajes de `Remove(`, con el receptor congelado en el conjunto VACÍO: un
        // contexto con cualquier nombre que contenga `db`/`Db` (`dbPlataforma`, `_db`, `miDb`),
        // la forma no tipada `Set<T>()` y el `Remove` del contexto mismo.
        Assert.Empty(ReceptoresDe(fuentes, @"\b\w*[dD]b\w*\.(\w+)\.Remove\("));
        Assert.Empty(ReceptoresDe(fuentes, @"\bSet<(\w+)>\(\)\.Remove\("));
        Assert.Empty(ReceptoresDe(fuentes, @"\b(\w*[dD]b\w*)\.Remove\("));

        var conDeleteFrom = fuentes
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
    /// Cláusula (judgment-day ronda 2, hallazgo R2-1): las TRES bajas de organización corren bajo
    /// la estrategia SIN REINTENTO, y ninguna vuelve a la reintentable. Es la mitad estructural que
    /// cubre las tres a la vez; la mitad conductual —el reintento inducido de verdad con un
    /// interceptor que tira un error transitorio sobre el INSERT de <c>auditoria</c>— vive en
    /// <c>BajasDeOrganizacionTests</c> y solo puede ejercitar un camino por prueba.
    ///
    /// El mecanismo por el que la propiedad se rompería es un cambio de una palabra
    /// (<c>EnUnaTransaccionDeBajaAsync</c> → <c>EnUnaTransaccionAsync</c>), y el daño no se ve en
    /// la fila que la baja escribe sino en las que DUPLICA: por eso se afirma acá, sobre el
    /// archivo, y no solo en el camino que la prueba conductual alcanza.
    /// </summary>
    [Fact]
    public void LasTresBajasDeOrganizacionCorrenBajoLaEstrategiaSinReintento()
    {
        var servicio = File.ReadAllText(Path.Combine(
            RaizDelRepositorio.Resolver(), "src", "Ways.Application", "Organizacion", "ServicioDeOrganizacion.cs"));

        string[] metodos = ["EliminarTenantAsync", "EliminarEmpresaAsync", "EliminarPuntoVentaAsync"];

        foreach (var metodo in metodos)
        {
            var cuerpo = CuerpoDelMetodo(servicio, metodo);

            Assert.Contains("EnUnaTransaccionDeBajaAsync(", cuerpo, StringComparison.Ordinal);
            Assert.DoesNotContain("EnUnaTransaccionAsync(", cuerpo, StringComparison.Ordinal);
        }

        // Y esa envoltura es la SIN reintento, no un alias de la otra.
        var envoltura = servicio[servicio.IndexOf(
            "private Task<T> EnUnaTransaccionDeBajaAsync<T>", StringComparison.Ordinal)..];

        Assert.Contains(
            "EjecutarEnTransaccionAsync(FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db)",
            envoltura[..300],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Cláusula (judgment-day ronda 1, hallazgo C2): en <c>ServicioDeUsuarios.EliminarAsync</c> el
    /// guard de uso corre BAJO el lock y DENTRO de la transacción, nunca como un SELECT suelto
    /// antes de abrirla. Es estructural y se dice que lo es (<c>mutation-proof-tests</c> regla 13):
    /// la carrera que cierra es que una venta o un turno del MISMO empleado se confirmen entre el
    /// guard y el estampado, y una prueba de un solo hilo no puede observar dónde está la línea.
    ///
    /// Se afirma el ORDEN de las marcas dentro del método:
    /// <c>ValidarPuedeIntervenirSobre</c> (afuera, es dominio puro) →
    /// <c>CrearEstrategiaSinReintento</c> → <c>BeginTransactionAsync</c> →
    /// <c>TomarLockDeBajaAsync</c> → la RELECTURA del sujeto → <c>PrimeraDependenciaEnUsoAsync</c>
    /// → estampado → <c>SaveChangesAsync</c>. Mover el guard afuera de la estrategia pone esto en
    /// rojo.
    ///
    /// Judgment-day ronda 2 suma dos marcas más, cada una por su propio hallazgo:
    /// <list type="bullet">
    /// <item><b>R2-1</b> — la estrategia es la SIN REINTENTO. <c>ServicioDeAuditoria.Registrar</c>
    /// hace <c>Add</c> de una instancia nueva por llamada, así que un reintento de
    /// <c>EnableRetryOnFailure</c> duplicaba la fila de <c>usuario.baja</c> en vez de rehacerla.
    /// Que sea estructural NO reemplaza al kill de verdad: el reintento se induce con un
    /// interceptor en <c>BajasDeOrganizacionTests</c>;</item>
    /// <item><b>R2-2</b> — el sujeto se RELEE bajo el lock, entre el lock y el guard. La lectura
    /// de afuera es de antes del lock, así que el perdedor de una baja concurrente re-estampaba un
    /// <c>deleted_at</c> nuevo. También tiene su kill conductual (la carrera con la cascada).</item>
    /// </list>
    /// </summary>
    [Fact]
    public void LaBajaDeUsuarioCorreSuGuardBajoElLockYDentroDeLaTransaccion()
    {
        var servicio = File.ReadAllText(Path.Combine(
            RaizDelRepositorio.Resolver(), "src", "Ways.Application", "Usuarios", "ServicioDeUsuarios.cs"));

        var cuerpo = CuerpoDelMetodo(servicio, "EliminarAsync");

        var politica = Posicion(cuerpo, "PoliticaDeRoles.ValidarPuedeIntervenirSobre(");
        var estrategia = Posicion(cuerpo, "FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db)");
        var ejecucion = Posicion(cuerpo, "estrategia.ExecuteAsync(");
        var transaccion = Posicion(cuerpo, "BeginTransactionAsync(");
        var lock_ = Posicion(cuerpo, "TomarLockDeBajaAsync(");
        var relectura = Posicion(cuerpo, "var sujeto = await BuscarAsync(id, ct);");
        var guard = Posicion(cuerpo, "inspector.PrimeraDependenciaEnUsoAsync(");
        var estampado = Posicion(cuerpo, "sujeto.DeletedAt = momento;");
        var guardado = Posicion(cuerpo, "db.SaveChangesAsync(ct)");

        Assert.True(politica < estrategia, "PoliticaDeRoles tiene que decidir antes de abrir nada.");
        Assert.True(estrategia < ejecucion, "La unidad corre bajo la estrategia SIN reintento (R2-1).");
        Assert.True(ejecucion < transaccion, "La transacción vive DENTRO de la estrategia de ejecución.");
        Assert.True(transaccion < lock_, "El lock se toma con la transacción ya abierta (xact scope).");
        Assert.True(lock_ < relectura, "El sujeto se relee BAJO el lock, nunca antes (R2-2).");
        Assert.True(relectura < guard, "El guard pregunta por el sujeto ya releído.");
        Assert.True(guard < estampado, "El estampado va después del guard.");
        Assert.True(estampado < guardado, "El SaveChanges cierra la unidad completa.");

        // Y la unidad NO puede volver a la estrategia reintentable por descuido.
        Assert.DoesNotContain("CreateExecutionStrategy", cuerpo, StringComparison.Ordinal);
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
    /// Cláusula: <c>DescribirBloqueo</c> atribuye el bloqueo a la RAMA QUE DISPARÓ, parseando la
    /// etiqueta que el inspector proyecta (<c>&lt;hoja&gt; via &lt;puente&gt;</c>), y no adivina
    /// nada a partir del conjunto de ramas del ancla.
    ///
    /// Es la corrección de judgment-day ronda 2 (hallazgo R2-6), y las dos redacciones anteriores
    /// se equivocaban sobre la MISMA hoja mixta. <c>parametros</c> llega a una empresa por DOS
    /// caminos: directo (<c>id_empresa</c>, una fila de nivel empresa) y puenteado por sus puntos
    /// de venta (<c>id_punto_venta</c>). La ronda 0 nombraba el puente solo si TODAS las ramas de
    /// la hoja lo eran, así que callaba el puente sobre un hit que sí vino por el puente; la ronda
    /// 1 lo nombraba si ALGUNA lo era, así que afirmaba "en sus puntos de venta" sobre una fila de
    /// nivel empresa. Las dos mandaban al operador a buscar donde la fila no está. Con la rama
    /// identificada, cada hit se atribuye exactamente donde vive.
    ///
    /// La primera aserción liga las dos puntas: la etiqueta que produce <see cref="RamaDeUso"/> es
    /// literalmente la que este método parsea, así que el formato no se puede separar en silencio.
    /// </summary>
    [Fact]
    public void LaCopiaDelBloqueoSaleDeLaRamaQueDisparoYNoDelConjuntoDeRamas()
    {
        var puente = new PuenteDeUso("public", "puntos_venta", ["id_punto_venta"], ["id_empresa"]);

        var puenteada = new RamaDeUso(
            "public", "parametros", ["id_punto_venta"], ["Id"],
            ClasificacionDeDependiente.Marcado, puente);

        var directa = new RamaDeUso(
            "public", "parametros", ["id_empresa"], ["Id"], ClasificacionDeDependiente.Marcado);

        Assert.Equal("parametros via puntos_venta", puenteada.Etiqueta);
        Assert.Equal("parametros", directa.Etiqueta);

        // LA HOJA MIXTA, en sus dos direcciones: la MISMA hoja rinde dos frases distintas según
        // por qué rama entró, que es justo lo que ninguna de las dos reglas anteriores podía hacer.
        Assert.Equal(
            "parámetros en sus puntos de venta", EtiquetasDeTablas.DescribirBloqueo(puenteada.Etiqueta));
        Assert.Equal("parámetros", EtiquetasDeTablas.DescribirBloqueo(directa.Etiqueta));

        // Una hoja que solo llega puenteada, y una que solo llega directo.
        Assert.Equal(
            "turnos de caja en sus puntos de venta",
            EtiquetasDeTablas.DescribirBloqueo("turnos_caja via puntos_venta"));

        Assert.Equal("marcas", EtiquetasDeTablas.DescribirBloqueo("marcas"));

        // Una hoja puenteada SIN etiqueta propia degrada la palabra pero conserva el puente: el
        // operador sigue sabiendo dónde buscar.
        Assert.Equal(
            $"{EtiquetasDeTablas.Generica} en sus puntos de venta",
            EtiquetasDeTablas.DescribirBloqueo("numeraciones_articulos via puntos_venta"));

        // Y un puente SIN etiqueta propia degrada solo la segunda mitad.
        Assert.Equal(
            $"marcas en sus {EtiquetasDeTablas.Generica}",
            EtiquetasDeTablas.DescribirBloqueo("marcas via numeraciones_articulos"));
    }
}
