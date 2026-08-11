export const AGENT_SCROLL_BOTTOM_THRESHOLD = 96

export type AgentScrollMetrics = { scrollHeight: number; scrollTop: number; clientHeight: number }
export type AgentScrollState = { following: boolean; lastScrollHeight: number }

export function agentScrollDistanceFromBottom(metrics: AgentScrollMetrics) {
  return Math.max(0, metrics.scrollHeight - metrics.scrollTop - metrics.clientHeight)
}

export function isAgentScrollNearBottom(metrics: AgentScrollMetrics, threshold = AGENT_SCROLL_BOTTOM_THRESHOLD) {
  return agentScrollDistanceFromBottom(metrics) <= Math.max(0, threshold)
}

export function createAgentScrollState(metrics: AgentScrollMetrics): AgentScrollState {
  return { following: isAgentScrollNearBottom(metrics), lastScrollHeight: metrics.scrollHeight }
}

export function updateAgentScrollState(state: AgentScrollState, metrics: AgentScrollMetrics): AgentScrollState {
  return { following: isAgentScrollNearBottom(metrics), lastScrollHeight: metrics.scrollHeight }
}

/** Whether a new transcript event should scroll to the exact latest position. */
export function shouldFollowAgentScroll(state: AgentScrollState) { return state.following }

/** A deterministic, non-animated latest position. */
export function agentScrollLatestTop(metrics: AgentScrollMetrics) { return Math.max(0, metrics.scrollHeight - metrics.clientHeight) }

/**
 * Keeps a reader's viewport stable when a known element above it changes height.
 * The caller supplies the actual measured delta so streaming content below the viewport
 * never shifts a reader upward.
 */
export function preserveAgentScrollAnchor(scrollTop: number, heightDelta: number) {
  return Math.max(0, scrollTop + heightDelta)
}
