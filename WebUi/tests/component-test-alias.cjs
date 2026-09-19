const Module = require('node:module')
const path = require('node:path')

const outputRoot = path.resolve(process.cwd(), '.bridge-test-dist')
const originalResolveFilename = Module._resolveFilename

Module._resolveFilename = function resolveWpeAlias(request, parent, isMain, options) {
  const mapped = typeof request === 'string' && request.startsWith('@/')
    ? path.join(outputRoot, request.slice(2))
    : request
  return originalResolveFilename.call(this, mapped, parent, isMain, options)
}
