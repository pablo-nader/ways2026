namespace Ways.Application.Tests.CuentaCorriente;

/// <summary>
/// stage-15-cc-proveedores-ledger, Slice 4 (task 4.16; design.md:164-172 / tasks.md decisión 4 —
/// mutation target #25). Mutation target #25: si <c>ThenByDescending(m => m.Id)</c> se borrara de
/// <c>ConstruirQuery</c>, la paginación con <c>fecha</c> empatada podría duplicar o saltear filas.
///
/// Resuelto con una aserción de TEXTO FUENTE, no de comportamiento — mismo criterio que los
/// mutation targets #4/#11 (Slice 1, <c>CuentaCorrienteProveedorBackfillTests</c>) y #19 (Slice 2,
/// <c>ServicioDeComprasLockOrderTests</c>): dos intentos de rutear el target por comportamiento
/// fueron descartados EMPÍRICAMENTE (mutation-proof-tests rule 2, "no lo razones, corré la
/// prueba"; rule 3 exhausted primero). Intento 1 — sembrar tres filas con `fecha` idéntica en
/// orden ascendente de `id`: el mutante (sin `ThenByDescending`) pasó la prueba de todos modos, en
/// este entorno de test, porque el orden físico (TID) de un `INSERT`-only sequencial coincide con
/// el orden de `id`. Intento 2 — forzar un `UPDATE` sobre la primera fila para desacoplar TID de
/// `id`: el mutante SIGUIÓ pasando la prueba de comportamiento vía el endpoint HTTP paginado
/// (`Skip`/`Take`), aunque una consulta EF sin `Take` explícito sí reveló una divergencia — la
/// resolución de un empate de `ORDER BY` sin desempate explícito depende del plan/estrategia de
/// sort que Postgres elija (quicksort acotado por `LIMIT` vs. sort completo), no de una garantía
/// observable desde el test. El orden de la cláusula en el código fuente es, por eso, el único
/// artefacto verificable de forma determinista.
/// </summary>
public class ServicioDeCuentaCorrienteDeProveedorLecturaTests
{
    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    private static string LeerFuente()
    {
        // tests/Ways.Application.Tests/CuentaCorriente/ → ../../src/Ways.Application/CuentaCorriente/
        var ruta = Path.Combine(
            Path.GetDirectoryName(RutaDeEsteArchivo())!,
            "..", "..", "..", "src", "Ways.Application", "CuentaCorriente", "ServicioDeCuentaCorrienteDeProveedor.cs");

        Assert.True(File.Exists(ruta), $"No se encontró {ruta}");
        return File.ReadAllText(ruta);
    }

    private static string ExtraerMetodoConstruirQuery(string fuente)
    {
        const string firma = "private IQueryable<MovimientoCuentaCorrienteProveedor> ConstruirQuery(";
        var inicio = fuente.IndexOf(firma, StringComparison.Ordinal);
        Assert.True(inicio >= 0, $"No se encontró el método '{firma}'.");

        // Único método privado después de ConstruirQuery en este archivo — hasta el cierre de la
        // clase basta (no hay otro método declarado luego).
        var finClase = fuente.LastIndexOf('}');
        Assert.True(finClase > inicio, "No se encontró el cierre de la clase.");

        return fuente[inicio..finClase];
    }

    [Fact]
    public void ConstruirQueryDesempataPorIdMovimientoDescendenteTarget25()
    {
        var metodo = ExtraerMetodoConstruirQuery(LeerFuente());

        Assert.Contains("OrderByDescending(m => m.Fecha)", metodo, StringComparison.Ordinal);
        Assert.Contains(
            "ThenByDescending(m => m.Id)", metodo,
            StringComparison.Ordinal);

        // El desempate tiene que venir DESPUÉS del OrderByDescending(Fecha) principal, en la misma
        // expresión encadenada — nunca un ThenBy suelto en otro lugar del archivo.
        var indiceOrderBy = metodo.IndexOf("OrderByDescending(m => m.Fecha)", StringComparison.Ordinal);
        var indiceThenBy = metodo.IndexOf("ThenByDescending(m => m.Id)", StringComparison.Ordinal);
        Assert.True(
            indiceThenBy > indiceOrderBy && indiceThenBy - indiceOrderBy < 40,
            "ThenByDescending(Id) tiene que encadenarse inmediatamente después de OrderByDescending(Fecha) (mutation target #25).");
    }
}
