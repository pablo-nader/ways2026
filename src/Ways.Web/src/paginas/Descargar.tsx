import { Link } from 'react-router'
import { urlDescargaPos } from '../config/descargaPos'

const PROBLEMAS_FRECUENTES = [
  {
    titulo: 'No imprime',
    detalle:
      'Revisá que la impresora esté encendida y con papel, y que en Configuración esté seleccionada la impresora correcta. Si un ticket puntual falló, usá "Reimprimir".',
  },
  {
    titulo: 'Pide vincular el equipo de nuevo',
    detalle:
      'El dispositivo fue revocado, o se dio de baja el punto de venta al que estaba vinculado.',
  },
  {
    titulo: 'Pide iniciar sesión',
    detalle: 'La sesión se cerró, o el usuario fue bloqueado.',
  },
]

/** Página pública de descarga del POS de escritorio (stage-desktop-pos): vive fuera de
 * `RutaProtegida`, igual que `/login` — un cliente sin sesión tiene que poder llegar acá para
 * instalar la app antes de vincular el primer equipo. */
export function Descargar() {
  const url = urlDescargaPos()

  return (
    <div className="d-flex justify-content-center min-vh-100 py-5 bg-body-tertiary">
      <div className="container" style={{ maxWidth: '720px' }}>
        <div className="bg-white rounded-0 shadow-sm p-4 p-md-5">
          <h1 className="text-center ways-brand mb-1">Ways POS para Windows</h1>
          <p className="text-muted text-center mb-4">
            La aplicación de escritorio para vender e imprimir tickets directo desde la caja.
          </p>

          <div className="text-center mb-4">
            <a href={url} className="btn btn-lg btn-success rounded-0">
              Descargar para Windows
            </a>
          </div>

          <hr />

          <h2 className="h5 mt-4">Requisitos</h2>
          <ul>
            <li>Windows 10/11 de 64 bits.</li>
            <li>Conexión a internet.</li>
            <li>Impresora térmica de 80 mm (opcional).</li>
          </ul>

          <h2 className="h5 mt-4">Instalación</h2>
          <ol>
            <li>Ejecutá el instalador descargado (WaysPOS-setup.exe).</li>
            <li>
              Si Windows SmartScreen muestra "Windows protegió su PC", hacé clic en
              "Más información" y luego en "Ejecutar de todas formas" (el instalador todavía no
              está firmado digitalmente).
            </li>
            <li>La instalación es por usuario y crea un acceso directo "Ways POS".</li>
          </ol>

          <h2 className="h5 mt-4">Primera configuración del equipo</h2>
          <ul>
            <li>
              Al abrir la app por primera vez, completá la URL del servidor (viene prellenada con{' '}
              <code>https://aipos.site</code>).
            </li>
            <li>
              Elegí la impresora térmica de la lista. Las impresoras virtuales (PDF, XPS, OneNote,
              Fax) no se pueden usar para tickets.
            </li>
            <li>Usá "Imprimir prueba" para confirmar que imprime bien.</li>
            <li>Guardá los cambios.</li>
          </ul>

          <h2 className="h5 mt-4">Vincular el equipo</h2>
          <p className="text-muted mb-2">Se hace una sola vez, y lo hace un administrador.</p>
          <ul>
            <li>Iniciá sesión con el mail y la contraseña de un administrador.</li>
            <li>Elegí el punto de venta al que pertenece esta caja.</li>
            <li>Poné un nombre al equipo (por ejemplo, "Caja 1").</li>
            <li>
              Mientras el equipo esté vinculado, ese punto de venta no se puede dar de baja, aunque
              el dispositivo haya sido revocado.
            </li>
          </ul>

          <h2 className="h5 mt-4">Uso diario del cajero</h2>
          <ul>
            <li>Cada cajero inicia sesión con su usuario y contraseña.</li>
            <li>La sesión queda abierta aunque se cierre la app, hasta "Cerrar sesión".</li>
            <li>
              Vender: el ticket se imprime solo; si la impresión falla aparece un aviso con la
              opción "Reimprimir".
            </li>
            <li>
              Cerrar caja: se cuenta el efectivo y los demás medios de pago, se confirma, y se
              imprime el reporte Z.
            </li>
          </ul>

          <h2 className="h5 mt-4">Cambiar servidor o impresora</h2>
          <p>
            Se accede a la pantalla de configuración con <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+
            <kbd>F10</kbd>, o con el botón "Configuración".
          </p>

          <h2 className="h5 mt-4">Revocar un equipo</h2>
          <ul>
            <li>
              Un administrador lo revoca desde Ways, en Administración → Organización →{' '}
              <Link to="/organizacion/equipos-pos">Equipos POS</Link>, con el botón "Revocar".
            </li>
            <li>
              El equipo deja de poder iniciar sesión y las sesiones abiertas en él se cortan en su
              próxima operación.
            </li>
            <li>
              Un equipo revocado sigue bloqueando la baja de su punto de venta, del tenant y del
              usuario que lo vinculó.
            </li>
          </ul>

          <h2 className="h5 mt-4">Problemas frecuentes</h2>
          <dl className="mb-0">
            {PROBLEMAS_FRECUENTES.map((problema) => (
              <div key={problema.titulo} className="mb-3">
                <dt>{problema.titulo}</dt>
                <dd className="text-muted mb-0">{problema.detalle}</dd>
              </div>
            ))}
          </dl>

          <div className="text-center mt-4">
            <Link to="/login">Volver a iniciar sesión</Link>
          </div>
        </div>
      </div>
    </div>
  )
}
