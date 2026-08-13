import { Check, ExternalLink, Gauge, Link2, LoaderCircle, RefreshCw, ShieldCheck } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'
import {
  getCodexAccountTelemetry,
  requestChatGptAccountLink,
  subscribeCodexAccountTelemetry,
} from '../CodexAgent/CodexAccountTelemetry'
import type { CodexAccountTelemetry } from '../CodexAgent/CodexAccountTelemetry'
import {
  accountLinkHostAvailable,
  openLocalAccountLink,
  requestLocalAccountLinkStatus,
} from './DesktopAccountLinkBridge'
import type { LocalAccountLinkProvider, LocalAccountLinkStatus } from './DesktopAccountLinkBridge'
import './AccountLinkPanel.css'

export function AccountLinkPanel() {
  const [codex, setCodex] = useState<CodexAccountTelemetry>(getCodexAccountTelemetry)
  const [local, setLocal] = useState<LocalAccountLinkStatus[]>([])
  const [loading, setLoading] = useState(false)
  const [available, setAvailable] = useState(accountLinkHostAvailable)
  const [openingProvider, setOpeningProvider] = useState<LocalAccountLinkProvider | null>(null)

  const refreshProvider = useCallback(async (provider: LocalAccountLinkProvider) => {
    const result = await requestLocalAccountLinkStatus(provider)
    const status = result.find((entry) => entry.provider === provider)
    if (!status) return
    setLocal((current) => [...current.filter((entry) => entry.provider !== provider), status])
  }, [])

  const refresh = useCallback(() => {
    setAvailable(accountLinkHostAvailable())
    setLoading(true)
    const providers: LocalAccountLinkProvider[] = ['claude', 'antigravity', 'google-cloud']
    void Promise.allSettled(providers.map(refreshProvider)).finally(() => setLoading(false))
  }, [refreshProvider])

  useEffect(() => subscribeCodexAccountTelemetry(setCodex), [])
  useEffect(() => {
    refresh()
    window.addEventListener('hermes-desktop-ready', refresh)
    return () => window.removeEventListener('hermes-desktop-ready', refresh)
  }, [refresh])

  const claude = local.find((entry) => entry.provider === 'claude')
  const antigravity = local.find((entry) => entry.provider === 'antigravity')
  const googleCloud = local.find((entry) => entry.provider === 'google-cloud')
  const openAccountPage = (url: string, key: string, label: string) => window.dispatchEvent(new CustomEvent('photos-browser-open', {
    detail: { url, key, label },
  }))
  const openAndRecheck = useCallback((provider: LocalAccountLinkProvider) => {
    void (async () => {
      setOpeningProvider(provider)
      try
      {
        const result = await openLocalAccountLink(provider)
        if (!result.opened) return
        for (const delay of [0, 750, 2_000, 4_000]) {
          if (delay > 0) await new Promise<void>((resolve) => window.setTimeout(resolve, delay))
          await refreshProvider(provider)
        }
      }
      finally
      {
        setOpeningProvider((current) => current === provider ? null : current)
      }
    })()
  }, [refreshProvider])

  return (
    <section className="ui-panel ui-account-link-panel" aria-label="Subscription account monitors">
      <header className="ui-panel-heading">
        <div><span className="ui-eyebrow">Subscription monitors</span><h2>The accounts you already pay for</h2><p>Each account reports independently. One slow or missing provider never hides the others.</p></div>
        <button type="button" className="ui-account-refresh" onClick={refresh} disabled={loading}><RefreshCw className={loading ? 'ui-spin' : undefined} size={12} />Refresh</button>
      </header>
      <div className="ui-account-link-grid">
        <AccountLinkCard
          name="ChatGPT / Codex subscription"
          linked={codex.chatGptLinked}
          available
          status={codex.chatGptLinked
            ? codex.label
            : codex.authenticated ? `${codex.label} · ChatGPT subscription not linked`
            : codex.checked ? 'Sign in with the official Codex device flow.' : 'Checking the local Codex account…'}
          detail="Uses Codex-managed ChatGPT sign-in and its official account and rate-limit protocol."
          usage={codex.weeklyUsedPercent !== undefined
            ? `${Math.round(codex.weeklyUsedPercent)}% of the weekly allowance used`
            : codex.usedPercent !== undefined
              ? `${Math.round(codex.usedPercent)}% of the current reported allowance used; weekly data is not available.`
              : 'Weekly allowance is waiting for Codex telemetry.'}
          onLink={requestChatGptAccountLink}
        />
        <AccountLinkCard
          name="Claude subscription"
          linked={claude?.linked === true}
          available={claude?.installed === true}
          status={claude?.status ?? (available ? 'Checking Claude Code…' : 'Desktop app required.')}
          detail="Uses Claude Code when installed; the official usage page remains available without a local CLI."
          usage="Claude does not expose subscription-week percentages through the installed local interface."
          onLink={() => openAccountPage('https://claude.ai/settings/usage', 'claude-subscription-usage', 'Claude usage')}
          actionLabel="Open usage"
          actionAvailable
        />
        <AccountLinkCard
          name="Google Antigravity subscription"
          linked={antigravity?.linked === true}
          available={antigravity?.installed === true}
          status={antigravity?.status ?? (available ? 'Checking Antigravity…' : 'Desktop app required.')}
          detail="Uses the official Antigravity desktop or CLI sign-in. Photon never asks for your Google password."
          usage={antigravity?.trackingAvailable === true
            ? 'The official client provides limited account tracking.'
            : 'Antigravity does not expose subscription allowance tracking to Photon; view it in the official client.'}
          onLink={() => openAndRecheck('antigravity')}
          opening={openingProvider === 'antigravity'}
        />
        <AccountLinkCard
          name="Google AI Studio / Gemini"
          linked={googleCloud?.linked === true}
          available
          status={googleCloud?.linked ? 'Google Cloud project credentials are available.' : 'AI Studio sign-in and quota are managed on the official usage page.'}
          detail="Consumer Google login and AI Studio quota are separate from optional Cloud project telemetry."
          usage="Open the official AI Studio usage page for the currently signed-in Google account."
          onLink={() => openAccountPage('https://aistudio.google.com/usage', 'google-ai-studio-usage', 'Google AI Studio usage')}
          actionLabel="Open usage"
          actionAvailable
        />
      </div>
      <footer><ShieldCheck size={12} /> Usage is shown only when an official client reports it; otherwise Photon opens that provider's own account view.</footer>
    </section>
  )
}

function AccountLinkCard({
  name,
  linked,
  available,
  status,
  detail,
  usage,
  onLink,
  actionLabel,
  actionAvailable,
  opening = false,
}: {
  name: string
  linked: boolean
  available: boolean
  status: string
  detail: string
  usage: string
  onLink: () => void
  actionLabel?: string
  actionAvailable?: boolean
  opening?: boolean
}) {
  const canOpen = actionAvailable === true || available || name.startsWith('ChatGPT / Codex')
  return <article className={`ui-account-link-card ${linked ? 'linked' : ''}`}>
    <span className="ui-account-link-mark">{linked ? <Check size={14} /> : <Link2 size={14} />}</span>
    <div><strong>{name}</strong><span>{status}</span><small>{detail}</small><em><Gauge size={11} />{usage}</em></div>
    <button type="button" disabled={!canOpen || opening} onClick={onLink}>
      {opening ? 'Opening and rechecking…' : actionLabel ?? (linked ? 'Open account' : canOpen ? 'Open sign-in' : 'Not installed')}
      {canOpen ? <ExternalLink size={11} /> : <LoaderCircle size={11} />}
    </button>
  </article>
}
