using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Usuarios;
using Ways.Domain.Common;
using Ways.Domain.Proveedores;

namespace Ways.Application.Proveedores;

/// <summary>
/// ABM de proveedores (design decision 1: entidad/servicio dedicados, no
/// <c>ServicioDeCatalogo&lt;T,..&gt;</c> — dedupe por <see cref="Proveedor.Cuit"/> tenant-wide,
/// no por nombre/empresa-par). Autorización: <c>Politicas.GestionDeCatalogo</c> aplicada en la
/// capa de API — sin chequeo de rol acá adentro, mismo criterio que
/// <see cref="Clientes.ServicioDeClientes"/>/<see cref="Catalogos.ServicioDeCatalogo{T,TListado,TAlta}"/>.
///
/// A diferencia de <see cref="Clientes.ServicioDeClientes"/>, no hay contador atómico ni
/// transacción explícita que envolver: el alta es un INSERT incondicional + <c>SaveChangesAsync</c>
/// directo, mismo shape que <see cref="Catalogos.ServicioDeCatalogo{T,TListado,TAlta}.CrearAsync"/>
/// (pre-chequeo de disponibilidad + INSERT, sin <c>AsignadorDeNumeroCliente</c> de por medio) —
/// esto también significa que, a diferencia de <c>ServicioDeClientes</c>, el alta completa SÍ es
/// testeable con el proveedor InMemory (ver <c>ServicioDeProveedoresTests</c>).
/// </summary>
public class ServicioDeProveedores(IWaysDbContext db, IRelojDelSistema reloj)
{
    public async Task<PaginaDe<ProveedorListado>> ListarAsync(
        string? busqueda = null,
        bool incluirEliminados = false,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = db.Proveedores.AsQueryable();

        if (incluirEliminados)
        {
            // Solo la baja lógica: ignorar todos los filtros de un tirón también saltearía
            // el de tenant (ADR-6) — mismo criterio que ServicioDeClientes.ListarAsync.
            query = query.IgnoreQueryFilters(["BajaLogica"]);
        }

        if (!string.IsNullOrWhiteSpace(busqueda))
        {
            // Columnas citext: el Contains ya es case-insensitive sin ILIKE explícito. Cuit
            // no es citext (formateado, no texto buscado) pero Contains sigue funcionando
            // como comparación de texto normal.
            var termino = busqueda.Trim();
            query = query.Where(p =>
                p.RazonSocial.Contains(termino) ||
                (p.NombreFantasia != null && p.NombreFantasia.Contains(termino)) ||
                (p.Cuit != null && p.Cuit.Contains(termino)));
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(p => p.RazonSocial)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(p => Proyectar(p))
            .ToListAsync(ct);

        return new PaginaDe<ProveedorListado>(items, total, pagina, tamanio);
    }

    public async Task<ProveedorListado> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var proveedor = await BuscarAsync(id, ct);
        return Proyectar(proveedor);
    }

    public async Task<ProveedorListado> CrearAsync(AltaProveedor datos, CancellationToken ct = default)
    {
        var razonSocial = NormalizarRequerido(datos.RazonSocial, "razon_social", 150);
        var nombreFantasia = NormalizarOpcional(datos.NombreFantasia, "nombre_fantasia", 150);
        var cuit = NormalizarCuit(datos.Cuit);
        var domicilio = NormalizarOpcional(datos.Domicilio, "domicilio", 255);
        var telefono = NormalizarOpcional(datos.Telefono, "telefono", 50);
        var email = NormalizarOpcional(datos.Email, "email", 255);
        var vendedor = NormalizarOpcional(datos.Vendedor, "vendedor", 150);
        var celularVendedor = NormalizarOpcional(datos.CelularVendedor, "celular_vendedor", 50);
        var supervisor = NormalizarOpcional(datos.Supervisor, "supervisor", 150);
        var celularSupervisor = NormalizarOpcional(datos.CelularSupervisor, "celular_supervisor", 50);
        var observaciones = NormalizarOpcional(datos.Observaciones, "observaciones", null);

        ExigirIdRequerido(datos.IdCondicionFiscal, "id_condicion_fiscal");
        ExigirMargenValido(datos.Margen);
        await ExigirCondicionFiscalValidaAsync(datos.IdCondicionFiscal, ct);
        await ExigirEmpresaValidaAsync(datos.IdEmpresa, ct);

        // db-error-backstops: pre-chequeo best-effort — el backstop real sigue siendo
        // ux_proveedores_cuit (23505 -> 409 cuit_duplicado, ManejadorDeErrores). No reemplaza
        // la constraint: dos altas concurrentes con el mismo cuit pueden pasar las dos este
        // chequeo y competir recién en el SaveChangesAsync (spec: Concurrent creation race
        // yields exactly one winner).
        await ExigirCuitDisponibleAsync(cuit, excluirId: null, ct);

        var ahora = reloj.Ahora;
        var proveedor = new Proveedor
        {
            RazonSocial = razonSocial,
            NombreFantasia = nombreFantasia,
            Cuit = cuit,
            IdCondicionFiscal = datos.IdCondicionFiscal,
            Domicilio = domicilio,
            Telefono = telefono,
            Email = email,
            Vendedor = vendedor,
            CelularVendedor = celularVendedor,
            Supervisor = supervisor,
            CelularSupervisor = celularSupervisor,
            Margen = datos.Margen,
            Observaciones = observaciones,
            IdEmpresa = datos.IdEmpresa,
            Activo = datos.Activo,
            CreatedAt = ahora,
            UpdatedAt = ahora
        };

        db.Proveedores.Add(proveedor);
        await db.SaveChangesAsync(ct);

        return Proyectar(proveedor);
    }

    public async Task<ProveedorListado> ActualizarAsync(int id, EdicionProveedor datos, CancellationToken ct = default)
    {
        var proveedor = await BuscarAsync(id, ct);

        var razonSocial = NormalizarRequerido(datos.RazonSocial, "razon_social", 150);
        var nombreFantasia = NormalizarOpcional(datos.NombreFantasia, "nombre_fantasia", 150);
        var cuit = NormalizarCuit(datos.Cuit);
        var domicilio = NormalizarOpcional(datos.Domicilio, "domicilio", 255);
        var telefono = NormalizarOpcional(datos.Telefono, "telefono", 50);
        var email = NormalizarOpcional(datos.Email, "email", 255);
        var vendedor = NormalizarOpcional(datos.Vendedor, "vendedor", 150);
        var celularVendedor = NormalizarOpcional(datos.CelularVendedor, "celular_vendedor", 50);
        var supervisor = NormalizarOpcional(datos.Supervisor, "supervisor", 150);
        var celularSupervisor = NormalizarOpcional(datos.CelularSupervisor, "celular_supervisor", 50);
        var observaciones = NormalizarOpcional(datos.Observaciones, "observaciones", null);

        ExigirIdRequerido(datos.IdCondicionFiscal, "id_condicion_fiscal");
        ExigirMargenValido(datos.Margen);
        await ExigirCondicionFiscalValidaAsync(datos.IdCondicionFiscal, ct);
        await ExigirEmpresaValidaAsync(datos.IdEmpresa, ct);
        await ExigirCuitDisponibleAsync(cuit, excluirId: id, ct);

        proveedor.RazonSocial = razonSocial;
        proveedor.NombreFantasia = nombreFantasia;
        proveedor.Cuit = cuit;
        proveedor.IdCondicionFiscal = datos.IdCondicionFiscal;
        proveedor.Domicilio = domicilio;
        proveedor.Telefono = telefono;
        proveedor.Email = email;
        proveedor.Vendedor = vendedor;
        proveedor.CelularVendedor = celularVendedor;
        proveedor.Supervisor = supervisor;
        proveedor.CelularSupervisor = celularSupervisor;
        proveedor.Margen = datos.Margen;
        proveedor.Observaciones = observaciones;
        proveedor.IdEmpresa = datos.IdEmpresa;
        proveedor.Activo = datos.Activo;
        proveedor.UpdatedAt = reloj.Ahora;

        await db.SaveChangesAsync(ct);

        return Proyectar(proveedor);
    }

    /// <summary>Baja lógica: escribe <c>deleted_at</c>, no borra la fila. Sin guard de fila
    /// protegida (a diferencia de <c>ServicioDeClientes.EliminarAsync</c>) — proveedores no
    /// tiene un equivalente al Consumidor Final.</summary>
    public async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var proveedor = await BuscarAsync(id, ct);

        var ahora = reloj.Ahora;
        proveedor.DeletedAt = ahora;
        proveedor.UpdatedAt = ahora;

        await db.SaveChangesAsync(ct);
    }

    private async Task<Proveedor> BuscarAsync(int id, CancellationToken ct) =>
        await db.Proveedores.FirstOrDefaultAsync(p => p.Id == id, ct)
            // El filtro de EF (+ RLS por debajo) ya deja invisible la fila de otro tenant —
            // esto solo cubre "no existe en absoluto" (ADR-8: mismo 404 en los dos casos).
            ?? throw ErrorDominio.NoEncontrado($"No existe el proveedor {id}.");

    private static void ExigirIdRequerido(int valor, string campo)
    {
        if (valor <= 0)
        {
            throw new ErrorDominio($"{campo}_requerido", $"El campo {campo} es obligatorio.", 400);
        }
    }

    /// <summary>Mismo criterio que <c>ServicioDeClientes.ExigirLimiteCreditoValido</c>
    /// (judgment-day ronda 1 de Slice 2) aplicado proactivamente acá: sin CHECK de esquema
    /// (fuera del gate NO-schema-changes de esta slice), solo validación de servicio.</summary>
    private static void ExigirMargenValido(decimal? margen)
    {
        if (margen is { } valor && valor < 0)
        {
            throw new ErrorDominio("margen_invalido", "El margen no puede ser negativo.", 400);
        }
    }

    /// <summary>db-error-backstops: pre-chequeo de existencia antes del INSERT — el backstop
    /// real sigue siendo la FK (23503 -> 400 referencia_invalida, genérico desde la Slice 1).
    /// condiciones_fiscales es [global] (no EntidadTenant): sin alcance de tenant que
    /// filtrar.</summary>
    private async Task ExigirCondicionFiscalValidaAsync(int id, CancellationToken ct)
    {
        if (!await db.CondicionesFiscales.AnyAsync(c => c.Id == id, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la condición fiscal {id}.", 400);
        }
    }

    /// <summary>Mismo criterio que <see cref="ExigirCondicionFiscalValidaAsync"/>, para
    /// fk_proveedores_empresa (compuesta) — pre-chequeo tenant-scoped antes del
    /// INSERT/UPDATE, sin reemplazar el backstop de la FK. IdEmpresa es nullable (NULL =>
    /// compartido por todas las empresas del tenant, ADR-10): sin chequeo cuando se
    /// omite.</summary>
    private async Task ExigirEmpresaValidaAsync(int? id, CancellationToken ct)
    {
        if (id is null)
        {
            return;
        }

        if (!await db.Empresas.AnyAsync(e => e.Id == id, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la empresa {id}.", 400);
        }
    }

    /// <summary>Pre-chequeo tenant-scoped (spec: cuit Uniqueness Is Scoped Per Tenant) — el
    /// filtro de EF ya limita <c>db.Proveedores</c> al tenant actual, así que no hace falta el
    /// contexto plateado-keyed que exige el skill db-error-backstops para unicidades GLOBALES
    /// (p.ej. ux_usuarios_mail). Sin chequeo cuando <paramref name="cuit"/> es <c>null</c>: el
    /// índice parcial nunca compara NULLs entre sí (spec: NULL cuit never collides).</summary>
    private async Task ExigirCuitDisponibleAsync(string? cuit, int? excluirId, CancellationToken ct)
    {
        if (cuit is null)
        {
            return;
        }

        var tomado = await db.Proveedores.AnyAsync(p => p.Cuit == cuit && p.Id != excluirId, ct);

        if (tomado)
        {
            throw ErrorDominio.Conflicto("cuit_duplicado", $"Ya existe un proveedor con el CUIT {cuit} en este tenant.");
        }
    }

    private static string? NormalizarCuit(string? valor)
    {
        var limpio = valor?.Trim();

        if (string.IsNullOrEmpty(limpio))
        {
            return null;
        }

        if (limpio.Length > 13)
        {
            throw new ErrorDominio("cuit_muy_largo", "El campo cuit no puede superar los 13 caracteres.", 400);
        }

        return limpio;
    }

    private static string? NormalizarOpcional(string? valor, string campo, int? largoMaximo)
    {
        var limpio = valor?.Trim();

        if (string.IsNullOrEmpty(limpio))
        {
            return null;
        }

        if (largoMaximo is { } maximo && limpio.Length > maximo)
        {
            throw new ErrorDominio(
                $"{campo}_muy_largo", $"El campo {campo} no puede superar los {maximo} caracteres.", 400);
        }

        return limpio;
    }

    private static string NormalizarRequerido(string? valor, string campo, int largoMaximo)
    {
        var limpio = valor?.Trim() ?? string.Empty;

        if (limpio.Length == 0)
        {
            throw new ErrorDominio($"{campo}_requerido", $"El campo {campo} es obligatorio.", 400);
        }

        if (limpio.Length > largoMaximo)
        {
            throw new ErrorDominio(
                $"{campo}_muy_largo", $"El campo {campo} no puede superar los {largoMaximo} caracteres.", 400);
        }

        return limpio;
    }

    private static ProveedorListado Proyectar(Proveedor p) => new(
        p.Id, p.RazonSocial, p.NombreFantasia, p.Cuit, p.IdCondicionFiscal, p.Domicilio, p.Telefono,
        p.Email, p.Vendedor, p.CelularVendedor, p.Supervisor, p.CelularSupervisor, p.Margen,
        p.Observaciones, p.Activo, p.IdEmpresa);
}
