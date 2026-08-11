import { describe, expect, it } from 'vitest'
import {
  HERMES_TOOL_DETAIL_LIMIT,
  HERMES_TOOL_TRUNCATION_SUFFIX,
  mergeHermesToolEvent,
  normalizeHermesApproval,
  normalizeHermesInteractivePrompt,
} from './HermesRuntimeAdapter'

describe('Hermes runtime compatibility adapter', () => {
  it('merges start, progress, and completion frames into one tool run', () => {
    const started = mergeHermesToolEvent([], {
      type: 'tool.start',
      session_id: 'runtime-1',
      payload: { tool_id: 'call-1', name: 'terminal', args: { command: 'git status --short' } },
    }, 'fallback')
    const progressed = mergeHermesToolEvent(started, {
      type: 'tool.progress',
      payload: { tool_id: 'call-1', name: 'terminal', status: 'Reading repository state' },
    })
    const completed = mergeHermesToolEvent(progressed, {
      type: 'tool.complete',
      payload: { tool_id: 'call-1', name: 'terminal', result: 'clean', duration_s: 0.42 },
    })

    expect(completed).toEqual([expect.objectContaining({
      id: 'call-1',
      name: 'terminal',
      phase: 'complete',
      context: 'Reading repository state',
      input: '{\n  "command": "git status --short"\n}',
      output: 'clean',
      durationSeconds: 0.42,
    })])
  })

  it('matches an id-less completion to the latest running tool with the same name', () => {
    const current = [{ id: 'generated', name: 'search_files', phase: 'running' as const }]
    const completed = mergeHermesToolEvent(current, {
      type: 'tool.complete',
      payload: { name: 'search_files', result: ['one.ts', 'two.ts'] },
    })

    expect(completed).toHaveLength(1)
    expect(completed[0]).toMatchObject({ id: 'generated', phase: 'complete' })
    expect(completed[0].output).toContain('one.ts')
  })

  it('marks a failed tool completion and keeps the error detail', () => {
    const tools = mergeHermesToolEvent([], {
      type: 'tool.complete',
      payload: { tool_id: 'bad-1', name: 'terminal', error: 'permission denied' },
    }, 'fallback')

    expect(tools[0]).toMatchObject({ phase: 'error', output: 'permission denied' })
  })

  it('normalizes inline diffs through the same bounded detail contract', () => {
    const inlineDiff = '--- a/file.ts\n+++ b/file.ts\n@@ -1 +1 @@\n-old\n+new'
    const tools = mergeHermesToolEvent([], {
      type: 'tool.complete',
      payload: { tool_id: 'edit-1', name: 'edit', inline_diff: inlineDiff },
    }, 'fallback')

    expect(tools[0]).toMatchObject({ phase: 'complete', inlineDiff })
  })

  it('caps oversized and non-string inline diffs before they reach the renderer', () => {
    const oversized = mergeHermesToolEvent([], {
      type: 'tool.complete',
      payload: { tool_id: 'edit-2', name: 'edit', inline_diff: 'x'.repeat(HERMES_TOOL_DETAIL_LIMIT + 1) },
    }, 'fallback')
    const structured = mergeHermesToolEvent([], {
      type: 'tool.complete',
      payload: { tool_id: 'edit-3', name: 'edit', inline_diff: { patch: '<script>not markup</script>' } },
    }, 'fallback')

    expect(oversized[0].inlineDiff).toBe(`${'x'.repeat(HERMES_TOOL_DETAIL_LIMIT)}${HERMES_TOOL_TRUNCATION_SUFFIX}`)
    expect(structured[0].inlineDiff).toBe('{\n  "patch": "<script>not markup</script>"\n}')
  })

  it('normalizes a session-keyed approval request', () => {
    const request = normalizeHermesApproval({
      type: 'approval.request',
      session_id: 'runtime-7',
      payload: {
        command: 'Remove-Item example.txt',
        description: 'delete a file',
        choices: ['once', 'session', 'always', 'deny'],
      },
    }, 'ignored')

    expect(request).toEqual({
      requestId: '',
      command: 'Remove-Item example.txt',
      description: 'delete a file',
      choices: ['once', 'session', 'always', 'deny'],
      allowPermanent: true,
      smartDenied: false,
      sessionId: 'runtime-7',
    })
  })

  it('removes permanent approval when the backend forbids it', () => {
    const request = normalizeHermesApproval({
      type: 'approval.request',
      payload: { allow_permanent: false },
    }, 'runtime-8')

    expect(request?.choices).toEqual(['once', 'session', 'deny'])
    expect(request?.sessionId).toBe('runtime-8')
  })

  it('uses the restricted smart-denial choices when choices are omitted', () => {
    const request = normalizeHermesApproval({
      type: 'approval.request',
      payload: { smart_denied: true },
    }, 'runtime-9')

    expect(request?.choices).toEqual(['once', 'deny'])
  })

  it('normalizes clarify choices and drops empty entries', () => {
    const prompt = normalizeHermesInteractivePrompt({
      type: 'clarify.request',
      session_id: 'runtime-10',
      payload: { request_id: 'clarify-1', question: 'Which branch?', choices: ['main', ' ', 4, 'feature'] },
    }, null)

    expect(prompt).toEqual({
      kind: 'clarify',
      requestId: 'clarify-1',
      sessionId: 'runtime-10',
      question: 'Which branch?',
      choices: ['main', 'feature'],
    })
  })

  it('rejects incomplete blocking prompts instead of parking a broken UI', () => {
    expect(normalizeHermesInteractivePrompt({ type: 'clarify.request', payload: { question: 'Missing id' } }, 's')).toBeNull()
    expect(normalizeHermesInteractivePrompt({ type: 'sudo.request', payload: {} }, 's')).toBeNull()
    expect(normalizeHermesInteractivePrompt({ type: 'secret.request', payload: {} }, 's')).toBeNull()
  })

  it('normalizes secret metadata without exposing a value', () => {
    const prompt = normalizeHermesInteractivePrompt({
      type: 'secret.request',
      payload: { request_id: 'secret-1', env_var: 'GITHUB_TOKEN', prompt: 'Paste a token' },
    }, 'runtime-11')

    expect(prompt).toEqual({
      kind: 'secret',
      requestId: 'secret-1',
      sessionId: 'runtime-11',
      envVar: 'GITHUB_TOKEN',
      prompt: 'Paste a token',
    })
  })
})
