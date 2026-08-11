import { Check, ExternalLink, Link2, LoaderCircle, RefreshCw, ShieldCheck } from 'lucide-react'
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
import type { LocalAccountLinkStatus } from './DesktopAccountLinkBridge'

export function AccountLinkPanel() {
  const [codex, setCodex] = useState<CodexAccountTelemetry>(getCodexAccountTelemetry)
  const [local, setLocal] = useState<LocalAccountLinkStatus[]>([])
  const [loading, setLoading] = useState(false)
  const [available, setAvailable] = useState(accountLinkHostAvailable)

  const refresh = useCallback(() => {
    setAvailable(accountLinkHostAvailable())
    setLoading(true)
    void requestLocalAccountLinkStatus().then(setLocal).finally(() => setLoading(false))
  }, [])

  useEffect(() => subscribeCodexAccountTelemetry(setCodex), [])
  useEffect(() => {
    refresh()
    window.addEventListener('hermes-desktop-ready', refresh)
    return () => window.removeEventListener('hermes-desktop-ready', refresh)
  }, [refresh])

  const claude = local.find((entry) => entry.provider === 'claude')
  const antigravity = local.find((entry) => entry.provider === 'antigravity')
  const googleCloud = local.find((entry) => entry.provider === 'google-cloud')

  return (
    <section className="ui-panel ui-account-link-panel" aria-label="Linked AI accounts">
      <header className="ui-panel-heading">
        <div><span className="ui-eyebrow">Subscription connections</span><h2>Link the tools you already pay for</h2></div>
        <button type="button" className="ui-account-refresh" onClick={refresh} disabled={loading}><RefreshCw className={loading ? 'ui-spin' : undefined} size={12} />Refresh</button>
      </header>
      <div className="ui-account-link-grid">
        <AccountLinkCard
          name="ChatGPT / Codex"
          linked={codex.chatGptLinked}
          available
          status={codex.chatGptLinked
            ? `${codex.label}${codex.usedPercent === undefined ? '' : ` · ${Math.round(codex.usedPercent)}% used`}`
            : codex.authenticated ? `${codex.label} · ChatGPT subscription not linked`
            : codex.checked ? 'Sign in with the official Codex device flow.' : 'Checking the local Codex account…'}
          detail="Uses Codex-managed ChatGPT sign-in and its official account/rate-limit protocol."
          onLink={requestChatGptAccountLink}
        />
        <AccountLinkCard
          name="Claude"
          linked={claude?.linked === true}
          available={claude?.installed === true}
          status={claude?.status ?? (available ? 'Checking Claude Code…' : 'Desktop app required.')}
          detail="Uses the official Claude Code auth login. Subscription rate limits appear after Claude exposes them through its documented status-line data."
          onLink={() => openLocalAccountLink('claude')}
        />
        <AccountLinkCard
          name="Google Antigravity"
          linked={antigravity?.linked === true}
          available={antigravity?.installed === true}
          status={antigravity?.status ?? (available ? 'Checking Antigravity CLI…' : 'Desktop app required.')}
          detail="Uses Antigravity's official local CLI and operating-system keyring sign-in. No Google password enters Workbench."
          onLink={() => openLocalAccountLink('antigravity')}
        />
        <AccountLinkCard
          name="Google Cloud / Gemini"
          linked={googleCloud?.linked === true}
          available={googleCloud?.installed === true}
          status={googleCloud?.status ?? (available ? 'Checking Google Cloud CLI…' : 'Desktop app required.')}
          detail="Uses Google Application Default Credentials in the official Cloud CLI for project-level Gemini Monitoring. Named API keys remain inventory-only."
          onLink={() => openLocalAccountLink('google-cloud')}
        />
      </div>
      <footer><ShieldCheck size={12} /> Workbench never asks for a ChatGPT, Claude, or Google password. Official clients own OAuth and keyring tokens.</footer>
    </section>
  )
}

function AccountLinkCard({
  name,
  linked,
  available,
  status,
  detail,
  onLink,
}: {
  name: string
  linked: boolean
  available: boolean
  status: string
  detail: string
  onLink: () => void
}) {
  return <article className={`ui-account-link-card ${linked ? 'linked' : ''}`}>
    <span className="ui-account-link-mark">{linked ? <Check size={14} /> : <Link2 size={14} />}</span>
    <div><strong>{name}</strong><span>{status}</span><small>{detail}</small></div>
    <button type="button" disabled={!available && name !== 'ChatGPT / Codex'} onClick={onLink}>
      {linked ? 'Relink' : available || name === 'ChatGPT / Codex' ? 'Link account' : 'Not installed'}
      {available || name === 'ChatGPT / Codex' ? <ExternalLink size={11} /> : <LoaderCircle size={11} />}
    </button>
  </article>
}
