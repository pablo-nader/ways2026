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
/// conductual (interceptor que inyecta un <c>40001</c> transitorio sobre la primera escritura,
/// conteo de intentos y conteo exacto de filas) vive en
/// <c>Ways.IntegrationTests.EscriturasSinReintentoTests</c> y solo puede ejercitar un camino por
/// prueba; esta cubre los once a la vez y no necesita Docker.
///
/// Cada fila de <see cref="SitiosSinReintento"/> es un sitio del audit: entidades construidas de
/// cero DENTRO del lambda (o números/códigos re-sorteados adentro), sin ninguna clave de
/// idempotencia natural que un reintento pueda usar para no duplicar.
///
/// <para><c>ServicioDeVentas.EmitirAsync</c> NO está en esa lista y tiene su propia prueba abajo:
/// es el único sitio del audit que SÍ conserva el reintento, porque su número precomiteado es una
/// clave de idempotencia real. La forma (a) del skill <c>ef-retry-safe-writes</c> —
/// <c>ChangeTracker.Clear()</c> como primera sentencia del lambda, guarda de commit ambiguo
/// inmediatamente después— es lo que la hace segura, y es exactamente lo que esa prueba
/// congela.</para>
/// </summary>
public class EscriturasSinReintentoEstructuralesTests
{
    /// <summary>Ruta, método y firma exacta de cada escritura sin reintento. La lista está
    /// congelada a propósito: revertir cualquiera de los once sitios a la estrategia reintentable
    /// pone esta prueba en rojo NOMBRANDO el método.</summary>
    public static TheoryData<string, string> SitiosSinReintento() => new()
    {
        { "Ways.Application/Clientes/ServicioDeClientes.cs", "CrearAsync" },
        { "Ways.Application/Articulos/ServicioDeArticulos.cs", "CrearAsync" },
        { "Ways.Application/Usuarios/ServicioDeUsuarios.cs", "CrearAsync" },
        { "Ways.Application/Precios/ServicioDePrecios.cs", "AbrirNuevoPrecioAsync" },
        { "Ways.Application/Fiscal/ServicioDeCertificados.cs", "RegistrarAsync" },
        { "Ways.Application/Catalogos/ServicioDeListasPrecio.cs", "CrearAsync" },
        { "Ways.Application/Ofertas/ServicioDeOfertas.cs", "CrearAsync" },
        { "Ways.Application/Ofertas/ServicioDeOfertas.cs", "ActualizarAsync" },

        // judgment-day fix/retry-double-add (item C4): la baja lógica fallaba el propio decision
        // gate del skill — un reintento sobre un commit ambiguo vuelve a leer la oferta por
        // BuscarAsync, que filtra BajaLogica, y responde 404 a una baja que sí tuvo éxito.
        { "Ways.Application/Ofertas/ServicioDeOfertas.cs", "EliminarAsync" },

        { "Ways.Application/Organizacion/ServicioDeAprovisionamiento.cs", "CrearTenantAsync" },

        // judgment-day fix/retry-double-add (item C3): el barrido del skill se había declarado
        // completo sobre Ways.Application y este sitio vive en Ways.Infrastructure — Adds de
        // ListaPrecio y Cliente dentro de un lambda reintentable, con el número de Consumidor
        // Final re-sorteado por intento: duplicado SILENCIOSO, ningún índice único lo frena.
        { "Ways.Infrastructure/Persistencia/InicializadorDeBaseDeDatos.cs", "BackfillDeClientesYListasPrecioAsync" },
    };

    /// <summary>
    /// El kill: el cuerpo del método referencia la fábrica sin reintento. Revertir un sitio a
    /// <c>db.Database.CreateExecutionStrategy()</c> deja el cuerpo sin la marca y la prueba falla
    /// diciendo exactamente qué método volvió a ser reintentable.
    /// </summary>
    [Theory]
    [MemberData(nameof(SitiosSinReintento))]
    public void CadaEscrituraNoIdempotenteCorreBajoLaEstrategiaSinReintento(string ruta, string metodo)
    {
        var cuerpo = CuerpoDelMetodo(LeerFuente(ruta), metodo);

        Assert.Contains(
            "FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(",
            cuerpo,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// La otra mitad del mismo enunciado, y la que no es redundante: que la fábrica esté presente
    /// no impide que la estrategia REINTENTABLE siga envolviendo la escritura al lado (una línea
    /// sobreviviente, un segundo <c>ExecuteAsync</c> agregado después). Ninguno de los once sitios
    /// puede nombrar <c>CreateExecutionStrategy</c> en absoluto.
    /// </summary>
    [Theory]
    [MemberData(nameof(SitiosSinReintento))]
    public void NingunaEscrituraCorregidaVuelveALaEstrategiaReintentable(string ruta, string metodo)
    {
        var cuerpo = CuerpoDelMetodo(LeerFuente(ruta), metodo);

        Assert.DoesNotContain("CreateExecutionStrategy(", cuerpo, StringComparison.Ordinal);
    }

    /// <summary>
    /// El camino caliente del POS, afirmado pieza por pieza porque las cuatro propiedades se
    /// pueden romper por separado:
    ///
    /// <list type="bullet">
    /// <item>el paso de NUMERACIÓN conserva la estrategia reintentable — reservar de nuevo solo
    /// vuelve a avanzar el contador, nunca duplica una fila;</item>
    /// <item>el paso de ESCRITURA TAMBIÉN reintenta, y ninguna de las dos es la fábrica sin
    /// reintento. El reintento es el ÚNICO consumidor de la clave de idempotencia: un reenvío del
    /// cajero trae una <c>SolicitudDeVenta</c> SIN número y emitiría un segundo comprobante, con
    /// su segundo descuento de stock y sus segundos movimientos de caja y cuenta corriente;</item>
    /// <item><c>db.ChangeTracker.Clear()</c> es la PRIMERA sentencia del lambda reintentado — sin
    /// él, las entidades <c>Added</c> del intento N (comprobante, ítems, pagos) sobreviven y el
    /// intento N+1 agrega un segundo set que el <c>SaveChangesAsync</c> final inserta;</item>
    /// <item>la guarda de commit ambiguo <c>BuscarPorNumeroComprometidoAsync</c> corre
    /// INMEDIATAMENTE después del <c>Clear</c> y ANTES de reinsertar: si el commit anterior sí
    /// llegó a puerto, devuelve el comprobante ya emitido en vez de chocar contra
    /// <c>ux_comprobantes_venta_numero</c>.</item>
    /// </list>
    ///
    /// El <c>Regex</c> de abajo es el kill de las dos últimas a la vez: exige el <c>Clear</c> y la
    /// guarda CONTIGUOS y en ese orden justo después de abrir el lambda, así que mover el
    /// <c>Clear</c> abajo de la guarda, meter cualquier otra sentencia antes, o borrarlo, dejan la
    /// prueba en rojo. Se afirma además que hay EXACTAMENTE DOS <c>CreateExecutionStrategy</c>.
    /// </summary>
    [Fact]
    public void LaVentaNumeraYEscribeConReintentoConElTrackerLimpioAntesDeLaGuardaDeCommitAmbiguo()
    {
        var cuerpo = CuerpoDelMetodo(
            LeerFuente("Ways.Application/Ventas/ServicioDeVentas.cs"), "EmitirAsync");

        var reintentables = Regex.Matches(
            cuerpo, @"CreateExecutionStrategy\(\)", RegexOptions.None, TimeSpan.FromSeconds(5));

        Assert.Equal(2, reintentables.Count);
        // La LLAMADA, no el nombre suelto: el doc-comment del método explica por qué este sitio no
        // usa la fábrica, y nombrarla ahí no la invoca.
        Assert.DoesNotContain(
            "FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(", cuerpo, StringComparison.Ordinal);
        Assert.Contains(
            "var estrategiaNumeracion = db.Database.CreateExecutionStrategy();",
            cuerpo,
            StringComparison.Ordinal);
        Assert.Contains(
            "var estrategia = db.Database.CreateExecutionStrategy();",
            cuerpo,
            StringComparison.Ordinal);

        var numeracion = Posicion(cuerpo, "var estrategiaNumeracion = db.Database.CreateExecutionStrategy();");
        var escritura = Posicion(cuerpo, "var estrategia = db.Database.CreateExecutionStrategy();");
        var limpieza = Posicion(cuerpo, "db.ChangeTracker.Clear();");
        var guarda = Posicion(cuerpo, "await BuscarPorNumeroComprometidoAsync(");
        var transaccion = Posicion(cuerpo, "await EjecutarTransaccionAsync(");

        Assert.True(numeracion < escritura, "El número se compromete ANTES de abrir la escritura.");
        Assert.True(escritura < limpieza, "El tracker se limpia DENTRO del lambda reintentado.");
        Assert.True(limpieza < guarda, "El ChangeTracker.Clear() corre ANTES de la guarda.");
        Assert.True(guarda < transaccion, "La guarda de commit ambiguo corre ANTES de reinsertar.");

        Assert.Matches(
            new Regex(
                @"estrategia\.ExecuteAsync\(async \(\) =>\s*\{\s*db\.ChangeTracker\.Clear\(\);\s*"
                + @"return await BuscarPorNumeroComprometidoAsync\(",
                RegexOptions.None,
                TimeSpan.FromSeconds(5)),
            cuerpo);
    }

    private static string LeerFuente(string rutaRelativa) =>
        File.ReadAllText(Path.Combine(
            RaizDelRepositorio.Resolver(), "src", Path.Combine(rutaRelativa.Split('/'))));

    private static int Posicion(string cuerpo, string marca)
    {
        var indice = cuerpo.IndexOf(marca, StringComparison.Ordinal);
        Assert.True(indice >= 0, $"No se encontró '{marca}' en el cuerpo del método.");
        return indice;
    }

    /// <summary>Extrae el cuerpo de un método por conteo de llaves desde su firma — mismo criterio
    /// que <c>BajasEstructuralesTests</c>, pero admitiendo <c>override</c>, un tipo de retorno
    /// genérico (<c>Task&lt;T&gt;</c>) y cualquier modificador de acceso: el sitio del backfill de
    /// <c>InicializadorDeBaseDeDatos</c> es <c>private</c>, no <c>public</c> como los diez
    /// servicios.</summary>
    private static string CuerpoDelMetodo(string fuente, string nombre)
    {
        var firma = Regex.Match(
            fuente,
            $@"(?:public|private|internal|protected)\s+(?:override\s+)?async\s+Task(?:<[^>]+>)?\s+{Regex.Escape(nombre)}\s*\(",
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
