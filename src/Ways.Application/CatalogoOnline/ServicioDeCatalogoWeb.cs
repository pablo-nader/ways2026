using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Bajas;
using Ways.Domain.Articulos;
using Ways.Domain.Common;

namespace Ways.Application.CatalogoOnline;

/// <summary>
/// Lo que el admin del catálogo decide sobre la tienda online y la venta de cada artículo: si está disponible
/// online, si está disponible a la venta y qué imagen lleva. Se autoriza con <c>GestionDeCatalogo</c> (la misma policy que editar el artículo) en la capa de API.
///
/// Orden de cada escritura: primero 404 (el artículo no existe, está dado de baja o es de otro tenant), después
/// la validación del cuerpo, después la escritura. Las tres escrituras corren bajo
/// <see cref="FabricaDeEstrategiaSinReintento"/> (skill <c>ef-retry-safe-writes</c>, forma (b)) y toman
/// <c>FOR UPDATE</c> sobre la fila del artículo antes de leer lo que mutan (skill
/// <c>single-read-under-lock</c>): dos subidas de imagen del mismo artículo se serializan, de modo que la
/// segunda ve la fila de la primera y la reemplaza en vez de chocar con <c>ux_imagenes_articulo_articulo</c>, y
/// una baja del artículo que gana la carrera se ve como 404 y no deja una imagen sobre un artículo borrado. Ese
/// lock es el único que toman: la disponibilidad solo cambia <c>disponible_online</c> y <c>disponible_venta</c>, que
/// no son campos compartidos de la familia, así que no entra al protocolo de locks de familias; con un solo lock y ninguna espera
/// después de tomarlo no puede cerrar un ciclo con <c>ServicioDeArticulos.ActualizarAsync</c>.
/// </summary>
public class ServicioDeCatalogoWeb(IWaysDbContext db, IRelojDelSistema reloj, GuardaDeReferencias guarda)
{
    /// <summary>Fija si un artículo está disponible online y a la venta. Idempotente: pedir el estado que ya tiene no
    /// escribe.</summary>
    public async Task<DisponibilidadDeArticulo> CambiarDisponibilidadAsync(
        int idArticulo, SolicitudDeDisponibilidad datos, CancellationToken ct = default)
    {
        await ObtenerTenantDelArticuloAsync(idArticulo, ct);

        var disponibleVenta = datos.DisponibleVenta
            ?? throw new ErrorDominio("disponible_venta_requerido", "El campo disponibleVenta es obligatorio.", 400);
        var disponibleOnline = datos.DisponibleOnline
            ?? throw new ErrorDominio("disponible_online_requerido", "El campo disponibleOnline es obligatorio.", 400);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await guarda.BloquearFilaAsync<Articulo>(idArticulo, ct);

            // La ÚNICA lectura del artículo: nacida bajo el lock.
            var articulo = await db.Articulos.FirstOrDefaultAsync(a => a.Id == idArticulo, ct)
                ?? throw SinArticulo(idArticulo);

            if (articulo.DisponibleVenta != disponibleVenta || articulo.DisponibleOnline != disponibleOnline)
            {
                articulo.DisponibleVenta = disponibleVenta;
                articulo.DisponibleOnline = disponibleOnline;
                articulo.UpdatedAt = reloj.Ahora;

                await db.SaveChangesAsync(ct);
            }

            await transaccion.CommitAsync(ct);

            return new DisponibilidadDeArticulo(idArticulo, articulo.DisponibleVenta, articulo.DisponibleOnline);
        });
    }

    /// <summary>
    /// Sube la imagen desde el cuerpo crudo de la request y reemplaza la que hubiera. El 404 va antes de tocar
    /// el cuerpo; después, el tipo declarado (415 antes de leer un solo byte), el tamaño (413) y el tipo real
    /// por los primeros bytes (415).
    /// </summary>
    public async Task<ImagenDeArticuloAdmin> GuardarImagenAsync(
        int idArticulo, string? tipoDeclarado, Stream cuerpo, CancellationToken ct = default)
    {
        var idTenant = await ObtenerTenantDelArticuloAsync(idArticulo, ct);

        var declarado = CuerpoDeImagen.TipoSinParametros(tipoDeclarado);
        if (!ValidadorDeImagen.EsTipoAdmitido(declarado))
        {
            throw new ErrorDominio(
                "imagen_no_admitida", "La imagen tiene que enviarse como image/png, image/jpeg o image/webp.", 415);
        }

        var contenido = await CuerpoDeImagen.LeerAcotadoAsync(cuerpo, ct);
        var tipo = ImagenDeArticulo.Validar(contenido, declarado);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await BloquearArticuloVivoAsync(idArticulo, ct);

            var ahora = reloj.Ahora;
            var imagen = await db.ImagenesArticulo.FirstOrDefaultAsync(i => i.IdArticulo == idArticulo, ct);

            if (imagen is null)
            {
                imagen = new ImagenDeArticulo
                {
                    IdTenant = idTenant,
                    IdArticulo = idArticulo,
                    Contenido = contenido,
                    ContentType = tipo,
                    ActualizadoEn = ahora,
                    CreatedAt = ahora,
                    UpdatedAt = ahora
                };
                db.ImagenesArticulo.Add(imagen);
            }
            else
            {
                imagen.Contenido = contenido;
                imagen.ContentType = tipo;
                imagen.ActualizadoEn = ahora;
                imagen.UpdatedAt = ahora;
            }

            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);

            return new ImagenDeArticuloAdmin(idArticulo, TieneImagen: true, ImagenDeArticulo.Version(ahora));
        });
    }

    /// <summary>
    /// Quita la imagen borrando la fila: no tiene historia que conservar y guardarla dada de baja dejaría hasta
    /// 1 MB por artículo sin que nada la use. Una subida posterior inserta una fila nueva. Idempotente: sin
    /// imagen no hay nada que hacer.
    /// </summary>
    public async Task QuitarImagenAsync(int idArticulo, CancellationToken ct = default)
    {
        await ObtenerTenantDelArticuloAsync(idArticulo, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await BloquearArticuloVivoAsync(idArticulo, ct);

            var imagenes = await db.ImagenesArticulo.Where(i => i.IdArticulo == idArticulo).ToListAsync(ct);

            if (imagenes.Count > 0)
            {
                db.ImagenesArticulo.RemoveRange(imagenes);

                await db.SaveChangesAsync(ct);
            }

            await transaccion.CommitAsync(ct);
        });
    }

    /// <summary>Tipo y marca de carga de la imagen, sin leer los bytes: con eso el endpoint arma el ETag y
    /// contesta 304 sin traer la columna. <c>null</c> cuando el artículo no tiene imagen; 404 primero si el
    /// artículo no se ve. Sirve también para un artículo sin publicar.</summary>
    public async Task<VersionDeImagen?> ObtenerVersionDeImagenAsync(int idArticulo, CancellationToken ct = default)
    {
        await ObtenerTenantDelArticuloAsync(idArticulo, ct);

        return await db.ImagenesArticulo
            .AsNoTracking()
            .Where(i => i.IdArticulo == idArticulo)
            .Select(i => new VersionDeImagen(i.ContentType, i.ActualizadoEn))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Los bytes de la imagen para la vista previa del admin. Misma regla que
    /// <see cref="ObtenerVersionDeImagenAsync"/>.</summary>
    public async Task<ImagenServida?> ObtenerImagenAsync(int idArticulo, CancellationToken ct = default)
    {
        await ObtenerTenantDelArticuloAsync(idArticulo, ct);

        return await db.ImagenesArticulo
            .AsNoTracking()
            .Where(i => i.IdArticulo == idArticulo)
            .Select(i => new ImagenServida(i.Contenido, i.ContentType, i.ActualizadoEn))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>404 primero, fuera de la transacción: el artículo no existe, está dado de baja o es de otro
    /// tenant (el filtro de EF y RLS lo dejan invisible). Devuelve su tenant, que es el <c>id_tenant</c> de la
    /// imagen cuando la escribe la plataforma. Es una proyección escalar de una columna inmutable: no mete
    /// ninguna entidad en el identity map, así que la lectura de adentro del lock sigue siendo la única.</summary>
    private async Task<int> ObtenerTenantDelArticuloAsync(int idArticulo, CancellationToken ct) =>
        await db.Articulos
            .Where(a => a.Id == idArticulo)
            .Select(a => (int?)a.IdTenant)
            .FirstOrDefaultAsync(ct)
        ?? throw SinArticulo(idArticulo);

    /// <summary>Toma el lock del artículo y comprueba, ya bajo él, que sigue vivo: la baja lógica no pudo
    /// comitear después. La existencia se lee con un escalar, no con la entidad.</summary>
    private async Task BloquearArticuloVivoAsync(int idArticulo, CancellationToken ct)
    {
        await guarda.BloquearFilaAsync<Articulo>(idArticulo, ct);

        if (!await db.Articulos.AnyAsync(a => a.Id == idArticulo, ct))
        {
            throw SinArticulo(idArticulo);
        }
    }

    private static ErrorDominio SinArticulo(int idArticulo) =>
        ErrorDominio.NoEncontrado($"No existe el artículo {idArticulo}.");
}
