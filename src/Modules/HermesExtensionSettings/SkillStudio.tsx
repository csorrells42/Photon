import { useEffect, useMemo, useState, type MouseEvent } from 'react'
import type { ExtensionWriteIntent, HermesExtensionSettingsSnapshot, SkillDocument } from './contracts'

interface SkillStudioProps {
  snapshot: HermesExtensionSettingsSnapshot
  busy: boolean
  onPreview(intent: ExtensionWriteIntent, source: HTMLElement): Promise<void>
}

type SkillDraft = {
  name: string
  description: string
  content: string
}

const emptyDraft: SkillDraft = { name: '', description: '', content: '# New skill\n\n' }

function draftFromSkill(skill: SkillDocument): SkillDraft {
  return { name: skill.name, description: skill.description, content: skill.content }
}

export function SkillStudio({ snapshot, busy, onPreview }: SkillStudioProps) {
  const [selectedId, setSelectedId] = useState<string | null>(snapshot.skills[0]?.id ?? null)
  const selected = useMemo(() => snapshot.skills.find((skill) => skill.id === selectedId) ?? null, [selectedId, snapshot.skills])
  const [draft, setDraft] = useState<SkillDraft>(() => selected ? draftFromSkill(selected) : emptyDraft)
  const [cloneIntent, setCloneIntent] = useState(false)

  useEffect(() => {
    setDraft(selected ? draftFromSkill(selected) : emptyDraft)
    setCloneIntent(false)
  }, [selected])

  const newSkill = () => {
    setSelectedId(null)
    setDraft(emptyDraft)
    setCloneIntent(false)
  }

  const preview = async (event: MouseEvent<HTMLButtonElement>) => {
    const intent: ExtensionWriteIntent = selected
      ? selected.provenance === 'user-created' && selected.editable
        ? { kind: 'edit', skillId: selected.id, ...draft }
        : { kind: 'clone-to-user', sourceSkillId: selected.id, ...draft }
      : { kind: 'create', ...draft }
    await onPreview(intent, event.currentTarget)
  }

  const upstream = selected && (selected.provenance !== 'user-created' || !selected.editable)

  return (
    <section className="hes-panel" aria-labelledby="hes-skills-heading">
      <header className="hes-panel-heading">
        <div>
          <small>SKILL STUDIO</small>
          <h2 id="hes-skills-heading">Read, author, and safely clone skills</h2>
          <p>Content is rendered as text. Every save requires a separate review and confirmed commit.</p>
        </div>
        <button type="button" onClick={newSkill} disabled={busy}>New user skill</button>
      </header>

      <div className="hes-provenance-note">
        <strong>Provenance is descriptive, not transferable approval.</strong>
        <span>A <b>nous-approved</b> catalog label never implies approval by Chris or Codex.</span>
      </div>

      <div className="hes-skill-layout">
        <nav aria-label="Available skills" className="hes-skill-list">
          {snapshot.skills.map((skill) => (
            <button
              type="button"
              key={skill.id}
              className={selectedId === skill.id ? 'selected' : ''}
              aria-pressed={selectedId === skill.id}
              onClick={() => setSelectedId(skill.id)}
            >
              <strong>{skill.name}</strong>
              <span className={`hes-provenance ${skill.provenance}`}>{skill.provenance}</span>
              <small>{skill.description || 'No description supplied.'}</small>
            </button>
          ))}
        </nav>

        <div className="hes-skill-editor">
          <div className="hes-field-grid">
            <label>
              <span>Name</span>
              <input
                value={draft.name}
                maxLength={256}
                onChange={(event) => setDraft((current) => ({ ...current, name: event.target.value }))}
              />
            </label>
            <label>
              <span>Description</span>
              <input
                value={draft.description}
                maxLength={2_048}
                onChange={(event) => setDraft((current) => ({ ...current, description: event.target.value }))}
              />
            </label>
          </div>
          <label>
            <span>Skill content</span>
            <textarea
              className="hes-content-editor"
              value={draft.content}
              maxLength={256_000}
              onChange={(event) => setDraft((current) => ({ ...current, content: event.target.value }))}
            />
          </label>
          <section className="hes-text-preview" aria-label="Skill content preview">
            <header><strong>Text preview</strong><small>{draft.content.length.toLocaleString()} characters</small></header>
            <pre>{draft.content || '(empty)'}</pre>
          </section>
          {upstream ? (
            <label className="hes-clone-confirm">
              <input type="checkbox" checked={cloneIntent} onChange={(event) => setCloneIntent(event.target.checked)} />
              Clone this {selected.provenance} skill into a separate user-created skill. The source will not be overwritten.
            </label>
          ) : null}
          <div className="hes-actions">
            <button
              type="button"
              className="hes-primary"
              disabled={busy || !draft.name.trim() || !draft.content.trim() || Boolean(upstream && !cloneIntent)}
              onClick={(event) => void preview(event)}
            >
              Preview {upstream ? 'clone' : selected ? 'edit' : 'create'}
            </button>
          </div>
        </div>
      </div>
    </section>
  )
}
