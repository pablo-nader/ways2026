namespace Ways.Application.Tests.Compras;

/// <summary>
/// stage-16-ordenes-de-compra, Slice 3 (design decisiones 6/9; Transactions — CONFIRMAR COMPRA/
/// ANULAR COMPRA; mutation targets #21, #29). Complementa
/// <see cref="ServicioDeComprasLockOrderTests"/> con las dos aserciones nuevas del lock de OC:
/// posición 2 (inmediatamente después del header, ANTES de lotes/stock/proveedores) y que la
/// llamada está SIEMPRE detrás del null-check <c>if (encabezado.IdOrdenCompra is { } idOc)</c>.
///
/// Resuelto con aserción de TEXTO FUENTE, no de comportamiento — mismo criterio documentado en
/// <c>ServicioDeComprasLockOrderTests</c>: <c>EscriturasDeOrdenDeCompra.BloquearYExigirNoAnuladaAsync</c>/
/// <c>ProyectarEstadoAsync</c> corren sobre un <c>DbCommand</c> crudo (<c>conexion.CreateCommand()</c>),
/// fuera del pipeline de <c>DbCommandInterceptor</c> de EF Core — verificado empíricamente por esa
/// misma clase (mutation-proof-tests rule 2/3). Por la misma razón, la "binding gate test (a)
/// zero-extra-statements" del design tampoco puede probarse con un contador de comandos EF
/// (<c>ContadorDeComandos</c>, usado en otras slices, solo ve statements EMITIDOS POR EF — un
/// <c>ContadorDeComandos</c> adjunto a un confirm sin OC ligada vería el MISMO conteo con o sin
/// este bloque, porque sus statements son crudos y por lo tanto invisibles para el interceptor
/// tanto si corren como si no): la prueba real de "cero statements extra" es esta aserción
/// estructural (el bloque entero, statements incluidos, nunca corre si <c>IdOrdenCompra</c> es
/// <c>null</c> — es un <c>if</c>, no una condición dentro del SQL) MÁS la prueba comportamental de
/// <c>ServicioDeComprasLigaduraTests.UnConfirmSinOrdenLigadaNoTocaNingunaOrdenDeCompraExistente</c>
/// (una OC hermana permanece byte-idéntica). Registrado como el mismo escape hatch que
/// <c>mutation-proof-tests</c> regla 3 pre-autoriza en tasks.md task 3.24.
/// </summary>
public class EscriturasDeOrdenDeCompraLockOrderTests
{
    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    private static string LeerFuente()
    {
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
    public void ElGuardDeLaOrdenDeCompraEstaEnPosicion2DeEjecutarConfirmarAsyncAntesDeLotesStockYProveedores()
    {
        var fuente = LeerFuente();
        var metodo = ExtraerMetodo(
            fuente, "private async Task<CompraDetalle> EjecutarConfirmarAsync(",
            "// ---- anular (design: Transactions");

        var indiceHeaderLock = metodo.IndexOf("ConfirmarHeaderAsync(conexion, transaccionCruda, id, idTenant, momento, ct)", StringComparison.Ordinal);
        var indiceGuardNulo = metodo.IndexOf("if (encabezado.IdOrdenCompra is { } idOc)", StringComparison.Ordinal);
        var indiceBloquearNoAnulada = metodo.IndexOf("EscriturasDeOrdenDeCompra.BloquearYExigirNoAnuladaAsync(", StringComparison.Ordinal);
        var indiceProyectar = metodo.IndexOf("EscriturasDeOrdenDeCompra.ProyectarEstadoAsync(", StringComparison.Ordinal);
        var indiceItemsQuery = metodo.IndexOf("db.ItemsComprobanteCompra", StringComparison.Ordinal);
        var indiceLotes = metodo.IndexOf("ServicioDeLotes.ResolverOCrearAsync(", StringComparison.Ordinal);
        var indiceStock = metodo.IndexOf("InsertarMovimientoStockAsync(", StringComparison.Ordinal);
        var indiceProveedores = metodo.IndexOf("EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(", StringComparison.Ordinal);

        Assert.True(indiceHeaderLock >= 0, "No se encontró el lock del header (ConfirmarHeaderAsync).");
        Assert.True(indiceGuardNulo >= 0, "No se encontró el null-check guard de IdOrdenCompra.");
        Assert.True(indiceBloquearNoAnulada >= 0, "No se encontró la llamada a BloquearYExigirNoAnuladaAsync.");
        Assert.True(indiceProyectar >= 0, "No se encontró la llamada a ProyectarEstadoAsync.");
        Assert.True(indiceItemsQuery >= 0, "No se encontró el read set de items (paso 2).");
        Assert.True(indiceLotes >= 0, "No se encontró la resolución de lotes (paso 2.b).");
        Assert.True(indiceStock >= 0, "No se encontró InsertarMovimientoStockAsync (paso 3).");
        Assert.True(indiceProveedores >= 0, "No se encontró el lock de proveedores (paso 5).");

        // El guard nulo va DESPUÉS del lock del header, y el lock/guard de la OC vive DENTRO de ese
        // bloque — posición 2 (mutation target #21).
        Assert.True(indiceGuardNulo > indiceHeaderLock, "El guard de la OC debe ir DESPUÉS del lock del header.");
        Assert.True(
            indiceBloquearNoAnulada > indiceGuardNulo && indiceBloquearNoAnulada < indiceItemsQuery,
            "BloquearYExigirNoAnuladaAsync debe estar DENTRO del guard nulo y ANTES del read set de items.");
        Assert.True(
            indiceProyectar > indiceBloquearNoAnulada && indiceProyectar < indiceItemsQuery,
            "ProyectarEstadoAsync debe correr DESPUÉS de BloquearYExigirNoAnuladaAsync y ANTES del read set de items.");

        // ANTES de lotes/stock/proveedores — nunca después (mutation target #21: moverlo tras
        // proveedores debe hacer fallar el rendezvous de confirm×confirm por deadlock).
        Assert.True(indiceProyectar < indiceLotes, "El lock de la OC debe preceder a la resolución de lotes.");
        Assert.True(indiceProyectar < indiceStock, "El lock de la OC debe preceder al primer InsertarMovimientoStockAsync.");
        Assert.True(indiceProyectar < indiceProveedores, "El lock de la OC debe preceder al lock de proveedores (design decisión 3/6).");
    }

    [Fact]
    public void LasLlamadasAEscriturasDeOrdenDeCompraEnConfirmarNuncaOcurrenFueraDelGuardNulo()
    {
        var fuente = LeerFuente();
        var metodo = ExtraerMetodo(
            fuente, "private async Task<CompraDetalle> EjecutarConfirmarAsync(",
            "// ---- anular (design: Transactions");

        // mutation target #29: si el guard `if (encabezado.IdOrdenCompra is { } idOc)` se llamara
        // incondicionalmente, esta aserción de conteo lo detecta — EL ÚNICO lugar donde
        // "EscriturasDeOrdenDeCompra." puede aparecer en todo el método es dentro del bloque del
        // guard. OJO: `is { } idOc` ya contiene su propio par `{ }` (el patrón "not-null") ANTES
        // de la llave del bloque — buscar la primera `{`/`}` desde `indiceGuardNulo` encontraría
        // ESE par, no el del bloque. Se ancla al final literal del `if (...)` para saltarlo.
        const string guardIfConfirmar = "if (encabezado.IdOrdenCompra is { } idOc)";
        var indiceGuardNulo = metodo.IndexOf(guardIfConfirmar, StringComparison.Ordinal);
        Assert.True(indiceGuardNulo >= 0, "No se encontró el guard nulo.");
        var indiceAperturaDelGuard = metodo.IndexOf('{', indiceGuardNulo + guardIfConfirmar.Length);
        var indiceCierreDelGuard = metodo.IndexOf('}', indiceAperturaDelGuard);
        Assert.True(indiceAperturaDelGuard > 0 && indiceCierreDelGuard > indiceAperturaDelGuard, "No se pudo delimitar el bloque del guard nulo.");

        var antesDelGuard = metodo[..indiceGuardNulo];
        var dentroDelGuard = metodo[indiceGuardNulo..(indiceCierreDelGuard + 1)];
        var despuesDelGuard = metodo[(indiceCierreDelGuard + 1)..];

        // Se busca la FORMA de LLAMADA real (con el argumento `conexion` pegado al paréntesis),
        // no el nombre pelado de la clase — los doc-comments de esta misma clase mencionan
        // "EscriturasDeOrdenDeCompra" en prosa (referencian el método por nombre), lo que un
        // `DoesNotContain` sobre el prefijo pelado confundiría con una llamada real.
        Assert.DoesNotContain("EscriturasDeOrdenDeCompra.BloquearYExigirNoAnuladaAsync(conexion", antesDelGuard, StringComparison.Ordinal);
        Assert.DoesNotContain("EscriturasDeOrdenDeCompra.ProyectarEstadoAsync(conexion", antesDelGuard, StringComparison.Ordinal);
        Assert.DoesNotContain("EscriturasDeOrdenDeCompra.BloquearYExigirNoAnuladaAsync(conexion", despuesDelGuard, StringComparison.Ordinal);
        Assert.DoesNotContain("EscriturasDeOrdenDeCompra.ProyectarEstadoAsync(conexion", despuesDelGuard, StringComparison.Ordinal);
        Assert.Contains("EscriturasDeOrdenDeCompra.BloquearYExigirNoAnuladaAsync(conexion", dentroDelGuard, StringComparison.Ordinal);
        Assert.Contains("EscriturasDeOrdenDeCompra.ProyectarEstadoAsync(conexion", dentroDelGuard, StringComparison.Ordinal);
    }

    [Fact]
    public void ElGuardDeLaOrdenDeCompraEnEjecutarAnulacionAsyncVaDespuesDeLaAuditoriaYAntesDeLaReversaDeStock()
    {
        var fuente = LeerFuente();
        var metodo = ExtraerMetodo(
            fuente, "private async Task<ResultadoAnulacion> EjecutarAnulacionAsync(",
            "// ---- aplicar precio sugerido");

        var indiceAuditoria = metodo.IndexOf("servicioDeAuditoriaAnulacionCompra.RegistrarAsync(", StringComparison.Ordinal);
        var indiceGuardNulo = metodo.IndexOf("if (encabezadoAnulado.Value.IdOrdenCompra is { } idOc)", StringComparison.Ordinal);
        var indiceProyectar = metodo.IndexOf("EscriturasDeOrdenDeCompra.ProyectarEstadoAsync(", StringComparison.Ordinal);
        var indiceReversaDeStock = metodo.IndexOf("db.MovimientosStock", StringComparison.Ordinal);
        var indiceProveedores = metodo.IndexOf("EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(", StringComparison.Ordinal);

        Assert.True(indiceAuditoria >= 0, "No se encontró el registro de auditoría.");
        Assert.True(indiceGuardNulo >= 0, "No se encontró el null-check guard de IdOrdenCompra en anular.");
        Assert.True(indiceProyectar >= 0, "No se encontró la llamada a ProyectarEstadoAsync en anular.");
        Assert.True(indiceReversaDeStock >= 0, "No se encontró la reversa de stock (paso 2).");
        Assert.True(indiceProveedores >= 0, "No se encontró el lock de proveedores (paso 6).");

        Assert.True(indiceGuardNulo > indiceAuditoria, "El guard de la OC debe ir DESPUÉS de la auditoría (que no lockea nada).");
        Assert.True(
            indiceProyectar > indiceGuardNulo && indiceProyectar < indiceReversaDeStock,
            "ProyectarEstadoAsync debe estar DENTRO del guard nulo y ANTES de la reversa de stock.");
        Assert.True(indiceProyectar < indiceProveedores, "El lock de la OC debe preceder al lock de proveedores en anular.");

        var antesDelGuard = metodo[..indiceGuardNulo];
        Assert.DoesNotContain("EscriturasDeOrdenDeCompra.ProyectarEstadoAsync(conexion", antesDelGuard, StringComparison.Ordinal);
    }
}
