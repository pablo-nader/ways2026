using System.Text.RegularExpressions;
using Ways.Application.Tests.Infraestructura;

namespace Ways.Application.Tests.Abstracciones;

/// <summary>
/// LA CLÁUSULA: cada una de las escrituras no idempotentes de esta corrección corre bajo
/// <c>FabricaDeEstrategiaSinReintento</c> y NO bajo <c>db.Database.CreateExecutionStrategy()</c>.
///
/// Por qué estructural y no solo conductual (<c>mutation-proof-tests</c> regla 13): el mecanismo
/// por el que la propiedad se rompe es un cambio de UNA línea por sitio, y el daño no se ve en la
/// fila que la operación escribe sino en las que DUPLICA ante un reintento de
/// <c>EnableRetryOnFailure(5)</c> — global, configurado en <c>DependencyInjection</c>. La mitad
/// conductual (interceptor que inyecta un <c>40001</c> transitorio sobre el primer INSERT, conteo
/// de intentos y conteo exacto de filas) vive en
/// <c>Ways.IntegrationTests.EscriturasSinReintentoTests</c> y solo puede ejercitar un camino por
/// prueba; esta cubre los diez a la vez y no necesita Docker.
///
/// Cada fila de <see cref="SitiosCorregidos"/> es un sitio del audit: entidades construidas de
/// cero DENTRO del lambda (o números/códigos re-sorteados adentro), sin ninguna clave de
/// idempotencia natural que un reintento pueda usar para no duplicar.
/// </summary>
public class EscriturasSinReintentoEstructuralesTests
{
    /// <summary>Archivo, método y firma exacta de cada escritura corregida. La lista está
    /// congelada a propósito: revertir cualquiera de los diez sitios a la estrategia reintentable
    /// pone esta prueba en rojo NOMBRANDO el método.</summary>
    public static TheoryData<string, string, string> SitiosCorregidos() => new()
    {
        { "Clientes", "ServicioDeClientes.cs", "CrearAsync" },
        { "Articulos", "ServicioDeArticulos.cs", "CrearAsync" },
        { "Usuarios", "ServicioDeUsuarios.cs", "CrearAsync" },
        { "Precios", "ServicioDePrecios.cs", "AbrirNuevoPrecioAsync" },
        { "Ventas", "ServicioDeVentas.cs", "EmitirAsync" },
        { "Fiscal", "ServicioDeCertificados.cs", "RegistrarAsync" },
        { "Catalogos", "ServicioDeListasPrecio.cs", "CrearAsync" },
        { "Ofertas", "ServicioDeOfertas.cs", "CrearAsync" },
        { "Ofertas", "ServicioDeOfertas.cs", "ActualizarAsync" },
        { "Organizacion", "ServicioDeAprovisionamiento.cs", "CrearTenantAsync" },
    };

    /// <summary>
    /// El kill: el cuerpo del método referencia la fábrica sin reintento. Revertir un sitio a
    /// <c>db.Database.CreateExecutionStrategy()</c> deja el cuerpo sin la marca y la prueba falla
    /// diciendo exactamente qué método volvió a ser reintentable.
    /// </summary>
    [Theory]
    [MemberData(nameof(SitiosCorregidos))]
    public void CadaEscrituraNoIdempotenteCorreBajoLaEstrategiaSinReintento(
        string carpeta, string archivo, string metodo)
    {
        var cuerpo = CuerpoDelMetodo(LeerServicio(carpeta, archivo), metodo);

        Assert.Contains(
            "FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(",
            cuerpo,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// La otra mitad del mismo enunciado, y la que no es redundante: que la fábrica esté presente
    /// no impide que la estrategia REINTENTABLE siga envolviendo la escritura al lado (una línea
    /// sobreviviente, un segundo <c>ExecuteAsync</c> agregado después). Nueve de los diez sitios
    /// no pueden nombrar <c>CreateExecutionStrategy</c> en absoluto.
    ///
    /// <c>ServicioDeVentas.EmitirAsync</c> es la excepción DECLARADA y tiene su propia prueba
    /// abajo: su paso de numeración —ADO crudo, idempotente por diseño ("gaps are accepted", nunca
    /// duplicados)— conserva la estrategia reintentable a propósito.
    /// </summary>
    [Theory]
    [MemberData(nameof(SitiosCorregidos))]
    public void NingunaEscrituraCorregidaVuelveALaEstrategiaReintentable(
        string carpeta, string archivo, string metodo)
    {
        if (archivo == "ServicioDeVentas.cs")
        {
            return;
        }

        var cuerpo = CuerpoDelMetodo(LeerServicio(carpeta, archivo), metodo);

        Assert.DoesNotContain("CreateExecutionStrategy(", cuerpo, StringComparison.Ordinal);
    }

    /// <summary>
    /// El camino caliente del POS, afirmado pieza por pieza porque las tres propiedades se pueden
    /// romper por separado:
    ///
    /// <list type="bullet">
    /// <item>el paso de NUMERACIÓN conserva la estrategia reintentable — reservar de nuevo solo
    /// vuelve a avanzar el contador, nunca duplica una fila;</item>
    /// <item>el paso de ESCRITURA (comprobante + ítems + pagos, entidades nuevas por intento) corre
    /// sin reintento;</item>
    /// <item>la guarda de commit ambiguo <c>BuscarPorNumeroComprometidoAsync</c> SIGUE corriendo
    /// primero. Es la pieza que hace aceptable el no-reintento: el número ya está comiteado, así
    /// que el reenvío del cliente con el MISMO número devuelve el comprobante ya emitido en vez de
    /// reinsertarlo contra <c>ux_comprobantes_venta_numero</c>. Borrarla convierte cada reenvío en
    /// un 409.</item>
    /// </list>
    ///
    /// Se afirma que hay EXACTAMENTE UNA <c>CreateExecutionStrategy</c> en el método y que es la de
    /// numeración: así, devolver el paso de escritura a la reintentable (que agregaría una segunda)
    /// pone esto en rojo aunque la fábrica siga nombrada más arriba.
    /// </summary>
    [Fact]
    public void LaVentaNumeraConReintentoPeroEscribeSinElYConservaLaGuardaDeCommitAmbiguo()
    {
        var cuerpo = CuerpoDelMetodo(LeerServicio("Ventas", "ServicioDeVentas.cs"), "EmitirAsync");

        var reintentables = Regex.Matches(
            cuerpo, @"CreateExecutionStrategy\(\)", RegexOptions.None, TimeSpan.FromSeconds(5));

        Assert.Single(reintentables);
        Assert.Contains(
            "var estrategiaNumeracion = db.Database.CreateExecutionStrategy();",
            cuerpo,
            StringComparison.Ordinal);

        var numeracion = Posicion(cuerpo, "var estrategiaNumeracion = db.Database.CreateExecutionStrategy();");
        var escritura = Posicion(
            cuerpo, "var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);");
        var guarda = Posicion(cuerpo, "await BuscarPorNumeroComprometidoAsync(");
        var transaccion = Posicion(cuerpo, "await EjecutarTransaccionAsync(");

        Assert.True(numeracion < escritura, "El número se compromete ANTES de abrir la escritura.");
        Assert.True(escritura < guarda, "La guarda corre bajo la estrategia sin reintento.");
        Assert.True(guarda < transaccion, "La guarda de commit ambiguo corre ANTES de reinsertar.");
    }

    private static string LeerServicio(string carpeta, string archivo) =>
        File.ReadAllText(Path.Combine(
            RaizDelRepositorio.Resolver(), "src", "Ways.Application", carpeta, archivo));

    private static int Posicion(string cuerpo, string marca)
    {
        var indice = cuerpo.IndexOf(marca, StringComparison.Ordinal);
        Assert.True(indice >= 0, $"No se encontró '{marca}' en el cuerpo del método.");
        return indice;
    }

    /// <summary>Extrae el cuerpo de un método por conteo de llaves desde su firma — mismo criterio
    /// que <c>BajasEstructuralesTests</c>, pero admitiendo <c>override</c> y un tipo de retorno
    /// genérico (<c>Task&lt;T&gt;</c>), que es la forma de todas las escrituras de esta lista.
    /// </summary>
    private static string CuerpoDelMetodo(string fuente, string nombre)
    {
        var firma = Regex.Match(
            fuente,
            $@"public\s+(?:override\s+)?async\s+Task(?:<[^>]+>)?\s+{Regex.Escape(nombre)}\s*\(",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(firma.Success, $"No se encontró el método {nombre}.");

        var apertura = fuente.IndexOf('{', firma.Index);
        var profundidad = 0;

        for (var i = apertura; i < fuente.Length; i++)
        {
            profundidad += fuente[i] switch { '{' => 1, '}' => -1, _ => 0 };

            if (profundidad == 0)
            {
                return fuente[apertura..(i + 1)];
            }
        }

        throw new InvalidOperationException($"El método {nombre} no cierra sus llaves.");
    }
}
