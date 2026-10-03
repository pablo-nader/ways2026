using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Precios;

/// <summary>
/// El orden de las llamadas de <c>ServicioDePrecios.AbrirNuevoPrecioAsync</c>, como aserción sobre el
/// TEXTO FUENTE — mismo criterio que <c>ServicioDeComprasLockOrderTests</c>: los statements son SQL
/// crudo sobre <c>db.Database.GetDbConnection()</c> y un <c>DbCommandInterceptor</c> de EF nunca los
/// ve. Es solo UNA de las dos redes: la otra, que mira los locks reales en <c>pg_locks</c> mientras
/// la transacción está detenida en un punto conocido, vive en <c>PreciosDeFamiliaTests</c>. Esta
/// cubre lo que esa no puede ver de afuera: que el lock de membresía sea la PRIMERA sentencia y que
/// "ahora" se capture una sola vez, después de todos los locks.
/// </summary>
public class ServicioDePreciosPosicionDeLocksTests
{
    private static string LeerFuente() =>
        File.ReadAllText(Path.Combine(
            RaizDelRepositorio.Resolver(), "src", "Ways.Application", "Precios", "ServicioDePrecios.cs"));

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

    private static string AbrirNuevoPrecio() =>
        CuerpoDe(LeerFuente(), "public async Task<PrecioVigente> AbrirNuevoPrecioAsync(");

    private static int Posicion(string cuerpo, string marca, int desde = 0)
    {
        var indice = cuerpo.IndexOf(marca, desde, StringComparison.Ordinal);
        Assert.True(indice >= 0, $"No se encontró '{marca}'.");
        return indice;
    }

    /// <summary>El lock de membresía es lo PRIMERO que hace la transacción: entre el
    /// <c>BeginTransactionAsync</c> y él no hay ninguna otra sentencia (lo único que los separa es el
    /// <c>await</c> de la propia llamada).</summary>
    [Fact]
    public void ElLockDeMembresiaEsLaPrimeraSentenciaDespuesDeAbrirLaTransaccion()
    {
        var cuerpo = AbrirNuevoPrecio();

        const string apertura = "db.Database.BeginTransactionAsync(ct);";
        var despuesDeAbrir = Posicion(cuerpo, apertura) + apertura.Length;
        var lockDeMembresia = Posicion(cuerpo, "TomarLockDeMembresiaAsync(");

        Assert.Equal("await", cuerpo[despuesDeAbrir..lockDeMembresia].Trim());
    }

    [Fact]
    public void LosLocksSubenEnElOrdenGlobalYAhoraSeCapturaDespuesDeTodos()
    {
        var cuerpo = AbrirNuevoPrecio();

        var membresia = Posicion(cuerpo, "TomarLockDeMembresiaAsync(");
        var resolverObjetivos = Posicion(cuerpo, "ResolverObjetivosAsync(");
        var locksDePares = Posicion(cuerpo, "TomarLockDelParAsync(");
        var ahora = Posicion(cuerpo, "reloj.Ahora");
        var escrituraPorObjetivo = Posicion(cuerpo, "AbrirPrecioDeUnArticuloAsync(");
        var guardado = Posicion(cuerpo, "db.SaveChangesAsync(ct)");
        var confirmacion = Posicion(cuerpo, "transaccion.CommitAsync(ct)");

        Assert.True(membresia < resolverObjetivos, "El lock de membresía va antes de leer la pertenencia.");
        Assert.True(resolverObjetivos < locksDePares, "Las filas de los miembros (dentro de ResolverObjetivosAsync) se bloquean antes que los pares.");
        Assert.True(locksDePares < ahora, "'ahora' se captura DESPUÉS de todos los locks de pares.");
        Assert.True(ahora < escrituraPorObjetivo, "Ninguna escritura corre antes de capturar 'ahora'.");
        Assert.True(escrituraPorObjetivo < guardado, "Un solo SaveChangesAsync, después de escribir todos los objetivos.");
        Assert.True(guardado < confirmacion, "El commit viene después del guardado.");
    }

    /// <summary>"Ahora" se resuelve UNA vez para todos los objetivos: con una lectura por objetivo,
    /// los miembros de una familia terminarían con <c>vigente_desde</c> distintos y el historial
    /// mentiría sobre un cambio que fue uno solo.</summary>
    [Fact]
    public void AhoraSeCapturaUnaSolaVezPorOperacion()
    {
        var cuerpo = AbrirNuevoPrecio();

        var primera = Posicion(cuerpo, "reloj.Ahora");

        Assert.Equal(-1, cuerpo.IndexOf("reloj.Ahora", primera + 1, StringComparison.Ordinal));
    }

    /// <summary>Los locks de pares se toman en un recorrido de los objetivos que ya vienen
    /// ascendentes por <c>id_articulo</c> (<c>ORDER BY id_articulo</c> del statement de miembros);
    /// ese orden es parte del protocolo y se afirma sobre el statement que lo produce.</summary>
    [Fact]
    public void LosMiembrosSeBloqueanAscendentesYConForNoKeyUpdate()
    {
        var cuerpo = CuerpoDe(LeerFuente(), "private async Task<List<int>> BloquearMiembrosAsync(");

        Assert.Contains("ORDER BY id_articulo FOR NO KEY UPDATE", cuerpo, StringComparison.Ordinal);
        Assert.DoesNotContain("FOR UPDATE", cuerpo.Replace("FOR NO KEY UPDATE", string.Empty), StringComparison.Ordinal);
    }

    /// <summary>La pertenencia se lee en <c>ResolverObjetivosAsync</c> ANTES de decidir y de bloquear a
    /// los miembros — y no en <c>AbrirNuevoPrecioAsync</c>, donde una lectura previa al lock de
    /// membresía sería la foto stale que el lock existe para evitar.</summary>
    [Fact]
    public void LaPertenenciaSeLeeUnaSolaVezYAntesDeDecidirYDeBloquearLosMiembros()
    {
        var cuerpo = CuerpoDe(LeerFuente(), "private async Task<IReadOnlyList<int>> ResolverObjetivosAsync(");

        var lectura = Posicion(cuerpo, "LeerIdFamiliaAsync(");
        var decision = Posicion(cuerpo, "ReglaDeFamilias.ResolverAlcance(");
        var bloqueo = Posicion(cuerpo, "BloquearMiembrosAsync(");

        Assert.True(lectura < decision && decision < bloqueo);
        Assert.Equal(-1, cuerpo.IndexOf("LeerIdFamiliaAsync(", lectura + 1, StringComparison.Ordinal));
        Assert.DoesNotContain("LeerIdFamiliaAsync(", AbrirNuevoPrecio(), StringComparison.Ordinal);
    }
}
