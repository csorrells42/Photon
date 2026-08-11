import { useCallback, useEffect, useRef, useState } from 'react'
import {
  desktopDeveloperServicesClient,
  type DeveloperBuildResult,
  type DeveloperConfiguration,
  type DeveloperOperation,
  type DeveloperServicesDescription,
} from './DesktopDeveloperServicesClient'
import {
  canRunDeveloperOperation,
  DeveloperOperationGate,
  operationUnavailableMessage,
  type DeveloperOperationIdentity,
} from './DeveloperOperationGate'

export type DeveloperBuildController = {
  desktopAvailable: boolean
  description: DeveloperServicesDescription | null
  selectedTarget: string
  setSelectedTarget: (target: string) => void
  configuration: DeveloperConfiguration
  setConfiguration: (configuration: DeveloperConfiguration) => void
  describing: boolean
  activeOperation: DeveloperOperation | null
  building: boolean
  analyzing: boolean
  busy: boolean
  cancelling: boolean
  error: string | null
  result: DeveloperBuildResult | null
  refresh: () => void
  build: () => Promise<void>
  analyze: () => Promise<void>
  cancel: () => void
}

export function useDeveloperBuild(onOperationStarted?: (operation: DeveloperOperation) => void): DeveloperBuildController {
  const [desktopAvailable, setDesktopAvailable] = useState(desktopDeveloperServicesClient.available)
  const [description, setDescription] = useState<DeveloperServicesDescription | null>(null)
  const [selectedTarget, setSelectedTarget] = useState('')
  const [configuration, setConfiguration] = useState<DeveloperConfiguration>('Debug')
  const [describing, setDescribing] = useState(false)
  const [activeOperation, setActiveOperation] = useState<DeveloperOperation | null>(null)
  const [cancelling, setCancelling] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<DeveloperBuildResult | null>(null)
  const operationGate = useRef(new DeveloperOperationGate())

  const refresh = useCallback(() => {
    const gate = operationGate.current
    const available = desktopDeveloperServicesClient.available
    setDesktopAvailable(available)
    if (!available) {
      gate.invalidateDescription()
      setDescription(null)
      setSelectedTarget('')
      setDescribing(false)
      setError('Developer services are available in the desktop app.')
      return
    }
    const generation = gate.beginDescription()
    setDescribing(true)
    setError(null)
    void desktopDeveloperServicesClient.describe()
      .then((next) => {
        if (!gate.isCurrentDescription(generation)) return
        setDescription(next)
        setSelectedTarget((current) => next.targets.includes(current) ? current : (next.targets[0] ?? ''))
      })
      .catch((reason) => {
        if (gate.isCurrentDescription(generation)) {
          setError(reason instanceof Error ? reason.message : 'Developer tools could not be inspected.')
        }
      })
      .finally(() => {
        if (gate.isCurrentDescription(generation)) setDescribing(false)
      })
  }, [])

  useEffect(() => {
    refresh()
    window.addEventListener('hermes-desktop-ready', refresh)
    return () => {
      window.removeEventListener('hermes-desktop-ready', refresh)
      const gate = operationGate.current
      gate.invalidateDescription()
      const requestId = gate.requestCancellation()
      if (requestId) desktopDeveloperServicesClient.cancel(requestId)
    }
  }, [refresh])

  const run = useCallback(async (operation: DeveloperOperation) => {
    const gate = operationGate.current
    if (gate.active) {
      setError('Wait for the current developer operation to finish or cancel it first.')
      return
    }
    if (!canRunDeveloperOperation(description, operation, selectedTarget)) {
      setError(operationUnavailableMessage(operation))
      return
    }

    let handle
    try {
      handle = gate.start(operation, () => operation === 'build'
        ? desktopDeveloperServicesClient.build(selectedTarget, configuration)
        : desktopDeveloperServicesClient.analyze(selectedTarget, configuration))
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'The developer operation could not be started.')
      return
    }
    if (!handle) {
      setError('Wait for the current developer operation to finish or cancel it first.')
      return
    }

    const identity: DeveloperOperationIdentity = {
      requestId: handle.requestId,
      revision: handle.revision,
      operation: handle.operation,
    }
    setError(null)
    setActiveOperation(operation)
    setCancelling(false)
    onOperationStarted?.(operation)

    let retired = false
    try {
      const next = await handle.promise
      const settlement = gate.settle(next)
      retired = settlement !== 'foreign'
      if (settlement === 'accepted') setResult(next)
    } catch (reason) {
      retired = gate.fail(identity)
      if (retired) {
        setError(reason instanceof Error ? reason.message : `The ${operation} operation failed unexpectedly.`)
      }
    } finally {
      if (retired) {
        setActiveOperation(null)
        setCancelling(false)
      }
    }
  }, [configuration, description, onOperationStarted, selectedTarget])

  const cancel = useCallback(() => {
    const requestId = operationGate.current.requestCancellation()
    if (!requestId) return
    setCancelling(true)
    desktopDeveloperServicesClient.cancel(requestId)
  }, [])

  const build = useCallback(() => run('build'), [run])
  const analyze = useCallback(() => run('analyze'), [run])
  const busy = activeOperation !== null

  return {
    desktopAvailable,
    description,
    selectedTarget,
    setSelectedTarget,
    configuration,
    setConfiguration,
    describing,
    activeOperation,
    building: activeOperation === 'build',
    analyzing: activeOperation === 'analyze',
    busy,
    cancelling,
    error,
    result,
    refresh,
    build,
    analyze,
    cancel,
  }
}
