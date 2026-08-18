import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { ModelControlPopover } from './ModelControlPopover'

describe('Hermes model truth display', () => {
  it('shows the global default and a different conversation model as separate facts', () => {
    const markup = renderToStaticMarkup(<ModelControlPopover
      approvalMode="smart"
      approvalModeSaving={false}
      catalog={{
        currentModel: 'gpt-5.6-terra',
        currentProvider: 'openai-codex',
        providers: [],
        reasoningControl: {
          defaultEnabled: false,
          effortAliases: {},
          label: 'Reasoning',
          mandatory: false,
          optionLabels: {},
          options: [],
          source: 'unverified',
          supportsEffort: false,
          supportsToggle: false,
          targetModel: '',
          targetProvider: '',
        },
      }}
      defaultSelection={{ provider: 'openai-codex', model: 'gpt-5.6-terra' }}
      loading={false}
      onCancelConfirmation={vi.fn()}
      onClose={vi.fn()}
      onConfirmSelection={vi.fn(async () => true)}
      onRefresh={vi.fn()}
      onSelect={vi.fn(async () => true)}
      onSetApprovalMode={vi.fn(async () => undefined)}
      pendingConfirmation={null}
      selection={{ provider: 'openrouter', model: 'deepseek/deepseek-v4-flash-0731' }}
      switching={false}
    />)

    expect(markup).toContain('Default for new conversations')
    expect(markup).toContain('openai-codex / gpt-5.6-terra')
    expect(markup).toContain('This conversation')
    expect(markup).toContain('openrouter / deepseek-v4-flash-0731')
    expect(markup).toContain('This conversation has a model override')
  })
})
