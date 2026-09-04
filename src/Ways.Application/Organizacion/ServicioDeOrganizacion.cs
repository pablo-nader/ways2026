using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Application.Organizacion;

/// <summary>
/// Lectura y edición de datos descriptivos de la organización (doc 09): tenants
/// (plataforma-only, <c>Politicas.SoloPlataforma</c> en la API), empresas y puntos de venta
/// (plataforma ve/edita cualquiera; un admin de tenant ve/edita solo los propios). El filtro
/// de EF (+ RLS por debajo) ya deja invisible una fila de otro tenant — la llamada a
/// <see cref="PoliticaDeRoles.ValidarAlcanceDeTenant"/> es la capa explícita de dominio
/// (ADR-8), mismo patrón que <c>ServicioDeUsuarios.BuscarAsync</c>.
///
/// Alta y baja de tenants/empresas/puntos_venta siguen siendo plataforma-only vía
/// <see cref="ServicioDeAprovisionamiento"/> (ADR-16): este servicio no crea ni elimina nada,
/// solo lista/edita datos descriptivos y alterna el estado de un tenant entre Activo y
/// Suspendido.
/// </summary>
public class ServicioDeOrganizacion(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    private ActorDeGestion Actor => new(contexto.Rol, contexto.UsuarioId, contexto.IdTenant);

    // --- Tenants ---

    /// <summary>Los tres contadores son subconsultas correlacionadas dentro del MISMO
    /// <c>Select</c> (design D13): el listado sigue costando una sola ida a la base, sin N+1 por
    /// tenant. Al vivir dentro del árbol LINQ, cada <c>Count</c> arrastra el filtro
    /// <c>"BajaLogica"</c>, así que un hijo dado de baja queda afuera sin predicado explícito. Y
    /// <c>u.IdTenant == t.Id</c> sobre un <c>int?</c> no matchea nunca contra <c>NULL</c>: el
    /// personal de plataforma no se cuenta bajo ningún tenant.</summary>
    private Expression<Func<Tenant, TenantListado>> ProyeccionDeTenant => t => new TenantListado(
        t.Id,
        t.Nombre,
        t.Estado,
        t.CreatedAt,
        db.Empresas.Count(e => e.IdTenant == t.Id),
        db.PuntosVenta.Count(p => p.IdTenant == t.Id),
        db.Usuarios.Count(u => u.IdTenant == t.Id));

    public async Task<IReadOnlyList<TenantListado>> ListarTenantsAsync(CancellationToken ct = default) =>
        await db.Tenants
            .OrderBy(t => t.Nombre)
            .Select(ProyeccionDeTenant)
            .ToListAsync(ct);

    public async Task<TenantListado> ObtenerTenantAsync(int id, CancellationToken ct = default) =>
        await db.Tenants.Where(t => t.Id == id).Select(ProyeccionDeTenant).FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el tenant {id}.");

    public async Task<TenantListado> ActualizarTenantAsync(
        int id, TenantEdicion datos, CancellationToken ct = default)
    {
        var tenant = await BuscarTenantAsync(id, ct);

        tenant.Nombre = Normalizar(datos.Nombre, "nombre_tenant", "nombre del tenant", 150);
        tenant.UpdatedAt = reloj.Ahora;

        await db.SaveChangesAsync(ct);
        return await ProyectarTenantAsync(tenant.Id, ct);
    }

    public Task<TenantListado> SuspenderTenantAsync(int id, CancellationToken ct = default) =>
        CambiarEstadoTenantAsync(id, EstadoTenant.Suspendido, ct);

    public Task<TenantListado> ReactivarTenantAsync(int id, CancellationToken ct = default) =>
        CambiarEstadoTenantAsync(id, EstadoTenant.Activo, ct);

    /// <summary>Idempotente a propósito: un doble clic en "suspender" sobre un tenant ya
    /// suspendido no debería ser un error, el estado resultante es el que pedía el operador
    /// de todos modos. <see cref="EstadoTenant.Baja"/> no lo toca ninguna de las dos acciones
    /// — alternar entre Activo/Suspendido no reactiva un tenant dado de baja por error.</summary>
    private async Task<TenantListado> CambiarEstadoTenantAsync(
        int id, EstadoTenant estado, CancellationToken ct)
    {
        var tenant = await BuscarTenantAsync(id, ct);

        if (tenant.Estado == EstadoTenant.Baja)
        {
            throw new ErrorDominio(
                "tenant_dado_de_baja",
                "Un tenant dado de baja no se puede suspender ni reactivar.", 409);
        }

        if (tenant.Estado != estado)
        {
            tenant.Estado = estado;
            tenant.UpdatedAt = reloj.Ahora;
            await db.SaveChangesAsync(ct);
        }

        return await ProyectarTenantAsync(tenant.Id, ct);
    }

    /// <summary>Reproyecta después de escribir: los contadores no viven en la entidad, así que el
    /// único lugar donde están es la consulta. Es una ida extra sobre una acción de plataforma
    /// puntual — el presupuesto de "una sola consulta" es el de los LISTADOS, que son los que
    /// escalan con la cantidad de filas. Si entre la escritura y esta relectura la fila quedó
    /// invisible, corresponde el mismo 404 de dominio que dan las lecturas, no un 500.</summary>
    private async Task<TenantListado> ProyectarTenantAsync(int id, CancellationToken ct) =>
        await db.Tenants.Where(t => t.Id == id).Select(ProyeccionDeTenant).FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el tenant {id}.");

    private async Task<Tenant> BuscarTenantAsync(int id, CancellationToken ct) =>
        await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el tenant {id}.");

    // --- Empresas ---

    /// <summary>El nombre del tenant se proyecta como subconsulta escalar correlacionada porque
    /// <see cref="Empresa"/> no tiene propiedad de navegación al tenant (design D13, hecho 1): no
    /// hay nada a lo que hacerle punto. De paso evita el INNER JOIN que borraría la fila cuando el
    /// tenant está dado de baja — ahí el nombre queda en <c>null</c> y la empresa se sigue
    /// listando como anomalía en vez de desaparecer.</summary>
    private Expression<Func<Empresa, EmpresaListado>> ProyeccionDeEmpresa => e => new EmpresaListado(
        e.Id,
        e.IdTenant,
        e.RazonSocial,
        e.NombreFantasia,
        e.Cuit,
        db.Tenants.Where(t => t.Id == e.IdTenant).Select(t => t.Nombre).FirstOrDefault());

    public async Task<IReadOnlyList<EmpresaListado>> ListarEmpresasAsync(CancellationToken ct = default) =>
        await db.Empresas
            .OrderBy(e => e.RazonSocial)
            .Select(ProyeccionDeEmpresa)
            .ToListAsync(ct);

    /// <summary>Proyecta y recién después valida el alcance, mismo orden que
    /// <see cref="BuscarEmpresaAsync"/>: primero 404 si no existe (o si el filtro de tenant la
    /// deja invisible), después la capa explícita de dominio (ADR-8).</summary>
    public async Task<EmpresaListado> ObtenerEmpresaAsync(int id, CancellationToken ct = default)
    {
        var empresa = await db.Empresas
            .Where(e => e.Id == id)
            .Select(ProyeccionDeEmpresa)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {id}.");

        PoliticaDeRoles.ValidarAlcanceDeTenant(Actor, empresa.IdTenant);

        return empresa;
    }

    public async Task<EmpresaListado> ActualizarEmpresaAsync(
        int id, EmpresaEdicion datos, CancellationToken ct = default)
    {
        var empresa = await BuscarEmpresaAsync(id, ct);

        empresa.RazonSocial = Normalizar(datos.RazonSocial, "razon_social", "razón social", 150);
        empresa.NombreFantasia = NormalizarOpcional(datos.NombreFantasia, "nombre_fantasia", "nombre de fantasía", 150);
        empresa.Cuit = NormalizarOpcional(datos.Cuit, "cuit", "CUIT", 13);
        empresa.UpdatedAt = reloj.Ahora;

        await db.SaveChangesAsync(ct);

        // Misma forma que ObtenerEmpresaAsync: si la fila quedó invisible entre la escritura y la
        // relectura, es un 404 de dominio, no un 500.
        return await db.Empresas
            .Where(e => e.Id == empresa.Id)
            .Select(ProyeccionDeEmpresa)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {id}.");
    }

    private async Task<Empresa> BuscarEmpresaAsync(int id, CancellationToken ct)
    {
        var empresa = await db.Empresas.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe la empresa {id}.");

        PoliticaDeRoles.ValidarAlcanceDeTenant(Actor, empresa.IdTenant);

        return empresa;
    }

    // --- Puntos de venta ---

    /// <summary>Dos subconsultas escalares correlacionadas, por el mismo motivo que en
    /// <see cref="ProyeccionDeEmpresa"/>: <see cref="PuntoVenta"/> tampoco tiene navegaciones
    /// (design D13, hecho 1).</summary>
    private Expression<Func<PuntoVenta, PuntoVentaListado>> ProyeccionDePuntoVenta => p => new PuntoVentaListado(
        p.Id,
        p.IdTenant,
        p.IdEmpresa,
        p.Nombre,
        p.Domicilio,
        p.Horario,
        p.Whatsapp,
        p.Instagram,
        p.Facebook,
        p.Web,
        db.Tenants.Where(t => t.Id == p.IdTenant).Select(t => t.Nombre).FirstOrDefault(),
        db.Empresas.Where(e => e.Id == p.IdEmpresa).Select(e => e.RazonSocial).FirstOrDefault());

    public async Task<IReadOnlyList<PuntoVentaListado>> ListarPuntosVentaAsync(CancellationToken ct = default) =>
        await db.PuntosVenta
            .OrderBy(p => p.Nombre)
            .Select(ProyeccionDePuntoVenta)
            .ToListAsync(ct);

    public async Task<PuntoVentaListado> ObtenerPuntoVentaAsync(int id, CancellationToken ct = default)
    {
        var puntoVenta = await db.PuntosVenta
            .Where(p => p.Id == id)
            .Select(ProyeccionDePuntoVenta)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {id}.");

        PoliticaDeRoles.ValidarAlcanceDeTenant(Actor, puntoVenta.IdTenant);

        return puntoVenta;
    }

    public async Task<PuntoVentaListado> ActualizarPuntoVentaAsync(
        int id, PuntoVentaEdicion datos, CancellationToken ct = default)
    {
        var puntoVenta = await BuscarPuntoVentaAsync(id, ct);

        puntoVenta.Nombre = Normalizar(datos.Nombre, "nombre_punto_venta", "nombre del punto de venta", 150);
        puntoVenta.Domicilio = NormalizarOpcional(datos.Domicilio, "domicilio", "domicilio", 255);
        puntoVenta.Horario = NormalizarOpcional(datos.Horario, "horario", "horario", 255);
        puntoVenta.Whatsapp = NormalizarOpcional(datos.Whatsapp, "whatsapp", "WhatsApp", 30);
        puntoVenta.Instagram = NormalizarOpcional(datos.Instagram, "instagram", "Instagram", 150);
        puntoVenta.Facebook = NormalizarOpcional(datos.Facebook, "facebook", "Facebook", 150);
        puntoVenta.Web = NormalizarOpcional(datos.Web, "sitio_web", "sitio web", 255);
        puntoVenta.UpdatedAt = reloj.Ahora;

        await db.SaveChangesAsync(ct);

        // Misma forma que ObtenerPuntoVentaAsync: 404 de dominio, nunca un 500, si la fila quedó
        // invisible entre la escritura y la relectura.
        return await db.PuntosVenta
            .Where(p => p.Id == puntoVenta.Id)
            .Select(ProyeccionDePuntoVenta)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {id}.");
    }

    private async Task<PuntoVenta> BuscarPuntoVentaAsync(int id, CancellationToken ct)
    {
        var puntoVenta = await db.PuntosVenta.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {id}.");

        PoliticaDeRoles.ValidarAlcanceDeTenant(Actor, puntoVenta.IdTenant);

        return puntoVenta;
    }

    // --- Común ---

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

    private static string? NormalizarOpcional(string? valor, string codigo, string campo, int largoMaximo)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return null;
        }

        var limpio = valor.Trim();

        if (limpio.Length > largoMaximo)
        {
            throw new ErrorDominio(
                $"{codigo}_muy_largo", $"El campo {campo} no puede superar los {largoMaximo} caracteres.", 400);
        }

        return limpio;
    }
}
