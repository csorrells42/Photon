import { describe, expect, it } from 'vitest'
import { isChatGptAccountType } from './CodexAccountTelemetry'

describe('Codex account telemetry', () => {
  it('recognizes only explicit ChatGPT account modes', () => {
    expect(isChatGptAccountType('chatgpt')).toBe(true)
    expect(isChatGptAccountType('chatgptDeviceCode')).toBe(true)
    expect(isChatGptAccountType('apiKey')).toBe(false)
    expect(isChatGptAccountType('bedrock')).toBe(false)
    expect(isChatGptAccountType(null)).toBe(false)
  })
})
