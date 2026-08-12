import { describe, expect, it } from 'vitest'
import { extractWorkspaceOutline } from './WorkspaceOutline'

describe('workspace outline extraction', () => {
  it('finds bounded TypeScript declarations without evaluating source', () => {
    const symbols = extractWorkspaceOutline('src/example.ts', `export interface Item {}\nexport class Store {}\nexport async function load() {}\nconst save = async () => {}`)
    expect(symbols.map(({ name, kind, line }) => ({ name, kind, line }))).toEqual([
      { name: 'Item', kind: 'interface', line: 1 },
      { name: 'Store', kind: 'class', line: 2 },
      { name: 'load', kind: 'function', line: 3 },
      { name: 'save', kind: 'function', line: 4 },
    ])
  })

  it('finds Python and C# declarations', () => {
    expect(extractWorkspaceOutline('agent.py', 'class Agent:\n  async def run(self):\n    pass').map((item) => item.name)).toEqual(['Agent', 'run'])
    expect(extractWorkspaceOutline('Program.cs', 'public sealed class Program {\n  public static async Task Main() {\n  }\n}').map((item) => item.name)).toEqual(['Program', 'Main'])
  })

  it('caps adversarially large symbol lists', () => {
    const content = Array.from({ length: 500 }, (_, index) => `function item${index}() {}`).join('\n')
    expect(extractWorkspaceOutline('many.js', content)).toHaveLength(250)
  })
})
