export const HERMES_AUTH_CHANGED_EVENT = 'hermes:auth-changed'

export type HermesAuthState = 'signed-in' | 'signed-out'

export function announceHermesAuthChanged(state: HermesAuthState, target: EventTarget = window) {
  target.dispatchEvent(new CustomEvent<HermesAuthState>(HERMES_AUTH_CHANGED_EVENT, { detail: state }))
}

export function subscribeHermesAuthChanged(listener: (state: HermesAuthState) => void, target: EventTarget = window) {
  const receive = (event: Event) => listener((event as CustomEvent<HermesAuthState>).detail)
  target.addEventListener(HERMES_AUTH_CHANGED_EVENT, receive)
  return () => target.removeEventListener(HERMES_AUTH_CHANGED_EVENT, receive)
}
