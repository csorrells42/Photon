import type { GitChangeEntry, GitStatusGroups } from './contracts'

const groupOrder: Array<{ key: keyof GitStatusGroups; label: string }> = [
  { key: 'conflicted', label: 'Conflicted' },
  { key: 'staged', label: 'Staged' },
  { key: 'unstaged', label: 'Unstaged' },
  { key: 'untracked', label: 'Untracked' },
  { key: 'renamed', label: 'Renamed' },
  { key: 'deleted', label: 'Deleted' },
  { key: 'submodules', label: 'Submodules' },
]

function ChangeRow({ entry }: { entry: GitChangeEntry }) {
  return (
    <li className="source-control-change">
      <span className={`source-control-change__kind source-control-change__kind--${entry.kind}`} aria-hidden="true">
        {entry.kind === 'untracked' ? '?' : entry.kind === 'deleted' ? '−' : entry.kind === 'renamed' ? 'R' : 'M'}
      </span>
      <span className="source-control-change__path" title={entry.path}>{entry.path}</span>
      {entry.originalPath && <span className="source-control-change__original">from {entry.originalPath}</span>}
    </li>
  )
}

export function ChangesView({ groups }: { groups: GitStatusGroups }) {
  return (
    <div className="source-control-groups" aria-label="Repository changes">
      {groupOrder.map(({ key, label }) => {
        const entries = groups[key]
        return (
          <section className="source-control-group" key={key} aria-labelledby={`source-control-${key}`}>
            <h3 id={`source-control-${key}`}>
              <span>{label}</span>
              <span className="source-control-group__count" aria-label={`${entries.length} changes`}>{entries.length}</span>
            </h3>
            {entries.length > 0
              ? <ul>{entries.map((entry, index) => <ChangeRow entry={entry} key={`${entry.path}\0${entry.originalPath ?? ''}\0${index}`} />)}</ul>
              : <p className="source-control-group__empty">No {label.toLocaleLowerCase()} changes</p>}
          </section>
        )
      })}
    </div>
  )
}
