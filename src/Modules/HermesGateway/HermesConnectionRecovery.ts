export const HERMES_RECONNECT_DELAYS_MS = [400, 1_200, 2_500, 5_000, 10_000] as const

export type HermesSessionResumeResult = {
  session_id?: string
  stored_session_id?: string
}

export type HermesSessionResumeRequest = (
  method: string,
  params: Record<string, unknown>,
  timeoutMs?: number,
) => Promise<unknown>

export function reconnectDelayMs(attempt: number) {
  const safeAttempt = Number.isFinite(attempt) ? Math.max(0, Math.floor(attempt)) : 0
  return HERMES_RECONNECT_DELAYS_MS[Math.min(safeAttempt, HERMES_RECONNECT_DELAYS_MS.length - 1)]
}

export function shouldRecoverHermesSession(hadOpenConnection: boolean, gatewayIsOpen: boolean) {
  return hadOpenConnection && !gatewayIsOpen
}

export function canStartHermesChat(busy: boolean, gatewayIsOpen: boolean) {
  return !busy && gatewayIsOpen
}

export function shouldAcceptHermesSessionEvent(
  sessionOpening: boolean,
  activeSessionId: string | null,
  eventSessionId?: string,
) {
  if (sessionOpening) return false
  return !eventSessionId || eventSessionId === activeSessionId
}

export class HermesSessionOpenGeneration {
  private generation = 0

  begin() { return ++this.generation }
  invalidate() { ++this.generation }
  isCurrent(candidate: number) { return candidate === this.generation }
}

export function isHermesAuthenticationError(reason: unknown) {
  if (reason instanceof Error && reason.name === 'HermesAuthenticationError') return true
  const message = reason instanceof Error ? reason.message : String(reason ?? '')
  return /^(sign in to hermes (inside workbench|from workbench account)|hermes authentication returned http (401|403)|unauthori[sz]ed\b|forbidden\b)/i.test(message.trim())
}

export function isHermesMissingSessionError(reason: unknown) {
  const message = reason instanceof Error ? reason.message : String(reason ?? '')
  return /session[^.\n]*(not found|does not exist|unknown|missing|stale)|no such session/i.test(message)
}

export async function resumeHermesSession(
  request: HermesSessionResumeRequest,
  storedSessionId: string,
  profile?: string,
) {
  const cleanStoredId = storedSessionId.trim()
  if (!cleanStoredId) throw new Error('Hermes cannot resume a conversation without its stored session ID.')

  const resumed = await request('session.resume', {
    session_id: cleanStoredId,
    cols: 96,
    source: 'desktop',
    omit_messages: true,
    ...(profile?.trim() ? { profile: profile.trim() } : {}),
  }) as HermesSessionResumeResult

  const runtimeSessionId = resumed?.session_id?.trim()
  if (!runtimeSessionId) throw new Error('Hermes resumed the conversation without returning a runtime session ID.')

  return {
    runtimeSessionId,
    storedSessionId: resumed.stored_session_id?.trim() || cleanStoredId,
  }
}
