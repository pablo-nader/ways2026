using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Fiscal;

namespace Ways.Application.Fiscal;

/// <summary>
/// El ABM de certificados fiscales bajo <c>Politicas.AdministracionFiscal</c> (solo Admin,
/// tasks.md Slice 4, target 63) — más la carga de <c>empresas.id_condicion_fiscal</c> /
/// <c>puntos_venta.numero_fiscal</c> que <c>FiscalEndpoints</c> agrupa bajo la misma policy
/// (proposal.md API surface 705-710). DEVIACIÓN REGISTRADA: <c>design.md</c>'s File changes table
/// solo nombra este servicio para "register/list/deactivate" del certificado — las dos rutas de
/// carga de empresa/PV no tienen dueño explícito en el diseño, y quedan acá por ser el único
/// servicio de aplicación bajo esta policy en esta slice.
///
/// <see cref="RegistrarAsync"/> implementa U4 (<c>UPDATE certificados_fiscales SET activo = false
/// … WHERE id_tenant = $ AND id_empresa = $ AND ambiente = $ AND activo AND deleted_at IS NULL</c>)
/// como la mitad "desactivar" de la rotación atómica (design.md: "rotation = deactivate+activate
/// inside one transaction") — SIEMPRE corre antes del alta, sea o no una rotación real: si no hay
/// fila activa, es un no-op; si la hay, la desactiva dentro de la MISMA transacción que inserta la
/// nueva fila activa, así que nunca hay una ventana con dos certificados activos a la vez (spec:
/// "Rotation Is Atomic — No Window With Two Active Certificates"). El backstop de esquema del
/// <c>23505</c> de <c>ux_certificados_fiscales_activo</c> (el "race backstop" de U4, ya existente
/// desde slice 1 en <c>ManejadorDeErrores</c>) es lo que traduce una carrera genuina entre dos
/// altas concurrentes — este método no lo reemplaza, lo ejercita.
/// </summary>
public class ServicioDeCertificados(IWaysDbContext db, IRelojDelSistema reloj, IAlmacenDeClavesFiscales almacen)
{
    // --- Certificados ---

    public async Task<CertificadoFiscalDto> RegistrarAsync(
        RegistroDeCertificadoFiscal datos, CancellationToken ct = default)
    {
        var alias = Normalizar(datos.Alias, "alias_de_certificado", "alias", 60);
        var cuitTitular = Normalizar(datos.CuitTitular, "cuit_titular", "CUIT titular", 11);

        var empresa = await db.Empresas.FirstOrDefaultAsync(e => e.Id == datos.IdEmpresa, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {datos.IdEmpresa}.");

        byte[]? clavePrivada = null;

        try
        {
            using var certificado = CargarPfx(datos.Pfx, datos.PasswordPfx);

            var certificadoPem = certificado.ExportCertificatePem();
            var huellaSha256 = Convert.ToHexStringLower(SHA256.HashData(certificado.RawData));

            using var rsa = certificado.GetRSAPrivateKey()
                ?? throw new ErrorDominio(
                    "certificado_sin_clave_rsa",
                    "El PFX no contiene una clave privada RSA — solo RSA está soportado en 19a.", 400);

            clavePrivada = rsa.ExportPkcs8PrivateKey();

            // EnableRetryOnFailure exige que BeginTransactionAsync viva DENTRO de la lambda del
            // execution strategy (mismo criterio que ServicioDePrecios.EstablecerPrecioAsync) —
            // sin esto, EF tira InvalidOperationException al primer BeginTransactionAsync manual.
            var estrategia = db.Database.CreateExecutionStrategy();

            var nuevo = await estrategia.ExecuteAsync(async () =>
            {
                await using var transaccion = await db.Database.BeginTransactionAsync(ct);

                // U4: desactiva lo que esté activo hoy para esta empresa+ambiente ANTES del alta
                // — no-op si no hay nada activo (primer alta), la mitad "desactivar" de una
                // rotación si lo hay. Misma transacción que el INSERT de más abajo (spec:
                // rotación atómica).
                await DesactivadorDeCertificadoFiscal.DesactivarActivoAsync(
                    db, empresa.IdTenant, empresa.Id, datos.Ambiente, reloj.Ahora, ct);

                var (ciphertext, nonce, tag, idClaveMaestra) = await almacen.CifrarAsync(
                    clavePrivada, empresa.IdTenant, empresa.Id, datos.Ambiente, huellaSha256, ct);

                var fila = new CertificadoFiscal
                {
                    IdTenant = empresa.IdTenant,
                    IdEmpresa = empresa.Id,
                    Ambiente = datos.Ambiente,
                    Alias = alias,
                    CuitTitular = cuitTitular,
                    CertificadoPem = certificadoPem,
                    ClavePrivadaCifrada = ciphertext,
                    Nonce = nonce,
                    TagAutenticacion = tag,
                    IdClaveMaestra = idClaveMaestra,
                    HuellaSha256 = huellaSha256,
                    VigenciaDesde = new DateTimeOffset(certificado.NotBefore.ToUniversalTime()),
                    VigenciaHasta = new DateTimeOffset(certificado.NotAfter.ToUniversalTime()),
                    Activo = true,
                    CreatedAt = reloj.Ahora,
                    UpdatedAt = reloj.Ahora
                };

                db.CertificadosFiscales.Add(fila);
                await db.SaveChangesAsync(ct);

                await transaccion.CommitAsync(ct);

                return fila;
            });

            return Proyectar(nuevo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clavePrivada);
            CryptographicOperations.ZeroMemory(datos.Pfx);
        }
    }

    public async Task<IReadOnlyList<CertificadoFiscalDto>> ListarAsync(CancellationToken ct = default) =>
        await db.CertificadosFiscales
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new CertificadoFiscalDto(
                c.Id, c.IdEmpresa, c.Ambiente, c.Alias, c.CuitTitular, c.VigenciaDesde, c.VigenciaHasta, c.Activo))
            .ToListAsync(ct);

    /// <summary>Idempotente a propósito (mismo criterio que
    /// <c>ServicioDeOrganizacion.CambiarEstadoTenantAsync</c>): desactivar un certificado ya
    /// inactivo no es un error.</summary>
    public async Task DesactivarAsync(int id, CancellationToken ct = default)
    {
        var certificado = await db.CertificadosFiscales.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el certificado fiscal {id}.");

        if (certificado.Activo)
        {
            certificado.Activo = false;
            certificado.UpdatedAt = reloj.Ahora;
            await db.SaveChangesAsync(ct);
        }
    }

    // --- Condición fiscal de empresa / número fiscal de punto de venta ---

    public async Task ActualizarCondicionFiscalDeEmpresaAsync(
        int idEmpresa, int idCondicionFiscal, CancellationToken ct = default)
    {
        var empresa = await db.Empresas.FirstOrDefaultAsync(e => e.Id == idEmpresa, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");

        empresa.IdCondicionFiscal = idCondicionFiscal;
        empresa.UpdatedAt = reloj.Ahora;

        await db.SaveChangesAsync(ct);
    }

    public async Task ActualizarNumeroFiscalDePuntoVentaAsync(
        int idPuntoVenta, int numeroFiscal, CancellationToken ct = default)
    {
        var puntoVenta = await db.PuntosVenta.FirstOrDefaultAsync(p => p.Id == idPuntoVenta, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

        puntoVenta.NumeroFiscal = numeroFiscal;
        puntoVenta.UpdatedAt = reloj.Ahora;

        await db.SaveChangesAsync(ct);
    }

    // --- Común ---

    private static X509Certificate2 CargarPfx(byte[] pfx, string password)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException)
        {
            throw new ErrorDominio(
                "pfx_invalido", "El archivo PFX o la contraseña son inválidos.", 400);
        }
    }

    private static CertificadoFiscalDto Proyectar(CertificadoFiscal c) => new(
        c.Id, c.IdEmpresa, c.Ambiente, c.Alias, c.CuitTitular, c.VigenciaDesde, c.VigenciaHasta, c.Activo);

    private static string Normalizar(string? valor, string codigo, string campo, int largoMaximo)
    {
        var limpio = valor?.Trim() ?? string.Empty;

        if (limpio.Length == 0)
        {
            throw new ErrorDominio($"{codigo}_requerido", $"El campo {campo} es obligatorio.", 400);
        }

        if (limpio.Length > largoMaximo)
        {
            throw new ErrorDominio(
                $"{codigo}_muy_largo", $"El campo {campo} no puede superar los {largoMaximo} caracteres.", 400);
        }

        return limpio;
    }
}
