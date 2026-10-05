using System.Text.RegularExpressions;

namespace Ways.Application.Tests.Compras;

/// <summary>
/// stage-15-cc-proveedores-ledger, Slice 2 (task 2.9; design.md: Transactions — Total order,
/// "`proveedores` is the last row lock any transaction takes for update, and the ledger `INSERT`
/// follows it immediately"). Mutation target #19: si el lock de <c>proveedores</c>
/// (<c>EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync</c>) se moviera al paso
/// 1.5 (antes del loop de stock) dentro de <c>EjecutarConfirmarAsync</c>/
/// <c>EjecutarAnulacionAsync</c>, esta prueba debe fallar.
///
/// Resuelto con una aserción de TEXTO FUENTE, no de comportamiento — mismo criterio que los
/// mutation targets #4/#11 de slice 1 (<c>CuentaCorrienteProveedorBackfillTests</c>): un
/// <c>DbCommandInterceptor</c> de EF Core NUNCA ve los statements de
/// <c>EjecutarConfirmarAsync</c>/<c>EjecutarAnulacionAsync</c> — se crean vía
/// <c>conexion.CreateCommand()</c> sobre <c>db.Database.GetDbConnection()</c> directamente, fuera
/// del pipeline de comandos de EF Core (confirmado empíricamente: la primera versión de esta
/// prueba usaba un interceptor y <c>interceptor.Orden</c> quedó vacío en la corrida real contra
/// <c>WaysApiFixture</c> — mutation-proof-tests rule 2, "no lo razones, corré la prueba"). No hay
/// seam de runtime para rutear la prueba por debajo del confound (rule 3 exhausted first); el
/// orden de las llamadas es, literalmente, el único artefacto observable.
/// </summary>
public class ServicioDeComprasLockOrderTests
{
    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    private static string LeerFuente()
    {
        // tests/Ways.Application.Tests/Compras/ → ../../src/Ways.Application/Compras/ (mismo
        // criterio que CuentaCorrienteProveedorBackfillTests.RutaDeEsteArchivo, slice 1).
        var ruta = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "..", "src", "Ways.Application", "Compras", "ServicioDeCompras.cs");

        Assert.True(File.Exists(ruta), $"No se encontró {ruta}");
        return File.ReadAllText(ruta);
    }

    private static string ExtraerMetodo(string fuente, string firma, string siguiente)
    {
        var inicio = fuente.IndexOf(firma, StringComparison.Ordinal);
        Assert.True(inicio >= 0, $"No se encontró el método '{firma}'.");

        var fin = fuente.IndexOf(siguiente, inicio, StringComparison.Ordinal);
        Assert.True(fin > inicio, $"No se encontró '{siguiente}' después de '{firma}'.");

        return fuente[inicio..fin];
    }

    [Fact]
    public void ElLockDeSaldoDeProveedorEsElUltimoDeEjecutarConfirmarAsyncYElLedgerLoSigue()
    {
        var fuente = LeerFuente();
        var metodo = ExtraerMetodo(
            fuente, "private async Task<CompraDetalle> EjecutarConfirmarAsync(",
            "public async Task<ResultadoAnulacion> AnularAsync(");

        var indiceStockInsert = metodo.LastIndexOf("InsertarMovimientoStockAsync(", StringComparison.Ordinal);
        var indiceCostoUpdate = metodo.LastIndexOf("ActualizarCostosNominalesAsync(", StringComparison.Ordinal);
        var indiceLockProveedor = metodo.IndexOf("EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(", StringComparison.Ordinal);
        var indiceLedgerInsert = metodo.IndexOf("EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(", StringComparison.Ordinal);
        var indiceCommit = metodo.IndexOf("transaccion.CommitAsync(ct);", StringComparison.Ordinal);

        Assert.True(indiceStockInsert >= 0, "No se encontró la última llamada a InsertarMovimientoStockAsync.");
        Assert.True(indiceCostoUpdate >= 0, "No se encontró la llamada a ActualizarCostosNominalesAsync.");
        Assert.True(indiceLockProveedor >= 0, "No se encontró la llamada a ActualizarSaldoProveedorAsync.");
        Assert.True(indiceLedgerInsert >= 0, "No se encontró la llamada a InsertarMovimientoCcProveedorAsync.");
        Assert.True(indiceCommit >= 0, "No se encontró el commit de EjecutarConfirmarAsync.");

        Assert.True(
            indiceLockProveedor > indiceStockInsert,
            "El lock de proveedores debe aparecer DESPUÉS del último InsertarMovimientoStockAsync (mutation target #19).");
        Assert.True(
            indiceLockProveedor > indiceCostoUpdate,
            "El lock de proveedores debe aparecer DESPUÉS de ActualizarCostosNominalesAsync (mutation target #19).");
        Assert.True(
            indiceLedgerInsert > indiceLockProveedor,
            "El INSERT del ledger de proveedor debe seguir inmediatamente al lock de saldo.");
        Assert.True(
            indiceCommit > indiceLedgerInsert,
            "El commit de confirmar debe ser posterior al INSERT del ledger de proveedor.");
    }

    [Fact]
    public void ElLockDeSaldoDeProveedorEsElUltimoDeEjecutarAnulacionAsyncYElLedgerLoSigue()
    {
        var fuente = LeerFuente();
        var metodo = ExtraerMetodo(
            fuente, "private async Task<ResultadoAnulacion> EjecutarAnulacionAsync(",
            "// ---- aplicar precio sugerido");

        var indiceGastosLigados = metodo.IndexOf("db.Gastos.CountAsync(g => g.IdComprobanteCompra == id, ct);", StringComparison.Ordinal);
        var indiceLockProveedor = metodo.IndexOf("EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(", StringComparison.Ordinal);
        var indiceLedgerInsert = metodo.IndexOf("EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(", StringComparison.Ordinal);
        var indiceCommit = metodo.IndexOf("transaccion.CommitAsync(ct);", StringComparison.Ordinal);

        Assert.True(indiceGastosLigados >= 0, "No se encontró el conteo informativo de gastosLigados.");
        Assert.True(indiceLockProveedor >= 0, "No se encontró la llamada a ActualizarSaldoProveedorAsync.");
        Assert.True(indiceLedgerInsert >= 0, "No se encontró la llamada a InsertarMovimientoCcProveedorAsync.");
        Assert.True(indiceCommit >= 0, "No se encontró el commit de EjecutarAnulacionAsync.");

        Assert.True(
            indiceLockProveedor > indiceGastosLigados,
            "El lock de proveedores debe aparecer DESPUÉS del conteo informativo de gastosLigados (mutation target #19).");
        Assert.True(
            indiceLedgerInsert > indiceLockProveedor,
            "El INSERT del ledger de proveedor debe seguir inmediatamente al lock de saldo.");
        Assert.True(
            indiceCommit > indiceLedgerInsert,
            "El commit de anular debe ser posterior al INSERT del ledger de proveedor.");
    }

    // =================================================================================================
    // Familias de artículos (doc 10 §3): el protocolo de locks de la confirmación
    // =================================================================================================

    private static string EjecutarConfirmar() =>
        ExtraerMetodo(
            LeerFuente(), "private async Task<CompraDetalle> EjecutarConfirmarAsync(",
            "public async Task<ResultadoAnulacion> AnularAsync(");

    private static string SinComentarios(string fuente) => Regex.Replace(fuente, @"//[^\r\n]*", string.Empty);

    private static int Contar(string texto, string marca) =>
        Regex.Matches(texto, Regex.Escape(marca)).Count;

    /// <summary>De la primera <c>{</c> que sigue a <paramref name="inicio"/> hasta su llave de cierre.</summary>
    private static string CuerpoDeLlaves(string fuente, int inicio)
    {
        var apertura = fuente.IndexOf('{', inicio);
        Assert.True(apertura >= 0, "No se encontró la llave de apertura.");

        var profundidad = 0;

        for (var i = apertura; i < fuente.Length; i++)
        {
            profundidad += fuente[i] switch { '{' => 1, '}' => -1, _ => 0 };

            if (profundidad == 0)
            {
                return fuente[apertura..(i + 1)];
            }
        }

        throw new InvalidOperationException("Las llaves no cierran.");
    }

    private static string Normalizado(string texto) => Regex.Replace(texto, @"\s+", " ").Trim();

    private static readonly Regex CabeceraDeCiclo = new(
        @"\b(?:foreach|for|while)\s*\(|\bdo\s*\{", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>El índice del <c>)</c> que cierra el <c>(</c> que está en <paramref name="apertura"/>.</summary>
    private static int CierreDelParentesis(string texto, int apertura)
    {
        var profundidad = 0;

        for (var i = apertura; i < texto.Length; i++)
        {
            profundidad += texto[i] switch { '(' => 1, ')' => -1, _ => 0 };

            if (profundidad == 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Los paréntesis no cierran.");
    }

    /// <summary>Si <paramref name="indice"/> cae dentro del cuerpo de alguna instrucción de ciclo de
    /// <paramref name="texto"/>. Ve <c>foreach</c>, <c>for</c> y <c>while</c> con el cuerpo entre llaves o con
    /// una sentencia simple sin llaves —hasta el primer <c>;</c> que sigue a la cabecera—, y <c>do</c> solo con
    /// el cuerpo entre llaves. No ve un <c>do</c> sin llaves; no ve, de un cuerpo sin llaves que es otra
    /// sentencia con llaves (un <c>if</c>, un <c>lock</c>), lo que viene después del primer <c>;</c> de ese
    /// bloque; y no ve una repetición hecha de otra manera, con LINQ o con recursión.</summary>
    private static bool EstaDentroDeUnCiclo(string texto, int indice)
    {
        foreach (Match cabecera in CabeceraDeCiclo.Matches(texto))
        {
            int inicioDelCuerpo;

            if (cabecera.Value.StartsWith("do", StringComparison.Ordinal))
            {
                inicioDelCuerpo = cabecera.Index + cabecera.Length - 1;
            }
            else
            {
                inicioDelCuerpo = CierreDelParentesis(texto, cabecera.Index + cabecera.Length - 1) + 1;

                while (char.IsWhiteSpace(texto[inicioDelCuerpo]))
                {
                    inicioDelCuerpo++;
                }
            }

            var finDelCuerpo = texto[inicioDelCuerpo] == '{'
                ? inicioDelCuerpo + CuerpoDeLlaves(texto, inicioDelCuerpo).Length - 1
                : texto.IndexOf(';', inicioDelCuerpo);

            if (indice >= inicioDelCuerpo && indice <= finDelCuerpo)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>La detección de ciclos que usan las pruebas de abajo ve el cuerpo entre llaves de cada una de las
    /// cuatro instrucciones (<c>foreach</c>, <c>for</c>, <c>while</c> y <c>do</c>) y el cuerpo de una sola
    /// sentencia sin llaves de las tres primeras, y no marca lo que está fuera de ellos ni dentro de un
    /// <c>if</c>. Lo que no ve lo fija <see cref="LaDeteccionDeCiclosNoVeUnDoSinLlavesNiLoQueSigueAlPrimerPuntoYComa"/>.</summary>
    [Theory]
    [InlineData("foreach (var x in xs) { Llamar(x); }", true)]
    [InlineData("foreach (var x in xs) { Otra(x); Llamar(x); }", true)]
    [InlineData("foreach (var x in xs) { if (y) { Llamar(x); } }", true)]
    [InlineData("foreach (var x in xs) Llamar(x);", true)]
    [InlineData("for (var i = 0; i < n; i++) { Llamar(i); }", true)]
    [InlineData("while (hay) { Llamar(); }", true)]
    [InlineData("do { Llamar(); } while (hay);", true)]
    [InlineData("Llamar(); foreach (var x in xs) { Otra(x); }", false)]
    [InlineData("foreach (var x in xs) { Otra(x); } Llamar();", false)]
    [InlineData("if (hay) { Llamar(); }", false)]
    public void LaDeteccionDeCiclosVeElCuerpoDeCadaInstruccionDeCiclo(string fuente, bool dentro)
    {
        var indice = fuente.IndexOf("Llamar(", StringComparison.Ordinal);

        Assert.True(indice >= 0);
        Assert.Equal(dentro, EstaDentroDeUnCiclo(fuente, indice));
    }

    /// <summary>Lo que la detección de ciclos NO ve, tal como lo declara la documentación de
    /// <see cref="EstaDentroDeUnCiclo"/>. Cada fila es código que SÍ está dentro de un ciclo y la detección no lo
    /// marca: un <c>do</c> sin llaves, y un ciclo sin llaves cuya sentencia es otra sentencia con llaves, de la que
    /// solo ve hasta el primer <c>;</c> del bloque. Si la detección pasa a ver alguno de estos casos, la fila y la
    /// documentación tienen que cambiar juntas.</summary>
    [Theory]
    [InlineData("do Llamar(); while (hay);")]
    [InlineData("foreach (var x in xs) if (y) { Otra(x); Llamar(x); }")]
    [InlineData("while (hay) lock (candado) { Otra(); Llamar(); }")]
    public void LaDeteccionDeCiclosNoVeUnDoSinLlavesNiLoQueSigueAlPrimerPuntoYComa(string fuente)
    {
        var indice = fuente.IndexOf("Llamar(", StringComparison.Ordinal);

        Assert.True(indice >= 0);
        Assert.False(EstaDentroDeUnCiclo(fuente, indice));
    }

    /// <summary>El lock de membresía de familias es lo PRIMERO que hace la transacción de la confirmación: lo
    /// único que hay entre el <c>BeginTransactionAsync</c> y él es armar la conexión y tomar la transacción cruda,
    /// sin ningún statement, y el <c>UPDATE</c> del encabezado viene después. Se afirma el texto EXACTO de ese
    /// tramo, sin comentarios y con los espacios normalizados: cualquier otra sentencia que se intercale —una
    /// lectura, un lock, una escritura— lo rompe. Que además espere ahí, sin haber escrito ni tomado ningún otro
    /// lock, lo prueba contra <c>pg_locks</c> <c>ComprasConFamiliasTests</c>.</summary>
    [Fact]
    public void ElLockDeMembresiaCompartidoEsLaPrimeraSentenciaDeEjecutarConfirmarAsync()
    {
        var metodo = EjecutarConfirmar();

        const string apertura = "db.Database.BeginTransactionAsync(ct);";
        var indiceApertura = metodo.IndexOf(apertura, StringComparison.Ordinal);
        var indiceLock = metodo.IndexOf("LockDeMembresiaDeFamilias.TomarCompartidoAsync(", StringComparison.Ordinal);
        var indiceEncabezado = metodo.IndexOf("ConfirmarHeaderAsync(", StringComparison.Ordinal);

        Assert.True(indiceApertura >= 0, "No se encontró la apertura de la transacción.");
        Assert.True(indiceLock > indiceApertura, "El lock de membresía tiene que venir después de abrir la transacción.");
        Assert.True(indiceEncabezado > indiceLock, "El UPDATE del encabezado tiene que venir DESPUÉS del lock de membresía.");

        var entre = Regex.Replace(
            SinComentarios(metodo[(indiceApertura + apertura.Length)..indiceLock]), @"\s+", " ").Trim();

        Assert.Equal(
            "var conexion = await ObtenerConexionAbiertaAsync(ct); " +
            "var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction(); await",
            entre);
    }

    /// <summary>El costo de las familias se escribe en el orden del protocolo: la pertenencia se lee y las filas
    /// de los artículos con costo se bloquean DESPUÉS del último movimiento de stock, antes de escribir el costo y
    /// antes del lock de proveedores, que sigue siendo el último lock de fila de la transacción.</summary>
    [Fact]
    public void LasFilasConCostoSeBloqueanDespuesDelStockYAntesDeEscribirElCostoYDelLockDeProveedores()
    {
        var metodo = EjecutarConfirmar();

        var indiceStock = metodo.LastIndexOf("InsertarMovimientoStockAsync(", StringComparison.Ordinal);
        var indiceFamilias = metodo.IndexOf("LeerFamiliasDeLosArticulosAsync(", StringComparison.Ordinal);
        var indiceBloqueo = metodo.IndexOf("BloquearArticulosConCostoAsync(", StringComparison.Ordinal);
        var indiceCosto = metodo.LastIndexOf("ActualizarCostosNominalesAsync(", StringComparison.Ordinal);
        var indiceProveedores = metodo.IndexOf(
            "EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(", StringComparison.Ordinal);

        Assert.True(indiceStock >= 0 && indiceFamilias >= 0 && indiceBloqueo >= 0 && indiceCosto >= 0 && indiceProveedores >= 0);
        Assert.True(indiceStock < indiceFamilias, "La pertenencia se lee después del stock.");
        Assert.True(indiceFamilias < indiceBloqueo, "Las filas se bloquean con la pertenencia ya leída.");
        Assert.True(indiceBloqueo < indiceCosto, "El costo se escribe con las filas ya bloqueadas.");
        Assert.True(indiceCosto < indiceProveedores, "El lock de proveedores sigue siendo el último.");
    }

    /// <summary>Si ninguna línea actualiza el costo el paso entero se salta: la lectura de la pertenencia, el
    /// bloqueo de las filas y la escritura del costo están dentro de UN <c>if</c> sobre las líneas que cuentan según
    /// <c>CalculadorDeCompra.ActualizaElCosto</c>, y ninguna de las tres llamadas aparece fuera de él. Son
    /// statements crudos que un interceptor de EF no ve —y que no cambian ningún resultado visible: sin líneas
    /// que cuenten tampoco habría filas que bloquear—, así que el texto fuente es la única red de que no se
    /// paguen esas idas a la base.</summary>
    [Fact]
    public void ElPasoDelCostoSeSaltaEnteroSiNingunaLineaActualizaElCosto()
    {
        var metodo = SinComentarios(EjecutarConfirmar());

        const string guarda = "if (idsConCosto.Count > 0)";
        var indiceGuarda = metodo.IndexOf(guarda, StringComparison.Ordinal);
        Assert.True(indiceGuarda >= 0, "No se encontró la guarda del paso del costo.");
        Assert.Equal(1, Contar(metodo, guarda));
        Assert.Contains(".Where(CalculadorDeCompra.ActualizaElCosto)", metodo, StringComparison.Ordinal);

        var cuerpo = CuerpoDeLlaves(metodo, indiceGuarda);

        foreach (var llamada in new[]
        {
            "LeerFamiliasDeLosArticulosAsync(", "BloquearArticulosConCostoAsync(", "ActualizarCostosNominalesAsync("
        })
        {
            Assert.Equal(1, Contar(cuerpo, llamada));
            Assert.Equal(1, Contar(metodo, llamada));
        }
    }

    /// <summary>El costo se escribe con UN solo <c>UPDATE … FROM unnest</c> para todas las filas ya bloqueadas, y
    /// no con uno por artículo. Sobre el texto fuente: la confirmación llama al escritor en una única llamada,
    /// escrita fuera de cualquier ciclo que <see cref="EstaDentroDeUnCiclo"/> reconoce (sus límites están en su
    /// documentación) y con la lista completa que devolvió el bloqueo; el texto del escritor no contiene
    /// <c>foreach</c>, <c>for (</c> ni <c>while</c> y emite un único <c>ExecuteNonQueryAsync</c>; y el escritor
    /// por fila ya no existe.</summary>
    [Fact]
    public void ElCostoSeEscribeConUnSoloUpdateParaTodasLasFilasBloqueadas()
    {
        var metodo = SinComentarios(EjecutarConfirmar());

        const string llamada = "ActualizarCostosNominalesAsync(";
        Assert.Equal(1, Contar(metodo, llamada));
        Assert.False(
            EstaDentroDeUnCiclo(metodo, metodo.IndexOf(llamada, StringComparison.Ordinal)),
            "El escritor del costo se llama dentro de un ciclo: escribiría con un UPDATE por vuelta.");

        var normalizado = Normalizado(metodo);
        Assert.Contains("var articulosConCosto = await BloquearArticulosConCostoAsync(", normalizado, StringComparison.Ordinal);
        Assert.Contains(
            "ActualizarCostosNominalesAsync(conexion, transaccionCruda, idTenant, articulosConCosto, momento, ct);",
            normalizado, StringComparison.Ordinal);

        Assert.DoesNotContain("ActualizarCostoNominalAsync(", LeerFuente(), StringComparison.Ordinal);

        var escritor = SinComentarios(ExtraerMetodo(
            LeerFuente(), "private static async Task ActualizarCostosNominalesAsync(",
            "private async Task<DbConnection> ObtenerConexionAbiertaAsync("));

        Assert.Contains("FROM unnest($2::int[], $3::numeric[])", escritor, StringComparison.Ordinal);
        Assert.Equal(1, Contar(escritor, "ExecuteNonQueryAsync("));

        foreach (var ciclo in new[] { "foreach", "for (", "while" })
        {
            Assert.DoesNotContain(ciclo, escritor, StringComparison.Ordinal);
        }
    }

    /// <summary>El escritor del costo no ejecuta ningún statement si no recibe artículos: la guarda
    /// <c>articulos.Count == 0</c> sale con un <c>return</c> antes de crear el comando. Llega a ella un bloqueo sin
    /// filas, por ejemplo si el único artículo con costo es el único miembro vivo de su familia y se da de baja
    /// entre la lectura de la pertenencia y el bloqueo. Sin la guarda correría un <c>UPDATE</c> sobre arreglos
    /// vacíos que no escribe nada: no cambia ningún resultado y solo ahorra una ida a la base, y como el comando es
    /// crudo y un interceptor de EF no lo ve, el texto fuente es la única red. Se afirma que la guarda aparece una
    /// vez, que su cuerpo es solo el <c>return</c> y que viene antes de <c>CreateCommand</c>.</summary>
    [Fact]
    public void ElEscritorDelCostoNoCreaNingunComandoSiNoRecibeArticulos()
    {
        var escritor = SinComentarios(ExtraerMetodo(
            LeerFuente(), "private static async Task ActualizarCostosNominalesAsync(",
            "private async Task<DbConnection> ObtenerConexionAbiertaAsync("));

        const string guarda = "if (articulos.Count == 0)";
        Assert.Equal(1, Contar(escritor, guarda));

        var indiceGuarda = escritor.IndexOf(guarda, StringComparison.Ordinal);
        var indiceComando = escritor.IndexOf("CreateCommand(", StringComparison.Ordinal);

        Assert.Equal("{ return; }", Normalizado(CuerpoDeLlaves(escritor, indiceGuarda)));
        Assert.True(indiceComando >= 0, "No se encontró la creación del comando.");
        Assert.True(indiceGuarda < indiceComando, "La guarda tiene que salir ANTES de crear el comando.");
    }

    /// <summary>Las filas a las que se escribe el costo se bloquean en UN statement ordenado por id y con
    /// <c>FOR NO KEY UPDATE</c>: es el orden en que PostgreSQL toma los locks, el que evita el ciclo con otro
    /// escritor de la misma familia, y nunca <c>FOR UPDATE</c>, que chocaría con el <c>FOR KEY SHARE</c> de las
    /// ventas. Lo prueban, contra la base, <c>ComprasConFamiliasTests</c>.</summary>
    [Fact]
    public void ElBloqueoDeLasFilasConCostoEsAscendentePorIdYConForNoKeyUpdate()
    {
        var cuerpo = ExtraerMetodo(
            LeerFuente(), "private static async Task<List<(int IdArticulo, decimal Costo)>> BloquearArticulosConCostoAsync(",
            "/// <summary>Escribe <c>costo_nominal</c>");

        Assert.Contains("ORDER BY id_articulo FOR NO KEY UPDATE", cuerpo, StringComparison.Ordinal);
        Assert.DoesNotContain("FOR UPDATE", cuerpo.Replace("FOR NO KEY UPDATE", string.Empty), StringComparison.Ordinal);
    }
}
