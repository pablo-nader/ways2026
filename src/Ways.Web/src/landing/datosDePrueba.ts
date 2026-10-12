import type { SucursalPublica, VitrinaPublica } from '../api/tipos'

export const sucursalCompleta: SucursalPublica = {
  nombre: 'Casa Central',
  domicilio: 'Av. Siempreviva 742',
  horario: 'Lun a Vie 9 a 18',
  whatsapp: '+54 9 11 5555-1234',
  instagram: '@ferreteriasur',
  facebook: 'https://www.facebook.com/FerreteriaSur',
  web: 'www.ferreteriasur.com.ar',
}

export const sucursalSinContacto: SucursalPublica = {
  nombre: 'Depósito',
  domicilio: null,
  horario: null,
  whatsapp: null,
  instagram: null,
  facebook: null,
  web: null,
}

export function vitrina(cambios: Partial<VitrinaPublica> = {}): VitrinaPublica {
  return {
    nombre: 'Ferretería Sur',
    tagline: 'Todo para tu obra',
    descripcion: 'Atendemos desde 1980.\nEnvíos en el día.',
    colorAcento: '#2a9d8f',
    tema: 'oscuro',
    tieneLogo: false,
    logoVersion: null,
    sucursales: [sucursalCompleta, sucursalSinContacto],
    ...cambios,
  }
}
