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
            Assert.Contains($"FROM \"public\".\"{rama.Tabla}\" d", sql);

            if (rama.Puente is { } puente)
            {
                Assert.Contains($"JOIN \"public\".\"{puente.Tabla}\" pv ON ", sql);

                foreach (var columna in rama.Columnas)
                {
                    Assert.Contains($"d.\"{columna}\" = pv.\"", sql);
                }

                foreach (var columna in puente.ColumnasHaciaElAncla)
                {
                    Assert.Contains($"pv.\"{columna}\" = $", sql);
                }

                continue;
            }

            foreach (var columna in rama.Columnas)
            {
                Assert.Contains($"d.\"{columna}\" = $", sql);
            }
        }
    }

    /// <summary>
    /// Cláusula: la validación <c>\A[a-z_][a-z0-9_]*\z</c> del generador. Los identificadores solo
    /// pueden venir de la metadata de EF, y este es el cierre de esa superficie: cualquier cosa
    /// que no matchee se RECHAZA en vez de concatenarse.
    ///
    /// El caso <c>"stock\n"</c> es el que fija el ANCLAJE: en .NET <c>$</c> matchea también antes
    /// de un <c>\n</c> final, así que con <c>^...$</c> ese identificador pasaba y arrastraba todo
    /// lo que viniera después del salto de línea al statement.
    /// </summary>
    [Theory]
    [InlineData("comprobantes_venta\"; DROP TABLE usuarios; --")]
    [InlineData("ComprobantesVenta")]
    [InlineData("1_tabla")]
    [InlineData("")]
    [InlineData("stock\n")]
    public void UnIdentificadorNoConformeSeRechaza(string tabla)
    {
        var rama = new RamaDeUso(
            "public", tabla, ["id_tenant"], ["Id"], ClasificacionDeDependiente.SinMarca);

        var error = Assert.Throws<InvalidOperationException>(
            () => InspectorDeUso.Renderizar([rama], ["Id"]));

        Assert.Contains(@"\A[a-z_][a-z0-9_]*\z", error.Message);
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
            var origen = $"\"{rama.Esquema}\".\"{rama.Tabla}\" d";
            List<string> conjuntos;

            if (rama.Puente is { } puente)
            {
                origen += $" JOIN \"{puente.Esquema}\".\"{puente.Tabla}\" pv ON " + string.Join(
                    " AND ",
                    rama.Columnas.Zip(
                        puente.ColumnasDeUnion,
                        (columna, columnaDelPuente) => $"d.\"{columna}\" = pv.\"{columnaDelPuente}\""));

                conjuntos = puente.ColumnasHaciaElAncla
                    .Zip(rama.PropiedadesDelPrincipal, (columna, propiedad) =>
                        $"pv.\"{columna}\" = ${propiedades.ToList().IndexOf(propiedad) + 1}")
                    .ToList();
            }
            else
            {
                conjuntos = rama.Columnas
                    .Zip(rama.PropiedadesDelPrincipal, (columna, propiedad) =>
                        $"d.\"{columna}\" = ${propiedades.ToList().IndexOf(propiedad) + 1}")
                    .ToList();
            }

            if (rama.UsaAncla)
            {
                conjuntos.Add($"d.\"created_at\" > ${propiedades.Count + 1}");
            }

            Assert.Contains(
                $"SELECT '{rama.Etiqueta}' AS tabla WHERE EXISTS (SELECT 1 FROM {origen} " +
                $"WHERE {string.Join(" AND ", conjuntos)})",
                sql);
        }
    }

    /// <summary>
    /// Cláusula: la rama PUENTEADA del ancla <see cref="Empresa"/>. Ninguna tabla operativa lleva
    /// <c>id_empresa</c> — las ventas se clavan en <c>id_punto_venta</c> —, así que sin este
    /// <c>JOIN</c> contra <c>puntos_venta</c> una empresa con historia operativa completa lee
    /// PRÍSTINA. La prueba fija el TEXTO exacto de las tres partes que la hacen correcta: la unión
    /// hoja↔puente por la clave alternativa <c>(id, id_tenant)</c>, los parámetros del ancla sobre
    /// el PUENTE (<c>pv."id_empresa" = $1 AND pv."id_tenant" = $2</c>, y ese segundo conjunto es lo
    /// que impide que el id de otro tenant bloquee) y el conjunto del instante sobre la HOJA.
    ///
    /// La etiqueta PROYECTADA es la de la RAMA —<c>&lt;hoja&gt; via puntos_venta</c>— desde
    /// judgment-day ronda 2 (hallazgo R2-6): con la hoja pelada, una tabla que llega al ancla por
    /// dos caminos dejaba al llamador sin saber cuál disparó, y la copia del 409 mandaba a buscar
    /// una fila de nivel empresa en los puntos de venta. Se compone de los identificadores ya
    /// validados, así que la superficie de inyección no se abre.
    /// </summary>
    [Fact]
    public void UnaRamaPuenteadaUneLaHojaConPuntosVentaYLigaElAnclaSobreElPuente()
    {
        using var db = CrearContexto();

        var sql = Renderizar(db, typeof(Empresa));

        Assert.Contains(
            "SELECT 'comprobantes_venta via puntos_venta' AS tabla WHERE EXISTS (SELECT 1 FROM " +
            "\"public\".\"comprobantes_venta\" d JOIN \"public\".\"puntos_venta\" pv ON " +
            "d.\"id_punto_venta\" = pv.\"id_punto_venta\" AND " +
            "d.\"id_tenant\" = pv.\"id_tenant\" " +
            "WHERE pv.\"id_empresa\" = $1 AND pv.\"id_tenant\" = $2 AND d.\"created_at\" > $3)",
            sql);

        Assert.Contains(
            "SELECT 'stock via puntos_venta' AS tabla WHERE EXISTS (SELECT 1 FROM " +
            "\"public\".\"stock\" d " +
            "JOIN \"public\".\"puntos_venta\" pv ON d.\"id_punto_venta\" = " +
            "pv.\"id_punto_venta\" AND d.\"id_tenant\" = pv.\"id_tenant\" " +
            "WHERE pv.\"id_empresa\" = $1 AND " +
            "pv.\"id_tenant\" = $2)",
            sql);
    }

    /// <summary>
    /// Cláusula: el conjunto PUENTEADO es exactamente el inventario EJECUTABLE del ancla
    /// <see cref="PuntoVenta"/> — ni una rama de menos (falla abierta) ni el carve-out
    /// <c>auditoria</c> de más. Y las otras tres anclas NO puentean: <c>Tenant</c> no lo necesita
    /// (toda tabla lleva <c>id_tenant</c> y la segunda fuente ya la trae) y <c>PuntoVenta</c> y
    /// <c>Usuario</c> son hojas de la jerarquía.
    /// </summary>
    [Fact]
    public void ElConjuntoPuenteadoDeEmpresaEsElInventarioEjecutableDePuntoVenta()
    {
        using var db = CrearContexto();

        var esperado = InventarioDeDependientes.Construir(db.Model, typeof(PuntoVenta))
            .Select(rama => $"{rama.Tabla}|{string.Join(',', rama.Columnas)}")
            .Order(StringComparer.Ordinal)
            .ToList();

        var puenteadas = InventarioDeDependientes.Construir(db.Model, typeof(Empresa))
            .Where(rama => rama.Puente is not null)
            .ToList();

        Assert.Equal(
            esperado,
            puenteadas
                .Select(rama => $"{rama.Tabla}|{string.Join(',', rama.Columnas)}")
                .Order(StringComparer.Ordinal));

        Assert.All(puenteadas, rama => Assert.Equal("puntos_venta", rama.Puente!.Tabla));
        Assert.DoesNotContain(puenteadas, rama => rama.Tabla == "auditoria");

        foreach (var ancla in new[] { typeof(Tenant), typeof(PuntoVenta), typeof(Usuario) })
        {
            Assert.DoesNotContain(
                InventarioDeDependientes.InventarioCompleto(db.Model, ancla),
                rama => rama.Puente is not null);
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
    ///
    /// Cada fila declara los carve-outs que REALMENTE referencian a esa ancla y la prueba fija ese
    /// conjunto antes de aserverar la ausencia: aserverar que no se emite <c>numeraciones_clientes</c>
    /// para <c>PuntoVenta</c> era vacuo (no existe esa FK), y una fila con conjunto VACÍO
    /// —<c>Empresa</c>— es la que declara que ningún carve-out la referencia hoy. Si mañana
    /// aparece uno nuevo, la igualdad del conjunto se rompe y nombra la tabla.
    /// </summary>
    [Theory]
    [InlineData(typeof(Tenant), new[] { "auditoria", "numeraciones_clientes" })]
    [InlineData(typeof(Empresa), new string[0])]
    [InlineData(typeof(PuntoVenta), new[] { "auditoria" })]
    [InlineData(typeof(Usuario), new[] { "auditoria" })]
    public void ElRenderizadorNuncaEmiteUnaTablaExcluida(Type ancla, string[] excluidasQueLaReferencian)
    {
        using var db = CrearContexto();

        var completo = InventarioDeDependientes.InventarioCompleto(db.Model, ancla);

        Assert.Equal(
            excluidasQueLaReferencian,
            completo
                .Where(rama => rama.Clasificacion is ClasificacionDeDependiente.Excluido)
                .Select(rama => rama.Tabla)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));

        var sql = InspectorDeUso.Renderizar(
            completo, InventarioDeDependientes.PropiedadesDeAncla(db.Model, ancla));

        foreach (var tabla in excluidasQueLaReferencian)
        {
            Assert.DoesNotContain(tabla, sql);
        }
    }

    /// <summary>
    /// Cláusula: el camino de EJECUCIÓN falla CERRADO cuando no hay ninguna rama ejecutable, igual
    /// que <see cref="InspectorDeUso.Renderizar"/>. Devolver <c>null</c> ahí era afirmar "esta
    /// entidad está prístina" sin haber preguntado nada — falla ABIERTA, y en la dirección de
    /// pérdida de datos.
    /// </summary>
    [Fact]
    public async Task UnAnclaSinRamasEjecutablesTiraEnVezDeDevolverNull()
    {
        using var db = CrearContexto();

        var ancla = typeof(Ways.Domain.Auditoria.Auditoria);
        Assert.Empty(InventarioDeDependientes.Construir(db.Model, ancla));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new InspectorDeUso(db).PrimeraDependenciaEnUsoAsync(ancla, [], DateTimeOffset.UnixEpoch));

        Assert.Contains("ninguna rama ejecutable", error.Message);
    }

    /// <summary>
    /// Cláusula: la validación posicional de <c>valoresDeClave</c>. Un <c>null</c> llegaba sin
    /// normalizar a <c>ParametrosDeComando.Agregar</c> y reventaba con un error opaco de Npgsql;
    /// ahora se rechaza NOMBRANDO el índice y la propiedad, igual que el desajuste de cuenta.
    /// </summary>
    [Fact]
    public async Task UnValorDeClaveNuloSeRechazaNombrandoSuIndice()
    {
        using var db = CrearContexto();

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => new InspectorDeUso(db).PrimeraDependenciaEnUsoAsync(
                typeof(Tenant), [null!], DateTimeOffset.UnixEpoch));

        Assert.Contains("posición 0", error.Message);
        Assert.Contains("Id", error.Message);
    }
}
