using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Ways.Domain.Fiscal;
using Ways.Infrastructure.Fiscal;
using Ways.Infrastructure.Multitenancy;
using Ways.Infrastructure.Persistencia;

namespace Ways.Application.Tests.Fiscal;

/// <summary>
/// <see cref="CifradoDeClavesFiscales"/> — tasks.md Slice 4, targets 57-60 (D5's AAD, D6's
/// versionado y ausencia de fallback, la limpieza de <see cref="CryptographicOperations.ZeroMemory"/>).
/// Los targets 57/59 corren sobre el crypto PURO (<see cref="CifradoDeClavesFiscales.Cifrar"/>/
/// <see cref="CifradoDeClavesFiscales.Descifrar"/>, sin base de datos ni configuración); 58/60
/// necesitan la instancia completa (EF InMemory como <c>IWaysDbContext</c>, <c>IConfiguration</c>
/// en memoria) porque son propiedades del PUERTO (versionado de clave, limpieza del buffer), no
/// del algoritmo aislado.
/// </summary>
public class CifradoDeClavesFiscalesTests
{
    private static readonly byte[] ClaveMaestra = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] Plaintext = Encoding.UTF8.GetBytes("material-de-clave-de-prueba-19a");
    private const int IdTenant = 1;
    private const int IdEmpresa = 10;
    private const AmbienteFiscal Ambiente = AmbienteFiscal.Homologacion;
    private const string Huella = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd";

    private static (byte[] Ciphertext, byte[] Nonce, byte[] Tag) CifrarDeReferencia() =>
        CifradoDeClavesFiscales.Cifrar(ClaveMaestra, Plaintext, IdTenant, IdEmpresa, Ambiente, Huella);

    [Fact]
    public void CifrarYDescifrarConLosMismosComponentesDeFilaDevuelveElPlaintextOriginal()
    {
        var (ciphertext, nonce, tag) = CifrarDeReferencia();

        var descifrado = CifradoDeClavesFiscales.Descifrar(
            ClaveMaestra, ciphertext, nonce, tag, IdTenant, IdEmpresa, Ambiente, Huella);

        Assert.Equal(Plaintext, descifrado);
    }

    // --- Target 57: el AAD ata el ciphertext a su fila — tamperear CUALQUIERA de sus cuatro
    // componentes tiene que fallar la autenticación de AesGcm, uno por uno. ---

    [Fact]
    public void MoverElCiphertextAOtroIdTenantFallaLaAutenticacion()
    {
        var (ciphertext, nonce, tag) = CifrarDeReferencia();

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            CifradoDeClavesFiscales.Descifrar(
                ClaveMaestra, ciphertext, nonce, tag, IdTenant + 1, IdEmpresa, Ambiente, Huella));
    }

    [Fact]
    public void MoverElCiphertextAOtraEmpresaFallaLaAutenticacion()
    {
        var (ciphertext, nonce, tag) = CifrarDeReferencia();

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            CifradoDeClavesFiscales.Descifrar(
                ClaveMaestra, ciphertext, nonce, tag, IdTenant, IdEmpresa + 1, Ambiente, Huella));
    }

    [Fact]
    public void MoverElCiphertextAOtroAmbienteFallaLaAutenticacion()
    {
        var (ciphertext, nonce, tag) = CifrarDeReferencia();

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            CifradoDeClavesFiscales.Descifrar(
                ClaveMaestra, ciphertext, nonce, tag, IdTenant, IdEmpresa, AmbienteFiscal.Produccion, Huella));
    }

    [Fact]
    public void MoverElCiphertextAOtraHuellaFallaLaAutenticacion()
    {
        var (ciphertext, nonce, tag) = CifrarDeReferencia();
        var huellaDistinta = new string('f', Huella.Length);

        Assert.Throws<AuthenticationTagMismatchException>(() =>
            CifradoDeClavesFiscales.Descifrar(
                ClaveMaestra, ciphertext, nonce, tag, IdTenant, IdEmpresa, Ambiente, huellaDistinta));
    }

    // --- Target 59: sin clave maestra (ausente/corta), nada se escribe — el error nombrado, jamás
    // texto plano ni una excepción de crypto pelada. ---

    [Fact]
    public async Task CifrarAsyncSinClaveMaestraConfiguradaTiraElErrorNombradoYNoLlegaADevolverNada()
    {
        var db = CrearContexto(nameof(CifrarAsyncSinClaveMaestraConfiguradaTiraElErrorNombradoYNoLlegaADevolverNada));
        var configuracion = ConfiguracionSinClaves();
        var almacen = new CifradoDeClavesFiscales(db, configuracion);

        var error = await Assert.ThrowsAsync<Domain.Common.ErrorDominio>(() =>
            almacen.CifrarAsync(Plaintext, IdTenant, IdEmpresa, Ambiente, Huella, CancellationToken.None));

        Assert.Equal("clave_maestra_ausente", error.Codigo);
        Assert.Equal(503, error.EstadoHttp);
    }

    [Fact]
    public async Task CifrarAsyncConUnaClaveMaestraDeMenosDe32BytesTiraElErrorNombrado()
    {
        var db = CrearContexto(nameof(CifrarAsyncConUnaClaveMaestraDeMenosDe32BytesTiraElErrorNombrado));
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ways:Fiscal:ClaveMaestraActual"] = "v1",
                ["Ways:Fiscal:ClavesMaestras:v1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            })
            .Build();
        var almacen = new CifradoDeClavesFiscales(db, configuracion);

        var error = await Assert.ThrowsAsync<Domain.Common.ErrorDominio>(() =>
            almacen.CifrarAsync(Plaintext, IdTenant, IdEmpresa, Ambiente, Huella, CancellationToken.None));

        Assert.Equal("clave_maestra_ausente", error.Codigo);
    }

    [Fact]
    public async Task UsarCertificadoAsyncSinCertificadoActivoTiraCertificadoFiscalAusente()
    {
        var db = CrearContexto(nameof(UsarCertificadoAsyncSinCertificadoActivoTiraCertificadoFiscalAusente));
        var almacen = new CifradoDeClavesFiscales(db, ConfiguracionSinClaves());

        var error = await Assert.ThrowsAsync<Domain.Common.ErrorDominio>(() =>
            almacen.UsarCertificadoAsync(IdEmpresa, Ambiente, _ => Task.FromResult(true), CancellationToken.None));

        Assert.Equal("certificado_fiscal_ausente", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    // --- Target 58: el AAD EXCLUYE id_clave_maestra — una fila cifrada bajo una versión de clave
    // maestra sigue descifrando cuando la clave "actual" del sistema ya rotó a otra versión (el
    // puerto resuelve por el id_clave_maestra de LA FILA, nunca por la actual). ---

    [Fact]
    public async Task UnaFilaCifradaConUnaVersionDeClaveMaestraSigueDescifrandoTrasRotarLaClaveActual()
    {
        var nombreDeBase = nameof(UnaFilaCifradaConUnaVersionDeClaveMaestraSigueDescifrandoTrasRotarLaClaveActual);
        var claveV1 = RandomNumberGenerator.GetBytes(32);
        var claveV2 = RandomNumberGenerator.GetBytes(32);

        // La clave privada cifrada tiene que ser un PKCS#8 real que empareje con el PEM público
        // guardado en la fila — UsarCertificadoAsync la reconstruye de verdad
        // (X509Certificate2.CopyWithPrivateKey), no un blob opaco cualquiera.
        using var certificadoDePrueba = GenerarCertificadoDePrueba();
        var certificadoPem = certificadoDePrueba.ExportCertificatePem();
        using var rsaPrivada = certificadoDePrueba.GetRSAPrivateKey()!;
        var clavePrivadaPkcs8 = rsaPrivada.ExportPkcs8PrivateKey();

        // Estado ANTES de la rotación: "actual" = v1.
        var configuracionAntesDeRotar = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ways:Fiscal:ClaveMaestraActual"] = "v1",
                ["Ways:Fiscal:ClavesMaestras:v1"] = Convert.ToBase64String(claveV1)
            })
            .Build();

        int idCertificado;
        await using (var db = CrearContexto(nombreDeBase))
        {
            var almacen = new CifradoDeClavesFiscales(db, configuracionAntesDeRotar);
            var (ciphertext, nonce, tag, idClaveMaestra) = await almacen.CifrarAsync(
                clavePrivadaPkcs8, IdTenant, IdEmpresa, Ambiente, Huella, CancellationToken.None);

            Assert.Equal("v1", idClaveMaestra);

            var certificado = CrearCertificadoDePrueba(certificadoPem, ciphertext, nonce, tag, idClaveMaestra);
            db.CertificadosFiscales.Add(certificado);
            await db.SaveChangesAsync();
            idCertificado = certificado.Id;
        }

        // Estado DESPUÉS de la rotación: "actual" pasa a v2 — v1 sigue configurada (no se puede
        // apagar hasta que TODAS las filas viejas se re-cifren), pero ya no es la que cifra
        // material nuevo. La fila de arriba NUNCA se re-cifra en este test — sigue con
        // id_clave_maestra = "v1" en la base.
        var configuracionDespuesDeRotar = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ways:Fiscal:ClaveMaestraActual"] = "v2",
                ["Ways:Fiscal:ClavesMaestras:v1"] = Convert.ToBase64String(claveV1),
                ["Ways:Fiscal:ClavesMaestras:v2"] = Convert.ToBase64String(claveV2)
            })
            .Build();

        await using var dbDespues = CrearContexto(nombreDeBase);
        var almacenDespues = new CifradoDeClavesFiscales(dbDespues, configuracionDespuesDeRotar);

        var descifradoOk = await almacenDespues.UsarCertificadoAsync(
            IdEmpresa, Ambiente, cert => Task.FromResult(cert is not null), CancellationToken.None);

        Assert.True(descifradoOk);
        Assert.True(idCertificado > 0);
    }

    // --- Target 60 [S]: UsarCertificadoAsync limpia su buffer descifrado en un finally. ---

    [Fact]
    public void UsarCertificadoAsyncLimpiaElBufferDescifradoEnUnFinally()
    {
        var directorioDeEsteArchivo = Path.GetDirectoryName(RutaDeEsteArchivo())!;
        var ruta = Path.GetFullPath(Path.Combine(
            directorioDeEsteArchivo, "..", "..", "..", "src", "Ways.Infrastructure", "Fiscal", "CifradoDeClavesFiscales.cs"));
        var fuente = File.ReadAllText(ruta);

        var inicio = fuente.IndexOf("public async Task<T> UsarCertificadoAsync<T>(", StringComparison.Ordinal);
        Assert.True(inicio >= 0, "No se encontró el método UsarCertificadoAsync en el archivo fuente.");

        // El método completo es el próximo bloque hasta el cierre del `finally` que sigue al
        // `try` — recorta un rango generoso (2500 caracteres alcanza sobra) en vez de parsear C#.
        var bloque = fuente.Substring(inicio, Math.Min(2500, fuente.Length - inicio));

        Assert.Contains("finally", bloque, StringComparison.Ordinal);

        var indiceFinally = bloque.IndexOf("finally", StringComparison.Ordinal);
        var cuerpoFinally = bloque[indiceFinally..];

        // El gap real que este test dejaba pasar (mutation-proof-tests regla 2, corregido en el
        // resume de este apply): un ÚNICO "Contains" no discrimina QUÉ buffer se limpia — un
        // finally que solo zerea `claveMaestra` (la clave que ABRE la fila) y JAMÁS
        // `clavePrivadaPlana` (el material descifrado en sí, lo verdaderamente sensible que sale
        // del `Descifrar`) pasaba este assert igual. Ambos nombres de variable, no solo el nombre
        // del método, es lo que hace la aserción estructural discriminante.
        Assert.Contains("ZeroMemory(claveMaestra)", cuerpoFinally, StringComparison.Ordinal);
        Assert.Contains("ZeroMemory(clavePrivadaPlana)", cuerpoFinally, StringComparison.Ordinal);
    }

    private static string RutaDeEsteArchivo([System.Runtime.CompilerServices.CallerFilePath] string ruta = "") => ruta;

    private static IConfiguration ConfiguracionSinClaves() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static WaysDbContext CrearContexto(string nombreDeBase) =>
        new(new DbContextOptionsBuilder<WaysDbContext>().UseInMemoryDatabase(nombreDeBase).Options,
            TenantActualFijo.Plataforma);

    private static CertificadoFiscal CrearCertificadoDePrueba(
        string certificadoPem, byte[] ciphertext, byte[] nonce, byte[] tag, string idClaveMaestra)
    {
        var ahora = DateTimeOffset.UtcNow;
        return new CertificadoFiscal
        {
            IdTenant = IdTenant,
            IdEmpresa = IdEmpresa,
            Ambiente = Ambiente,
            Alias = "Homo de prueba",
            CuitTitular = "20111111112",
            CertificadoPem = certificadoPem,
            ClavePrivadaCifrada = ciphertext,
            Nonce = nonce,
            TagAutenticacion = tag,
            IdClaveMaestra = idClaveMaestra,
            HuellaSha256 = Huella,
            VigenciaDesde = ahora.AddDays(-1),
            VigenciaHasta = ahora.AddYears(1),
            Activo = true,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };
    }

    /// <summary>Generado en runtime (D7/decisión 12, mismo criterio que
    /// <c>CertificadoDePrueba</c>): CERO material de clave se escribe a disco ni se commitea.
    /// Exportable a propósito — el test necesita <c>GetRSAPrivateKey()</c>.</summary>
    private static X509Certificate2 GenerarCertificadoDePrueba()
    {
        using var rsa = RSA.Create(2048);
        var solicitud = new CertificateRequest(
            "CN=Ways Test Rotacion", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var ahora = DateTimeOffset.UtcNow;
        using var efimero = solicitud.CreateSelfSigned(ahora.AddDays(-1), ahora.AddYears(1));

        var contrasenaEfimera = Guid.NewGuid().ToString("N");
        var pfx = efimero.Export(X509ContentType.Pkcs12, contrasenaEfimera);
        return X509CertificateLoader.LoadPkcs12(pfx, contrasenaEfimera, X509KeyStorageFlags.Exportable);
    }
}
