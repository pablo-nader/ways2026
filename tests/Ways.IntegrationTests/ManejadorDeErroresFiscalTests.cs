using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ways.Api.Seguridad;

namespace Ways.IntegrationTests;

/// <summary>
/// stage-19a-slice1 (task 1.22, db-error-backstops, mutation targets 4-13): mismo patrón
/// unit-style que <see cref="ManejadorDeErroresRemitosTests"/> — sin <c>WaysApiFixture</c> ni
/// Postgres real, la <see cref="PostgresException"/> se construye "a mano". No hay endpoint
/// todavía en esta slice (el ABM de certificados y la emisión fiscal llegan en slices 4/5) para
/// ejercer el round-trip HTTP completo; las diez ramas nuevas (2 × <c>23505</c> + 8 ×
/// <c>23514</c>) se prueban acá contra el <c>ManejadorDeErrores</c> real.
/// </summary>
public class ManejadorDeErroresFiscalTests
{
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

    private static PostgresException CrearExcepcion(string sqlState, string constraintName) =>
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

    private static async Task<(int Estado, string? Codigo)> ManejarAsync(Exception excepcion)
    {
        var servicioDeProblemDetails = new ServicioDeProblemDetailsFalso();
        var manejador = new ManejadorDeErrores(servicioDeProblemDetails, NullLogger<ManejadorDeErrores>.Instance);
        var contexto = new DefaultHttpContext();

        var manejado = await manejador.TryHandleAsync(contexto, excepcion, CancellationToken.None);

        Assert.True(manejado);
        Assert.NotNull(servicioDeProblemDetails.Ultimo);

        var problema = servicioDeProblemDetails.Ultimo!.ProblemDetails;
        return (contexto.Response.StatusCode, problema.Extensions["codigo"] as string);
    }

    // ---- ux_puntos_venta_numero_fiscal (el trap de ordering, SEXTA ocurrencia) -----------------

    /// <summary>Mutation target #12: prueba tanto el camino EF (<c>DbUpdateException</c>) como el
    /// camino raw-ADO (<c>PostgresException</c> pelada).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UxPuntosVentaNumeroFiscalGanaLaRamaNuevaAntesQueLaFamiliaGenericaDeNumero(bool caminoRawAdo)
    {
        var postgres = CrearExcepcion("23505", "ux_puntos_venta_numero_fiscal");
        Exception excepcion = caminoRawAdo ? postgres : new DbUpdateException("dup", postgres);

        var (estado, codigo) = await ManejarAsync(excepcion);

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("numero_fiscal_duplicado", codigo);
        // La prueba negativa que cierra el trap: NUNCA el código genérico de ux_clientes_numero
        // — sin la rama exacta ANTES de ClasificarUnicidad, la substring "_numero" la atraparía
        // primero.
        Assert.NotEqual("numero_duplicado", codigo);
    }

    [Fact]
    public async Task UxCertificadosFiscalesActivoSeTraduceA409CertificadoFiscalActivoDuplicado()
    {
        var postgres = CrearExcepcion("23505", "ux_certificados_fiscales_activo");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("dup", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("certificado_fiscal_activo_duplicado", codigo);
    }

    // ---- ClasificarCheckDeFiscal (detrás del guard SqlState 23514) — OCHO ramas -----------------

    [Fact]
    public async Task CkPuntosVentaNumeroFiscalRangoSeTraduceA400NumeroFiscalInvalido()
    {
        var postgres = CrearExcepcion("23514", "ck_puntos_venta_numero_fiscal_rango");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("numero_fiscal_invalido", codigo);
    }

    [Fact]
    public async Task CkComprobantesVentaFiscalCoherenteSeTraduceA409ComprobanteFiscalIncoherente()
    {
        var postgres = CrearExcepcion("23514", "ck_comprobantes_venta_fiscal_coherente");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("comprobante_fiscal_incoherente", codigo);
    }

    [Fact]
    public async Task CkComprobantesVentaCaeDigitosSeTraduceA400CaeInvalido()
    {
        var postgres = CrearExcepcion("23514", "ck_comprobantes_venta_cae_digitos");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("cae_invalido", codigo);
    }

    [Fact]
    public async Task CkCertificadosFiscalesVigenciaSeTraduceA400VigenciaDeCertificadoInvalida()
    {
        var postgres = CrearExcepcion("23514", "ck_certificados_fiscales_vigencia");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("vigencia_de_certificado_invalida", codigo);
    }

    [Fact]
    public async Task CkCertificadosFiscalesCuitSeTraduceA400CuitTitularInvalido()
    {
        var postgres = CrearExcepcion("23514", "ck_certificados_fiscales_cuit");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("cuit_titular_invalido", codigo);
    }

    [Fact]
    public async Task CkCertificadosFiscalesMaterialSeTraduceA400MaterialDeClaveInvalido()
    {
        var postgres = CrearExcepcion("23514", "ck_certificados_fiscales_material");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("material_de_clave_invalido", codigo);
    }

    [Fact]
    public async Task CkNumeracionesFiscalesRangoSeTraduceA400NumeroFiscalDeSerieInvalido()
    {
        var postgres = CrearExcepcion("23514", "ck_numeraciones_fiscales_rango");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("numero_fiscal_de_serie_invalido", codigo);
    }

    [Fact]
    public async Task CkNumeracionesFiscalesSincronizacionSeTraduceA409NumeracionFiscalSincronizacionIncoherente()
    {
        var postgres = CrearExcepcion("23514", "ck_numeraciones_fiscales_sincronizacion");
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("check", postgres));

        Assert.Equal(StatusCodes.Status409Conflict, estado);
        Assert.Equal("numeracion_fiscal_sincronizacion_incoherente", codigo);
    }

    // ---- FKs nuevas: el match genérico por prefijo "fk_" ya las cubre, sin cambio de código -----

    [Theory]
    [InlineData("fk_empresas_condicion_fiscal")]
    [InlineData("fk_certificados_fiscales_tenant")]
    [InlineData("fk_certificados_fiscales_empresa")]
    [InlineData("fk_numeraciones_fiscales_tenant")]
    [InlineData("fk_numeraciones_fiscales_punto_venta")]
    public async Task CadaFkNuevaSeTraduceA400ReferenciaInvalida(string nombreDeFk)
    {
        var postgres = CrearExcepcion("23503", nombreDeFk);
        var (estado, codigo) = await ManejarAsync(new DbUpdateException("fk", postgres));

        Assert.Equal(StatusCodes.Status400BadRequest, estado);
        Assert.Equal("referencia_invalida", codigo);
    }

    // ---- PK compuesta exenta: solo alcanzable por INSERT crudo/fuera de banda -------------------

    [Fact]
    public async Task PkNumeracionesFiscalesSinRamaDe23505CaeAlDefaultDe500()
    {
        var postgres = CrearExcepcion("23505", "pk_numeraciones_fiscales");
        var (estado, _) = await ManejarAsync(new DbUpdateException("pk", postgres));

        Assert.Equal(StatusCodes.Status500InternalServerError, estado);
    }
}
