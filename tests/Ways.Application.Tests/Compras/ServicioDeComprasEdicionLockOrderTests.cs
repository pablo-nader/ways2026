using System.Text.RegularExpressions;

namespace Ways.Application.Tests.Compras;

/// <summary>
/// E6-S2: orden de locks y lecturas de la edición de una compra confirmada
/// (<c>ServicioDeCompras.EjecutarEdicionConfirmadaAsync</c>). Aserciones de TEXTO FUENTE, con el mismo criterio que
/// <see cref="ServicioDeComprasLockOrderTests"/>: los statements son SQL crudo sobre la conexión de la transacción, fuera del
/// pipeline de comandos de EF, y no hay un seam de runtime por debajo del cual rutear la prueba; el orden de las llamadas es
/// el único artefacto observable. Mutación: mover cualquiera de las llamadas nombradas fuera de su lugar pone en rojo la
/// aserción que la nombra.
/// </summary>
public class ServicioDeComprasEdicionLockOrderTests
{
    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    private static string LeerFuente()
    {
        var ruta = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "..", "src", "Ways.Application", "Compras", "ServicioDeCompras.Edicion.cs");

        Assert.True(File.Exists(ruta), $"No se encontró {ruta}");
        return File.ReadAllText(ruta);
    }

    private static string SinComentarios(string fuente) => Regex.Replace(fuente, @"//[^\r\n]*", string.Empty);

    private static string Ejecucion()
    {
        var fuente = LeerFuente();
        const string firma = "private async Task<CompraDetalle> EjecutarEdicionConfirmadaAsync(";
        var inicio = fuente.IndexOf(firma, StringComparison.Ordinal);
        Assert.True(inicio >= 0, $"No se encontró '{firma}'.");

        var fin = fuente.IndexOf("private static string DetalleDeLaEdicion(", inicio, StringComparison.Ordinal);
        Assert.True(fin > inicio, "No se encontró el final de EjecutarEdicionConfirmadaAsync.");

        return SinComentarios(fuente[inicio..fin]);
    }

    private static int Indice(string metodo, string marca)
    {
        var indice = metodo.IndexOf(marca, StringComparison.Ordinal);
        Assert.True(indice >= 0, $"No se encontró '{marca}'.");
        return indice;
    }

    [Fact]
    public void ElLockDeMembresiaCompartidoEsLaPrimeraSentenciaDeLaEdicion()
    {
        var metodo = Ejecucion();

        const string apertura = "db.Database.BeginTransactionAsync(ct);";
        var indiceApertura = Indice(metodo, apertura);
        var indiceLock = Indice(metodo, "LockDeMembresiaDeFamilias.TomarCompartidoAsync(");

        Assert.True(indiceLock > indiceApertura);
        Assert.True(
            Indice(metodo, "BloquearConfirmadaAsync(") > indiceLock,
            "El FOR UPDATE del encabezado tiene que venir DESPUÉS del lock de membresía.");

        var entre = Regex.Replace(metodo[(indiceApertura + apertura.Length)..indiceLock], @"\s+", " ").Trim();

        Assert.Equal(
            "var conexion = await ObtenerConexionAbiertaAsync(ct); " +
            "var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction(); await",
            entre);
    }

    [Fact]
    public void ElOrdenDeLosLocksEsMembresiaEncabezadoOrdenesLotesStockYProveedoresAlFinal()
    {
        var metodo = Ejecucion();

        var membresia = Indice(metodo, "LockDeMembresiaDeFamilias.TomarCompartidoAsync(");
        var encabezado = Indice(metodo, "BloquearConfirmadaAsync(");
        var ordenes = Indice(metodo, "EscriturasDeOrdenDeCompra.BloquearAsync(");
        var pagos = Indice(metodo, "BloquearPagosDeLaEdicionAsync(");
        var lotes = Indice(metodo, "ServicioDeLotes.ResolverOCrearAsync(");
        var stock = Indice(metodo, "AsentarDiferenciasDeStockAsync(");
        var costos = Indice(metodo, "ActualizarCostosDeLaEdicionAsync(");
        var codigos = Indice(metodo, "AsociarCodigoDeProveedorSiEstaLibreAsync(");
        var escritura = Indice(metodo, "await db.SaveChangesAsync(ct);");
        var proyeccion = Indice(metodo, "EscriturasDeOrdenDeCompra.ProyectarEstadoAsync(");
        var auditoria = Indice(metodo, "RegistrarAsync(");
        var proveedor = Indice(metodo, "EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(");
        var asiento = Indice(metodo, "EscriturasDeCuentaCorrienteProveedor.InsertarMovimientoCcProveedorAsync(");
        var commit = Indice(metodo, "transaccion.CommitAsync(ct);");

        Assert.True(membresia < encabezado, "membresía → encabezado");
        Assert.True(encabezado < ordenes, "encabezado → órdenes de compra");
        Assert.True(ordenes < pagos, "órdenes → pagos (E6-S3)");
        Assert.True(pagos < lotes, "pagos → lotes");
        Assert.True(lotes < stock, "lotes → stock (design decisión 3 de la etapa 12)");
        Assert.True(stock < costos, "stock → costos (E6-S4, igual que la confirmación)");
        Assert.True(costos < escritura, "costos → escritura de encabezado y líneas");
        Assert.True(escritura < codigos, "las líneas insertadas toman el KEY SHARE del artículo antes de asociar su código");
        Assert.True(codigos < proyeccion, "códigos → re-proyección de las órdenes");
        Assert.True(codigos < proveedor, "códigos → proveedores (último lock de fila)");
        Assert.True(escritura < proyeccion, "la re-proyección de las órdenes lee las líneas ya escritas");
        Assert.True(proyeccion < auditoria, "proyección → auditoría");
        Assert.True(auditoria < proveedor, "auditoría → proveedores");
        Assert.True(proveedor < asiento, "el asiento sigue al lock del saldo, que va primero");
        Assert.True(asiento < commit, "el commit es lo último");
    }

    /// <summary>El lock de proveedores es el ÚLTIMO lock de fila: ni los de las órdenes, ni el de stock, ni el de lotes
    /// pueden aparecer después de él. Los locks de proveedores se toman ascendentes por id: el ciclo
    /// <c>foreach (var diferencia in diferenciasDeCuenta)</c> recorre un resultado que
    /// <c>CalculadorDeEdicionDeCompra</c> ya entrega ordenado (probado en Domain.Tests).</summary>
    [Fact]
    public void NingunOtroLockDeFilaApareceDespuesDelLockDeProveedores()
    {
        var metodo = Ejecucion();
        var proveedor = Indice(metodo, "EscriturasDeCuentaCorrienteProveedor.ActualizarSaldoProveedorAsync(");
        var despues = metodo[proveedor..];

        foreach (var marca in new[]
        {
            "BloquearConfirmadaAsync(", "EscriturasDeOrdenDeCompra.BloquearAsync(", "ProyectarEstadoAsync(",
            "BloquearPagosDeLaEdicionAsync(", "AplicarElTratamientoDePagosAsync(", "PlanearElTratamientoDePagosAsync(",
            "ServicioDeLotes.ResolverOCrearAsync(", "AsentarDiferenciasDeStockAsync(", "LockDeMembresiaDeFamilias.",
            "ActualizarCostosDeLaEdicionAsync(", "AsociarCodigoDeProveedorSiEstaLibreAsync("
        })
        {
            Assert.DoesNotContain(marca, despues, StringComparison.Ordinal);
        }
    }

    /// <summary>La reversa del redondeo de la compra toma la fila del proveedor (es un movimiento de cuenta): se escribe en el
    /// recorrido ascendente de proveedores del final, después de lotes, stock y escritura, nunca junto al plan de pagos.</summary>
    [Fact]
    public void LaReversaDelRedondeoSeEscribeEnElRecorridoAscendenteDeProveedoresDelFinal()
    {
        var metodo = Ejecucion();
        var reversa = Indice(metodo, "RedondeoDeProveedor.RevertirDeLaCompraAsync(");
        var recorrido = Indice(metodo, "foreach (var idProveedor in proveedoresATocar)");

        Assert.True(Indice(metodo, "AsentarDiferenciasDeStockAsync(") < reversa);
        Assert.True(Indice(metodo, "await db.SaveChangesAsync(ct);") < reversa);
        Assert.True(recorrido < reversa);
        Assert.Matches(@"\.Distinct\(\)\s*\.Order\(\)\s*\.ToList\(\);\s*foreach \(var idProveedor in proveedoresATocar\)", metodo);
    }

    /// <summary>single-read-under-lock: el encabezado se lee UNA vez, con la entidad rastreada, y después del lock. Una
    /// segunda lectura resolvería contra el identity map y no cambiaría nada.</summary>
    [Fact]
    public void ElEncabezadoSeLeeUnaSolaVezYDespuesDelLock()
    {
        var metodo = Ejecucion();

        Assert.Single(Regex.Matches(metodo, Regex.Escape("db.ComprobantesCompra.FirstAsync(")));
        Assert.DoesNotContain("db.ComprobantesCompra.AsNoTracking().FirstAsync", metodo, StringComparison.Ordinal);
        Assert.True(
            Indice(metodo, "db.ComprobantesCompra.FirstAsync(") > Indice(metodo, "BloquearConfirmadaAsync("),
            "La lectura del encabezado tiene que nacer después del lock.");
    }

    /// <summary>Con la compra bloqueada y sin ningún intento de leer antes: la única lectura previa al lock en todo el
    /// archivo es la proyección escalar del estado del despachador y la verificación de la orden pedida (404 sin lock).</summary>
    [Fact]
    public void ElDespachadorLeeElEstadoPorProyeccionEscalarYElMetodoDeEdicionNoRastreaElEncabezadoAntesDelLock()
    {
        var fuente = SinComentarios(LeerFuente());
        var despachador = fuente[Indice(fuente, "public async Task<CompraDetalle> ActualizarAsync(")..Indice(
            fuente, "public async Task<CompraDetalle> ActualizarConfirmadaAsync(")];

        Assert.Contains(".AsNoTracking()", despachador, StringComparison.Ordinal);
        Assert.Contains(".Select(c => (EstadoCompra?)c.Estado)", despachador, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstAsync(c => c.Id", despachador, StringComparison.Ordinal);

        var previo = fuente[Indice(fuente, "public async Task<CompraDetalle> ActualizarConfirmadaAsync(")..Indice(
            fuente, "private async Task<CompraDetalle> EjecutarEdicionConfirmadaAsync(")];
        Assert.DoesNotContain("db.ComprobantesCompra.FirstAsync(", previo, StringComparison.Ordinal);
        Assert.DoesNotContain("db.ComprobantesCompra.FirstOrDefaultAsync(", previo, StringComparison.Ordinal);
    }

    /// <summary>El despachador responde 404 antes que cualquier otra cosa: su primera sentencia es la lectura del estado y
    /// el 404 sale de ella; borrador, confirmada y anulada toman cada una su camino.</summary>
    [Fact]
    public void ElDespachadorResponde404PrimeroYDespuesElegiElCaminoPorEstado()
    {
        var fuente = SinComentarios(LeerFuente());
        var despachador = fuente[Indice(fuente, "public async Task<CompraDetalle> ActualizarAsync(")..Indice(
            fuente, "public async Task<CompraDetalle> ActualizarConfirmadaAsync(")];

        var lectura = Indice(despachador, ".Select(c => (EstadoCompra?)c.Estado)");
        var noEncontrado = Indice(despachador, "null => throw ErrorDominio.NoEncontrado(");

        Assert.True(lectura < noEncontrado);
        Assert.Contains("EstadoCompra.Borrador => await ActualizarBorradorAsync(id, solicitud, ct)", despachador, StringComparison.Ordinal);
        Assert.Contains("EstadoCompra.Confirmada => await ActualizarConfirmadaAsync(id, solicitud, ct)", despachador, StringComparison.Ordinal);
        Assert.Contains("\"compra_no_editable\"", despachador, StringComparison.Ordinal);
    }

    /// <summary>Texto fuente (no hay forma de armar un punto de venta con baja lógica referenciado por una compra viva): el
    /// cálculo de "cambia la empresa" ve también los puntos de venta con baja lógica. Con el filtro global puesto, un punto de
    /// venta anterior dado de baja desaparecería del <c>Distinct</c> y un cambio de empresa se vería como "misma empresa" y no
    /// liberaría las aplicaciones. Mutación: quitar el <c>IgnoreQueryFilters</c> pone en rojo la aserción.</summary>
    [Fact]
    public void ElCalculoDeCambioDeEmpresaVeTambienLosPuntosDeVentaDadosDeBaja()
    {
        var metodo = Ejecucion();
        var calculo = metodo[Indice(metodo, "var cambiaEmpresa =")..Indice(metodo, "var plan = await PlanearElTratamientoDePagosAsync(")];

        Assert.Matches(@"db\.PuntosVenta\s*\.AsNoTracking\(\)\s*\.IgnoreQueryFilters\(\[""BajaLogica""\]\)", calculo);
        Assert.Contains(".Distinct()", calculo, StringComparison.Ordinal);
    }

    private static string Costos()
    {
        var fuente = SinComentarios(LeerFuente());
        var inicio = Indice(fuente, "private async Task<CostosDeLaEdicion> ActualizarCostosDeLaEdicionAsync(");
        return fuente[inicio..];
    }

    /// <summary>E6-S4: el costo de la edición reusa los helpers de la confirmación en su orden (familias, bloqueo ascendente
    /// de las filas, una escritura), y la búsqueda de la compra más nueva nace DESPUÉS del bloqueo: una confirmación
    /// concurrente que ya comiteó se ve, y una que no, espera al lock y escribe después. Mutación: mover la lectura de
    /// compras posteriores antes de <c>BloquearArticulosConCostoAsync</c> pone en rojo la última aserción.</summary>
    [Fact]
    public void ElCostoDeLaEdicionLeeFamiliasBloqueaLasFilasBuscaLaCompraMasNuevaYRecienEscribe()
    {
        var metodo = Costos();

        var familias = Indice(metodo, "LeerFamiliasDeLosArticulosAsync(");
        var cambian = Indice(metodo, "CalculadorDeEdicionDeCompra.CostosQueCambian(");
        var bloqueo = Indice(metodo, "BloquearArticulosConCostoAsync(");
        var posteriores = Indice(metodo, "db.ItemsComprobanteCompra");
        var filtro = Indice(metodo, "CalculadorDeEdicionDeCompra.SinLosPisadosPorUnaCompraMasNueva(");
        var escritura = Indice(metodo, "ActualizarCostosNominalesAsync(");

        Assert.True(familias < cambian, "familias → costos que cambian");
        Assert.True(cambian < bloqueo, "costos que cambian → bloqueo ascendente de las filas");
        Assert.True(bloqueo < posteriores, "bloqueo → lectura de la compra más nueva (post-lock)");
        Assert.True(posteriores < filtro, "lectura de la compra más nueva → filtro");
        Assert.True(filtro < escritura, "filtro → escritura del costo");
        Assert.Single(Regex.Matches(metodo, Regex.Escape("ActualizarCostosNominalesAsync(")));
        Assert.Single(Regex.Matches(metodo, Regex.Escape("db.ItemsComprobanteCompra")));
    }

    /// <summary>Una edición sin cambio de costo efectivo no hace ninguna ida a la base para costos: las dos salidas
    /// tempranas (sin líneas que actualicen, sin costos que cambien) van antes de la lectura de familias y del bloqueo.</summary>
    [Fact]
    public void UnaEdicionSinCambioDeCostoSaleAntesDeCualquierLecturaOLock()
    {
        var metodo = Costos();

        Assert.True(Indice(metodo, "if (idsConCosto.Count == 0)") < Indice(metodo, "LeerFamiliasDeLosArticulosAsync("));
        Assert.True(Indice(metodo, "if (aCambiar.Count == 0)") < Indice(metodo, "BloquearArticulosConCostoAsync("));
    }

    /// <summary>La compra más nueva se define por fecha de recepción y, en empate, por id mayor, y solo cuenta si está
    /// confirmada y no es la propia compra.</summary>
    [Fact]
    public void LaCompraMasNuevaSeDefinePorFechaDeRecepcionYDesempatePorId()
    {
        var metodo = Costos();

        Assert.Contains("c.Estado == EstadoCompra.Confirmada && c.Id != idCompra", metodo, StringComparison.Ordinal);
        Assert.Contains(
            "(c.FechaRecepcion > recepcion || (c.FechaRecepcion == recepcion && c.Id > idCompra))", metodo, StringComparison.Ordinal);
    }
}
