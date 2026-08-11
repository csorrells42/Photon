import { describe, expect, it } from 'vitest'
import { AGENT_SCROLL_BOTTOM_THRESHOLD, agentScrollDistanceFromBottom, agentScrollLatestTop, createAgentScrollState, isAgentScrollNearBottom, preserveAgentScrollAnchor, shouldFollowAgentScroll, updateAgentScrollState } from './AgentScroll'

describe('AgentScroll', () => {
  it('recognizes exact bottom and threshold boundary', () => {
    expect(agentScrollDistanceFromBottom({ scrollHeight: 1000, scrollTop: 600, clientHeight: 400 })).toBe(0)
    expect(isAgentScrollNearBottom({ scrollHeight: 1000, scrollTop: 600 - AGENT_SCROLL_BOTTOM_THRESHOLD, clientHeight: 400 })).toBe(true)
  })

  it('suspends following above the threshold and tolerates browser overscroll', () => {
    const above = { scrollHeight: 1000, scrollTop: 599 - AGENT_SCROLL_BOTTOM_THRESHOLD, clientHeight: 400 }
    expect(isAgentScrollNearBottom(above)).toBe(false)
    expect(createAgentScrollState(above).following).toBe(false)
    expect(isAgentScrollNearBottom({ scrollHeight: 1000, scrollTop: 650, clientHeight: 400 })).toBe(true)
  })

  it('follows appends only while the user was already following', () => {
    const following = createAgentScrollState({ scrollHeight: 1000, scrollTop: 600, clientHeight: 400 })
    const reading = createAgentScrollState({ scrollHeight: 1000, scrollTop: 300, clientHeight: 400 })
    expect(shouldFollowAgentScroll(following)).toBe(true)
    expect(shouldFollowAgentScroll(reading)).toBe(false)
  })

  it('preserves the visible reading anchor for height changes above the viewport', () => {
    expect(preserveAgentScrollAnchor(300, 120)).toBe(420)
    expect(preserveAgentScrollAnchor(420, -120)).toBe(300)
    expect(preserveAgentScrollAnchor(10, -100)).toBe(0)
  })

  it('jumps explicitly to latest and resumes following after observer-sized content changes', () => {
    const latest = agentScrollLatestTop({ scrollHeight: 1500, scrollTop: 300, clientHeight: 400 })
    expect(latest).toBe(1100)
    const afterJump = updateAgentScrollState(createAgentScrollState({ scrollHeight: 1000, scrollTop: 200, clientHeight: 400 }), { scrollHeight: 1500, scrollTop: latest, clientHeight: 400 })
    expect(afterJump.following).toBe(true)
    expect(afterJump.lastScrollHeight).toBe(1500)
  })
})
