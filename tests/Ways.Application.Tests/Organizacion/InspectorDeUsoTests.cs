using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Organizacion;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Organizacion;

/// <summary>
/// stage-20-organizacion-relaciones-y-bajas, Slice 3 (tareas 3.13-3.15): el RENDERING del
/// statement del guard, en aserciones de texto puras sobre <c>Construir</c> +
/// <see cref="InspectorDeUso.Renderizar"/>. Sin contenedor: el modelo Npgsql real se construye
/// sobre una conexión que nunca se abre.
///
/// Qué cláusula prueba cada test está dicho en su propio doc-comment
/// (<c>mutation-proof-tests</c> regla 1) — acá no hay "cobertura general": cada aserción existe
/// para matar un mutante nombrado.
/// </summary>
public class InspectorDeUsoTests
{
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

    private static string Renderizar(WaysDbContext db, Type ancla) => InspectorDeUso.Renderizar(
        InventarioDeDependientes.Construir(db.Model, ancla),
        InventarioDeDependientes.PropiedadesDeAncla(db.Model, ancla));

    /// <summary>
    /// Cláusula: el conjunto <c>created_at &gt; $n</c> de una rama <c>Marcado</c>, y el
    /// <c>&gt;</c> ESTRICTO. Lo que creó el aprovisionamiento comparte el instante del ancla
    /// (<c>ServicioDeAprovisionamiento</c> lee el reloj una sola vez), así que un <c>&gt;=</c>
    /// volvería indeleteable a todo tenant recién creado.
    /// </summary>
    [Fact]
    public void UnaRamaMarcadaLlevaElConjuntoDeAnclaConMayorEstricto()
    {
        using var db = CrearContexto();

        var sql = Renderizar(db, typeof(PuntoVenta));

        Assert.Contains(
            "SELECT 'comprobantes_venta' AS tabla WHERE EXISTS (SELECT 1 FROM " +
            "\"public\".\"comprobantes_venta\" d WHERE d.\"id_punto_venta\" = $1 AND " +
            "d.\"id_tenant\" = $2 AND d.\"created_at\" > $3)",
            sql);

        Assert.DoesNotContain(">=", sql);
    }

    /// <summary>
    /// Cláusula: una rama <c>SinMarca</c> lleva SOLO los conjuntos de la FK. <c>stock</c> no
    /// tiene <c>created_at</c>, así que existir ya significa uso — un conjunto temporal ahí no
    /// compilaría contra la tabla.
    /// </summary>
    [Fact]
    public void UnaRamaSinMarcaLlevaSoloElPredicadoDeLaFk()
    {
        using var db = CrearContexto();

        var sql = Renderizar(db, typeof(PuntoVenta));

        Assert.Contains(
            "SELECT 'stock' AS tabla WHERE EXISTS (SELECT 1 FROM \"public\".\"stock\" d " +
            "WHERE d.\"id_punto_venta\" = $1 AND d.\"id_tenant\" = $2)",
            sql);
    }

    /// <summary>
    /// Cláusula: el zip de <c>fk.Properties</c> con <c>fk.PrincipalKey.Properties</c>. Una FK
    /// compuesta rinde DOS conjuntos, y la clave principal es la ALTERNATIVA
    /// <c>(Id, IdTenant)</c> de <c>Empresa</c> — ambos valores se leen del ancla, en el orden de
    /// <c>PropiedadesDeAncla</c>. Sin el zip, la segunda columna quedaría sin ligar o ligada al
    /// parámetro equivocado.
    /// </summary>
    [Fact]
    public void UnaFkCompuestaDeClaveAlternativaRindeDosConjuntosLeidosDelAncla()
    {
        using var db = CrearContexto();

        var ramas = InventarioDeDependientes.Construir(db.Model, typeof(Empresa));
        var propiedades = InventarioDeDependientes.PropiedadesDeAncla(db.Model, typeof(Empresa));

        Assert.Equal(["Id", "IdTenant"], propiedades);

        var puntosVenta = Assert.Single(ramas, rama => rama.Tabla == "puntos_venta");
        Assert.Equal(["id_empresa", "id_tenant"], puntosVenta.Columnas);
        Assert.Equal(["Id", "IdTenant"], puntosVenta.PropiedadesDelPrincipal);

        Assert.Contains(
            "SELECT 'puntos_venta' AS tabla WHERE EXISTS (SELECT 1 FROM " +
            "\"public\".\"puntos_venta\" d WHERE d.\"id_empresa\" = $1 AND d.\"id_tenant\" = $2 " +
            "AND d.\"created_at\" > $3)",
            InspectorDeUso.Renderizar(ramas, propiedades));
    }

    /// <summary>
    /// Cláusula: un tipo que apunta al ancla por DOS FKs distintas aporta DOS ramas
    /// independientes. <c>movimientos_stock</c> referencia al punto de venta por
    /// <c>id_punto_venta</c> y por <c>id_punto_venta_destino</c>: agrupar por tipo en vez de por
    /// FK perdería la transferencia entrante.
    /// </summary>
    [Fact]
    public void UnTipoConDosFksAlAnclaAportaDosRamasIndependientes()
    {
        using var db = CrearContexto();

        var sql = Renderizar(db, typeof(PuntoVenta));

        Assert.Contains("d.\"id_punto_venta\" = $1 AND d.\"id_tenant\" = $2)", sql);
        Assert.Contains(
            "SELECT 'movimientos_stock' AS tabla WHERE EXISTS (SELECT 1 FROM " +
            "\"public\".\"movimientos_stock\" d WHERE d.\"id_punto_venta_destino\" = $1 AND " +
            "d.\"id_tenant\" = $2)",
            sql);
        Assert.Equal(
            2,
            InventarioDeDependientes.Construir(db.Model, typeof(PuntoVenta))
                .Count(rama => rama.Tabla == "movimientos_stock"));
    }

    /// <summary>
    /// Cláusula: todo identificador emitido va calificado por esquema y entre comillas dobles.
    /// Sin comillas, una tabla futura que se llame como una palabra reservada rompe el
    /// statement entero.
    /// </summary>
    [Theory]
    [InlineData(typeof(Tenant))]
    [InlineData(typeof(Empresa))]
    [InlineData(typeof(PuntoVenta))]
    [InlineData(typeof(Usuario))]
    public void TodaTablaSeEmiteCalificadaPorEsquemaYEntreComillas(Type ancla)
    {
        using var db = CrearContexto();

        var ramas = InventarioDeDependientes.Construir(db.Model, ancla);
        var sql = Renderizar(db, ancla);

        foreach (var rama in ramas)
        {
            Assert.Contains($"FROM \"public\".\"{rama.Tabla}\" d WHERE ", sql);

            foreach (var columna in rama.Columnas)
            {
                Assert.Contains($"d.\"{columna}\" = $", sql);
            }
        }
    }

    /// <summary>
    /// Cláusula: la validación <c>^[a-z_][a-z0-9_]*$</c> del generador. Los identificadores solo
    /// pueden venir de la metadata de EF, y este es el cierre de esa superficie: cualquier cosa
    /// que no matchee se RECHAZA en vez de concatenarse.
    /// </summary>
    [Theory]
    [InlineData("comprobantes_venta\"; DROP TABLE usuarios; --")]
    [InlineData("ComprobantesVenta")]
    [InlineData("1_tabla")]
    [InlineData("")]
    public void UnIdentificadorNoConformeSeRechaza(string tabla)
    {
        var rama = new RamaDeUso(
            "public", tabla, ["id_tenant"], ["Id"], ClasificacionDeDependiente.SinMarca);

        var error = Assert.Throws<InvalidOperationException>(
            () => InspectorDeUso.Renderizar([rama], ["Id"]));

        Assert.Contains("^[a-z_][a-z0-9_]*$", error.Message);
    }

    /// <summary>
    /// Cláusula: la validación también corre sobre las COLUMNAS y sobre el ESQUEMA, no solo
    /// sobre el nombre de tabla.
    /// </summary>
    [Fact]
    public void UnaColumnaOUnEsquemaNoConformeTambienSeRechazan()
    {
        Assert.Throws<InvalidOperationException>(() => InspectorDeUso.Renderizar(
            [new RamaDeUso("public", "stock", ["id_tenant\" OR 1=1 --"], ["Id"], ClasificacionDeDependiente.SinMarca)],
            ["Id"]));

        Assert.Throws<InvalidOperationException>(() => InspectorDeUso.Renderizar(
            [new RamaDeUso("public\"; --", "stock", ["id_tenant"], ["Id"], ClasificacionDeDependiente.SinMarca)],
            ["Id"]));
    }

    /// <summary>
    /// Cláusula: la cuenta de parámetros y el orden de ligado. Los <c>$n</c> usados son
    /// exactamente <c>1..PropiedadesDeAncla.Count + 1</c> — ni uno de más (Postgres rechaza el
    /// bind con "supplies N parameters but requires M") ni uno de menos —, cada propiedad va en
    /// su posición de <c>PropiedadesDeAncla</c> y el instante del ancla va SIEMPRE al último.
    /// </summary>
    [Theory]
    [InlineData(typeof(Tenant))]
    [InlineData(typeof(Empresa))]
    [InlineData(typeof(PuntoVenta))]
    [InlineData(typeof(Usuario))]
    public void LaCuentaYElOrdenDeParametrosCoincidenConLasPropiedadesDelAncla(Type ancla)
    {
        using var db = CrearContexto();

        var ramas = InventarioDeDependientes.Construir(db.Model, ancla);
        var propiedades = InventarioDeDependientes.PropiedadesDeAncla(db.Model, ancla);
        var sql = InspectorDeUso.Renderizar(ramas, propiedades);

        var usados = Regex.Matches(sql, @"\$(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToHashSet();

        Assert.Equal(Enumerable.Range(1, propiedades.Count + 1).ToHashSet(), usados);

        foreach (var rama in ramas)
        {
            var conjuntos = rama.Columnas
                .Zip(rama.PropiedadesDelPrincipal, (columna, propiedad) =>
                    $"d.\"{columna}\" = ${propiedades.ToList().IndexOf(propiedad) + 1}")
                .ToList();

            if (rama.UsaAncla)
            {
                conjuntos.Add($"d.\"created_at\" > ${propiedades.Count + 1}");
            }

            Assert.Contains(
                $"SELECT '{rama.Tabla}' AS tabla WHERE EXISTS (SELECT 1 FROM " +
                $"\"{rama.Esquema}\".\"{rama.Tabla}\" d WHERE {string.Join(" AND ", conjuntos)})",
                sql);
        }
    }

    /// <summary>
    /// Cláusula: el <c>LIMIT 1</c> EXTERNO. Es lo que hace que el nodo <c>Append</c> corte en la
    /// primera rama que devuelve fila; sin él las ~40 ramas se evalúan enteras en cada intento.
    /// </summary>
    [Theory]
    [InlineData(typeof(Tenant))]
    [InlineData(typeof(Empresa))]
    [InlineData(typeof(PuntoVenta))]
    [InlineData(typeof(Usuario))]
    public void ElStatementAbreConLaProyeccionYCierraConElLimitExterno(Type ancla)
    {
        using var db = CrearContexto();

        var sql = Renderizar(db, ancla);

        Assert.StartsWith("SELECT tabla FROM (SELECT ", sql);
        Assert.EndsWith(") AS ramas LIMIT 1", sql);
        Assert.Contains(" UNION ALL ", sql);
    }

    /// <summary>
    /// OD4, la mitad barata (tarea 3.14): NINGUNA rama emite <c>deleted_at</c>. Una fila que el
    /// cliente cargó y después dio de baja IGUAL bloquea — el cliente operó ahí, y borrarla
    /// después no rebobina esa historia. La mitad conductual es la tarea 4.11.
    /// </summary>
    [Theory]
    [InlineData(typeof(Tenant))]
    [InlineData(typeof(Empresa))]
    [InlineData(typeof(PuntoVenta))]
    [InlineData(typeof(Usuario))]
    public void NingunaRamaMencionaDeletedAt(Type ancla)
    {
        using var db = CrearContexto();

        Assert.DoesNotContain("deleted_at", Renderizar(db, ancla));
    }

    /// <summary>
    /// BO-R8 (tarea 3.15): una FK NULLABLE rinde el predicado llano <c>&lt;fk&gt; = $n</c>, sin
    /// ningún caso especial de <c>IS NULL</c>. <c>clientes.id_empresa</c> es nullable y
    /// <c>NULL</c> ahí significa "fila de catálogo compartida": <c>= $n</c> no matchea
    /// <c>NULL</c>, así que una fila compartida no bloquea la baja de una empresa. La prueba
    /// conductual es la tarea 4.21.
    /// </summary>
    [Fact]
    public void UnaFkNullableRindeElPredicadoLlanoSinCasoEspecialDeNull()
    {
        using var db = CrearContexto();

        var clientes = db.Model.FindEntityType(typeof(Ways.Domain.Clientes.Cliente))!;
        Assert.True(clientes.FindProperty(nameof(Ways.Domain.Clientes.Cliente.IdEmpresa))!.IsNullable);

        var sql = Renderizar(db, typeof(Empresa));

        Assert.Contains(
            "SELECT 'clientes' AS tabla WHERE EXISTS (SELECT 1 FROM \"public\".\"clientes\" d " +
            "WHERE d.\"id_empresa\" = $1 AND d.\"id_tenant\" = $2 AND d.\"created_at\" > $3)",
            sql);

        Assert.DoesNotContain("IS NULL", sql);
        Assert.DoesNotContain("IS NOT NULL", sql);
    }

    /// <summary>
    /// Cláusula: <c>Renderizar</c> filtra los carve-outs. Aunque le pasen el inventario COMPLETO,
    /// ninguna tabla excluida llega al statement.
    /// </summary>
    [Theory]
    [InlineData(typeof(Tenant))]
    [InlineData(typeof(PuntoVenta))]
    [InlineData(typeof(Usuario))]
    public void ElRenderizadorNuncaEmiteUnaTablaExcluida(Type ancla)
    {
        using var db = CrearContexto();

        var sql = InspectorDeUso.Renderizar(
            InventarioDeDependientes.InventarioCompleto(db.Model, ancla),
            InventarioDeDependientes.PropiedadesDeAncla(db.Model, ancla));

        Assert.DoesNotContain("auditoria", sql);
        Assert.DoesNotContain("numeraciones_clientes", sql);
    }
}
