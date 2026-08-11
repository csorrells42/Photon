import { useCallback, useEffect, useRef, useState } from 'react'
import { HermesConsoleClient } from './HermesConsoleClient'
import type { HermesConsoleConnectionState } from './HermesConsoleClient'
import { useAssistantDisplayName } from '../AssistantIdentity/AssistantIdentity'

export type HermesConsoleLine = {
  id: string
  kind: 'command' | 'output' | 'error' | 'system'
  text: string
}

export function useHermesConsole() {
  const [assistantName] = useAssistantDisplayName()
  const clientRef = useRef<HermesConsoleClient | null>(null)
  if (!clientRef.current) clientRef.current = new HermesConsoleClient()
  const client = clientRef.current
  const [connection, setConnection] = useState<HermesConsoleConnectionState>('idle')
  const [lines, setLines] = useState<HermesConsoleLine[]>([])
  const [busy, setBusy] = useState(false)
  const [prompt, setPrompt] = useState(`${assistantName.toLocaleLowerCase()}> `)
  const [pendingConfirmation, setPendingConfirmation] = useState<{ command: string; message: string } | null>(null)

  const append = useCallback((kind: HermesConsoleLine['kind'], text: string) => {
    if (!text) return
    setLines((current) => [...current, { id: crypto.randomUUID(), kind, text }].slice(-400))
  }, [])

  const connect = useCallback(async () => {
    try { await client.connect() }
    catch (reason) { append('error', reason instanceof Error ? reason.message : 'Could not connect to Hermes Console.') }
  }, [append, client])

  useEffect(() => {
    const removeState = client.onState(setConnection)
    const removeFrame = client.onFrame((frame) => {
      if (frame.prompt !== undefined) setPrompt(frame.prompt.replace(/^hermes(?=>)/i, assistantName.toLocaleLowerCase()))
      if (frame.type === 'ready') {
        append('system', `${assistantName} Console connected · profile ${frame.profile ?? 'current'}`)
      } else if (frame.type === 'output') {
        append(frame.stream === 'stderr' ? 'error' : 'output', frame.data ?? '')
      } else if (frame.type === 'error') {
        append('error', frame.message ?? 'Hermes Console reported an error.')
      } else if (frame.type === 'confirm_required') {
        setPendingConfirmation({
          command: frame.command ?? '',
          message: frame.message ?? `Run ${frame.command ?? 'this command'}?`,
        })
      } else if (frame.type === 'complete') {
        setBusy(false)
        if (frame.status === 'cancelled') append('system', 'Command cancelled.')
      } else if (frame.type === 'clear') {
        setLines([])
      }
    })
    void connect()
    return () => { removeState(); removeFrame(); client.close() }
  }, [append, assistantName, client, connect])

  useEffect(() => setPrompt((current) => current.replace(/^[^>]*(?=>)/, assistantName.toLocaleLowerCase())), [assistantName])

  const run = useCallback((command: string) => {
    const clean = command.trim()
    if (!clean || busy || connection !== 'open') return false
    append('command', `${prompt}${clean}`)
    setBusy(true)
    try { client.run(clean); return true }
    catch (reason) {
      setBusy(false)
      append('error', reason instanceof Error ? reason.message : 'Console command could not be sent.')
      return false
    }
  }, [append, busy, client, connection, prompt])

  const confirm = useCallback(() => {
    if (!pendingConfirmation) return
    append('command', `confirm ${pendingConfirmation.command}`)
    setBusy(true)
    try { client.confirm(pendingConfirmation.command); setPendingConfirmation(null) }
    catch (reason) { setBusy(false); append('error', reason instanceof Error ? reason.message : 'Confirmation failed.') }
  }, [append, client, pendingConfirmation])

  const cancel = useCallback(() => {
    try { client.cancel() } catch { /* disconnected console is already cancelled */ }
    setPendingConfirmation(null)
    setBusy(false)
  }, [client])

  return { busy, cancel, confirm, connect, connection, lines, pendingConfirmation, prompt, run }
}
