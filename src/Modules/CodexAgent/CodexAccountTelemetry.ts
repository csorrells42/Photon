export type CodexAccountTelemetry = {
  checked: boolean
  authenticated: boolean
  chatGptLinked: boolean
  label: string
  usedPercent?: number
  weeklyUsedPercent?: number
  weeklyResetAt?: number
  lifetimeTokens?: number
}

let current: CodexAccountTelemetry = {
  checked: false,
  authenticated: false,
  chatGptLinked: false,
  label: 'Checking ChatGPT account',
}

export function isChatGptAccountType(value: unknown) {
  return value === 'chatgpt' || value === 'chatgptDeviceCode'
}

const listeners = new Set<(value: CodexAccountTelemetry) => void>()

export function getCodexAccountTelemetry() { return current }

export function publishCodexAccountTelemetry(value: CodexAccountTelemetry) {
  current = { ...value }
  listeners.forEach((listener) => listener(current))
  window.dispatchEvent(new CustomEvent('hermes-usage-account-changed'))
}

export function subscribeCodexAccountTelemetry(listener: (value: CodexAccountTelemetry) => void) {
  listeners.add(listener)
  listener(current)
  return () => { listeners.delete(listener) }
}

export function requestChatGptAccountLink() {
  window.dispatchEvent(new CustomEvent('hermes-show-codex'))
  window.dispatchEvent(new CustomEvent('hermes-codex-login-requested'))
}
