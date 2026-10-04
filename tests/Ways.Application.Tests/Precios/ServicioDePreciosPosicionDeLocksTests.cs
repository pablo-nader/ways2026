using System.Text.RegularExpressions;
using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Precios;

/// <summary>
/// El orden de las llamadas de <c>ServicioDePrecios.AbrirNuevoPrecioAsync</c>, como aserción sobre el
/// TEXTO FUENTE — mismo criterio que <c>ServicioDeComprasLockOrderTests</c>: los statements son SQL
/// crudo sobre <c>db.Database.GetDbConnection()</c> y un <c>DbCommandInterceptor</c> de EF nunca los
/// ve. Es solo UNA de las dos redes: la otra, que mira los locks reales en <c>pg_locks</c> mientras
/// la transacción está detenida en un punto conocido, vive en <c>PreciosDeFamiliaTests</c>. Esta
/// cubre lo que esa no puede ver de afuera: que el lock de membresía sea la PRIMERA sentencia y que
/// "ahora" se capture una sola vez para TODA la escritura —cada objetivo y la salida de la familia—,
/// después de todos los locks.
/// </summary>
public class ServicioDePreciosPosicionDeLocksTests
{
    private static string LeerFuente() =>
        File.ReadAllText(Path.Combine(
            RaizDelRepositorio.Resolver(), "src", "Ways.Application", "Precios", "ServicioDePrecios.cs"));

    /// <summary>Los bloqueos de fila de la pertenencia viven en <c>MembresiaDeFamilias</c>, compartidos con los
    /// demás escritores de familias: el texto de sus statements se afirma sobre ese archivo.</summary>
    private static string LeerFuenteDeMembresia() =>
        File.ReadAllText(Path.Combine(
            RaizDelRepositorio.Resolver(), "src", "Ways.Application", "Familias", "MembresiaDeFamilias.cs"));

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

    /// <summary>El fuente sin comentarios: una mención a <c>reloj.Ahora</c> en un doc-comment no es una
    /// lectura del reloj.</summary>
    private static string FuenteSinComentarios() =>
        Regex.Replace(LeerFuente(), @"//[^\r\n]*", string.Empty);

    /// <summary>El nombre del miembro (método, propiedad o tipo anidado) cuya declaración es la última
    /// que precede a <paramref name="indice"/>.</summary>
    private static string MiembroQueContiene(string fuente, int indice)
    {
        var declaraciones = Regex.Matches(
            fuente,
            @"^    (?:public|private|internal|protected)\b[^\r\n;=]*?\b(\w+)\s*(?:\(|=>|\{)",
            RegexOptions.Multiline);

        return declaraciones.Last(declaracion => declaracion.Index < indice).Groups[1].Value;
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
        var planificacion = Posicion(cuerpo, "PlanificarPrecioDeUnArticuloAsync(");
        var salidaDeLaFamilia = Posicion(cuerpo, "SacarDeLaFamiliaAsync(");
        var cierres = Posicion(cuerpo, "CerrarFilasDelPlanAsync(");
        var altas = Posicion(cuerpo, "EncolarPrecioNuevo(");
        var guardado = Posicion(cuerpo, "db.SaveChangesAsync(ct)");
        var confirmacion = Posicion(cuerpo, "transaccion.CommitAsync(ct)");

        Assert.True(membresia < resolverObjetivos, "El lock de membresía va antes de leer la pertenencia.");
        Assert.True(resolverObjetivos < locksDePares, "Las filas de los miembros (dentro de ResolverObjetivosAsync) se bloquean antes que los pares.");
        Assert.True(locksDePares < ahora, "'ahora' se captura DESPUÉS de todos los locks de pares.");
        Assert.True(ahora < planificacion, "Ninguna lectura de la fase 1 corre antes de capturar 'ahora'.");
        Assert.True(planificacion < salidaDeLaFamilia, "Las escrituras vienen después de la fase de validación.");
        Assert.True(salidaDeLaFamilia < cierres && cierres < altas, "Primero la salida de la familia y los cierres, después el encolado de lo nuevo.");
        Assert.True(altas < guardado, "Un solo SaveChangesAsync, después de encolar todos los objetivos.");
        Assert.True(guardado < confirmacion, "El commit viene después del guardado.");
    }

    /// <summary>Los pares se recorren en el orden de <c>OrdenDeLocksDePares</c> (ascendente por la clave
    /// del lock) y no en el de los ids de los objetivos: la propiedad del orden la prueba
    /// <c>ServicioDePreciosOrdenDeLocksDeParesTests</c>; acá solo se afirma que el bucle de locks la usa.</summary>
    [Fact]
    public void LosLocksDeParesSeRecorrenEnElOrdenDeLaClave()
    {
        var cuerpo = AbrirNuevoPrecio();

        var recorrido = Posicion(
            cuerpo, "foreach (var idObjetivo in OrdenDeLocksDePares(idTenant, idListaPrecio, objetivos.Ids))");

        Assert.True(recorrido < Posicion(cuerpo, "TomarLockDelParAsync("));
    }

    /// <summary>"Ahora" se resuelve UNA vez por operación: con una lectura por objetivo, los miembros de una
    /// familia terminarían con <c>vigente_desde</c> distintos y el historial mentiría sobre un cambio que
    /// fue uno solo; con una lectura propia en la salida de la familia, el <c>updated_at</c> del artículo
    /// no coincidiría con el instante del precio ni se tomaría después de los locks. Se afirma sobre el
    /// archivo entero —no solo sobre <c>AbrirNuevoPrecioAsync</c>— porque un helper de la escritura que
    /// lee el reloj no aparece en el cuerpo de su llamador: las únicas lecturas del reloj fuera de él son
    /// las de las consultas de precio y el pre-chequeo de <c>ProgramarPrecioAsync</c>.</summary>
    [Fact]
    public void AhoraSeCapturaUnaSolaVezPorOperacion()
    {
        var cuerpo = AbrirNuevoPrecio();

        var primera = Posicion(cuerpo, "reloj.Ahora");

        Assert.Equal(-1, cuerpo.IndexOf("reloj.Ahora", primera + 1, StringComparison.Ordinal));

        var fuente = FuenteSinComentarios();
        var lectores = Regex.Matches(fuente, Regex.Escape("reloj.Ahora"))
            .Select(lectura => MiembroQueContiene(fuente, lectura.Index))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["AbrirNuevoPrecioAsync", "ExigirVigenteDesdeFuturo", "PrecioVigenteAsync", "PreciosVigentesAsync"],
            lectores);
    }

    /// <summary>La salida de la familia de "solo este" es una escritura de la fase 2: ocurre DESPUÉS de
    /// capturar "ahora" y se hace con ese valor, y la fase de filas (<c>ResolverObjetivosAsync</c>) solo
    /// bloquea la fila del artículo.</summary>
    [Fact]
    public void LaSalidaDeLaFamiliaSeEscribeDespuesDeCapturarAhoraYLaFaseDeFilasSoloBloquea()
    {
        var cuerpo = AbrirNuevoPrecio();

        var ahora = Posicion(cuerpo, "reloj.Ahora");
        var salida = Posicion(cuerpo, "SacarDeLaFamiliaAsync(idArticulo, idTenant, ahora, ct)");

        Assert.True(ahora < salida);

        var resolver = CuerpoDe(LeerFuente(), "private async Task<ObjetivosDeLaEscritura> ResolverObjetivosAsync(");

        Assert.DoesNotContain("SacarDeLaFamiliaAsync(", resolver, StringComparison.Ordinal);
        Assert.Contains("BloquearFilaDelArticuloAsync(", resolver, StringComparison.Ordinal);
    }

    /// <summary>La fase 1 no escribe: ni cierra una fila ni registra auditoría ni agrega una entidad ni
    /// guarda. Que un rechazo de un miembro posterior no deje nada hecho por los anteriores lo prueban,
    /// contra la base, las pruebas de <c>PreciosDeFamiliaTests</c>; esta es la red estructural.</summary>
    [Fact]
    public void LaFaseDeValidacionSoloLee()
    {
        var planificar = CuerpoDe(LeerFuente(), "private async Task<PlanDeUnArticulo> PlanificarPrecioDeUnArticuloAsync(");

        foreach (var escritura in new[]
        {
            "CerrarFilaAsync(", "Auditoria.Registrar(", "db.Precios.Add(", "SaveChangesAsync(", "ExecuteNonQueryAsync("
        })
        {
            Assert.DoesNotContain(escritura, planificar, StringComparison.Ordinal);
        }
    }

    /// <summary>Las filas de los miembros se bloquean ascendentes por <c>id_articulo</c> (paso (2) del
    /// protocolo) y <c>FOR NO KEY UPDATE</c>: es el orden de los locks de FILA, que el statement de
    /// miembros produce con su <c>ORDER BY</c>; el de los locks de PAR es otro (por clave) y lo cubre
    /// <see cref="LosLocksDeParesSeRecorrenEnElOrdenDeLaClave"/>.</summary>
    [Fact]
    public void LosMiembrosSeBloqueanAscendentesYConForNoKeyUpdate()
    {
        var cuerpo = CuerpoDe(LeerFuenteDeMembresia(), "public static async Task<List<int>> BloquearMiembrosAsync(");

        Assert.Contains("ORDER BY id_articulo FOR NO KEY UPDATE", cuerpo, StringComparison.Ordinal);
        Assert.DoesNotContain("FOR UPDATE", cuerpo.Replace("FOR NO KEY UPDATE", string.Empty), StringComparison.Ordinal);
    }

    /// <summary>La fila del artículo de "solo este" también se bloquea <c>FOR NO KEY UPDATE</c>, por la
    /// misma razón que las de los miembros: un <c>FOR UPDATE</c> chocaría con el <c>FOR KEY SHARE</c> que
    /// toman las ventas por sus FK.</summary>
    [Fact]
    public void LaFilaDeSoloEsteSeBloqueaConForNoKeyUpdate()
    {
        var cuerpo = CuerpoDe(LeerFuenteDeMembresia(), "public static async Task BloquearFilaDelArticuloAsync(");

        Assert.Contains("FOR NO KEY UPDATE", cuerpo, StringComparison.Ordinal);
        Assert.DoesNotContain("FOR UPDATE", cuerpo.Replace("FOR NO KEY UPDATE", string.Empty), StringComparison.Ordinal);
    }

    /// <summary>La pertenencia se lee en <c>ResolverObjetivosAsync</c> ANTES de decidir y de bloquear a
    /// los miembros — y no en <c>AbrirNuevoPrecioAsync</c>, donde una lectura previa al lock de
    /// membresía sería la foto stale que el lock existe para evitar.</summary>
    [Fact]
    public void LaPertenenciaSeLeeUnaSolaVezYAntesDeDecidirYDeBloquearLosMiembros()
    {
        var cuerpo = CuerpoDe(LeerFuente(), "private async Task<ObjetivosDeLaEscritura> ResolverObjetivosAsync(");

        var lectura = Posicion(cuerpo, "LeerIdFamiliaAsync(");
        var decision = Posicion(cuerpo, "ReglaDeFamilias.ResolverAlcance(");
        var bloqueo = Posicion(cuerpo, "BloquearMiembrosAsync(");

        Assert.True(lectura < decision && decision < bloqueo);
        Assert.Equal(-1, cuerpo.IndexOf("LeerIdFamiliaAsync(", lectura + 1, StringComparison.Ordinal));
        Assert.DoesNotContain("LeerIdFamiliaAsync(", AbrirNuevoPrecio(), StringComparison.Ordinal);
    }
}
