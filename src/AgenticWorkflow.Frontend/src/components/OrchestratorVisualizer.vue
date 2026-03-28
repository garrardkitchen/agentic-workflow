<script setup lang="ts">
import { computed } from 'vue'
import type { AgentNode, OrchestratorState } from '../types'

const props = defineProps<{
  agents: AgentNode[]
  state: OrchestratorState
}>()

const nodePositions = {
  user: { x: 70, y: 150 },
  agents: [
    { x: 210, y: 40 },
    { x: 210, y: 150 },
    { x: 210, y: 260 },
  ],
  evaluator: { x: 370, y: 150 },
}

const nodeW = 110
const nodeH = 50
const nodeR = 8

function statusColor(status: string) {
  switch (status) {
    case 'running': return 'var(--accent-blue)'
    case 'complete': return 'var(--accent-green)'
    case 'failed': return 'var(--accent-red)'
    case 'winner': return 'var(--accent-amber)'
    default: return 'var(--text-secondary)'
  }
}

function userToAgentEdge(agentIndex: number) {
  const agent = props.agents[agentIndex]
  if (!agent) return { cls: 'edge-idle', color: '', glow: false }
  if (agent.status === 'running')
    return { cls: 'edge-active', color: 'var(--accent-blue)', glow: true }
  if (agent.status === 'complete' || agent.status === 'winner')
    return { cls: 'edge-done', color: 'var(--accent-green)', glow: false }
  if (agent.status === 'failed')
    return { cls: 'edge-failed', color: 'var(--accent-red)', glow: false }
  return { cls: 'edge-idle', color: '', glow: false }
}

function agentToEvalEdge(agentIndex: number) {
  const agent = props.agents[agentIndex]
  if (!agent) return { cls: 'edge-idle', color: '', glow: false }
  const isEvalPhase = props.state === 'evaluating' || props.state === 'awaiting-approval' || props.state === 'accepted'
  if (agent.status === 'winner')
    return { cls: 'edge-winner', color: 'var(--accent-amber)', glow: true }
  if (agent.status === 'complete' && isEvalPhase)
    return { cls: 'edge-done', color: 'var(--accent-green)', glow: false }
  if (agent.status === 'complete' && props.state === 'fan-out')
    return { cls: 'edge-active', color: 'var(--accent-green)', glow: true }
  if (agent.status === 'failed')
    return { cls: 'edge-failed', color: 'var(--accent-red)', glow: false }
  return { cls: 'edge-idle', color: '', glow: false }
}

function evalToUserEdge() {
  if (props.state === 'awaiting-approval')
    return { cls: 'edge-active', color: 'var(--accent-purple)', glow: true }
  if (props.state === 'accepted')
    return { cls: 'edge-done', color: 'var(--accent-green)', glow: false }
  return { cls: 'edge-idle', color: '', glow: false }
}

const evaluatorColor = computed(() => {
  if (props.state === 'evaluating') return 'var(--accent-amber)'
  if (props.state === 'awaiting-approval' || props.state === 'accepted') return 'var(--accent-green)'
  return 'var(--text-secondary)'
})

// Edge connection points (right side of source → left side of target)
function edgeFrom(pos: { x: number; y: number }) {
  return { x: pos.x + nodeW / 2, y: pos.y }
}
function edgeTo(pos: { x: number; y: number }) {
  return { x: pos.x - nodeW / 2, y: pos.y }
}
</script>

<template>
  <div class="visualizer glass-card">
    <div class="viz-header">
      <i class="pi pi-sitemap"></i>
      <span>Orchestration</span>
      <span :class="['viz-badge', state]">{{ state }}</span>
    </div>
    <svg viewBox="0 0 480 310" class="viz-svg">
      <defs>
        <filter id="glow-edge">
          <feGaussianBlur stdDeviation="3" result="blur" />
          <feMerge>
            <feMergeNode in="blur" />
            <feMergeNode in="SourceGraphic" />
          </feMerge>
        </filter>
      </defs>

      <!-- Edges: User → Agents -->
      <line v-for="(pos, i) in nodePositions.agents" :key="'e1-'+i"
        :x1="edgeFrom(nodePositions.user).x" :y1="edgeFrom(nodePositions.user).y"
        :x2="edgeTo(pos).x" :y2="edgeTo(pos).y"
        :class="userToAgentEdge(i).cls"
        :style="userToAgentEdge(i).color ? { stroke: userToAgentEdge(i).color } : {}"
        :filter="userToAgentEdge(i).glow ? 'url(#glow-edge)' : ''"
        stroke-width="2" />

      <!-- Edges: Agents → Evaluator -->
      <line v-for="(pos, i) in nodePositions.agents" :key="'e2-'+i"
        :x1="edgeFrom(pos).x" :y1="edgeFrom(pos).y"
        :x2="edgeTo(nodePositions.evaluator).x" :y2="edgeTo(nodePositions.evaluator).y"
        :class="agentToEvalEdge(i).cls"
        :style="agentToEvalEdge(i).color ? { stroke: agentToEvalEdge(i).color } : {}"
        :filter="agentToEvalEdge(i).glow ? 'url(#glow-edge)' : ''"
        stroke-width="2" />

      <!-- Edge: Evaluator → User (curved below) -->
      <path
        :d="`M ${edgeFrom(nodePositions.evaluator).x} ${nodePositions.evaluator.y + nodeH/2}
             C ${edgeFrom(nodePositions.evaluator).x + 20} ${nodePositions.evaluator.y + 80},
               ${edgeTo(nodePositions.user).x - 20} ${nodePositions.user.y + 80},
               ${edgeTo(nodePositions.user).x} ${nodePositions.user.y + nodeH/2}`"
        fill="none"
        :class="evalToUserEdge().cls"
        :style="evalToUserEdge().color ? { stroke: evalToUserEdge().color } : {}"
        :filter="evalToUserEdge().glow ? 'url(#glow-edge)' : ''"
        stroke-width="2" />

      <!-- User Node -->
      <g :transform="`translate(${nodePositions.user.x - nodeW/2}, ${nodePositions.user.y - nodeH/2})`">
        <rect :width="nodeW" :height="nodeH" :rx="nodeR" fill="var(--bg-secondary)" stroke="var(--accent-purple)" stroke-width="2" />
        <text :x="nodeW/2" :y="nodeH/2 + 1" text-anchor="middle" dominant-baseline="middle" fill="var(--accent-purple)" font-size="12" font-weight="600">
          👤 You
        </text>
      </g>

      <!-- Agent Nodes -->
      <g v-for="(agent, i) in agents" :key="agent.name"
        :transform="`translate(${nodePositions.agents[i].x - nodeW/2}, ${nodePositions.agents[i].y - nodeH/2})`">
        <rect :width="nodeW" :height="nodeH" :rx="nodeR" fill="var(--bg-secondary)"
          :stroke="statusColor(agent.status)" stroke-width="2.5"
          :class="{ 'animate-pulse-glow': agent.status === 'running' }" />
        <text :x="nodeW/2" :y="nodeH/2 - 7" text-anchor="middle" dominant-baseline="middle"
          :fill="statusColor(agent.status)" font-size="10" font-weight="600">
          {{ agent.name.replace('Agent-', '') }}
        </text>
        <text :x="nodeW/2" :y="nodeH/2 + 7" text-anchor="middle" dominant-baseline="middle"
          fill="var(--text-secondary)" font-size="8" class="mono">
          {{ agent.model }}
        </text>
        <!-- Elapsed time below node -->
        <text v-if="agent.elapsedMs" :x="nodeW/2" :y="nodeH + 12" text-anchor="middle"
          fill="var(--text-secondary)" font-size="8">
          {{ agent.elapsedMs }}ms
        </text>
        <!-- Winner glow -->
        <rect v-if="agent.status === 'winner'" :x="-4" :y="-4" :width="nodeW + 8" :height="nodeH + 8"
          :rx="nodeR + 2" fill="none" stroke="var(--accent-amber)" stroke-width="2" opacity="0.5"
          class="animate-burst" />
      </g>

      <!-- Evaluator Node -->
      <g :transform="`translate(${nodePositions.evaluator.x - nodeW/2}, ${nodePositions.evaluator.y - nodeH/2})`">
        <rect :width="nodeW" :height="nodeH" :rx="nodeR" fill="var(--bg-secondary)"
          :stroke="evaluatorColor" stroke-width="2.5"
          :class="{ 'animate-pulse-glow': state === 'evaluating' }" />
        <text :x="nodeW/2" :y="nodeH/2 + 1" text-anchor="middle" dominant-baseline="middle"
          :fill="evaluatorColor" font-size="12" font-weight="600">
          ⚖️ Evaluator
        </text>
      </g>
    </svg>
  </div>
</template>

<style scoped>
.visualizer {
  padding: 1rem;
  min-height: 200px;
}

.viz-header {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-bottom: 0.5rem;
  font-size: 0.85rem;
  color: var(--text-secondary);
}

.viz-badge {
  margin-left: auto;
  padding: 0.15rem 0.5rem;
  border-radius: 6px;
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.viz-badge.idle { background: rgba(255,255,255,0.05); color: var(--text-secondary); }
.viz-badge.fan-out { background: rgba(59,130,246,0.15); color: var(--accent-blue); }
.viz-badge.evaluating { background: rgba(245,158,11,0.15); color: var(--accent-amber); }
.viz-badge.awaiting-input { background: rgba(139,92,246,0.15); color: var(--accent-purple); }
.viz-badge.awaiting-approval { background: rgba(139,92,246,0.15); color: var(--accent-purple); }
.viz-badge.accepted { background: rgba(16,185,129,0.15); color: var(--accent-green); }
.viz-badge.declined { background: rgba(239,68,68,0.15); color: var(--accent-red); }
.viz-badge.failed { background: rgba(239,68,68,0.15); color: var(--accent-red); }

.viz-svg {
  width: 100%;
  max-height: 290px;
}

.edge-idle {
  stroke: rgba(255,255,255,0.08);
}

.edge-active {
  stroke-dasharray: 8 4;
  animation: flow-dash 1s linear infinite;
}

.edge-done {
  stroke-dasharray: none;
  opacity: 0.8;
}

.edge-failed {
  stroke-dasharray: 4 4;
  opacity: 0.4;
}

.edge-winner {
  stroke-dasharray: 10 4;
  stroke-width: 3px;
  animation: flow-dash 0.6s linear infinite;
}
</style>

<!-- Unscoped for SVG keyframes -->
<style>
@keyframes flow-dash {
  to { stroke-dashoffset: -20; }
}
</style>
