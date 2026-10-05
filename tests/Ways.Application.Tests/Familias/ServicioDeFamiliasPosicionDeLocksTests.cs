using System.Text.RegularExpressions;
using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Familias;

/// <summary>
/// El orden de las llamadas de las operaciones de <c>ServicioDeFamilias</c> que cambian la pertenencia, como aserción
/// sobre el TEXTO FUENTE — mismo criterio que <c>ServicioDePreciosPosicionDeLocksTests</c>: los locks y la escritura
/// son SQL crudo sobre <c>db.Database.GetDbConnection()</c> y un <c>DbCommandInterceptor</c> de EF nunca los ve.
/// Es solo UNA de las dos redes: la otra, que mira los locks reales en <c>pg_locks</c> mientras la operación espera,
/// vive en <c>FamiliasSalidaYDisolucionTests</c>. Esta cubre lo que esa no puede ver de afuera: que el lock de
/// membresía sea la PRIMERA sentencia, que "ahora" se lea una sola vez, después de todos los locks, y que la
/// familia se lea una sola vez.
/// </summary>
public class ServicioDeFamiliasPosicionDeLocksTests
{
    private static string LeerFuente() =>
        File.ReadAllText(Path.Combine(
            RaizDelRepositorio.Resolver(), "src", "Ways.Application", "Familias", "ServicioDeFamilias.cs"));

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

    /// <summary>El cuerpo del método público <c>async</c> de ese nombre, con o sin tipo de retorno genérico.</summary>
    private static string Metodo(string nombre)
    {
        var fuente = FuenteSinComentarios();
        var firma = Regex.Match(
            fuente, $@"public async Task(?:<[^>]+>)?\s+{Regex.Escape(nombre)}\(", RegexOptions.None, TimeSpan.FromSeconds(5));
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
    /// <c>BeginTransactionAsync</c> y él solo se arma la conexión y se toma la transacción cruda, sin ningún
    /// statement. Se afirma el texto exacto de ese tramo, con los espacios normalizados: cualquier otra sentencia que
    /// se intercale —una lectura, un lock, una escritura— lo rompe. Y ninguna de las dos toma el compartido.</summary>
    [Theory]
    [InlineData("SacarArticuloAsync")]
    [InlineData("DisolverAsync")]
    public void ElLockDeMembresiaExclusivoEsLaPrimeraSentenciaDespuesDeAbrirLaTransaccion(string metodo)
    {
        var cuerpo = Metodo(metodo);

        const string apertura = "db.Database.BeginTransactionAsync(ct);";
        var despuesDeAbrir = Posicion(cuerpo, apertura) + apertura.Length;
        var bloqueo = Posicion(cuerpo, "LockDeMembresiaDeFamilias.TomarExclusivoAsync(");

        Assert.Equal(
            "var conexion = await ObtenerConexionAbiertaAsync(ct); " +
            "var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction(); await",
            Regex.Replace(cuerpo[despuesDeAbrir..bloqueo], @"\s+", " ").Trim());
        Assert.DoesNotContain("TomarCompartidoAsync(", cuerpo, StringComparison.Ordinal);
    }

    /// <summary>Disolver toma los locks en el orden global de las familias y escribe después: la membresía, la fila de
    /// la familia —<c>FOR UPDATE</c>, antes de leerla—, la lectura de la familia, las filas de los miembros, recién
    /// entonces "ahora", la baja de los miembros, el guardado de la baja de la familia y el commit. La familia se lee
    /// UNA sola vez (<c>single-read-under-lock</c>).</summary>
    [Fact]
    public void DisolverTomaLosLocksEnElOrdenDelProtocoloYEscribeDespues()
    {
        var cuerpo = Metodo("DisolverAsync");

        var membresia = Posicion(cuerpo, "LockDeMembresiaDeFamilias.TomarExclusivoAsync(");
        var filaDeLaFamilia = Posicion(cuerpo, "guarda.BloquearFilaAsync<Familia>(");
        var lecturaDeLaFamilia = Posicion(cuerpo, "db.Familias.FirstOrDefaultAsync(");
        var filasDeLosMiembros = Posicion(cuerpo, "MembresiaDeFamilias.BloquearMiembrosAsync(");
        var ahora = Posicion(cuerpo, "reloj.Ahora");
        var escrituraDeLosMiembros = Posicion(cuerpo, "DesvincularArticulosAsync(");
        var guardado = Posicion(cuerpo, "db.SaveChangesAsync(ct)");
        var confirmacion = Posicion(cuerpo, "transaccion.CommitAsync(ct)");

        Assert.True(membresia < filaDeLaFamilia, "La membresía va antes que la fila de la familia.");
        Assert.True(filaDeLaFamilia < lecturaDeLaFamilia, "La fila de la familia se bloquea ANTES de leerla.");
        Assert.True(lecturaDeLaFamilia < filasDeLosMiembros, "Las filas de los miembros se bloquean después de la familia.");
        Assert.True(filasDeLosMiembros < ahora, "'ahora' se lee DESPUÉS de todos los locks.");
        Assert.True(ahora < escrituraDeLosMiembros, "Ninguna escritura corre antes de leer 'ahora'.");
        Assert.True(escrituraDeLosMiembros < guardado && guardado < confirmacion, "La baja de la familia se guarda antes del commit.");

        Assert.Equal(1, Contar(cuerpo, "db.Familias."));
        Assert.Equal(1, Contar(cuerpo, "reloj.Ahora"));
    }

    /// <summary>Sacar comprueba la familia y el artículo bajo el lock de membresía, bloquea la fila del artículo —paso
    /// (2)— y recién entonces escribe. "Ahora" se lee una sola vez, como argumento de la escritura, así que después del
    /// bloqueo de la fila.</summary>
    [Fact]
    public void SacarTomaLosLocksEnElOrdenDelProtocoloYEscribeDespues()
    {
        var cuerpo = Metodo("SacarArticuloAsync");

        var membresia = Posicion(cuerpo, "LockDeMembresiaDeFamilias.TomarExclusivoAsync(");
        var lecturaDeLaFamilia = Posicion(cuerpo, "db.Familias.AnyAsync(");
        var existenciaDelArticulo = Posicion(cuerpo, "MembresiaDeFamilias.LeerIdFamiliaAsync(");
        var filaDelArticulo = Posicion(cuerpo, "MembresiaDeFamilias.BloquearFilaDelArticuloAsync(");
        var escrituraConAhora = Posicion(
            cuerpo, "DesvincularArticulosAsync(conexion, transaccionCruda, [idArticulo], idTenant, reloj.Ahora, ct)");
        var confirmacion = Posicion(cuerpo, "transaccion.CommitAsync(ct)");

        Assert.True(membresia < lecturaDeLaFamilia, "Todo lo que se lee va después del lock de membresía.");
        Assert.True(lecturaDeLaFamilia < existenciaDelArticulo, "Primero la familia y después el artículo.");
        Assert.True(existenciaDelArticulo < filaDelArticulo, "La fila del artículo se bloquea con la existencia ya comprobada.");
        Assert.True(filaDelArticulo < escrituraConAhora, "La escritura, con 'ahora' como argumento, va DESPUÉS del bloqueo de la fila.");
        Assert.True(escrituraConAhora < confirmacion, "La escritura va antes del commit.");

        Assert.Equal(1, Contar(cuerpo, "reloj.Ahora"));
    }

    /// <summary>"Ahora" se lee UNA vez por operación de escritura: las tres lo leen una sola vez, y el único otro
    /// lector del reloj es la lectura de los precios de la referencia. Con una lectura por fila escrita, los
    /// miembros de una familia disuelta terminarían con <c>updated_at</c> distintos para una operación que fue una
    /// sola.</summary>
    [Fact]
    public void AhoraSoloLoLeenLasEscriturasYLaLecturaDeLosPrecios()
    {
        var fuente = FuenteSinComentarios();

        var lectores = Regex.Matches(fuente, Regex.Escape("reloj.Ahora"))
            .Select(lectura => Regex.Matches(
                fuente[..lectura.Index], @"^    (?:public|private|internal)\b[^\r\n;=]*?\b(\w+)\s*\(", RegexOptions.Multiline).Last().Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["ActualizarAsync", "DisolverAsync", "EstadoDePreciosDeLaReferenciaAsync", "SacarArticuloAsync"], lectores);
    }

    /// <summary>La edición del nombre y del estado toma solo la fila de la familia: ni el lock de membresía ni la fila
    /// de ningún artículo. Con una espera real lo prueba <c>FamiliasActualizacionTests</c>; acá, que el texto no los
    /// nombra.</summary>
    [Fact]
    public void LaEdicionNoTomaElLockDeMembresiaNiBloqueaFilasDeArticulos()
    {
        var cuerpo = Metodo("ActualizarAsync");

        foreach (var ajeno in new[]
        {
            "LockDeMembresiaDeFamilias", "BloquearMiembrosAsync", "BloquearFilaDelArticuloAsync", "db.Articulos.Where"
        })
        {
            Assert.DoesNotContain(ajeno, cuerpo, StringComparison.Ordinal);
        }

        Assert.Equal(1, Contar(cuerpo, "guarda.BloquearFilaAsync<Familia>("));
    }
}
