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
        var apellido = NormalizarOpcional(datos.Apellido, 150);
        var razonSocial = NormalizarOpcional(datos.RazonSocial, 150);
        var numeroDocumento = NormalizarOpcional(datos.NumeroDocumento, 30);
        var domicilio = NormalizarOpcional(datos.Domicilio, 255);
        var telefono = NormalizarOpcional(datos.Telefono, 50);
        var celular = NormalizarOpcional(datos.Celular, 50);
        var email = NormalizarOpcional(datos.Email, 255);
        var observaciones = NormalizarOpcional(datos.Observaciones, null);

        ExigirIdRequerido(datos.IdCondicionFiscal, "id_condicion_fiscal");
        ExigirIdRequerido(datos.IdListaPrecio, "id_lista_precio");
        await ExigirCondicionFiscalValidaAsync(datos.IdCondicionFiscal, ct);
        await ExigirListaPrecioValidaAsync(datos.IdListaPrecio, ct);

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
        var apellido = NormalizarOpcional(datos.Apellido, 150);
        var razonSocial = NormalizarOpcional(datos.RazonSocial, 150);
        var numeroDocumento = NormalizarOpcional(datos.NumeroDocumento, 30);
        var domicilio = NormalizarOpcional(datos.Domicilio, 255);
        var telefono = NormalizarOpcional(datos.Telefono, 50);
        var celular = NormalizarOpcional(datos.Celular, 50);
        var email = NormalizarOpcional(datos.Email, 255);
        var observaciones = NormalizarOpcional(datos.Observaciones, null);

        ExigirIdRequerido(datos.IdCondicionFiscal, "id_condicion_fiscal");
        ExigirIdRequerido(datos.IdListaPrecio, "id_lista_precio");
        await ExigirCondicionFiscalValidaAsync(datos.IdCondicionFiscal, ct);
        await ExigirListaPrecioValidaAsync(datos.IdListaPrecio, ct);

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

    private static string? NormalizarOpcional(string? valor, int? largoMaximo)
    {
        var limpio = valor?.Trim();

        if (string.IsNullOrEmpty(limpio))
        {
            return null;
        }

        if (largoMaximo is { } maximo && limpio.Length > maximo)
        {
            throw new ErrorDominio(
                "campo_muy_largo", $"El valor no puede superar los {maximo} caracteres.", 400);
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
