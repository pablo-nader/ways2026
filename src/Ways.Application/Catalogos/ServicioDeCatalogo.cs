using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Application.Catalogos;

/// <summary>
/// Las 5 operaciones que comparten los catálogos de tenant (ADR-11): list/create/edit/
/// soft-delete/get. El aislamiento de tenant no aparece acá — lo aplican el filtro de EF y
/// RLS por debajo, transparente para este servicio (doc 09). El único dato propio de cada
/// catálogo que este servicio necesita es dónde vive (<see cref="Conjunto"/>), cómo
/// proyectarlo a su DTO de listado (<see cref="Proyectar"/>) y cómo aplicar sus columnas
/// propias (<see cref="AplicarPropios"/>) — <c>Nombre</c>/<c>Activo</c>/<c>IdEmpresa</c> ya
/// están cubiertos acá porque viven en <see cref="CatalogoSimple"/>.
/// </summary>
public abstract class ServicioDeCatalogo<T, TListado, TAlta>(IWaysDbContext db, IRelojDelSistema reloj)
    where T : CatalogoSimple
    where TListado : ListadoDeCatalogo
    where TAlta : AltaDeCatalogo
{
    /// <summary>Expone el <c>db</c> del constructor primario a las subclases sin que ellas
    /// vuelvan a capturar su propio parámetro <c>db</c> (evitaría CS9107: un mismo valor
    /// capturado dos veces, acá y en la subclase, al pasarlo también a este constructor
    /// base).</summary>
    protected IWaysDbContext Db => db;

    protected abstract DbSet<T> Conjunto { get; }

    protected abstract TListado Proyectar(T entidad);

    /// <summary>Instancia una fila nueva con los campos comunes ya seteados. No se usa un
    /// constraint <c>new()</c> genérico a propósito: <c>Nombre</c> es <c>required</c> en
    /// <see cref="CatalogoSimple"/>, y el compilador no puede verificar `required` a través
    /// de un `new T()` genérico (CS9040) — cada catálogo concreto satisface `required` con
    /// su propio inicializador de objeto.</summary>
    protected abstract T Instanciar(string nombre, int? idEmpresa, bool activo, DateTimeOffset ahora);

    /// <summary>Mapea las columnas propias de cada catálogo (~10 líneas, ADR-11). Sin
    /// implementación por defecto: cada catálogo que agrega algo más que
    /// <see cref="CatalogoSimple"/> tiene que decidir explícitamente cómo mapearlo — un
    /// catálogo sin columnas propias (p.ej. <c>marcas</c>) puede dejar el cuerpo vacío.</summary>
    protected abstract void AplicarPropios(T entidad, TAlta datos);

    public virtual async Task<IReadOnlyList<TListado>> ListarAsync(
        bool incluirInactivos = false, CancellationToken ct = default)
    {
        var query = Conjunto.AsQueryable();

        if (!incluirInactivos)
        {
            query = query.Where(e => e.Activo);
        }

        var items = await query.OrderBy(e => e.Nombre).ToListAsync(ct);
        return items.Select(Proyectar).ToList();
    }

    public virtual async Task<TListado> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var entidad = await BuscarAsync(id, ct);
        return Proyectar(entidad);
    }

    public virtual async Task<TListado> CrearAsync(TAlta datos, CancellationToken ct = default)
    {
        var nombre = Normalizar(datos.Nombre);
        await ExigirDisponibilidadAsync(nombre, datos.IdEmpresa, excluirId: null, ct);

        var ahora = reloj.Ahora;
        var entidad = Instanciar(nombre, datos.IdEmpresa, datos.Activo, ahora);
        AplicarPropios(entidad, datos);

        Conjunto.Add(entidad);
        await db.SaveChangesAsync(ct);

        return Proyectar(entidad);
    }

    public virtual async Task<TListado> ActualizarAsync(int id, TAlta datos, CancellationToken ct = default)
    {
        var entidad = await BuscarAsync(id, ct);

        var nombre = Normalizar(datos.Nombre);
        await ExigirDisponibilidadAsync(nombre, datos.IdEmpresa, excluirId: id, ct);

        entidad.Nombre = nombre;
        entidad.IdEmpresa = datos.IdEmpresa;
        entidad.Activo = datos.Activo;
        entidad.UpdatedAt = reloj.Ahora;
        AplicarPropios(entidad, datos);

        await db.SaveChangesAsync(ct);

        return Proyectar(entidad);
    }

    /// <summary>Baja lógica: escribe <c>deleted_at</c>, no borra la fila.</summary>
    public virtual async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var entidad = await BuscarAsync(id, ct);

        entidad.DeletedAt = reloj.Ahora;
        entidad.UpdatedAt = reloj.Ahora;

        await db.SaveChangesAsync(ct);
    }

    protected async Task<T> BuscarAsync(int id, CancellationToken ct) =>
        await Conjunto.FirstOrDefaultAsync(e => e.Id == id, ct)
            // El filtro de EF (+ RLS por debajo) ya deja invisible la fila de otro tenant —
            // esto solo cubre "no existe en absoluto" (ADR-8: mismo 404 en los dos casos).
            ?? throw ErrorDominio.NoEncontrado($"No existe el recurso {id}.");

    /// <summary>Replica a nivel de aplicación el mismo par de índices únicos parciales que
    /// <c>ConfiguracionDeCatalogo&lt;T&gt;</c> declara en la base (ADR-11): compartido
    /// (<c>IdEmpresa</c> nulo) vs propio de una empresa. No reemplaza al índice — sigue
    /// siendo el backstop real ante una carrera — pero da un 409 de negocio en el caso común
    /// en vez de dejar que la excepción de Postgres llegue sin traducir.</summary>
    private async Task ExigirDisponibilidadAsync(
        string nombre, int? idEmpresa, int? excluirId, CancellationToken ct)
    {
        var tomado = await Conjunto.AnyAsync(
            e => e.Nombre == nombre && e.IdEmpresa == idEmpresa && e.Id != excluirId, ct);

        if (tomado)
        {
            throw ErrorDominio.Conflicto("nombre_duplicado", $"Ya existe '{nombre}' en este alcance.");
        }
    }

    private static string Normalizar(string? valor)
    {
        var limpio = valor?.Trim() ?? string.Empty;

        if (limpio.Length == 0)
        {
            throw new ErrorDominio("nombre_requerido", "El nombre es obligatorio.", 400);
        }

        if (limpio.Length > 150)
        {
            throw new ErrorDominio(
                "nombre_muy_largo", "El nombre no puede superar los 150 caracteres.", 400);
        }

        return limpio;
    }
}
