import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import {
  HermesReasoningControl,
  hermesReasoningControlLabel,
  hermesReasoningControlSummary,
  hermesReasoningOptionRows,
  nextReasoningOptionIndex,
} from './HermesReasoningControl'
import type { HermesReasoningControlOptions } from '../HermesSettings/HermesModelAdapter'

function control(
  targetProvider: string,
  targetModel: string,
  values: Partial<HermesReasoningControlOptions> & Pick<HermesReasoningControlOptions, 'label' | 'options' | 'source'>,
): HermesReasoningControlOptions {
  return {
    effortAliases: {},
    mandatory: false,
    optionLabels: {},
    supportsEffort: false,
    supportsToggle: false,
    targetModel,
    targetProvider,
    ...values,
  }
}

function markup(options?: HermesReasoningControlOptions, effort: Parameters<typeof HermesReasoningControl>[0]['effort'] = null) {
  return renderToStaticMarkup(<HermesReasoningControl
    disabled={false}
    effort={effort}
    modelSelection={options ? { provider: options.targetProvider ?? '', model: options.targetModel ?? '' } : null}
    onChange={() => undefined}
    options={options}
  />)
}

describe('Hermes per-conversation reasoning control', () => {
  it('uses a neutral fallback vocabulary only when no descriptor is bound', () => {
    expect(hermesReasoningControlLabel({ provider: 'custom:lm-studio', model: 'gpt-oss-20b' })).toBe('Thinking')
    expect(hermesReasoningControlLabel({ provider: 'openai-codex', model: 'gpt-5.6-sol' })).toBe('Effort')
    expect(hermesReasoningControlLabel({ provider: 'openrouter', model: 'some-reasoner' })).toBe('Reasoning')
  })

  it('does not offer invented levels without a verified capability set', () => {
    const rendered = renderToStaticMarkup(<HermesReasoningControl
      disabled={false}
      effort="medium"
      modelSelection={{ provider: 'custom:lm-studio', model: 'gpt-oss-20b' }}
      onChange={() => undefined}
    />)
    expect(rendered).toContain('could not verify reasoning controls')
    expect(rendered).toContain('disabled=""')
    expect(rendered).toContain('Model default')
  })

  it('renders toggle-only models as Thinking On or Off', () => {
    const options = control('custom:lm-studio', 'gemma-4', {
      label: 'Thinking',
      options: ['none', 'enabled'],
      optionLabels: { none: 'Off', enabled: 'On' },
      source: 'server',
      supportsToggle: true,
    })
    expect(hermesReasoningControlSummary(options, 'enabled')).toBe('On')
    expect(hermesReasoningControlSummary(options, 'none')).toBe('Off')
    expect(hermesReasoningOptionRows(options)).toEqual([
      { label: 'Off', value: 'none' },
      { label: 'On', value: 'enabled' },
    ])
  })

  it('renders effort-only models as Effort Medium', () => {
    const options = control('openai-codex', 'gpt-5.5', {
      label: 'Effort',
      options: ['low', 'medium', 'high'],
      source: 'compatibility',
      supportsEffort: true,
    })
    expect(hermesReasoningControlSummary(options, 'medium')).toBe('Medium')
    expect(markup(options, 'medium')).toMatch(/Effort[\s\S]*<strong>Medium<\/strong>/)
  })

  it('renders hybrid models as Thinking On with an independent effort', () => {
    const options = control('custom:lm-studio', 'gpt-oss-20b', {
      label: 'Thinking',
      options: ['none', 'low', 'medium', 'high'],
      source: 'server',
      supportsEffort: true,
      supportsToggle: true,
    })
    expect(hermesReasoningControlSummary(options, 'high')).toBe('On · Effort High')
    expect(hermesReasoningOptionRows(options).find((row) => row.value === 'high')?.label)
      .toBe('On · Effort High')
    expect(markup(options, 'high')).toContain('On · Effort High')
  })

  it('shows only DeepSeek V4 controls that have distinct wire behavior', () => {
    const options = control('deepseek', 'deepseek-v4-flash', {
      defaultEffort: 'high',
      defaultEnabled: true,
      label: 'Thinking',
      options: ['none', 'high', 'max'],
      source: 'compatibility',
      supportsEffort: true,
      supportsToggle: true,
    })
    expect(hermesReasoningControlSummary(options, null)).toBe('On · Effort High')
    expect(hermesReasoningOptionRows(options)).toEqual([
      { label: 'Off', value: 'none' },
      { label: 'On · Effort High', value: 'high' },
      { label: 'On · Effort Maximum', value: 'max' },
    ])
  })

  it('shows a mandatory thinking model as on and never offers Off', () => {
    const options = control('openrouter', 'anthropic/claude-opus-4.7', {
      defaultEffort: 'high',
      defaultEnabled: true,
      label: 'Thinking',
      mandatory: true,
      options: ['low', 'high', 'max'],
      source: 'server',
      supportsEffort: true,
    })
    expect(hermesReasoningControlSummary(options, null)).toBe('On · Effort High')
    expect(hermesReasoningOptionRows(options).map((row) => row.value)).toEqual(['low', 'high', 'max'])
    expect(markup(options)).not.toContain('Not supported')
  })

  it('shows Docker Model Runner as runner managed and emits no control', () => {
    const options = control('custom:docker-model-runner', 'ai/qwen3', {
      label: 'Reasoning',
      options: [],
      source: 'server-managed',
    })
    const rendered = markup(options)
    expect(rendered).toContain('Runner managed')
    expect(rendered).toContain('model runner owns reasoning configuration')
    expect(rendered).toContain('disabled=""')
  })

  it('makes the whole visible chip one accessible button', () => {
    const options = control('openai-api', 'gpt-5.6', {
      label: 'Reasoning',
      options: ['none', 'low', 'medium', 'high'],
      source: 'compatibility',
      supportsEffort: true,
      supportsToggle: true,
    })
    const rendered = markup(options, 'medium')
    expect(rendered).toMatch(/<button[^>]*class="hermes-reasoning-trigger"[^>]*>[\s\S]*Reasoning[\s\S]*On · Effort Medium[\s\S]*<\/button>/)
    expect(rendered).toContain('aria-haspopup="listbox"')
    expect(rendered).not.toContain('<select')
  })

  it('fails closed while model selection and option binding differ', () => {
    const options = control('openrouter', 'deepseek/deepseek-v4-flash', {
      label: 'Thinking',
      options: ['none', 'low', 'high'],
      source: 'server',
      supportsEffort: true,
      supportsToggle: true,
    })
    const rendered = renderToStaticMarkup(<HermesReasoningControl
      disabled={false}
      effort="high"
      modelSelection={{ provider: 'openai-codex', model: 'gpt-5.6-sol' }}
      onChange={() => undefined}
      options={options}
    />)
    expect(rendered).toContain('Model default')
    expect(rendered).toContain('disabled=""')
  })

  it('shows the effective adapter alias without sending an invalid tier', () => {
    const options = control('openai-codex', 'gpt-5.6-sol', {
      effortAliases: { ultra: 'max' },
      label: 'Reasoning',
      options: ['none', 'low', 'medium', 'high', 'xhigh', 'max'],
      source: 'compatibility',
      supportsEffort: true,
      supportsToggle: true,
    })
    expect(hermesReasoningControlSummary(options, 'ultra')).toBe('On · Effort Maximum')
  })

  it('renders provider labels as text rather than HTML', () => {
    const options = control('custom:lm-studio', 'reasoner', {
      label: 'Thinking',
      options: ['high'],
      optionLabels: { high: '<img src=x onerror=alert(1)>' },
      source: 'server',
      supportsEffort: true,
    })
    const rendered = markup(options, 'high')
    expect(rendered).toContain('&lt;img src=x onerror=alert(1)&gt;')
    expect(rendered).not.toContain('<img src="x"')
  })

  it('wraps arrow navigation and supports Home and End deterministically', () => {
    expect(nextReasoningOptionIndex(4, 3, 'next')).toBe(0)
    expect(nextReasoningOptionIndex(4, 0, 'previous')).toBe(3)
    expect(nextReasoningOptionIndex(4, 2, 'first')).toBe(0)
    expect(nextReasoningOptionIndex(4, 1, 'last')).toBe(3)
    expect(nextReasoningOptionIndex(0, 0, 'next')).toBe(-1)
  })
})
