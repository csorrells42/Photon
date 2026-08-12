import { useEffect, useId, useMemo, useState, useSyncExternalStore, type KeyboardEvent, type ReactNode } from 'react'
import {
  Activity,
  Bot,
  Box,
  Boxes,
  CheckCircle2,
  CircleAlert,
  Cpu,
  Database,
  HardDrive,
  MemoryStick,
  Network,
  Play,
  RefreshCw,
  RotateCw,
  Server,
  ShieldCheck,
  Square,
  TerminalSquare,
  Workflow,
} from 'lucide-react'
import { DockerControlController } from './DockerControlController'
import type {
  DockerControlAdapter,
  DockerMutationIntent,
  DockerObservedState,
  DockerProductService,
  DockerProductServiceSnapshot,
  DockerStackSnapshot,
} from './contracts'
import './DockerControlCenter.css'

export type DockerControlCenterProps = {
  controller?: DockerControlController
  adapter?: DockerControlAdapter
}

const serviceLabels: Record<DockerProductService, string> = {
  hermes: 'Hermes',
  'memory-vector': 'Memory Vector',
  serena: 'Serena',
  'model-runner': 'Model Runner',
}

const stateLabels: Record<DockerObservedState, string> = {
  running: 'Running',
  stopped: 'Stopped',
  degraded: 'Degraded',
  unavailable: 'Unavailable',
  unknown: 'Unknown',
}

export function DockerControlCenter({ controller: suppliedController, adapter }: DockerControlCenterProps) {
  const ownedController = useMemo(() => suppliedController ?? new DockerControlController({ adapter }), [adapter, suppliedController])
  const state = useSyncExternalStore(ownedController.subscribe, ownedController.getSnapshot, ownedController.getSnapshot)
  const [confirmed, setConfirmed] = useState(false)
  const confirmationId = useId()
  const mutationBusy = state.mutationStatus !== 'idle'
  const canMutate = state.status === 'ready' && state.snapshot !== null && !mutationBusy
  const loadedModels = state.snapshot?.modelRunner?.models.filter((model) => model.loaded).length ?? 0
  const runningServices = state.snapshot?.services.filter((service) => service.state === 'running').length ?? 0
  const serviceRollupState: DockerObservedState = !state.snapshot?.services.length
    ? 'unknown'
    : runningServices === state.snapshot.services.length
      ? 'running'
      : 'degraded'

  useEffect(() => {
    if (state.status === 'idle') void ownedController.refresh()
  }, [ownedController, state.status])

  useEffect(() => {
    setConfirmed(false)
  }, [state.review?.reviewToken])

  useEffect(() => {
    if (suppliedController) return
    return () => ownedController.dispose()
  }, [ownedController, suppliedController])

  const selected = state.snapshot?.services.find((service) => service.id === state.selectedService)

  function request(intent: DockerMutationIntent) {
    setConfirmed(false)
    void ownedController.requestMutation(intent)
  }

  return (
    <section className="docker-control" aria-label="Docker Control Center" aria-busy={state.status === 'refreshing' || mutationBusy}>
      <header className="docker-control__header">
        <div className="docker-control__title-group">
          <span className="docker-control__title-icon" aria-hidden="true"><Boxes size={24} /></span>
          <div>
            <p className="docker-control__eyebrow">Docker status</p>
            <h2>Docker Control Center</h2>
            <p>Health, resources, local models, and reviewed lifecycle controls for Photon services.</p>
          </div>
        </div>
        <div className="docker-control__header-actions">
          {state.snapshot ? <div className="docker-control__snapshot"><span>Revision {state.snapshot.revision}</span><time dateTime={state.snapshot.observedAtUtc}>{state.snapshot.observedAtUtc}</time></div> : null}
          <button className="docker-control__icon-button" type="button" onClick={() => void ownedController.refresh()} disabled={state.status === 'refreshing' || state.mutationStatus === 'committing'}>
            <RefreshCw size={15} aria-hidden="true" className={state.status === 'refreshing' ? 'docker-control__spin' : undefined} />
          {state.status === 'refreshing' ? 'Refreshing…' : 'Refresh'}
          </button>
        </div>
      </header>

      <p className={`docker-control__notice docker-control__notice--${state.status}`} role={state.status === 'error' ? 'alert' : 'status'} aria-live="polite">
        {state.message}
      </p>

      {!state.snapshot ? (
        <div className="docker-control__empty">
          <span aria-hidden="true">⬡</span>
          <h3>{state.status === 'unavailable' ? 'Docker integration unavailable' : 'No Docker snapshot'}</h3>
          <p>Running, healthy, installed, or connected status is never inferred without a trusted host snapshot.</p>
        </div>
      ) : (
        <>
          <section className="docker-control__overview" aria-label="Stack identity">
            <StatusCard icon={<Server size={18} />} title="Docker engine" state={state.snapshot.engine.state} detail={state.snapshot.engine.version ?? 'Version not reported'} />
            <StatusCard icon={<Workflow size={18} />} title="Compose stack" state={state.snapshot.compose.state} detail={state.snapshot.compose.runtimeProtocol ?? 'Runtime protocol not reported'} />
            <StatusCard icon={<Activity size={18} />} title="Approved services" state={serviceRollupState} detail={`${runningServices} of ${state.snapshot.services.length} running`} />
            <StatusCard icon={<Bot size={18} />} title="Local inference" state={state.snapshot.modelRunner?.state ?? 'unavailable'} detail={`${loadedModels} model${loadedModels === 1 ? '' : 's'} loaded`} />
          </section>

          <section className="docker-control__actions" aria-label="Reviewed stack operations">
            <button type="button" disabled={!canMutate || !state.operations.startStack} onClick={() => request({ kind: 'start-stack' })}><Play size={14} aria-hidden="true" />Review stack start</button>
            <button type="button" disabled={!canMutate || !state.operations.stopStack} onClick={() => request({ kind: 'stop-stack' })}><Square size={13} aria-hidden="true" />Review stack stop</button>
            <button
              type="button"
              disabled={!canMutate || !state.operations.update}
              title={!state.operations.update && state.updateReason === 'derived-runtime-updater-not-integrated' ? 'The trusted Docker updater is not integrated.' : undefined}
              onClick={() => request({ kind: 'request-update' })}
            >
              <Workflow size={14} aria-hidden="true" />{state.operations.update ? 'Review update workflow' : 'Update workflow unavailable'}
            </button>
          </section>

          <div className="docker-control__columns">
            <section className="docker-control__services" aria-label="Approved services">
              <h3>Approved services</h3>
              {state.snapshot.services.length ? (
                <div className="docker-control__service-tabs" role="tablist" aria-label="Docker services">
                  {state.snapshot.services.map((service, index, services) => (
                    <button
                      key={service.id}
                      id={`docker-service-tab-${service.id}`}
                      type="button"
                      role="tab"
                      aria-selected={service.id === state.selectedService}
                      aria-controls={`docker-service-panel-${service.id}`}
                      tabIndex={service.id === state.selectedService ? 0 : -1}
                      onClick={() => ownedController.selectService(service.id)}
                      onKeyDown={(event) => handleServiceKey(event, index, services, ownedController)}
                    >
                      <span className="docker-control__service-label"><ServiceIcon service={service.id} /><span><strong>{serviceLabels[service.id]}</strong><small>{service.health}</small></span></span>
                      <StatePill state={service.state} />
                    </button>
                  ))}
                </div>
              ) : <p className="docker-control__missing">No approved service evidence was returned.</p>}
            </section>

            <section
              className="docker-control__service-detail"
              role="tabpanel"
              id={`docker-service-panel-${state.selectedService}`}
              aria-labelledby={`docker-service-tab-${state.selectedService}`}
              tabIndex={0}
            >
              {selected ? (
                <ServiceDetail
                  service={selected}
                  canStart={canMutate && state.operations.startService && selected.manageable === true}
                  canStop={canMutate && state.operations.stopService && selected.manageable === true}
                  canRestart={canMutate && state.operations.restartService && selected.manageable === true}
                  onStart={() => request({ kind: 'start-service', service: selected.id })}
                  onStop={() => request({ kind: 'stop-service', service: selected.id })}
                  onRestart={() => request({ kind: 'restart-service', service: selected.id })}
                />
              ) : <p className="docker-control__missing">No selected service snapshot.</p>}
            </section>
          </div>

          <section className="docker-control__evidence" aria-label="Persistence and provenance">
            <div>
              <h3>Persistent volumes</h3>
              {state.snapshot.volumes.length ? state.snapshot.volumes.map((volume) => (
                <p key={volume.role}><strong>{volume.role === 'data' ? 'Data' : 'Workspace'}:</strong> {volume.state} · {volume.persistent ? 'persistent' : 'not attested persistent'}</p>
              )) : <p className="docker-control__missing">No volume evidence reported.</p>}
            </div>
            <div>
              <h3>Compose identity</h3>
              <p><strong>Definition:</strong> {state.snapshot.compose.definitionFingerprint ?? 'Not reported'}</p>
              <p><strong>Upstream revision:</strong> {state.snapshot.compose.upstreamRevision ?? 'Not reported'}</p>
            </div>
            <div>
              <h3>Last update / rollback</h3>
              {state.snapshot.lastWorkflow
                ? <p><strong>{state.snapshot.lastWorkflow.kind} · {state.snapshot.lastWorkflow.state}</strong><br />{state.snapshot.lastWorkflow.summary}</p>
                : <p className="docker-control__missing">No workflow receipt reported.</p>}
            </div>
          </section>

          <ModelRunnerPanel
            snapshot={state.snapshot.modelRunner}
            canMutate={canMutate && state.operations.unloadModel}
            onUnload={(model) => request({ kind: 'unload-model', model })}
          />

          <section className="docker-control__logs" aria-label="Bounded service logs">
            <div className="docker-control__section-heading">
              <div><h3>{serviceLabels[state.selectedService]} logs</h3><p>Bounded and redacted by the trusted host and projected again before display.</p></div>
              <button type="button" disabled={!selected || state.logsStatus === 'loading'} onClick={() => void ownedController.readLogs()}>
                {state.logsStatus === 'loading' ? 'Loading…' : 'Load bounded logs'}
              </button>
            </div>
            {state.logs.length ? (
              <ol className="docker-control__log-lines" aria-label="Redacted Docker log lines">
                {state.logs.map((entry, index) => <li key={`${entry.timestampUtc ?? 'no-time'}-${index}`}><span>{entry.stream}</span><code>{entry.text}</code></li>)}
              </ol>
            ) : <p className="docker-control__missing">No logs loaded.</p>}
            {state.logsTruncated && <p className="docker-control__truncated" role="status">Log output was truncated to the safe display limit.</p>}
          </section>
        </>
      )}

      {state.review && (
        <section className="docker-control__review" role="dialog" aria-modal="false" aria-labelledby={`${confirmationId}-title`}>
          <p className="docker-control__eyebrow">Review before mutation</p>
          <h3 id={`${confirmationId}-title`}>{state.review.summary}</h3>
          <p>Exact affected services:</p>
          <ul>{state.review.affectedServices.map((service) => <li key={service}>{serviceLabels[service]}</li>)}</ul>
          {state.review.warnings.length > 0 && <ul className="docker-control__warnings">{state.review.warnings.map((warning, index) => <li key={index}>{warning}</li>)}</ul>}
          <dl>
            <div><dt>Snapshot revision</dt><dd>{state.review.snapshotRevision}</dd></div>
            <div><dt>Review fingerprint</dt><dd><code>{state.review.fingerprint}</code></dd></div>
            <div><dt>Expires</dt><dd>{state.review.expiresAtUtc}</dd></div>
          </dl>
          <label className="docker-control__confirm">
            <input type="checkbox" checked={confirmed} onChange={(event) => setConfirmed(event.currentTarget.checked)} />
            I reviewed the exact services and operation above.
          </label>
          <div className="docker-control__review-actions">
            <button type="button" onClick={() => ownedController.cancelReview()}>Cancel</button>
            <button type="button" className="docker-control__primary" disabled={!confirmed} onClick={() => void ownedController.commitReviewed()}>Apply reviewed operation</button>
          </div>
        </section>
      )}

      <footer className="docker-control__boundary">
        No generic Docker command surface is exposed. Environment values, Docker credentials, mounted secret contents, arbitrary commands, and unrestricted logs never enter this view.
      </footer>
    </section>
  )
}

function StatusCard({ icon, title, state, detail }: { icon: ReactNode; title: string; state: DockerObservedState; detail: string }) {
  return <article className="docker-control__status-card"><span className="docker-control__status-icon" aria-hidden="true">{icon}</span><span>{title}</span><strong>{stateLabels[state]}</strong><small>{detail}</small></article>
}

function StatePill({ state }: { state: DockerObservedState }) {
  const Icon = state === 'running' ? CheckCircle2 : state === 'degraded' || state === 'unknown' ? CircleAlert : Square
  return <span className={`docker-control__pill docker-control__pill--${state}`}><Icon size={11} aria-hidden="true" />{stateLabels[state]}</span>
}

function ServiceIcon({ service }: { service: DockerProductService }) {
  const Icon = service === 'hermes' ? Box : service === 'memory-vector' ? Database : service === 'serena' ? ShieldCheck : Bot
  return <span className="docker-control__service-icon" aria-hidden="true"><Icon size={17} /></span>
}

function ServiceDetail({
  service,
  canStart,
  canStop,
  canRestart,
  onStart,
  onStop,
  onRestart,
}: {
  service: DockerProductServiceSnapshot
  canStart: boolean
  canStop: boolean
  canRestart: boolean
  onStart(): void
  onStop(): void
  onRestart(): void
}) {
  return (
    <>
      <div className="docker-control__section-heading">
        <div className="docker-control__detail-title"><ServiceIcon service={service.id} /><div><p className="docker-control__eyebrow">Approved service</p><h3>{serviceLabels[service.id]}</h3></div></div>
        <div className="docker-control__service-actions">
          <button type="button" disabled={!canStart || service.state === 'running'} onClick={onStart}><Play size={13} aria-hidden="true" />Review start</button>
          <button type="button" disabled={!canStop || service.state === 'stopped' || service.state === 'unavailable'} onClick={onStop}><Square size={12} aria-hidden="true" />Review stop</button>
          <button type="button" disabled={!canRestart || (service.state !== 'running' && service.state !== 'degraded')} onClick={onRestart}><RotateCw size={13} aria-hidden="true" />Review restart</button>
        </div>
      </div>
      <dl className="docker-control__facts">
        <div><dt>State</dt><dd>{stateLabels[service.state]}</dd></div>
        <div><dt>Health</dt><dd>{service.health}</dd></div>
        <div><dt>Managed here</dt><dd>{service.manageable ? 'Yes' : 'No'}</dd></div>
        <div><dt>Container ID</dt><dd><code>{service.containerId ?? 'Not running'}</code></dd></div>
        <div><dt>Version</dt><dd>{service.version ?? 'Not reported'}</dd></div>
        <div><dt>Image verification</dt><dd>{service.image?.verification ?? 'Not reported'}</dd></div>
        <div><dt>Exact image ID</dt><dd><code>{service.image?.imageId ?? 'Not reported'}</code></dd></div>
        <div><dt>Approved digest</dt><dd><code>{service.image?.approvedDigest ?? 'Not reported'}</code></dd></div>
        <div><dt>OCI revision</dt><dd><code>{service.image?.ociRevision ?? 'Not reported'}</code></dd></div>
      </dl>
      <h4>Loopback ports</h4>
      {service.ports.length ? <ul>{service.ports.map((port) => <li key={`${port.address}-${port.hostPort}-${port.containerPort}-${port.protocol}`}><code>{port.address}:{port.hostPort}</code> → {port.containerPort}/{port.protocol}</li>)}</ul> : <p className="docker-control__missing">No loopback ports reported.</p>}
      <h4>Current resources</h4>
      {service.resources ? (
        <dl className="docker-control__facts docker-control__facts--resources">
          <ResourceFact icon={<Cpu size={14} />} label="CPU" value={service.resources.cpuPercent === undefined ? 'Not reported' : `${service.resources.cpuPercent.toFixed(2)}%`} percent={service.resources.cpuPercent} />
          <ResourceFact icon={<MemoryStick size={14} />} label="Memory" value={`${service.resources.memoryUsage ?? 'Not reported'}${service.resources.memoryLimit ? ` / ${service.resources.memoryLimit}` : ''}`} percent={service.resources.memoryPercent} />
          <div><dt><Network size={12} aria-hidden="true" />Network I/O</dt><dd>{service.resources.networkIo ?? 'Not reported'}</dd></div>
          <div><dt><HardDrive size={12} aria-hidden="true" />Block I/O</dt><dd>{service.resources.blockIo ?? 'Not reported'}</dd></div>
          <div><dt>PIDs</dt><dd>{service.resources.pids ?? 'Not reported'}</dd></div>
        </dl>
      ) : <p className="docker-control__missing">Resource telemetry is unavailable for this service.</p>}
    </>
  )
}

function ResourceFact({ icon, label, value, percent }: { icon: ReactNode; label: string; value: string; percent?: number }) {
  const bounded = percent === undefined ? 0 : Math.min(100, Math.max(0, percent))
  return <div className="docker-control__resource-fact"><dt>{icon}{label}</dt><dd>{value}</dd>{percent === undefined ? null : <div className="docker-control__meter" aria-label={`${label} utilization ${percent.toFixed(2)} percent`} role="meter" aria-valuemin={0} aria-valuemax={100} aria-valuenow={bounded}><span style={{ width: `${bounded}%` }} /></div>}</div>
}

function ModelRunnerPanel({
  snapshot,
  canMutate,
  onUnload,
}: {
  snapshot: DockerStackSnapshot['modelRunner']
  canMutate: boolean
  onUnload(model: string): void
}) {
  return (
    <section className="docker-control__model-runner" aria-label="Docker Model Runner">
      <div className="docker-control__section-heading">
        <div>
          <p className="docker-control__eyebrow">Local inference authority</p>
          <h3>Docker Model Runner</h3>
          <p>{snapshot?.message ?? 'No Docker Model Runner evidence was returned.'}</p>
        </div>
        <StatePill state={snapshot?.state ?? 'unavailable'} />
      </div>
      {snapshot ? (
        <>
          <dl className="docker-control__facts">
            <div><dt>Version</dt><dd>{snapshot.version ?? 'Not reported'}</dd></div>
            <div><dt>Runtime kind</dt><dd>{snapshot.kind ?? 'Not reported'}</dd></div>
            <div><dt>Loopback endpoint</dt><dd><code>{snapshot.endpoint ?? 'Not reported'}</code></dd></div>
            <div><dt>Model disk usage</dt><dd>{snapshot.diskUsage ?? 'Not reported'}</dd></div>
          </dl>
          <div className="docker-control__model-authority">
            <button type="button" disabled title="This Docker CLI exposes pull/run, not a bounded load-only command. Loading remains unavailable here.">Load model unavailable</button>
            <span><Bot size={14} aria-hidden="true" />{snapshot.models.filter((model) => model.loaded).length} loaded / {snapshot.models.length} local</span>
          </div>
          {snapshot.models.length ? (
            <div className="docker-control__models">
              {snapshot.models.map((model) => (
                <article key={`${model.reference}-${model.modelId ?? 'unknown'}`}>
                  <div>
                    <strong>{model.reference}</strong>
                    <small>{[model.parameters, model.format, model.size, model.backend, model.mode].filter(Boolean).join(' · ') || 'No model details reported'}</small>
                    <code>{model.modelId ?? 'Model digest not reported'}</code>
                  </div>
                  <div className="docker-control__model-state">
                    <span>{model.loaded ? 'Loaded' : 'Local'}</span>
                    <button type="button" disabled={!model.loaded || !snapshot.unloadAvailable || !canMutate} onClick={() => onUnload(model.reference)}>Review unload</button>
                  </div>
                </article>
              ))}
            </div>
          ) : <p className="docker-control__missing">No local models reported.</p>}
        </>
      ) : <p className="docker-control__missing">Model Runner inventory is unavailable.</p>}
    </section>
  )
}

export function nextDockerServiceIndex(current: number, count: number, key: string): number | null {
  if (!Number.isSafeInteger(current) || current < 0 || current >= count || count <= 0) return null
  if (key === 'Home') return 0
  if (key === 'End') return count - 1
  if (key === 'ArrowRight' || key === 'ArrowDown') return (current + 1) % count
  if (key === 'ArrowLeft' || key === 'ArrowUp') return (current - 1 + count) % count
  return null
}

function handleServiceKey(
  event: KeyboardEvent<HTMLButtonElement>,
  current: number,
  services: readonly DockerProductServiceSnapshot[],
  controller: DockerControlController,
) {
  const next = nextDockerServiceIndex(current, services.length, event.key)
  if (next === null) return
  event.preventDefault()
  controller.selectService(services[next].id)
  const owner = event.currentTarget.parentElement
  owner?.querySelectorAll<HTMLButtonElement>('[role="tab"]')[next]?.focus()
}
