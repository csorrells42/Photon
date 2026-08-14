import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { clampQuarkPosition, parseQuarkPosition, QuarkCompanion, quarkConversationPrompt } from './QuarkCompanion'
import { hermesVisiblePromptText } from '../HermesGateway/useHermesChat'

describe('QuarkCompanion', () => {
  it('renders the talk control and idle status', () => {
    const html = renderToStaticMarkup(<QuarkCompanion active={false} phase="idle" onExit={() => undefined} onRetry={() => undefined} onStop={() => undefined} onTalk={() => undefined} />)
    expect(html).toContain('aria-label="Start conversation with Quark"')
    expect(html).toContain('Talk with Quark')
    expect(html).not.toContain('Exit Quark conversation mode')
    expect(html).toContain('aria-label="Reset Quark position"')
  })

  it('clamps persisted positions into the visible app viewport', () => {
    expect(clampQuarkPosition({ x: -500, y: 9999 }, { width: 1000, height: 700 }, { width: 200, height: 100 })).toEqual({ x: 8, y: 592 })
    expect(clampQuarkPosition({ x: 120, y: 90 }, { width: 1000, height: 700 }, { width: 200, height: 100 })).toEqual({ x: 120, y: 90 })
  })

  it('accepts only finite persisted coordinates', () => {
    expect(parseQuarkPosition('{"x":14,"y":28}')).toEqual({ x: 14, y: 28 })
    expect(parseQuarkPosition('{"x":"14","y":28}')).toBeNull()
    expect(parseQuarkPosition('junk')).toBeNull()
  })

  it('offers stop and exit while listening', () => {
    const html = renderToStaticMarkup(<QuarkCompanion active phase="listening" onExit={() => undefined} onRetry={() => undefined} onStop={() => undefined} onTalk={() => undefined} />)
    expect(html).toContain('aria-label="Stop listening and send to Photon"')
    expect(html).toContain('aria-label="Exit Quark conversation mode"')
    expect(html).toContain('Listening...')
  })

  it('offers a distinct stop action while Photon is answering', () => {
    const html = renderToStaticMarkup(<QuarkCompanion active phase="thinking" onExit={() => undefined} onRetry={() => undefined} onStop={() => undefined} onTalk={() => undefined} />)
    expect(html).toContain('aria-label="Stop Quark response"')
    expect(html).toContain('aria-label="Exit Quark conversation mode"')
  })

  it('keeps the conversational guidance bounded and the transcript intact', () => {
    const transcript = 'How is the build going?'
    const prompt = quarkConversationPrompt(`  ${transcript}  `)
    expect(prompt).toContain('one to three short sentences')
    expect(prompt).toContain('User said: How is the build going?')
    expect(prompt.length).toBeLessThan(600)
    expect(hermesVisiblePromptText(prompt, transcript)).toBe(transcript)
    expect(hermesVisiblePromptText(prompt, transcript)).not.toContain('Conversation mode:')
  })

  it('drops below approval surfaces while a review is active', () => {
    const html = renderToStaticMarkup(<QuarkCompanion active={false} reviewActive phase="idle" onExit={() => undefined} onRetry={() => undefined} onStop={() => undefined} onTalk={() => undefined} />)
    expect(html).toContain('is-reviewing')
  })
})
