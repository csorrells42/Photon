import { describe, expect, it } from 'vitest'
import { buildCodexApprovalReviewPrompt } from './CodexApprovalReview'

describe('Codex approval review prompt', () => {
  it('includes the raw command and refuses to invent a missing reason', () => {
    const prompt = buildCodexApprovalReviewPrompt({
      requestId: 'approval-1',
      command: 'rm -rf /root',
      description: 'recursive delete of system directory',
      reason: null,
      choices: ['once', 'deny'],
      allowPermanent: false,
      smartDenied: true,
      sessionId: 'session-1',
    }, [
      { id: '1', author: 'you', body: 'Clean up the temporary build folder.' },
      { id: '2', author: 'hermes', body: 'I will inspect it first.' },
    ])

    expect(prompt).toContain('Do not execute the command')
    expect(prompt).toContain('No reason was supplied.')
    expect(prompt).toContain('rm -rf /root')
    expect(prompt).toContain('Chris: Clean up the temporary build folder.')
    expect(prompt).toContain('Photon: I will inspect it first.')
  })
})
