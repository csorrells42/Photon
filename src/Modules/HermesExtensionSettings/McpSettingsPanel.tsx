import { useEffect, useMemo, useState, type MouseEvent } from 'react'
import type {
  ExtensionWriteIntent,
  HermesExtensionSettingsSnapshot,
  McpServerConfiguration,
} from './contracts'

interface McpSettingsPanelProps {
  snapshot: HermesExtensionSettingsSnapshot
  busy: boolean
  onPreview(intent: ExtensionWriteIntent, source: HTMLElement): Promise<void>
}

function nonSecretEndpoint(value: string) {
  return value.split(/[?#]/, 1)[0].replace(/^(https?:\/\/)[^/@]+@/i, '$1').slice(0, 2_048)
}

export function McpSettingsPanel({ snapshot, busy, onPreview }: McpSettingsPanelProps) {
  const [selectedId, setSelectedId] = useState(snapshot.mcpServers[0]?.id ?? '')
  const selected = useMemo(
    () => snapshot.mcpServers.find((server) => server.id === selectedId) ?? snapshot.mcpServers[0] ?? null,
    [selectedId, snapshot.mcpServers],
  )
  const [draft, setDraft] = useState<McpServerConfiguration | null>(() => selected ? structuredClone(selected) : null)

  useEffect(() => {
    setDraft(selected ? structuredClone(selected) : null)
  }, [selected])

  if (!draft || !selected) {
    return (
      <section className="hes-panel" aria-labelledby="hes-mcp-heading">
        <header className="hes-panel-heading"><div><small>MCP CONFIGURATION</small><h2 id="hes-mcp-heading">MCP review and edit</h2></div></header>
        <div className="hes-inline-state unavailable">No MCP configuration is available.</div>
      </section>
    )
  }

  const preview = (intent: ExtensionWriteIntent, event: MouseEvent<HTMLButtonElement>) =>
    onPreview(intent, event.currentTarget)

  return (
    <section className="hes-panel" aria-labelledby="hes-mcp-heading">
      <header className="hes-panel-heading">
        <div>
          <small>MCP CONFIGURATION</small>
          <h2 id="hes-mcp-heading">Review, edit, test, and enable</h2>
          <p>Updates, tests, and enabled-state changes each require before/after review and confirmed commit.</p>
        </div>
      </header>

      <div className="hes-mcp-layout">
        <nav className="hes-mcp-list" aria-label="MCP servers">
          {snapshot.mcpServers.map((server) => (
            <button
              type="button"
              key={server.id}
              className={selected.id === server.id ? 'selected' : ''}
              aria-pressed={selected.id === server.id}
              onClick={() => setSelectedId(server.id)}
            >
              <strong>{server.name}</strong>
              <span className={`hes-provenance ${server.provenance}`}>{server.provenance}</span>
              <small>{server.enabled ? 'Enabled' : 'Disabled'} - {server.transport}</small>
            </button>
          ))}
        </nav>

        <div className="hes-mcp-editor">
          <div className="hes-field-grid">
            <label>
              <span>Name</span>
              <input value={draft.name} maxLength={256} onChange={(event) => setDraft((current) => current ? { ...current, name: event.target.value } : current)} />
            </label>
            <label>
              <span>Transport</span>
              <select
                value={draft.transport}
                onChange={(event) => setDraft((current) => current ? {
                  ...current,
                  transport: event.target.value === 'stdio' ? 'stdio' : 'http',
                  endpoint: event.target.value === 'stdio' ? '' : current.endpoint,
                  command: event.target.value === 'stdio' ? current.command : '',
                } : current)}
              >
                <option value="http">HTTP</option>
                <option value="stdio">stdio</option>
              </select>
            </label>
          </div>

          {draft.transport === 'http' ? (
            <label>
              <span>HTTP endpoint - no query or credentials</span>
              <input type="url" value={draft.endpoint} onChange={(event) => setDraft((current) => current ? { ...current, endpoint: nonSecretEndpoint(event.target.value) } : current)} />
            </label>
          ) : (
            <>
              <label>
                <span>Command</span>
                <input value={draft.command} maxLength={1_024} onChange={(event) => setDraft((current) => current ? { ...current, command: event.target.value } : current)} />
              </label>
              <label>
                <span>Arguments - one per line</span>
                <textarea
                  value={draft.args.join('\n')}
                  onChange={(event) => setDraft((current) => current ? {
                    ...current,
                    args: event.target.value.split(/\r?\n/).map((value) => value.trim()).filter(Boolean).slice(0, 64),
                  } : current)}
                />
              </label>
            </>
          )}

          <section className="hes-mcp-safe-metadata">
            <h3>Renderer-safe metadata</h3>
            <dl>
              <div><dt>Provenance</dt><dd><span className={`hes-provenance ${draft.provenance}`}>{draft.provenance}</span></dd></div>
              <div><dt>Environment names</dt><dd>{draft.environmentVariableNames.join(', ') || 'None advertised'}</dd></div>
              <div><dt>Stored values</dt><dd>Never supplied to this module</dd></div>
            </dl>
          </section>

          <div className="hes-actions">
            <button
              type="button"
              className="hes-primary"
              disabled={busy || !draft.name.trim() || (draft.transport === 'http' ? !draft.endpoint.trim() : !draft.command.trim())}
              onClick={(event) => void preview({ kind: 'mcp-update', serverId: selected.id, configuration: structuredClone(draft) }, event)}
            >
              Preview update
            </button>
            <button
              type="button"
              disabled={busy}
              onClick={(event) => void preview({ kind: 'mcp-test', serverId: selected.id }, event)}
            >
              Preview test
            </button>
            <button
              type="button"
              disabled={busy}
              onClick={(event) => void preview({ kind: 'mcp-enable', serverId: selected.id, enabled: !selected.enabled }, event)}
            >
              Preview {selected.enabled ? 'disable' : 'enable'}
            </button>
          </div>
        </div>
      </div>
    </section>
  )
}
