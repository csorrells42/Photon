import { randomUUID } from 'node:crypto'
import { readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs'
import { resolve } from 'node:path'

import { buildPortableArtifact } from 'file:///C:/Users/clsor/.codex/plugins/cache/openai-curated-remote/data-analytics/0.2.8-13ceeea1f599/skills/build-report/scripts/build_portable_artifact.mjs'
import { extractPortableChartSvgs } from 'file:///C:/Users/clsor/.codex/plugins/cache/openai-curated-remote/data-analytics/0.2.8-13ceeea1f599/skills/build-report/scripts/extract_portable_chart_svgs.mjs'
import { verifyPortableArtifact } from 'file:///C:/Users/clsor/.codex/plugins/cache/openai-curated-remote/data-analytics/0.2.8-13ceeea1f599/skills/build-report/scripts/verify_portable_artifact.mjs'

const [inputArgument, outputArgument] = process.argv.slice(2)
if (!inputArgument || !outputArgument) throw new Error('Usage: node build-verified-report.mjs <artifact.json> <report.html>')

const inputPath = resolve(inputArgument)
const outputPath = resolve(outputArgument)
const temporaryPath = `${outputPath}.tmp-${process.pid}-${randomUUID()}`
const artifact = JSON.parse(readFileSync(inputPath, 'utf8'))

function addWindowsOverflowGuard(html) {
  const before = '*{box-sizing:border-box}html,body{margin:0;min-height:100%;'
  const after = '*{box-sizing:border-box}html,body{margin:0;min-height:100%;max-width:100%;overflow-x:hidden;'
  if (!html.includes(before)) throw new Error('The portable report base stylesheet changed; overflow guard was not applied.')
  return html.replaceAll(before, after)
}

try {
  let html = addWindowsOverflowGuard(buildPortableArtifact(artifact))
  writeFileSync(temporaryPath, html, 'utf8')

  let staticCharts
  for (let attempt = 1; attempt <= 3; attempt += 1) {
    try {
      staticCharts = await extractPortableChartSvgs({ htmlPath: temporaryPath })
      break
    } catch (error) {
      if (attempt === 3 || error?.code !== 'reader_timeout') throw error
    }
  }
  html = addWindowsOverflowGuard(buildPortableArtifact(artifact, { staticCharts }))
  writeFileSync(temporaryPath, html, 'utf8')

  const verification = await verifyPortableArtifact({
    artifactPath: inputPath,
    htmlPath: temporaryPath,
    screenshotPath: `${outputPath}.verification-failure.png`,
  })
  renameSync(temporaryPath, outputPath)
  process.stdout.write(`${JSON.stringify({ ok: true, output: outputPath, verification })}\n`)
} finally {
  rmSync(temporaryPath, { force: true })
}
