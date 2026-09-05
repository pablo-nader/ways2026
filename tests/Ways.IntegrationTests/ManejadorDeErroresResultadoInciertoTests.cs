using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// LA CLÁUSULA (judgment-day fix/retry-double-add, item C2): el brazo de <c>ManejadorDeErrores</c>
/// que traduce un fallo TRANSITORIO de Postgres a <c>503</c> con código <c>resultado_incierto</c>.
///
/// Es el residual declarado de la forma (b) del skill <c>ef-retry-safe-writes</c> (regla 4): una
/// escritura sin reintento que muere por un error transitorio pudo haber COMITEADO igual — el
/// servidor comitea y el ACK se pierde al cortarse la conexión. Como <c>500 error_interno</c> las
/// diez pantallas decían "Ocurrió un error inesperado" y el operador reintentaba a ciegas sobre
/// algo que quizás ya existía; como <c>503 resultado_incierto</c> la copia manda a verificar el
/// listado primero, y llega a TODAS las pantallas sin tocar ninguna (cada una rinde
/// <c>ErrorApi.mensaje</c>, que es el <c>title</c> del ProblemDetails).
///
/// Mismo patrón unit-style que <see cref="ManejadorDeErroresFiscalTests"/>: sin
/// <c>WaysApiFixture</c> ni Postgres real, las excepciones se construyen "a mano". El round-trip
/// HTTP completo sobre una escritura real vive en
/// <c>EscriturasSinReintentoTests.UnFalloTransitorioEnUnAltaSinReintentoLlegaComo503ResultadoIncierto</c>.
/// </summary>
public class ManejadorDeErroresResultadoInciertoTests
{
    private const string CopiaEsperada =
        "No se pudo confirmar el resultado de la operación: verificá el listado antes de reintentar.";

    /// <summary>La copia del mismo fallo sobre un método SEGURO: neutra, sin residual que
    /// verificar.</summary>
    private const string CopiaDeConsulta = "No se pudo completar la consulta: reintentá en unos segundos.";

    private sealed class ServicioDeProblemDetailsFalso : IProblemDetailsService
    {
        public ProblemDetailsContext? Ultimo { get; private set; }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Ultimo = context;
            return ValueTask.FromResult(true);
        }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Ultimo = context;
            return ValueTask.CompletedTask;
        }
    }

    private static PostgresException CrearExcepcion(string sqlState, string? constraintName = null) =>
        new(
            messageText: "mensaje de prueba",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: sqlState,
            detail: null,
            hint: null,
            position: 0,
            internalPosition: 0,
            internalQuery: null,
            where: null,
            schemaName: null,
            tableName: null,
            columnName: null,
            dataTypeName: null,
            constraintName: constraintName,
            file: null,
            line: null,
            routine: null);

    /// <summary><paramref name="metodo"/> por default <c>POST</c>: todas las pruebas de esta
    /// clase salvo la del reparto son sobre ESCRITURAS, y el código del commit ambiguo es el de
    /// los métodos no seguros. Un <c>DefaultHttpContext</c> recién construido trae el método
    /// VACÍO, que clasificaría como no seguro por accidente en vez de por intención.</summary>
    private static async Task<(int Estado, string? Codigo, string? Titulo)> ManejarAsync(
        Exception excepcion, string metodo = "POST")
    {
        var servicioDeProblemDetails = new ServicioDeProblemDetailsFalso();
        var manejador = new ManejadorDeErrores(servicioDeProblemDetails, NullLogger<ManejadorDeErrores>.Instance);
        var contexto = new DefaultHttpContext();
        contexto.Request.Method = metodo;

        var manejado = await manejador.TryHandleAsync(contexto, excepcion, CancellationToken.None);

        Assert.True(manejado);
        Assert.NotNull(servicioDeProblemDetails.Ultimo);

        var problema = servicioDeProblemDetails.Ultimo!.ProblemDetails;
        return (contexto.Response.StatusCode, problema.Extensions["codigo"] as string, problema.Title);
    }

    /// <summary>
    /// Los SQLSTATE que <c>EnableRetryOnFailure</c> considera transitorios, por los DOS caminos que
    /// tiene el manejador: el de EF (<c>DbUpdateException</c> envolviendo la
    /// <see cref="PostgresException"/>, que es como sale un <c>SaveChangesAsync</c>) y el raw-ADO
    /// (excepción PELADA, que es como salen los statements crudos de
    /// <c>ServicioDeVentas</c>/<c>ServicioDeCompras</c>/<c>ServicioDeStock</c>). Un solo camino no
    /// cubre al otro: son dos brazos distintos del <c>switch</c>.
    ///
    /// <para>La clase <c>08</c> entera (connection_exception) entra: es LA forma del commit
    /// ambiguo — el canal se cortó y el cliente no sabe si el <c>COMMIT</c> llegó.</para>
    /// </summary>
    [Theory]
    [InlineData("40001", false)]  // serialization_failure
    [InlineData("40001", true)]
    [InlineData("40P01", false)]  // deadlock_detected
    [InlineData("40P01", true)]
    [InlineData("57P01", false)]  // admin_shutdown
    [InlineData("57P01", true)]
    [InlineData("08000", false)]  // connection_exception
    [InlineData("08000", true)]
    [InlineData("08003", false)]  // connection_does_not_exist
    [InlineData("08006", false)]  // connection_failure
    [InlineData("08006", true)]
    public async Task UnSqlStateTransitorioSeTraduceAResultadoIncierto(string sqlState, bool caminoEf)
    {
        var pg = CrearExcepcion(sqlState);
        Exception excepcion = caminoEf ? new DbUpdateException("fallo de escritura", pg) : pg;

        var (estado, codigo, titulo) = await ManejarAsync(excepcion);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, estado);
        Assert.Equal("resultado_incierto", codigo);
        Assert.Equal(CopiaEsperada, titulo);
    }

    /// <summary>
    /// El caso que NINGÚN SQLSTATE cubre y que es, justamente, el más frecuente del commit
    /// ambiguo: la conexión se cae mientras se espera el <c>COMMIT</c>. Npgsql no tiene servidor
    /// del otro lado para darle un SQLSTATE, así que tira una <see cref="NpgsqlException"/> PELADA
    /// (no una <see cref="PostgresException"/>) cuyo <c>IsTransient</c> es <c>true</c> por su
    /// <see cref="IOException"/> interna. Ese es el motivo por el que el brazo mira
    /// <see cref="NpgsqlException.IsTransient"/> además de la lista de SQLSTATE — con la lista
    /// sola, este caso caía al 500 genérico.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnaConexionCortadaSinSqlStateTambienEsResultadoIncierto(bool caminoEf)
    {
        var npgsql = new NpgsqlException(
            "Exception while reading from stream", new IOException("se cortó el canal"));

        Assert.True(npgsql.IsTransient, "La excepción de prueba tiene que ser transitoria de verdad.");

        Exception excepcion = caminoEf ? new DbUpdateException("fallo de escritura", npgsql) : npgsql;

        var (estado, codigo, titulo) = await ManejarAsync(excepcion);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, estado);
        Assert.Equal("resultado_incierto", codigo);
        Assert.Equal(CopiaEsperada, titulo);
    }

    /// <summary>
    /// Los brazos transitorios van DESPUÉS de los dos que llaman a
    /// <c>ClasificarPostgresException</c>, a propósito. Esta es la prueba de que no eclipsan
    /// nada: un <c>23505</c> con constraint mapeada sigue dando su 409 de dominio, y un
    /// error determinístico SIN mapeo sigue cayendo al 500 genérico (el brazo transitorio no es un
    /// catch-all disfrazado).
    /// </summary>
    [Theory]
    [InlineData("23505", "ux_usuarios_mail", StatusCodes.Status409Conflict, "mail_duplicado")]
    [InlineData("42601", null, StatusCodes.Status500InternalServerError, "error_interno")]
    public async Task ElBrazoTransitorioNoEclipsaNingunaClasificacionExistente(
        string sqlState, string? constraintName, int estadoEsperado, string codigoEsperado)
    {
        var (estado, codigo, _) = await ManejarAsync(CrearExcepcion(sqlState, constraintName));

        Assert.Equal(estadoEsperado, estado);
        Assert.Equal(codigoEsperado, codigo);
    }

    /// <summary>
    /// LA CLÁUSULA: el brazo <c>RetryLimitExceededException</c>.
    ///
    /// <para>Es el agujero que dejaban los otros dos brazos. Sobre un sitio que SÍ conserva el
    /// reintento, EF no propaga el fallo transitorio cuando agota los cinco intentos: lo envuelve
    /// en <see cref="RetryLimitExceededException"/>, que no es <c>DbUpdateException</c> ni
    /// <c>NpgsqlException</c> y caía derecho al <c>500 error_interno</c>. Es justo el caso peor
    /// —cinco commits potencialmente ambiguos, no uno— y era el único que no llevaba la copia.</para>
    ///
    /// <para>Las dos formas del <c>InnerException</c>: la excepción pelada (camino raw-ADO) y la
    /// <c>DbUpdateException</c> que a su vez la envuelve (camino <c>SaveChangesAsync</c>). El
    /// desenvuelto recorre la cadena entera, así que las dos llegan al mismo lugar. Mutante:
    /// borrar el brazo devuelve 500 y las tres afirmaciones se ponen en rojo.</para>
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ElLimiteDeReintentosAgotadoTambienEsResultadoIncierto(bool caminoEf)
    {
        var pg = CrearExcepcion("40001");
        Exception causa = caminoEf ? new DbUpdateException("fallo de escritura", pg) : pg;

        var (estado, codigo, titulo) = await ManejarAsync(
            new RetryLimitExceededException("se agotaron los reintentos", causa));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, estado);
        Assert.Equal("resultado_incierto", codigo);
        Assert.Equal(CopiaEsperada, titulo);
    }

    /// <summary>Un <see cref="RetryLimitExceededException"/> cuya causa NO es transitoria no se
    /// hace pasar por commit ambiguo: el brazo mira la cadena, no el tipo de la envoltura.</summary>
    [Fact]
    public async Task UnLimiteDeReintentosConCausaNoTransitoriaSigueCayendoAl500()
    {
        var (estado, codigo, _) = await ManejarAsync(
            new RetryLimitExceededException("se agotaron los reintentos", new InvalidOperationException("nada de base")));

        Assert.Equal(StatusCodes.Status500InternalServerError, estado);
        Assert.Equal("error_interno", codigo);
    }

    /// <summary>
    /// LA CLÁUSULA: el reparto por método HTTP de <c>RespuestaDeFalloTransitorio</c>.
    ///
    /// <para>El predicado transitorio incluye <c>IsTransient</c>, que una conexión cortada dispara
    /// igual en una LECTURA. Sin el reparto, un <c>GET</c> que pierde la conexión respondía
    /// <c>resultado_incierto</c> con la copia de una escritura ("verificá el listado antes de
    /// reintentar"): no hubo escritura, así que no hay nada que verificar y la copia miente.</para>
    ///
    /// <para>Los cuatro casos son la tabla entera del reparto —los tres métodos seguros de un lado,
    /// el escritor del otro— sobre EL MISMO fallo, así que lo único que puede explicar la
    /// diferencia es el método (<c>mutation-proof-tests</c> regla 4). Borrar el reparto pone en
    /// rojo las tres primeras filas; invertirlo, la cuarta.</para>
    /// </summary>
    [Theory]
    [InlineData("GET", "servicio_no_disponible", CopiaDeConsulta)]
    [InlineData("HEAD", "servicio_no_disponible", CopiaDeConsulta)]
    [InlineData("OPTIONS", "servicio_no_disponible", CopiaDeConsulta)]
    [InlineData("POST", "resultado_incierto", CopiaEsperada)]
    [InlineData("PUT", "resultado_incierto", CopiaEsperada)]
    [InlineData("DELETE", "resultado_incierto", CopiaEsperada)]
    public async Task ElMismoFalloTransitorioSeParteEnDosCopiasSegunElMetodo(
        string metodo, string codigoEsperado, string copiaEsperada)
    {
        var (estado, codigo, titulo) = await ManejarAsync(CrearExcepcion("40001"), metodo);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, estado);
        Assert.Equal(codigoEsperado, codigo);
        Assert.Equal(copiaEsperada, titulo);
    }
}
