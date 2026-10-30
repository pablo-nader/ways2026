using System.Text.RegularExpressions;

namespace Ways.Application.Tests.CuentaCorriente;

/// <summary>
/// E7-S2: orden de locks y lecturas de las aplicaciones de cobros (<c>ServicioDeCobros</c>) y de su liberación en la
/// anulación (<c>ServicioDeVentas.EjecutarAnulacionAsync</c>). Aserciones de TEXTO FUENTE, con el mismo criterio que
/// <c>ServicioDeComprasEdicionLockOrderTests</c>: los locks son SQL crudo sobre la conexión de la transacción y el orden de
/// las llamadas es el único artefacto observable sin una carrera real. Mutación: mover cualquiera de las llamadas nombradas
/// fuera de su lugar pone en rojo la aserción que la nombra.
/// </summary>
public class ServicioDeCobrosLockOrderTests
{
    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    private static string LeerFuente(params string[] partes)
    {
        var ruta = Path.Combine(
            [Path.GetDirectoryName(RutaDeEsteArchivo())!, "..", "..", "..", "src", .. partes]);

        Assert.True(File.Exists(ruta), $"No se encontró {ruta}");
        return File.ReadAllText(ruta);
    }

    private static string SinComentarios(string fuente) => Regex.Replace(fuente, @"//[^\r\n]*", string.Empty);

    private static string Metodo(string fuente, string firma, string siguiente)
    {
        var inicio = fuente.IndexOf(firma, StringComparison.Ordinal);
        Assert.True(inicio >= 0, $"No se encontró '{firma}'.");

        var fin = fuente.IndexOf(siguiente, inicio + firma.Length, StringComparison.Ordinal);
        Assert.True(fin > inicio, $"No se encontró el final de '{firma}'.");

        return SinComentarios(fuente[inicio..fin]);
    }

    private static int Indice(string metodo, string marca)
    {
        var indice = metodo.IndexOf(marca, StringComparison.Ordinal);
        Assert.True(indice >= 0, $"No se encontró '{marca}'.");
        return indice;
    }

    private static string Cobros() => LeerFuente("Ways.Application", "CuentaCorriente", "ServicioDeCobros.cs");

    [Fact]
    public void AplicarBloqueaLosComprobantesAntesDeLeerYEscribirLasAplicaciones()
    {
        var metodo = Metodo(Cobros(), "private async Task<int> EjecutarAplicarAsync(", "public async Task<EstadoDeCobro> DesaplicarAsync(");

        var apertura = Indice(metodo, "db.Database.BeginTransactionAsync(ct);");
        var locks = Indice(metodo, "BloquearComprobantesAsync(");
        var lectura = Indice(metodo, "db.AplicacionesCobro.Where(a => a.IdComprobanteCobro == idRecibo).ToListAsync(ct)");
        var topes = Indice(metodo, "ReglaDeAplicacionDeCobro.ExigirTopes(");
        var alta = Indice(metodo, "db.AplicacionesCobro.Add(");
        var escritura = Indice(metodo, "await db.SaveChangesAsync(ct);");
        var commit = Indice(metodo, "transaccion.CommitAsync(ct);");

        Assert.True(apertura < locks, "Los locks se toman adentro de la transacción.");
        Assert.True(locks < lectura, "Las aplicaciones del recibo se leen DESPUÉS del FOR UPDATE de los comprobantes.");
        Assert.True(lectura < topes && topes < alta, "Los topes se validan con lo leído bajo el lock y antes de escribir.");
        Assert.True(alta < escritura && escritura < commit);
    }

    /// <summary>Lo aplicado del recibo se lee UNA vez (la lista trackeada) y de ahí salen el disponible de los topes y el
    /// rastro: ni una segunda consulta de sus aplicaciones ni el lector de disponible, que las vuelve a sumar en la base.
    /// Es texto fuente: no prueba qué ve la transacción bajo concurrencia, solo que hay una única fuente.</summary>
    [Fact]
    public void AplicarDerivaElDisponibleYElRastroDeUnaSolaLecturaDeLasAplicaciones()
    {
        var metodo = Metodo(Cobros(), "private async Task<int> EjecutarAplicarAsync(", "public async Task<EstadoDeCobro> DesaplicarAsync(");

        Assert.Single(Regex.Matches(metodo, @"db\.AplicacionesCobro\.Where\("));
        Assert.DoesNotContain("LeerDisponiblePorReciboAsync", metodo, StringComparison.Ordinal);

        var suma = Indice(metodo, "aplicaciones.Sum(a => a.Importe)");
        var topes = Indice(metodo, "ReglaDeAplicacionDeCobro.ExigirTopes(");
        var rastro = Indice(metodo, "aplicaciones.Select(a => (a.IdComprobanteVenta, a.Importe)).ToList());");
        Assert.True(suma < topes && topes < rastro);
    }

    [Fact]
    public void DesaplicarLeeLaAplicacionUnaSolaVezYDespuesDeLosLocks()
    {
        var metodo = Metodo(Cobros(), "private async Task<int> EjecutarDesaplicarAsync(", "public async Task<EstadoDeCobro> ObtenerEstadoAsync(");

        var locks = Indice(metodo, "BloquearComprobantesAsync(");
        var lectura = Indice(metodo, "db.AplicacionesCobro");
        var baja = Indice(metodo, "aplicacion.DeletedAt = momento;");

        Assert.True(locks < lectura && lectura < baja);
        Assert.Single(Regex.Matches(metodo, @"db\.AplicacionesCobro"));
    }

    [Fact]
    public void LosComprobantesSeBloqueanEnIdAscendenteSinRepetir()
    {
        var metodo = Metodo(Cobros(), "BloquearComprobantesAsync(\n        IEnumerable<int> ids", "private async Task<DbConnection> ObtenerConexionAbiertaAsync(");

        Assert.Contains("foreach (var id in ids.Distinct().Order())", metodo, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE OF c", metodo, StringComparison.Ordinal);
    }

    /// <summary>La anulación libera las aplicaciones después de tomar la fila del comprobante con su UPDATE de estado y antes
    /// de cuenta corriente (donde toma la fila del cliente), dentro de la misma transacción.</summary>
    [Fact]
    public void LaAnulacionLiberaLasAplicacionesConElComprobanteYaBloqueadoYAntesDeLaCuentaCorriente()
    {
        var metodo = Metodo(
            LeerFuente("Ways.Application", "Ventas", "ServicioDeVentas.cs"),
            "private async Task<ComprobanteEmitido> EjecutarAnulacionAsync(",
            "private static async Task ExigirTurnoNoCerradoAsync(");

        var marcado = Indice(metodo, "await MarcarAnuladoAsync(");
        var liberacion = Indice(metodo, "EscriturasDeAplicacionesDeCobro.LiberarDelComprobanteAsync(");
        var auditoria = Indice(metodo, "AccionAuditada.CobroDesaplicacion");
        var cliente = Indice(metodo, "await BloquearClienteAsync(");
        var commit = Indice(metodo, "transaccion.CommitAsync(ct);");

        Assert.True(marcado < liberacion, "Las aplicaciones se liberan con la fila del comprobante ya tomada.");
        Assert.True(liberacion < auditoria && auditoria < cliente, "La liberación y su auditoría van antes del lock del cliente.");
        Assert.True(cliente < commit);
    }
}
