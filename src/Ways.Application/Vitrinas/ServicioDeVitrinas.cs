using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Application.Organizacion;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;

namespace Ways.Application.Vitrinas;

/// <summary>
/// La vitrina de cada empresa: leerla, guardarla, darla de baja y subir, quitar o previsualizar su logo.
/// La administra el admin del tenant (<c>GestionDeOrganizacion</c> en la API); la plataforma también puede.
///
/// Orden de cada operación: primero 404 (la empresa no existe, está dada de baja o es de otro tenant),
/// después la validación del cuerpo, después la escritura. Las cuatro escrituras (guardar, subir el logo,
/// quitarlo y dar de baja la vitrina) corren bajo
/// <see cref="FabricaDeEstrategiaSinReintento"/> (skill <c>ef-retry-safe-writes</c>, forma (b)) y bajo el
/// mismo lock de tenant que las bajas de organización y los dominios: una vitrina no puede nacer
/// mientras se da de baja su empresa o su tenant, y dos publicaciones del mismo tenant se serializan, de
/// modo que la segunda llega a <c>ux_vitrinas_publicada_por_tenant</c> y <c>ManejadorDeErrores</c> la
/// traduce al 409 <c>vitrina_ya_publicada</c>. La vitrina se lee una sola vez, adentro de la
/// transacción y después del lock (<c>single-read-under-lock</c>).
/// </summary>
public class ServicioDeVitrinas(IWaysDbContext db, IRelojDelSistema reloj, GuardaDeReferencias guarda)
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
                v.IdListaPrecio,
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
                fila.Tema, fila.TieneLogo, Vitrina.VersionDelLogo(fila.LogoActualizadoEn), fila.IdListaPrecio);
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
            // FOR KEY SHARE sobre la lista, después del lock del tenant: la baja de una lista toma FOR UPDATE
            // sobre ella y cuenta sus referencias, así que o ve esta vitrina o la espera.
            if (datos.IdListaPrecio is { } idListaPrecio)
            {
                await ExigirListaValidaAsync(idListaPrecio, idEmpresa, idTenant, ct);
            }

            var vitrina = await LeerOCrearAsync(idEmpresa, idTenant, ct);

            vitrina.Publicada = publicada;
            vitrina.Tagline = tagline;
            vitrina.Descripcion = descripcion;
            vitrina.ColorAcento = colorAcento;
            vitrina.Tema = tema;
            vitrina.IdListaPrecio = datos.IdListaPrecio;
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

        var declarado = CuerpoDeImagen.TipoSinParametros(tipoDeclarado);
        if (!Vitrina.EsTipoDeLogoAdmitido(declarado))
        {
            throw new ErrorDominio(
                "logo_no_admitido",
                "El logo tiene que enviarse como image/png, image/jpeg o image/webp.",
                415);
        }

        var contenido = await CuerpoDeImagen.LeerAcotadoAsync(cuerpo, ct);
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

    /// <summary>
    /// Baja lógica de la vitrina de la empresa, que además la despublica: la policy pública exige
    /// <c>deleted_at IS NULL</c>. Idempotente a propósito: una empresa sin vitrina contesta lo mismo que una
    /// que la tenía (204 en la API), porque lo que el admin pidió —que no haya vitrina— ya se cumple; el
    /// 404 queda para la empresa que no se ve. No libera nada del guard de bajas: esa guarda cuenta las filas
    /// de <c>vitrinas</c> con SQL crudo, que ignora <c>deleted_at</c>, así que una vitrina dada de baja
    /// sigue impidiendo la baja de su empresa. Una vitrina nueva (PUT) la reemplaza: los índices únicos son
    /// parciales sobre las filas vivas.
    /// </summary>
    public async Task EliminarAsync(int idEmpresa, CancellationToken ct = default)
    {
        var idTenant = await ObtenerTenantDeLaEmpresaAsync(idEmpresa, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await EnUnaTransaccionAsync(estrategia, idEmpresa, idTenant, async () =>
        {
            var vitrina = await db.Vitrinas.FirstOrDefaultAsync(v => v.IdEmpresa == idEmpresa, ct);

            if (vitrina is null)
            {
                return false;
            }

            var ahora = reloj.Ahora;
            vitrina.DeletedAt = ahora;
            vitrina.UpdatedAt = ahora;

            await db.SaveChangesAsync(ct);

            return true;
        }, ct);
    }

    /// <summary>Tipo y marca de carga del logo, sin leer los bytes: con eso el endpoint arma el ETag y
    /// contesta 304 sin traer la columna. <c>null</c> cuando la empresa no tiene vitrina o no tiene logo; 404
    /// primero si la empresa no se ve. Sirve también para una vitrina sin publicar.</summary>
    public async Task<VersionDeLogo?> ObtenerVersionDelLogoAsync(int idEmpresa, CancellationToken ct = default)
    {
        await ObtenerTenantDeLaEmpresaAsync(idEmpresa, ct);

        var version = await db.Vitrinas
            .AsNoTracking()
            .Where(v => v.IdEmpresa == idEmpresa && v.Logo != null)
            .Select(v => new { v.LogoContentType, v.LogoActualizadoEn })
            .FirstOrDefaultAsync(ct);

        return version is { LogoContentType: not null, LogoActualizadoEn: not null }
            ? new VersionDeLogo(version.LogoContentType, version.LogoActualizadoEn.Value)
            : null;
    }

    /// <summary>Los bytes del logo para la vista previa del admin. Misma regla que
    /// <see cref="ObtenerVersionDelLogoAsync"/>.</summary>
    public async Task<LogoPublico?> ObtenerLogoAsync(int idEmpresa, CancellationToken ct = default)
    {
        await ObtenerTenantDeLaEmpresaAsync(idEmpresa, ct);

        var logo = await db.Vitrinas
            .AsNoTracking()
            .Where(v => v.IdEmpresa == idEmpresa && v.Logo != null)
            .Select(v => new { v.Logo, v.LogoContentType, v.LogoActualizadoEn })
            .FirstOrDefaultAsync(ct);

        return logo is { Logo: not null, LogoContentType: not null, LogoActualizadoEn: not null }
            ? new LogoPublico(logo.Logo, logo.LogoContentType, logo.LogoActualizadoEn.Value)
            : null;
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

    /// <summary>La lista del catálogo de la tienda tiene que existir, estar viva y activa, ser del tenant de la
    /// empresa y ser compartida o de esta empresa. Corre dentro de la transacción, bajo el lock del tenant.</summary>
    private async Task ExigirListaValidaAsync(int idListaPrecio, int idEmpresa, int idTenant, CancellationToken ct)
    {
        var viva = await guarda.BloquearSiEstaVivaAsync<ListaPrecio>(idListaPrecio, ct);

        // El IdTenant se compara a mano: una escritura de la plataforma ve las listas de todos los tenants.
        if (!viva || !await db.ListasPrecio.AnyAsync(
                l => l.Id == idListaPrecio && l.IdTenant == idTenant && l.Activo
                    && (l.IdEmpresa == null || l.IdEmpresa == idEmpresa),
                ct))
        {
            throw new ErrorDominio(
                "lista_invalida",
                "La lista de precios no existe, está inactiva o no corresponde a esta empresa.",
                400);
        }
    }

    private static VitrinaAdmin Mapear(Vitrina v) => new(
        Existe: true, v.IdEmpresa, v.Publicada, v.Tagline, v.Descripcion, v.ColorAcento, v.Tema,
        TieneLogo: v.Logo is not null, Vitrina.VersionDelLogo(v.LogoActualizadoEn), v.IdListaPrecio);

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
