using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Ways.Application.Organizacion;
using Ways.Domain.Clientes;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Persistencia;

/// <summary>
/// stage-20-organizacion-relaciones-y-bajas, Slice 3 (design sección B): las redes N1, N2 y N3.
///
/// Detrás del guard de uso NO hay ninguna red de base — toda FK del modelo es
/// <c>Restrict</c>, pero la baja es lógica y <c>Restrict</c> no aporta nada contra un
/// <c>UPDATE ... SET deleted_at</c> (<c>db-error-backstops</c> estructuralmente N/A). Estas
/// pruebas no son buena práctica: son el argumento de seguridad completo.
///
/// Corren SIN CONTENEDOR sobre el modelo Npgsql real, construido sobre una cadena de conexión
/// que nunca se abre — el patrón de <see cref="ModeloDeOrganizacionTests"/>.
/// </summary>
public class InventarioDeDependientesTests
{
    private static readonly Type[] Anclas =
        [typeof(Tenant), typeof(Empresa), typeof(PuntoVenta), typeof(Usuario)];

    private static WaysDbContext CrearContexto()
    {
        var opciones = new DbContextOptionsBuilder<WaysDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=probe;Username=probe;Password=probe",
                npgsql =>
                {
                    npgsql.MapEnum<EstadoUsuario>("estado_usuario");
                    npgsql.MapEnum<EstadoTenant>("estado_tenant");
                })
            .Options;

        return new WaysDbContext(opciones, TenantActualFijo.Plataforma);
    }

    // ---------------------------------------------------------------------------------------
    // N1 — TOTALIDAD. Nunca degradable.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// N1, primera mitad: <c>Construir</c> no tira para ninguna de las cuatro anclas. Las tres
    /// imposibilidades mecánicas (tipo sin tabla mapeada, tipo <c>Marcado</c> sin
    /// <c>created_at</c> resoluble, clave principal no legible desde el ancla) tiran
    /// <see cref="InvalidOperationException"/> NOMBRANDO el tipo y la FK, y esta prueba es quien
    /// las ejecuta en CI — para que sean fallas de build y nunca un 500 sobre un intento de baja.
    /// </summary>
    [Theory]
    [InlineData(typeof(Tenant))]
    [InlineData(typeof(Empresa))]
    [InlineData(typeof(PuntoVenta))]
    [InlineData(typeof(Usuario))]
    public void N1_ConstruirNoTiraParaNingunaDeLasCuatroAnclas(Type ancla)
    {
        using var db = CrearContexto();

        var ramas = InventarioDeDependientes.Construir(db.Model, ancla);

        Assert.NotEmpty(ramas);
    }

    /// <summary>
    /// N1, segunda mitad: NINGUNA FK SE CAE EN SILENCIO. Toda FK que referencia al ancla aporta su
    /// rama <c>(tabla, columnas)</c> al inventario, y ningún carve-out llega a las ramas
    /// ejecutables.
    ///
    /// Honestidad sobre el alcance (judgment-day slice 3, ronda 1): la CUENTA de ramas no es una
    /// red. Comparar <c>InventarioCompleto().Count</c> contra la cuenta de su propia fuente es una
    /// tautología — la cuenta se deriva del mismo recorrido que se está probando, así que un
    /// dependiente que el recorrido no ve tampoco aparece del lado esperado. La red de nivel
    /// CONJUNTO es N5.
    /// </summary>
    [Theory]
    [InlineData(typeof(Tenant))]
    [InlineData(typeof(Empresa))]
    [InlineData(typeof(PuntoVenta))]
    [InlineData(typeof(Usuario))]
    public void N1_NingunaFkSeCaeEnSilencioYLosCarveOutsNoEjecutan(Type ancla)
    {
        using var db = CrearContexto();

        var completo = InventarioDeDependientes.InventarioCompleto(db.Model, ancla);
        var claves = completo
            .Select(rama => $"{rama.Tabla}|{string.Join(',', rama.Columnas)}")
            .ToHashSet(StringComparer.Ordinal);

        var faltantes = db.Model.FindEntityType(ancla)!.GetReferencingForeignKeys()
            .Select(ClaveDeFk)
            .Where(clave => !claves.Contains(clave))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            faltantes.Count == 0,
            $"Estas FKs hacia {ancla.Name} no aportaron ninguna rama al inventario: " +
            string.Join(", ", faltantes));

        var excluidas = completo.Count(
            rama => rama.Clasificacion is ClasificacionDeDependiente.Excluido);

        Assert.Equal(
            completo.Count - excluidas,
            InventarioDeDependientes.Construir(db.Model, ancla).Count);
    }

    private static string ClaveDeFk(IForeignKey fk)
    {
        var dependiente = fk.DeclaringEntityType;
        var objeto = StoreObjectIdentifier.Create(dependiente, StoreObjectType.Table)!.Value;

        return $"{dependiente.GetTableName()}|" +
            string.Join(',', fk.Properties.Select(p => p.GetColumnName(objeto)));
    }

    // ---------------------------------------------------------------------------------------
    // N2 — EL BALDE SE LEE DE LA TABLA, NO SE REPITE DESDE EL CÓDIGO. Nunca degradable.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// N2: para CADA rama, <c>UsaAncla</c> equivale a las DOS condiciones del balde
    /// <c>Marcado</c> (design sección A): el tipo dependiente hereda de <c>EntidadBase</c> Y la
    /// tabla tiene una columna <c>created_at</c> — recalculado acá contra el modelo, sin mirar el
    /// clasificador.
    ///
    /// Mata: cambiar el clasificador a <c>EntidadTenant</c> (usuarios/tenants heredan de
    /// <c>EntidadBase</c> y caerían a <c>SinMarca</c>), invertirlo, quedarse corto con una lista
    /// de tipos escrita a mano, o quedarse con una sola de las dos condiciones.
    /// </summary>
    [Theory]
    [InlineData(typeof(Tenant))]
    [InlineData(typeof(Empresa))]
    [InlineData(typeof(PuntoVenta))]
    [InlineData(typeof(Usuario))]
    public void N2_UsaAnclaEquivaleAEntidadBaseConColumnaCreatedAt(Type ancla)
    {
        using var db = CrearContexto();

        var llevaMarcaPorTabla = db.Model.GetEntityTypes()
            .Where(tipo => tipo.GetTableName() is not null)
            .GroupBy(tipo => tipo.GetTableName()!, StringComparer.Ordinal)
            .ToDictionary(
                grupo => grupo.Key,
                grupo => grupo.Any(tipo =>
                    typeof(EntidadBase).IsAssignableFrom(tipo.ClrType)
                    && tipo.GetProperties().Any(propiedad => propiedad.GetColumnName() == "created_at")),
                StringComparer.Ordinal);

        var ramas = InventarioDeDependientes.Construir(db.Model, ancla);

        Assert.NotEmpty(ramas);

        foreach (var rama in ramas)
        {
            Assert.Equal(llevaMarcaPorTabla[rama.Tabla], rama.UsaAncla);
        }
    }

    // ---------------------------------------------------------------------------------------
    // N3 — EL GOLDEN DEL INVENTARIO (EL CABLE TRAMPA). Nunca degradable.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// N3: el inventario completo de las cuatro anclas, ordenado, contra el archivo versionado.
    /// Cualquier FK que una etapa futura agregue, saque, reapunte o reclasifique produce un diff
    /// que NOMBRA la tabla y la columna exactas.
    ///
    /// Dicho con honestidad: N3 no prueba que la clasificación sea correcta — prueba que NINGUNA
    /// clasificación cambia en silencio. Regenerar el archivo es una edición deliberada que hay
    /// que justificar línea por línea en el cuerpo del PR (criterio de verificación V8);
    /// regenerarlo a ciegas degrada la propiedad central de esta etapa a un sello de goma.
    /// </summary>
    [Fact]
    public void N3_ElInventarioCoincideConElGoldenVersionado()
    {
        using var db = CrearContexto();

        var esperado = File.ReadAllLines(RutaDelGolden())
            .Where(linea => linea.Length > 0)
            .ToList();

        var actual = RenderizarInventario(db.Model);

        // El diff de Assert.Equal sobre colecciones trunca las líneas a ~50 caracteres, que se
        // come el final de una columna como id_punto_venta_destino. El mensaje explícito es lo
        // que hace que la red NOMBRE la tabla y la columna exactas.
        var agregadas = actual.Except(esperado, StringComparer.Ordinal).ToList();
        var quitadas = esperado.Except(actual, StringComparer.Ordinal).ToList();

        Assert.True(
            agregadas.Count == 0 && quitadas.Count == 0,
            $"El inventario de dependientes cambió respecto de {RutaDelGolden()}." +
            $"{Environment.NewLine}AGREGADAS (están en el modelo, no en el golden):" +
            $"{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", agregadas)}" +
            $"{Environment.NewLine}QUITADAS (están en el golden, no en el modelo):" +
            $"{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", quitadas)}" +
            $"{Environment.NewLine}Regenerar el golden es una edición deliberada: cada línea " +
            "cambiada necesita su decisión de clasificación escrita en el cuerpo del PR (V8).");

        Assert.Equal(esperado, actual);
    }

    private static IReadOnlyList<string> RenderizarInventario(IModel modelo) =>
        [.. Anclas
            .SelectMany(ancla => InventarioDeDependientes.InventarioCompleto(modelo, ancla)
                .Select(rama =>
                    $"{ancla.Name} | {rama.Tabla} | {string.Join(',', rama.Columnas)} | " +
                    $"{rama.Clasificacion.ToString().ToLowerInvariant()}"))
            .Order(StringComparer.Ordinal)];

    private static string RutaDelGolden() => Path.Combine(
        Path.GetDirectoryName(RutaDeEsteArchivo())!, "Fixtures", "inventario-de-dependientes.txt");

    private static string RutaDeEsteArchivo([CallerFilePath] string ruta = "") => ruta;

    // ---------------------------------------------------------------------------------------
    // N5 — COMPLETITUD DEL CONJUNTO DE DEPENDIENTES DEL TENANT. Nunca degradable.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// N5: la auditoría manual de judgment-day (slice 3, ronda 1) escrita como código. Para el
    /// ancla <see cref="Tenant"/>, TODA tabla del modelo con una columna <c>id_tenant</c> tiene
    /// que aparecer en su inventario — las líneas <c>excluido</c> cuentan como presentes, porque
    /// el carve-out es una decisión escrita, no una omisión.
    ///
    /// Es la red que N1 NO puede ser: N1 compara el inventario contra el mismo recorrido de FKs
    /// que lo produce, así que un dependiente que ese recorrido no ve es invisible para los dos
    /// lados de la igualdad. Este conjunto se calcula desde una fuente INDEPENDIENTE (el mapeo de
    /// columnas del modelo), y esa independencia es toda la red.
    ///
    /// El agujero real que encontró: <c>puntos_venta</c> declara su FK compuesta contra
    /// <c>empresas</c> y NINGUNA contra <c>tenants</c>, así que
    /// <c>GetReferencingForeignKeys()</c> nunca lo devolvía para el ancla <c>Tenant</c> y un
    /// tenant cuyo cliente agregó un segundo punto de venta leía PRÍSTINO — falla ABIERTA.
    /// </summary>
    [Fact]
    public void N5_TodaTablaConIdTenantAparecenEnElInventarioDelAncla()
    {
        using var db = CrearContexto();

        var enElInventario = InventarioDeDependientes.InventarioCompleto(db.Model, typeof(Tenant))
            .Select(rama => rama.Tabla)
            .ToHashSet(StringComparer.Ordinal);

        var tablaDelAncla = db.Model.FindEntityType(typeof(Tenant))!.GetTableName();

        var faltantes = db.Model.GetEntityTypes()
            .Where(tipo => tipo.GetTableName() is { } tabla && tabla != tablaDelAncla)
            .Where(TieneColumnaDeAlcanceDeTenant)
            .Select(tipo => tipo.GetTableName()!)
            .Distinct(StringComparer.Ordinal)
            .Where(tabla => !enElInventario.Contains(tabla))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            faltantes.Count == 0,
            "Estas tablas llevan id_tenant y NO están en el inventario del ancla Tenant, así que " +
            "un tenant que las usó lee PRÍSTINO (falla abierta): " + string.Join(", ", faltantes));
    }

    private static bool TieneColumnaDeAlcanceDeTenant(IEntityType tipo) =>
        StoreObjectIdentifier.Create(tipo, StoreObjectType.Table) is { } objeto
        && tipo.GetProperties().Any(propiedad =>
            propiedad.GetColumnName(objeto) == InventarioDeDependientes.ColumnaDeAlcanceDeTenant);

    // ---------------------------------------------------------------------------------------
    // Carve-outs (tarea 3.12)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// El conjunto de carve-outs tiene EXACTAMENTE dos miembros. Un tercero agregado sin su
    /// razón escrita y sin su propia prueba abre un agujero en la única línea de defensa.
    /// </summary>
    [Fact]
    public void LosCarveOutsSonExactamenteAuditoriaYNumeracionCliente()
    {
        Assert.Equal(
            [typeof(Ways.Domain.Auditoria.Auditoria), typeof(NumeracionCliente)],
            InventarioDeDependientes.Excluidos.Order(Comparer<Type>.Create(
                static (a, b) => string.CompareOrdinal(a.FullName, b.FullName))));
    }

    /// <summary>
    /// Ninguno de los dos carve-outs aporta una rama ejecutable para ninguna de las cuatro
    /// anclas — y sí aparece en el inventario completo, para que el golden N3 también fije este
    /// conjunto en vez de dejarlo desaparecer sin rastro.
    /// </summary>
    [Fact]
    public void NingunCarveOutAportaRamaParaNingunaAncla()
    {
        using var db = CrearContexto();

        var tablasExcluidas = InventarioDeDependientes.Excluidos
            .Select(tipo => db.Model.FindEntityType(tipo)!.GetTableName()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(["auditoria", "numeraciones_clientes"], tablasExcluidas.Order(StringComparer.Ordinal));

        foreach (var ancla in Anclas)
        {
            Assert.DoesNotContain(
                InventarioDeDependientes.Construir(db.Model, ancla),
                rama => tablasExcluidas.Contains(rama.Tabla));
        }

        var completo = Anclas
            .SelectMany(ancla => InventarioDeDependientes.InventarioCompleto(db.Model, ancla))
            .Where(rama => tablasExcluidas.Contains(rama.Tabla))
            .ToList();

        Assert.All(completo, rama =>
            Assert.Equal(ClasificacionDeDependiente.Excluido, rama.Clasificacion));
        Assert.Contains(completo, rama => rama.Tabla == "auditoria");
        Assert.Contains(completo, rama => rama.Tabla == "numeraciones_clientes");
    }
}
