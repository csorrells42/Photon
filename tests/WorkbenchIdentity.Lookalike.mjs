import http from 'node:http'

const portIndex = process.argv.indexOf('--port')
const port = Number(portIndex >= 0 ? process.argv[portIndex + 1] : 0)
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('A valid --port is required.')

const server = http.createServer((request, response) => {
  const url = new URL(request.url ?? '/', `http://127.0.0.1:${port}`)
  if (url.pathname !== '/workbench-api/host-identity') {
    response.writeHead(404).end()
    return
  }
  response.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' })
  response.end(JSON.stringify({
    protocolVersion: 1,
    challenge: url.searchParams.get('challenge') ?? '',
    proof: '00'.repeat(32),
  }))
})

server.listen(port, '127.0.0.1')

