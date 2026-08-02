using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Usuarios;
using Ways.Domain.Clientes;
using Ways.Domain.Common;

namespace Ways.Application.Clientes;

/// <summary>
/// ABM de clientes (design decision 1: entidad/servicio dedicados, no
/// <c>ServicioDeCatalogo&lt;T,..&gt;</c>). Autorización: <c>Politicas.GestionDeCatalogo</c>
/// aplicada en la capa de API — sin chequeo de rol acá adentro, mismo criterio que
/// <see cref="Catalogos.ServicioDeCatalogo{T,TListado,TAlta}"/> (una sola policy, admin-only,
/// nada que diferenciar entre root/admin como sí hace <see cref="ServicioDeUsuarios"/>).
///
/// El guard del Consumidor Final (<see cref="ReglaDeClientes.ValidarNoConsumidorFinal"/>) se
/// llama con el <c>Numero</c> ACTUAL de la fila antes de aplicar cualquier cambio de
/// edición/baja — como <see cref="Numero"/> nunca es un campo editable de
/// <see cref="AltaCliente"/>/<see cref="EdicionCliente"/> (lo asigna
/// <see cref="AsignadorDeNumeroCliente"/>, nunca el cliente HTTP), no existe ningún camino de
/// este servicio para renumerar una fila antes de borrarla: el bypass de dos pasos que
/// documenta <see cref="ReglaDeClientes"/> queda cerrado acá, no solo en la constraint de
/// esquema.
/// </summary>
public class ServicioDeClientes(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    public async Task<PaginaDe<ClienteListado>> ListarAsync(
        string? busqueda = null,
        bool incluirEliminados = false,
        int pagina = 1,
        int tamanio = 25,
        CancellationToken ct = default)
    {
        pagina = Math.Max(pagina, 1);
        tamanio = Math.Clamp(tamanio, 1, 200);

        var query = db.Clientes.AsQueryable();

        if (incluirEliminados)
        {
            // Solo la baja lógica: ignorar todos los filtros de un tirón también saltearía
            // el de tenant (ADR-6) — mismo criterio que ServicioDeUsuarios.ListarAsync.
            query = query.IgnoreQueryFilters(["BajaLogica"]);
        }

        if (!string.IsNullOrWhiteSpace(busqueda))
        {
            // Columnas citext: el Contains ya es case-insensitive sin ILIKE explícito.
            var termino = busqueda.Trim();
            query = query.Where(c =>
                c.Nombre.Contains(termino) ||
                (c.Apellido != null && c.Apellido.Contains(termino)) ||
                (c.RazonSocial != null && c.RazonSocial.Contains(termino)) ||
                (c.NumeroDocumento != null && c.NumeroDocumento.Contains(termino)));
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(c => c.Numero)
            .Skip((pagina - 1) * tamanio)
            .Take(tamanio)
            .Select(c => new ClienteListado(
                c.Id, c.Numero, c.Nombre, c.Apellido, c.RazonSocial, c.TipoDocumento,
                c.NumeroDocumento, c.IdCondicionFiscal, c.Nacimiento, c.Domicilio, c.Telefono,
                c.Celular, c.Email, c.Observaciones, c.IdListaPrecio, c.LimiteCredito,
                c.CreditoIlimitado, c.Saldo, c.Activo, c.IdEmpresa, c.EsConsumidorFinal))
            .ToListAsync(ct);

        return new PaginaDe<ClienteListado>(items, total, pagina, tamanio);
    }

    public async Task<ClienteListado> ObtenerAsync(int id, CancellationToken ct = default)
    {
        var cliente = await BuscarAsync(id, ct);
        return Proyectar(cliente);
    }

    /// <summary>Asigna <c>numero</c> de forma atómica (design decisions 2/3) dentro de la
    /// misma transacción que el INSERT: si el alta falla después de tomar el número (p.ej. una
    /// FK que se termina violando), el rollback también deshace el avance del contador — el
    /// mismo "gaps solo en rollback" que documenta <c>AsignadorDeNumeroCliente</c>, no un hueco
    /// garantizado en cada error. Mismo wrapper de <c>CreateExecutionStrategy</c> que
    /// <see cref="Organizacion.ServicioDeAprovisionamiento.CrearTenantAsync"/> — EnableRetryOnFailure
    /// exige que la transacción se abra adentro de <c>ExecuteAsync</c>.</summary>
    public async Task<ClienteListado> CrearAsync(AltaCliente datos, CancellationToken ct = default)
    {
        var nombre = NormalizarRequerido(datos.Nombre, "nombre", 150);
        var apellido = NormalizarOpcional(datos.Apellido, "apellido", 150);
        var razonSocial = NormalizarOpcional(datos.RazonSocial, "razon_social", 150);
        var numeroDocumento = NormalizarOpcional(datos.NumeroDocumento, "numero_documento", 30);
        var domicilio = NormalizarOpcional(datos.Domicilio, "domicilio", 255);
        var telefono = NormalizarOpcional(datos.Telefono, "telefono", 50);
        var celular = NormalizarOpcional(datos.Celular, "celular", 50);
        var email = NormalizarOpcional(datos.Email, "email", 255);
        var observaciones = NormalizarOpcional(datos.Observaciones, "observaciones", null);

        ExigirIdRequerido(datos.IdCondicionFiscal, "id_condicion_fiscal");
        ExigirIdRequerido(datos.IdListaPrecio, "id_lista_precio");
        ExigirLimiteCreditoValido(datos.LimiteCredito);
        await ExigirCondicionFiscalValidaAsync(datos.IdCondicionFiscal, ct);
        await ExigirListaPrecioValidaAsync(datos.IdListaPrecio, ct);
        await ExigirEmpresaValidaAsync(datos.IdEmpresa, ct);

        var idTenant = ExigirTenantDeLaSesion();

        var estrategia = db.Database.CreateExecutionStrategy();

        return await estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await db.Database.BeginTransactionAsync(ct);

            await AsignadorDeNumeroCliente.AsegurarContadorAsync(db, idTenant, ct);
            var numero = await AsignadorDeNumeroCliente.AsignarSiguienteAsync(db, idTenant, ct);

            var ahora = reloj.Ahora;
            var cliente = new Cliente
            {
                Numero = numero,
                Nombre = nombre,
                Apellido = apellido,
                RazonSocial = razonSocial,
                TipoDocumento = datos.TipoDocumento,
                NumeroDocumento = numeroDocumento,
                IdCondicionFiscal = datos.IdCondicionFiscal,
                Nacimiento = datos.Nacimiento,
                Domicilio = domicilio,
                Telefono = telefono,
                Celular = celular,
                Email = email,
                Observaciones = observaciones,
                IdListaPrecio = datos.IdListaPrecio,
                LimiteCredito = datos.LimiteCredito,
                CreditoIlimitado = datos.CreditoIlimitado,
                IdEmpresa = datos.IdEmpresa,
                Activo = datos.Activo,
                CreatedAt = ahora,
                UpdatedAt = ahora
            };

            db.Clientes.Add(cliente);
            await db.SaveChangesAsync(ct);

            await transaccion.CommitAsync(ct);

            return Proyectar(cliente);
        });
    }

    public async Task<ClienteListado> ActualizarAsync(int id, EdicionCliente datos, CancellationToken ct = default)
    {
        var cliente = await BuscarAsync(id, ct);

        // Guard del Consumidor Final ANTES de tocar cualquier campo (spec: Consumidor Final
        // Protected Row) — usa el Numero actual de la fila, nunca uno que venga del request
        // (EdicionCliente no tiene Numero).
        ReglaDeClientes.ValidarNoConsumidorFinal(cliente.Numero);

        var nombre = NormalizarRequerido(datos.Nombre, "nombre", 150);
        var apellido = NormalizarOpcional(datos.Apellido, "apellido", 150);
        var razonSocial = NormalizarOpcional(datos.RazonSocial, "razon_social", 150);
        var numeroDocumento = NormalizarOpcional(datos.NumeroDocumento, "numero_documento", 30);
        var domicilio = NormalizarOpcional(datos.Domicilio, "domicilio", 255);
        var telefono = NormalizarOpcional(datos.Telefono, "telefono", 50);
        var celular = NormalizarOpcional(datos.Celular, "celular", 50);
        var email = NormalizarOpcional(datos.Email, "email", 255);
        var observaciones = NormalizarOpcional(datos.Observaciones, "observaciones", null);

        ExigirIdRequerido(datos.IdCondicionFiscal, "id_condicion_fiscal");
        ExigirIdRequerido(datos.IdListaPrecio, "id_lista_precio");
        ExigirLimiteCreditoValido(datos.LimiteCredito);
        await ExigirCondicionFiscalValidaAsync(datos.IdCondicionFiscal, ct);
        await ExigirListaPrecioValidaAsync(datos.IdListaPrecio, ct);
        await ExigirEmpresaValidaAsync(datos.IdEmpresa, ct);

        cliente.Nombre = nombre;
        cliente.Apellido = apellido;
        cliente.RazonSocial = razonSocial;
        cliente.TipoDocumento = datos.TipoDocumento;
        cliente.NumeroDocumento = numeroDocumento;
        cliente.IdCondicionFiscal = datos.IdCondicionFiscal;
        cliente.Nacimiento = datos.Nacimiento;
        cliente.Domicilio = domicilio;
        cliente.Telefono = telefono;
        cliente.Celular = celular;
        cliente.Email = email;
        cliente.Observaciones = observaciones;
        cliente.IdListaPrecio = datos.IdListaPrecio;
        cliente.LimiteCredito = datos.LimiteCredito;
        cliente.CreditoIlimitado = datos.CreditoIlimitado;
        cliente.IdEmpresa = datos.IdEmpresa;
        cliente.Activo = datos.Activo;
        cliente.UpdatedAt = reloj.Ahora;

        await db.SaveChangesAsync(ct);

        return Proyectar(cliente);
    }

    /// <summary>Baja lógica: escribe <c>deleted_at</c>, no borra la fila.</summary>
    public async Task EliminarAsync(int id, CancellationToken ct = default)
    {
        var cliente = await BuscarAsync(id, ct);

        // Mismo guard que ActualizarAsync, mismo motivo (spec: Consumidor Final Protected
        // Row) — el backstop de esquema (ck_clientes_cf_protegido) es la segunda capa, esta
        // es la primera.
        ReglaDeClientes.ValidarNoConsumidorFinal(cliente.Numero);

        var ahora = reloj.Ahora;
        cliente.DeletedAt = ahora;
        cliente.UpdatedAt = ahora;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Referencia mínima para el selector de lista de precios del formulario — no un
    /// ABM de <c>listas_precio</c> (ver <see cref="ListaPrecioAsignable"/>).</summary>
    public async Task<IReadOnlyList<ListaPrecioAsignable>> ListasDePrecioAsignablesAsync(CancellationToken ct = default) =>
        await db.ListasPrecio
            .Where(l => l.Activo)
            .OrderByDescending(l => l.EsDefault).ThenBy(l => l.Nombre)
            .Select(l => new ListaPrecioAsignable(l.Id, l.Nombre, l.EsDefault))
            .ToListAsync(ct);

    private async Task<Cliente> BuscarAsync(int id, CancellationToken ct) =>
        await db.Clientes.FirstOrDefaultAsync(c => c.Id == id, ct)
            // El filtro de EF (+ RLS por debajo) ya deja invisible la fila de otro tenant —
            // esto solo cubre "no existe en absoluto" (ADR-8: mismo 404 en los dos casos).
            ?? throw ErrorDominio.NoEncontrado($"No existe el cliente {id}.");

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // GestionDeCatalogo (capa de API) ya exige admin de tenant — un actor de
            // plataforma nunca llega hasta acá. Defensa en profundidad, no un camino
            // alcanzable en operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDeClientes requiere un actor de tenant; GestionDeCatalogo es admin-only.");

    private static void ExigirIdRequerido(int valor, string campo)
    {
        if (valor <= 0)
        {
            throw new ErrorDominio($"{campo}_requerido", $"El campo {campo} es obligatorio.", 400);
        }
    }

    /// <summary>Judgment-day ronda 1 (item 2): validación de negocio, solo a nivel de
    /// servicio — sin CHECK de esquema. Una opción sería un <c>CHECK (limite_credito &gt;=
    /// 0)</c> en la tabla <c>clientes</c>, pero eso exigiría una migración nueva y esta ronda
    /// está bajo el gate "NO schema changes" (la migración de la Slice 2 ya está mergeada);
    /// queda como mejora futura si se decide blindar también contra un bypass directo por
    /// SQL, igual que <c>ck_clientes_cf_protegido</c>.
    /// Judgment-day ronda 1 de Slice 3: también rechaza valores que desbordan la columna
    /// <c>numeric(14,2)</c> (mayores o iguales a 1_000_000_000_000) — mismo defecto de clase
    /// que <c>ServicioDeProveedores.ExigirMargenValido</c>.</summary>
    private static void ExigirLimiteCreditoValido(decimal limiteCredito)
    {
        if (limiteCredito < 0 || limiteCredito >= 1_000_000_000_000m)
        {
            throw new ErrorDominio(
                "limite_credito_invalido",
                "El límite de crédito debe estar entre 0 y 999999999999.99.",
                400);
        }
    }

    /// <summary>db-error-backstops: pre-chequeo de existencia antes del INSERT — el backstop
    /// real sigue siendo la FK (23503 → 400 <c>referencia_invalida</c>, ya genérico desde la
    /// Slice 1), esto solo adelanta el mismo código/estado sin esperar la carrera con
    /// Postgres. <c>condiciones_fiscales</c> es <c>[global]</c> (no <see cref="Common.EntidadTenant"/>):
    /// sin alcance de tenant que filtrar.</summary>
    private async Task ExigirCondicionFiscalValidaAsync(int id, CancellationToken ct)
    {
        if (!await db.CondicionesFiscales.AnyAsync(c => c.Id == id, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la condición fiscal {id}.", 400);
        }
    }

    /// <summary>Mismo criterio que <see cref="ExigirCondicionFiscalValidaAsync"/>, para
    /// <c>fk_clientes_lista_precio</c> (compuesta, judgment-day ronda 1) — el filtro de EF ya
    /// deja afuera una lista de OTRO tenant, así que este chequeo también cubre "es de otro
    /// tenant" con el mismo 400, sin esperar la FK compuesta.</summary>
    private async Task ExigirListaPrecioValidaAsync(int id, CancellationToken ct)
    {
        if (!await db.ListasPrecio.AnyAsync(l => l.Id == id, ct))
        {
            throw new ErrorDominio("referencia_invalida", $"No existe la lista de precios {id}.", 400);
        }
    }

    /// <summary>Judgment-day ronda 1 (item 8): mismo criterio que
    /// <see cref="ExigirCondicionFiscalValidaAsync"/>/<see cref="ExigirListaPrecioValidaAsync"/>,
    /// para <c>fk_clientes_empresa</c> — pre-chequeo de existencia tenant-scoped antes del
    /// INSERT/UPDATE, sin reemplazar el backstop de la FK compuesta (23503 →
    /// <c>referencia_invalida</c>, sin cambios). <c>IdEmpresa</c> es nullable (<c>NULL</c> ⇒
    /// compartido por todas las empresas del tenant, ADR-10): sin chequeo cuando se omite.</summary>
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

    /// <summary>Judgment-day ronda 1 (item 6): <paramref name="campo"/> identifica el código
    /// de error específico, igual que <see cref="NormalizarRequerido"/> — antes tiraban todos
    /// el mismo <c>campo_muy_largo</c> genérico, sin decir cuál de los ocho campos opcionales
    /// era el culpable.</summary>
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

    private static ClienteListado Proyectar(Cliente c) => new(
        c.Id, c.Numero, c.Nombre, c.Apellido, c.RazonSocial, c.TipoDocumento, c.NumeroDocumento,
        c.IdCondicionFiscal, c.Nacimiento, c.Domicilio, c.Telefono, c.Celular, c.Email,
        c.Observaciones, c.IdListaPrecio, c.LimiteCredito, c.CreditoIlimitado, c.Saldo, c.Activo,
        c.IdEmpresa, c.EsConsumidorFinal);
}
