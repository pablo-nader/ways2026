using System.Text.RegularExpressions;
using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Familias;

/// <summary>
/// El orden de las llamadas de <c>ServicioDeAgrupacionDeFamilias</c>, como aserción sobre el TEXTO FUENTE — mismo criterio que
/// <c>ServicioDeFamiliasPosicionDeLocksTests</c>: los locks son SQL crudo sobre <c>db.Database.GetDbConnection()</c> y un
/// <c>DbCommandInterceptor</c> de EF nunca los ve. Es solo UNA de las dos redes: la otra, que mira los locks reales en
/// <c>pg_locks</c> mientras el pedido espera o está pausado antes del commit, vive en
/// <c>FamiliasAgrupacionConcurrenciaTests</c>. Esta cubre lo que esa no puede ver de afuera: que el lock de membresía sea la
/// PRIMERA sentencia, que "ahora" se lea una sola vez, después de todos los locks, que las entidades se lean una sola vez y que
/// la reconciliación de lotes quede fuera de la transacción.
/// </summary>
public class ServicioDeAgrupacionDeFamiliasPosicionDeLocksTests
{
    private static string LeerFuente() =>
        File.ReadAllText(Path.Combine(
            RaizDelRepositorio.Resolver(), "src", "Ways.Application", "Familias", "ServicioDeAgrupacionDeFamilias.cs"));

    /// <summary>Los bloqueos de fila de la pertenencia viven en <c>MembresiaDeFamilias</c>, compartidos con los demás
    /// escritores de familias: el texto de sus statements se afirma sobre ese archivo.</summary>
    private static string LeerFuenteDeMembresia() =>
        File.ReadAllText(Path.Combine(
            RaizDelRepositorio.Resolver(), "src", "Ways.Application", "Familias", "MembresiaDeFamilias.cs"));

    /// <summary>El fuente sin comentarios: una mención a <c>reloj.Ahora</c> en un doc-comment no es una lectura del
    /// reloj.</summary>
    private static string FuenteSinComentarios() => Regex.Replace(LeerFuente(), @"//[^\r\n]*", string.Empty);

    /// <summary>Del primer <c>{</c> después de la firma hasta su llave de cierre.</summary>
    private static string CuerpoDe(string fuente, string firma)
    {
        var inicio = fuente.IndexOf(firma, StringComparison.Ordinal);
        Assert.True(inicio >= 0, $"No se encontró '{firma}'.");

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

        throw new InvalidOperationException($"'{firma}' no cierra sus llaves.");
    }

    /// <summary>El cuerpo del método <c>async</c>, público o privado, de ese nombre, con o sin tipo de retorno genérico.</summary>
    private static string Metodo(string nombre)
    {
        var fuente = FuenteSinComentarios();
        var firma = Regex.Match(
            fuente, $@"(?:public|private) async Task(?:<[^>]+>)?\s+{Regex.Escape(nombre)}\(", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(firma.Success, $"No se encontró el método {nombre}.");

        return CuerpoDe(fuente, firma.Value);
    }

    private static int Posicion(string cuerpo, string marca, int desde = 0)
    {
        var indice = cuerpo.IndexOf(marca, desde, StringComparison.Ordinal);
        Assert.True(indice >= 0, $"No se encontró '{marca}'.");

        return indice;
    }

    private static int Contar(string texto, string marca) => Regex.Matches(texto, Regex.Escape(marca)).Count;

    /// <summary>El lock de membresía EXCLUSIVO es lo PRIMERO que hace la transacción: entre el
    /// <c>BeginTransactionAsync</c> y él solo se arma la conexión y se toma la transacción cruda, sin ningún statement. Se afirma
    /// el texto exacto de ese tramo, con los espacios normalizados: cualquier otra sentencia que se intercale —una lectura, un
    /// lock, una escritura— lo rompe. Y no toma el compartido: agrupar cambia la pertenencia.</summary>
    [Fact]
    public void ElLockDeMembresiaExclusivoEsLaPrimeraSentenciaDespuesDeAbrirLaTransaccion()
    {
        var cuerpo = Metodo("AgruparAsync");

        const string apertura = "db.Database.BeginTransactionAsync(ct);";
        var despuesDeAbrir = Posicion(cuerpo, apertura) + apertura.Length;
        var bloqueo = Posicion(cuerpo, "LockDeMembresiaDeFamilias.TomarExclusivoAsync(");

        Assert.Equal(
            "var conexion = await ObtenerConexionAbiertaAsync(ct); " +
            "var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction(); await",
            Regex.Replace(cuerpo[despuesDeAbrir..bloqueo], @"\s+", " ").Trim());
        Assert.DoesNotContain("TomarCompartidoAsync(", cuerpo, StringComparison.Ordinal);
    }

    /// <summary>Agrupar toma los locks en el orden global de las familias y escribe después: la membresía; la familia —al
    /// agregar—; las filas de los artículos; la ÚNICA lectura de las entidades; las validaciones, con el tope exacto de
    /// destinos al agregar; los chequeos de catálogo de la referencia, con las filas ya bloqueadas; las listas fijas y el tope
    /// de pares; los locks de par; "ahora"; la planificación de la fase 1 y su rechazo; la lectura de los catálogos
    /// visibles; y recién entonces la fase 2: la familia nueva, la escritura de los artículos y de los precios, el guardado y
    /// el commit. Las lecturas son una sola vez cada una (<c>single-read-under-lock</c>).</summary>
    [Fact]
    public void AgruparTomaLosLocksEnElOrdenDelProtocoloYEscribeDespues()
    {
        var cuerpo = Metodo("AgruparAsync");

        var membresia = Posicion(cuerpo, "LockDeMembresiaDeFamilias.TomarExclusivoAsync(");
        var familia = Posicion(cuerpo, "MembresiaDeFamilias.LeerFamiliaParaIngresarAsync(");
        var filasAlAgregar = Posicion(cuerpo, "MembresiaDeFamilias.BloquearMiembrosYArticulosAsync(");
        var filasAlCrear = Posicion(cuerpo, "MembresiaDeFamilias.BloquearArticulosAsync(");
        var lecturaDeLasEntidades = Posicion(cuerpo, "db.Articulos.Where(");
        var decisionDeLaFamilia = Posicion(cuerpo, "ReglaDeFamilias.ResolverAgregado(");
        var topeExacto = Posicion(cuerpo, "ReglaDeAgrupacion.ExigirTope(");
        var existencia = Posicion(cuerpo, "new ErrorDominio(\"referencia_invalida\"");
        var otraFamilia = Posicion(cuerpo, "\"articulo_en_otra_familia\"");
        var catalogos = Posicion(cuerpo, "ExigirCatalogosDeLaReferenciaAsync(");
        var listas = Posicion(cuerpo, "servicioDePrecios.ListasFijasAsync(");
        var topeDePares = Posicion(cuerpo, "ReglaDeAgrupacion.ExigirParesAcotados(");
        var locksDePares = Posicion(cuerpo, "servicioDePrecios.TomarLocksDeParesAsync(");
        var ahora = Posicion(cuerpo, "reloj.Ahora");
        var planificacion = Posicion(cuerpo, "servicioDePrecios.PlanificarAlineacionAsync(");
        var rechazo = Posicion(cuerpo, "plan.PrimerRechazo");
        var catalogosVisibles = Posicion(cuerpo, "CatalogosVisibles.LeerAsync(");
        var familiaNueva = Posicion(cuerpo, "db.Familias.Add(");
        var primerGuardado = Posicion(cuerpo, "db.SaveChangesAsync(ct)");
        var escrituraDePrecios = Posicion(cuerpo, "servicioDePrecios.EscribirAlineacionAsync(");
        var segundoGuardado = Posicion(cuerpo, "db.SaveChangesAsync(ct)", escrituraDePrecios);
        var confirmacion = Posicion(cuerpo, "transaccion.CommitAsync(ct)");

        Assert.True(membresia < familia, "La membresía va antes que la fila de la familia.");
        Assert.True(familia < filasAlAgregar, "La familia se bloquea antes que las filas de los artículos.");
        Assert.True(membresia < filasAlCrear, "Las filas de los artículos se bloquean después de la membresía.");
        Assert.True(Math.Max(filasAlAgregar, filasAlCrear) < lecturaDeLasEntidades, "Las entidades se leen DESPUÉS de bloquear sus filas.");
        Assert.True(lecturaDeLasEntidades < decisionDeLaFamilia, "La decisión sobre la familia usa lo leído bajo el lock.");
        Assert.True(
            decisionDeLaFamilia < topeExacto && topeExacto < existencia && existencia < otraFamilia,
            "Primero la familia, después el tope exacto, la existencia y por último la pertenencia.");
        Assert.True(
            Math.Max(filasAlAgregar, filasAlCrear) < catalogos && otraFamilia < catalogos,
            "Los chequeos de catálogo van con las filas de los artículos ya bloqueadas y las validaciones del pedido hechas.");
        Assert.True(catalogos < listas && listas < topeDePares && topeDePares < locksDePares, "El tope de pares se exige con las listas leídas y antes de tomar ningún lock de par.");
        Assert.True(locksDePares < ahora, "'ahora' se lee DESPUÉS de todos los locks.");
        Assert.True(ahora < planificacion && planificacion < rechazo, "La planificación y su rechazo van después de leer 'ahora'.");
        Assert.True(rechazo < catalogosVisibles && catalogosVisibles < familiaNueva, "Ninguna escritura corre antes de resolver la fase 1.");
        Assert.True(familiaNueva < primerGuardado && primerGuardado < escrituraDePrecios, "La familia nueva se guarda antes de escribir lo que la usa.");
        Assert.True(escrituraDePrecios < segundoGuardado && segundoGuardado < confirmacion, "Un último guardado antes del commit.");

        Assert.Equal(1, Contar(cuerpo, "reloj.Ahora"));
        Assert.Equal(1, Contar(cuerpo, "db.Articulos."));
        Assert.Equal(1, Contar(cuerpo, "db.Familias.Add("));
        Assert.Equal(2, Contar(cuerpo, "db.SaveChangesAsync(ct)"));
        Assert.Equal(1, Contar(cuerpo, "servicioDePrecios.TomarLocksDeParesAsync("));
        Assert.Equal(1, Contar(cuerpo, "ExigirCatalogosDeLaReferenciaAsync("));
        Assert.Equal(1, Contar(cuerpo, "ReglaDeAgrupacion.ExigirParesAcotados("));
        Assert.Equal(1, Contar(cuerpo, "ReglaDeAgrupacion.ExigirTope("));
        Assert.Equal(1, Contar(cuerpo, "CatalogosVisibles.LeerAsync("));
    }

    /// <summary>La reconciliación de lotes corre DESPUÉS del commit y fuera de la estrategia: después del guardado de la
    /// familia y de los artículos, del commit y de la limpieza del rastreo ante un fallo. Dentro de la transacción un fallo de la
    /// reconciliación revertiría la agrupación entera, y su contrato es el de la edición de un artículo: el cambio ya está
    /// comiteado.</summary>
    [Fact]
    public void LaReconciliacionDeLotesCorreDespuesDelCommitYFueraDeLaEstrategia()
    {
        var cuerpo = Metodo("AgruparAsync");

        var confirmacion = Posicion(cuerpo, "transaccion.CommitAsync(ct)");
        var limpieza = Posicion(cuerpo, "RastreoDeEntidades.SoltarLoAgregadoDesde(");
        var reconciliacion = Posicion(cuerpo, "servicioDeLotes.ReconciliarAsync(");

        Assert.True(confirmacion < limpieza && limpieza < reconciliacion);
        Assert.Equal(1, Contar(cuerpo, "servicioDeLotes.ReconciliarAsync("));
        Assert.Contains("idPuntoVenta: null", cuerpo[reconciliacion..], StringComparison.Ordinal);
    }

    /// <summary>"Ahora" lo leen solo la previsualización y la agrupación, una vez cada una: nada más en el archivo toca el
    /// reloj. Con una lectura por artículo escrito, los miembros de una familia terminarían con <c>updated_at</c> distintos para
    /// una operación que fue una sola.</summary>
    [Fact]
    public void AhoraSoloLoLeenLaPrevisualizacionYLaAgrupacion()
    {
        var fuente = FuenteSinComentarios();

        var lectores = Regex.Matches(fuente, Regex.Escape("reloj.Ahora"))
            .Select(lectura => Regex.Matches(
                fuente[..lectura.Index], @"^    (?:public|private|internal)\b[^\r\n;=]*?\b(\w+)\s*\(", RegexOptions.Multiline).Last().Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["AgruparAsync", "PrevisualizarAsync"], lectores);
    }

    /// <summary>Crear y agregar no leen ni bloquean nada por su cuenta: validan la forma del pedido —y, al crear, chequean el
    /// nombre ANTES de abrir ninguna transacción— y delegan todo el trabajo bajo locks en <c>AgruparAsync</c>, una sola
    /// vez.</summary>
    [Theory]
    [InlineData("CrearAsync")]
    [InlineData("AgregarArticulosAsync")]
    public void CrearYAgregarSoloValidanLaFormaYDelegan(string metodo)
    {
        var cuerpo = Metodo(metodo);

        foreach (var ajeno in new[]
        {
            "LockDeMembresiaDeFamilias", "BeginTransactionAsync(", "db.Articulos", "BloquearMiembrosYArticulosAsync(",
            "BloquearArticulosAsync(", "SaveChangesAsync(", "reloj.Ahora"
        })
        {
            Assert.DoesNotContain(ajeno, cuerpo, StringComparison.Ordinal);
        }

        Assert.Equal(1, Contar(cuerpo, "AgruparAsync("));
    }

    /// <summary>El chequeo previo del nombre va antes de delegar en la transacción: es best-effort y no espera a ningún
    /// lock; el contrato real es <c>ux_familias_nombre</c>.</summary>
    [Fact]
    public void ElChequeoDelNombreVaAntesDeAbrirLaTransaccion()
    {
        var cuerpo = Metodo("CrearAsync");

        var normalizacion = Posicion(cuerpo, "NombreDeFamilia.Normalizar(");
        var chequeo = Posicion(cuerpo, "NombreDeFamilia.ExigirDisponibleAsync(");
        var delegacion = Posicion(cuerpo, "AgruparAsync(");

        Assert.True(normalizacion < chequeo && chequeo < delegacion);
        Assert.DoesNotContain("NombreDeFamilia.ExigirDisponibleAsync(", Metodo("AgruparAsync"), StringComparison.Ordinal);
        Assert.DoesNotContain("NombreDeFamilia.ExigirDisponibleAsync(", Metodo("AgregarArticulosAsync"), StringComparison.Ordinal);
    }

    /// <summary>La previsualización solo lee: ni abre una transacción, ni toma un lock, ni guarda, ni rastrea una entidad.
    /// Las pruebas contra la base lo comprueban —no escribe, no espera a ningún lock, no deja nada en el contexto—; esta es la
    /// red estructural.</summary>
    [Fact]
    public void LaPrevisualizacionNoTomaLocksNiGuardaNiRastrea()
    {
        var cuerpo = Metodo("PrevisualizarAsync");

        foreach (var ajeno in new[]
        {
            "LockDeMembresiaDeFamilias", "BeginTransactionAsync(", "BloquearMiembrosYArticulosAsync(", "BloquearArticulosAsync(",
            "TomarLocksDeParesAsync(", "EscribirAlineacionAsync(", "SaveChangesAsync(", "ReconciliarAsync(",
            "BloquearSiEstaVivaAsync("
        })
        {
            Assert.DoesNotContain(ajeno, cuerpo, StringComparison.Ordinal);
        }

        var lecturas = Regex.Matches(cuerpo, @"\bdb\.[A-Z]\w+", RegexOptions.None, TimeSpan.FromSeconds(5)).Count;
        var sinRastreo = Regex.Matches(
            cuerpo, @"\bdb\.[A-Z]\w+\s*\.AsNoTracking\(\)", RegexOptions.None, TimeSpan.FromSeconds(5)).Count;

        Assert.True(lecturas > 0);
        Assert.Equal(lecturas, sinRastreo);
    }

    private static string FuenteDeArticulosSinComentarios() =>
        Regex.Replace(
            File.ReadAllText(Path.Combine(
                RaizDelRepositorio.Resolver(), "src", "Ways.Application", "Articulos", "ServicioDeArticulos.cs")),
            @"//[^\r\n]*", string.Empty);

    /// <summary>Los chequeos de catálogo de la referencia van en el orden de la edición de artículos, y son los mismos que
    /// ella: la alícuota de IVA, el área, la categoría, el grupo y el proveedor habitual (la marca no es un campo
    /// compartido). El orden de la edición sale del texto de <c>ActualizarAsync</c>; el de agrupar, de la lista única que
    /// el pedido real y la previsualización recorren (<c>CatalogosDeLaReferencia</c>).</summary>
    [Fact]
    public void LosCatalogosDeLaReferenciaVanEnElOrdenDeLaEdicionDeArticulos()
    {
        var edicion = CuerpoDe(FuenteDeArticulosSinComentarios(), "public async Task<ArticuloListado> ActualizarAsync(");

        var enLaEdicion = Regex.Matches(
                edicion, @"Exigir(AlicuotaIva|Area|Categoria|Marca|Grupo|ProveedorHabitual)Valid[ao]Async\(", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(chequeo => chequeo.Groups[1].Value switch
            {
                "AlicuotaIva" => "AlicuotaDeIva",
                var otro => otro
            })
            .Where(catalogo => catalogo != "Marca")
            .ToList();

        var lista = CuerpoDe(FuenteSinComentarios(), "private static IEnumerable<(Catalogo Catalogo, int Id)> CatalogosDeLaReferencia(");
        var enAgrupar = Regex.Matches(lista, @"yield return \(Catalogo\.(\w+),", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(chequeo => chequeo.Groups[1].Value)
            .ToList();

        Assert.Equal(["AlicuotaDeIva", "Area", "Categoria", "Grupo", "ProveedorHabitual"], enLaEdicion);
        Assert.Equal(enLaEdicion, enAgrupar);
    }

    /// <summary>Los chequeos del pedido real toman el lock de las cuatro filas de catálogo y no el de la alícuota: el área, la
    /// categoría, el grupo y el proveedor habitual con <c>BloquearSiEstaVivaAsync</c> (<c>FOR KEY SHARE</c> sobre la fila
    /// viva, una vez cada uno) y la alícuota de IVA, que es global, con una lectura sin lock, como la edición. Rechazan con
    /// <c>referencia_invalida</c> y 400.</summary>
    [Fact]
    public void LosCatalogosDeLaReferenciaSeBloqueanForKeyShareExceptoLaAlicuotaYRechazanConReferenciaInvalida()
    {
        var cuerpo = CuerpoDe(FuenteSinComentarios(), "private async Task ExigirCatalogosDeLaReferenciaAsync(");

        foreach (var tipo in new[] { "Area", "Categoria", "Grupo", "Proveedor" })
        {
            Assert.Equal(1, Contar(cuerpo, $"guarda.BloquearSiEstaVivaAsync<{tipo}>(id, ct)"));
        }

        Assert.Equal(4, Contar(cuerpo, "guarda.BloquearSiEstaVivaAsync<"));
        Assert.Equal(1, Contar(cuerpo, "db.AlicuotasIva.AnyAsync("));
        Assert.Contains("new ErrorDominio(\"referencia_invalida\", MensajeDeCatalogoInexistente(catalogo, id), 400)", cuerpo, StringComparison.Ordinal);
    }

    /// <summary>Las filas que bloquea agrupar son las VIVAS y DEL TENANT, ascendentes por <c>id_articulo</c> en un solo
    /// statement y <c>FOR NO KEY UPDATE</c>: el orden de los locks de FILA lo produce el <c>ORDER BY</c> del statement, y un
    /// <c>FOR UPDATE</c> chocaría con el <c>FOR KEY SHARE</c> que toman las ventas por sus FK. El orden lo prueba, contra la
    /// base, que otra conexión toma sin esperar las de id más alto. El <c>id_tenant</c> explícito es redundante con RLS —la
    /// política ya esconde las filas de otro tenant—, así que ningún dato sembrado lo distingue de su ausencia: el texto
    /// es la única red. Esta es la red sobre el texto de las dos formas del statement.</summary>
    [Theory]
    [InlineData("public static async Task<List<int>> BloquearArticulosAsync(", "AND id_tenant = $2 AND deleted_at IS NULL")]
    [InlineData("public static async Task<List<int>> BloquearMiembrosYArticulosAsync(", "AND id_tenant = $3 AND deleted_at IS NULL")]
    public void LasFilasQueBloqueaAgruparSonVivasDelTenantAscendentesYConForNoKeyUpdate(string firma, string tenantYBajaLogica)
    {
        var cuerpo = CuerpoDe(LeerFuenteDeMembresia(), firma);

        Assert.Contains(tenantYBajaLogica, cuerpo, StringComparison.Ordinal);
        Assert.Contains("ORDER BY id_articulo FOR NO KEY UPDATE", cuerpo, StringComparison.Ordinal);
        Assert.DoesNotContain("FOR UPDATE", cuerpo.Replace("FOR NO KEY UPDATE", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }
}
