import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { HermesAdvancedComposer } from './HermesAdvancedComposer'
import { HermesComposerController } from './HermesComposerController'

function controller() {
  let id = 0
  return new HermesComposerController({ submit: async () => ({ accepted: true }), createId: () => `render-${++id}` })
}

describe('HermesAdvancedComposer', () => {
  it('renders caller input and message content as escaped text, never HTML', () => {
    const hostile = '<img src=x onerror="alert(1)"><script>unsafe()</script>'
    const markup = renderToStaticMarkup(
      <HermesAdvancedComposer
        controller={controller()}
        draft={hostile}
        onDraftChange={() => undefined}
        messageReferences={[{ messageId: 'message-1', text: hostile, label: '<b>Label</b>' }]}
      />,
    )

    expect(markup).toContain('&lt;script&gt;unsafe()&lt;/script&gt;')
    expect(markup).toContain('&lt;b&gt;Label&lt;/b&gt;')
    expect(markup).not.toContain('<script>')
    expect(markup).not.toContain('<img src=x')
    expect(markup).not.toContain('dangerouslySetInnerHTML')
  })

  it('exposes keyboard and assistive-technology semantics', () => {
    const markup = renderToStaticMarkup(
      <HermesAdvancedComposer
        controller={controller()}
        draft="Ask @"
        onDraftChange={() => undefined}
        completionCatalogs={{ mentions: [{ id: 'workspace', label: 'Workspace', description: 'Current context' }] }}
      />,
    )
    expect(markup).toContain('role="combobox"')
    expect(markup).toContain('aria-autocomplete="list"')
    expect(markup).toContain('role="listbox"')
    expect(markup).toContain('role="option"')
    expect(markup).toContain('Ctrl/Cmd+Enter queues the draft')
    expect(markup).toContain('↑↓ navigate')
  })

  it('renders every connection and voice recovery state without auto-submit controls', () => {
    const composer = controller()
    for (const connection of ['ready', 'reconnecting', 'offline', 'blocked'] as const) {
      composer.setConnection(connection)
      const markup = renderToStaticMarkup(<HermesAdvancedComposer controller={composer} draft="" onDraftChange={() => undefined} />)
      expect(markup).toContain(connection === 'ready' ? 'Ready' : connection[0].toUpperCase() + connection.slice(1))
      if (connection === 'reconnecting') expect(markup).toContain('will not replay automatically')
    }

    for (const voice of [
      { kind: 'idle' },
      { kind: 'listening' },
      { kind: 'processing' },
      { kind: 'error', message: 'Voice input needs attention.' },
    ] as const) {
      composer.setVoiceState(voice)
      const markup = renderToStaticMarkup(<HermesAdvancedComposer controller={composer} draft="" onDraftChange={() => undefined} onVoiceAction={() => undefined} />)
      expect(markup).toContain(`hc-voice--${voice.kind}`)
    }
  })
})
