using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Organizacion;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;

namespace Ways.Application.Vitrinas;

/// <summary>
/// La vitrina de cada empresa: leerla, guardarla y subir o quitar su logo. La administra el admin del
/// tenant (<c>GestionDeOrganizacion</c> en la API); la plataforma también puede.
///
/// Orden de cada operación: primero 404 (la empresa no existe, está dada de baja o es de otro tenant),
/// después la validación del cuerpo, después la escritura. Las tres escrituras corren bajo
/// <see cref="FabricaDeEstrategiaSinReintento"/> (skill <c>ef-retry-safe-writes</c>, forma (b)) y bajo el
/// mismo lock de tenant que las bajas de organización y los dominios: una vitrina no puede nacer
/// mientras se da de baja su empresa o su tenant, y dos publicaciones del mismo tenant se serializan, de
/// modo que la segunda llega a <c>ux_vitrinas_publicada_por_tenant</c> y <c>ManejadorDeErrores</c> la
/// traduce al 409 <c>vitrina_ya_publicada</c>. La vitrina se lee una sola vez, adentro de la
/// transacción y después del lock (<c>single-read-under-lock</c>).
/// </summary>
public class ServicioDeVitrinas(IWaysDbContext db, IRelojDelSistema reloj)
{
    public async Task<VitrinaAdmin> ObtenerAsync(int idEmpresa, CancellationToken ct = default)
    {
        await ObtenerTenantDeLaEmpresaAsync(idEmpresa, ct);

        var fila = await db.Vitrinas
            .AsNoTracking()
            .Where(v => v.IdEmpresa == idEmpresa)
            .Select(v => new
            {
                v.Publicada,
                v.Tagline,
                v.Descripcion,
                v.ColorAcento,
                v.Tema,
                TieneLogo = v.Logo != null,
                v.LogoActualizadoEn
            })
            .FirstOrDefaultAsync(ct);

        return fila is null
            ? new VitrinaAdmin(
                Existe: false, idEmpresa, Publicada: false, Tagline: null, Descripcion: null, ColorAcento: null,
                Vitrina.TemaOscuro, TieneLogo: false, LogoVersion: null)
            : new VitrinaAdmin(
                Existe: true, idEmpresa, fila.Publicada, fila.Tagline, fila.Descripcion, fila.ColorAcento,
                fila.Tema, fila.TieneLogo, Vitrina.VersionDelLogo(fila.LogoActualizadoEn));
    }

    public async Task<VitrinaAdmin> GuardarAsync(
        int idEmpresa, SolicitudDeVitrina datos, CancellationToken ct = default)
    {
        var idTenant = await ObtenerTenantDeLaEmpresaAsync(idEmpresa, ct);

        var publicada = datos.Publicada
            ?? throw new ErrorDominio("publicada_requerida", "El campo publicada es obligatorio.", 400);
        var tagline = Vitrina.NormalizarTexto(datos.Tagline, Vitrina.LargoMaximoDelTagline, "El slogan");
        var descripcion = Vitrina.NormalizarTexto(
            datos.Descripcion, Vitrina.LargoMaximoDeLaDescripcion, "La descripción");
        var colorAcento = Vitrina.NormalizarColor(datos.ColorAcento);
        var tema = Vitrina.NormalizarTema(datos.Tema);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await EnUnaTransaccionAsync(estrategia, idEmpresa, idTenant, async () =>
        {
            var vitrina = await LeerOCrearAsync(idEmpresa, idTenant, ct);

            vitrina.Publicada = publicada;
            vitrina.Tagline = tagline;
            vitrina.Descripcion = descripcion;
            vitrina.ColorAcento = colorAcento;
            vitrina.Tema = tema;
            vitrina.UpdatedAt = reloj.Ahora;

            await db.SaveChangesAsync(ct);

            return Mapear(vitrina);
        }, ct);
    }

    /// <summary>
    /// Sube el logo desde el cuerpo crudo de la request. El 404 va antes de tocar el cuerpo; después, el
    /// tipo declarado (415 antes de leer un solo byte), el tamaño (413) y el tipo real por los primeros
    /// bytes (415). Si la empresa todavía no tiene vitrina, la crea sin publicar.
    /// </summary>
    public async Task<VitrinaAdmin> GuardarLogoAsync(
        int idEmpresa, string? tipoDeclarado, Stream cuerpo, CancellationToken ct = default)
    {
        var idTenant = await ObtenerTenantDeLaEmpresaAsync(idEmpresa, ct);

        var declarado = TipoSinParametros(tipoDeclarado);
        if (!Vitrina.EsTipoDeLogoAdmitido(declarado))
        {
            throw new ErrorDominio(
                "logo_no_admitido",
                "El logo tiene que enviarse como image/png, image/jpeg o image/webp.",
                415);
        }

        var contenido = await LeerAcotadoAsync(cuerpo, ct);
        var tipo = Vitrina.ValidarLogo(contenido, declarado);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await EnUnaTransaccionAsync(estrategia, idEmpresa, idTenant, async () =>
        {
            var vitrina = await LeerOCrearAsync(idEmpresa, idTenant, ct);

            var ahora = reloj.Ahora;
            vitrina.Logo = contenido;
            vitrina.LogoContentType = tipo;
            vitrina.LogoActualizadoEn = ahora;
            vitrina.UpdatedAt = ahora;

            await db.SaveChangesAsync(ct);

            return Mapear(vitrina);
        }, ct);
    }

    /// <summary>Quita el logo. Idempotente: sin vitrina o sin logo no hay nada que hacer.</summary>
    public async Task QuitarLogoAsync(int idEmpresa, CancellationToken ct = default)
    {
        var idTenant = await ObtenerTenantDeLaEmpresaAsync(idEmpresa, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await EnUnaTransaccionAsync(estrategia, idEmpresa, idTenant, async () =>
        {
            var vitrina = await db.Vitrinas.FirstOrDefaultAsync(v => v.IdEmpresa == idEmpresa, ct);

            if (vitrina?.Logo is null)
            {
                return false;
            }

            vitrina.Logo = null;
            vitrina.LogoContentType = null;
            vitrina.LogoActualizadoEn = null;
            vitrina.UpdatedAt = reloj.Ahora;

            await db.SaveChangesAsync(ct);

            return true;
        }, ct);
    }

    // --- Común ---

    /// <summary>404 primero, fuera de la transacción: la empresa no existe, está dada de baja o es de
    /// otro tenant (el filtro de EF y RLS la dejan invisible). Devuelve su tenant, que es la clave del
    /// lock y el <c>id_tenant</c> de la vitrina cuando la escribe la plataforma.</summary>
    private async Task<int> ObtenerTenantDeLaEmpresaAsync(int idEmpresa, CancellationToken ct) =>
        await db.Empresas
            .Where(e => e.Id == idEmpresa)
            .Select(e => (int?)e.IdTenant)
            .FirstOrDefaultAsync(ct)
        ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");

    /// <summary>La ÚNICA lectura de la vitrina de una escritura: adentro de la transacción y después del
    /// lock. Si no existe, la agrega sin publicar.</summary>
    private async Task<Vitrina> LeerOCrearAsync(int idEmpresa, int idTenant, CancellationToken ct)
    {
        var vitrina = await db.Vitrinas.FirstOrDefaultAsync(v => v.IdEmpresa == idEmpresa, ct);

        if (vitrina is not null)
        {
            return vitrina;
        }

        var ahora = reloj.Ahora;
        vitrina = new Vitrina { IdTenant = idTenant, IdEmpresa = idEmpresa, CreatedAt = ahora, UpdatedAt = ahora };
        db.Vitrinas.Add(vitrina);

        return vitrina;
    }

    private async Task<T> EnUnaTransaccionAsync<T>(
        IExecutionStrategy estrategia, int idEmpresa, int idTenant, Func<Task<T>> cuerpo, CancellationToken ct) =>
        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await TomarLockDelTenantAsync(idTenant, ct);

            // Con el lock tomado, la baja de la empresa ya commiteó o todavía no empezó.
            if (!await db.Empresas.AnyAsync(e => e.Id == idEmpresa, ct))
            {
                throw ErrorDominio.NoEncontrado($"No existe la empresa {idEmpresa}.");
            }

            var resultado = await cuerpo();

            await transaccion.CommitAsync(ct);
            return resultado;
        });

    private static VitrinaAdmin Mapear(Vitrina v) => new(
        Existe: true, v.IdEmpresa, v.Publicada, v.Tagline, v.Descripcion, v.ColorAcento, v.Tema,
        TieneLogo: v.Logo is not null, Vitrina.VersionDelLogo(v.LogoActualizadoEn));

    /// <summary><c>image/png; charset=binary</c> es <c>image/png</c>.</summary>
    private static string? TipoSinParametros(string? contentType)
    {
        var tipo = contentType?.Split(';')[0].Trim().ToLowerInvariant();

        return string.IsNullOrEmpty(tipo) ? null : tipo;
    }

    /// <summary>Lee a lo sumo un byte más que el máximo: alcanza para saber que se pasó sin traer el
    /// resto del cuerpo.</summary>
    private static async Task<byte[]> LeerAcotadoAsync(Stream cuerpo, CancellationToken ct)
    {
        using var memoria = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var restante = Vitrina.TamanoMaximoDelLogo + 1;

        while (restante > 0)
        {
            var leidos = await cuerpo.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, restante)), ct);

            if (leidos == 0)
            {
                break;
            }

            memoria.Write(buffer, 0, leidos);
            restante -= leidos;
        }

        return memoria.ToArray();
    }

    /// <summary>
    /// <c>pg_advisory_xact_lock</c> sobre el tenant dueño con la MISMA clave que las bajas de
    /// organización (<see cref="ServicioDeOrganizacion.ClaveDeLockDeBaja"/>). Mismo idioma de ADO crudo
    /// que <c>ServicioDeDominios</c>: la conexión se abre por <c>Database.OpenConnectionAsync</c> para que
    /// el interceptor aplique los GUC de RLS.
    /// </summary>
    private async Task TomarLockDelTenantAsync(int idTenant, CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        await using var comando = conexion.CreateCommand();
        comando.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        comando.CommandText = "SELECT pg_advisory_xact_lock($1, $2)";

        ParametrosDeComando.Agregar(comando, idTenant);
        ParametrosDeComando.Agregar(comando, ServicioDeOrganizacion.ClaveDeLockDeBaja);

        await comando.ExecuteNonQueryAsync(ct);
    }
}
