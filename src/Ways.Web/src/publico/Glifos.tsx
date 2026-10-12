import type { ReactNode } from 'react'

type PropsDeGlifo = { className?: string }

function Glifo({ className, children }: PropsDeGlifo & { children: ReactNode }) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      width="20"
      height="20"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      {children}
    </svg>
  )
}

/** Marca de Ways: tres chevrones, en el color del texto o del acento que herede. */
export function Chevrones({ className }: PropsDeGlifo) {
  return (
    <svg
      className={className}
      viewBox="0 0 96 70"
      fill="none"
      stroke="currentColor"
      strokeWidth="12"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <polyline points="10,8 36,35 10,62" />
      <polyline points="34,8 60,35 34,62" />
      <polyline points="58,8 84,35 58,62" />
    </svg>
  )
}

export function GlifoWhatsapp({ className }: PropsDeGlifo) {
  return (
    <Glifo className={className}>
      <path d="M4 20l1.3-4.2A8 8 0 1 1 8.2 18.8z" />
      <path d="M9 9.5c0 3 2.5 5.5 5.5 5.5l1.2-1.4-2-1-.9.6c-.8-.4-1.5-1.1-1.9-1.9l.6-.9-1-2z" />
    </Glifo>
  )
}

export function GlifoInstagram({ className }: PropsDeGlifo) {
  return (
    <Glifo className={className}>
      <rect x="4" y="4" width="16" height="16" rx="4.5" />
      <circle cx="12" cy="12" r="3.6" />
      <circle cx="16.8" cy="7.2" r="0.6" />
    </Glifo>
  )
}

export function GlifoFacebook({ className }: PropsDeGlifo) {
  return (
    <Glifo className={className}>
      <path d="M14 21v-8h2.6l.4-3H14V8.2c0-.9.3-1.5 1.6-1.5H17V4.1C16.7 4 15.9 4 15 4c-2.2 0-3.7 1.3-3.7 3.8V10H9v3h2.3v8z" />
    </Glifo>
  )
}

export function GlifoWeb({ className }: PropsDeGlifo) {
  return (
    <Glifo className={className}>
      <circle cx="12" cy="12" r="8.5" />
      <path d="M3.5 12h17M12 3.5c2.5 2.3 3.5 5.2 3.5 8.5s-1 6.2-3.5 8.5c-2.5-2.3-3.5-5.2-3.5-8.5s1-6.2 3.5-8.5z" />
    </Glifo>
  )
}
